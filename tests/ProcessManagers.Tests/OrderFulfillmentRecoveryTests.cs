using EventSourcingCqrs.Application.Commands.Billing;
using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing;
using EventSourcingCqrs.Domain.Billing.Events;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.ProcessManagers.OrderFulfillment;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.ProcessManagers.Tests;

public sealed class OrderFulfillmentRecoveryTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Retrying_failed_compensation_never_creates_an_untracked_reservation()
    {
        var (h, order, auth) = await Start(1);
        var inventory = Inventory.Create(Guid.NewGuid(), "sku-0", Now);
        var failCancel = true;
        h.Bus.OutcomeFor = command =>
        {
            if (command is ReserveInventory reserve)
            {
                try { inventory.Reserve(reserve.OrderId, reserve.LineId, reserve.Quantity, Now); }
                catch (DomainException ex) { return CommandOutcome.Failed(ex); }
            }
            if (command is ReleaseInventory release) inventory.Release(release.LineId, release.Reason, Now);
            if (command is CancelOrder && failCancel) throw new InvalidOperationException("Unavailable command transport");
            return CommandOutcome.Success();
        };
        Func<Task> receive = () => h.Receive(auth);
        await receive.Should().ThrowAsync<InvalidOperationException>();
        (await h.LoadPm(order.Id))!.Reservations.Values.Should().OnlyContain(r => r.Status == ReservationLineStatus.Failed);
        inventory.Adjust(5, "Restock", Now);
        failCancel = false;
        await receive();
        (await h.LoadPm(order.Id))!.State.Should().Be(OrderFulfillmentState.Cancelled);
        inventory.Reserved.Should().Be(0, "a cancelled order must not leave a new reservation behind");
        h.Dispatched.Count(d => d.Command is ReserveInventory).Should().Be(1);
    }

    [Theory]
    [InlineData(34)]
    [InlineData(70)]
    public async Task Large_order_reservation_outcomes_fit_bounded_appends(int lineCount)
    {
        var (h, order, auth) = await Start(lineCount);
        h.Persistence.MaximumEvents = 33;
        await h.Receive(auth);
        var pm = (await h.LoadPm(order.Id))!;
        pm.State.Should().Be(OrderFulfillmentState.AwaitingDispatch);
        pm.Reservations.Should().HaveCount(lineCount);
        h.Persistence.SavedBatchSizes.Should().OnlyContain(size => size <= 33);
    }

    [Theory]
    [InlineData(34)]
    [InlineData(70)]
    public async Task Large_order_timeout_compensation_fits_bounded_appends(int lineCount)
    {
        var (h, order, auth) = await Start(lineCount);
        await h.Receive(auth);
        h.Persistence.MaximumEvents = 33;
        await h.DispatchTimeoutAwaitingDispatch(order.Id);
        (await h.LoadPm(order.Id))!.State.Should().Be(OrderFulfillmentState.Cancelled);
        h.Dispatched.Count(d => d.Command is ReleaseInventory).Should().Be(lineCount);
    }

    [Theory]
    [InlineData(34)]
    [InlineData(70)]
    public async Task Customer_cancellation_recovers_many_unsaved_reservations_with_bounded_appends(int lineCount)
    {
        var (h, order, auth) = await Start(lineCount);
        foreach (var line in order.Lines)
        {
            var inventory = Inventory.Create(Guid.NewGuid(), line.Sku, Now);
            inventory.Adjust(1, "Stock", Now);
            inventory.Reserve(order.Id, line.LineId, 1, Now);
            h.MapSku(line.Sku, inventory.Id);
            await h.SeedInventory(inventory);
        }
        h.Bus.OutcomeFor = command => command is ReserveInventory
            ? throw new InvalidOperationException("Crash after reservation commits") : CommandOutcome.Success();
        Func<Task> receive = () => h.Receive(auth);
        await receive.Should().ThrowAsync<InvalidOperationException>();
        h.Bus.OutcomeFor = _ => CommandOutcome.Success();
        h.Persistence.MaximumEvents = 33;
        await h.Receive(new OrderCancelled(order.Id, "Customer cancelled", Guid.NewGuid(), Now));
        (await h.LoadPm(order.Id))!.State.Should().Be(OrderFulfillmentState.Cancelled);
        h.Dispatched.Count(d => d.Command is ReleaseInventory).Should().Be(lineCount);
    }

    [Fact]
    public async Task Compensation_resumes_after_a_release_batch_is_committed()
    {
        var (h, order, auth) = await Start(70);
        await h.Receive(auth);
        h.Persistence.MaximumEvents = 33;
        var releases = 0;
        h.Bus.OutcomeFor = command => command is ReleaseInventory && ++releases == 40
            ? CommandOutcome.Failed(new DomainException("Release unavailable")) : CommandOutcome.Success();
        Func<Task> timeout = () => h.DispatchTimeoutAwaitingDispatch(order.Id);
        await timeout.Should().ThrowAsync<DomainException>();
        var interrupted = (await h.LoadPm(order.Id))!;
        interrupted.CancellationReason.Should().NotBeNull();
        interrupted.Reservations.Values.Should().Contain(r => r.Status == ReservationLineStatus.Released);
        h.Bus.OutcomeFor = _ => CommandOutcome.Success();
        await timeout();
        var completed = (await h.LoadPm(order.Id))!;
        completed.State.Should().Be(OrderFulfillmentState.Cancelled);
        completed.Reservations.Values.Should().OnlyContain(r => r.Status == ReservationLineStatus.Released);
    }

    [Fact]
    public async Task Reservation_fanout_resumes_after_an_outcome_batch_is_committed()
    {
        var (h, order, auth) = await Start(70);
        h.Persistence.MaximumEvents = 33;
        var reservationSaves = 0;
        h.Persistence.BeforeSave = pm =>
        {
            if (pm.GetUncommittedEvents().Any(e => e is OrderFulfillment.Events.ReservationSucceeded)
                && ++reservationSaves == 2)
                throw new InvalidOperationException("Unavailable append");
        };
        Func<Task> receive = () => h.Receive(auth);
        await receive.Should().ThrowAsync<InvalidOperationException>();
        (await h.LoadPm(order.Id))!.Reservations.Count.Should().BeGreaterThan(0);
        h.Persistence.BeforeSave = null;
        await receive();
        var completed = (await h.LoadPm(order.Id))!;
        completed.State.Should().Be(OrderFulfillmentState.AwaitingDispatch);
        completed.Reservations.Should().HaveCount(70);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shipment_notifications_cannot_advance_a_cancelling_workflow(bool delivered)
    {
        var (h, order, auth) = await Start(1);
        await h.Receive(auth);
        var pm = (await h.LoadPm(order.Id))!;
        await h.SeedShipment(Shipment.Schedule(pm.ShipmentId, order.Id,
            new Address("1 Main", "City", "12345", "US"),
            order.Lines.Select(line => new ShipmentLine(order.Id, line.LineId, line.Sku, line.Quantity)).ToArray(), Now));
        if (delivered) await h.Receive(new ShipmentDispatched(pm.ShipmentId, "carrier", Now));
        h.Bus.OutcomeFor = command => command is CancelOrder
            ? throw new InvalidOperationException("Unavailable cancellation") : CommandOutcome.Success();
        Func<Task> cancel = () => h.Receive(new OrderCancelled(order.Id, "Customer cancelled", Guid.NewGuid(), Now));
        await cancel.Should().ThrowAsync<InvalidOperationException>();
        var interrupted = (await h.LoadPm(order.Id))!;
        h.Bus.OutcomeFor = _ => CommandOutcome.Success();
        if (delivered) await h.Receive(new ShipmentDelivered(pm.ShipmentId, Now));
        else await h.Receive(new ShipmentDispatched(pm.ShipmentId, "carrier", Now));
        (await h.LoadPm(order.Id))!.State.Should().Be(interrupted.State);
        h.Dispatched.Should().NotContain(dispatch => dispatch.Command is MarkOrderCompleted);
    }

    private static async Task<(OrderFulfillmentTestHarness Harness, Order Order, PaymentAuthorized Auth)> Start(int lineCount)
    {
        var h = new OrderFulfillmentTestHarness();
        var order = Order.Draft(Guid.NewGuid(), Guid.NewGuid(), Now, "web");
        for (var i = 0; i < lineCount; i++)
        {
            var sku = $"sku-{i}";
            order.AddLine(Guid.NewGuid(), sku, 1, new Money(10m, Currency.USD), Now);
            h.MapSku(sku, Guid.NewGuid());
        }
        order.SetShippingAddress(new Address("1 Main", "City", "12345", "US"), Now);
        order.Place(Now);
        await h.SeedOrder(order);
        await h.Receive(new OrderPlaced(order.Id, order.CustomerId, order.Total, Now));
        var pm = (await h.LoadPm(order.Id))!;
        await h.SeedPayment(Payment.Authorize(pm.PaymentId, order.Id, order.Total, "card", Now));
        return (h, order, new PaymentAuthorized(pm.PaymentId, order.Id, order.Total, "card", Now));
    }
}
