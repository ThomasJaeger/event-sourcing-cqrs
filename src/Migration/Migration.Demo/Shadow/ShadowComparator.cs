using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Migration.Demo.Cdc;

namespace EventSourcingCqrs.Migration.Demo.Shadow;

// Chapter 18: the shadow-mode comparator. Shadow mode runs the event-sourced path in parallel with the
// authoritative legacy write; this folds the emitted events into the legacy row shape and reports whether
// the two agree. Fold, then compare: OrderDrafted seeds a "new" order, OrderPlaced moves it to "paid" and
// pins the total from its Money amount, OrderCancelled removes it (a legacy delete removes the row).
// Then compare identity, customer mapping, status, and total; a mismatch names the diverged field.
//
// A pure function: no I/O, no clock, no state. Timestamps are ignored, since the two sides record them
// independently. The total is a placement-time domain fact, so a drafted-not-placed order carries none
// and total is compared only once placed. When the events project no order (empty, or a cancel) against a
// present legacy row, the mismatch names existence: the result type's free-text detail already carries
// that case, so no new absent-row shape was needed on it.
public static class ShadowComparator
{
    public static ShadowComparisonResult Compare(
        LegacyOrderState legacy, IReadOnlyList<IDomainEvent> events)
    {
        var projected = Fold(events);
        if (projected is null)
        {
            return ShadowComparisonResult.Mismatch(
                $"existence: the events project no order, but legacy order {legacy.Id} is present.");
        }

        if (LegacyChangeTranslator.OrderIdFor(legacy.Id) != projected.OrderId)
        {
            return ShadowComparisonResult.Mismatch("identity: the legacy id maps to a different stream.");
        }
        if (LegacyChangeTranslator.CustomerIdFor(legacy.CustomerName) != projected.CustomerId)
        {
            return ShadowComparisonResult.Mismatch("customer: the legacy name maps to a different id.");
        }
        if (!string.Equals(legacy.Status, projected.Status, StringComparison.Ordinal))
        {
            return ShadowComparisonResult.Mismatch(
                $"status: legacy is '{legacy.Status}', events project '{projected.Status}'.");
        }
        if (projected.Total is decimal total && legacy.Total != total)
        {
            return ShadowComparisonResult.Mismatch(
                $"total: legacy is {legacy.Total}, events project {total}.");
        }

        return ShadowComparisonResult.Match();
    }

    // Folds the emitted events into the legacy row shape, or null when they project no order: a stream
    // that never drafts, or one that ends in a cancel, has no row on the legacy side to match.
    private static ProjectedOrder? Fold(IReadOnlyList<IDomainEvent> events)
    {
        ProjectedOrder? projected = null;
        foreach (var @event in events)
        {
            switch (@event)
            {
                case OrderDrafted drafted:
                    projected = new ProjectedOrder(drafted.OrderId, drafted.CustomerId, "new", null);
                    break;
                case OrderPlaced placed:
                    if (projected is not null)
                    {
                        projected = projected with { Status = "paid", Total = placed.Total.Amount };
                    }

                    break;
                case OrderCancelled:
                    projected = null;
                    break;
            }
        }

        return projected;
    }

    private sealed record ProjectedOrder(Guid OrderId, Guid CustomerId, string Status, decimal? Total);
}

// The legacy order row the comparator reads, mirroring the S1 legacy.orders columns.
public sealed record LegacyOrderState(long Id, string CustomerName, string Status, decimal Total);

public sealed record ShadowComparisonResult(bool IsMatch, string Detail)
{
    public static ShadowComparisonResult Match() => new(true, string.Empty);

    public static ShadowComparisonResult Mismatch(string detail) => new(false, detail);
}
