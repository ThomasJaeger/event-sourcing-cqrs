namespace EventSourcingCqrs.Infrastructure.EventStore.SqlServer;

public sealed class SqlServerMigrationRunnerOptions
{
    public required string ConnectionString { get; init; }

    public bool DryRun { get; init; }

    public Action<string>? Log { get; init; }
}
