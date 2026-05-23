using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing.ReadModels;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Projections.Infrastructure;
using EventSourcingCqrs.Projections.OrderIdToPaymentId;
using EventSourcingCqrs.Projections.OrderList;
using EventSourcingCqrs.Projections.SkuToInventoryId;
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

        // The store-to-port pairings stay hand-written: they are Postgres-specific
        // and not derivable from the projection type. AddProjection registers each
        // projection's singleton, its IProjection forwarding, and one
        // IEventHandler<TEvent> forwarding per subscribed event (by reflection over
        // the projection's interfaces), so every interface its consumers resolve
        // hands back the one singleton.
        services.AddSingleton<IOrderListStore, PostgresOrderListStore>();
        services.AddProjection<OrderListProjection>();

        services.AddSingleton<ISkuToInventoryIdStore, PostgresSkuToInventoryIdStore>();
        services.AddProjection<SkuToInventoryIdProjection>();

        services.AddSingleton<IOrderIdToPaymentIdStore, PostgresOrderIdToPaymentIdStore>();
        services.AddProjection<OrderIdToPaymentIdProjection>();

        return services;
    }
}
