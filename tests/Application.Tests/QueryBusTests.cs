using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Application.Tests;

public sealed class QueryBusTests
{
    [Fact]
    public async Task AskAsync_dispatches_to_registered_handler_and_returns_result()
    {
        var handler = new EchoHandler();
        var services = new ServiceCollection()
            .AddSingleton<IQueryHandler<Echo, string>>(handler)
            .AddSingleton<IQueryContextAccessor, AsyncLocalQueryContextAccessor>()
            .AddSingleton<ICurrentTenantAccessor, AsyncLocalCurrentTenantAccessor>()
            .BuildServiceProvider();
        var bus = new QueryBus(services);

        var result = await bus.AskAsync(new Echo("ping"), CancellationToken.None);

        result.Should().Be("ping");
    }

    [Fact]
    public async Task AskAsync_throws_when_handler_not_registered()
    {
        var services = new ServiceCollection()
            .AddSingleton<IQueryContextAccessor, AsyncLocalQueryContextAccessor>()
            .AddSingleton<ICurrentTenantAccessor, AsyncLocalCurrentTenantAccessor>()
            .BuildServiceProvider();
        var bus = new QueryBus(services);

        var act = () => bus.AskAsync(new Echo("x"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AskAsync_passes_cancellation_token_to_handler()
    {
        var handler = new EchoHandler();
        var services = new ServiceCollection()
            .AddSingleton<IQueryHandler<Echo, string>>(handler)
            .AddSingleton<IQueryContextAccessor, AsyncLocalQueryContextAccessor>()
            .AddSingleton<ICurrentTenantAccessor, AsyncLocalCurrentTenantAccessor>()
            .BuildServiceProvider();
        var bus = new QueryBus(services);
        using var cts = new CancellationTokenSource();

        await bus.AskAsync(new Echo("x"), cts.Token);

        handler.ReceivedToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task AskAsync_resolves_a_scoped_handler_under_scope_validation()
    {
        // ADR 0024: the bus is a singleton over the root provider and handlers
        // register scoped, so AskAsync must open a scope. A validating provider, the
        // shape WebApplicationFactory enables, throws if the scoped handler resolves
        // from the root. This locks the scope-per-dispatch fix.
        var services = new ServiceCollection()
            .AddScoped<IQueryHandler<Echo, string>, EchoHandler>()
            .AddSingleton<IQueryContextAccessor, AsyncLocalQueryContextAccessor>()
            .AddSingleton<ICurrentTenantAccessor, AsyncLocalCurrentTenantAccessor>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var bus = new QueryBus(services);

        var result = await bus.AskAsync(new Echo("ping"), CancellationToken.None);

        result.Should().Be("ping");
    }

    [Fact]
    public async Task AskAsync_with_a_principal_pushes_an_authenticated_context_the_pipeline_can_read()
    {
        var actorId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var roles = new[] { Role.Customer, Role.Support };
        var accessor = new AsyncLocalQueryContextAccessor();
        var handler = new ContextCapturingHandler(accessor);
        var services = new ServiceCollection()
            .AddSingleton<IQueryHandler<Echo, string>>(handler)
            .AddSingleton<IQueryContextAccessor>(accessor)
            .AddSingleton<ICurrentTenantAccessor, AsyncLocalCurrentTenantAccessor>()
            .BuildServiceProvider();
        var bus = new QueryBus(services);

        await bus.AskAsync(new Echo("x"), actorId, roles, WellKnownTenants.Default, CancellationToken.None);

        handler.Observed.Should().NotBeNull();
        handler.Observed!.IsAuthenticatedUserQuery.Should().BeTrue();
        handler.Observed.ActorId.Should().Be(actorId);
        handler.Observed.Roles.Should().BeEquivalentTo(roles);
    }

    [Fact]
    public async Task AskAsync_bare_overload_leaves_the_context_non_authenticated()
    {
        var accessor = new AsyncLocalQueryContextAccessor();
        var handler = new ContextCapturingHandler(accessor);
        var services = new ServiceCollection()
            .AddSingleton<IQueryHandler<Echo, string>>(handler)
            .AddSingleton<IQueryContextAccessor>(accessor)
            .AddSingleton<ICurrentTenantAccessor, AsyncLocalCurrentTenantAccessor>()
            .BuildServiceProvider();
        var bus = new QueryBus(services);

        await bus.AskAsync(new Echo("x"), CancellationToken.None);

        handler.Observed!.IsAuthenticatedUserQuery.Should().BeFalse();
        handler.Observed.ActorId.Should().Be(Guid.Empty);
        handler.Observed.Roles.Should().BeEmpty();
    }

    [Fact]
    public async Task AskAsync_restores_the_previous_accessor_value_after_the_handler_completes()
    {
        var accessor = new AsyncLocalQueryContextAccessor();
        var services = new ServiceCollection()
            .AddSingleton<IQueryHandler<Echo, string>>(new ContextCapturingHandler(accessor))
            .AddSingleton<IQueryContextAccessor>(accessor)
            .AddSingleton<ICurrentTenantAccessor, AsyncLocalCurrentTenantAccessor>()
            .BuildServiceProvider();
        var bus = new QueryBus(services);

        await bus.AskAsync(new Echo("x"), CancellationToken.None);

        accessor.Current.Should().BeNull();
    }

    private sealed record Echo(string Payload) : IQuery<string>;

    private sealed class EchoHandler : IQueryHandler<Echo, string>
    {
        public CancellationToken ReceivedToken { get; private set; }

        public Task<string> HandleAsync(Echo query, CancellationToken ct)
        {
            ReceivedToken = ct;
            return Task.FromResult(query.Payload);
        }
    }

    private sealed class ContextCapturingHandler : IQueryHandler<Echo, string>
    {
        private readonly IQueryContextAccessor _accessor;

        public ContextCapturingHandler(IQueryContextAccessor accessor) => _accessor = accessor;

        public IQueryContext? Observed { get; private set; }

        public Task<string> HandleAsync(Echo query, CancellationToken ct)
        {
            Observed = _accessor.Current;
            return Task.FromResult(query.Payload);
        }
    }
}
