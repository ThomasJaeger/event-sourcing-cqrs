namespace EventSourcingCqrs.Domain.Abstractions;

// One correlation trace: the rows carrying the correlation id, oldest first, and
// whether the store held more of them than the caller's cap allowed back.
//
// Truncated is true when the read found at least one row beyond maxRows. The rows
// it returns are the earliest by global_position, so a truncated trace shows where
// the workflow began and states that it runs on past the cap.
public sealed record CorrelationTraceResult(
    IReadOnlyList<CorrelationTraceRow> Rows,
    bool Truncated);
