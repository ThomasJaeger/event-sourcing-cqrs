using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.EventStore.ContractTests;
using EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;
using Microsoft.Extensions.DependencyInjection;

namespace EventSourcingCqrs.Infrastructure.Tests.DynamoDb;

// One backend per fact: its own table on the shared LocalStack node, its own composed store, the
// table dropped with the fact. The suite's event types register into the adapter's registries here
// through the same provider seam AddDynamoDbEventStore walks; the suite itself knows nothing about
// EventTypeRegistry.
//
// Composed through the production DI seam, following KurrentContractBackend rather than the
// relational backends' direct construction: resolving IEventStore runs the registry provider walk,
// so a registration defect surfaces at construction rather than as a puzzling fact failure.
//
// It implements the universal IEventStoreContractBackend only, not IHeldWriterContractBackend.
// DynamoDB has no interactive, held-open write: a spike against the live engine found the client's
// only transaction surface is TransactWriteItems, a one-shot request carrying its whole item list,
// with no Begin, Commit, or Rollback anywhere on AmazonDynamoDBClient. ADR 0049 records that engine
// fact and the capability routing that follows from it.
internal sealed class DynamoDbContractBackend : IEventStoreContractBackend
{
    private readonly ServiceProvider _provider;
    private readonly IAmazonDynamoDB _client;
    private readonly string _tableName;

    private DynamoDbContractBackend(
        ServiceProvider provider,
        IAmazonDynamoDB client,
        string tableName,
        IEventStore store,
        IEventStoreHeadPosition headReader)
    {
        _provider = provider;
        _client = client;
        _tableName = tableName;
        Store = store;
        HeadReader = headReader;
    }

    public IEventStore Store { get; }

    // The head reader rides this backend rather than a Compose helper of its own, because it needs
    // the same provisioned table the store writes to and the shared contract suite ignores members
    // it does not know about. KurrentEventStoreHeadReaderTests builds its own graph instead, for the
    // reason its fixture hands out a container rather than a composed store.
    public IEventStoreHeadPosition HeadReader { get; }

    public static async Task<DynamoDbContractBackend> CreateAsync(LocalStackFixture fixture)
    {
        var tableName = LocalStackFixture.NewTableName();

        var services = new ServiceCollection();
        services.AddSingleton<IEventTypeProvider, ContractDomainEventTypeProvider>();
        services.AddSingleton<IProcessManagerEventTypeProvider, ContractProcessManagerEventTypeProvider>();
        services.AddDynamoDbEventStore(opts =>
        {
            opts.ServiceUrl = fixture.ServiceUrl;
            opts.TableName = tableName;
            opts.Region = "us-east-1";
            opts.AccessKeyId = "test";
            opts.SecretAccessKey = "test";
        });
        services.AddDynamoDbEventStoreHeadPosition();

        var provider = services.BuildServiceProvider();
        try
        {
            var store = provider.GetRequiredService<IEventStore>();
            var client = provider.GetRequiredService<IAmazonDynamoDB>();
            var headReader = provider.GetRequiredService<IEventStoreHeadPosition>();

            // The schema is the test's to provision, the way the relational fixtures migrate before
            // handing a database over. The Workers host adopts the same provisioner in a later slice.
            await provider.GetRequiredService<DynamoDbTableProvisioner>()
                .EnsureTableAsync(CancellationToken.None);

            return new DynamoDbContractBackend(provider, client, tableName, store, headReader);
        }
        catch
        {
            await provider.DisposeAsync();
            throw;
        }
    }

    // The global feed, read straight off the log partition with ConsistentRead in sort order. This
    // reaches past IEventStore on purpose: the hook must see the PM rows ReadAllAsync excludes, and
    // one log partition carries both append paths, so this read spans the whole committed sequence.
    // ConsistentRead is available here only because the log lives on the base table; the GSI the
    // plan proposed could not serve it (ADR 0049).
    public async Task<IReadOnlyList<long>> ReadCommittedPositionsAsync()
    {
        var positions = new List<long>();
        Dictionary<string, AttributeValue>? start = null;

        do
        {
            var page = await _client.QueryAsync(new QueryRequest
            {
                TableName = _tableName,
                KeyConditionExpression = "pk = :pk",
                ExpressionAttributeValues = new()
                {
                    [":pk"] = new AttributeValue { S = "$log" },
                },
                ConsistentRead = true,
                ScanIndexForward = true,
                ExclusiveStartKey = start,
            });

            positions.AddRange(page.Items.Select(i => long.Parse(i["sk"].N)));
            start = page.LastEvaluatedKey is { Count: > 0 } ? page.LastEvaluatedKey : null;
        }
        while (start is not null);

        return positions;
    }

    public async ValueTask DisposeAsync()
    {
        // Drop the table rather than leave it: the node is shared across the class, and a fact's
        // leftovers would otherwise accumulate for the container's life.
        try
        {
            await _client.DeleteTableAsync(_tableName);
        }
        catch (ResourceNotFoundException)
        {
            // Never provisioned, or already gone. Either way there is nothing to drop.
        }

        await _provider.DisposeAsync();
    }
}

// The suite's event types, handed to AddDynamoDbEventStore through the same provider seam a host
// uses. Named apart from the Kurrent backend's equivalents, which are internal to their own
// namespace.
internal sealed class ContractDomainEventTypeProvider : IEventTypeProvider
{
    public IEnumerable<Type> GetEventTypes() => ContractEventTypes.DomainEvents;
}

internal sealed class ContractProcessManagerEventTypeProvider : IProcessManagerEventTypeProvider
{
    public IEnumerable<Type> GetEventTypes() => ContractEventTypes.ProcessManagerEvents;
}
