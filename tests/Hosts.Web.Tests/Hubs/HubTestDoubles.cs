using System.Runtime.CompilerServices;
using System.Security.Claims;
using EventSourcingCqrs.Application;
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

internal sealed class FakeHubCallerContext(string connectionId, ClaimsPrincipal? user = null) : HubCallerContext
{
    public override string ConnectionId { get; } = connectionId;
    public override string? UserIdentifier => null;

    // HubCallerContext.User is a get-only abstract property, so the test supplies the principal through
    // the optional constructor argument rather than a setter; it defaults to null, preserving the
    // existing new FakeHubCallerContext("conn-1") call sites.
    public override ClaimsPrincipal? User { get; } = user;
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

// Returns a configured SubscriptionAuthorizationResult and records every authorize call, so a test can
// assert both that the hub consulted the client and what it asked. A valid allow requires the authorized
// tenant, mirroring the production contract: Allow takes a tenant, Deny takes none, so an allow with no
// tenant is unconstructable through the normal factories. The one intentional malformed allow (the
// fail-closed guard test) goes through WithResult. Shared by the per-member subscribe cases and the hub
// authorization tests.
internal sealed class StubSubscriptionAuthorizationClient : ISubscriptionAuthorizationClient
{
    private readonly SubscriptionAuthorizationResult _result;

    private StubSubscriptionAuthorizationClient(SubscriptionAuthorizationResult result) => _result = result;

    public static StubSubscriptionAuthorizationClient Allow(Guid tenant) =>
        new(new SubscriptionAuthorizationResult(true, tenant));

    public static StubSubscriptionAuthorizationClient Deny() =>
        new(new SubscriptionAuthorizationResult(false, null));

    public static StubSubscriptionAuthorizationClient WithResult(SubscriptionAuthorizationResult result) =>
        new(result);

    public List<(Guid ActorId, SubscriptionAuthorizationRequest Request)> Calls { get; } = [];

    public Task<SubscriptionAuthorizationResult> AuthorizeAsync(
        Guid actorId, SubscriptionAuthorizationRequest request, CancellationToken ct)
    {
        Calls.Add((actorId, request));
        return Task.FromResult(_result);
    }
}

// Records every envelope fed to the in-process dispatcher and hands out a no-op subscription. Used no-op
// at the broadcast sites (which never publish to it) and asserted in the dual-sink feed test.
internal sealed class RecordingResourceNotificationDispatcher : IResourceNotificationDispatcher
{
    public List<NotificationEnvelope> Published { get; } = [];

    public IDisposable Subscribe(ResourceKey key, Func<NotificationEnvelope, Task> onNotified)
        => new NoOpRegistration();

    public void Publish(NotificationEnvelope envelope) => Published.Add(envelope);

    private sealed class NoOpRegistration : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
