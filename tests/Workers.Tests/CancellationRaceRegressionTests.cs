using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Hosts.Workers;
using EventSourcingCqrs.ProcessManagers.OrderFulfillment;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Workers.Tests;

public sealed class CancellationRaceRegressionTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_and_dispatch_respect_the_committed_outcome_even_before_event_delivery(bool dispatched)
    {
        var connection = await fixture.CreateMigratedDatabaseAsync();
        using var host = WorkersHostFactory.Build(EventStoreProvider.Postgres, connection, connection);
        // Leave workers stopped: no projection or PM has observed ShipmentDispatched.
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<ICommandContextAccessor>();
        var tenant = services.GetRequiredService<ICurrentTenantAccessor>();
        context.Current = CommandContext.System;
        tenant.Current = WellKnownTenants.Default;
        var id = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var address = new Address("1 Main", "City", "12345", "US");
        var order = Order.Draft(id, Guid.NewGuid(), now, "test");
        order.AddLine(lineId, "SKU", 1, new Money(10, Currency.USD), now);
        order.SetShippingAddress(address, now);
        order.Place(now);
        var orders = services.GetRequiredService<IEventStoreRepository<Order>>();
        await orders.SaveAsync(order, default);
        var shipment = Shipment.Schedule(shipmentId, id, address, [new ShipmentLine(id, lineId, "SKU", 1)], now);
        if (dispatched) shipment.Dispatch("carrier", now);
        var shipments = services.GetRequiredService<IEventStoreRepository<Shipment>>();
        await shipments.SaveAsync(shipment, default);
        var pm = new OrderFulfillmentProcessManager(StreamId.ForProcessManager(
            StreamPrefixes.OrderFulfillmentPm, WellKnownTenants.Default, id));
        pm.Start(id, order.Total, Guid.NewGuid());
        pm.RecordPaymentAuthorized();
        pm.CompleteReservations();
        pm.RequestShipmentScheduling(shipmentId);
        await services.GetRequiredService<IProcessManagerRepository<OrderFulfillmentProcessManager>>().SaveAsync(pm, default);
        context.Current = null;
        var bus = services.GetRequiredService<ICommandBus>();
        Func<Task> cancel = () => bus.SendAsync(new CancelOrder(id, "customer request", Guid.NewGuid()),
            Guid.NewGuid(), [Role.Admin], WellKnownTenants.Default, Guid.NewGuid().ToString(), default);
        if (dispatched)
        {
            await cancel.Should().ThrowAsync<DomainException>();
            (await orders.LoadAsync(id, default))!.Status.Should().Be(OrderStatus.Placed);
        }
        else
        {
            await cancel();
            Func<Task> dispatch = () => bus.SendAsync(new DispatchShipment(shipmentId, "carrier"),
                Guid.NewGuid(), [Role.Admin], WellKnownTenants.Default, Guid.NewGuid().ToString(), default);
            await dispatch.Should().ThrowAsync<DomainException>();
            (await shipments.LoadAsync(shipmentId, default))!.Status.Should().Be(ShipmentStatus.Scheduled);
        }
    }
}
