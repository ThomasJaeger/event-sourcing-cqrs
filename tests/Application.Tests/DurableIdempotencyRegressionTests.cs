using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Application.Pipelines;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests;

public sealed class DurableIdempotencyRegressionTests
{
    [Fact]
    public async Task Oversized_utf8_key_is_rejected_before_any_effect()
    {
        var f = new InventoryTestFixture();
        f.Accessor.Current = new StubCommandContext { IdempotencyKey = new string('é', 101) };
        var behavior = new IdempotencyBehavior<AdjustInventory>(new MissingReceiptCache(), f.Accessor,
            new StubTenantAccessor { Current = WellKnownTenants.Default });
        var executed = false;
        Func<Task> send = () => behavior.HandleAsync(new AdjustInventory(Guid.NewGuid(), 5, "return"),
            () => { executed = true; return Task.CompletedTask; }, default);
        await send.Should().ThrowAsync<ValidationException>();
        executed.Should().BeFalse();
    }

    [Fact]
    public async Task Retry_after_receipt_cache_failure_does_not_adjust_inventory_twice()
    {
        var f = new InventoryTestFixture();
        await f.SeedCreatedAsync();
        f.Accessor.Current = new StubCommandContext { IdempotencyKey = "restock-1" };
        var cache = new MissingReceiptCache { FailRecord = true };
        var behavior = new IdempotencyBehavior<AdjustInventory>(cache, f.Accessor,
            new StubTenantAccessor { Current = WellKnownTenants.Default });
        var handler = new AdjustInventoryHandler(f.Repository, f.Accessor);
        var command = new AdjustInventory(InventoryTestFixture.InventoryId, 5, "return");
        Func<Task> send = () => behavior.HandleAsync(command, () => handler.HandleAsync(command, default), default);
        await send.Should().ThrowAsync<IOException>();
        cache.FailRecord = false;
        await send();
        await send(); // even a permanently empty cache is safe
        f.Accessor.Current = null;
        (await f.LoadAsync())!.TotalAdjusted.Should().Be(5);
    }

    [Fact]
    public async Task Concurrent_stale_copy_cannot_apply_a_committed_key_again()
    {
        var f = new InventoryTestFixture();
        await f.SeedCreatedAsync();
        f.Accessor.Current = new StubCommandContext { IdempotencyKey = "adjust-1" };
        var first = (await f.LoadAsync())!;
        var second = (await f.LoadAsync())!;
        first.Adjust(5, "return", InventoryTestFixture.At);
        second.Adjust(5, "return", InventoryTestFixture.At);
        await f.Repository.SaveAsync(first, default);
        Func<Task> save = () => f.Repository.SaveAsync(second, default);
        await save.Should().ThrowAsync<CommandAlreadyCommittedException>();
        f.Accessor.Current = null;
        (await f.LoadAsync())!.TotalAdjusted.Should().Be(5);
    }

    private sealed class MissingReceiptCache : IIdempotencyStore
    {
        public bool FailRecord { get; set; }
        public Task<bool> ExistsAsync(TenantId tenant, string key, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> TryRecordAsync(TenantId tenant, string key, string commandType, CancellationToken ct)
            => FailRecord ? throw new IOException("Receipt cache unavailable after event commit") : Task.FromResult(false);
    }
}
