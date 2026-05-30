using System.Buffers.Text;
using EventSourcingCqrs.Application.Authentication;

namespace EventSourcingCqrs.Hosts.Web.Authentication;

// Signs the verbatim forwarded-identity value for the wire: the base64url HMAC-SHA256 under the
// shared secret, computed through the same ForwardedIdentitySigningKey the Api verifier uses (2a), so
// the Web signer and the Api verifier agree by construction. The signer holds the secret only through
// the shared key.
public sealed class ForwardedIdentitySigner
{
    private readonly ForwardedIdentitySigningKey _signingKey;

    public ForwardedIdentitySigner(ForwardedIdentitySigningKey signingKey)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        _signingKey = signingKey;
    }

    public string Sign(string identityValue)
        => Base64Url.EncodeToString(_signingKey.ComputeHash(identityValue));
}
