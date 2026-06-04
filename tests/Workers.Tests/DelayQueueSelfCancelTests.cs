using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Hosts.Workers;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.ProcessManagers.OrderFulfillment;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NpgsqlTypes;
using Xunit;

namespace EventSourcingCqrs.Workers.Tests;

// Reproduces the delay-queue self-cancel deadlock. When an AwaitingPayment timeout fires,
// the dispatcher claims its delayed_commands row under FOR UPDATE SKIP LOCKED and holds the
// lock for the whole dispatch; the command's compensation, as its first effect, calls
// CancelAsync on the row scheduled by (pm.StreamId, "await-payment-timeout"), the same row
// the dispatcher holds locked. CancelAsync is a plain UPDATE on a fresh connection, so it
// blocks on that lock while the dispatching transaction awaits it: a self-lock the database
// cannot detect (one side waits on a C# await, not a DB lock).
//
// The experiment seeds matching provenance so the compensation cancels the locked row and the
// batch hangs, surfaced as a bounded OperationCanceledException via the token. The control
// changes only the provenance (a fresh PM stream id) so the cancel matches nothing and the
// dispatch completes. Control passes today and after the fix; the experiment fails bounded
// today and passes once CancelAsync skips the locked row.
public class DelayQueueSelfCancelTests : IClassFixture<PostgresFixture>
{
    // Comfortably above a non-deadlocked single-batch dispatch (sub-second). When the
    // self-cancel blocks, the token converts the otherwise-indefinite stall into a bounded
    // OperationCanceledException, since the token threads from ProcessBatchAsync down into
    // CancelAsync's ExecuteNonQueryAsync.
    private static readonly TimeSpan DispatchBound = TimeSpan.FromSeconds(10);

    // The literal value of OrderFulfillmentSteps.AwaitPaymentTimeout, which is internal to the
    // ProcessManagers assembly. The matching row references it by content so it truly matches
    // what the compensation cancels.
    private const string AwaitPaymentTimeoutStep = "await-payment-timeout";

    private readonly PostgresFixture _fixture;

    public DelayQueueSelfCancelTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Non_self_cancelling_timeout_dispatches_within_bound()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        using var host = WorkersHostFactory.Build(connStr, connStr);
        var processor = ResolveProcessor(host);
        var orderId = Guid.NewGuid();

        // Non-matching provenance: a fresh, unrelated PM stream id, so the compensation's
        // CancelAsync matches nothing and never contends with the locked row.
        await SeedChainAsync(
            host,
            orderId,
            scheduledByStreamId: $"{StreamPrefixes.OrderFulfillmentPm}:{Guid.NewGuid():N}",
            scheduledByStep: AwaitPaymentTimeoutStep,
            CancellationToken.None);

        using var cts = new CancellationTokenSource(DispatchBound);
        var processed = await processor.ProcessBatchAsync(cts.Token);

