using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace EventSourcingCqrs.Hosts.Web.Tests.TestDoubles;

// A test AuthenticationStateProvider whose principal can be set after construction, so a test can give
// each DI scope its own circuit identity. This mirrors how the framework seeds the real circuit's
// provider from the cookie principal; the tests substitute it for that framework seeding.
internal sealed class MutableAuthenticationStateProvider : AuthenticationStateProvider
{
    private AuthenticationState _state = new(new ClaimsPrincipal(new ClaimsIdentity()));

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(_state);

    public void SetActor(Guid actorId) => SetNameIdentifier(actorId.ToString());

    public void SetNameIdentifier(string value) =>
        _state = new AuthenticationState(new ClaimsPrincipal(
            new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, value) }, "Test")));
}
