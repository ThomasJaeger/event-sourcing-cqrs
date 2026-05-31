using System.Reflection;
using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Application.Pipelines;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using EventSourcingCqrs.Application.Authorization;

namespace EventSourcingCqrs.Application;

// Hosts call AddApplication after their event-store registration. The
// extension wires the buses, the AsyncLocal accessor, the open-generic
// pipeline behaviors, the engine-agnostic repository, and reflection-scans
// every ICommandHandler<> / IQueryHandler<,> in the Application assembly so a
// host does not have to enumerate handlers by hand.
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(
        this IServiceCollection services,
        Action<ApplicationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ApplicationOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // Lets a host pre-register a fake TimeProvider for tests; falls back
        // to the wall clock when nothing has been registered yet.
        services.TryAddSingleton(TimeProvider.System);

        // One CommandBus instance behind two handles: the public ICommandBus and
        // the concrete type. CausedCommandBus needs the concrete type to reach
        // the internal SendWithContextAsync seam (ADR 0014), and both must
        // resolve the same singleton so the AsyncLocal accessor scoping stays
        // coherent across user and process-manager dispatch.
        services.AddSingleton<CommandBus>();
        services.AddSingleton<ICommandBus>(sp => sp.GetRequiredService<CommandBus>());
        services.AddSingleton<ICausedCommandBus, CausedCommandBus>();
        services.AddSingleton<IQueryBus, QueryBus>();
        services.AddSingleton<ICommandContextAccessor, AsyncLocalCommandContextAccessor>();
        services.AddSingleton<IQueryContextAccessor, AsyncLocalQueryContextAccessor>();

        // Query types resolve through QueryTypeRegistry for the /queries HTTP
        // endpoint's type-discriminator dispatch (ADR 0022). Populated from
        // IQueryTypeProvider exactly as the event-store registries are populated
        // from their providers in AddPostgresEventStore, walked lazily at first
        // resolution. TryAddSingleton so a host can pre-register a populated
        // registry and win. Empty until a host registers query providers (the Api
        // and Web hosts do; the Workers host registers none and never resolves it).
        services.TryAddSingleton<QueryTypeRegistry>(sp =>
        {
            var registry = new QueryTypeRegistry();
            foreach (var provider in sp.GetServices<IQueryTypeProvider>())
            {
                foreach (var queryType in provider.GetQueryTypes())
                {
                    registry.Register(queryType);
                }
            }
            return registry;
        });

        // Logging is outermost so its one-log-line-per-command guarantee covers
        // short-circuited validation failures and deduplicated duplicates alike.
        // Authorization sits inside logging and before idempotency (ADR 0028): an
        // unauthorized attempt is logged and consumes no idempotency storage.
        // Idempotency sits inside authorization and before validation (ADR 0016): a
        // duplicate is logged but does no validation work and never reaches the
        // handler. Registration order is pipeline order outermost-to-innermost
        // (CommandPipelineBuilder folds in reverse).
        services.AddSingleton(typeof(ICommandPipelineBehavior<>), typeof(LoggingCommandBehavior<>));
        services.AddSingleton(typeof(ICommandPipelineBehavior<>), typeof(AuthorizationCommandBehavior<>));
        services.AddSingleton(typeof(ICommandPipelineBehavior<>), typeof(IdempotencyBehavior<>));
        services.AddSingleton(typeof(ICommandPipelineBehavior<>), typeof(ValidationCommandBehavior<>));
        services.AddSingleton(typeof(IQueryPipelineBehavior<,>), typeof(LoggingQueryBehavior<,>));
        services.AddSingleton(typeof(IQueryPipelineBehavior<,>), typeof(AuthorizationQueryBehavior<,>));

        services.AddScoped(typeof(IEventStoreRepository<>), typeof(EventStoreRepository<>));
        services.AddScoped(typeof(IProcessManagerRepository<>), typeof(ProcessManagerRepository<>));

        RegisterHandlers(services);

        // Permission-based authorization policy substrate. The role-to-permission policy is part of
        // the application definition and validates at composition: an incomplete or malformed policy
        // throws here, at startup, rather than at the first authorization decision.
        services.AddSingleton(new RolePermissionRegistry(RolePermissionPolicy.Default));
        services.AddSingleton<IPermissionAuthorizer, PermissionAuthorizer>();

        // Maps an authenticated actor to the customer it owns, so the ownership-filtering query handlers
        // compare a resolved customer id against a read-model row. The P9.5 implementation is the
        // actor-equals-customer convention; Phase 10 swaps it for an actor-to-customer mapping (ADR 0028).
        services.AddSingleton<IResourceOwnershipResolver, ActorIsCustomerOwnershipResolver>();

        // Every concrete command in this assembly must declare a required permission by implementing
        // IAuthorizedCommand. The walk runs at composition, eager rather than in a factory, so a command
        // added without a declaration is a startup failure rather than an unauthorized dispatch in
        // production. This assembly holds exactly the user commands; the ProcessManagers-assembly timeout
        // commands fall outside the walk by assembly boundary and declare their permission at the
        // caused-command commit. The CommandTypeRegistry was rejected as the surface: it is host-dependent
        // and registers only a subset, missing the process-manager-dispatched commands.
        var undeclaredCommands = CommandPermissionValidation.FindUndeclared(
            typeof(ServiceCollectionExtensions).Assembly.GetTypes());
        if (undeclaredCommands.Count > 0)
        {
            throw new CommandPermissionDeclarationException(undeclaredCommands);
        }

        // The read-side twin of the command walk: every concrete query in this assembly must declare a
        // required permission by implementing IAuthorizedQuery, so a query added without a declaration is
        // a startup failure rather than an unenforced read. The Application assembly holds exactly the
        // five read queries.
        var undeclaredQueries = QueryPermissionValidation.FindUndeclared(
            typeof(ServiceCollectionExtensions).Assembly.GetTypes());
        if (undeclaredQueries.Count > 0)
        {
            throw new QueryPermissionDeclarationException(undeclaredQueries);
        }

        return services;
    }

    private static void RegisterHandlers(IServiceCollection services)
    {
        var assembly = typeof(ServiceCollectionExtensions).Assembly;
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface || !type.IsClass)
            {
                continue;
            }
            foreach (var iface in type.GetInterfaces())
            {
                if (!iface.IsGenericType)
                {
                    continue;
                }
                var def = iface.GetGenericTypeDefinition();
                if (def == typeof(ICommandHandler<>) || def == typeof(IQueryHandler<,>))
                {
                    services.AddScoped(iface, type);
                }
            }
        }
    }
}
