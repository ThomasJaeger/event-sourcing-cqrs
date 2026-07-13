using System.Data;
using System.Text.Encodings.Web;
using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.EventStore.ContractTests;
using EventSourcingCqrs.Infrastructure.EventStore.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.SqlServer;

// The SQL Server backend for the one IEventStore contract suite (docs/TDD_RULES.md §5). Every
// fact lives in EventStoreContractTests and is inherited unchanged. The suite is the standing
// RED for this engine: its facts could not run before this backend existed, and they are what
// "done" means for the adapter.
//
// This is the moment the suite stops being a characterization of one implementation and starts
// being a contract. Anything the PostgreSQL adapter got away with because the suite was written
// against it shows up here.
public class SqlServerEventStore_Contract_Tests
    : EventStoreContractTests, IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture;

    public SqlServerEventStore_Contract_Tests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    protected override async Task<IEventStoreContractBackend> CreateBackendAsync()
        => await SqlServerContractBackend.CreateAsync(_fixture);
}

// One instance per fact: its own migrated database with RCSI on, torn down with the fact. The
// suite's event types register into the adapter's registries here, which is the whole job of a
// backend.
internal sealed class SqlServerContractBackend : IEventStoreContractBackend
{
    private readonly string _connectionString;

    private SqlServerContractBackend(string connectionString, IEventStore store)
    {
        _connectionString = connectionString;
        Store = store;
    }

    public IEventStore Store { get; }

    public static async Task<SqlServerContractBackend> CreateAsync(SqlServerFixture fixture)
    {
        var connectionString = await fixture.CreateMigratedDatabaseAsync();

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

        var store = new SqlServerEventStore(
            new SqlServerConnectionFactory(connectionString),
            registry,
            pmRegistry,
            CreateJsonOptions());

        return new SqlServerContractBackend(connectionString, store);
    }

    // The adapter's serialization seam: snake_case so the PERSISTED computed columns can read the
    // metadata, and TenantIdJsonConverter so tenant_id lands as a flat scalar they can convert.
    internal static JsonSerializerOptions CreateJsonOptions()
        => new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters = { new TenantIdJsonConverter() },
            Encoder = JavaScriptEncoder.Default,
        };

    public async Task<IHeldWriter> StartHeldWriterAsync(StreamId streamId)
        => await SqlServerHeldWriter.StartAsync(_connectionString, streamId);

    public async Task<IReadOnlyList<long>> ReadCommittedPositionsAsync()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT global_position FROM event_store.events ORDER BY global_position", connection);

        var positions = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            positions.Add(reader.GetInt64(0));
        }
        return positions;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// A writer parked mid-transaction: it holds the append serialization lock and has drawn a
// global_position, but has not committed. The INSERT mirrors the adapter's own, NVARCHAR binding
// on the JSON columns included; the outbox row does not exist yet and no reader the suite drives
// would read it anyway.
internal sealed class SqlServerHeldWriter : IHeldWriter
{
    private readonly SqlConnection _connection;
    private readonly SqlTransaction _transaction;

    private SqlServerHeldWriter(SqlConnection connection, SqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public static async Task<SqlServerHeldWriter> StartAsync(
        string connectionString, StreamId streamId)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync();
            var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

            await using (var takeLock = new SqlCommand("sp_getapplock", connection, transaction)
            { CommandType = CommandType.StoredProcedure })
            {
                takeLock.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value =
                    SqlServerEventStore.AppendLockResource;
                takeLock.Parameters.Add("@LockMode", SqlDbType.NVarChar, 32).Value = "Exclusive";
                takeLock.Parameters.Add("@LockOwner", SqlDbType.NVarChar, 32).Value = "Transaction";
                takeLock.Parameters.Add("@LockTimeout", SqlDbType.Int).Value = -1;
                await takeLock.ExecuteNonQueryAsync();
            }

            await InsertOneEventAsync(connection, transaction, streamId);
            return new SqlServerHeldWriter(connection, transaction);
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
        SqlConnection connection, SqlTransaction transaction, StreamId streamId)
    {
        var envelope = ContractEnvelopes.Build(
            streamId, 1, new ContractOrderPlaced(Guid.NewGuid(), 1m));
        var jsonOptions = SqlServerContractBackend.CreateJsonOptions();

        await using var insert = new SqlCommand(
            "INSERT INTO event_store.events " +
            "(stream_id, stream_version, event_id, event_type, event_version, " +
            "payload, metadata, occurred_utc) " +
            "VALUES (@stream_id, @stream_version, @event_id, @event_type, " +
            "@event_version, @payload, @metadata, @occurred_utc)",
            connection,
            transaction);
        insert.Parameters.Add("@stream_id", SqlDbType.VarChar, 200).Value = envelope.StreamId.Value;
        insert.Parameters.Add("@stream_version", SqlDbType.Int).Value = envelope.StreamVersion;
        insert.Parameters.Add("@event_id", SqlDbType.UniqueIdentifier).Value = envelope.EventId;
        insert.Parameters.Add("@event_type", SqlDbType.VarChar, 200).Value = envelope.EventType;
        insert.Parameters.Add("@event_version", SqlDbType.SmallInt).Value =
            (short)envelope.EventVersion;
        insert.Parameters.Add("@payload", SqlDbType.NVarChar, -1).Value =
            JsonSerializer.Serialize(envelope.Payload, envelope.Payload.GetType(), jsonOptions);
        insert.Parameters.Add("@metadata", SqlDbType.NVarChar, -1).Value =
            JsonSerializer.Serialize(envelope.Metadata, jsonOptions);
        insert.Parameters.Add("@occurred_utc", SqlDbType.DateTimeOffset).Value =
            new DateTimeOffset(envelope.OccurredUtc, TimeSpan.Zero);

        await insert.ExecuteNonQueryAsync();
    }
}
