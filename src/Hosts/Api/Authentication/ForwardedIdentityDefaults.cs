namespace EventSourcingCqrs.Hosts.Api.Authentication;

// The forwarded-identity authentication scheme name. The Api host registers the scheme under this
// name, and only the Api host consumes it, so it stays here rather than in the shared wire contract.
// The header names and the value format live in Application's ForwardedIdentityHeaders and
// ForwardedIdentityValue, single-sourced because the Web signer and the Api reader both depend on
// them.
public static class ForwardedIdentityDefaults
{
    public const string SchemeName = "ForwardedIdentity";
}
