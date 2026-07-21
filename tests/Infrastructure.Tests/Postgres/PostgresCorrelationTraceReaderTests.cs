using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Versioning;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;
using static EventSourcingCqrs.Infrastructure.Tests.Postgres.PostgresEventStoreTestKit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

// Phase 12, the Correlation-ID Tracer's read port (RED). ICorrelationTraceReader returns every event
// carrying one correlation id, across streams and across tenants, ordered by global_position and bounded
// by a caller-supplied cap. These specs drive PostgresCorrelationTraceReader straight against migrated
// Testcontainers Postgres, with no web host and no service provider, the shape the sibling
// PostgresEventStore suites use.
//
// PostgresCorrelationTraceReader has no implementation yet, so this file does not compile: the RED. The
// seeds and the assertions below are the GREEN-ready shape that implementation will satisfy.
public class PostgresCorrelationTraceReaderTests : IClassFixture<PostgresFixture>
{
    private static readonly TenantId OtherTenant =
        TenantId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private readonly PostgresFixture _fixture;

    public PostgresCorrelationTraceReaderTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task A_trace_returns_events_across_streams_ordered_by_global_position()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = CreateStore(dataSource);
        var reader = CreateReader(dataSource);
        var correlationId = Guid.NewGuid();
        var streamA = NewStreamId();
        var streamB = NewStreamId();

        // Interleave the appends so global order differs from per-stream order.
        await store.AppendAsync(streamA, 0,
            [BuildEnvelope(streamA, 1, new TestPayload(Guid.NewGuid(), 1m), correlationId: correlationId)],
            CancellationToken.None);
        await store.AppendAsync(streamB, 0,
            [BuildEnvelope(streamB, 1, new TestPayload(Guid.NewGuid(), 2m), correlationId: correlationId)],
            CancellationToken.None);
        await store.AppendAsync(streamA, 1,
            [BuildEnvelope(streamA, 2, new TestPayload(Guid.NewGuid(), 3m), correlationId: correlationId)],
            CancellationToken.None);

        var trace = await reader.ReadTraceAsync(correlationId, 100, CancellationToken.None);

        trace.Rows.Select(r => r.StreamId).Should().Equal(streamA.Value, streamB.Value, streamA.Value);
        trace.Rows.Select(r => r.StreamVersion).Should().Equal(1, 1, 2);
        trace.Rows.Select(r => r.GlobalPosition).Should().BeInAscendingOrder();
        trace.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task A_trace_returns_process_manager_rows_alongside_aggregate_rows()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = CreateStore(dataSource);
        var reader = CreateReader(dataSource);
        var correlationId = Guid.NewGuid();
        var aggregateStream = NewStreamId();
        var pmStream = NewPmStreamId();

        await store.AppendAsync(aggregateStream, 0,
            [BuildEnvelope(aggregateStream, 1, new TestPayload(Guid.NewGuid(), 1m), correlationId: correlationId)],
            CancellationToken.None);
        await store.AppendProcessManagerEventsAsync(pmStream, 0,
            [BuildPmEnvelope(pmStream, 1, new TestPmPayload(1), correlationId: correlationId)],
            CancellationToken.None);

        var trace = await reader.ReadTraceAsync(correlationId, 100, CancellationToken.None);

