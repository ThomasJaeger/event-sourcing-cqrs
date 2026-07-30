using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using EventSourcingCqrs.EventStore.ContractTests;
using EventSourcingCqrs.Infrastructure.EventStore.SqlServer;
using EventSourcingCqrs.Infrastructure.Versioning;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.SqlServer;

// The byte-level tripwire the engine-agnostic contract suite cannot carry, and the reason it
// cannot.
//
// The suite has a non-ASCII round-trip fact. It passes on this adapter even with the CORRUPTING
// parameter binding in place, and that is not a defect in the suite: System.Text.Json escapes
// non-ASCII to \uXXXX by default, so a serialized payload is pure ASCII by the time it reaches a
// driver, and pure ASCII survives a binding that narrows to a single-byte codepage. A suite that
// cannot look at stored bytes cannot see the difference.
//
// So this fact looks at the stored bytes. It drives an UNESCAPED non-ASCII payload through the
// adapter's real append path by relaxing the JSON encoder, which is exactly what a future change
// to the encoder pin would do, and then asserts on the bytes that landed in the column. If the
// JSON columns ever bind as VarChar instead of NVarChar, the client narrows the string to the
// connection codepage before it leaves the process and this goes red.
//
// The encoder pin in ServiceCollectionExtensions is the first line of defence. This is the second,
// and it is the one that holds if the first is ever relaxed on purpose.
public class SqlServerOutboxEncodingTests : IClassFixture<SqlServerFixture>
{
    // Accented Latin, CJK, an emoji (a surrogate pair), and Greek. None of it survives a
    // single-byte codepage.
    private const string NonAscii = "café / 日本語 / \U0001F680 / Ωμέγα";

    private readonly SqlServerFixture _fixture;

    public SqlServerOutboxEncodingTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Unescaped_non_ascii_json_lands_in_the_column_as_the_exact_utf8_bytes()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();

        // Deliberately not EventStoreJsonOptions.Create(). This fixture suspends the factory's
        // encoder pin, which is the thing under test, so consolidating this construction onto the
        // factory voids the fact: under the factory the payload is pure ASCII and the sanity
        // assertions below stop distinguishing a byte-correct binding from a lucky one.
        // The relaxed encoder is the whole point: it sends RAW UTF-8 to the driver instead of
        // \uXXXX escapes, so the parameter binding is the only thing protecting the payload.
        var relaxed = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters = { new TenantIdJsonConverter() },
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        var registry = new EventTypeRegistry();
        foreach (var eventType in ContractEventTypes.DomainEvents)
        {
            registry.Register(eventType);
        }

        var store = new SqlServerEventStore(
            new SqlServerConnectionFactory(connStr),
            registry,
            new ProcessManagerEventTypeRegistry(),
            relaxed,
            new EventUpcasterPipeline(registry, []));

        var stream = ContractEnvelopes.NewStreamId();
        var payload = new ContractOrderNoted(NonAscii);
        var envelope = ContractEnvelopes.Build(stream, 1, payload, source: NonAscii);

        await store.AppendAsync(stream, 0, [envelope], CancellationToken.None);

        // What the serializer produced is what the column must hold, byte for byte.
        var expectedPayloadJson = JsonSerializer.Serialize(
            (object)payload, typeof(ContractOrderNoted), relaxed);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedPayloadJson);

        // Sanity: the relaxed encoder really did emit raw non-ASCII, so this fact is testing what
        // it claims to test rather than passing on an ASCII payload the way the suite's does.
        expectedPayloadJson.Should().Contain("café");
        expectedBytes.Length.Should().BeGreaterThan(expectedPayloadJson.Length);

        var (storedBytes, dataLength) = await ReadPayloadBytesAsync(connStr, "event_store.events");

        dataLength.Should().Be(expectedBytes.Length);
        storedBytes.Should().Equal(expectedBytes);

        // The outbox copy is written by the same append, through the same binding helper, and is
        // dispatched to subscribers. It has to be byte-identical or the bus carries corruption.
        var (outboxBytes, outboxLength) = await ReadPayloadBytesAsync(connStr, "event_store.outbox");
        outboxLength.Should().Be(expectedBytes.Length);
        outboxBytes.Should().Equal(expectedBytes);

        // And it still round-trips through the port, which is the behavior a caller depends on.
        var read = await store.ReadStreamAsync(stream, 0, CancellationToken.None);
        read.Should().HaveCount(1);
        read[0].Payload.Should().BeOfType<ContractOrderNoted>().Which.Note.Should().Be(NonAscii);
        read[0].Metadata.Source.Should().Be(NonAscii);
    }

    // DATALENGTH is the byte count of the stored value, not the character count. On a UTF-8
    // VARCHAR column it is what proves the bytes are UTF-8 rather than a narrowed codepage.
    private static async Task<(byte[] Bytes, long DataLength)> ReadPayloadBytesAsync(
        string connStr, string table)
    {
        await using var connection = new SqlConnection(connStr);
        await connection.OpenAsync();
        await using var cmd = new SqlCommand(
            $"SELECT CONVERT(VARBINARY(MAX), payload), DATALENGTH(payload) FROM {table}",
            connection);

        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return ((byte[])reader[0], reader.GetInt64(1));
    }
}
