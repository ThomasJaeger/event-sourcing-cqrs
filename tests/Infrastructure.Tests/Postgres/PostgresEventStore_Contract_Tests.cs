using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.EventStore.ContractTests;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Versioning;
using EventSourcingCqrs.TestInfrastructure;
using Npgsql;
using NpgsqlTypes;
using Xunit;
using static EventSourcingCqrs.Infrastructure.Tests.Postgres.PostgresEventStoreTestKit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

// The PostgreSQL backend for the one IEventStore contract suite (docs/TDD_RULES.md §5). Every
// fact lives in EventStoreContractTests and is inherited unchanged; this class supplies the
// engine and the two hooks the divergence probes need, and nothing else. When the SQL Server
// adapter lands it writes a twin of this file and gets the same facts for free.
public class PostgresEventStore_Contract_Tests
    : EventStoreContractTests, IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PostgresEventStore_Contract_Tests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    protected override async Task<IEventStoreContractBackend> CreateBackendAsync()
        => await PostgresContractBackend.CreateAsync(_fixture);
}

// The held-writer capability suite on PostgreSQL. Same fixture, same backend: the relational store
// exposes an interactive, held-open append, so PostgresContractBackend implements
// IHeldWriterContractBackend and these facts run. Each fact gets its own migrated database, so the
// sibling classes are parallel-safe with the core suite above.
public class PostgresEventStore_HeldWriter_Contract_Tests
    : HeldWriterEventStoreContractTests, IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PostgresEventStore_HeldWriter_Contract_Tests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    protected override async Task<IHeldWriterContractBackend> CreateBackendAsync()
        => await PostgresContractBackend.CreateAsync(_fixture);
}

// The duplicate-id rejection capability suite on PostgreSQL: the relational store raises on a
// reused event id through the uq_events_event_id unique constraint, so it owes the stronger fact.
public class PostgresEventStore_DuplicateEventId_Contract_Tests
    : DuplicateEventIdRejectionContractTests, IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PostgresEventStore_DuplicateEventId_Contract_Tests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    protected override async Task<IEventStoreContractBackend> CreateBackendAsync()
        => await PostgresContractBackend.CreateAsync(_fixture);
}

// The upcast-on-read capability suite on PostgreSQL: the same backend, now carrying the read-time
// upcaster its pipeline was threaded with, seeds a version-1 row and reads it back lifted.
public class PostgresEventStore_UpcastOnRead_Contract_Tests
    : UpcastOnReadContractTests, IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PostgresEventStore_UpcastOnRead_Contract_Tests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    protected override async Task<IUpcastSeedingContractBackend> CreateBackendAsync()
        => await PostgresContractBackend.CreateAsync(_fixture);
}

// One instance per fact: its own migrated database, its own data source, torn down with the
// fact. The suite's event types register into the adapter's registries here, which is the whole
// job of a backend; the suite itself knows nothing about EventTypeRegistry.
internal sealed class PostgresContractBackend : IHeldWriterContractBackend, IUpcastSeedingContractBackend
{
    private readonly string _connectionString;
    private readonly NpgsqlDataSource _dataSource;

    private PostgresContractBackend(
        string connectionString, NpgsqlDataSource dataSource, IEventStore store)
    {
        _connectionString = connectionString;
        _dataSource = dataSource;
        Store = store;
    }

    public IEventStore Store { get; }

    public static async Task<PostgresContractBackend> CreateAsync(PostgresFixture fixture)
    {
        var connectionString = await fixture.CreateMigratedDatabaseAsync();
        var dataSource = NpgsqlDataSource.Create(connectionString);
        try
        {
            var registry = new EventTypeRegistry();
            foreach (var eventType in ContractEventTypes.DomainEvents)
            {
                registry.Register(eventType);
            }

            var pmRegistry = new ProcessManagerEventTypeRegistry();
            foreach (var eventType in ContractEventTypes.ProcessManagerEvents)
            {
                pmRegistry.Register(eventType);
            }

            // CreateJsonOptions is the adapter's serialization seam, TenantIdJsonConverter and
            // all. Reusing it keeps the metadata shape the generated tenant_id column parses on.
            var store = new PostgresEventStore(
                new NpgsqlConnectionFactory(dataSource),
                registry,
                pmRegistry,
                CreateJsonOptions(),
                new EventUpcasterPipeline(registry, [new ContractItemArchivedV1ToV2()]));

            return new PostgresContractBackend(connectionString, dataSource, store);
        }
        catch
        {
            await dataSource.DisposeAsync();
            throw;
        }
    }

