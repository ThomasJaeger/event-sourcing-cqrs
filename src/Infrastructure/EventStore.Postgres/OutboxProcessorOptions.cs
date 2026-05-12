namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

public sealed class OutboxProcessorOptions
{
    public int BatchSize { get; init; } = 100;
    public int MaxAttempts { get; init; } = 10;
    public int BaseSeconds { get; init; } = OutboxRetryPolicy.BaseSeconds;
    public int CapSeconds { get; init; } = OutboxRetryPolicy.CapSeconds;
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public Func<double> Jitter { get; init; } = Random.Shared.NextDouble;
}