        processed.Should().Be(1);
    }

    [Fact]
    public async Task Self_cancelling_timeout_dispatches_without_deadlock()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        using var host = WorkersHostFactory.Build(connStr, connStr);
        var processor = ResolveProcessor(host);
        var orderId = Guid.NewGuid();

        // Matching provenance: the row carries the PM's own default-tenant stream id and the
        // await-payment-timeout step, so the compensation cancels the same row the processor
        // holds locked mid-dispatch.
        var pmStreamValue = StreamId
            .ForProcessManager(StreamPrefixes.OrderFulfillmentPm, WellKnownTenants.Default, orderId)
            .Value;
        await SeedChainAsync(
            host,
            orderId,
            scheduledByStreamId: pmStreamValue,
            scheduledByStep: AwaitPaymentTimeoutStep,
            CancellationToken.None);

        using var cts = new CancellationTokenSource(DispatchBound);
        var processed = await processor.ProcessBatchAsync(cts.Token);

        processed.Should().Be(1);
    }

    private static DelayQueueProcessor ResolveProcessor(IHost host) =>
        host.Services.GetServices<IHostedService>().OfType<DelayQueueProcessor>().Single();

    // Seeds the OF PM at AwaitingPayment on its default stream, a Placed order under the default
    // tenant (so the dispatched CancelOrder completes cleanly when the cancel is a no-op), and a
    // due timeout row with the given provenance and the {order_id} payload.
    private static async Task SeedChainAsync(
        IHost host, Guid orderId, string scheduledByStreamId, string scheduledByStep, CancellationToken ct)
    {
        await SeedPmAtAwaitingPaymentAsync(host, orderId, ct);
        await SeedPlacedOrderAsync(host, orderId, ct);
        await SeedDueTimeoutRowAsync(host, orderId, scheduledByStreamId, scheduledByStep, ct);
    }

    private static async Task SeedPmAtAwaitingPaymentAsync(IHost host, Guid orderId, CancellationToken ct)
    {
        var pmStream = StreamId.ForProcessManager(
            StreamPrefixes.OrderFulfillmentPm, WellKnownTenants.Default, orderId);
        var pm = new OrderFulfillmentProcessManager(pmStream);
        pm.Start(orderId, new Money(20m, Currency.USD), Guid.NewGuid());   // -> AwaitingPayment

        using var scope = host.Services.CreateScope();
        var pms = scope.ServiceProvider
            .GetRequiredService<IProcessManagerRepository<OrderFulfillmentProcessManager>>();
        await pms.SaveAsync(pm, ct);
    }

    private static async Task SeedPlacedOrderAsync(IHost host, Guid orderId, CancellationToken ct)
    {
        var stream = StreamId.ForAggregate<Order>(WellKnownTenants.Default, orderId);
        var eventStore = host.Services.GetRequiredService<IEventStore>();
        var customerId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await eventStore.AppendAsync(stream, 0,
        [
            Envelope(stream, 1, new OrderDrafted(orderId, customerId, now), now),
            Envelope(stream, 2, new OrderLineAdded(orderId, lineId, "SKU-1", 1, new Money(20m, Currency.USD), now), now),
            Envelope(stream, 3, new ShippingAddressSet(orderId, new Address("1 Main St", "Smalltown", "12345", "US"), now), now),
            Envelope(stream, 4, new OrderPlaced(orderId, customerId, new Money(20m, Currency.USD), now), now),
        ], ct);
    }

    private static async Task SeedDueTimeoutRowAsync(
        IHost host, Guid orderId, string scheduledByStreamId, string scheduledByStep, CancellationToken ct)
    {
        var jsonOptions = host.Services.GetRequiredService<JsonSerializerOptions>();
        var payloadJson = JsonSerializer.Serialize(new TimeoutAwaitingPaymentForOrder(orderId), jsonOptions);
        var factory = host.Services.GetRequiredService<INpgsqlConnectionFactory>();

        await using var conn = await factory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO event_store.delayed_commands " +
            "(fire_at_utc, command_type, command_payload, correlation_id, causation_id, " +
            "actor_id, service_name, idempotency_key, scheduled_by_stream_id, scheduled_by_step, " +
            "attempt_count, tenant_id) " +
            "VALUES (@fire_at, @type, @payload, @correlation, @causation, @actor, @service, " +
            "@key, @stream, @step, @attempts, @tenant)";
        cmd.Parameters.AddWithValue("fire_at", NpgsqlDbType.TimestampTz, DateTime.UtcNow.AddMinutes(-5));
        cmd.Parameters.AddWithValue("type", NpgsqlDbType.Text, nameof(TimeoutAwaitingPaymentForOrder));
        cmd.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payloadJson);
        cmd.Parameters.AddWithValue("correlation", NpgsqlDbType.Uuid, Guid.NewGuid());
        cmd.Parameters.AddWithValue("causation", NpgsqlDbType.Uuid, Guid.NewGuid());
        cmd.Parameters.AddWithValue("actor", NpgsqlDbType.Uuid, SystemActors.OrderFulfillment.Id);
        cmd.Parameters.AddWithValue("service", NpgsqlDbType.Text, SystemActors.OrderFulfillment.ServiceName);
        cmd.Parameters.AddWithValue("key", NpgsqlDbType.Text, Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("stream", NpgsqlDbType.Text, scheduledByStreamId);
        cmd.Parameters.AddWithValue("step", NpgsqlDbType.Text, scheduledByStep);
        cmd.Parameters.AddWithValue("attempts", NpgsqlDbType.Integer, 0);
        cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, WellKnownTenants.Default.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static EventEnvelope Envelope(StreamId streamId, int version, IDomainEvent payload, DateTime now)
    {
        var eventId = Guid.NewGuid();
        var metadata = new EventMetadata(
            EventId: eventId,
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.Empty,
            Source: "self-cancel-red",
            SchemaVersion: 1,
            OccurredUtc: now,
            Tenant: WellKnownTenants.Default);
        return new EventEnvelope(
            StreamId: streamId,
            StreamVersion: version,
            EventId: eventId,
            EventType: payload.GetType().Name,
            EventVersion: 1,
            Payload: payload,
            Metadata: metadata,
            OccurredUtc: now,
            GlobalPosition: 0);
    }
}
