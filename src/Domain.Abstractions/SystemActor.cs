namespace EventSourcingCqrs.Domain.Abstractions;

// Identity for a command originator that is not a human principal: a process
// manager, the outbox processor, a replay tool. Bundles the Guid that lands in
// EventMetadata.ActorId with the human-readable ServiceName that lands in
// EventMetadata.Source, so a caller names the actor once and the bus stamps
// both. SystemActors holds the constants (ADR 0014).
public sealed record SystemActor(Guid Id, string ServiceName);
