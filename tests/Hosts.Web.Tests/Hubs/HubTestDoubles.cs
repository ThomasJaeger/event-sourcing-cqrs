using System.Runtime.CompilerServices;
using System.Security.Claims;
using EventSourcingCqrs.Application.SignalR;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.Web.Hubs;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace EventSourcingCqrs.Hosts.Web.Tests.Hubs;

// Captures every group-targeted broadcast the hosted service makes. Dispatch
// only calls Clients.Group(name).SendAsync(...); the other IHubClients targets
// are not exercised and throw to surface any drift.
internal sealed class RecordingHubContext : IHubContext<DashboardHub>
{
    public List<(string Group, string Method, object?[] Args)> Broadcasts { get; } = [];

    public IHubClients Clients => new RecordingClients(this);

    public IGroupManager Groups => throw new NotSupportedException();

    private sealed class RecordingClients(RecordingHubContext context) : IHubClients
    {
        public IClientProxy Group(string groupName) => new RecordingClientProxy(context, groupName);

        public IClientProxy All => throw new NotSupportedException();
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Client(string connectionId) => throw new NotSupportedException();
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
        public IClientProxy User(string userId) => throw new NotSupportedException();
        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }

    private sealed class RecordingClientProxy(RecordingHubContext context, string group) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            context.Broadcasts.Add((group, method, args));
            return Task.CompletedTask;
        }
    }
}

// Yields a fixed sequence of envelopes then parks until the stopping token
// cancels, mirroring the real backplane's open-ended subscription.
internal sealed class StubBackplane(params NotificationEnvelope[] envelopes) : IHubBackplaneConnection
{
    public async IAsyncEnumerable<NotificationEnvelope> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var envelope in envelopes)
        {
            yield return envelope;
        }

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

internal sealed class RecordingGroupManager : IGroupManager
{
    public List<(string Action, string ConnectionId, string Group)> Calls { get; } = [];

    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        Calls.Add(("add", connectionId, groupName));
        return Task.CompletedTask;
    }

    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        Calls.Add(("remove", connectionId, groupName));
        return Task.CompletedTask;
    }
}

internal sealed class FakeHubCallerContext(string connectionId) : HubCallerContext
{
    public override string ConnectionId { get; } = connectionId;
    public override string? UserIdentifier => null;
    public override ClaimsPrincipal? User => null;
    public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
    public override IFeatureCollection Features => throw new NotSupportedException();
    public override CancellationToken ConnectionAborted => CancellationToken.None;
    public override void Abort() => throw new NotSupportedException();
}

internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception), exception));
}
