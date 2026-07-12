namespace EventSourcingCqrs.Domain.Abstractions;

// Reads every event carrying one correlation id, across all streams and all
// tenants, for the AdminConsole Correlation-ID Tracer (Chapter 17). The events
// table projects correlation_id out of the metadata JSONB as a STORED generated
// column and indexes it (migration 0001), so one query answers a whole trace
// with no join.
//
// The read is registry-free. It resolves no CLR payload type and consumes no
// event-type registry, so a row whose event type the reading host never
// registered still comes back, and the payload is handed over as the JSON text
// the store holds. That is what lets one read return aggregate rows and
// process-manager rows together: the two families resolve through separate
// registries (ADR 0013), and a typed read would have to choose one of them.
//
// Reading the global log by correlation is separate from the aggregate-lifecycle
// IEventStore contract, so it is its own port, the shape IEventStoreHeadPosition
// established.
public interface ICorrelationTraceReader
{
    // Returns at most maxRows rows carrying the given correlation id, ordered
    // ascending by global_position, oldest first. The implementation fetches
    // maxRows plus one row and reports the extra one as truncation, so a caller
    // learns the trace was cut without paying for a second count query.
    //
    // The caller owns maxRows. The seam that drives this reader holds the cap
    // constant; the port executes the bound it is handed.
    //
    // Guid.Empty is a legal argument here and matches the pre-fix process-manager
    // rows that ADR 0042 left in place. Refusing it as operator input is the
    // seam's job, upstream of this port.
    Task<CorrelationTraceResult> ReadTraceAsync(
        Guid correlationId,
        int maxRows,
        CancellationToken ct);
}
