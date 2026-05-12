namespace EventSourcingCqrs.Domain.Abstractions;

public sealed class ConcurrencyException : Exception
{
    public Guid StreamId { get; }
    public int ExpectedVersion { get; }

    public ConcurrencyException(Guid streamId, int expectedVersion)
        : base($"Concurrency conflict on stream {streamId} at expected version {expectedVersion}.")
    {
        StreamId = streamId;
        ExpectedVersion = expectedVersion;
    }
}
