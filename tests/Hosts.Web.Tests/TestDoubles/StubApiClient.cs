using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.Web;

namespace EventSourcingCqrs.Hosts.Web.Tests.TestDoubles;

/// <summary>
/// Hand-rolled IApiClient stub for component tests. Tests seed results by
/// query type or command type via Enqueue; the stub returns seeded results
/// in FIFO order. Captures every invocation for assertion. Throws explicit
/// failures when a test invokes a path the test did not seed, surfacing the
/// test gap at the boundary that owns it rather than returning a silent
/// default. SeedCommandFailure seeds a dispatch failure; a seeded failure for
/// a command type takes precedence over a seeded success result for the same
/// type.
/// </summary>
internal sealed class StubApiClient : IApiClient
{
    private readonly Dictionary<Type, Queue<object?>> queryResults = new();
    private readonly Dictionary<Type, Queue<CommandAcceptedResponse>> commandResults = new();
    private readonly Dictionary<Type, Queue<Exception>> commandFailures = new();
    private readonly Dictionary<Type, Queue<Exception>> queryFailures = new();

    public List<object> CapturedQueries { get; } = new();
    public List<(ICommand Command, string IdempotencyKey)> CapturedCommands { get; } = new();

    public void EnqueueQueryResult<TQuery, TResult>(TResult result)
        where TQuery : IQuery<TResult>
    {
        var key = typeof(TQuery);
        if (!queryResults.TryGetValue(key, out var queue))
        {
            queue = new Queue<object?>();
            queryResults[key] = queue;
        }
        queue.Enqueue(result);
    }

    public void EnqueueCommandResult(Type commandType, CommandAcceptedResponse response)
    {
        if (!commandResults.TryGetValue(commandType, out var queue))
        {
            queue = new Queue<CommandAcceptedResponse>();
            commandResults[commandType] = queue;
        }
        queue.Enqueue(response);
    }

    public void SeedCommandFailure<TCommand>(Exception exception)
        where TCommand : ICommand
    {
        if (!commandFailures.TryGetValue(typeof(TCommand), out var queue))
        {
            queue = new Queue<Exception>();
            commandFailures[typeof(TCommand)] = queue;
        }
        queue.Enqueue(exception);
    }

    // Seeds a query to throw, for the polling-loop transient-failure tests. A
    // seeded query failure takes precedence over a seeded result for the same
    // type, mirroring SeedCommandFailure.
    public void SeedQueryFailure<TQuery>(Exception exception)
    {
        if (!queryFailures.TryGetValue(typeof(TQuery), out var queue))
        {
            queue = new Queue<Exception>();
            queryFailures[typeof(TQuery)] = queue;
        }
        queue.Enqueue(exception);
    }

    public Task<TResult?> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken ct)
    {
        CapturedQueries.Add(query);
        var key = query.GetType();
        if (queryFailures.TryGetValue(key, out var seededFailures) && seededFailures.Count > 0)
        {
            throw seededFailures.Dequeue();
        }
        if (!queryResults.TryGetValue(key, out var queue) || queue.Count == 0)
        {
            throw new InvalidOperationException(
                $"StubApiClient has no seeded result for query type {key.Name}. " +
                $"Call EnqueueQueryResult<{key.Name}, {typeof(TResult).Name}>(...) " +
                $"before the component renders.");
        }
        var result = (TResult?)queue.Dequeue();
        return Task.FromResult(result);
    }

    public Task<CommandAcceptedResponse> SendCommandAsync(
        ICommand command,
        string idempotencyKey,
        CancellationToken ct)
    {
        CapturedCommands.Add((command, idempotencyKey));
        var key = command.GetType();
        if (commandFailures.TryGetValue(key, out var failures) && failures.Count > 0)
        {
            throw failures.Dequeue();
        }
        if (!commandResults.TryGetValue(key, out var queue) || queue.Count == 0)
        {
            throw new InvalidOperationException(
                $"StubApiClient has no seeded result for command type {key.Name}. " +
                $"Call EnqueueCommandResult(typeof({key.Name}), ...) before dispatch.");
        }
        return Task.FromResult(queue.Dequeue());
    }
}