        // One read, both families. The aggregate feeds exclude pm- streams (ADR 0013); the trace does not.
        trace.Rows.Select(r => r.StreamId).Should().Equal(aggregateStream.Value, pmStream.Value);
        trace.Rows.Select(r => r.EventType).Should().Equal(nameof(TestPayload), nameof(TestPmPayload));
        trace.Rows.Should().AllSatisfy(r => r.Metadata.CorrelationId.Should().Be(correlationId));
    }

    [Fact]
    public async Task A_trace_spans_tenants_and_each_row_carries_its_own_tenant()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = CreateStore(dataSource);
        var reader = CreateReader(dataSource);
        var correlationId = Guid.NewGuid();
        var defaultTenantStream = NewStreamId();
        var otherTenantStream = NewStreamId();

        await store.AppendAsync(defaultTenantStream, 0,
            [BuildEnvelope(defaultTenantStream, 1, new TestPayload(Guid.NewGuid(), 1m),
                correlationId: correlationId)],
            CancellationToken.None);
        await store.AppendAsync(otherTenantStream, 0,
            [BuildEnvelope(otherTenantStream, 1, new TestPayload(Guid.NewGuid(), 2m),
                correlationId: correlationId, tenant: OtherTenant)],
            CancellationToken.None);

        var trace = await reader.ReadTraceAsync(correlationId, 100, CancellationToken.None);

        // A chain crossing tenants is the isolation defect the Tracer exists to reveal, so the read
        // carries no tenant predicate and each row reports the tenant it was written under.
        trace.Rows.Select(r => r.Metadata.Tenant).Should().Equal(WellKnownTenants.Default, OtherTenant);
    }

    [Fact]
    public async Task A_trace_over_the_cap_returns_the_earliest_rows_and_flags_truncation()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = CreateStore(dataSource);
        var reader = CreateReader(dataSource);
        var correlationId = Guid.NewGuid();
        var streamId = NewStreamId();
        await store.AppendAsync(streamId, 0,
            [
                BuildEnvelope(streamId, 1, new TestPayload(Guid.NewGuid(), 1m), correlationId: correlationId),
                BuildEnvelope(streamId, 2, new TestPayload(Guid.NewGuid(), 2m), correlationId: correlationId),
                BuildEnvelope(streamId, 3, new TestPayload(Guid.NewGuid(), 3m), correlationId: correlationId),
                BuildEnvelope(streamId, 4, new TestPayload(Guid.NewGuid(), 4m), correlationId: correlationId),
                BuildEnvelope(streamId, 5, new TestPayload(Guid.NewGuid(), 5m), correlationId: correlationId),
            ],
            CancellationToken.None);

        var trace = await reader.ReadTraceAsync(correlationId, 3, CancellationToken.None);

        // Fresh database, IDENTITY starts at 1, nothing else appended. The cap keeps the oldest rows.
        trace.Rows.Should().HaveCount(3);
        trace.Rows.Select(r => r.GlobalPosition).Should().Equal(1, 2, 3);
        trace.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task A_trace_under_the_cap_returns_every_row_and_flags_no_truncation()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = CreateStore(dataSource);
        var reader = CreateReader(dataSource);
        var correlationId = Guid.NewGuid();
        var streamId = NewStreamId();
        await store.AppendAsync(streamId, 0,
            [
                BuildEnvelope(streamId, 1, new TestPayload(Guid.NewGuid(), 1m), correlationId: correlationId),
                BuildEnvelope(streamId, 2, new TestPayload(Guid.NewGuid(), 2m), correlationId: correlationId),
            ],
            CancellationToken.None);

        var trace = await reader.ReadTraceAsync(correlationId, 10, CancellationToken.None);

        trace.Rows.Should().HaveCount(2);
        trace.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task A_trace_at_exactly_the_cap_flags_no_truncation()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = CreateStore(dataSource);
        var reader = CreateReader(dataSource);
        var correlationId = Guid.NewGuid();
        var streamId = NewStreamId();
        await store.AppendAsync(streamId, 0,
            [
                BuildEnvelope(streamId, 1, new TestPayload(Guid.NewGuid(), 1m), correlationId: correlationId),
                BuildEnvelope(streamId, 2, new TestPayload(Guid.NewGuid(), 2m), correlationId: correlationId),
            ],
            CancellationToken.None);

        var trace = await reader.ReadTraceAsync(correlationId, 2, CancellationToken.None);

        // The boundary the fetch-cap-plus-one shape exists to get right: a trace filling the cap exactly
        // is complete, and the extra row the query asks for comes back empty.
        trace.Rows.Should().HaveCount(2);
        trace.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task A_trace_of_an_unmatched_correlation_returns_no_rows()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = CreateStore(dataSource);
        var reader = CreateReader(dataSource);
        var streamId = NewStreamId();
        await store.AppendAsync(streamId, 0,
            [BuildEnvelope(streamId, 1, new TestPayload(Guid.NewGuid(), 1m), correlationId: Guid.NewGuid())],
            CancellationToken.None);

        var trace = await reader.ReadTraceAsync(Guid.NewGuid(), 100, CancellationToken.None);

        trace.Rows.Should().BeEmpty();
        trace.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task A_traced_row_carries_the_payload_as_the_json_text_the_store_holds()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = CreateStore(dataSource);
        var reader = CreateReader(dataSource);
        var correlationId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var streamId = NewStreamId();
        await store.AppendAsync(streamId, 0,
            [BuildEnvelope(streamId, 1, new TestPayload(orderId, 42.50m), correlationId: correlationId)],
            CancellationToken.None);

        var trace = await reader.ReadTraceAsync(correlationId, 100, CancellationToken.None);

        // The stored form, snake_case and all, survives without a CLR round-trip. No registry resolved
        // TestPayload on the way out, and the reader never had to know the type existed.
        //
        // Asserted on content rather than on an exact string: payload is JSONB, a parsed binary form, so
        // what comes back is PostgreSQL's canonical rendering of the document (keys reordered by length,
        // a space after each colon) rather than the bytes JsonSerializer emitted. The naming policy, the
        // values, and the decimal's scale all survive that normalization, which is what this case is for.
        trace.Rows.Should().HaveCount(1);
        trace.Rows[0].PayloadJson.Should().Contain("order_id").And.Contain(orderId.ToString());
        trace.Rows[0].PayloadJson.Should().Contain("total").And.Contain("42.50");
        trace.Rows[0].EventType.Should().Be(nameof(TestPayload));
    }

    private static PostgresEventStore CreateStore(NpgsqlDataSource dataSource)
        => new(new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreatePmRegistry(), CreateJsonOptions(), new EventUpcasterPipeline(CreateRegistry(), []));

    private static PostgresCorrelationTraceReader CreateReader(NpgsqlDataSource dataSource)
        => new(new NpgsqlConnectionFactory(dataSource), CreateJsonOptions());
}
