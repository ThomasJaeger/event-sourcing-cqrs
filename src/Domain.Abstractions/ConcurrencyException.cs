namespace EventSourcingCqrs.Domain.Abstractions;

public sealed class ConcurrencyException : Exception
{
    public Guid StreamId { get; }
    public int ExpectedVersion { get; }
    public int ActualVersion { get; }

    public ConcurrencyException(Guid streamId, int expectedVersion, int actualVersion)
        : base($"Concurrency conflict on stream {streamId}: expected version {expectedVersion}, found {actualVersion}.")
    {
        StreamId = streamId;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }
}
