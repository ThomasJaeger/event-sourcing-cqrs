namespace EventSourcingCqrs.Domain.Abstractions;

// The snapshot seam an aggregate implements: capture its rehydrated state into a memento, and restore
// a pristine aggregate from a memento plus the version the memento was taken at. Pattern from Chapter
// 12: Snapshots. A snapshot is a performance optimization over full replay and never a source of
// truth, so a restore seats the aggregate at the snapshot's version and replays only the tail from
// there. S1 declares the seam; the store that persists and loads snapshots (ISnapshotStore) and the
// repository that uses it land in S2.
public interface ISnapshotSource<TSnapshot>
{
    TSnapshot ToSnapshot();

    void RestoreFrom(TSnapshot snapshot, int version);
}
