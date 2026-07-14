using EventSourcingCqrs.Infrastructure.EventStore.SqlServer;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.SqlServer;

// Characterization, green on write. Duplicated from Postgres/DelayQueueRetryPolicyTests against
// the SQL Server copy of the curve. Same provenance as SqlServerOutboxRetryPolicyTests: the
// duplicated curve had coverage on one copy only.
public class SqlServerDelayQueueRetryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 21, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    [InlineData(9, 256)]
    [InlineData(10, 300)]
    [InlineData(15, 300)]
    public void ComputeNextAttempt_follows_exponential_backoff_with_cap(
        int attemptCount, int expectedSeconds)
    {
        var policy = new SqlServerDelayQueueRetryPolicy();
        const double jitter = 1.0;

        var next = policy.ComputeNextAttempt(attemptCount, Now, jitter);

        next.Should().Be(Now.AddSeconds(expectedSeconds));
    }

    [Fact]
    public void ComputeNextAttempt_zero_jitter_returns_now()
    {
        var policy = new SqlServerDelayQueueRetryPolicy();

        var next = policy.ComputeNextAttempt(attemptCount: 5, Now, jitter: 0.0);

        next.Should().Be(Now);
    }
}