    public async Task<IHeldWriter> StartHeldWriterAsync(StreamId streamId)
        => await PostgresHeldWriter.StartAsync(_connectionString, streamId);

    // A historical write, reproduced through the adapter's own append with a throwaway registry that
    // maps the V1 shape onto the terminal's stable storage name. The row lands with event_version 1
    // and the V1 payload, exactly as an older revision of the code left it; the suite's own store
    // reads it back through the real pipeline.
    public async Task SeedVersionOneAsync(
        StreamId stream, string terminalStorageName, IDomainEvent versionOnePayload)
    {
        var seedRegistry = new EventTypeRegistry().Register(
            versionOnePayload.GetType(), terminalStorageName);
        var seedStore = new PostgresEventStore(
            new NpgsqlConnectionFactory(_dataSource),
            seedRegistry,
            new ProcessManagerEventTypeRegistry(),
            CreateJsonOptions(),
            new EventUpcasterPipeline(seedRegistry, []));
        await seedStore.AppendAsync(
            stream, 0, [ContractEnvelopes.Build(stream, 1, versionOnePayload)], CancellationToken.None);
    }

    public async Task<IReadOnlyList<long>> ReadCommittedPositionsAsync()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT global_position FROM event_store.events ORDER BY global_position";

        var positions = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            positions.Add(reader.GetInt64(0));
        }
        return positions;
    }

    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync();
}

// A writer parked mid-transaction: it holds the append serialization lock and has drawn a
// global_position, but has not committed. The INSERT mirrors the adapter's own; the outbox row
// the adapter also writes is omitted, because no reader the suite drives reads the outbox.
internal sealed class PostgresHeldWriter : IHeldWriter
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    private PostgresHeldWriter(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public static async Task<PostgresHeldWriter> StartAsync(
        string connectionString, StreamId streamId)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync();
            var transaction = await connection.BeginTransactionAsync();

            await using (var takeLock = connection.CreateCommand())
            {
                takeLock.Transaction = transaction;
                takeLock.CommandText = "SELECT pg_advisory_xact_lock(@key)";
                takeLock.Parameters.AddWithValue(
                    "key", NpgsqlDbType.Bigint, PostgresEventStore.AppendAdvisoryLockKey);
                await takeLock.ExecuteNonQueryAsync();
            }

            await InsertOneEventAsync(connection, transaction, streamId);
            return new PostgresHeldWriter(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public Task CommitAsync() => _transaction.CommitAsync();

    public Task RollbackAsync() => _transaction.RollbackAsync();

    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static async Task InsertOneEventAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, StreamId streamId)
    {
        var envelope = ContractEnvelopes.Build(
            streamId, 1, new ContractOrderPlaced(Guid.NewGuid(), 1m));
        var jsonOptions = CreateJsonOptions();

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT INTO event_store.events " +
            "(stream_id, stream_version, event_id, event_type, event_version, " +
            "payload, metadata, occurred_utc) " +
            "VALUES (@stream_id, @stream_version, @event_id, @event_type, " +
            "@event_version, @payload, @metadata, @occurred_utc)";
        insert.Parameters.AddWithValue(
            "stream_id", NpgsqlDbType.Text, envelope.StreamId.Value);
        insert.Parameters.AddWithValue(
            "stream_version", NpgsqlDbType.Integer, envelope.StreamVersion);
        insert.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, envelope.EventId);
        insert.Parameters.AddWithValue("event_type", NpgsqlDbType.Text, envelope.EventType);
        insert.Parameters.AddWithValue(
            "event_version", NpgsqlDbType.Smallint, (short)envelope.EventVersion);
        insert.Parameters.AddWithValue(
            "payload",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(envelope.Payload, envelope.Payload.GetType(), jsonOptions));
        insert.Parameters.AddWithValue(
            "metadata",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(envelope.Metadata, jsonOptions));
        insert.Parameters.AddWithValue(
            "occurred_utc", NpgsqlDbType.TimestampTz, envelope.OccurredUtc);

        await insert.ExecuteNonQueryAsync();
    }
}
