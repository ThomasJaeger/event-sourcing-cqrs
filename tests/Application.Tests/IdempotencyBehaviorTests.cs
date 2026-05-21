using EventSourcingCqrs.Application.Pipelines;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests;

public sealed class IdempotencyBehaviorTests
{
    [Fact]
    public async Task Null_key_passes_through_without_touching_the_store()
    {
        var store = new StubIdempotencyStore();
        var behavior = BuildBehavior(store, idempotencyKey: null);
        var handlerRan = false;

        await behavior.HandleAsync(
            new DoThing(), () => { handlerRan = true; return Task.CompletedTask; }, CancellationToken.None);

        handlerRan.Should().BeTrue();
        store.ExistsCalls.Should().Be(0);
        store.Recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task Blank_key_is_treated_as_no_dedup_requested()
    {
        // The behavior treats a whitespace key as "dedup not requested" and
        // passes through, so it never hands a blank key to the store.
        // PostgresIdempotencyStore rejects a blank key for callers that reach it
        // directly (the delay queue, ADR 0017); the two policies are deliberate.
        var store = new StubIdempotencyStore();
        var behavior = BuildBehavior(store, idempotencyKey: "   ");
        var handlerRan = false;

        await behavior.HandleAsync(
            new DoThing(), () => { handlerRan = true; return Task.CompletedTask; }, CancellationToken.None);

        handlerRan.Should().BeTrue();
        store.ExistsCalls.Should().Be(0);
        store.Recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task Unseen_key_runs_the_handler_then_records_the_key()
    {
        var store = new StubIdempotencyStore { ExistsResult = false };
        var behavior = BuildBehavior(store, idempotencyKey: "key-1");
        var handlerRan = false;

        await behavior.HandleAsync(
            new DoThing(), () => { handlerRan = true; return Task.CompletedTask; }, CancellationToken.None);

        handlerRan.Should().BeTrue();
        store.Recorded.Should().ContainSingle();
        store.Recorded[0].Should().Be(("key-1", nameof(DoThing)));
    }

    [Fact]
    public async Task Seen_key_short_circuits_without_running_the_handler_or_recording()
    {
        var store = new StubIdempotencyStore { ExistsResult = true };
        var behavior = BuildBehavior(store, idempotencyKey: "key-1");
        var handlerRan = false;

        await behavior.HandleAsync(
            new DoThing(), () => { handlerRan = true; return Task.CompletedTask; }, CancellationToken.None);

        handlerRan.Should().BeFalse();
        store.Recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task Handler_failure_does_not_record_the_key_so_a_retry_is_allowed()
    {
        var store = new StubIdempotencyStore { ExistsResult = false };
        var behavior = BuildBehavior(store, idempotencyKey: "key-1");

        var act = async () => await behavior.HandleAsync(
            new DoThing(),
            () => throw new InvalidOperationException("handler failed"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        store.Recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task Record_race_loss_does_not_throw()
    {
        // TryRecordAsync returning false is the lazy-fallback race; the behavior
        // completes normally and leaves the double-apply to the event store's
        // version constraint (ADR 0016).
        var store = new StubIdempotencyStore { ExistsResult = false, TryRecordResult = false };
        var behavior = BuildBehavior(store, idempotencyKey: "key-1");
        var handlerRan = false;

        await behavior.HandleAsync(
            new DoThing(), () => { handlerRan = true; return Task.CompletedTask; }, CancellationToken.None);

        handlerRan.Should().BeTrue();
        store.Recorded.Should().ContainSingle();
    }

    private static IdempotencyBehavior<DoThing> BuildBehavior(
        IIdempotencyStore store, string? idempotencyKey)
    {
        var accessor = new StubCommandContextAccessor
        {
            Current = new StubCommandContext { IdempotencyKey = idempotencyKey }
        };
        return new IdempotencyBehavior<DoThing>(store, accessor);
    }

    private sealed record DoThing : ICommand;

    private sealed class StubIdempotencyStore : IIdempotencyStore
    {
        public bool ExistsResult { get; init; }
        public bool TryRecordResult { get; init; } = true;
        public int ExistsCalls { get; private set; }
        public List<(string Key, string CommandType)> Recorded { get; } = new();

        public Task<bool> ExistsAsync(string idempotencyKey, CancellationToken ct)
        {
            ExistsCalls++;
            return Task.FromResult(ExistsResult);
        }

        public Task<bool> TryRecordAsync(string idempotencyKey, string commandType, CancellationToken ct)
        {
            Recorded.Add((idempotencyKey, commandType));
            return Task.FromResult(TryRecordResult);
        }
    }
}
