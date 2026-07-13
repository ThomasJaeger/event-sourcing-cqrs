namespace EventSourcingCqrs.Infrastructure.EventStore.SqlServer;

// Pattern from Chapter 8: outbox dispatcher backoff. Full-jitter exponential per the AWS
// Architecture Blog's "Exponential Backoff and Jitter".
//
// Byte-identical curve to the PostgreSQL adapter's OutboxRetryPolicy, duplicated per ADR 0004
// rather than shared. Backoff is a dispatch policy, not an engine mechanic, so the two copies
// are free to diverge if one engine's dispatch ever needs a different curve; today they do not.
//
// MaxAttempts is not on this class because it drives the processor's quarantine branch, not the
// curve. See SqlServerOutboxProcessorOptions.
public sealed class SqlServerOutboxRetryPolicy
{
    public const int BaseSeconds = 1;
    public const int CapSeconds = 300;

    private readonly int _baseSeconds;
    private readonly int _capSeconds;

    public SqlServerOutboxRetryPolicy(int baseSeconds = BaseSeconds, int capSeconds = CapSeconds)
    {
        _baseSeconds = baseSeconds;
        _capSeconds = capSeconds;
    }

    public DateTimeOffset ComputeNextAttempt(int attemptCount, DateTimeOffset now, double jitter)
    {
        // attemptCount is the post-increment value. First retry passes attemptCount = 1.
        var rawDelaySeconds = Math.Min(Math.Pow(2, attemptCount - 1) * _baseSeconds, _capSeconds);
        var scaledSeconds = rawDelaySeconds * jitter;
        return now.AddSeconds(scaledSeconds);
    }
}
