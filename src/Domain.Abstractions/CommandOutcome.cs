namespace EventSourcingCqrs.Domain.Abstractions;

// The result of a process-manager command dispatch through TrySendAsync (ADR
// 0015). A handler switches on this rather than catching exceptions inline, so
// a parallel fan-out can collect one outcome per dispatch and decide
// compensation over the full set. The shape stays minimal: a success flag plus
// the captured failure. Richer failure categorisation waits until a process
// manager has differentiated handling that needs it.
public sealed record CommandOutcome
{
    public bool IsSuccess { get; init; }
    public Exception? Failure { get; init; }

    public static CommandOutcome Success() => new() { IsSuccess = true };
    public static CommandOutcome Failed(Exception failure) => new() { IsSuccess = false, Failure = failure };
}
