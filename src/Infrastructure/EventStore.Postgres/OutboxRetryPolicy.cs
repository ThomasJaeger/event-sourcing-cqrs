namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

// Pattern from Chapter 8: outbox dispatcher backoff.
// Full-jitter exponential per AWS Architecture Blog "Exponential Backoff and Jitter".
// MaxAttempts is not on this class because it drives the processor's quarantine
// branch, not this curve. See OutboxProcessorOptions.
public sealed class OutboxRetryPolicy
{
    public const int BaseSeconds = 1;
    public const int CapSeconds = 300;

    private readonly int _baseSeconds;
    private readonly int _capSeconds;

    public OutboxRetryPolicy(int baseSeconds = BaseSeconds, int capSeconds = CapSeconds)
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
