using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Migration.Demo.Cdc;

namespace EventSourcingCqrs.Migration.Demo.Scenarios;

// Shared read-back for the scenarios: they all key a stream on the legacy id through the same OrderIdFor
// mapping the events carry, and print what the event store now holds.
internal static class DemoNarration
{
    public static async Task PrintStreamAsync(DemoContext context, long legacyId, string label)
    {
        var events = await ReadStreamAsync(context, legacyId);
        var description = events.Count == 0
            ? "(empty)"
            : string.Join(", ", events.Select(e => e.GetType().Name));
        Console.WriteLine($"  {label} (legacy id {legacyId}) stream: {description}");
    }

    public static async Task<IReadOnlyList<IDomainEvent>> ReadStreamAsync(DemoContext context, long legacyId)
    {
        var streamId = StreamId.ForAggregate<Order>(
            WellKnownTenants.Default, LegacyChangeTranslator.OrderIdFor(legacyId));
        var envelopes = await context.EventStore.ReadStreamAsync(streamId, 0, CancellationToken.None);
        return envelopes.Select(e => e.Payload).ToList();
    }
}
