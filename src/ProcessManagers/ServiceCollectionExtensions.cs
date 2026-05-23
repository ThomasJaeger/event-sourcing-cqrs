using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace EventSourcingCqrs.ProcessManagers;

public static class ServiceCollectionExtensions
{
    // Registers a process-manager handler and its consumer-side forwardings: the
    // THandler scoped registration, and one IProcessManagerHandler<TEvent>
    // forwarding per closed interface it implements, so the outbox dispatcher
    // resolves the one handler for each event it subscribes to. Scoped is the
    // contrast with AddProjection's singleton: PM handlers depend on the scoped
    // aggregate and process-manager repositories, which is why the dispatcher opens
    // a scope per message. Mirrors AddProjection's reflection-walk so the
    // forwardings stay in step with the handler's implemented interfaces.
    public static IServiceCollection AddProcessManagerHandler<THandler>(this IServiceCollection services)
        where THandler : class
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<THandler>();

        foreach (var implemented in typeof(THandler).GetInterfaces())
        {
            if (!implemented.IsGenericType
                || implemented.GetGenericTypeDefinition() != typeof(IProcessManagerHandler<>))
            {
                continue;
            }
            services.AddScoped(implemented, sp => sp.GetRequiredService<THandler>());
        }

        return services;
    }
}
