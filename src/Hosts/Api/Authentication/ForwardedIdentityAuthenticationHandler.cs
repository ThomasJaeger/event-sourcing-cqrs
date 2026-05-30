using System.Security.Claims;
using System.Text.Encodings.Web;
using EventSourcingCqrs.Application.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventSourcingCqrs.Hosts.Api.Authentication;

// Authenticates a request from the forwarded-identity header. No header is NoResult (anonymous, which
// a gated endpoint turns into a 401 challenge); a present header must carry a valid shared-secret
// signature, and on success the claim parses through IForwardedIdentityReader into a ClaimsPrincipal
// carrying the actor id as the name identifier and the forwarded roles as role claims. The endpoint
// reads the actor id off this principal and loads the authoritative roles separately, so the role
// claims here are the upstream's assertion, not the authorization the command runs under.
//
// P9.3b enforces the signature. The handler verifies it over the verbatim header value before it
// parses the claim, and hands the reader that same value, so an unsigned or wrongly-signed header
// fails authentication rather than reaching the reader. There is no unsigned-acceptance path: this
// closes the P9.3a posture where the header was trusted only behind a trusted upstream, because the
// signature is now the enforced credential. See ADR 0028.
public sealed class ForwardedIdentityAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IForwardedIdentityReader _reader;
    private readonly ForwardedIdentitySignatureVerifier _verifier;

    public ForwardedIdentityAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IForwardedIdentityReader reader,
        ForwardedIdentitySignatureVerifier verifier)
        : base(options, logger, encoder)
    {
        _reader = reader;
        _verifier = verifier;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ForwardedIdentityDefaults.HeaderName, out var values))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identityValue = values.ToString();

        // A present identity must be signed. An absent or invalid signature is a failure, never a
        // fall-through to the reader on an unverified value.
        var signature = Request.Headers.TryGetValue(
            ForwardedIdentityDefaults.SignatureHeaderName, out var signatureValues)
            ? signatureValues.ToString()
            : null;
        if (!_verifier.Verify(identityValue, signature))
        {
            return Task.FromResult(
                AuthenticateResult.Fail("The forwarded-identity signature is missing or invalid."));
        }

        // The string handed to the reader is the same verbatim value that was just verified.
        var identity = _reader.Read(identityValue);
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
