using EventSourcingCqrs.Hosts.AdminConsole.Components;

// Chapter 17: the AdminConsole operator host. This bootstrap stands up a runnable
// Blazor Server shell. It composes no data access and registers no authorization.
// The operational reader and the deny-by-default authorization posture are later
// Phase 12 slices, born at the pages that consume them.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
