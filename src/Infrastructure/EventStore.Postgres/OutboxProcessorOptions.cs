namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

public sealed class OutboxProcessorOptions
{
    public int BatchSize { get; init; } = 100;
    public int MaxAttempts { get; init; } = 10;
    public int BaseSeconds { get; init; } = OutboxRetryPolicy.BaseSeconds;
    public int CapSeconds { get; init; } = OutboxRetryPolicy.CapSeconds;
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public Func<double> Jitter { get; init; } = Random.Shared.NextDouble;

    // Fallback bound on dispatch latency when the LISTEN/NOTIFY signal is
    // unavailable (the listener is reconnecting, or its trigger has been
    // disabled in a test). Promoted from a constant so listener tests can
    // raise it to a value high enough that any wake faster than the timer
    // must be a notification. Production default keeps a 500ms upper bound.
    public TimeSpan IdlePollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    // PostgreSQL LISTEN channel name. Must match the channel the
    // notify_outbox_pending trigger function publishes to in migration 0005.
    // A host overriding either side has to keep both in sync.
    public string NotificationChannelName { get; init; } = "outbox_pending";
}
