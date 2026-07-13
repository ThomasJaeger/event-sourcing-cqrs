namespace EventSourcingCqrs.Infrastructure.EventStore.SqlServer;

public sealed class SqlServerEventStoreOptions
{
    public string ConnectionString { get; set; } = string.Empty;
}
