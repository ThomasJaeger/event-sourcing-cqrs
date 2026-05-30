using System.Security.Cryptography;
using System.Text;

namespace EventSourcingCqrs.Application.Authentication;

// The shared HMAC key both sides of the forwarded-identity hop compute with: the Web signer and the
// Api verifier each construct one from the same secret, so a header signed on one side verifies on
// the other by construction. The secret is guarded in the constructor, so a missing or under-length
// secret throws as the composition root builds the key at startup, not on the first request.
//
// The carrier is passed by value rather than through IOptions<T>: the key is always constructed by
// hand at the composition root, so the options wrapper would buy nothing and would pull an options
// package onto the Application layer that nothing else there needs.
public sealed class ForwardedIdentitySigningKey
{
    private readonly byte[] _key;

    public ForwardedIdentitySigningKey(ForwardedIdentitySigningOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var secret = options.Secret;
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

    // The raw HMAC-SHA256 of the value under the shared secret, not base64url-encoded. The signer
    // base64url-encodes this for the wire; the verifier compares it against the decoded presented
    // signature in constant time.
    public byte[] ComputeHash(string value)
        => HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value));
}
