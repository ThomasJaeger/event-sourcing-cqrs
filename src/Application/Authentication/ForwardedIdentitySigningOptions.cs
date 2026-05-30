namespace EventSourcingCqrs.Application.Authentication;

// The shared secret both hosts use to sign and verify the forwarded-identity header (P9.3b). The Web
// host signs the verbatim X-Forwarded-Identity value with this secret; the Api host verifies it. The
// secret is a plain configuration string read from one key on both hosts, never a serialized or
// persisted value.
//
// No validation here. Like the other options classes, the required-field contract is a constructor
// guard on the consumer (the signature verifier, and in the Web host the signer), which throws at
// startup when the composition root constructs it. The carrier holds the secret and gives nothing
// else a reason to read it.
public sealed class ForwardedIdentitySigningOptions
{
    public string Secret { get; init; } = "";
}
