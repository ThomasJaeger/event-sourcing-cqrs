using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Application.Tests;

// The query bus sets the current-tenant accessor at dispatch, the read-side twin of the command
// bus's tenant set-point. These mirror the QueryBusTests context-restore pattern but capture
// ICurrentTenantAccessor.Current. Pure unit tests against the real AsyncLocalCurrentTenantAccessor,
// so the async-local isolation is exercised rather than stubbed away.
public sealed class QueryBusTenantTests
{
    private static readonly TenantId TenantA =
        TenantId.From(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    private static readonly TenantId TenantB =
        TenantId.From(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
    private static readonly TenantId Sentinel =
        TenantId.From(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"));

    private static readonly IReadOnlyCollection<Role> Roles = [Role.Customer];

    [Fact]
    public async Task AskAsync_with_a_principal_sets_the_current_tenant_the_handler_can_read()
    {
        var accessor = new AsyncLocalCurrentTenantAccessor();
        var handler = new TenantCapturingHandler(accessor);
        var bus = BuildBus(handler, accessor);

        await bus.AskAsync(new ObserveTenant(), Guid.NewGuid(), Roles, TenantA, CancellationToken.None);

        handler.Observed.Should().Be(TenantA);
    }

    [Fact]
    public async Task AskAsync_without_a_principal_sets_the_default_tenant()
    {
        var accessor = new AsyncLocalCurrentTenantAccessor();
        var handler = new TenantCapturingHandler(accessor);
        var bus = BuildBus(handler, accessor);

        await bus.AskAsync(new ObserveTenant(), CancellationToken.None);

        handler.Observed.Should().Be(WellKnownTenants.Default);
    }

    [Fact]
    public async Task AskAsync_restores_the_previous_current_tenant_after_the_handler_completes()
    {
        var accessor = new AsyncLocalCurrentTenantAccessor();
        var handler = new TenantCapturingHandler(accessor);
        var bus = BuildBus(handler, accessor);

        accessor.Current = Sentinel;
        await bus.AskAsync(new ObserveTenant(), Guid.NewGuid(), Roles, TenantA, CancellationToken.None);

        // The bus swaps the dispatch tenant in for the handler and the prior sentinel back out after.
        handler.Observed.Should().Be(TenantA);
        accessor.Current.Should().Be(Sentinel);
    }

    [Fact]
    public async Task AskAsync_isolates_the_current_tenant_across_concurrent_dispatches()
    {
        var accessor = new AsyncLocalCurrentTenantAccessor();
        var release = new TaskCompletionSource();
        var bothEntered = new TaskCompletionSource();
        var handler = new RendezvousTenantHandler(accessor, release, bothEntered);
        var bus = BuildBus(handler, accessor);

        var taskA = Task.Run(async () =>
            await bus.AskAsync(new ObserveTenant(), Guid.NewGuid(), Roles, TenantA, CancellationToken.None));
        var taskB = Task.Run(async () =>
            await bus.AskAsync(new ObserveTenant(), Guid.NewGuid(), Roles, TenantB, CancellationToken.None));

        // Hold both dispatches inside the handler at once, so the accessor carries both tenants across
        // the two async flows concurrently, then let them finish and read their own flow's tenant.
        await bothEntered.Task;
        release.SetResult();
        var results = await Task.WhenAll(taskA, taskB);

        results[0].Should().Be(TenantA);
        results[1].Should().Be(TenantB);
        accessor.Current.Should().BeNull();
    }

    private static QueryBus BuildBus(
        IQueryHandler<ObserveTenant, TenantId?> handler, ICurrentTenantAccessor tenantAccessor)
        => new(new ServiceCollection()
            .AddSingleton<IQueryHandler<ObserveTenant, TenantId?>>(handler)
            .AddSingleton<IQueryContextAccessor, AsyncLocalQueryContextAccessor>()
            .AddSingleton(tenantAccessor)
            .BuildServiceProvider());

    private sealed record ObserveTenant : IQuery<TenantId?>;

    private sealed class TenantCapturingHandler : IQueryHandler<ObserveTenant, TenantId?>
    {
        private readonly ICurrentTenantAccessor _tenantAccessor;

        public TenantCapturingHandler(ICurrentTenantAccessor tenantAccessor)
            => _tenantAccessor = tenantAccessor;

        public TenantId? Observed { get; private set; }

        public Task<TenantId?> HandleAsync(ObserveTenant query, CancellationToken ct)
        {
            Observed = _tenantAccessor.Current;
            return Task.FromResult(_tenantAccessor.Current);
        }
    }

    // Holds both concurrent dispatches inside HandleAsync at the same time, so the test can prove the
    // accessor carries each flow's own tenant rather than leaking one into the other. Stateless beyond
    // the shared rendezvous, so one instance safely serves both dispatches.
    private sealed class RendezvousTenantHandler : IQueryHandler<ObserveTenant, TenantId?>
    {
        private readonly ICurrentTenantAccessor _tenantAccessor;
        private readonly TaskCompletionSource _release;
        private readonly TaskCompletionSource _bothEntered;
        private int _entered;

        public RendezvousTenantHandler(
            ICurrentTenantAccessor tenantAccessor, TaskCompletionSource release, TaskCompletionSource bothEntered)
        {
            _tenantAccessor = tenantAccessor;
            _release = release;
            _bothEntered = bothEntered;
        }

        public async Task<TenantId?> HandleAsync(ObserveTenant query, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _entered) == 2)
            {
                _bothEntered.SetResult();
            }
            await _release.Task;
            return _tenantAccessor.Current;
        }
    }
}
