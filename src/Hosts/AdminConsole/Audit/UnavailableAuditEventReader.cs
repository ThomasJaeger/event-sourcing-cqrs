using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Hosts.AdminConsole.Audit;

// An unavailable provider never substitutes a whole-store scan for the operator read.
public sealed class UnavailableAuditEventReader(AuditExplorerAvailability availability) : IAuditEventReader
{
    public Task<AuditEventPage> ReadPageAsync(AuditEventFilter filter, long? upperPosition,
        long? beforePosition, int pageSize, CancellationToken ct)
        => throw new NotSupportedException(availability.UnavailableReason);
}
