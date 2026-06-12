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

    // P11.11: an elapsed HttpClient.Timeout surfaces from the snapshot query as TaskCanceledException
    // wrapping TimeoutException. That shape is infrastructure failure, not teardown, so it must fault
    // StartAsync as TimeoutException for the page's Exception arm to read NotLive (the ADR 0035 residual).
    [Fact]
    public async Task A_snapshot_timeout_faults_StartAsync_as_TimeoutException()
    {
        await using var dispatcher = NewDispatcher();
        var applied = new ConcurrentQueue<int>();
        var sendTimeout = new TaskCanceledException("timeout", new TimeoutException());
        await using var subscription = new CircuitResourceSubscription(
            dispatcher, StubSubscriptionAuthorizationClient.Allow(TenantGuid), new StubCircuitIdentity(Actor));

        var start = () => subscription.StartAsync(
            SubscriptionResourceType.Order, "order-1",
            _ => Task.FromException<int>(sendTimeout),
            value =>
            {
                applied.Enqueue(value);
                return Task.CompletedTask;
            },
            async render => await render(),
            CancellationToken.None);

        var thrown = (await start.Should().ThrowAsync<TimeoutException>()).Which;
        thrown.InnerException.Should().BeSameAs(sendTimeout);
        applied.Should().BeEmpty();
    }

    // P11.11 (green on write): the registration precedes the snapshot and a faulted snapshot must not
    // unwind it. The arm can fail while the dispatcher leg keeps working; a later delivery re-queries
    // and applies.
    [Fact]
    public async Task A_snapshot_timeout_leaves_the_registration_armed_so_a_later_delivery_applies()
    {
        await using var dispatcher = NewDispatcher();
        var applied = new ConcurrentQueue<int>();
        var calls = 0;
        await using var subscription = new CircuitResourceSubscription(
            dispatcher, StubSubscriptionAuthorizationClient.Allow(TenantGuid), new StubCircuitIdentity(Actor));

        Func<CancellationToken, Task<int>> query = _ =>
            Interlocked.Increment(ref calls) == 1
                ? Task.FromException<int>(new TaskCanceledException("timeout", new TimeoutException()))
                : Task.FromResult(42);

        try
        {
            await subscription.StartAsync(
                SubscriptionResourceType.Order, "order-1",
                query,
                value =>
                {
                    applied.Enqueue(value);
                    return Task.CompletedTask;
                },
                async render => await render(),
                CancellationToken.None);
        }
        catch (TimeoutException)
        {
            // The faulted arm is A_snapshot_timeout_faults_StartAsync_as_TimeoutException's subject.
        }

        dispatcher.Publish(OrderEnvelope());

        await WaitUntilAsync(() => applied.Contains(42), TimeSpan.FromSeconds(2));
        applied.Should().Equal(42);
    }

    // P11.11 (green on write): teardown cancels the component's own token while the snapshot is in
    // flight. That cancellation stays quiet; the repair must not turn disposal into a thrown timeout.
    [Fact]
    public async Task A_teardown_cancellation_during_the_snapshot_still_returns_quietly()
    {
        await using var dispatcher = NewDispatcher();
        var applied = new ConcurrentQueue<int>();
        var queryEntered = new TaskCompletionSource();
        var releaseQuery = new TaskCompletionSource();
        var subscription = new CircuitResourceSubscription(
            dispatcher, StubSubscriptionAuthorizationClient.Allow(TenantGuid), new StubCircuitIdentity(Actor));

        Func<CancellationToken, Task<int>> query = async ct =>
        {
            queryEntered.SetResult();
            await releaseQuery.Task.WaitAsync(ct); // throws when DisposeAsync cancels the component token
            return 1;
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

        await queryEntered.Task;
        await subscription.DisposeAsync(); // cancels the in-flight snapshot mid-arm

        var awaitingStart = async () => await start;
        await awaitingStart.Should().NotThrowAsync();
        applied.Should().BeEmpty();
    }

    // P11.11: a TaskCanceledException without a TimeoutException inner is genuine cancellation, not a
    // timeout. The translation must not widen to rewrap it; the same instance propagates unchanged.
    [Fact]
    public async Task A_bare_cancellation_from_the_query_propagates_unchanged()
    {
        await using var dispatcher = NewDispatcher();
        var cancellation = new TaskCanceledException("external");
        await using var subscription = new CircuitResourceSubscription(
            dispatcher, StubSubscriptionAuthorizationClient.Allow(TenantGuid), new StubCircuitIdentity(Actor));

        var start = () => subscription.StartAsync(
            SubscriptionResourceType.Order, "order-1",
            _ => Task.FromException<int>(cancellation),
            _ => Task.CompletedTask,
            async render => await render(),
            CancellationToken.None);

        var thrown = (await start.Should().ThrowAsync<TaskCanceledException>()).Which;
        thrown.Should().BeSameAs(cancellation);
    }

    // P11.11 (green on write): pins the notification leg. A timed-out delivery applies nothing and its
    // throw is isolated on the dispatcher's drain loop, so the subscriber survives and the next delivery
    // re-queries and applies.
    [Fact]
    public async Task A_push_delivery_timeout_is_isolated_and_a_following_delivery_still_applies()
    {
        await using var dispatcher = NewDispatcher();
        var applied = new ConcurrentQueue<int>();
        var calls = 0;
        await using var subscription = new CircuitResourceSubscription(
            dispatcher, StubSubscriptionAuthorizationClient.Allow(TenantGuid), new StubCircuitIdentity(Actor));

        Func<CancellationToken, Task<int>> query = _ => Interlocked.Increment(ref calls) switch
        {
            1 => Task.FromResult(1),
            2 => Task.FromException<int>(new TaskCanceledException("timeout", new TimeoutException())),
            _ => Task.FromResult(3),
        };

        await subscription.StartAsync(
            SubscriptionResourceType.Order, "order-1",
            query,
            value =>
            {
                applied.Enqueue(value);
                return Task.CompletedTask;
            },
            async render => await render(),
            CancellationToken.None);
        applied.Should().Equal(1); // the snapshot

        dispatcher.Publish(OrderEnvelope());
        await WaitUntilAsync(() => Volatile.Read(ref calls) >= 2, TimeSpan.FromSeconds(2)); // the timed-out delivery ran

        dispatcher.Publish(OrderEnvelope());
        await WaitUntilAsync(() => applied.Contains(3), TimeSpan.FromSeconds(2));
        await Task.Delay(100); // an apply from the timed-out delivery would land here
        applied.Should().Equal(1, 3);
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
