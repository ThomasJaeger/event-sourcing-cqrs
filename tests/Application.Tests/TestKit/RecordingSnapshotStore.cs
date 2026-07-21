using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Tests.TestKit;

// A recording ISnapshotStore for the snapshotting-repository facts: it captures every SaveAsync so a
// fact can assert what was captured and at which version, and it honors the schema-version filter on
// LoadAsync exactly as PostgresSnapshotStore does (a stored snapshot at a version the caller did not
// ask for reads as a miss). ISnapshotStore is our own port, so a hand-built double is the right seam
// here rather than a mock of something we do not own. ThrowOnSave lets a fact drive the best-effort
// capture path where the store is down but the append must still land.
internal sealed class RecordingSnapshotStore : ISnapshotStore
{
    private readonly Dictionary<StreamId, StoredSnapshot> _seeds = [];

    public List<SavedSnapshot> Saves { get; } = [];

    public int LoadCallCount { get; private set; }

    public int? LastLoadExpectedSchemaVersion { get; private set; }

    public StreamId? LastLoadStreamId { get; private set; }

    public bool ThrowOnSave { get; set; }

    // Seeds a snapshot the store will return on load only when the caller asks at this exact schema
    // version, so a fact can stage a mismatched-schema row and watch the repository fall back to replay.
    public void Seed<TSnapshot>(StreamId streamId, TSnapshot snapshot, int streamVersion, int schemaVersion)
        => _seeds[streamId] = new StoredSnapshot(snapshot!, streamVersion, schemaVersion);

    public Task SaveAsync<TSnapshot>(
        StreamId streamId,
        TSnapshot snapshot,
        int streamVersion,
        int snapshotSchemaVersion,
        CancellationToken ct)
    {
        if (ThrowOnSave)
        {
            throw new InvalidOperationException("Snapshot store unavailable.");
        }

        Saves.Add(new SavedSnapshot(streamId, snapshot!, streamVersion, snapshotSchemaVersion));
        return Task.CompletedTask;
    }

    public Task<SnapshotEnvelope<TSnapshot>?> LoadAsync<TSnapshot>(
        StreamId streamId,
        int expectedSchemaVersion,
        CancellationToken ct)
    {
        LoadCallCount++;
        LastLoadStreamId = streamId;
        LastLoadExpectedSchemaVersion = expectedSchemaVersion;

        if (_seeds.TryGetValue(streamId, out var seed)
            && seed.SchemaVersion == expectedSchemaVersion
            && seed.Snapshot is TSnapshot typed)
        {
            return Task.FromResult<SnapshotEnvelope<TSnapshot>?>(
                new SnapshotEnvelope<TSnapshot>(typed, seed.StreamVersion));
        }

        return Task.FromResult<SnapshotEnvelope<TSnapshot>?>(null);
    }

    private sealed record StoredSnapshot(object Snapshot, int StreamVersion, int SchemaVersion);

    public sealed record SavedSnapshot(StreamId StreamId, object Snapshot, int StreamVersion, int SchemaVersion);
}
