using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using Xunit;
using static EventSourcingCqrs.Infrastructure.Tests.Postgres.PostgresEventStoreTestKit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

public class PostgresEventStore_ReadStreamAsync_Tests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PostgresEventStore_ReadStreamAsync_Tests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ReadStream_returns_empty_for_unknown_stream()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresEventStore(dataSource, CreateRegistry(), CreateJsonOptions());

        var read = await store.ReadStreamAsync(Guid.NewGuid(), 0, CancellationToken.None);

        read.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadStream_returns_single_event_in_order()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresEventStore(dataSource, CreateRegistry(), CreateJsonOptions());
        var streamId = Guid.NewGuid();
        var payload = new TestPayload(Guid.NewGuid(), 12.34m);
        await store.AppendAsync(
            streamId, 0,
            [BuildEnvelope(streamId, 1, payload)],
            CancellationToken.None);

        var read = await store.ReadStreamAsync(streamId, 0, CancellationToken.None);

        read.Should().HaveCount(1);
        read[0].StreamId.Should().Be(streamId);
        read[0].StreamVersion.Should().Be(1);
        read[0].Payload.Should().BeOfType<TestPayload>().Which.Should().Be(payload);
    }

    [Fact]
    public async Task ReadStream_returns_multiple_events_in_stream_version_order()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresEventStore(dataSource, CreateRegistry(), CreateJsonOptions());
        var streamId = Guid.NewGuid();
        await store.AppendAsync(
            streamId, 0,
            [
                BuildEnvelope(streamId, 1, new TestPayload(Guid.NewGuid(), 1m)),
                BuildEnvelope(streamId, 2, new OtherTestPayload("two")),
                BuildEnvelope(streamId, 3, new TestPayload(Guid.NewGuid(), 3m)),
            ],
            CancellationToken.None);

        var read = await store.ReadStreamAsync(streamId, 0, CancellationToken.None);

        read.Select(e => e.StreamVersion).Should().Equal(1, 2, 3);
        read[0].Payload.Should().BeOfType<TestPayload>();
        read[1].Payload.Should().BeOfType<OtherTestPayload>().Which.Description.Should().Be("two");
        read[2].Payload.Should().BeOfType<TestPayload>();
    }

    [Fact]
    public async Task ReadStream_filters_by_from_version_exclusive()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresEventStore(dataSource, CreateRegistry(), CreateJsonOptions());
        var streamId = Guid.NewGuid();
        await store.AppendAsync(
            streamId, 0,
            [
                BuildEnvelope(streamId, 1, new TestPayload(Guid.NewGuid(), 1m)),
                BuildEnvelope(streamId, 2, new TestPayload(Guid.NewGuid(), 2m)),
                BuildEnvelope(streamId, 3, new TestPayload(Guid.NewGuid(), 3m)),
            ],
            CancellationToken.None);

        var read = await store.ReadStreamAsync(streamId, 2, CancellationToken.None);

        read.Should().HaveCount(1);
        read[0].StreamVersion.Should().Be(3);
    }

    [Fact]
    public async Task ReadStream_throws_UnknownEventTypeException_when_event_type_not_registered()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresEventStore(dataSource, CreateRegistry(), CreateJsonOptions());
        var streamId = Guid.NewGuid();

        // Insert a row directly with an event_type the registry does not
        // know about. Reading is what surfaces the failure: registries are
        // typically populated at composition-root startup, and a deprecated
        // event type left in old streams is exactly the production scenario
        // this exception is built to make loud.
        await InsertEventDirectlyAsync(connStr, streamId, "DeprecatedEvent");

        var act = async () => await store.ReadStreamAsync(streamId, 0, CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<UnknownEventTypeException>()).Which;
        ex.TypeName.Should().Be("DeprecatedEvent");
        ex.StreamId.Should().Be(streamId);
        ex.InnerException.Should().BeOfType<UnknownEventTypeException>();
        ex.Message.Should().Contain(streamId.ToString());
    }

    private static async Task InsertEventDirectlyAsync(
        string connStr, Guid streamId, string eventType)
    {
        await using var connection = new NpgsqlConnection(connStr);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "INSERT INTO event_store.events " +
            "(stream_id, stream_version, event_id, event_type, event_version, " +
            "payload, metadata, occurred_utc) " +
            "VALUES (@stream_id, 1, @event_id, @event_type, 1, " +
            "'{}'::jsonb, " +
            "jsonb_build_object('correlation_id', gen_random_uuid()::text, " +
            "'causation_id', gen_random_uuid()::text), " +
            "now())";
        cmd.Parameters.AddWithValue("stream_id", NpgsqlDbType.Uuid, streamId);
        cmd.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, Guid.NewGuid());
        cmd.Parameters.AddWithValue("event_type", NpgsqlDbType.Text, eventType);
        await cmd.ExecuteNonQueryAsync();
    }
}
