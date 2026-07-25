using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Migration.Demo;
using EventSourcingCqrs.Migration.Demo.Cdc;
using EventSourcingCqrs.Migration.Demo.LegacyOutbox;
using EventSourcingCqrs.Migration.Demo.Shadow;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Migration.Tests;

// S5: shadow mode. The shadow service does the authoritative legacy write and emits the corresponding
// domain events in parallel, and a comparator checks the two agree. This is how a team builds confidence
// in the event-sourced path before making it authoritative: run it in the shadow, compare, and only then
// cut over.
//
// RED this turn: the shadow service is a no-op, so both facts fail on the absent legacy row (and empty
// stream).
public sealed class ShadowModeTests : IClassFixture<CdcDatabaseFixture>
{
    private const long OrderId = 1;

    private readonly CdcDatabaseFixture _fixture;

    public ShadowModeTests(CdcDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Shadow_service_emits_events_matching_the_legacy_write()
    {
        var context = await _fixture.CreateContextAsync();
        var shadow = CreateShadow(context);

        await shadow.PlaceOrderAsync(OrderId, "Ada Lovelace", 42.50m, CancellationToken.None);
        await shadow.MarkOrderPaidAsync(OrderId, CancellationToken.None);

        (await OrderStatusAsync(context, OrderId)).Should().Be("paid");

        var stream = await ReadStreamAsync(context, OrderId);
        stream.Select(e => e.Payload.GetType()).Should().Equal(typeof(OrderDrafted), typeof(OrderPlaced));

        var legacyState = await ReadLegacyStateAsync(context, OrderId);
        var payloads = stream.Select(e => e.Payload).ToList();
        ShadowComparator.Compare(legacyState, payloads).IsMatch.Should().BeTrue();
    }

    [Fact]
    public async Task Comparator_surfaces_divergence_between_the_two_systems()
    {
        var context = await _fixture.CreateContextAsync();
        var shadow = CreateShadow(context);

        await shadow.PlaceOrderAsync(OrderId, "Ada Lovelace", 42.50m, CancellationToken.None);
        // A legacy-only status change the shadow's event side never sees.
        await UpdateLegacyStatusAsync(context, OrderId, "cancelled");

        var legacyState = await ReadLegacyStateAsync(context, OrderId);
        var payloads = (await ReadStreamAsync(context, OrderId)).Select(e => e.Payload).ToList();

        var result = ShadowComparator.Compare(legacyState, payloads);
        result.IsMatch.Should().BeFalse();
        result.Detail.Should().Contain("status");
    }

    private static ShadowOrderService CreateShadow(CdcTestContext context)
        => new(
            new LegacyOrderService(context.LegacyConnectionString, context.EventTypes, context.JsonOptions),
            new EventStreamAppender(context.EventStore, context.SchemaVersions),
            context.LegacyConnectionString);

    private static async Task<IReadOnlyList<EventEnvelope>> ReadStreamAsync(CdcTestContext context, long legacyId)
    {
        var stream = StreamId.ForAggregate<Order>(
            WellKnownTenants.Default, LegacyChangeTranslator.OrderIdFor(legacyId));
        return await context.EventStore.ReadStreamAsync(stream, 0, CancellationToken.None);
    }

    private static async Task<string?> OrderStatusAsync(CdcTestContext context, long legacyId)
    {
        await using var connection = new NpgsqlConnection(context.LegacyConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM legacy.orders WHERE id = @id";
        command.Parameters.AddWithValue("id", legacyId);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<LegacyOrderState> ReadLegacyStateAsync(CdcTestContext context, long legacyId)
    {
        await using var connection = new NpgsqlConnection(context.LegacyConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, customer_name, status, total FROM legacy.orders WHERE id = @id";
        command.Parameters.AddWithValue("id", legacyId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException($"Legacy order {legacyId} does not exist.");
        }

        return new LegacyOrderState(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetDecimal(3));
    }

    private static async Task UpdateLegacyStatusAsync(CdcTestContext context, long legacyId, string status)
    {
        await using var connection = new NpgsqlConnection(context.LegacyConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE legacy.orders SET status = @status WHERE id = @id";
        command.Parameters.AddWithValue("id", legacyId);
        command.Parameters.AddWithValue("status", status);
        await command.ExecuteNonQueryAsync();
    }
}
