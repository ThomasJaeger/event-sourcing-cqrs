using System.Collections.Concurrent;
using System.Reflection;
using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Application.Pipelines;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace EventSourcingCqrs.Application;

// Hand-rolled command dispatch. Same shape as InProcessMessageDispatcher: the
// runtime command type closes the generic ICommandHandler<TCommand>, the closed
// type plus its HandleAsync method get cached once per command type, and every
// subsequent dispatch is one DI lookup plus one MethodInfo.Invoke. A compiled
// delegate cache would skip the Invoke cost but adds machinery; v1 keeps the
// shape simple and a later commit can swap it in if a profiler asks for it.
//
// Each dispatch opens a service scope, builds a fresh CommandContext, pushes
// it onto the AsyncLocal accessor, runs the pipeline, and restores the prior
// accessor value in finally so nested dispatches and exception paths leak no
// context.
public sealed class CommandBus : ICommandBus
{
    private static readonly ConcurrentDictionary<Type, CommandInvoker> InvokerCache = new();
    private readonly IServiceProvider _services;

    public CommandBus(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    public Task SendAsync(ICommand command, CancellationToken ct)
        => SendInternal(command, Guid.NewGuid(), idempotencyKey: null, ct);

    // Overload for callers that already have a correlation ID (HTTP middleware
    // forwarding an X-Correlation-Id header, a process manager continuing an
    // existing causation chain). Lives on the concrete class so the interface
    // stays at the shape Ch 10 depicts; callers that need this take CommandBus
    // directly or push a pre-built context onto the accessor before calling
    // the interface form.
    public Task SendAsync(ICommand command, Guid correlationId, CancellationToken ct)
        => SendInternal(command, correlationId, idempotencyKey: null, ct);

    // User-dispatch with a caller-supplied idempotency key (ADR 0021). Mints a
    // fresh context exactly like the bare overload and threads the key into it,
    // so IdempotencyBehavior can dedupe a retried command. The PM seam stays the
    // place where a caller supplies all of the context; this path only adds a
    // key to the otherwise-minted user context.
    public Task SendAsync(ICommand command, string? idempotencyKey, CancellationToken ct)
        => SendInternal(command, Guid.NewGuid(), idempotencyKey, ct);

    // Authenticated user-dispatch (Phase 9). The Api /commands endpoint resolves the actor and its
    // authoritative roles and dispatches here, so both land on the command context and the actor
    // flows into event metadata. Mints a fresh correlation and causation id like the bare path; the
    // bare paths keep ActorId = Guid.Empty for callers without a principal.
    public Task SendAsync(
        ICommand command,
        Guid actorId,
        IReadOnlyCollection<Role> roles,
        TenantId tenant,
        string? idempotencyKey,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return DispatchAsync(
            command,
            (timeProvider, options) => new CommandContext(timeProvider)
            {
                CorrelationId = Guid.NewGuid(),
                CausationCommandId = Guid.NewGuid(),
                ActorId = actorId,
                Roles = roles,
                AuthorizationMode = DispatchAuthorizationMode.AuthenticatedUser,
                ServiceName = options.ServiceName,
                IdempotencyKey = idempotencyKey
            },
            tenant,
            ct);
    }

    private Task SendInternal(
        ICommand command, Guid correlationId, string? idempotencyKey, CancellationToken ct)
        // ActorId stays Guid.Empty until Phase 7's HTTP middleware maps an
        // authenticated principal. ServiceName reads from ApplicationOptions,
        // defaulting to "Workers" when the host has not configured it.
        // IdempotencyKey is null on the bare and correlation-id paths and carries
        // the caller's key on the idempotency overload (ADR 0021).
        => DispatchAsync(
            command,
            (timeProvider, options) => new CommandContext(timeProvider)
            {
                CorrelationId = correlationId,
                CausationCommandId = Guid.NewGuid(),
                ActorId = Guid.Empty,
                ServiceName = options.ServiceName,
                IdempotencyKey = idempotencyKey
            },
            // The bare path carries no principal and no causing event, so the default tenant is the
            // honest value (the no-principal funnel; see the SendInternal callers).
            WellKnownTenants.Default,
            ct);

    // Process-manager dispatch enters here through CausedCommandBus (ADR 0014).
    // The caller supplies the context values built from the causing event's
    // metadata instead of letting the bus mint fresh ones; everything else is
    // the user-dispatch path unchanged. internal because CausedCommandBus shares
    // this assembly, so the public ICommandBus surface does not widen. The
    // dispatch runs in SystemActor mode under Role.System (the single source on
    // SystemActor), so the authorization behavior enforces the caused path.
    internal Task SendWithContextAsync(
        ICommand command, CausedDispatchFragment fragment, TenantId tenant, CancellationToken ct)
        => DispatchAsync(
            command,
            (timeProvider, _) => new CommandContext(timeProvider)
            {
                CorrelationId = fragment.CorrelationId,
                CausationCommandId = fragment.CausationCommandId,
                ActorId = fragment.ActorId,
                Roles = SystemActor.SystemRoles,
                AuthorizationMode = DispatchAuthorizationMode.SystemActor,
                ServiceName = fragment.ServiceName,
                IdempotencyKey = fragment.IdempotencyKey
            },
            tenant,
            ct);

    // The one dispatch loop both entry points run: open a scope, resolve the
    // handler, behaviors, and accessor, build the context from the caller's
    // recipe, push it for the pipeline's duration, and restore the prior value
    // in finally. Sharing it makes behaviors-run-once and accessor-scope-holds
    // structural properties of either caller rather than discipline repeated at
    // two sites.
    private async Task DispatchAsync(
        ICommand command,
        Func<TimeProvider, ApplicationOptions, CommandContext> buildContext,
        TenantId tenant,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        var invoker = InvokerCache.GetOrAdd(command.GetType(), BuildInvoker);
        var handler = sp.GetRequiredService(invoker.HandlerType);
        var behaviors = sp.GetServices(invoker.BehaviorType).ToArray();
        var accessor = sp.GetRequiredService<ICommandContextAccessor>();
        var tenantAccessor = sp.GetRequiredService<ICurrentTenantAccessor>();
        var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
        var options = sp.GetService<ApplicationOptions>() ?? new ApplicationOptions();

        var context = buildContext(timeProvider, options);

        // The tenant is pushed onto its accessor in the same block as the command
        // context, so a handler that finds a context also finds the tenant. Both
        // restore in finally so a nested dispatch sees its parent's values.
        var previous = accessor.Current;
        var previousTenant = tenantAccessor.Current;
        accessor.Current = context;
        tenantAccessor.Current = tenant;
        try
        {
            var pipeline = CommandPipelineBuilder.Build(
                behaviors, handler, command, invoker.HandleMethod, invoker.BehaviorHandleMethod, ct);
            await pipeline();
        }
        finally
        {
            accessor.Current = previous;
            tenantAccessor.Current = previousTenant;
        }
    }

    private static CommandInvoker BuildInvoker(Type commandType)
    {
        var handlerType = typeof(ICommandHandler<>).MakeGenericType(commandType);
        var behaviorType = typeof(ICommandPipelineBehavior<>).MakeGenericType(commandType);
        return new CommandInvoker(
            HandlerType: handlerType,
            HandleMethod: handlerType.GetMethod(nameof(ICommandHandler<ICommand>.HandleAsync))!,
            BehaviorType: behaviorType,
            BehaviorHandleMethod: behaviorType.GetMethod(nameof(ICommandPipelineBehavior<ICommand>.HandleAsync))!);
    }

    private sealed record CommandInvoker(
        Type HandlerType,
        MethodInfo HandleMethod,
        Type BehaviorType,
        MethodInfo BehaviorHandleMethod);
}
