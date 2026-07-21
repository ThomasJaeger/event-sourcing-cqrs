namespace EventSourcingCqrs.Domain.Abstractions;

// The current schema version of each stored event type, keyed by storage name. The write path stamps
// an appended event with the version it is written at, so a reader resolves the terminal shape
// directly instead of walking a current-shape payload back through an upcaster chain. The upcaster
// pipeline implements this from the same chain topology it resolves reads with, so the version a
// write stamps and the version a read expects can never disagree. Pattern from Chapter 11: Upcasting.
public interface ICurrentEventSchemaVersions
{
    // The version a newly written event of this storage name carries: the number of shapes in its
    // lineage, which is 1 for a type with no upcasters.
    int CurrentVersionFor(string storageName);
}
