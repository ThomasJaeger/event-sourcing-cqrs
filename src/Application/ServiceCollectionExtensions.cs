using System.Reflection;
using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Application.Pipelines;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        services.AddSingleton<ICommandBus, CommandBus>();
        services.AddSingleton<IQueryBus, QueryBus>();
        services.AddSingleton<ICommandContextAccessor, AsyncLocalCommandContextAccessor>();

        // Logging wraps Validation so a short-circuited validation failure
        // still produces one log line per command with the elapsed time and
        // the correlation id, matching the production observability story.
        services.AddSingleton(typeof(ICommandPipelineBehavior<>), typeof(LoggingCommandBehavior<>));
        services.AddSingleton(typeof(ICommandPipelineBehavior<>), typeof(ValidationCommandBehavior<>));
        services.AddSingleton(typeof(IQueryPipelineBehavior<,>), typeof(LoggingQueryBehavior<,>));

        services.AddScoped(typeof(IEventStoreRepository<>), typeof(EventStoreRepository<>));

        RegisterHandlers(services);

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
