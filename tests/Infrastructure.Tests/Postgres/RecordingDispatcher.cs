using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

// Hand-rolled IMessageDispatcher test double. Records every message it
// receives. Evaluate is the per-message hook tests use to either fail the
// dispatch (return a non-null Exception) or block on a TaskCompletionSource
// for concurrency tests. Default evaluator succeeds silently.
internal sealed class RecordingDispatcher : IMessageDispatcher
{
    public List<OutboxMessage> Received { get; } = new();

    public Func<OutboxMessage, CancellationToken, Task<Exception?>>? Evaluate { get; set; }

    public async Task DispatchAsync(OutboxMessage message, CancellationToken ct)
    {
        Received.Add(message);
        if (Evaluate is null)
        {
            return;
        }
        var ex = await Evaluate(message, ct);
        if (ex is not null)
        {
            throw ex;
        }
    }
}
