using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Hosts.Workers;
using EventSourcingCqrs.Projections.OrderList;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Workers.Tests;

// The full path end-to-end for the first time: AppendAsync writes events plus
// outbox in one transaction; commit fires the pg_notify trigger from migration
// 0005; the OutboxProcessor's listener wakes; ProcessBatchAsync hydrates the
// OrderPlaced; InProcessMessageDispatcher fans out to OrderListProjection;
// PostgresOrderListUnitOfWork writes the order_list row and advances the
// checkpoint, all in another single transaction. Commit 5's OutboxNotificationTests
// exercise the listener mechanically by seeding outbox rows via raw SQL; this
// is the first test driving the full chain through PostgresEventStore.AppendAsync.
public class WorkersHostIntegrationTests : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PollBudget = TimeSpan.FromSeconds(2);

    private readonly PostgresFixture _fixture;

    public WorkersHostIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task OrderPlaced_propagates_to_order_list_via_listen_notify()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        using var host = WorkersHostFactory.Build(connStr, connStr);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // StartAsync runs ProjectionStartupCatchUpService.StartingAsync (no-op
        // with no events yet) and then base.StartAsync which kicks off the
        // OutboxProcessor's StartAsync (opens the listener connection, runs
        // LISTEN). After this returns, the host is warm: the 2s poll budget
        // below applies to a registered listener and a connected processor.
        await host.StartAsync(cts.Token);
        try
        {
            var orderId = Guid.NewGuid();
            var envelope = BuildEnvelope(
                streamId: orderId,
                streamVersion: 1,
                payload: new OrderPlaced(
                    OrderId: orderId,
                    CustomerId: Guid.NewGuid(),
                    Total: new Money(99.95m, "USD"),
                    PlacedUtc: new DateTime(2026, 5, 16, 12, 0, 0, DateTimeKind.Utc)));

            var eventStore = host.Services.GetRequiredService<IEventStore>();
            await eventStore.AppendAsync(orderId, 0, [envelope], cts.Token);

            var orderListStore = host.Services.GetRequiredService<IOrderListStore>();
            var row = await PollForRowAsync(orderListStore, orderId, PollBudget, cts.Token);

            row.Should().NotBeNull(
                "AppendAsync should reach order_list end-to-end within {0} via LISTEN/NOTIFY", PollBudget);
            row!.OrderId.Should().Be(orderId);
            row.Status.Should().Be(OrderStatus.Placed);
            row.Total.Should().Be(new Money(99.95m, "USD"));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<OrderListRow?> PollForRowAsync(
        IOrderListStore store, Guid orderId, TimeSpan budget, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            var row = await store.GetAsync(orderId, ct);
            if (row is not null) return row;
            await Task.Delay(PollInterval, ct);
        }
        return await store.GetAsync(orderId, ct);
    }

    private static EventEnvelope BuildEnvelope(
        Guid streamId, int streamVersion, IDomainEvent payload)
    {
        var eventId = Guid.NewGuid();
        var when = new DateTime(2026, 5, 16, 12, 0, 0, DateTimeKind.Utc);
        var metadata = new EventMetadata(
            EventId: eventId,
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.Empty,
            Source: "integration-test",
            SchemaVersion: 1,
            OccurredUtc: when);
        return new EventEnvelope(
            StreamId: streamId,
            StreamVersion: streamVersion,
            EventId: eventId,
            EventType: payload.GetType().Name,
            EventVersion: 1,
            Payload: payload,
            Metadata: metadata,
            OccurredUtc: when,
            GlobalPosition: 0);
    }
}
