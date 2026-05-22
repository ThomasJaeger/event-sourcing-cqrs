namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment;

// Per-line reservation status. Reserved on a successful ReserveInventory, Failed
// when the dispatch returns a CommandOutcome failure, Released after a
// compensating ReleaseInventory.
public enum ReservationLineStatus
{
    Reserved,
    Failed,
    Released
}
