using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Projections.OrderList;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddReadModels(
        this IServiceCollection services,
        Action<ReadModelOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);

        // The read-model side keeps its own connection string and its own
        // NpgsqlDataSource, built here inside the factory rather than registered
        // as a bare NpgsqlDataSource. AddPostgresEventStore already registers
        // that service type; a second TryAddSingleton<NpgsqlDataSource> would be
        // silently dropped by call order, and ReadModelOptions.ConnectionString
        // would be ignored. The two sides share one database in v1, but the
        // separation makes the split-database move a configuration change. A
        // host that needs custom data-source wiring pre-registers its own
        // IReadModelConnectionFactory before calling this extension.
        services.TryAddSingleton<IReadModelConnectionFactory>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<ReadModelOptions>>().Value;
            return new NpgsqlReadModelConnectionFactory(
                NpgsqlDataSource.Create(opts.ConnectionString));
        });

        services.AddSingleton<ICheckpointStore, PostgresCheckpointStore>();
        services.AddSingleton<IOrderListStore, PostgresOrderListStore>();

        // One projection instance with one identity, surfaced under every
        // interface its consumers resolve: IProjection for the replayer and the
        // AdminConsole dashboard, IEventHandler<TEvent> for the outbox
        // dispatcher. The forwarding registrations all resolve the bare
        // OrderListProjection singleton, so every interface hands back the same
        // instance. Hand-written per event type; one projection does not earn a
        // reflection-driven registration helper. The second projection does.
        services.AddSingleton<OrderListProjection>();
        services.AddSingleton<IProjection>(
            sp => sp.GetRequiredService<OrderListProjection>());
        services.AddSingleton<IEventHandler<OrderPlaced>>(
            sp => sp.GetRequiredService<OrderListProjection>());
        services.AddSingleton<IEventHandler<OrderShipped>>(
            sp => sp.GetRequiredService<OrderListProjection>());
        services.AddSingleton<IEventHandler<OrderCancelled>>(
            sp => sp.GetRequiredService<OrderListProjection>());

        return services;
    }
}
