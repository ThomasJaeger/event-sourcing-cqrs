using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing;
using EventSourcingCqrs.Domain.Billing.Events;
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

// The structural cross-tenant coverage harness for the command types the Workers host registers
// (ADR 0031's coverage mandate), the Workers-host twin of the Api-host CrossTenantCommandCoverageTests.
// The Workers host registers only the OrderFulfillment timeout commands (ADR 0017), which round-trip
// through the delay queue rather than the by-name dispatch surface, so they need their own structural
// coverage. The meta-test enumerates the command types the Workers host registers, asserts
// every one has a cross-tenant coverage entry (completeness), and runs every entry (correctness), so a
// timeout command added to the Workers registry without an isolation case fails the suite by construction.
//
// Each case drives the full delay-queue chain deterministically on the fixed self-cancel chain (commit
// 407b674): seed the PM at its awaiting state, the order under both tenants, and a due timeout row under
// the non-default tenant, then pump one ProcessBatchAsync. The compensation resurfaces under the row's
// tenant, so the non-default order ends Cancelled and the same-id default order stays untouched. The
// dispatch-timeout case also seeds the authorized payment its with-releases compensation voids, since the
// PM only reaches AwaitingDispatch after PaymentAuthorized, so a payment always exists there.
public class WorkersCommandCrossTenantCoverageTests : IClassFixture<PostgresFixture>
{
    // Comfortably above a single-batch dispatch (sub-second). The fixed CancelAsync skips the locked
    // firing row, so the self-cancel the compensation performs no longer blocks; the bound stays as the
    // backstop the self-cancel reproduction established.
    private static readonly TimeSpan DispatchBound = TimeSpan.FromSeconds(10);

    // The non-default tenant, the same literal the Api-side cross-tenant cases seed their twin under, so
    // the two boundaries assert isolation against one operative tenant value.
    private static readonly TenantId OtherTenant =
        TenantId.From(Guid.Parse("55555555-5555-5555-5555-555555555555"));

    private static readonly DateTime SeededAt = new(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);

    // The literal values of OrderFulfillmentSteps.AwaitPaymentTimeout and AwaitDispatchTimeout, which are
    // internal to the ProcessManagers assembly. The due row references the step by content so it matches
    // the row the compensation self-cancels.
    private const string AwaitPaymentTimeoutStep = "await-payment-timeout";
    private const string AwaitDispatchTimeoutStep = "await-dispatch-timeout";

    private readonly PostgresFixture _fixture;

    public WorkersCommandCrossTenantCoverageTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Every_registered_command_type_has_cross_tenant_coverage()
    {
        var connectionString = await _fixture.CreateMigratedDatabaseAsync();
        using var host = WorkersHostFactory.Build(EventStoreProvider.Postgres, connectionString, connectionString);
        var registered = host.Services
            .GetRequiredService<CommandTypeRegistry>()
            .EnumerateCommands();

        var cases = new Dictionary<Type, Func<Task>>
        {
            [typeof(TimeoutAwaitingPaymentForOrder)] = () => PaymentTimeoutIsolatesAsync(host),
            [typeof(TimeoutAwaitingDispatchForOrder)] = () => DispatchTimeoutIsolatesAsync(host),
        };

        // Completeness: a registered command type with no coverage entry fails here by
        // construction, mirroring the Api-host command coverage walk.
        CrossTenantCoverage.FindUncovered(registered, cases.Keys.ToHashSet())
            .Should().BeEmpty();

        // Correctness: run every entry, so the structural check exercises the real isolation
        // assertion for each type rather than asserting a case is merely present.
        foreach (var commandType in registered)
        {
            await cases[commandType]();
        }
    }

