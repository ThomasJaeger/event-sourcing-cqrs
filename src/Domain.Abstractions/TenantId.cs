namespace EventSourcingCqrs.Domain.Abstractions;

// Typed tenant identifier: the shared-schema isolation discriminator that
// scopes every stream, event, and read-model row to a single tenant. Wraps a
// non-empty Guid.
//
// This is a deliberate exception to the convention that identifiers in this
// codebase travel as a raw Guid. Tenancy is a security boundary, so a tenant id
// confused with any other identifier is a cross-tenant leak. The type system
// closes that gap at compile time rather than leaving it to validators and
// tests, which is the cost the raw-Guid convention accepts everywhere a typed
// id is not warranted.
//
// The constructor is private. Every TenantId is built through From or Parse,
// both of which route through the one validating constructor, so a TenantId
// never wraps Guid.Empty. Parse rejects malformed input loudly.
public sealed record TenantId
{
    public Guid Value { get; }

    private TenantId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(value));
        }
        Value = value;
    }

    public static TenantId From(Guid value) => new(value);

    public static TenantId Parse(string value)
    {
        if (!Guid.TryParse(value, out var id))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid tenant id; expected a GUID.", nameof(value));
        }
        return new TenantId(id);
    }

    public override string ToString() => Value.ToString();
}
