namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

public sealed class PostgresEventStoreOptions
{
    public string ConnectionString { get; set; } = string.Empty;
}
