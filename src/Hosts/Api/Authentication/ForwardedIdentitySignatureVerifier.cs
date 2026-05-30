using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using EventSourcingCqrs.Application.Authentication;
using Microsoft.Extensions.Options;

namespace EventSourcingCqrs.Hosts.Api.Authentication;

// Verifies the shared-secret signature a trusted upstream attaches to the forwarded-identity header
// (P9.3b). The Web host signs the verbatim X-Forwarded-Identity value with HMAC-SHA256 under a secret
// both hosts share and carries the result in X-Forwarded-Identity-Signature; this type recomputes the
// MAC and compares it in constant time. A request whose signature is absent, does not decode, or does
// not match fails verification, so the authentication handler rejects it before it parses an
// unverified claim. This closes the P9.3a posture where the unsigned header was trusted behind a
// trusted upstream: the signature is now the enforced credential.
//
// The secret is guarded in the constructor so a missing or under-length secret throws when the
// composition root constructs this verifier at startup, not on the first request.
public sealed class ForwardedIdentitySignatureVerifier
{
    private readonly byte[] _key;

    public ForwardedIdentitySignatureVerifier(IOptions<ForwardedIdentitySigningOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var secret = options.Value.Secret;
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException("FORWARDED_IDENTITY_SIGNING_SECRET is not set.");
        }
        if (secret.Length < 32)
        {
            throw new InvalidOperationException(
                "FORWARDED_IDENTITY_SIGNING_SECRET must be at least 32 characters.");
        }

        // The MAC key is the secret's UTF-8 bytes. A deployment that configures a non-ASCII secret
        // should switch the length guard above to Encoding.UTF8.GetByteCount so the 32-byte floor
        // holds on the key bytes rather than the character count.
        _key = Encoding.UTF8.GetBytes(secret);
    }

    // True when signatureValue is the base64url HMAC-SHA256 of identityValue under the shared secret.
    // An absent or non-base64url signature returns false rather than throwing, so a malformed header
    // is a clean authentication failure, not a 500.
    public bool Verify(string identityValue, string? signatureValue)
    {
        if (string.IsNullOrEmpty(signatureValue))
        {
            return false;
        }

        byte[] presented;
        try
        {
            presented = Base64Url.DecodeFromChars(signatureValue);
        }
        catch (FormatException)
        {
            return false;
        }

        var computed = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(identityValue));
        return CryptographicOperations.FixedTimeEquals(presented, computed);
    }
}