    // Branch 1 (CancelOrder-only). Closes the ADR 0017 Commit-2 residual: a non-default-tenant payment
    // timeout drives the non-default-tenant order to Cancelled while the same-id default-tenant order
    // stays untouched. No payment was ever authorized at AwaitingPayment, so the compensation voids
    // nothing.
    private static async Task PaymentTimeoutIsolatesAsync(IHost host)
    {
        var orderId = Guid.NewGuid();
        var eventStore = host.Services.GetRequiredService<IEventStore>();

        // The PM lives on OtherTenant's stream, so OtherTenant's OrderPlaced is what caused it.
        await SeedPlacedOrderAsync(eventStore, orderId, WellKnownTenants.Default);
        var causing = await SeedPlacedOrderAsync(eventStore, orderId, OtherTenant);
        await SeedPmAtAwaitingPaymentAsync(host, orderId, causing);
        await SeedDueTimeoutRowAsync(
            host, orderId, new TimeoutAwaitingPaymentForOrder(orderId), AwaitPaymentTimeoutStep, OtherTenant);

        await PumpAsync(host);

        await AssertOrderCancelledAsync(eventStore, orderId, OtherTenant);
        await AssertOrderUntouchedAsync(eventStore, orderId, WellKnownTenants.Default);
    }

    // With-releases branch (branch 4 collapsed into branch 3). The non-default-tenant dispatch timeout
    // cancels the non-default-tenant order and voids its authorized payment under the same tenant; the
    // same-id default-tenant order stays untouched. PaymentAuthorized is seeded because the PM only
    // reaches AwaitingDispatch after authorization, so the unconditional void always has a payment.
    private static async Task DispatchTimeoutIsolatesAsync(IHost host)
    {
        var orderId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var eventStore = host.Services.GetRequiredService<IEventStore>();

        // The PM lives on OtherTenant's stream, so OtherTenant's OrderPlaced is what caused it.
        await SeedPlacedOrderAsync(eventStore, orderId, WellKnownTenants.Default);
        var causing = await SeedPlacedOrderAsync(eventStore, orderId, OtherTenant);
        await SeedPmAtAwaitingDispatchAsync(host, orderId, paymentId, causing);
        await SeedPaymentAuthorizedAsync(eventStore, orderId, paymentId, OtherTenant);
        await SeedDueTimeoutRowAsync(
            host, orderId, new TimeoutAwaitingDispatchForOrder(orderId), AwaitDispatchTimeoutStep, OtherTenant);

        await PumpAsync(host);

        await AssertOrderCancelledAsync(eventStore, orderId, OtherTenant);
        await AssertOrderUntouchedAsync(eventStore, orderId, WellKnownTenants.Default);
        await AssertPaymentVoidedAsync(eventStore, paymentId, OtherTenant);
    }

    private static async Task SeedPmAtAwaitingPaymentAsync(IHost host, Guid orderId, EventMetadata causing)
    {
        var pm = new OrderFulfillmentProcessManager(PmStream(orderId));
        pm.Start(orderId, new Money(20m, Currency.USD), Guid.NewGuid());   // -> AwaitingPayment
        await SavePmAsync(host, pm, causing);
    }

    private static async Task SeedPmAtAwaitingDispatchAsync(
        IHost host, Guid orderId, Guid paymentId, EventMetadata causing)
    {
        var pm = new OrderFulfillmentProcessManager(PmStream(orderId));
        pm.Start(orderId, new Money(20m, Currency.USD), paymentId);   // -> AwaitingPayment, records PaymentId
        pm.RecordPaymentAuthorized();                                 // -> AwaitingInventory
        pm.CompleteReservations();                                    // -> AwaitingShipment, no reserved lines
        pm.RequestShipmentScheduling(Guid.NewGuid());                 // -> AwaitingDispatch
        await SavePmAsync(host, pm, causing);
    }

    // The PM stream is tenant-formed: the timeout handler loads it through OrderFulfillmentStreams.For
    // under the resurfaced command's tenant, the accessor the caused bus sets from the due row, not the
    // default. The seeded PM and the due row's scheduled_by_stream_id both compose under OtherTenant, so
    // the resurfaced timeout finds and cancels the non-default-tenant PM at its own stream.
    private static StreamId PmStream(Guid orderId) =>
        StreamId.ForProcessManager(StreamPrefixes.OrderFulfillmentPm, OtherTenant, orderId);

