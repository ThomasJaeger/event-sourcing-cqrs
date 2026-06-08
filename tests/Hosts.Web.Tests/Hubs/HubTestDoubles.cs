using System.Runtime.CompilerServices;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.SignalR;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.Web.Hubs;
using Microsoft.Extensions.Logging;

namespace EventSourcingCqrs.Hosts.Web.Tests.Hubs;

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
// assert both that the subscription consulted the client and what it asked. A valid allow requires the
// authorized tenant, mirroring the production contract: Allow takes a tenant, Deny takes none, so an allow
// with no tenant is unconstructable through the normal factories. The one intentional malformed allow (the
// fail-closed guard test) goes through WithResult.
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

// Records every envelope fed to the in-process dispatcher and hands out a no-op subscription. Used to
// assert the backplane reader's feed.
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
