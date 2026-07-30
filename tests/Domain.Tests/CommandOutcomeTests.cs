using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Domain.Tests;

public class CommandOutcomeTests
{
    private static readonly SystemActor Actor = SystemActors.OrderFulfillment;

    private static EventMetadata CausingEvent()
        => new(
            EventId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.NewGuid(),
            Source: "Sales",
            OccurredUtc: new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc),
            Tenant: WellKnownTenants.Default);

    [Fact]
    public void Success_sets_the_flag_and_leaves_failure_null()
    {
        var outcome = CommandOutcome.Success();

        outcome.IsSuccess.Should().BeTrue();
        outcome.Failure.Should().BeNull();
    }

    [Fact]
    public void Failed_captures_the_exception_and_clears_the_flag()
    {
        var failure = new InvalidOperationException("boom");

        var outcome = CommandOutcome.Failed(failure);

        outcome.IsSuccess.Should().BeFalse();
        outcome.Failure.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task TrySendAsync_returns_success_when_the_dispatch_completes()
    {
        var bus = new StubCausedCommandBus(_ => Task.CompletedTask);

        var outcome = await bus.TrySendAsync(
            new TestCommand(), CausingEvent(), Actor, idempotencyKey: null, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Failure.Should().BeNull();
    }

    [Fact]
    public async Task TrySendAsync_converts_a_DomainException_to_a_failed_outcome()
    {
        var failure = new DomainException("insufficient stock");
        var bus = new StubCausedCommandBus(_ => throw failure);

        var outcome = await bus.TrySendAsync(
            new TestCommand(), CausingEvent(), Actor, idempotencyKey: null, CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.Failure.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task TrySendAsync_converts_a_ConcurrencyException_to_a_failed_outcome()
    {
        var failure = new ConcurrencyException(StreamId.Parse($"order:{Guid.NewGuid():N}"), 0);
        var bus = new StubCausedCommandBus(_ => throw failure);

        var outcome = await bus.TrySendAsync(
            new TestCommand(), CausingEvent(), Actor, idempotencyKey: null, CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.Failure.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task TrySendAsync_propagates_an_unexpected_exception()
    {
        var bus = new StubCausedCommandBus(_ => throw new InvalidOperationException("unexpected"));

        var act = async () => await bus.TrySendAsync(
            new TestCommand(), CausingEvent(), Actor, idempotencyKey: null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task TrySendAsync_propagates_cancellation_rather_than_swallowing_it()
    {
        // Cancellation is neither DomainException nor ConcurrencyException, so it
        // is a fault that must surface, not an outcome the try/catch absorbs.
        var bus = new StubCausedCommandBus(ct =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await bus.TrySendAsync(
            new TestCommand(), CausingEvent(), Actor, idempotencyKey: null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed record TestCommand : ICommand;

    private sealed class StubCausedCommandBus : ICausedCommandBus
    {
        private readonly Func<CancellationToken, Task> _behavior;

        public StubCausedCommandBus(Func<CancellationToken, Task> behavior) => _behavior = behavior;

        public Task SendAsync(
            ICommand command,
            EventMetadata causingEventMetadata,
            SystemActor actor,
            string? idempotencyKey,
            CancellationToken ct)
            => _behavior(ct);
    }
}
