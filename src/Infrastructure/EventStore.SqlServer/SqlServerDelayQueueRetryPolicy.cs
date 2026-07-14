namespace EventSourcingCqrs.Infrastructure.EventStore.SqlServer;

// Full-jitter exponential backoff for delayed-command dispatch (ADR 0017). A deliberate concrete
// duplicate of SqlServerOutboxRetryPolicy, exactly as the PostgreSQL adapter keeps two copies of
// the same curve: the two queues' backoff may need to diverge, and a shared type would make that a
// breaking change instead of an edit.
public sealed class SqlServerDelayQueueRetryPolicy
{
    public const int BaseSeconds = 1;
    public const int CapSeconds = 300;

    private readonly int _baseSeconds;
    private readonly int _capSeconds;

    public SqlServerDelayQueueRetryPolicy(int baseSeconds = BaseSeconds, int capSeconds = CapSeconds)
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
