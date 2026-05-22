namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

// Mirrors OutboxProcessorOptions so an operator who knows how to tune one queue
// knows how to tune the other (ADR 0017). Separate type rather than a shared one
// because the two queues' values may diverge.
public sealed class DelayQueueProcessorOptions
{
    public int BatchSize { get; init; } = 100;
    public int MaxAttempts { get; init; } = 10;
    public int BaseSeconds { get; init; } = DelayQueueRetryPolicy.BaseSeconds;
    public int CapSeconds { get; init; } = DelayQueueRetryPolicy.CapSeconds;
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public Func<double> Jitter { get; init; } = Random.Shared.NextDouble;

    // Fallback bound on dispatch latency when the LISTEN/NOTIFY signal is
    // unavailable. The delay queue leans on the timer more than the outbox does:
    // most rows are future-dated, so the notification mainly shortens latency for
    // at-or-near-present rows (ADR 0017).
    public TimeSpan IdlePollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    // PostgreSQL LISTEN channel name. Must match the channel the
    // notify_delayed_command_pending trigger publishes to in migration 0008.
    public string NotificationChannelName { get; init; } = "delayed_commands_pending";
}
