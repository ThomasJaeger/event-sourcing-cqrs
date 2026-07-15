namespace EventSourcingCqrs.Infrastructure.EventStore.Kurrent;

// The tuning surface for the KurrentDB subscription dispatch service.
public sealed class KurrentSubscriptionOptions
{
    // How many events the server scans between $all checkpoint-reached signals on the filtered
    // subscription. 1 means a checkpoint after every event the subscription observes, matched or
    // filtered, so the dispatch position never trails a filtered stretch by more than one event; raise
    // it to batch checkpoint writes at the cost of re-scanning more filtered events after a reconnect.
    public uint CheckpointInterval { get; init; } = 1;

    // How long to wait before resubscribing after the subscription faults, mirroring the outbox
    // processors' back-off-and-retry posture. The reconnect resumes from the stored checkpoint.
    public TimeSpan ReconnectBackoff { get; init; } = TimeSpan.FromSeconds(2);
}
