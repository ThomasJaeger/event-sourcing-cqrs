namespace EventSourcingCqrs.Infrastructure.EventStore.SqlServer;

public sealed class SqlServerMigrationOutOfOrderException : Exception
{
    public SqlServerMigrationOutOfOrderException(
        int version, string name, int highestApplied)
        : base(
            $"Migration {version:0000}_{name} is out of order. Highest applied version: {highestApplied:0000}. " +
            "A migration numbered below what is already applied would run against a schema that " +
            "later migrations have already shaped. " +
            "Renumber it above the highest applied version.")
    {
        Version = version;
        Name = name;
        HighestApplied = highestApplied;
    }

    public int Version { get; }

    public string Name { get; }

    public int HighestApplied { get; }
}
