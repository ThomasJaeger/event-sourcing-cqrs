using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Hosts.Web.Components;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Components;

// RED #7 (P11.12b): the windowed per-minute rate the throughput meter derives from the
// retained per-second buckets. OrderThroughputRate.Compute is the compile-enabling placeholder
// that throws NotImplementedException; these specs pin the windowing contract before GREEN.
// The 60-second window is per-minute and sits inside the projection's 300-second bucket-retention
// span (OrderThroughputProjection.RetentionWindow), so the buckets the reader returns always cover it.
public sealed class OrderThroughputRateTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private static readonly DateTime Anchor = new(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc);

    private static OrderThroughputRow Bucket(int secondsBeforeAnchor, long count)
        => new(Anchor.AddSeconds(-secondsBeforeAnchor), count);

    [Fact]
    public void Compute_over_an_empty_bucket_list_returns_null()
    {
        var reading = OrderThroughputRate.Compute(Array.Empty<OrderThroughputRow>(), Window);

        reading.Should().BeNull();
    }

    [Fact]
    public void Compute_sums_the_buckets_within_the_window_and_anchors_the_as_of_to_the_newest_second()
    {
        var buckets = new[] { Bucket(30, 7), Bucket(0, 5) };

        var reading = OrderThroughputRate.Compute(buckets, Window);

        reading!.WindowedCount.Should().Be(12);
        reading.AsOfUtc.Should().Be(Anchor);
    }

    [Fact]
    public void Compute_excludes_a_bucket_older_than_the_window_from_the_count()
    {
        var buckets = new[] { Bucket(61, 100), Bucket(30, 7), Bucket(0, 5) };

        var reading = OrderThroughputRate.Compute(buckets, Window);

        reading!.WindowedCount.Should().Be(12);
    }

    [Fact]
    public void Compute_anchors_the_as_of_to_the_max_second_regardless_of_list_order()
    {
        var buckets = new[] { Bucket(0, 5), Bucket(30, 7), Bucket(10, 3) };

        var reading = OrderThroughputRate.Compute(buckets, Window);

        reading!.AsOfUtc.Should().Be(Anchor);
    }
}
