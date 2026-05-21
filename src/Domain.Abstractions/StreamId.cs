namespace EventSourcingCqrs.Domain.Abstractions;

// Typed stream identifier per ADR 0011. Wraps a string in the form
// {prefix}:{guid:N}, where the prefix names the stream's role (an aggregate
// type for aggregate streams, a process-manager category for PM streams) and
// the guid identifies the instance. The colon separates them; it appears in
// neither a prefix nor a Guid's N format, so parsing stays unambiguous even
// for hyphenated PM prefixes like "pm-order-fulfillment".
//
// The constructor is private. Every StreamId is built through ForAggregate,
// ForProcessManager, or Parse, all of which route through the one validating
// constructor, so a StreamId is always well-formed. Parse rejects malformed
// input loudly per ADR 0011. Prefix-family checks (is this a known aggregate
// or PM prefix) are a read-side routing concern that lives with that routing;
// this type validates structure only.
public sealed record StreamId
{
    public string Value { get; }

    private StreamId(string value)
    {
        if (!IsWellFormed(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid stream id; expected '{{prefix}}:{{guid:N}}'.",
                nameof(value));
        }
        Value = value;
    }

    // Aggregate prefix is the lowercased aggregate type name (Order -> "order").
    // Domain.Abstractions cannot reference the Domain aggregate types, so the
    // prefix derives from the generic type parameter's runtime name rather than
    // a curated type-to-prefix map.
    public static StreamId ForAggregate<TAggregate>(Guid id)
        where TAggregate : AggregateRoot
        => new($"{typeof(TAggregate).Name.ToLowerInvariant()}:{id:N}");

    // PM prefix is passed explicitly from StreamPrefixes. A process manager's
    // stream category is not its class name, so there is nothing to derive.
    public static StreamId ForProcessManager(string pmType, Guid id)
        => new($"{pmType}:{id:N}");

    public static StreamId Parse(string value) => new(value);

    public override string ToString() => Value;

    private static bool IsWellFormed(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }
        var colon = value.IndexOf(':');
        if (colon <= 0 || value.IndexOf(':', colon + 1) >= 0)
        {
            return false;
        }
        return Guid.TryParseExact(value[(colon + 1)..], "N", out _);
    }
}
