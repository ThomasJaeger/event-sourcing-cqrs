using System.Buffers.Text;
using System.Security.Cryptography;

namespace EventSourcingCqrs.Application.Authentication;

// Verifies the shared-secret signature a trusted upstream attaches to the forwarded-identity header
// (P9.3b). The Web host signs the verbatim X-Forwarded-Identity value with HMAC-SHA256 under a secret
// both hosts share and carries the result in X-Forwarded-Identity-Signature; this type recomputes the
// MAC through the shared ForwardedIdentitySigningKey and compares it in constant time. A request whose
// signature is absent, does not decode, or does not match fails verification, so the authentication
// handler rejects it before it parses an unverified claim. This closes the P9.3a posture where the
// unsigned header was trusted behind a trusted upstream: the signature is now the enforced credential.
//
// The secret guard lives in the signing key's constructor, which the composition root builds at
// startup, so a missing or under-length secret still fails fast as the container is built.
public sealed class ForwardedIdentitySignatureVerifier
{
    private readonly ForwardedIdentitySigningKey _signingKey;

    public ForwardedIdentitySignatureVerifier(ForwardedIdentitySigningKey signingKey)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        _signingKey = signingKey;
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

        return CryptographicOperations.FixedTimeEquals(presented, _signingKey.ComputeHash(identityValue));
    }
}
