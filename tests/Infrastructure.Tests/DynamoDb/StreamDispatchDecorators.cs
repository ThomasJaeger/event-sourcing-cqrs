using Amazon.DynamoDBStreams;
using Amazon.DynamoDBStreams.Model;
using Amazon.Runtime;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.Tests.DynamoDb;

// What the dispatch loop did, in the order it did it. The trigger fact needs to know that a
// non-empty GetRecords came before the drain that delivered, which no assertion on the dispatcher
// alone can see.
internal enum DispatchObservation
{
    RecordsReturned,
    RecordsEmpty,
    FeedRead,
    IteratorAcquired,
}

internal sealed class DispatchObservationLog
{
    private readonly object _gate = new();
    private readonly List<DispatchObservation> _observations = [];

    public IReadOnlyList<DispatchObservation> Observations
    {
        get
        {
            lock (_gate)
            {
                return _observations.ToList();
            }
        }
    }

    public void Record(DispatchObservation observation)
    {
        lock (_gate)
        {
            _observations.Add(observation);
        }
    }

    public int Count(DispatchObservation observation)
        => Observations.Count(o => o == observation);
}

// Pass-through over the real Streams client against LocalStack, recording what came back. Every
// call reaches the live engine; nothing here decides an answer. It is a double we own wrapped
// around a seam the harness already injects by hand, not a stand-in for the engine, so the
// TDD_RULES section 3 carve-out is not in play: what the engine does stays the engine's to say.
internal sealed class RecordingStreamsDecorator : AmazonDynamoDBStreamsClient
{
    private readonly IAmazonDynamoDBStreams _inner;
    private readonly DispatchObservationLog _log;

    public RecordingStreamsDecorator(IAmazonDynamoDBStreams inner, DispatchObservationLog log)
        : base(
            new BasicAWSCredentials("recording", "recording"),
            new AmazonDynamoDBStreamsConfig
            {
                ServiceURL = "http://localhost:1",
                AuthenticationRegion = "us-east-1",
            })
    {
        _inner = inner;
        _log = log;
    }

    public override Task<ListStreamsResponse> ListStreamsAsync(
        ListStreamsRequest request, CancellationToken cancellationToken = default)
        => _inner.ListStreamsAsync(request, cancellationToken);

    public override Task<DescribeStreamResponse> DescribeStreamAsync(
        DescribeStreamRequest request, CancellationToken cancellationToken = default)
        => _inner.DescribeStreamAsync(request, cancellationToken);

    public override async Task<GetShardIteratorResponse> GetShardIteratorAsync(
        GetShardIteratorRequest request, CancellationToken cancellationToken = default)
    {
        var iterator = await _inner.GetShardIteratorAsync(request, cancellationToken);
        _log.Record(DispatchObservation.IteratorAcquired);
        return iterator;
    }

    public override async Task<GetRecordsResponse> GetRecordsAsync(
        GetRecordsRequest request, CancellationToken cancellationToken = default)
    {
        var page = await _inner.GetRecordsAsync(request, cancellationToken);
        _log.Record(page.Records.Count > 0
            ? DispatchObservation.RecordsReturned
            : DispatchObservation.RecordsEmpty);
        return page;
    }
}

// Every GetRecords throws, so the wake never fires. Everything else reaches the live engine, which
// is the point: this pins that the wake is load-bearing rather than decorative, by removing only
// the wake.
//
// TDD_RULES section 3's error-path carve-out, condition by condition. It stands in for an error
// path the live engine cannot deterministically produce: LocalStack has no way to fail GetRecords
// on demand while serving every other call. The shape it returns is one the SDK declares and the
// engine really raises under a broken stream. It is named here. It replaces no live-engine fact:
// what a healthy stream does is pinned by the trigger and no-poll facts against LocalStack, both
// below. And the reach for it is surfaced rather than assumed.
internal sealed class FaultingStreamsDecorator : AmazonDynamoDBStreamsClient
{
    private readonly IAmazonDynamoDBStreams _inner;

    public FaultingStreamsDecorator(IAmazonDynamoDBStreams inner)
        : base(
            new BasicAWSCredentials("faulting", "faulting"),
            new AmazonDynamoDBStreamsConfig
            {
                ServiceURL = "http://localhost:1",
                AuthenticationRegion = "us-east-1",
            })
        => _inner = inner;

    public override Task<ListStreamsResponse> ListStreamsAsync(
        ListStreamsRequest request, CancellationToken cancellationToken = default)
        => _inner.ListStreamsAsync(request, cancellationToken);

    public override Task<DescribeStreamResponse> DescribeStreamAsync(
        DescribeStreamRequest request, CancellationToken cancellationToken = default)
        => _inner.DescribeStreamAsync(request, cancellationToken);

    public override Task<GetShardIteratorResponse> GetShardIteratorAsync(
        GetShardIteratorRequest request, CancellationToken cancellationToken = default)
        => _inner.GetShardIteratorAsync(request, cancellationToken);

    public override Task<GetRecordsResponse> GetRecordsAsync(
        GetRecordsRequest request, CancellationToken cancellationToken = default)
        => throw new AmazonDynamoDBStreamsException("The stream is unavailable.");
}

// Counts reads of the event feed, so a fact can say how many drains ran. The drain is the only
// caller of ReadAllAsync in this composition, so one read is one drain pass.
internal sealed class CountingEventStore : IEventStore
{
    private readonly IEventStore _inner;
    private readonly DispatchObservationLog _log;

    public CountingEventStore(IEventStore inner, DispatchObservationLog log)
    {
        _inner = inner;
        _log = log;
    }

    public IAsyncEnumerable<EventEnvelope> ReadAllAsync(long fromPosition, CancellationToken ct = default)
    {
        _log.Record(DispatchObservation.FeedRead);
        return _inner.ReadAllAsync(fromPosition, ct);
    }

    public Task AppendAsync(
        StreamId streamId, int expectedVersion, IReadOnlyList<EventEnvelope> events, CancellationToken ct)
        => _inner.AppendAsync(streamId, expectedVersion, events, ct);

    public Task<IReadOnlyList<EventEnvelope>> ReadStreamAsync(
        StreamId streamId, int fromVersion = 0, CancellationToken ct = default)
        => _inner.ReadStreamAsync(streamId, fromVersion, ct);

    public Task AppendProcessManagerEventsAsync(
        StreamId streamId, int expectedVersion, IReadOnlyList<ProcessManagerEventEnvelope> events,
        CancellationToken ct)
        => _inner.AppendProcessManagerEventsAsync(streamId, expectedVersion, events, ct);

    public Task<IReadOnlyList<ProcessManagerEventEnvelope>> ReadProcessManagerStreamAsync(
        StreamId streamId, int fromVersion = 0, CancellationToken ct = default)
        => _inner.ReadProcessManagerStreamAsync(streamId, fromVersion, ct);

    public IAsyncEnumerable<EventEnvelope> ReadAllForTenantAsync(
        TenantId tenant, long fromPosition, long toPositionInclusive, CancellationToken ct = default)
        => _inner.ReadAllForTenantAsync(tenant, fromPosition, toPositionInclusive, ct);
}
