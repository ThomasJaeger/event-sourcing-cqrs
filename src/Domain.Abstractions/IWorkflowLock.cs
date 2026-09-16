namespace EventSourcingCqrs.Domain.Abstractions;

// Serializes one order's decisions across hosts. Implementations must release on
// process failure and allow nested caused commands in the owning execution flow.
public interface IWorkflowLock
{
    Task RunAsync(TenantId tenant, Guid orderId, Func<Task> action, CancellationToken ct);
}
