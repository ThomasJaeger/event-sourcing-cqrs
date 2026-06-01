namespace EventSourcingCqrs.Domain.Abstractions;

// Typed stream identifier per ADR 0011. Wraps a string in the form
// {prefix}:{guid:N} for the default tenant, or {prefix}:{tenant:N}:{guid:N} for
// any other tenant, where the prefix names the stream's role (an aggregate type
// for aggregate streams, a process-manager category for PM streams), the
// optional middle segment is the tenant, and the last guid identifies the
// instance. The colon separates the segments; it appears in neither a prefix nor
// a Guid's N format, so parsing stays unambiguous even for hyphenated PM prefixes
// like "pm-order-fulfillment". The prefix stays the leading segment in both
// forms, so read-side prefix-family routing is unaffected by tenancy.
//
// The constructor is private. Every StreamId is built through ForAggregate,
// ForProcessManager, or Parse, all of which route through the one validating
// constructor, so a StreamId is always well-formed. Parse rejects malformed
// input loudly per ADR 0011 and tolerates both the two-segment default-tenant
// form and the three-segment form. Prefix-family checks (is this a known
// aggregate or PM prefix) are a read-side routing concern that lives with that
// routing; this type validates structure only.
public sealed record StreamId
{
    public string Value { get; }

    private StreamId(string value)
    {
        if (!IsWellFormed(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid stream id; expected '{{prefix}}:{{guid:N}}' " +
                $"or '{{prefix}}:{{tenant:N}}:{{guid:N}}'.",
                nameof(value));
        }
        Value = value;
    }

    // Aggregate prefix is the lowercased aggregate type name (Order -> "order").
    // Domain.Abstractions cannot reference the Domain aggregate types, so the
    // prefix derives from the generic type parameter's runtime name rather than
    // a curated type-to-prefix map.
    public static StreamId ForAggregate<TAggregate>(TenantId tenant, Guid id)
        where TAggregate : AggregateRoot
        => new(Compose(typeof(TAggregate).Name.ToLowerInvariant(), tenant, id));

    // PM prefix is passed explicitly from StreamPrefixes. A process manager's
    // stream category is not its class name, so there is nothing to derive.
    public static StreamId ForProcessManager(string pmType, TenantId tenant, Guid id)
        => new(Compose(pmType, tenant, id));

    public static StreamId Parse(string value) => new(value);

    public override string ToString() => Value;

    // The default tenant is the absence of a tenant segment: its streams render
    // as the two-segment {prefix}:{guid:N}, so pre-tenancy and new default-tenant
    // streams share one form and rehydrate without a tenant lookup. A non-default
    // tenant adds the middle segment in compact N form, so the colon stays the
    // only separator and the tenant segment is shaped like the instance segment.
    private static string Compose(string prefix, TenantId tenant, Guid id)
        => tenant == WellKnownTenants.Default
            ? $"{prefix}:{id:N}"
            : $"{prefix}:{tenant.Value:N}:{id:N}";

    // Structure only: two segments is the default-tenant form {prefix}:{guid:N},
    // three is the tenant-scoped {prefix}:{tenant:N}:{guid:N}. The prefix must be
    // non-empty and each guid segment a compact-form guid. Prefix-family checks
    // stay a read-side routing concern, so they are not validated here.
    private static bool IsWellFormed(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }
        var segments = value.Split(':');
        if (segments.Length is < 2 or > 3 || segments[0].Length == 0)
        {
            return false;
        }
        for (var i = 1; i < segments.Length; i++)
        {
            if (!Guid.TryParseExact(segments[i], "N", out _))
            {
                return false;
            }
        }
        return true;
    }
}
