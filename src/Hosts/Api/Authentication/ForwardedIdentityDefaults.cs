namespace EventSourcingCqrs.Hosts.Api.Authentication;

// The scheme name and the headers the forwarded-identity authentication reads. X-Forwarded-Identity
// carries the actor id and the upstream's role claim in the value format "{actorId:N};{role},{role}"
// (the roles segment may be empty). X-Forwarded-Identity-Signature carries the base64url HMAC-SHA256
// of that verbatim value under the shared secret (P9.3b). The signature rides in its own header so
// the value the reader parses stays byte-identical to the value that was signed, rather than folding
// a third segment into the "{actorId:N};{roles}" split. The format is internal to the Web-to-Api hop.
public static class ForwardedIdentityDefaults
{
    public const string SchemeName = "ForwardedIdentity";

    public const string HeaderName = "X-Forwarded-Identity";

    public const string SignatureHeaderName = "X-Forwarded-Identity-Signature";
}
