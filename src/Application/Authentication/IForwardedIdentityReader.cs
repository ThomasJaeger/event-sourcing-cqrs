namespace EventSourcingCqrs.Application.Authentication;

// Reads the forwarded-identity claim off a request's header value and produces a ForwardedIdentity,
// or null when the header is absent or malformed. This is the validation seam: in P9.3a the
// implementation parses the dev header format without a signature check; P9.3b replaces or decorates
// it to verify a shared-secret signature before trusting the claim. The port takes the raw header
// string rather than a request type so it stays free of any host transport dependency.
public interface IForwardedIdentityReader
{
    ForwardedIdentity? Read(string? headerValue);
}
