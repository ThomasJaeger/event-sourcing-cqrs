namespace EventSourcingCqrs.Domain.Fulfillment.ReadModels;

// One row of the per-SKU inventory dashboard read model: the C# shape of
// read_models.inventory_dashboard. Keyed on InventoryId (1:1 with Sku per the
// Phase 4 Inventory aggregate). OnHandQuantity is the running sum of
// InventoryAdjusted deltas; ReservedQuantity is reserves minus releases.
public sealed record InventoryDashboardRow(
    Guid InventoryId,
    string Sku,
    int OnHandQuantity,
    int ReservedQuantity,
    DateTime LastUpdatedUtc);