    // The PM is saved under the context the outbox dispatcher would have established for the
    // OrderPlaced that started it (ADR 0042). The repository fails closed without one, and the causing
    // event's tenant is what puts the PM's events under the tenant the isolation case asserts on.
    private static async Task SavePmAsync(
        IHost host, OrderFulfillmentProcessManager pm, EventMetadata causing)
    {
        using var scope = host.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var accessor = sp.GetRequiredService<ICommandContextAccessor>();
        var tenantAccessor = sp.GetRequiredService<ICurrentTenantAccessor>();
        var previousContext = accessor.Current;
        var previousTenant = tenantAccessor.Current;
        accessor.Current = new CausedCommandContext(
            causing, SystemActors.OrderFulfillment, TimeProvider.System);
        tenantAccessor.Current = causing.Tenant;
        try
        {
            var pms = sp.GetRequiredService<IProcessManagerRepository<OrderFulfillmentProcessManager>>();
            await pms.SaveAsync(pm, CancellationToken.None);
        }
        finally
        {
            accessor.Current = previousContext;
            tenantAccessor.Current = previousTenant;
        }
    }

    // Returns the OrderPlaced envelope's metadata, the causing event for the PM seeded under this tenant.
    private static async Task<EventMetadata> SeedPlacedOrderAsync(
        IEventStore eventStore, Guid orderId, TenantId tenant)
    {
        var stream = StreamId.ForAggregate<Order>(tenant, orderId);
        var customerId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var orderPlaced = Envelope(
            stream, 4, new OrderPlaced(orderId, customerId, new Money(20m, Currency.USD), SeededAt), tenant);

        await eventStore.AppendAsync(stream, 0,
        [
            Envelope(stream, 1, new OrderDrafted(orderId, customerId, SeededAt, "web"), tenant),
            Envelope(stream, 2, new OrderLineAdded(orderId, lineId, "SKU-1", 1, new Money(20m, Currency.USD), SeededAt), tenant),
            Envelope(stream, 3, new ShippingAddressSet(orderId, new Address("1 Main St", "Smalltown", "12345", "US"), SeededAt), tenant),
            orderPlaced,
        ], CancellationToken.None);

        return orderPlaced.Metadata;
    }

    private static async Task SeedPaymentAuthorizedAsync(
        IEventStore eventStore, Guid orderId, Guid paymentId, TenantId tenant)
    {
        var stream = StreamId.ForAggregate<Payment>(tenant, paymentId);
        await eventStore.AppendAsync(stream, 0,
        [
            Envelope(
                stream, 1,
                new PaymentAuthorized(paymentId, orderId, new Money(20m, Currency.USD), "pm-ref-test", SeededAt),
                tenant),
        ], CancellationToken.None);
    }

