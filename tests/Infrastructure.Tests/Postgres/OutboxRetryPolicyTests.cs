using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

public class OutboxRetryPolicyTests
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
        var policy = new OutboxRetryPolicy();
        const double jitter = 1.0;

        var next = policy.ComputeNextAttempt(attemptCount, Now, jitter);

        next.Should().Be(Now.AddSeconds(expectedSeconds));
    }

    [Fact]
    public void ComputeNextAttempt_zero_jitter_returns_now()
    {
        var policy = new OutboxRetryPolicy();

        var next = policy.ComputeNextAttempt(attemptCount: 5, Now, jitter: 0.0);

        next.Should().Be(Now);
    }
}
