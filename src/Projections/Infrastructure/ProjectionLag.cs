namespace EventSourcingCqrs.Projections.Infrastructure;

// One projection's position relative to the head of the global event stream.
// PositionsBehind is HeadPosition minus CheckpointPosition: the number of global
// positions the projection has not yet processed. Positions can have gaps (a
// rolled-back append burns a global_position), so this is a position delta, not a
// literal event count. A never-run projection reads CheckpointPosition 0 and is
// behind by the whole log.
public sealed record ProjectionLag(
    string ProjectionName,
    long HeadPosition,
    long CheckpointPosition,
    long PositionsBehind);
