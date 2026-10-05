using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.Fulfillment;

// A write-side reservation, independent of the eventually consistent SKU lookup projection.
// Claims survive an uncertain append outcome. Retrying the same tenant/SKU/id is allowed;
// assigning either identifier to a different partner throws DomainException.
public interface IInventorySkuRegistry
{
    Task ClaimAsync(TenantId tenant, Guid inventoryId, string sku, CancellationToken ct);
}
