using Amazon;
using Amazon.DynamoDBStreams;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.Outbox;
using EventSourcingCqrs.Infrastructure.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;

public static class ServiceCollectionExtensions
{
    // Composes the DynamoDB event store: the client, the JSON options, the three type registries
    // populated by the same provider walk every other adapter uses, the table provisioner, and the
    // store. No outbox and no companion ports: DynamoDB holds only events, and ADR 0046's
    // non-relational posture puts the idempotency store and the delay queue on the read-model
    // PostgreSQL database. The host arm that composes them lands in a later slice.
    //
    // TryAdd throughout, matching AddKurrentEventStore and diverging from AddPostgresEventStore's
    // plain Adds for the same reason: the relational adapters own the sole bare NpgsqlDataSource in
    // a host, while a DynamoDB arm composes into a host whose read-model side is PostgreSQL, so
    // registering defensively keeps a shared dependency from being double-registered.
    public static IServiceCollection AddDynamoDbEventStore(
        this IServiceCollection services,
        Action<DynamoDbEventStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);

        // The shared seam in Infrastructure/Versioning (ADR 0048). A payload written on any engine
        // round-trips on any other only while all four agree on it, so this adapter reaches for the
        // one factory rather than carrying a fourth copy.
        services.TryAddSingleton(_ => EventStoreJsonOptions.Create());

        services.TryAddSingleton<IAmazonDynamoDB>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<DynamoDbEventStoreOptions>>().Value;
            var config = new AmazonDynamoDBConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(opts.Region) };

            // A service URL overrides the region-derived endpoint, which is how LocalStack is
            // reached. RegionEndpoint and ServiceURL are mutually exclusive on the SDK config, so
            // the region survives as AuthenticationRegion for the signer.
            if (!string.IsNullOrWhiteSpace(opts.ServiceUrl))
            {
                config = new AmazonDynamoDBConfig
                {
                    ServiceURL = opts.ServiceUrl,
                    AuthenticationRegion = opts.Region,
                };
            }

            // Empty credentials mean the SDK's own resolution chain, which is what a deployed host
            // wants. LocalStack needs a non-empty pair and does not care what it is.
            return string.IsNullOrWhiteSpace(opts.AccessKeyId)
                ? new AmazonDynamoDBClient(config)
                : new AmazonDynamoDBClient(
                    new BasicAWSCredentials(opts.AccessKeyId, opts.SecretAccessKey), config);
        });

        // Providers contribute by bounded context, exactly as on every other adapter. The factory
        // walks every registered provider once on first resolution; the registry is immutable from
        // there.
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

        // Command types resolve through CommandTypeRegistry for the delay queue (ADR 0017),
        // registered here for parity with the other adapters even though this slice does not
        // consume it.
        services.TryAddSingleton<CommandTypeRegistry>(sp =>
        {
            var registry = new CommandTypeRegistry();
            foreach (var provider in sp.GetServices<ICommandTypeProvider>())
            {
                foreach (var commandType in provider.GetCommandTypes())
                {
                    registry.Register(commandType);
                }
            }
            return registry;
        });

        services.TryAddSingleton<DynamoDbTableProvisioner>();
        services.TryAddSingleton<IEventStore, DynamoDbEventStore>();

        return services;
    }

    // Registers the DynamoDB head-position reader for the AdminConsole's projection-lag read, the
    // engine-specific counterpart to AddKurrentEventStoreHeadPosition and to the relational
    // AddEventStoreHeadPosition. It reads the log partition's tail, so it needs only the client and
    // the options the AddDynamoDbEventStore registration provides; a host composes both. TryAdd so it
    // composes beside that registration without racing it.
    public static IServiceCollection AddDynamoDbEventStoreHeadPosition(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IEventStoreHeadPosition, DynamoDbEventStoreHeadReader>();

        return services;
    }

    // Registers the DynamoDB Streams dispatch service, the native read-side mechanism the Workers
    // host composes in place of an outbox processor. The table's change feed wakes it; the log
    // partition is what it reads. There is no outbox table and no poll of the event table.
    //
    // Same posture as AddKurrentSubscriptionService: the hosted service is a plain Add, everything
    // else TryAdd, so this composes into a host whose read-model side is PostgreSQL without
    // double-registering a shared dependency.
    public static IServiceCollection AddDynamoDbStreamDispatchService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<DynamoDbStreamDispatchOptions>();

        // The Streams consumer API lives in AWSSDK.DynamoDBStreams, a package separate from
        // AWSSDK.DynamoDBv2: the table client cannot read the stream it enables. Its endpoint is
        // the same one the table client uses, so it reads the same options.
        services.TryAddSingleton<IAmazonDynamoDBStreams>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<DynamoDbEventStoreOptions>>().Value;
            var config = new AmazonDynamoDBStreamsConfig
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(opts.Region),
            };

            if (!string.IsNullOrWhiteSpace(opts.ServiceUrl))
            {
                config = new AmazonDynamoDBStreamsConfig
                {
                    ServiceURL = opts.ServiceUrl,
                    AuthenticationRegion = opts.Region,
                };
            }

            return string.IsNullOrWhiteSpace(opts.AccessKeyId)
                ? new AmazonDynamoDBStreamsClient(config)
                : new AmazonDynamoDBStreamsClient(
                    new BasicAWSCredentials(opts.AccessKeyId, opts.SecretAccessKey), config);
        });

        // The in-process dispatcher the drain plays events into, resolving projections and
        // process-manager handlers per event through the same class the relational outbox processors
        // and the KurrentDB subscription use (ADR 0004 covers engine-specific mechanics, not this
        // consumer-side resolver). TryAdd so a host that also composed a relational path keeps its
        // single dispatcher.
        services.TryAddSingleton<IMessageDispatcher, InProcessMessageDispatcher>();
        services.AddHostedService<DynamoDbStreamDispatchService>();

        return services;
    }
}
