using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Access.ReadModels;
using EventSourcingCqrs.Domain.Billing.ReadModels;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Infrastructure.SignalR;
using EventSourcingCqrs.Projections.CurrentRoles;
using EventSourcingCqrs.Projections.CustomerSummary;
using EventSourcingCqrs.Projections.Infrastructure;
using EventSourcingCqrs.Projections.InventoryDashboard;
using EventSourcingCqrs.Projections.OrderDetail;
using EventSourcingCqrs.Projections.OrderIdToPaymentId;
using EventSourcingCqrs.Projections.OrderList;
using EventSourcingCqrs.Projections.OrderThroughput;
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

        // The current-roles read path, single-sourced in AddCurrentRolesReadModel: it configures the
        // read-model options and registers the connection factory, the checkpoint store, and the
        // current-roles store. AddReadModels adds the rest of the read-model surface on top.
        services.AddCurrentRolesReadModel(configure);

        // The notification publisher the six unit-of-works stage onto inside
        // CommitAsync. One shared singleton, stateless beyond its logger; it sources
        // the envelope serializer and channel name from NotificationContract, so it
        // needs no JsonSerializerOptions from the container. TryAdd so a host that
        // wires its own publisher wins. The stores take it by constructor injection;
        // the four read models with no v1 subscriber hold it but never stage onto it.
        services.TryAddSingleton<PostgresPgNotifyPublisher>();

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

        services.AddSingleton<ICustomerSummaryStore, PostgresCustomerSummaryStore>();
        services.AddProjection<CustomerSummaryProjection>();

        services.AddSingleton<IInventoryDashboardStore, PostgresInventoryDashboardStore>();
        services.AddProjection<InventoryDashboardProjection>();

        services.AddSingleton<IOrderThroughputStore, PostgresOrderThroughputStore>();
        services.AddProjection<OrderThroughputProjection>();

        services.AddSingleton<IOrderDetailStore, PostgresOrderDetailStore>();
        services.AddProjection<OrderDetailProjection>();

        // The current-roles read store is registered by AddCurrentRolesReadModel above; its catch-up
        // projection registers here with the rest of the projection set.
        services.AddProjection<CurrentRolesProjection>();

        return services;
    }

    // The current-roles read path: the store the principal factory and the AdminConsole authorization
    // handler read roles through, plus the connection factory and checkpoint store it depends on. A
    // host that needs only to read current roles (the AdminConsole, ADR 0040) composes this alone,
    // free of the projection set and the event store; AddReadModels delegates here so the read path
    // has one home. Read-path only: it does not register the CurrentRolesProjection catch-up, which
    // belongs to the hosts that run the projection set.
    public static IServiceCollection AddCurrentRolesReadModel(
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
        services.AddSingleton<ICurrentUserRolesStore, PostgresCurrentUserRolesStore>();

        return services;
    }
}
