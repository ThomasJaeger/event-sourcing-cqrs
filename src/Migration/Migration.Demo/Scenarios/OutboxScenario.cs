using EventSourcingCqrs.Migration.Demo.LegacyOutbox;
using Npgsql;

namespace EventSourcingCqrs.Migration.Demo.Scenarios;

// Outbox-on-legacy: the legacy service writes its CRUD row and a serialized domain event to an outbox
// table in one transaction; the emitter drains the outbox into the event store. Ids in the 200s.
public static class OutboxScenario
{
    private const long OrderId = 201;

    public static async Task RunAsync(DemoContext context)
    {
        Console.WriteLine();
        Console.WriteLine("== Outbox-on-legacy: a transactional outbox drained into events (ids 200s) ==");

        var service = new LegacyOrderService(
            context.LegacyConnectionString, context.EventTypes, context.JsonOptions);
        await service.PlaceOrderAsync(OrderId, "Ada Lovelace", 42.50m, CancellationToken.None);
        await service.MarkOrderPaidAsync(OrderId, CancellationToken.None);

        var pending = await CountOutboxAsync(context.LegacyConnectionString, emitted: false);
        Console.WriteLine(
            $"Legacy service wrote order {OrderId} and {pending} unemitted outbox rows, each in the same transaction as its CRUD write.");

        await new LegacyOutboxEmitter(
                context.LegacyConnectionString, context.EventStore, context.SchemaVersions,
                context.EventTypes, context.JsonOptions)
            .DrainAsync(CancellationToken.None);

        var drained = await CountOutboxAsync(context.LegacyConnectionString, emitted: true);
        Console.WriteLine($"The emitter drained {drained} outbox rows into the event store:");
        await DemoNarration.PrintStreamAsync(context, OrderId, "order");
    }

    private static async Task<long> CountOutboxAsync(string connectionString, bool emitted)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = emitted
            ? "SELECT COUNT(*) FROM legacy.legacy_outbox WHERE emitted_utc IS NOT NULL"
            : "SELECT COUNT(*) FROM legacy.legacy_outbox WHERE emitted_utc IS NULL";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
