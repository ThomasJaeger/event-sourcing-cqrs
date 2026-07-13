using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace EventSourcingCqrs.Infrastructure.EventStore.SqlServer;

public static class ServiceCollectionExtensions
{
    // Mirrors AddPostgresEventStore for what the SQL Server adapter has so far: the store, its
    // two type registries, the options, and the connection factory. The idempotency store and
    // the delay queue are not here, and no host calls this yet. Slice 4 rules how a host chooses
    // between the two adapters; until then this extension exists so the composition it will need
    // is already written and testable.
    public static IServiceCollection AddSqlServerEventStore(
        this IServiceCollection services,
        Action<SqlServerEventStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);

        // snake_case_lower so payload and metadata round-trip through the schema's PERSISTED
        // computed columns, which read JSON_VALUE(metadata, '$.correlation_id') and friends. The
        // options freeze on first use, so a host wanting different ones pre-registers them.
        services.TryAddSingleton(_ => new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters = { new TenantIdJsonConverter() },
        });

        services.TryAddSingleton<ISqlServerConnectionFactory>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<SqlServerEventStoreOptions>>().Value;
            return new SqlServerConnectionFactory(opts.ConnectionString);
        });

        // Providers contribute by bounded context, exactly as on the PostgreSQL side. The factory
        // walks every registered provider once on first resolution; the registry is immutable
        // from there.
        services.TryAddSingleton<EventTypeRegistry>(sp =>
        {
            var registry = new EventTypeRegistry();
            foreach (var provider in sp.GetServices<IEventTypeProvider>())
            {
                foreach (var eventType in provider.GetEventTypes())
                {
                    registry.Register(eventType);
                }
            }
            return registry;
        });

        // PM event types resolve through a separate registry (ADR 0013).
        services.TryAddSingleton<ProcessManagerEventTypeRegistry>(sp =>
        {
            var registry = new ProcessManagerEventTypeRegistry();
            foreach (var provider in sp.GetServices<IProcessManagerEventTypeProvider>())
            {
                foreach (var eventType in provider.GetEventTypes())
                {
                    registry.Register(eventType);
                }
            }
            return registry;
        });

        services.AddSingleton<IEventStore, SqlServerEventStore>();

        return services;
    }
}
