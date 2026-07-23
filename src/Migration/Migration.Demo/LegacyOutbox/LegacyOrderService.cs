using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.Versioning;
using EventSourcingCqrs.Migration.Demo.Cdc;
using Npgsql;

namespace EventSourcingCqrs.Migration.Demo.LegacyOutbox;

// Chapter 18: the outbox-on-legacy write side. Intent is captured at write time, inside the CRUD
// transaction: the legacy order path writes its row and the serialized domain event to the outbox
// together, so the event cannot be lost relative to the state change. It serializes through the event
// store's own registry and JSON options (the shared seam) and builds the events with the same identity
// and default conventions the CDC translator uses, so the two paths produce identical events.
public sealed class LegacyOrderService
{
    private readonly string _legacyConnectionString;
    private readonly EventTypeRegistry _eventTypes;
    private readonly JsonSerializerOptions _jsonOptions;

    public LegacyOrderService(
        string legacyConnectionString,
        EventTypeRegistry eventTypes,
        JsonSerializerOptions jsonOptions)
    {
        _legacyConnectionString = legacyConnectionString;
        _eventTypes = eventTypes;
        _jsonOptions = jsonOptions;
    }

    public async Task PlaceOrderAsync(
        long orderId, string customerName, decimal total, CancellationToken cancellationToken)
    {
        var occurredUtc = DateTime.UtcNow;
        var drafted = new OrderDrafted(
            LegacyChangeTranslator.OrderIdFor(orderId),
            LegacyChangeTranslator.CustomerIdFor(customerName),
            occurredUtc,
            LegacyChangeTranslator.LegacyChannel);

        await using var connection = new NpgsqlConnection(_legacyConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await InsertOrderAsync(connection, transaction, orderId, customerName, "new", total, cancellationToken);
        await InsertOutboxAsync(connection, transaction, drafted, occurredUtc, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkOrderPaidAsync(long orderId, CancellationToken cancellationToken)
    {
        var occurredUtc = DateTime.UtcNow;

        await using var connection = new NpgsqlConnection(_legacyConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var (customerName, total) = await ReadOrderAsync(connection, transaction, orderId, cancellationToken);
        await UpdateStatusAsync(connection, transaction, orderId, LegacyChangeTranslator.PlacedStatus, cancellationToken);

        var placed = new OrderPlaced(
            LegacyChangeTranslator.OrderIdFor(orderId),
            LegacyChangeTranslator.CustomerIdFor(customerName),
            new Money(total, Currency.USD),
            occurredUtc);
        await InsertOutboxAsync(connection, transaction, placed, occurredUtc, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task InsertOutboxAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        IDomainEvent @event, DateTime occurredUtc, CancellationToken ct)
    {
        var aggregateId = @event switch
        {
            OrderDrafted drafted => drafted.OrderId,
            OrderPlaced placed => placed.OrderId,
            _ => throw new InvalidOperationException($"Unroutable outbox event {@event.GetType().Name}."),
        };
        var typeName = _eventTypes.NameFor(@event.GetType());
        var payload = JsonSerializer.Serialize(@event, @event.GetType(), _jsonOptions);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO legacy.legacy_outbox (aggregate_id, type_name, payload, occurred_utc) "
            + "VALUES (@aggregate_id, @type_name, @payload::jsonb, @occurred_utc)";
        command.Parameters.AddWithValue("aggregate_id", aggregateId);
        command.Parameters.AddWithValue("type_name", typeName);
        command.Parameters.AddWithValue("payload", payload);
        command.Parameters.AddWithValue("occurred_utc", occurredUtc);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertOrderAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long orderId, string customerName, string status, decimal total, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO legacy.orders (id, customer_name, status, total) "
            + "VALUES (@id, @name, @status, @total)";
        command.Parameters.AddWithValue("id", orderId);
        command.Parameters.AddWithValue("name", customerName);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("total", total);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<(string CustomerName, decimal Total)> ReadOrderAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long orderId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT customer_name, total FROM legacy.orders WHERE id = @id";
        command.Parameters.AddWithValue("id", orderId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException($"Legacy order {orderId} does not exist.");
        }

        return (reader.GetString(0), reader.GetDecimal(1));
    }

    private static async Task UpdateStatusAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long orderId, string status, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE legacy.orders SET status = @status WHERE id = @id";
        command.Parameters.AddWithValue("id", orderId);
        command.Parameters.AddWithValue("status", status);
        await command.ExecuteNonQueryAsync(ct);
    }
}
