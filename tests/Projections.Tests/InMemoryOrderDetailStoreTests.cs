using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// Behaviour of the in-memory OrderDetail double, the seam the projection (commits
// 16-17) and the rebuild test (commit 18) run against. The line-delete-then-re-add
// case is the worked case for the plain-insert-plus-delete call (the Order aggregate
// frees a LineId on removal); the two lookup cases exercise the ADR 0020 resolve
// path the shipment-update and payment-follow-on handlers run.
public class InMemoryOrderDetailStoreTests
{
    private const string ProjectionName = "order-detail";
    private static readonly DateTime At = new(2026, 5, 20, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = At.AddHours(1);

    [Fact]
    public async Task CreateHeader_then_GetHeader_round_trips_as_draft_with_no_totals_or_address()
    {
        var store = new InMemoryOrderDetailStore();
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateHeaderAsync(orderId, customerId, At, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        var row = (await store.GetHeaderAsync(orderId, CancellationToken.None))!;
        row.OrderId.Should().Be(orderId);
        row.CustomerId.Should().Be(customerId);
        row.Status.Should().Be(OrderStatus.Draft);
        row.PlacedUtc.Should().BeNull();
        row.Total.Should().BeNull();
        row.ShippingAddress.Should().BeNull();
    }

    [Fact]
    public async Task ApplyPlaced_sets_status_total_and_placed_utc()
    {
        var store = new InMemoryOrderDetailStore();
        var orderId = Guid.NewGuid();
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateHeaderAsync(orderId, Guid.NewGuid(), At, CancellationToken.None);
            await uow.ApplyPlacedAsync(
                orderId, new Money(125m, Currency.USD), At, At, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        var row = (await store.GetHeaderAsync(orderId, CancellationToken.None))!;
        row.Status.Should().Be(OrderStatus.Placed);
        row.PlacedUtc.Should().Be(At);
        row.Total.Should().Be(new Money(125m, Currency.USD));
    }

    [Fact]
    public async Task Lifecycle_transitions_advance_status_and_stamp_each_utc()
    {
        var store = new InMemoryOrderDetailStore();
        var orderId = Guid.NewGuid();
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateHeaderAsync(orderId, Guid.NewGuid(), At, CancellationToken.None);
            await uow.ApplyPlacedAsync(orderId, new Money(10m, Currency.USD), At, At, CancellationToken.None);
            await uow.ApplyShippedAsync(orderId, Later, Later, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        var row = (await store.GetHeaderAsync(orderId, CancellationToken.None))!;
        row.Status.Should().Be(OrderStatus.Shipped);
        row.PlacedUtc.Should().Be(At);
        row.ShippedUtc.Should().Be(Later);
        row.LastUpdatedUtc.Should().Be(Later);
    }

    [Fact]
    public async Task SetShippingAddress_writes_all_four_fields()
    {
        var store = new InMemoryOrderDetailStore();
        var orderId = Guid.NewGuid();
        var address = new Address("12 Main St", "Portland", "97201", "US");
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateHeaderAsync(orderId, Guid.NewGuid(), At, CancellationToken.None);
            await uow.SetShippingAddressAsync(orderId, address, At, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        (await store.GetHeaderAsync(orderId, CancellationToken.None))!.ShippingAddress
            .Should().Be(address);
    }

    [Fact]
    public async Task MarkReturned_sets_returned_utc_and_leaves_status()
    {
        var store = new InMemoryOrderDetailStore();
        var orderId = Guid.NewGuid();
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateHeaderAsync(orderId, Guid.NewGuid(), At, CancellationToken.None);
            await uow.ApplyShippedAsync(orderId, At, At, CancellationToken.None);
            await uow.MarkReturnedAsync(orderId, Later, Later, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        var row = (await store.GetHeaderAsync(orderId, CancellationToken.None))!;
        row.ReturnedUtc.Should().Be(Later);
        row.Status.Should().Be(OrderStatus.Shipped);
    }

    [Fact]
    public async Task InsertLine_then_GetLines_returns_every_line()
    {
        var store = new InMemoryOrderDetailStore();
        var orderId = Guid.NewGuid();
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.InsertLineAsync(
                new OrderDetailLineRow(orderId, Guid.NewGuid(), "SKU-1", 2, new Money(5m, Currency.USD)),
                CancellationToken.None);
            await uow.InsertLineAsync(
                new OrderDetailLineRow(orderId, Guid.NewGuid(), "SKU-2", 1, new Money(8m, Currency.USD)),
                CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        var lines = await store.GetLinesAsync(orderId, CancellationToken.None);
        lines.Should().HaveCount(2);
        lines.Select(l => l.Sku).Should().BeEquivalentTo(["SKU-1", "SKU-2"]);
    }

    [Fact]
    public async Task DeleteLine_then_re_add_same_line_id_lands_the_new_values()
    {
        // The Order aggregate forbids adding a currently-live LineId but RemoveLine
        // frees it, so add-remove-add of the same LineId is a valid stream. Plain
        // insert plus delete handles it: the delete frees the key, the re-add lands
        // the new sku and quantity.
        var store = new InMemoryOrderDetailStore();
        var orderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.InsertLineAsync(
                new OrderDetailLineRow(orderId, lineId, "SKU-1", 1, new Money(5m, Currency.USD)),
                CancellationToken.None);
            await uow.DeleteLineAsync(orderId, lineId, CancellationToken.None);
            await uow.InsertLineAsync(
                new OrderDetailLineRow(orderId, lineId, "SKU-2", 9, new Money(7m, Currency.USD)),
                CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        var lines = await store.GetLinesAsync(orderId, CancellationToken.None);
        lines.Should().ContainSingle();
        lines[0].Sku.Should().Be("SKU-2");
        lines[0].Quantity.Should().Be(9);
    }

    [Fact]
    public async Task AppendTimeline_returns_entries_ordered_by_global_position()
    {
        var store = new InMemoryOrderDetailStore();
        var orderId = Guid.NewGuid();
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            // Appended out of order; the read returns them by global position.
            await uow.AppendTimelineAsync(
                new OrderDetailTimelineRow(orderId, 30, "OrderPlaced", Later, "{}"), CancellationToken.None);
            await uow.AppendTimelineAsync(
                new OrderDetailTimelineRow(orderId, 10, "OrderDrafted", At, "{}"), CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 30, CancellationToken.None);
        }

        var timeline = await store.GetTimelineAsync(orderId, CancellationToken.None);
        timeline.Select(t => t.GlobalPosition).Should().ContainInOrder(10L, 30L);
        timeline.Select(t => t.EventType).Should().ContainInOrder("OrderDrafted", "OrderPlaced");
    }

    [Fact]
    public async Task Shipment_mapping_round_trips_and_returns_null_when_absent()
    {
        var store = new InMemoryOrderDetailStore();
        var shipmentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.InsertShipmentMappingAsync(
                new OrderDetailShipmentRow(shipmentId, orderId, At), CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        await using var read = await store.BeginAsync(CancellationToken.None);
        (await read.GetOrderIdByShipmentIdAsync(shipmentId, CancellationToken.None)).Should().Be(orderId);
        (await read.GetOrderIdByShipmentIdAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Payment_mapping_round_trips_and_returns_null_when_absent()
    {
        var store = new InMemoryOrderDetailStore();
        var paymentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.InsertPaymentMappingAsync(
                new OrderDetailPaymentRow(paymentId, orderId, At), CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        await using var read = await store.BeginAsync(CancellationToken.None);
        (await read.GetOrderIdByPaymentIdAsync(paymentId, CancellationToken.None)).Should().Be(orderId);
        (await read.GetOrderIdByPaymentIdAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Truncate_clears_every_table()
    {
        var store = new InMemoryOrderDetailStore();
        var orderId = Guid.NewGuid();
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateHeaderAsync(orderId, Guid.NewGuid(), At, CancellationToken.None);
            await uow.InsertLineAsync(
                new OrderDetailLineRow(orderId, Guid.NewGuid(), "SKU-1", 1, new Money(5m, Currency.USD)),
                CancellationToken.None);
            await uow.AppendTimelineAsync(
                new OrderDetailTimelineRow(orderId, 10, "OrderDrafted", At, "{}"), CancellationToken.None);
            await uow.InsertShipmentMappingAsync(
                new OrderDetailShipmentRow(Guid.NewGuid(), orderId, At), CancellationToken.None);
            await uow.InsertPaymentMappingAsync(
                new OrderDetailPaymentRow(Guid.NewGuid(), orderId, At), CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        await store.TruncateAsync(CancellationToken.None);

        (await store.GetHeaderAsync(orderId, CancellationToken.None)).Should().BeNull();
        (await store.GetLinesAsync(orderId, CancellationToken.None)).Should().BeEmpty();
        (await store.GetTimelineAsync(orderId, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Commit_advances_checkpoint_with_greatest()
    {
        var store = new InMemoryOrderDetailStore();
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CommitAsync(ProjectionName, 5, CancellationToken.None);
        }
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            // A lower position does not move the checkpoint backwards.
            await uow.CommitAsync(ProjectionName, 3, CancellationToken.None);
        }

        store.Checkpoints[ProjectionName].Should().Be(5);
    }
}
