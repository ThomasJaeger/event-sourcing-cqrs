using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

public class NpgsqlReadModelConnectionFactoryTests
{
    [Fact]
    public async Task OpenConnectionAsync_after_DisposeAsync_throws_ObjectDisposedException()
    {
        // NpgsqlDataSource.Create does not open a connection until the first
        // OpenConnectionAsync, so a stub connection string is enough; this test
        // exercises the disposal contract, not connection behaviour.
        var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=stub");
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);

        await factory.DisposeAsync();

        // Proving the underlying data source rejects further use proves the
        // factory's disposal chained through to it; the factory itself does
        // nothing besides delegate to the data source.
        await factory.Invoking(f => f.OpenConnectionAsync(CancellationToken.None))
            .Should().ThrowAsync<ObjectDisposedException>();
    }
}
