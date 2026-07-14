using EventSourcingCqrs.Infrastructure.EventStore.SqlServer;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.SqlServer;

// Characterization, green on write. Duplicated from Postgres/OutboxRetryPolicyTests against the
// SQL Server copy of the curve, which ADR 0004 duplicates rather than shares.
//
// The provenance matters: these facts assert nothing that was not already true, and they found
// nothing. They exist because the two copies of the curve had tests on one copy only, and code
// duplicated per ADR 0004 with coverage on one side is code that drifts on the other. The
// composition defect in ServiceCollectionExtensionsTests is what that gap was hiding.
public class SqlServerOutboxRetryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 12, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    [InlineData(6, 32)]
    [InlineData(7, 64)]
    [InlineData(8, 128)]
    [InlineData(9, 256)]
    [InlineData(10, 300)]
    [InlineData(11, 300)]
    [InlineData(12, 300)]
    [InlineData(13, 300)]
    [InlineData(14, 300)]
    [InlineData(15, 300)]
    public void ComputeNextAttempt_follows_exponential_backoff_with_cap(int attemptCount, int expectedSeconds)
    {
        var policy = new SqlServerOutboxRetryPolicy();
        const double jitter = 1.0;

        var next = policy.ComputeNextAttempt(attemptCount, Now, jitter);

        next.Should().Be(Now.AddSeconds(expectedSeconds));
    }

    [Fact]
    public void ComputeNextAttempt_zero_jitter_returns_now()
    {
        var policy = new SqlServerOutboxRetryPolicy();

        var next = policy.ComputeNextAttempt(attemptCount: 5, Now, jitter: 0.0);

        next.Should().Be(Now);
    }
}
