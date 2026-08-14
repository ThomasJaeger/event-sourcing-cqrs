using EventSourcingCqrs.Migration.Demo.Legacy;
using Testcontainers.PostgreSql;
using Xunit;

namespace EventSourcingCqrs.Migration.Tests;

// A PostgreSQL container standing in for the CRUD-shaped legacy database, on the same
// postgres:16.6-alpine the reference system pins. The production LegacySchemaApplier runs in
// InitializeAsync, so the schema the demo ships is on the test path from the first run rather
// than duplicated in test setup. In the S1 RED the applier is a no-op placeholder, so the
// database comes up empty and the change-tracking test fails on the absent legacy.orders.
public sealed class LegacyDatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16.6-alpine")
        .WithUsername("esrcq")
        .WithPassword("esrcq")
        .WithDatabase("legacy")
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        await new LegacySchemaApplier().ApplyAsync(ConnectionString, CancellationToken.None);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
