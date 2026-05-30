using EventSourcingCqrs.Application.Authentication;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Hosts.Api.Authentication;

// Parses the dev forwarded-identity header value "{actorId:N};{role},{role}" into a
// ForwardedIdentity. Returns null when the value is absent, the actor id does not parse, or a role
// token is not a defined Role: a malformed claim fails closed rather than authenticating a partial
// or guessed identity.
//
// DELIBERATELY NOT PRODUCTION-COMPLETE. This reader trusts the header's contents without verifying
// any signature, so the Api host must not be exposed to untrusted callers carrying this header until
// P9.3b adds shared-secret signature validation at this seam. Until then the header is a same-trust-
// boundary dev mechanism, not an authentication credential.
public sealed class HeaderForwardedIdentityReader : IForwardedIdentityReader
{
    public ForwardedIdentity? Read(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return null;
        }

        var separator = headerValue.IndexOf(';');
        var actorSegment = separator < 0 ? headerValue : headerValue[..separator];
        var rolesSegment = separator < 0 ? string.Empty : headerValue[(separator + 1)..];

        if (!Guid.TryParseExact(actorSegment.Trim(), "N", out var actorId) || actorId == Guid.Empty)
        {
            return null;
        }

        var roles = new List<Role>();
        foreach (var token in rolesSegment.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<Role>(token, ignoreCase: false, out var role) || !Enum.IsDefined(role))
            {
                return null;
            }
            roles.Add(role);
        }

        return new ForwardedIdentity(actorId, roles);
    }
}
