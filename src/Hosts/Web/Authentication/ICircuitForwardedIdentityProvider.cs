namespace EventSourcingCqrs.Hosts.Web.Authentication;

// The circuit's current actor id, read at send time so the value reflects the principal of the
// circuit making the call. Scoped per Blazor Server circuit, so each circuit reads its own identity.
// This is why ApiClient signs through this provider rather than a DelegatingHandler: a handler is
// pooled across circuits and rotated on a timer, so it would attach one circuit's identity to
// another circuit's request.
public interface ICircuitForwardedIdentityProvider
{
    Task<Guid> GetActorIdAsync();
}
