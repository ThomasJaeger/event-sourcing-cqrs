using EventSourcingCqrs.Hosts.Web.Authentication;

namespace EventSourcingCqrs.Hosts.Web.Tests.TestDoubles;

/// <summary>
/// Settable ICircuitForwardedIdentityProvider for rendered page tests. The page
/// under test subscribes under the actor id this stub returns. ThrowUnavailable
/// switches GetActorIdAsync to throw ForwardedIdentityUnavailableException, the
/// fail-closed shape the real provider throws when the circuit's principal
/// carries no usable identity.
/// </summary>
internal sealed class StubCircuitIdentityProvider : ICircuitForwardedIdentityProvider
{
    private bool unavailable;

    public Guid ActorId { get; set; } = Guid.NewGuid();

    public void ThrowUnavailable() => unavailable = true;

    public Task<Guid> GetActorIdAsync()
        => unavailable
            ? Task.FromException<Guid>(new ForwardedIdentityUnavailableException())
            : Task.FromResult(ActorId);
}
