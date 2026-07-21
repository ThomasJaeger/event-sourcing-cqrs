namespace EventSourcingCqrs.Domain.Abstractions;

// The snapshot store: persist an aggregate's memento keyed by stream, and load the latest one for a
// stream at a schema version the caller can consume. Pattern from Chapter 12: Snapshots. Save and Load
// are generic per call rather than the interface being generic, because one store serves every
// aggregate's snapshot type. Load takes the expected schema version and returns null when the stored
// row is a different shape, so a shape change is a discard-and-rebuild rather than an upcast: a
// snapshot is a cache, not history. The returned envelope carries the stream version the snapshot was
// captured at, so the repository replays only the events after it.
public interface ISnapshotStore
{
    Task SaveAsync<TSnapshot>(
        StreamId streamId,
        TSnapshot snapshot,
        int streamVersion,
        int snapshotSchemaVersion,
        CancellationToken ct);

    Task<SnapshotEnvelope<TSnapshot>?> LoadAsync<TSnapshot>(
        StreamId streamId,
        int expectedSchemaVersion,
        CancellationToken ct);
}
