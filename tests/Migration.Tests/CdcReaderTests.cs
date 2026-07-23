using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Migration.Demo.Cdc;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Migration.Tests;

// S2: the CDC reader turns legacy CRUD writes into domain events. A legacy order's insert and its
// paid update become OrderDrafted then the placed-lifecycle event on that order's stream; a second
// order's insert becomes its own OrderDrafted. Rerunning with no new legacy writes emits nothing new.
//
// RED this turn: CdcReader.RunAsync is a no-op, so every asserted stream is empty and both facts fail
// on their emission assertions.
public sealed class CdcReaderTests : IClassFixture<CdcDatabaseFixture>
{
    private const long FirstOrderId = 1;
    private const long SecondOrderId = 2;

    private readonly CdcDatabaseFixture _fixture;

    public CdcReaderTests(CdcDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Cdc_reader_emits_domain_events_for_pending_changes()
    {
        var context = await _fixture.CreateContextAsync();
        await SeedOrderAsync(context, FirstOrderId, "Ada Lovelace", "new", 42.50m);
        await UpdateStatusAsync(context, FirstOrderId, "paid");
        await SeedOrderAsync(context, SecondOrderId, "Grace Hopper", "new", 10.00m);

        await new CdcReader(context.LegacyConnectionString, context.EventStore, context.SchemaVersions)
            .RunAsync(CancellationToken.None);

        var firstEvents = await ReadStreamAsync(context, FirstOrderId);
        firstEvents.Select(e => e.Payload.GetType())
            .Should().Equal(typeof(OrderDrafted), typeof(OrderPlaced));
        var drafted = firstEvents[0].Payload.Should().BeOfType<OrderDrafted>().Subject;
        drafted.OrderId.Should().Be(LegacyChangeTranslator.OrderIdFor(FirstOrderId));
        drafted.CustomerId.Should().Be(LegacyChangeTranslator.CustomerIdFor("Ada Lovelace"));
        var placed = firstEvents[1].Payload.Should().BeOfType<OrderPlaced>().Subject;
        placed.OrderId.Should().Be(LegacyChangeTranslator.OrderIdFor(FirstOrderId));
        placed.Total.Should().Be(new Money(42.50m, Currency.USD));

        var secondEvents = await ReadStreamAsync(context, SecondOrderId);
        secondEvents.Select(e => e.Payload.GetType()).Should().Equal(typeof(OrderDrafted));
        secondEvents[0].Payload.Should().BeOfType<OrderDrafted>()
            .Which.OrderId.Should().Be(LegacyChangeTranslator.OrderIdFor(SecondOrderId));
    }

    [Fact]
    public async Task Cdc_reader_rerun_emits_nothing_new()
    {
        var context = await _fixture.CreateContextAsync();
        await SeedOrderAsync(context, FirstOrderId, "Ada Lovelace", "new", 42.50m);
        await UpdateStatusAsync(context, FirstOrderId, "paid");
        await SeedOrderAsync(context, SecondOrderId, "Grace Hopper", "new", 10.00m);

        var reader = new CdcReader(context.LegacyConnectionString, context.EventStore, context.SchemaVersions);
        await reader.RunAsync(CancellationToken.None);

        var firstCount = (await ReadStreamAsync(context, FirstOrderId)).Count;
        var secondCount = (await ReadStreamAsync(context, SecondOrderId)).Count;
        // The first run must have emitted the events before rerun-idempotency is meaningful.
        firstCount.Should().Be(2);
        secondCount.Should().Be(1);

        await reader.RunAsync(CancellationToken.None);

        (await ReadStreamAsync(context, FirstOrderId)).Count.Should().Be(firstCount);
        (await ReadStreamAsync(context, SecondOrderId)).Count.Should().Be(secondCount);
    }

    private static async Task<IReadOnlyList<EventEnvelope>> ReadStreamAsync(
        CdcTestContext context, long legacyOrderId)
    {
        var stream = StreamId.ForAggregate<Order>(
            WellKnownTenants.Default, LegacyChangeTranslator.OrderIdFor(legacyOrderId));
        return await context.EventStore.ReadStreamAsync(stream, 0, CancellationToken.None);
    }

    private static async Task SeedOrderAsync(
        CdcTestContext context, long id, string customerName, string status, decimal total)
    {
        await using var connection = new NpgsqlConnection(context.LegacyConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO legacy.orders (id, customer_name, status, total) "
            + "VALUES (@id, @name, @status, @total)";
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", customerName);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("total", total);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task UpdateStatusAsync(CdcTestContext context, long id, string status)
    {
        await using var connection = new NpgsqlConnection(context.LegacyConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE legacy.orders SET status = @status WHERE id = @id";
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("status", status);
        await command.ExecuteNonQueryAsync();
    }
}
