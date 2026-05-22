namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

// Delay-queue dispatcher backoff. Identical full-jitter exponential shape to
// OutboxRetryPolicy (base 1s, cap 5min): a deliberate concrete duplicate rather
// than a shared abstraction (ADR 0017). Each processor reads end-to-end without
// an indirection, the stable outbox stays untouched, and the two retry curves
// can diverge independently if command dispatch ever wants a different tolerance
// than event delivery. A shared policy is the right extraction at that
// divergence point, not before it.
public sealed class DelayQueueRetryPolicy
{
    public const int BaseSeconds = 1;
    public const int CapSeconds = 300;

    private readonly int _baseSeconds;
    private readonly int _capSeconds;

    public DelayQueueRetryPolicy(int baseSeconds = BaseSeconds, int capSeconds = CapSeconds)
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
