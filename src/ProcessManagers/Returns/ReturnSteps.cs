namespace EventSourcingCqrs.ProcessManagers.Returns;

// Idempotency-key step names for the Return workflow's dispatches. Parallel to
// OrderFulfillmentSteps; the restock step takes a per-line sub-id, the void step
// does not.
internal static class ReturnSteps
{
    public const string Restock = "restock";
    public const string VoidPayment = "void-payment";
}
