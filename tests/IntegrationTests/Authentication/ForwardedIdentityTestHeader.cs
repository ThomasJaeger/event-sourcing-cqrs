using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using EventSourcingCqrs.Application.Authentication;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.IntegrationTests.Authentication;

// The default forwarded-identity the integration-test client sends so the now-gated /commands and
// /queries endpoints stay reachable for every existing command and query test. A test that
// exercises the unauthenticated path builds its own request and omits this header.
//
// P9.3b: the Api host verifies a shared-secret signature over the verbatim identity value, so the
// test client signs too. ApiFixture configures the host under test with SigningSecret, so the host's
// verifier and SignatureFor here compute the same MAC. SigningSecret is a test constant, not a real
// secret; it only has to clear the host's length floor and match on both sides.
internal static class ForwardedIdentityTestHeader
{
    // A fixed test actor. A fresh test database has no current-roles rows for it, so the principal
    // factory loads an empty role set; the authenticated-actor test asserts the actor reaches event
    // metadata, not the roles (role enforcement is a later commit).
    public static readonly Guid DefaultActorId = Guid.Parse("0a9f7c2e-4d6b-4c1a-9f3e-2b8d5e7a1c40");

    // Shared with ApiFixture, which sets it as FORWARDED_IDENTITY_SIGNING_SECRET on the host under
    // test. At least 32 characters so it clears the verifier's startup guard.
    public const string SigningSecret = "integration-test-forwarded-identity-secret";

    public static string Default { get; } = Build(DefaultActorId, Role.Admin);

    public static string Build(Guid actorId, params Role[] roles)
        => ForwardedIdentityValue.Format(actorId, roles);

    // The base64url HMAC-SHA256 of the verbatim identity value under SigningSecret: the same
    // computation the host's ForwardedIdentitySignatureVerifier performs, so a header signed here
    // verifies there.
    public static string SignatureFor(string identityValue)
        => Base64Url.EncodeToString(
            HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(SigningSecret), Encoding.UTF8.GetBytes(identityValue)));
}
