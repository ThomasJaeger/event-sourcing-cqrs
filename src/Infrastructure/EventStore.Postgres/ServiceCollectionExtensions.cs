using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPostgresEventStore(
        this IServiceCollection services,
        Action<PostgresEventStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.AddOptions<OutboxProcessorOptions>();

        // Defaults a host can pre-empt. A host that needs custom data-source
        // wiring (logging integration, custom type mappings, connection
        // multiplexing) registers its own NpgsqlDataSource before calling
        // this extension. JsonSerializerOptions defaults to snake_case_lower
        // so the payload and metadata round-trip through the schema's STORED
        // generated columns; it freezes on first use, so post-registration
        // mutation is closed by design and a host that wants different
        // options must pre-register them too.
        services.TryAddSingleton<NpgsqlDataSource>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<PostgresEventStoreOptions>>().Value;
            return NpgsqlDataSource.Create(opts.ConnectionString);
        });
        services.TryAddSingleton(_ => new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        });

        services.AddSingleton<INpgsqlConnectionFactory, NpgsqlConnectionFactory>();
        services.AddSingleton<EventTypeRegistry>();
        services.AddSingleton<IEventStore, PostgresEventStore>();

        // Factory delegate so the policy picks up OutboxProcessorOptions
        // overrides at first resolution. The singleton is constructed once;
        // a services.Configure<OutboxProcessorOptions>(...) call AFTER
        // AddPostgresEventStore has no effect on the policy because the
        // snapshot is taken at the first GetService<OutboxRetryPolicy>().
        // Configure outbox options before calling this extension.
        services.AddSingleton<OutboxRetryPolicy>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<OutboxProcessorOptions>>().Value;
            return new OutboxRetryPolicy(opts.BaseSeconds, opts.CapSeconds);
        });

        services.AddSingleton<IMessageDispatcher, InProcessMessageDispatcher>();
        services.AddHostedService<OutboxProcessor>();

        return services;
    }
}