    // The due row form per the delay-queue serializer (ADR 0017): command_type is the registered type
    // name the processor resolves through CommandTypeRegistry, command_payload is the command serialized
    // with the host options, tenant_id is the operative tenant the resurfaced command runs under, and the
    // scheduling stream and step name the default-tenant row the compensation self-cancels.
    private static async Task SeedDueTimeoutRowAsync(
        IHost host, Guid orderId, ICommand command, string scheduledByStep, TenantId tenant)
    {
        var jsonOptions = host.Services.GetRequiredService<JsonSerializerOptions>();
        var payloadJson = JsonSerializer.Serialize(command, command.GetType(), jsonOptions);
        var factory = host.Services.GetRequiredService<INpgsqlConnectionFactory>();

        await using var conn = await factory.OpenConnectionAsync(CancellationToken.None);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO event_store.delayed_commands " +
            "(fire_at_utc, command_type, command_payload, correlation_id, causation_id, " +
            "actor_id, service_name, idempotency_key, scheduled_by_stream_id, scheduled_by_step, " +
            "attempt_count, tenant_id) " +
            "VALUES (@fire_at, @type, @payload, @correlation, @causation, @actor, @service, " +
            "@key, @stream, @step, @attempts, @tenant)";
        cmd.Parameters.AddWithValue("fire_at", NpgsqlDbType.TimestampTz, DateTime.UtcNow.AddMinutes(-5));
        cmd.Parameters.AddWithValue("type", NpgsqlDbType.Text, command.GetType().Name);
        cmd.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payloadJson);
        cmd.Parameters.AddWithValue("correlation", NpgsqlDbType.Uuid, Guid.NewGuid());
        cmd.Parameters.AddWithValue("causation", NpgsqlDbType.Uuid, Guid.NewGuid());
        cmd.Parameters.AddWithValue("actor", NpgsqlDbType.Uuid, SystemActors.OrderFulfillment.Id);
        cmd.Parameters.AddWithValue("service", NpgsqlDbType.Text, SystemActors.OrderFulfillment.ServiceName);
        cmd.Parameters.AddWithValue("key", NpgsqlDbType.Text, Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("stream", NpgsqlDbType.Text, PmStream(orderId).Value);
        cmd.Parameters.AddWithValue("step", NpgsqlDbType.Text, scheduledByStep);
        cmd.Parameters.AddWithValue("attempts", NpgsqlDbType.Integer, 0);
        cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenant.Value);
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task PumpAsync(IHost host)
    {
        var processor = host.Services.GetServices<IHostedService>().OfType<DelayQueueProcessor>().Single();
        using var cts = new CancellationTokenSource(DispatchBound);
        await processor.ProcessBatchAsync(cts.Token);
    }

    private static async Task AssertOrderCancelledAsync(IEventStore eventStore, Guid orderId, TenantId tenant)
    {
        var events = await eventStore.ReadStreamAsync(StreamId.ForAggregate<Order>(tenant, orderId), fromVersion: 0);
        events.Should().Contain(e => e.EventType == nameof(OrderCancelled));
        events.Should().AllSatisfy(e => e.Metadata.Tenant.Should().Be(tenant));
    }

    // Untouched: the same-id order under the other tenant carries exactly its seeded placed events and no
    // cancellation, so the cross-tenant timeout did not leak across the discriminator.
    private static async Task AssertOrderUntouchedAsync(IEventStore eventStore, Guid orderId, TenantId tenant)
    {
        var events = await eventStore.ReadStreamAsync(StreamId.ForAggregate<Order>(tenant, orderId), fromVersion: 0);
        events.Should().HaveCount(4);
        events.Should().NotContain(e => e.EventType == nameof(OrderCancelled));
    }

    private static async Task AssertPaymentVoidedAsync(IEventStore eventStore, Guid paymentId, TenantId tenant)
    {
        var events = await eventStore.ReadStreamAsync(StreamId.ForAggregate<Payment>(tenant, paymentId), fromVersion: 0);
        events.Should().Contain(e => e.EventType == nameof(PaymentVoided));
        events.Should().AllSatisfy(e => e.Metadata.Tenant.Should().Be(tenant));
    }

    private static EventEnvelope Envelope(StreamId streamId, int version, IDomainEvent payload, TenantId tenant)
    {
        var eventId = Guid.NewGuid();
        var metadata = new EventMetadata(
            EventId: eventId,
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.Empty,
            Source: "workers-cross-tenant-coverage",
            SchemaVersion: 1,
            OccurredUtc: SeededAt,
            Tenant: tenant);
        return new EventEnvelope(
            StreamId: streamId,
            StreamVersion: version,
            EventId: eventId,
            EventType: payload.GetType().Name,
            EventVersion: 1,
            Payload: payload,
            Metadata: metadata,
            OccurredUtc: SeededAt,
            GlobalPosition: 0);
    }
}
