using System.Reflection;

namespace EventSourcingCqrs.Infrastructure.EventStore.SqlServer;

// Migrations live as embedded .sql resources in this assembly under the LogicalName prefix
// "EventStore.SqlServer.Migrations." (see the csproj, which globs migrations/sqlserver/*.sql
// and nothing else). This class is the single owner of the assembly handle and the prefix.
//
// The PostgreSQL adapter has its own twin of this class over its own directory. The globs are
// non-recursive, so neither adapter's assembly can pick up the other's SQL by accident.
public static class EventStoreSqlServerMigrations
{
    public static readonly Assembly Assembly = typeof(EventStoreSqlServerMigrations).Assembly;

    public const string ResourcePrefix = "EventStore.SqlServer.Migrations.";
}
