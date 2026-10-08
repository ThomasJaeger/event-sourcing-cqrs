using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Queries.Sales;

public sealed class OrderHistoryReaderTests
{
    private static readonly Guid OrderId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid CustomerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly TenantId Tenant = WellKnownTenants.Default;
    private static readonly StreamId Stream = StreamId.ForAggregate<Order>(Tenant, OrderId);
    private static readonly DateTime At = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Every_step_reconstructs_its_own_prefix_without_changing_prior_snapshots()
    {
        var oldAddress = new Address("1 Old Road", "Seattle", "98101", "US");
        var newAddress = new Address("2 New Road", "Seattle", "98102", "US");
        var line = Guid.NewGuid();
        var order = Order.Draft(OrderId, CustomerId, At, "web");
        order.AddLine(line, "NOTEBOOK", 2, new Money(12.5m, Currency.USD), At);
        order.SetShippingAddress(oldAddress, At);
        order.RemoveLine(line, At);
        order.AddLine(line, "NOTEBOOK", 1, new Money(12.5m, Currency.USD), At);
        order.SetShippingAddress(newAddress, At);
        order.Place(At);
        order.Cancel("Customer changed plans", CustomerId, At);
        var events = Wrap(order.DequeueUncommittedEvents());
        // Recorded timestamps can arrive out of order. Stream versions define reconstruction.
        events[1] = events[1] with { OccurredUtc = At.AddHours(-1) };
        var source = new StubReader(events);

        var view = await new OrderHistoryReader(source).ReadAsync(Tenant, OrderId, CancellationToken.None);

        view.Should().NotBeNull();
        view!.Steps.Select(x => x.Version).Should().Equal(Enumerable.Range(1, 8));
        view.Steps[0].Snapshot.Lines.Should().BeEmpty();
        view.Steps[1].Total.Should().Be(new Money(25m, Currency.USD));
        view.Steps[1].OccurredUtc.Should().Be(At.AddHours(-1));
        view.Steps[2].Snapshot.ShippingAddress.Should().Be(oldAddress);
        view.Steps[3].Snapshot.Lines.Should().BeEmpty();
        view.Steps[4].Snapshot.Lines.Should().ContainSingle().Which.Quantity.Should().Be(1);
        view.Steps[5].Snapshot.ShippingAddress.Should().Be(newAddress);
        view.Steps[6].Snapshot.Status.Should().Be(OrderStatus.Placed);
        view.Steps[7].Snapshot.Status.Should().Be(OrderStatus.Cancelled);
        view.Steps[7].Total.Should().Be(new Money(12.5m, Currency.USD));
        view.Steps.Should().OnlyContain(x => x.Snapshot.OrderId == OrderId && x.Snapshot.CustomerId == CustomerId);
        view.Steps[1].Snapshot.Lines[0].Quantity.Should().Be(2);
        view.Steps.Select(x => x.Description).Should().Equal(
            "Order draft created", "Added 2 × NOTEBOOK", "Shipping address set to 1 Old Road, Seattle", "Removed 2 × NOTEBOOK",
            "Added 1 × NOTEBOOK", "Shipping address set to 2 New Road, Seattle", "Order placed", "Order cancelled: Customer changed plans");
        source.Requests.Should().Equal((Stream, OrderHistoryReader.MaxEvents));
    }

    [Fact]
    public async Task Duplicate_active_line_ids_are_not_presented_as_a_comparable_history()
    {
        var lineId = Guid.NewGuid();
        var events = Wrap([
            new OrderDrafted(OrderId, CustomerId, At, "web"),
            new OrderLineAdded(OrderId, lineId, "NOTEBOOK", 2, new Money(12.5m, Currency.USD), At),
            new OrderLineAdded(OrderId, lineId, "PEN", 1, new Money(3m, Currency.USD), At),
        ]);

        await FluentActions.Awaiting(() => new OrderHistoryReader(new StubReader(events))
                .ReadAsync(Tenant, OrderId, CancellationToken.None))
            .Should().ThrowAsync<OrderHistoryIncompleteException>();
    }

