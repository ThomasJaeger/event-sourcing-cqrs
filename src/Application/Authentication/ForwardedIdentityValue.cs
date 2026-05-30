using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Authentication;

// The single source of the X-Forwarded-Identity value format, so the Web signer and the Api reader
// agree on the bytes by construction rather than by a duplicated format string kept in sync by a
// test. The form is "{actorId:N};{role},{role}": the actor id in "N" form, a semicolon, then the
// roles comma-joined. An empty role set yields "{actorId:N};", the separator with an empty roles
// segment after it. HeaderForwardedIdentityReader parses this exact form, and the signature is
// computed over the verbatim string this produces.
public static class ForwardedIdentityValue
{
    public static string Format(Guid actorId, IReadOnlyCollection<Role> roles)
        => $"{actorId:N};{string.Join(',', roles)}";
}
