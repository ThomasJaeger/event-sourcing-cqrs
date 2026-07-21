using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.SharedKernel;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Domain.Tests.Sales;

// S1 of the snapshot arc (Chapter 12): the capture and restore seam on the aggregate, pinned at the
// domain level before any store. These facts drive Order through real commands, capture a snapshot,
// and restore a pristine Order, asserting through the public surface and the restored aggregate's own
// subsequent behavior rather than through private state. The seam ships as throwing skeletons this
// turn (ISnapshotSource on Order, AggregateRoot.RestoreVersion), so the round-trip, the tail
// equivalence, and the guard fail on missing behavior; the schema-version fact pins the constant an
// S2 row will carry, and it is green on write because the constant is a one-line declaration.
public class OrderSnapshotTests
{
    private static readonly Guid OrderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CustomerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid LineId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid OtherLineId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTime At = new(2026, 5, 11, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Money TenUsd = new(10m, Currency.USD);
    private static readonly Address Shipping = new("1 Main St", "Smalltown", "12345", "US");

    // (a) Capture then restore round-trips the state and the version, and the restored aggregate
    // behaves like the original: a placed order accepts Ship, and so does the one restored from it.
    [Fact]
    public void A_snapshot_captures_and_restores_state_and_version()
    {
        var order = Order.Draft(OrderId, CustomerId, At, "web");
        order.AddLine(LineId, "SKU-1", 2, TenUsd, At);
        order.SetShippingAddress(Shipping, At);
        order.Place(At);

        var snapshot = order.ToSnapshot();

        var restored = new Order();
        restored.RestoreFrom(snapshot, order.Version);

        restored.Id.Should().Be(order.Id);
        restored.Status.Should().Be(order.Status);
        restored.Total.Should().Be(order.Total);
        restored.Lines.Should().BeEquivalentTo(order.Lines);
        restored.ShippingAddress.Should().Be(order.ShippingAddress);
        restored.Version.Should().Be(order.Version);
        restored.Invoking(o => o.Ship("UPS", "1Z999", At)).Should().NotThrow();
    }

    // (b) A snapshot taken mid-stream, restored and then replayed to the tail, equals a full replay of
    // the whole stream, through the public surface and the version.
    [Fact]
    public void A_restored_aggregate_replaying_the_tail_equals_a_full_replay()
    {
        var source = Order.Draft(OrderId, CustomerId, At, "web");
        source.AddLine(LineId, "SKU-1", 2, TenUsd, At);
        source.SetShippingAddress(Shipping, At);
        source.Place(At);
        var stream = source.DequeueUncommittedEvents();

        var atSnapshot = new Order();
        atSnapshot.ApplyHistoric(stream[0]);
        atSnapshot.ApplyHistoric(stream[1]);
        var snapshot = atSnapshot.ToSnapshot();

        var restored = new Order();
        restored.RestoreFrom(snapshot, 2);
        foreach (var e in stream.Skip(2))
        {
            restored.ApplyHistoric(e);
        }

        var full = new Order();
        foreach (var e in stream)
        {
            full.ApplyHistoric(e);
        }

        restored.Status.Should().Be(full.Status);
        restored.Total.Should().Be(full.Total);
        restored.Lines.Should().BeEquivalentTo(full.Lines);
        restored.ShippingAddress.Should().Be(full.ShippingAddress);
        restored.Version.Should().Be(full.Version);
    }

    // (c) Restoring onto a non-pristine aggregate is a programming error and throws loudly, so a
    // restore cannot land on a half-built aggregate and corrupt it.
    [Fact]
    public void Restoring_onto_a_non_pristine_aggregate_throws()
    {
        var nonPristine = Order.Draft(OrderId, CustomerId, At, "web");
        var snapshot = new OrderSnapshot(OrderId, CustomerId, OrderStatus.Placed, [], null);

        var act = () => nonPristine.RestoreFrom(snapshot, 5);

        act.Should().Throw<InvalidOperationException>();
    }

    // (d) The snapshot schema version constant exists and is 1, the field an S2 row carries to decide
    // whether a stored snapshot's shape still matches.
    [Fact]
    public void The_snapshot_schema_version_constant_is_one()
    {
        OrderSnapshot.SnapshotSchemaVersion.Should().Be(1);
    }

    // AUTHORIZED ADDITION (S1 GREEN): a green-on-write characterization pinning the defensive-copy
    // decision in both directions. Capture takes a copy, so a command on the source after ToSnapshot
    // leaves the memento unchanged; restore copies into each aggregate's own list, so two orders
    // restored from one snapshot, one of them mutated, leave the other and the snapshot unchanged. Its
    // teeth were proven by sharing the reference in a scratch shape and watching it fail, then
    // restoring the copy.
    [Fact]
    public void The_snapshot_and_each_restored_aggregate_hold_their_own_lines()
    {
        var source = Order.Draft(OrderId, CustomerId, At, "web");
        source.AddLine(LineId, "SKU-1", 2, TenUsd, At);
        var snapshot = source.ToSnapshot();

        // Capture isolation: a command on the source after capture does not reach the memento.
        source.AddLine(OtherLineId, "SKU-2", 1, TenUsd, At);
        snapshot.Lines.Should().HaveCount(1);

        // Restore isolation: two orders restored from one snapshot each hold their own list.
        var first = new Order();
        first.RestoreFrom(snapshot, 2);
        var second = new Order();
        second.RestoreFrom(snapshot, 2);
        first.AddLine(OtherLineId, "SKU-2", 1, TenUsd, At);
        second.Lines.Should().HaveCount(1);
        snapshot.Lines.Should().HaveCount(1);
    }
}
