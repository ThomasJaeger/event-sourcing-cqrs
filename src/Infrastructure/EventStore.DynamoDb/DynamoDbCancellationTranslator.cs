using Amazon.DynamoDBv2.Model;

namespace EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;

// What an append does about a cancelled transaction.
//
// Public while the translator that returns it stays internal, which is the narrowest shape that
// works: xUnit needs a public test class, and a public method cannot take an internal parameter, so
// the facts that pin the mapping cannot name a verdict the assembly hides. Nothing outside the
// adapter can produce one of these, because the only thing that returns them is internal.
public enum CancellationVerdict
{
    // Another writer took the position between the counter read and the write. Re-read and retry.
    Contention,

    // The stream version is taken. Terminal: retrying re-reads the counter and fails the same way.
    VersionConflict,

    // A reused event id, or a shape this translator does not recognize. Propagate untranslated.
    Propagate,
}

// The positional mapping from a cancelled transaction to what the append does next. Pure: a reason
// array and the append's event count in, a verdict out. No client, no I/O, no throwing.
//
// It is a seam of its own because the mapping is the adapter's most consequential branch and its
// least reachable one. Getting it wrong is silent: a version conflict read as contention retries
// forever, and a duplicate event id read as a version conflict arrives dressed as the concurrency
// contract, which a handler would retry as though the bug were a race. The live engine cannot
// produce these shapes on demand, so the facts that pin them construct reason arrays directly
// against this method, which is a pure function and needs no engine at all.
internal static class DynamoDbCancellationTranslator
{
    // Reasons come back one per transaction item, positionally, against the layout DynamoDbSchema
    // documents. The engine reports every failed condition, so several indices can fail at once and
    // the order of these checks is the precedence.
    public static CancellationVerdict Translate(
        IReadOnlyList<CancellationReason> reasons, int eventCount)
    {
        ArgumentNullException.ThrowIfNull(reasons);

        var failed = Enumerable.Range(0, reasons.Count)
            .Where(i => reasons[i].Code is not null and not "None")
            .ToList();

        // A reused event id must never surface as the concurrency contract: a handler catching
        // ConcurrencyException would retry a bug forever. Checked first, so an id collision wins
        // over anything else that failed alongside it.
        if (failed.Any(i => DynamoDbSchema.IsEventIdRowIndex(i, eventCount)))
        {
            return CancellationVerdict.Propagate;
        }

        // A taken version is terminal even if the counter lost its race in the same transaction:
        // the retry would draw a new position and collide on the same version again.
        if (failed.Any(i => DynamoDbSchema.IsEventRowIndex(i, eventCount)))
        {
            return CancellationVerdict.VersionConflict;
        }

        // The counter and the log row fail together, because the log row keys on the position the
        // counter handed out. Either one alone is the same story: someone else took the position.
        if (failed.Any(i =>
            i == DynamoDbSchema.CounterIndex || DynamoDbSchema.IsLogRowIndex(i, eventCount)))
        {
            return CancellationVerdict.Contention;
        }

        // A cancellation this translator does not recognize. Propagating beats guessing: a silent
        // retry on an unknown failure is how an append spins forever on something structural.
        return CancellationVerdict.Propagate;
    }
}
