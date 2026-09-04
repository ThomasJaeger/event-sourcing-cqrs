extern alias WebHost;

using System.Net;
using System.Net.Http.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Versioning;
using EventSourcingCqrs.IntegrationTests.Commands.Sales;
using EventSourcingCqrs.Projections.OrderThroughput;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Xunit;
using OrderThroughputRate = WebHost::EventSourcingCqrs.Hosts.Web.Components.OrderThroughputRate;

namespace EventSourcingCqrs.IntegrationTests.Metrics;

// Phase 12's exit condition: "the admin dashboard shows live system metrics that match what the
// AdminConsole tools show" (ADR 0039's metrics-parity requirement). ADR 0039 puts operational
// metrics in the AdminConsole and keeps them out of the Web meter, so the two sides render disjoint
// quantity families by decision and no single number appears on both. "Match" is therefore read as
// consistency with the substrate rather than equality of rendered figures: the count the Web meter's
// real read path produces must equal the order events the event store holds for that tenant and that
// window. event_store.events is the substrate the AdminConsole tools are windows onto, so
// pinning the meter against it is what makes the meter's number trustworthy against the operator's.
//
// The read path is the real one: the gated /queries dispatch (AuthorizationQueryBehavior, on
// Permission.ViewOrderThroughput) plus the page's own fold, OrderThroughputRate.Compute over
// OrderThroughputRate.RateWindow. The test never re-implements the window semantics; it calls the
// production fold and reads the window off the production constant, so the anchor (the newest bucket
// second) and the inclusive-both-ends 60-second span cannot drift between the page and this test.
//
// FIXTURE DEVIATION (Option B). The Api host does not drain the outbox in production; the Workers host
// owns the OutboxProcessor, and an Api-host processor would race it (src/Hosts/Api/Program.cs:53-56).
// This class overlays AddPostgresOutboxProcessor onto its own host instance only, so the real
// OutboxProcessor drives the real OrderThroughputProjection through the handler registrations
// AddReadModels already wires. The production race cannot occur here because no Workers host runs
// beside the fixture, and the overlay is scoped to this class: the shared ApiFixture default
// composition is untouched, and every other test class still gets an undrained outbox. Buckets are
// never seeded directly, because a seeded bucket would make the assertion circular.
//
// PROVENANCE: a characterization, green on write. The behavior it pins predates it, so the honest
// failure window is closed; it is committed to hold that behavior still, on the precedent of the two
// AdminConsole route-level e2es (session docs 0046 and 0048).
public sealed class OrderThroughputConsistencyTests : IClassFixture<ApiFixture>
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    // Wider than the 2-second budget the Workers-host precedent uses (WorkersHostIntegrationTests.cs:28):
    // that one waits on a single appended event, this one waits on the outbox rows of several placed
    // orders, each of which fans out to four Sales events.
    private static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(10);

    // Three orders, spaced past a second boundary, so the meter folds several distinct per-second
    // buckets rather than one. Everything the test places stays far inside the projection's 300-second
    // prune horizon, so no bucket it counts on can be pruned before the assert.
    private const int OrdersToPlace = 3;
    private static readonly TimeSpan SpacingBetweenOrders = TimeSpan.FromMilliseconds(1100);

    private readonly ApiFixture _fixture;

    public OrderThroughputConsistencyTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_meter_counts_exactly_the_order_events_the_event_store_holds_for_its_window()
    {
        await using var factory = _fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddPostgresOutboxProcessor()));
        var client = factory.CreateClient();

        for (var i = 0; i < OrdersToPlace; i++)
        {
            if (i > 0)
            {
                await Task.Delay(SpacingBetweenOrders);
            }
            await OrderSetup.PlaceOrderAsync(client, Guid.NewGuid());
        }

        await WaitForOutboxToDrainAsync();

        var reading = await ReadTheMeterAsync(client);
        reading.Should().NotBeNull("the placed orders populate the buckets the meter folds");

        var eventsInWindow = await CountSubscribedOrderEventsAsync(
            factory, windowStart: reading!.AsOfUtc - OrderThroughputRate.RateWindow, asOf: reading.AsOfUtc);

        reading.WindowedCount.Should().Be(eventsInWindow);
    }

    // The Web meter's figure, through the path the page takes: the gated query, then the production
    // fold over the production window.
    private static async Task<WebHost::EventSourcingCqrs.Hosts.Web.Components.ThroughputReading?>
        ReadTheMeterAsync(HttpClient client)
    {
        var response = await client.PostQueryAsync("GetOrderThroughput", new { });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var buckets = (await response.Content.ReadFromJsonAsync<IReadOnlyList<OrderThroughputRow>>())!;
        return OrderThroughputRate.Compute(buckets, OrderThroughputRate.RateWindow);
    }

    // The store-side figure, counted straight off the substrate. The second-flooring mirrors the
    // projection's own bucket key (OrderThroughputProjection.cs:83-84): date_trunc truncates toward the
    // earlier second exactly as its tick arithmetic does. The window is closed at both ends, matching
    // Compute, which admits the bucket at windowStart and anchors the far end on the newest bucket.
    private async Task<long> CountSubscribedOrderEventsAsync(
        WebApplicationFactory<Program> factory, DateTime windowStart, DateTime asOf)
    {
        var registry = factory.Services.GetRequiredService<EventTypeRegistry>();
        var subscribedTypeNames = SubscribedEventTypes().Select(registry.NameFor).ToArray();

        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM event_store.events " +
            "WHERE tenant_id = @tenant " +
            "AND event_type = ANY(@event_types) " +
            "AND date_trunc('second', occurred_utc) >= @window_start " +
            "AND date_trunc('second', occurred_utc) <= @as_of";
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, WellKnownTenants.Default.Value);
        command.Parameters.AddWithValue(
            "event_types", NpgsqlDbType.Array | NpgsqlDbType.Text, subscribedTypeNames);
        command.Parameters.AddWithValue("window_start", NpgsqlDbType.TimestampTz, windowStart);
        command.Parameters.AddWithValue("as_of", NpgsqlDbType.TimestampTz, asOf);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    // The event types the projection subscribes to, derived the way the runtime derives them: off its
    // IEventHandler<TEvent> interfaces, the same reflection ProjectionReplayer.BuildDispatchTable and
    // AddProjection do. The storage names come from the write path's own registry. Nothing here is a
    // literal, so a ninth handler on the projection cannot leave this test counting a stale set.
    private static IEnumerable<Type> SubscribedEventTypes()
        => typeof(OrderThroughputProjection).GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEventHandler<>))
            .Select(i => i.GetGenericArguments()[0]);

    // The settle: wait on the drain, never on the invariant. Polling until the two counts agree would
    // hollow out the assertion, so the condition is the outbox's own pending-row count, which is a
    // proxy for dispatch progress and says nothing about what the projection wrote.
    private async Task WaitForOutboxToDrainAsync()
    {
        var deadline = DateTime.UtcNow + DrainBudget;
        while (DateTime.UtcNow < deadline)
        {
            if (await PendingOutboxRowsAsync() == 0)
            {
                return;
            }
            await Task.Delay(PollInterval);
        }

        (await PendingOutboxRowsAsync()).Should()
            .Be(0, "the outbox should drain within {0}", DrainBudget);
    }

    private async Task<long> PendingOutboxRowsAsync()
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM event_store.outbox WHERE sent_utc IS NULL";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
