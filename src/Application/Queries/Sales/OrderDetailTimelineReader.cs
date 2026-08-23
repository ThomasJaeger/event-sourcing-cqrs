using System.Text.Json;

namespace EventSourcingCqrs.Application.Queries.Sales;

// Reads one event's payload back out of an order's timeline.
//
// The fulfillment process manager mints the payment id and the shipment id itself, so a caller
// that placed an order never sees either one. No read model maps an order to its shipment: the
// two mapping tables are keyed the other way and are private to their projection. The timeline
// is the one public surface carrying the ids, because every observed event is recorded there
// with its full payload.
//
// The rows arrive in the order the store returned them, ascending by global position, and this
// type takes the first match in that order without re-sorting. The ordering is the store's
// invariant rather than this type's, and asserting it here would let the two drift apart while
// both looked correct.
//
// The type check before deserialising is load-bearing. The shared options skip unmapped members
// and leave absent ones at their defaults, so reading one event's payload into another event's
// type would yield an instance whose every field defaulted rather than a fault. Refusing is how
// a caller learns it asked for a pair that does not go together.
public static class OrderDetailTimelineReader
{
    // Returns the payload of the first timeline row carrying the given event type, deserialised
    // to TPayload. Throws when no row carries it, when the requested type is not that event's
    // type, or when the payload deserialises to nothing.
    public static TPayload ReadFirst<TPayload>(
        OrderDetailView view,
        string eventType,
        JsonSerializerOptions options)
        where TPayload : class
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentException.ThrowIfNullOrEmpty(eventType);
        ArgumentNullException.ThrowIfNull(options);

        if (!string.Equals(typeof(TPayload).Name, eventType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"A '{eventType}' timeline row cannot be read as '{typeof(TPayload).Name}'. " +
                "The requested type has to be the event type the row carries.");
        }

        foreach (var row in view.Timeline)
        {
            if (!string.Equals(row.EventType, eventType, StringComparison.Ordinal))
            {
                continue;
            }

            // The caller's options, never a fresh instance: the payload was written with the
            // event store's shared options and reads back only under the same ones.
            return JsonSerializer.Deserialize<TPayload>(row.Payload, options)
                ?? throw new InvalidOperationException(
                    $"The '{eventType}' timeline row deserialised to nothing.");
        }

        throw new InvalidOperationException(
            $"The order's timeline carries no '{eventType}' row.");
    }
}