    [Fact]
    public async Task A_removed_line_id_can_be_reused_with_different_details()
    {
        var lineId = Guid.NewGuid();
        var events = Wrap([
            new OrderDrafted(OrderId, CustomerId, At, "web"),
            new OrderLineAdded(OrderId, lineId, "NOTEBOOK", 2, new Money(12.5m, Currency.USD), At),
            new OrderLineRemoved(OrderId, lineId, At),
            new OrderLineAdded(OrderId, lineId, "PEN", 3, new Money(2m, Currency.USD), At),
        ]);

        var view = await new OrderHistoryReader(new StubReader(events))
            .ReadAsync(Tenant, OrderId, CancellationToken.None);

        view!.Steps.Should().HaveCount(4);
        view.Steps[1].Snapshot.Lines.Should().ContainSingle().Which.Sku.Should().Be("NOTEBOOK");
        view.Steps[2].Snapshot.Lines.Should().BeEmpty();
        var restored = view.Steps[3].Snapshot.Lines.Should().ContainSingle().Subject;
        restored.LineId.Should().Be(lineId);
        restored.Sku.Should().Be("PEN");
        restored.Quantity.Should().Be(3);
        restored.UnitPrice.Should().Be(new Money(2m, Currency.USD));
        view.Steps[3].Total.Should().Be(new Money(6m, Currency.USD));
    }

    [Fact]
    public async Task Shipped_and_completed_states_are_reconstructed_from_sales_events()
    {
        var order = Order.Draft(OrderId, CustomerId, At, "web");
        order.AddLine(Guid.NewGuid(), "BOOK", 1, new Money(10m, Currency.USD), At);
        order.SetShippingAddress(new Address("1 Main St", "Seattle", "98101", "US"), At);
        order.Place(At);
        order.Ship("Carrier", "TRACK-1", At);
        order.Complete(At);

        var view = await new OrderHistoryReader(new StubReader(Wrap(order.DequeueUncommittedEvents())))
            .ReadAsync(Tenant, OrderId, CancellationToken.None);

        view!.Steps[^2].Snapshot.Status.Should().Be(OrderStatus.Shipped);
        view.Steps[^1].Snapshot.Status.Should().Be(OrderStatus.Completed);
        view.Steps[^2].Description.Should().Be("Order shipped with Carrier; tracking TRACK-1");
        view.Steps[^1].Description.Should().Be("Order completed");
    }

    [Fact]
    public async Task An_empty_stream_has_no_history()
    {
        var view = await new OrderHistoryReader(new StubReader([]))
            .ReadAsync(Tenant, OrderId, CancellationToken.None);
        view.Should().BeNull();
    }

    [Fact]
    public async Task A_history_at_the_cap_is_complete_but_one_more_event_is_refused()
    {
        var order = Order.Draft(OrderId, CustomerId, At, "web");
        for (var i = 1; i < OrderHistoryReader.MaxEvents; i++)
            order.SetShippingAddress(new Address($"{i} Main St", "Seattle", "98101", "US"), At);
        var events = Wrap(order.DequeueUncommittedEvents());
        var reader = new OrderHistoryReader(new StubReader(events));
        (await reader.ReadAsync(Tenant, OrderId, CancellationToken.None))!.Steps.Should().HaveCount(500);

        var oversized = new OrderHistoryReader(new StubReader(events, hasMore: true));
        await FluentActions.Awaiting(() => oversized.ReadAsync(Tenant, OrderId, CancellationToken.None))
            .Should().ThrowAsync<OrderHistoryTooLongException>();
    }

    [Fact]
    public async Task A_foreign_owner_is_hidden_before_reading_the_remainder_or_exposing_the_cap()
    {
        var source = new StubReader(Wrap([new OrderDrafted(OrderId, CustomerId, At, "web")]), hasMore: true);
        var view = await new OrderHistoryReader(source).ReadAsync(
            Tenant, OrderId, Guid.NewGuid(), CancellationToken.None);
        view.Should().BeNull();
        source.Requests.Should().Equal((Stream, 1));
    }

    [Fact]
    public async Task An_owner_without_a_readable_ownership_event_gets_no_history()
    {
        var source = new StubReader(Wrap([new UnknownEvent()]));
        var view = await new OrderHistoryReader(source).ReadAsync(Tenant, OrderId, CustomerId, CancellationToken.None);
        view.Should().BeNull();
        source.Requests.Should().Equal((Stream, 1));
    }

    [Fact]
    public async Task An_owner_gets_no_history_when_the_first_stored_payload_cannot_be_interpreted()
    {
        var view = await new OrderHistoryReader(new UnreadableReader())
            .ReadAsync(Tenant, OrderId, CustomerId, CancellationToken.None);
        view.Should().BeNull();
    }

