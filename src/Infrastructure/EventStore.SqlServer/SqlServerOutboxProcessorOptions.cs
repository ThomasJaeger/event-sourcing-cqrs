namespace EventSourcingCqrs.Infrastructure.EventStore.SqlServer;

public sealed class SqlServerOutboxProcessorOptions
{
    public int BatchSize { get; init; } = 100;
    public int MaxAttempts { get; init; } = 10;
    public int BaseSeconds { get; init; } = SqlServerOutboxRetryPolicy.BaseSeconds;
    public int CapSeconds { get; init; } = SqlServerOutboxRetryPolicy.CapSeconds;
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public Func<double> Jitter { get; init; } = Random.Shared.NextDouble;

    // The whole wake path, not a fallback. The PostgreSQL processor treats its idle timer as a
    // backstop behind LISTEN/NOTIFY; SQL Server has no LISTEN/NOTIFY, and PLAN.md:245 scopes the
    // SQL Server trigger mechanism to polling in v1, so this interval IS the dispatch latency
    // bound. There is no NotificationChannelName here because there is no channel.
    public TimeSpan IdlePollInterval { get; init; } = TimeSpan.FromMilliseconds(500);
}
