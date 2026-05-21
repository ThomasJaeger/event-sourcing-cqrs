namespace EventSourcingCqrs.Domain.Abstractions;

public sealed class ConcurrencyException : Exception
{
    public StreamId StreamId { get; }
    public int ExpectedVersion { get; }

    public ConcurrencyException(StreamId streamId, int expectedVersion)
        : base($"Concurrency conflict on stream {streamId} at expected version {expectedVersion}.")
    {
        StreamId = streamId;
        ExpectedVersion = expectedVersion;
    }
}
