using EventSourcingCqrs.Application;
using EventSourcingCqrs.Hosts.AdminConsole.Authorization;
using EventSourcingCqrs.Hosts.AdminConsole.Components;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Projections.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

// Chapter 17: the AdminConsole operator host. A Blazor Server host that fails closed (ADR 0040): a
// host-level fallback policy gates every route, so an unauthenticated request is challenged with a
// redirect to the login path. The interactive login surface is a later slice, so that path does not
// resolve yet; the redirect is the declared fail-closed interim.
var builder = WebApplication.CreateBuilder(args);

// Validate the DI graph on build in every environment. The default builder validates only in
// Development; making it unconditional fails the host closed at startup on a missing or
// over-provisioned registration rather than surfacing the defect at the first operator action, the
// same fail-closed posture as the throw-on-missing connection-string guards below.
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// The deny path's two dependencies: the permission authorizer and the current-roles read the handler
// resolves roles through. The console composes focused read registrations rather than the full
// command, event-store, or projection stacks. Throw-on-missing
// at startup, the same guard the Web host uses, so a misconfigured deployment fails to boot rather
// than failing the first authorization decision.
var readModelConnectionString = builder.Configuration["READ_MODEL_CONNECTION_STRING"]
    ?? throw new InvalidOperationException("READ_MODEL_CONNECTION_STRING is not set.");
builder.Services.AddPermissionAuthorization();
builder.Services.AddCurrentRolesReadModel(options => options.ConnectionString = readModelConnectionString);

// The Projection Status Dashboard reads projection lag in process (ADR 0040): the head of the event
// stream minus each projection's checkpoint. It composes the reader's three ports focused, with no
// read-model or event-store over-provisioning. The checkpoint store comes from AddCurrentRolesReadModel
// above; AddEventStoreHeadPosition adds only the head read and its connection, not the full event store;
// AddProjectionRoster adds the name-only projection set. Throw-on-missing for the event-store
// connection, the same guard the read-model connection uses.
var eventStoreConnectionString = builder.Configuration["EVENT_STORE_CONNECTION_STRING"]
    ?? throw new InvalidOperationException("EVENT_STORE_CONNECTION_STRING is not set.");
builder.Services.AddEventStoreHeadPosition(eventStoreConnectionString);
builder.Services.AddProjectionRoster();
builder.Services.AddSingleton<ProjectionLagReader>();

// Cookie authentication for the operator. The cookie is HttpOnly and Secure-always, so the host
// requires an https endpoint. An unauthenticated request is challenged with a redirect to LoginPath.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = ".EventSourcingCqrs.AdminConsole.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.LoginPath = "/login";
    });

// Deny-by-default (ADR 0040): the fallback policy gates every endpoint that carries no other
// authorization metadata, so a page added without an attribute is denied rather than exposed. The
// AdminConsoleAccessHandler satisfies the requirement only for an operator whose roles grant console
// access.
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .AddRequirements(new AdminConsoleAccessRequirement())
        .Build());
builder.Services.AddSingleton<IAuthorizationHandler, AdminConsoleAccessHandler>();
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

// Authentication and authorization run before the component endpoints, so the fallback policy
// challenges an unauthenticated request before it reaches a page. HttpsRedirection is first: the
// Secure-always cookie is only sent over https.
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// Exposed so the IntegrationTests project's WebApplicationFactory can boot this composition in-memory.
public partial class Program { }
