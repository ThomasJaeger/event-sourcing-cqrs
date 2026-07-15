namespace EventSourcingCqrs.Infrastructure.EventStore.Kurrent;

public sealed class KurrentEventStoreOptions
{
    // The KurrentDB gRPC connection string, e.g. esdb://host:2113?tls=false. Parsed by
    // KurrentDBClientSettings.Create in AddKurrentEventStore.
    public string ConnectionString { get; set; } = string.Empty;
}
