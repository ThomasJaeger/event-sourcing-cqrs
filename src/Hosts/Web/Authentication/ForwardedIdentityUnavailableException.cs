namespace EventSourcingCqrs.Hosts.Web.Authentication;

// Thrown when the Web circuit has no established identity at the moment ApiClient is about to
// dispatch. Fail-closed: the Web host signs every forwarded request with the circuit's actor, so a
// request with no identity must not leave the client unsigned or anonymous. Commit 1 made the Api
// host reject unsigned requests; this keeps the Web host from sending one in the first place.
public sealed class ForwardedIdentityUnavailableException : Exception
{
    public ForwardedIdentityUnavailableException()
        : base("The circuit has no established identity to forward.")
    {
    }
}
