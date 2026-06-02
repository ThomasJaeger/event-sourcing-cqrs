using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// Contract gate for read-model tenant isolation.
//
// The read path opens pooled connections from a plain NpgsqlDataSource built via
// NpgsqlDataSource.Create, the same construction NpgsqlReadModelConnectionFactory
// uses. Production sets no pool-reset key on the connection string, so Npgsql's
// default No Reset On Close=false applies and a connection is reset when it returns
// to the pool. This test pins that behaviour for the shipped Npgsql 9.0.3: a
// session variable set on one checkout must not survive into the next checkout of
// the same physical connection.
//
// MaxPoolSize=1 is an adversarial pin, not the production shape. It forces the
// second checkout to reuse the one physical connection so the reuse is
// deterministic, which makes a pass here stronger than the default pool the hosts
// run would surface. The test sets a sentinel GUC on connection A with a bare
// session-scope SET (mirroring the read path, which runs queries on an open
// connection with no per-query transaction), returns A to the pool, reuses it as
// connection B, and asserts B no longer sees the sentinel.
//
// This is the contract gate for whether row-level security keyed on a
// per-connection session variable is a safe defense-in-depth layer for read-model
// tenant isolation. The primary mechanism is the mandatory tenant-filter wrapper,
// decided separately. If a future Npgsql upgrade changes reset-on-return, this
// breaks the build rather than silently weakening that isolation.
public class NpgsqlSessionVariableResetContractTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public NpgsqlSessionVariableResetContractTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task A_session_variable_does_not_survive_a_pooled_connection_reuse()
    {
        // An empty database is enough: a custom GUC needs no table.
        var connStr = await _fixture.CreateDatabaseAsync();

        // Plain NpgsqlDataSource.Create as in production, with the pool pinned to a
        // single connection so connection B provably reuses connection A.
        var pinned = new NpgsqlConnectionStringBuilder(connStr)
        {
            MaxPoolSize = 1,
            MinPoolSize = 0,
        }.ConnectionString;

        await using var dataSource = NpgsqlDataSource.Create(pinned);

        await using (var a = await dataSource.OpenConnectionAsync(CancellationToken.None))
        {
            await using var set = a.CreateCommand();
            set.CommandText = "SET app.tenant_spike = 'tenant-A'";
            await set.ExecuteNonQueryAsync();
        }
        // A is back in the single-connection pool here.

        string? observedOnReuse;
        await using (var b = await dataSource.OpenConnectionAsync(CancellationToken.None))
        {
            await using var read = b.CreateCommand();
            read.CommandText = "SELECT current_setting('app.tenant_spike', true)";
            observedOnReuse = await read.ExecuteScalarAsync() as string;
        }

        observedOnReuse.Should().BeEmpty();
    }
}
