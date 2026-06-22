namespace EventSourcingCqrs.Domain.Abstractions;

// The projection roster as identities only: the set of projection checkpoint
// names, without the projection instances. A host that only needs to know which
// projections exist and how far behind each one runs (the operational
// projection-lag read) composes this instead of IEnumerable<IProjection>, so it
// carries no projection store and no serializer. The names are the checkpoint
// keys ICheckpointStore is keyed on. Separate from IProjection by design:
// IProjection keeps its full behavior for the catch-up and replay path, which
// needs the instances, not just their names.
public interface IProjectionRoster
{
    IReadOnlyCollection<string> Names { get; }
}
