namespace EventSourcingCqrs.Application.Authentication;

// The two header names the forwarded-identity wire contract uses, single-sourced here so the Web
// signer and the Api verifier name the same headers by construction rather than by a per-host
// duplicate. X-Forwarded-Identity carries the value ForwardedIdentityValue.Format produces;
// X-Forwarded-Identity-Signature carries the base64url HMAC-SHA256 of that verbatim value under the
// shared secret (P9.3b). The signature rides in its own header so the value the reader parses stays
// byte-identical to the value that was signed. The scheme name is not here: it names the registered
// authentication scheme that only the Api host consumes, so it stays in ForwardedIdentityDefaults.
public static class ForwardedIdentityHeaders
{
    public const string HeaderName = "X-Forwarded-Identity";

    public const string SignatureHeaderName = "X-Forwarded-Identity-Signature";
}
