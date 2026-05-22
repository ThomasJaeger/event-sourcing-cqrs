using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace EventSourcingCqrs.Projections.Infrastructure;

public static class ServiceCollectionExtensions
{
    // Registers a projection and its consumer-side forwardings: the TProjection
    // singleton, the IProjection forwarding (for the replayer and the
    // AdminConsole dashboard), and one IEventHandler<TEvent> forwarding per
    // closed IEventHandler<EventContext<T>> the projection implements (for the
    // outbox dispatcher), discovered by the same GetType().GetInterfaces() filter
    // ProjectionReplayer uses to build its replay dispatch table.
    //
    // It does not register the projection's store, its checkpoint store, or
    // anything engine-specific; those stay hand-written in the read-model
    // composition root because they are adapter-to-port pairings, not derivable
    // from the projection type.
    //
    // Every forwarding resolves the one TProjection singleton, so the projection
    // has one identity across every interface its consumers resolve.
    public static IServiceCollection AddProjection<TProjection>(this IServiceCollection services)
        where TProjection : class, IProjection
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<TProjection>();
        services.AddSingleton<IProjection>(sp => sp.GetRequiredService<TProjection>());

        foreach (var implemented in typeof(TProjection).GetInterfaces())
        {
            if (!implemented.IsGenericType
                || implemented.GetGenericTypeDefinition() != typeof(IEventHandler<>))
            {
                continue;
            }
            // Forward the closed IEventHandler<TEvent> to the same singleton, so
            // the dispatcher resolves the projection for each event it subscribes to.
            services.AddSingleton(implemented, sp => sp.GetRequiredService<TProjection>());
        }

        return services;
    }
}
