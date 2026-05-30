using System.Security.Claims;
using System.Text.Encodings.Web;
using EventSourcingCqrs.Application.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventSourcingCqrs.Hosts.Api.Authentication;

// Authenticates a request from the forwarded-identity header. Reads the header through
// IForwardedIdentityReader (the validation seam): no header is NoResult (anonymous, which a gated
// endpoint turns into a 401 challenge), a malformed header is an explicit failure, and a parsed
// claim becomes a ClaimsPrincipal carrying the actor id as the name identifier and the forwarded
// roles as role claims. The endpoint reads the actor id off this principal and loads the
// authoritative roles separately, so the role claims here are the upstream's assertion, not the
// authorization the command runs under.
//
// The reader does not verify a signature in P9.3a, so this scheme is only safe behind a trusted
// upstream until P9.3b adds the signature. See ADR 0028.
public sealed class ForwardedIdentityAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IForwardedIdentityReader _reader;

    public ForwardedIdentityAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IForwardedIdentityReader reader)
        : base(options, logger, encoder)
    {
        _reader = reader;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ForwardedIdentityDefaults.HeaderName, out var values))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = _reader.Read(values.ToString());
        if (identity is null)
        {
            return Task.FromResult(
                AuthenticateResult.Fail("The forwarded-identity header is malformed."));
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, identity.ActorId.ToString()),
        };
        foreach (var role in identity.Roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role.ToString()));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
