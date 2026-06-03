using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.IntegrationTests.Authentication;
using EventSourcingCqrs.TestInfrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests;

// Composes the Api host under test with a fresh migrated Postgres database. One
// container, one migrated database, and one WebApplicationFactory per test class;
// tests isolate by using unique aggregate ids rather than per-test databases. The
// Api host reads both connection strings from configuration, so both point at the
// one test database (v1 shares one database across the event store and read
// models). Reused by the /commands and /queries endpoint tests.
//
// The default actor is seeded with the Admin role straight into the current-roles read model, so the
// real principal factory loads it: with command authorization enforced (P9.4), every authenticated
// dispatch the command and query tests make is an authorization decision, and the default actor must
// hold a role that grants the command. A direct insert rather than the Workers-host bootstrap seed,
// because this fixture boots only the Api host. Admin holds every permission.
public sealed class ApiFixture : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.InitializeAsync();
        ConnectionString = await _postgres.CreateMigratedDatabaseAsync();
        await SeedRoleAsync(ForwardedIdentityTestHeader.DefaultActorId, Role.Admin);
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("EVENT_STORE_CONNECTION_STRING", ConnectionString);
            builder.UseSetting("READ_MODEL_CONNECTION_STRING", ConnectionString);
            // The host verifies the forwarded-identity signature against this secret; the test client
            // signs with the same constant, so both sides of the WebApplicationFactory agree (P9.3b).
            builder.UseSetting(
                "FORWARDED_IDENTITY_SIGNING_SECRET", ForwardedIdentityTestHeader.SigningSecret);
        });
    }

    // Inserts one current-roles row so the principal factory loads the role for this actor. Exposed so
    // a test can seed a second actor with a narrower role than the default administrator. Idempotent
    // through the table's (user_id, role) primary key, mirroring the projection's own upsert.
    public async Task SeedRoleAsync(Guid userId, Role role)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO read_models.current_user_roles (user_id, role) " +
            "VALUES (@user_id, @role) ON CONFLICT (user_id, role) DO NOTHING";
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("role", role.ToString());
        await command.ExecuteNonQueryAsync();
    }

    // Sets the current tenant on the host's async-local accessor for the duration of a direct
    // read-model seed, then restores it. A unit-of-work seed writes through the real projection
    // write path, which tags the row from the current tenant (ADR 0031); production sets that
    // tenant on the dispatch or replay set-point, so a direct seed sets it here. Mirrors the
    // command bus's set-then-restore so the tenant never leaks past the seed.
    public async Task SeedAsTenantAsync(TenantId tenant, Func<Task> seed)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(seed);
        var accessor = Factory.Services.GetRequiredService<ICurrentTenantAccessor>();
        var previous = accessor.Current;
        accessor.Current = tenant;
        try
        {
            await seed();
        }
        finally
        {
            accessor.Current = previous;
        }
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
