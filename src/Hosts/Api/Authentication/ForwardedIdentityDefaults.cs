namespace EventSourcingCqrs.Hosts.Api.Authentication;

// The scheme name and the header the forwarded-identity authentication reads. A single combined
// header carries the actor id and the upstream's role claim; the value format is
// "{actorId:N};{role},{role}" (the roles segment may be empty). The format is internal to the
// Web-to-Api hop and dev-only in P9.3a: P9.3b adds a signature segment the reader verifies.
public static class ForwardedIdentityDefaults
{
    public const string SchemeName = "ForwardedIdentity";

    public const string HeaderName = "X-Forwarded-Identity";
}
