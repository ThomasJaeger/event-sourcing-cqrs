using System.Reflection;

namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

// Migrations live as embedded .sql resources in this assembly under the
// LogicalName prefix "EventStore.Postgres.Migrations." (see the csproj). The
// migration runner lives in Infrastructure.Migrations.Postgres (engine-agnostic
// infrastructure, per ADR 0004), so it needs both the assembly handle and the
// resource prefix passed in. This class is the single owner of those two values.
public static class EventStorePostgresMigrations
{
    public static readonly Assembly Assembly = typeof(EventStorePostgresMigrations).Assembly;

    public const string ResourcePrefix = "EventStore.Postgres.Migrations.";
}
