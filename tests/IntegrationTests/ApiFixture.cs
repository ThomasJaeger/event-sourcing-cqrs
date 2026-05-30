using EventSourcingCqrs.IntegrationTests.Authentication;
using EventSourcingCqrs.TestInfrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests;

// Composes the Api host under test with a fresh migrated Postgres database. One
// container, one migrated database, and one WebApplicationFactory per test class;
// tests isolate by using unique aggregate ids rather than per-test databases. The
// Api host reads both connection strings from configuration, so both point at the
// one test database (v1 shares one database across the event store and read
// models). Reused by the /commands and /queries endpoint tests.
public sealed class ApiFixture : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.InitializeAsync();
        var connectionString = await _postgres.CreateMigratedDatabaseAsync();
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("EVENT_STORE_CONNECTION_STRING", connectionString);
            builder.UseSetting("READ_MODEL_CONNECTION_STRING", connectionString);
            // The host verifies the forwarded-identity signature against this secret; the test client
            // signs with the same constant, so both sides of the WebApplicationFactory agree (P9.3b).
            builder.UseSetting(
                "FORWARDED_IDENTITY_SIGNING_SECRET", ForwardedIdentityTestHeader.SigningSecret);
        });
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
