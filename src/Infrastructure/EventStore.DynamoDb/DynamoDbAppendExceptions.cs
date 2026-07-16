namespace EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;

// Refused before any write, because the append cannot be expressed as one transaction.
// TransactWriteItems takes at most 100 items, measured against the live engine: 101 items are
// rejected with "Member must have length less than or equal to 100" (ValidationException). This
// adapter spends 1 item on the position counter and 3 per event (the event row, the log row, the
// event-id row), so an append of n events costs 1 + 3n and the ceiling is 33 events.
//
// The guard exists so the limit surfaces as this adapter's own named failure at the boundary that
// owns it, rather than as a ValidationException from the SDK that reads like a bug in the caller.
// Splitting a large append across transactions is not on the table: it would break the atomicity
// the port promises.
public sealed class DynamoDbAppendTooLargeException : Exception
{
    public int EventCount { get; }
    public int ItemCount { get; }
    public int ItemLimit { get; }

    public DynamoDbAppendTooLargeException(int eventCount, int itemCount, int itemLimit)
        : base($"Appending {eventCount} events needs {itemCount} transaction items " +
               $"(1 counter + 3 per event), and DynamoDB allows {itemLimit}. " +
               $"The ceiling is {(itemLimit - 1) / 3} events per append.")
    {
        EventCount = eventCount;
        ItemCount = itemCount;
        ItemLimit = itemLimit;
    }
}

// Every attempt lost the race for a global position. The append is correct and retryable; the
// engine simply serializes position assignment through one counter row, and under enough
// concurrent writers a caller can lose repeatedly.
//
// The cap is here so a losing writer fails loudly instead of spinning forever. A spike against the
// live engine measured 12 concurrent writers on distinct streams landing 300 appends in 1667
// attempts, with one writer retrying 42 times: contention on this engine is the normal case, not
// the exception, which is why the cap is generous and why the exception names it.
public sealed class DynamoDbPositionContentionException : Exception
{
    public int Attempts { get; }

    public DynamoDbPositionContentionException(int attempts, Exception innerException)
        : base($"Could not draw a global position after {attempts} attempts. Every attempt lost " +
               $"the counter's conditional write to another writer.",
            innerException)
        => Attempts = attempts;
}
