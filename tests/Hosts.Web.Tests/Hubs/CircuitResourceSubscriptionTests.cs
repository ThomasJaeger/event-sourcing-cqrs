using System.Collections.Concurrent;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.Web.Authentication;
using EventSourcingCqrs.Hosts.Web.Hubs;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Hubs;

// Commit 1, ADR 0032. The circuit-scoped subscription is the page's handle onto the in-process dispatcher.
// It authorizes the subscribe through the same Api gate the hub used, registers under the authoritative
// tenant (never a client-supplied one), takes an initial snapshot after registering, and runs every
// snapshot and notification through one issue-token guard so a late re-query cannot clobber a fresher one.
// Delivery to the page is marshalled onto the render thread, so the page never needs an idempotency guard.
public class CircuitResourceSubscriptionTests
{
    private static readonly Guid Actor = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantGuid = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly TenantId Tenant = TenantId.From(TenantGuid);

    private static ResourceNotificationDispatcher NewDispatcher() =>
        new(new RecordingLogger<ResourceNotificationDispatcher>());

    private static NotificationEnvelope OrderEnvelope() =>
        new("order-detail", "order-1", "OrderShipped", ["status"], Tenant);

    [Fact]
    public async Task StartAsync_authorizes_then_registers_and_takes_an_initial_snapshot()
    {
        await using var dispatcher = NewDispatcher();
        var authorizationClient = StubSubscriptionAuthorizationClient.Allow(TenantGuid);
        var applied = new ConcurrentQueue<int>();
        await using var subscription = new CircuitResourceSubscription(
            dispatcher, authorizationClient, new StubCircuitIdentity(Actor));

        await subscription.StartAsync(
            SubscriptionResourceType.Order, "order-1",
            _ => Task.FromResult(7),
            value =>
            {
                applied.Enqueue(value);
                return Task.CompletedTask;
            },
            async render => await render(),
            CancellationToken.None);

        authorizationClient.Calls.Should().ContainSingle();
        authorizationClient.Calls[0].ActorId.Should().Be(Actor);
        authorizationClient.Calls[0].Request.Should().Be(
            new SubscriptionAuthorizationRequest(SubscriptionResourceType.Order, "order-1"));
        applied.Should().Equal(7); // the initial snapshot applied once, after registering
    }

    [Fact]
    public async Task A_denied_authorize_throws_and_registers_nothing()
    {
        await using var dispatcher = NewDispatcher();
        var applied = new ConcurrentQueue<int>();
        await using var subscription = new CircuitResourceSubscription(
            dispatcher, StubSubscriptionAuthorizationClient.Deny(), new StubCircuitIdentity(Actor));

        var start = () => subscription.StartAsync(
            SubscriptionResourceType.Order, "order-1",
            _ => Task.FromResult(1),
            value =>
            {
                applied.Enqueue(value);
                return Task.CompletedTask;
            },
            async render => await render(),
            CancellationToken.None);

        await start.Should().ThrowAsync<ResourceSubscriptionDeniedException>();
        dispatcher.Publish(OrderEnvelope());
        await Task.Delay(100); // a delivery, if the denial had wrongly registered, would land here
        applied.Should().BeEmpty();
    }

    [Fact]
    public async Task An_allow_without_a_tenant_throws_and_registers_nothing()
    {
        await using var dispatcher = NewDispatcher();
        var applied = new ConcurrentQueue<int>();
        var allowWithoutTenant = StubSubscriptionAuthorizationClient.WithResult(
            new SubscriptionAuthorizationResult(true, null));
        await using var subscription = new CircuitResourceSubscription(
            dispatcher, allowWithoutTenant, new StubCircuitIdentity(Actor));

        var start = () => subscription.StartAsync(
            SubscriptionResourceType.Order, "order-1",
            _ => Task.FromResult(1),
            value =>
            {
                applied.Enqueue(value);
                return Task.CompletedTask;
            },
            async render => await render(),
            CancellationToken.None);

        await start.Should().ThrowAsync<ResourceSubscriptionDeniedException>();
        dispatcher.Publish(OrderEnvelope());
        await Task.Delay(100);
        applied.Should().BeEmpty();
    }

    [Fact]
    public async Task One_notification_triggers_exactly_one_marshalled_apply()
    {
        await using var dispatcher = NewDispatcher();
        var applied = new ConcurrentQueue<int>();
        var value = 0;
        var marshalCount = 0;
        await using var subscription = new CircuitResourceSubscription(
            dispatcher, StubSubscriptionAuthorizationClient.Allow(TenantGuid), new StubCircuitIdentity(Actor));

        await subscription.StartAsync(
            SubscriptionResourceType.Order, "order-1",
            _ => Task.FromResult(Interlocked.Increment(ref value)),
            v =>
            {
                applied.Enqueue(v);
                return Task.CompletedTask;
            },
            async render =>
            {
                Interlocked.Increment(ref marshalCount);
                await render();
            },
            CancellationToken.None);

        applied.Should().Equal(1); // snapshot
        var marshalsAfterSnapshot = marshalCount;

        dispatcher.Publish(OrderEnvelope());

        await WaitUntilAsync(() => applied.Count == 2, TimeSpan.FromSeconds(2));
        await Task.Delay(100); // a second (duplicate) delivery would land here
        applied.Should().Equal(1, 2);
        (marshalCount - marshalsAfterSnapshot).Should().Be(1);
    }

