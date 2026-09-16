using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application;

internal static class CommittedCommandGuard
{
    public static async Task CheckAsync(IEventStore store, StreamId stream, ICommandContext? context, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(context?.IdempotencyKey)
            && await store.HasCommittedCommandAsync(stream, context.IdempotencyKey, ct))
            throw new CommandAlreadyCommittedException();
    }

    public static void Check(IEnumerable<EventMetadata> history, ICommandContext? context)
    {
        if (!string.IsNullOrWhiteSpace(context?.IdempotencyKey)
            && history.Any(e => e.IdempotencyKey == context.IdempotencyKey))
            throw new CommandAlreadyCommittedException();
    }
}
