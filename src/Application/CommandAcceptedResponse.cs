namespace EventSourcingCqrs.Application;

// The 202 Accepted body for a command the Api host took for asynchronous
// processing. Minimal in v1 (Option B per the setup doc's D4 reshape): the field
// that matters for Phase 7 is when the command was accepted; clients poll the read
// model by the aggregate id they already hold. CorrelationId and a CommandId are
// deferred to their consumers (Phase 8 SignalR binds settlement notifications to a
// correlation id), born-at-consumer at the field level.
public sealed record CommandAcceptedResponse(DateTime AcceptedUtc);
