using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// Behaviour of the in-memory InventoryDashboard double, the seam the projection
// (commit 12) and the rebuild test run against. The release case exercises the
// recover-reverse-delete sequence the projection handler runs end to end.
public class InMemoryInventoryDashboardStoreTests
{
    private const string ProjectionName = "inventory-dashboard";
    private static readonly DateTime At = new(2026, 5, 14, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task CreateDashboard_then_GetBySku_round_trips_with_zeroed_quantities()
    {
        var store = new InMemoryInventoryDashboardStore();
        var inventoryId = Guid.NewGuid();
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateDashboardAsync(inventoryId, "SKU-1", At, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        var row = await store.GetBySkuAsync("SKU-1", CancellationToken.None);
        row!.InventoryId.Should().Be(inventoryId);
        row.Sku.Should().Be("SKU-1");
        row.OnHandQuantity.Should().Be(0);
        row.ReservedQuantity.Should().Be(0);
    }

    [Fact]
    public async Task AdjustOnHand_accumulates_in_both_directions()
    {
        var store = new InMemoryInventoryDashboardStore();
        var inventoryId = Guid.NewGuid();
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateDashboardAsync(inventoryId, "SKU-1", At, CancellationToken.None);
            await uow.AdjustOnHandAsync(inventoryId, 100, At, CancellationToken.None);
            await uow.AdjustOnHandAsync(inventoryId, -30, At, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        (await store.GetBySkuAsync("SKU-1", CancellationToken.None))!.OnHandQuantity.Should().Be(70);
    }

    [Fact]
    public async Task Reserve_adds_to_reserved_and_records_the_lookup_row()
    {
        var store = new InMemoryInventoryDashboardStore();
        var inventoryId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateDashboardAsync(inventoryId, "SKU-1", At, CancellationToken.None);
            await uow.AdjustReservedAsync(inventoryId, 5, At, CancellationToken.None);
            await uow.InsertReservationAsync(
                new InventoryReservationRow(inventoryId, orderId, lineId, 5, At), CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        (await store.GetBySkuAsync("SKU-1", CancellationToken.None))!.ReservedQuantity.Should().Be(5);
        await using var read = await store.BeginAsync(CancellationToken.None);
        (await read.GetReservationAsync(inventoryId, orderId, lineId, CancellationToken.None))
            .Should().Be(new InventoryReservationRow(inventoryId, orderId, lineId, 5, At));
    }

    [Fact]
    public async Task Release_reverses_reserved_via_the_recovered_quantity_and_deletes_the_lookup()
    {
        var store = new InMemoryInventoryDashboardStore();
        var inventoryId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateDashboardAsync(inventoryId, "SKU-1", At, CancellationToken.None);
            await uow.AdjustReservedAsync(inventoryId, 5, At, CancellationToken.None);
            await uow.InsertReservationAsync(
                new InventoryReservationRow(inventoryId, orderId, lineId, 5, At), CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        // The release path the projection runs: recover the quantity from the
        // lookup, reverse reserved by it, delete the lookup row.
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            var reservation = await uow.GetReservationAsync(
                inventoryId, orderId, lineId, CancellationToken.None);
            await uow.AdjustReservedAsync(inventoryId, -reservation!.Quantity, At, CancellationToken.None);
            await uow.DeleteReservationAsync(inventoryId, orderId, lineId, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 2, CancellationToken.None);
        }

        (await store.GetBySkuAsync("SKU-1", CancellationToken.None))!.ReservedQuantity.Should().Be(0);
        await using var read = await store.BeginAsync(CancellationToken.None);
        (await read.GetReservationAsync(inventoryId, orderId, lineId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task GetReservation_returns_null_when_absent()
    {
        var store = new InMemoryInventoryDashboardStore();
        await using var uow = await store.BeginAsync(CancellationToken.None);
        (await uow.GetReservationAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task GetAllAsync_returns_every_dashboard_row()
    {
        var store = new InMemoryInventoryDashboardStore();
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateDashboardAsync(Guid.NewGuid(), "SKU-1", At, CancellationToken.None);
            await uow.CreateDashboardAsync(Guid.NewGuid(), "SKU-2", At, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        var all = await store.GetAllAsync(CancellationToken.None);
        all.Should().HaveCount(2);
        all.Select(r => r.Sku).Should().BeEquivalentTo(["SKU-1", "SKU-2"]);
    }

    [Fact]
    public async Task Truncate_clears_the_dashboard_and_the_reservation_lookup()
    {
        var store = new InMemoryInventoryDashboardStore();
        var inventoryId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateDashboardAsync(inventoryId, "SKU-1", At, CancellationToken.None);
            await uow.InsertReservationAsync(
                new InventoryReservationRow(inventoryId, orderId, lineId, 5, At), CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        await store.TruncateAsync(CancellationToken.None);

        (await store.GetBySkuAsync("SKU-1", CancellationToken.None)).Should().BeNull();
        await using var read = await store.BeginAsync(CancellationToken.None);
        (await read.GetReservationAsync(inventoryId, orderId, lineId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task PublishOnCommit_a_second_time_in_one_unit_of_work_throws()
    {
        var store = new InMemoryInventoryDashboardStore();
        await using var uow = await store.BeginAsync(CancellationToken.None);
        uow.PublishOnCommit(new NotificationEnvelope("InventoryDashboard", "SKU-1", "InventoryCreated", [], WellKnownTenants.Default));

        // A unit of work stages at most one notification: one handler, one event,
        // one logical change per commit.
        var act = () => uow.PublishOnCommit(
            new NotificationEnvelope("InventoryDashboard", "SKU-1", "InventoryAdjusted", [], WellKnownTenants.Default));

        act.Should().Throw<InvalidOperationException>();
    }
}