    [Fact]
    public async Task An_owner_reads_one_captured_history_after_the_authoritative_ownership_check()
    {
        var source = new StubReader(Wrap([new OrderDrafted(OrderId, CustomerId, At, "web")]));
        var view = await new OrderHistoryReader(source).ReadAsync(
            Tenant, OrderId, CustomerId, CancellationToken.None);
        view!.Steps.Should().ContainSingle();
        source.Requests.Should().Equal((Stream, 1), (Stream, OrderHistoryReader.MaxEvents));
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("duplicate")]
    [InlineData("wrong-stream")]
    [InlineData("wrong-tenant")]
    [InlineData("wrong-order")]
    [InlineData("second-draft")]
    [InlineData("unsupported")]
    [InlineData("missing-draft")]
    public async Task Incomplete_or_inconsistent_history_is_not_presented_as_an_order(string fault)
    {
        var draft = new OrderDrafted(OrderId, CustomerId, At, "web");
        var address = new ShippingAddressSet(OrderId, new Address("1 Main", "Seattle", "98101", "US"), At);
        var events = Wrap([draft, address]);
        events = fault switch
        {
            "gap" => [events[0], events[1] with { StreamVersion = 3 }],
            "duplicate" => [events[0], events[1] with { StreamVersion = 1 }],
            "wrong-stream" => [events[0], events[1] with { StreamId = StreamId.Parse($"order:{Guid.NewGuid():N}") }],
            "wrong-tenant" => [events[0], events[1] with { Metadata = events[1].Metadata with { Tenant = TenantId.From(Guid.NewGuid()) } }],
            "wrong-order" => Wrap([draft, address with { OrderId = Guid.NewGuid() }]),
            "second-draft" => Wrap([draft, draft]),
            "unsupported" => Wrap([draft, new UnknownEvent()]),
            "missing-draft" => Wrap([address]),
            _ => throw new InvalidOperationException(),
        };
        await FluentActions.Awaiting(() => new OrderHistoryReader(new StubReader(events))
                .ReadAsync(Tenant, OrderId, CancellationToken.None))
            .Should().ThrowAsync<OrderHistoryIncompleteException>();
    }

    [Fact]
    public async Task The_requested_tenant_controls_the_stream_identity()
    {
        var otherTenant = TenantId.From(Guid.NewGuid());
        var source = new StubReader([]);
        await new OrderHistoryReader(source).ReadAsync(otherTenant, OrderId, CancellationToken.None);
        source.Requests.Should().Equal((StreamId.ForAggregate<Order>(otherTenant, OrderId), OrderHistoryReader.MaxEvents));
    }

    [Fact]
    public async Task Cancelled_reads_do_not_reconstruct_history()
    {
        var source = new StubReader(Wrap([new OrderDrafted(OrderId, CustomerId, At, "web")]));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await FluentActions.Awaiting(() => new OrderHistoryReader(source).ReadAsync(Tenant, OrderId, cancelled.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        source.Requests.Should().BeEmpty();
    }

    private static EventEnvelope[] Wrap(IReadOnlyList<IDomainEvent> events)
        => events.Select((payload, index) =>
        {
            var id = Guid.NewGuid();
            return new EventEnvelope(Stream, index + 1, id, payload.GetType().Name, 1, payload,
                new EventMetadata(id, Guid.NewGuid(), Guid.NewGuid(), CustomerId, "test", At, Tenant), At, index + 1);
        }).ToArray();

    private sealed record UnknownEvent : IDomainEvent;

    private sealed class UnreadableReader : IBoundedEventStreamReader
    {
        public Task<EventStreamWindow> ReadAsync(StreamId streamId, int maxEvents, CancellationToken ct)
            => throw new EventStreamReadException(new InvalidOperationException("Unreadable event"));
    }


    private sealed class StubReader(IReadOnlyList<EventEnvelope> events, bool hasMore = false) : IBoundedEventStreamReader
    {
        public List<(StreamId, int)> Requests { get; } = [];
        public Task<EventStreamWindow> ReadAsync(StreamId streamId, int maxEvents, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add((streamId, maxEvents));
            return Task.FromResult(new EventStreamWindow(events.Take(maxEvents).ToArray(), hasMore || events.Count > maxEvents));
        }
    }
}
