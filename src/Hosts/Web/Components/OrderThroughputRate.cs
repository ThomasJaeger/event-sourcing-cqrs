using EventSourcingCqrs.Domain.Sales.ReadModels;

namespace EventSourcingCqrs.Hosts.Web.Components;

// The data-domain throughput reading: a windowed order count anchored on the newest bucket
// second, with no wall clock. The page derives the bucket age from this AsOfUtc against its
// injected TimeProvider, so the clock touches exactly one quantity (the age), not the count.
public sealed record ThroughputReading(long WindowedCount, TimeSpan Window, DateTime AsOfUtc);

public static class OrderThroughputRate
{
    // Folds the per-second buckets into a windowed count, measured back from the newest bucket
    // second rather than a wall clock, and reports that newest second as the as-of. Returns null
    // when there are no buckets so the page can render its empty state. Pattern from Chapter 13:
    // the read side keeps a bounded per-second history and the rate is a pure fold over it.
    public static ThroughputReading? Compute(IReadOnlyList<OrderThroughputRow> buckets, TimeSpan window)
    {
        if (buckets.Count == 0)
        {
            return null;
        }

        var asOfUtc = buckets.Max(bucket => bucket.SecondUtc);
        var windowStart = asOfUtc - window;
        var windowedCount = buckets
            .Where(bucket => bucket.SecondUtc >= windowStart)
            .Sum(bucket => bucket.Count);

        return new ThroughputReading(windowedCount, window, asOfUtc);
    }
}
