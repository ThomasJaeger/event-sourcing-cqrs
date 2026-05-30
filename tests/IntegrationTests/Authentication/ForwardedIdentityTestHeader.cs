using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.IntegrationTests.Authentication;

// The default forwarded-identity the integration-test client sends so the now-gated /commands and
// /queries endpoints stay reachable for every existing command and query test. A test that
// exercises the unauthenticated path builds its own request and omits this header.
internal static class ForwardedIdentityTestHeader
{
    // A fixed test actor. A fresh test database has no current-roles rows for it, so the principal
    // factory loads an empty role set; the authenticated-actor test asserts the actor reaches event
    // metadata, not the roles (role enforcement is a later commit).
    public static readonly Guid DefaultActorId = Guid.Parse("0a9f7c2e-4d6b-4c1a-9f3e-2b8d5e7a1c40");

    public static string Default { get; } = Build(DefaultActorId, Role.Admin);

    public static string Build(Guid actorId, params Role[] roles)
        => $"{actorId:N};{string.Join(',', roles)}";
}
