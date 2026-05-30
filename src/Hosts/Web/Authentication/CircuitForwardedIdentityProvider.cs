using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace EventSourcingCqrs.Hosts.Web.Authentication;

// Reads the circuit's actor id from the authentication state the cookie principal established. The
// .NET 10 framework seeds the InteractiveServer circuit's AuthenticationStateProvider from the
// authenticated HttpContext.User at circuit start, so reading the name-identifier claim here yields
// the logged-in actor. Fail-closed: a missing claim, an unparseable one, or the empty Guid throws,
// so ApiClient never sends an unsigned or anonymous request.
internal sealed class CircuitForwardedIdentityProvider : ICircuitForwardedIdentityProvider
{
    private readonly AuthenticationStateProvider _authenticationStateProvider;

    public CircuitForwardedIdentityProvider(AuthenticationStateProvider authenticationStateProvider)
    {
        ArgumentNullException.ThrowIfNull(authenticationStateProvider);
        _authenticationStateProvider = authenticationStateProvider;
    }

    public async Task<Guid> GetActorIdAsync()
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync();
        var subject = state.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(subject, out var actorId) || actorId == Guid.Empty)
        {
            throw new ForwardedIdentityUnavailableException();
        }
        return actorId;
    }
}
