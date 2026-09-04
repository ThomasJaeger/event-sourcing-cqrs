using System.Text.Json;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Migration.Tests;

// S1: the CDC baseline. A CRUD write to the legacy orders table must leave an ordered audit
// trail in legacy.legacy_changes, one row per insert/update/delete, carrying the row image the
// later CDC pattern reads to emit domain events. This is the change-tracking table the book's
// Chapter 18 CDC pattern reads from.
//
// RED this turn: LegacySchemaApplier is a no-op placeholder, so legacy.orders does not exist and
// the first INSERT fails on the absent relation. GREEN ships the schema and the trigger.
public sealed class LegacyChangeTrackingTests : IClassFixture<LegacyDatabaseFixture>
{
    private readonly LegacyDatabaseFixture _fixture;

    public LegacyChangeTrackingTests(LegacyDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Legacy_write_operations_append_change_rows()
    {
        const long orderId = 1;
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();

        await ExecuteAsync(
            connection,
            "INSERT INTO legacy.orders (id, customer_name, status, total) "
            + "VALUES (@id, 'Ada Lovelace', 'new', 42.50)",
            orderId);
        await ExecuteAsync(
            connection,
            "UPDATE legacy.orders SET status = 'paid' WHERE id = @id",
            orderId);
        await ExecuteAsync(
            connection,
            "DELETE FROM legacy.orders WHERE id = @id",
            orderId);

        var changes = await ReadChangesAsync(connection);

        changes.Should().HaveCount(3);
        changes.Select(c => c.Operation).Should().Equal("I", "U", "D");
        changes.Should().OnlyContain(c => c.TableName == "orders");
        changes.Should().OnlyContain(c => c.RecordId == orderId);

        var insertStatus = StatusOf(changes[0].Payload);
        var updateStatus = StatusOf(changes[1].Payload);
        var deleteStatus = StatusOf(changes[2].Payload);

        // The insert and update payloads carry the new row image, so the update's status differs
        // from the insert's. The delete payload carries the old row image, the last state before
        // the row was removed.
        insertStatus.Should().Be("new");
        updateStatus.Should().Be("paid");
        insertStatus.Should().NotBe(updateStatus);
        deleteStatus.Should().Be("paid");
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, long orderId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("id", orderId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<ChangeRow>> ReadChangesAsync(NpgsqlConnection connection)
    {
        var rows = new List<ChangeRow>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT operation, table_name, record_id, payload "
            + "FROM legacy.legacy_changes ORDER BY change_id";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ChangeRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetFieldValue<string>(3)));
        }

        return rows;
    }

    private static string? StatusOf(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.GetProperty("status").GetString();
    }

    private sealed record ChangeRow(string Operation, string TableName, long RecordId, string Payload);
}
