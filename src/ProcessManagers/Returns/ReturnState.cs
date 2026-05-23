namespace EventSourcingCqrs.ProcessManagers.Returns;

// The workflow states for ReturnProcessManager. NotStarted is the explicit zero so
// a freshly-constructed PM does not read as RestockingInventory. The happy path is
// RestockingInventory -> VoidingPayment -> Completed; Stuck is the single terminal
// for any step failure (Decision 12). The Return PM has no compensation: unlike
// OrderFulfillment it does not unwind on failure, it halts at Stuck for human
// intervention, the deliberate contrast that makes it the smaller second example.
public enum ReturnState
{
    NotStarted,
    RestockingInventory,
    VoidingPayment,
    Completed,
    Stuck
}
