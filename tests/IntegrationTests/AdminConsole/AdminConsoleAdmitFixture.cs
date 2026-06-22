extern alias AdminConsoleHost;
using System.Security.Claims;
using System.Text.Encodings.Web;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.TestInfrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.AdminConsole;

// Boots the AdminConsole host over a migrated Testcontainers Postgres for the admit path (ADR 0040).
// The admit path queries the current-roles read model, so it needs a real database, unlike the deny
// spec. One seeded (AdminActorId, Admin) row plus a test authentication scheme that emits that actor
// as the NameIdentifier exercise the real fallback policy, AdminConsoleAccessHandler, and current-roles
// read end to end. The test scheme stands in for cookie issuance, which is the deferred login surface's
// concern (ADR 0040), so the seed actor id must equal the NameIdentifier the scheme emits.
public sealed class AdminConsoleAdmitFixture : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();

    public WebApplicationFactory<AdminConsoleHost::Program> Factory { get; private set; } = null!;

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.InitializeAsync();
        ConnectionString = await _postgres.CreateMigratedDatabaseAsync();
        await SeedRoleAsync(AdminTestAuthHandler.AdminActorId, Role.Admin);
        Factory = new WebApplicationFactory<AdminConsoleHost::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("READ_MODEL_CONNECTION_STRING", ConnectionString);
            // The Projection Status Dashboard's head reader needs the event-store connection; in v1 it
            // is the same migrated database, which carries event_store.events for the head read.
            builder.UseSetting("EVENT_STORE_CONNECTION_STRING", ConnectionString);
            // Override the default scheme with a test scheme that authenticates the seeded Admin actor,
            // so the fallback policy authenticates against it. ConfigureTestServices runs after the
            // host's own AddAuthentication, so its DefaultScheme wins.
            builder.ConfigureTestServices(services =>
                services.AddAuthentication(AdminTestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, AdminTestAuthHandler>(
                        AdminTestAuthHandler.SchemeName, _ => { }));
        });
    }

    // Mirrors ApiFixture.SeedRoleAsync: one current-roles row keyed on the actor, storing the role as
    // its enum name, the shape GetRolesForUserAsync parses back.
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

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

// A test authentication scheme that authenticates every request as the seeded Admin actor, carrying
// the actor id as the NameIdentifier the AdminConsoleAccessHandler reads. Registered as the default
// scheme through ConfigureTestServices so the fallback policy authenticates against it. It exercises
// the authorization path while standing in for cookie issuance and validation, which are the deferred
// login surface's concern (ADR 0040).
internal sealed class AdminTestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "AdminConsoleAdmitTest";
    public static readonly Guid AdminActorId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    public AdminTestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, AdminActorId.ToString()) };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
