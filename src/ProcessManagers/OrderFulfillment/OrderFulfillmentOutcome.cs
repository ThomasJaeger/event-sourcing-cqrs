namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment;

// The terminal outcome carried by OrderFulfillmentCompleted. Kept separate from
// OrderFulfillmentState so the terminal event declares its two-value semantics
// without reusing the workflow-state vocabulary for a forensic-only field.
public enum OrderFulfillmentOutcome
{
    Completed,
    Cancelled
}