    [Fact]
    public async Task A_late_snapshot_does_not_clobber_a_fresher_notification_result()
    {
        await using var dispatcher = NewDispatcher();
        var applied = new ConcurrentQueue<string>();
        var releaseSnapshot = new TaskCompletionSource();
        var callCount = 0;
        await using var subscription = new CircuitResourceSubscription(
            dispatcher, StubSubscriptionAuthorizationClient.Allow(TenantGuid), new StubCircuitIdentity(Actor));

        Func<CancellationToken, Task<string>> query = async _ =>
        {
            var call = Interlocked.Increment(ref callCount);
            if (call == 1)
            {
                await releaseSnapshot.Task; // the snapshot completes last, but was issued first
                return "stale-snapshot";
            }
            return "fresh-notification";
        };

        var start = subscription.StartAsync(
            SubscriptionResourceType.Order, "order-1",
            query,
            value =>
            {
                applied.Enqueue(value);
                return Task.CompletedTask;
            },
            async render => await render(),
            CancellationToken.None);

        // The snapshot (issued first) is in flight and blocked; a notification re-queries and applies.
        dispatcher.Publish(OrderEnvelope());
        await WaitUntilAsync(() => applied.Contains("fresh-notification"), TimeSpan.FromSeconds(2));

        releaseSnapshot.SetResult();
        await start;
        await Task.Delay(100);
        applied.Should().Equal("fresh-notification"); // the older-issued snapshot result was discarded
    }

    [Fact]
    public async Task Apply_runs_only_through_the_marshaller()
    {
        await using var dispatcher = NewDispatcher();
        var appliesOutsideMarshal = 0;
        var applyCount = 0;
        var insideMarshal = false;
        await using var subscription = new CircuitResourceSubscription(
            dispatcher, StubSubscriptionAuthorizationClient.Allow(TenantGuid), new StubCircuitIdentity(Actor));

        await subscription.StartAsync(
            SubscriptionResourceType.Order, "order-1",
            _ => Task.FromResult(0),
            _ =>
            {
                if (!insideMarshal)
                {
                    Interlocked.Increment(ref appliesOutsideMarshal);
                }
                Interlocked.Increment(ref applyCount);
                return Task.CompletedTask;
            },
            async render =>
            {
                insideMarshal = true;
                try
                {
                    await render();
                }
                finally
                {
                    insideMarshal = false;
                }
            },
            CancellationToken.None);

        dispatcher.Publish(OrderEnvelope());
        await WaitUntilAsync(() => applyCount >= 2, TimeSpan.FromSeconds(2));
        appliesOutsideMarshal.Should().Be(0);
    }

    [Fact]
    public async Task Dispose_deregisters_so_a_later_notification_does_not_apply()
    {
        await using var dispatcher = NewDispatcher();
        var applied = new ConcurrentQueue<int>();
        var value = 0;
        var subscription = new CircuitResourceSubscription(
            dispatcher, StubSubscriptionAuthorizationClient.Allow(TenantGuid), new StubCircuitIdentity(Actor));

        await subscription.StartAsync(
            SubscriptionResourceType.Order, "order-1",
            _ => Task.FromResult(Interlocked.Increment(ref value)),
            v =>
            {
                applied.Enqueue(v);
                return Task.CompletedTask;
            },
            async render => await render(),
            CancellationToken.None);
        await WaitUntilAsync(() => applied.Count == 1, TimeSpan.FromSeconds(2));

        await subscription.DisposeAsync();
        dispatcher.Publish(OrderEnvelope());
        await Task.Delay(150); // a delivery, if dispose had not deregistered, would land here
        applied.Should().HaveCount(1);
    }

    [Fact]
    public async Task DisposeAsync_is_idempotent_under_a_double_dispose()
    {
        await using var dispatcher = NewDispatcher();
        var subscription = new CircuitResourceSubscription(
            dispatcher, StubSubscriptionAuthorizationClient.Allow(TenantGuid), new StubCircuitIdentity(Actor));
        await subscription.StartAsync(
            SubscriptionResourceType.Order, "order-1",
            _ => Task.FromResult(0),
            _ => Task.CompletedTask,
            async render => await render(),
            CancellationToken.None);

        // The page disposes the subscription from its DisposeAsync and DI disposes it again at scope end, so
        // the second pass must be a no-op (the idempotency guard), not a re-cancel of a disposed CTS.
        await subscription.DisposeAsync();
        var secondDispose = async () => await subscription.DisposeAsync();

        await secondDispose.Should().NotThrowAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException($"Condition was not met within {timeout}.");
    }

    private sealed class StubCircuitIdentity(Guid actorId) : ICircuitForwardedIdentityProvider
    {
        public Task<Guid> GetActorIdAsync() => Task.FromResult(actorId);
    }
}
