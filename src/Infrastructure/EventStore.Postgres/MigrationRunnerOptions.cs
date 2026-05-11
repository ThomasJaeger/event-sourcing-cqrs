namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

public sealed class MigrationRunnerOptions
{
    public required string ConnectionString { get; init; }

    public bool DryRun { get; init; }

    public Action<string>? Log { get; init; }
}
