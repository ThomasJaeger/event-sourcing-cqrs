namespace EventSourcingCqrs.Infrastructure.EventStore.SqlServer;

// Mirrors SqlServerOutboxProcessorOptions so an operator who knows how to tune one queue knows how
// to tune the other (ADR 0017). A separate type rather than a shared one because the two queues'
// values may diverge.
public sealed class SqlServerDelayQueueProcessorOptions
{
    public int BatchSize { get; init; } = 100;
    public int MaxAttempts { get; init; } = 10;
    public int BaseSeconds { get; init; } = SqlServerDelayQueueRetryPolicy.BaseSeconds;
    public int CapSeconds { get; init; } = SqlServerDelayQueueRetryPolicy.CapSeconds;
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public Func<double> Jitter { get; init; } = Random.Shared.NextDouble;

    // The whole wake path, not a fallback. The PostgreSQL processor treats its timer as a backstop
    // behind a pg_notify trigger; SQL Server has no LISTEN/NOTIFY and PLAN.md's Phase 2 out-of-scope entry for engine-native SQL Server triggers scopes this
    // engine to polling, so this interval IS the dispatch-latency bound. There is no
    // NotificationChannelName here because there is no channel.
    //
    // The delay queue feels this less than the outbox does. Most rows are future-dated, so even on
    // PostgreSQL the notification mainly shortens latency for at-or-near-present rows and the timer
    // already carried the load (ADR 0017).
    public TimeSpan IdlePollInterval { get; init; } = TimeSpan.FromMilliseconds(500);
}
