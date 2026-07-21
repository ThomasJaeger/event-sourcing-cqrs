namespace EventSourcingCqrs.Domain.Abstractions;

// The result of loading a snapshot: the deserialized memento and the aggregate version it was captured
// at. A load returns null rather than this when no snapshot exists for the stream, or the stored one
// carries a schema version the caller did not ask for. Pattern from Chapter 12: Snapshots.
public sealed record SnapshotEnvelope<TSnapshot>(TSnapshot Snapshot, int StreamVersion);
