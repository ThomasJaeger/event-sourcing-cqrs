namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

public sealed class InventorySkuRegistryInitializationException(Exception innerException)
    : InvalidOperationException(
        "Historical inventory has conflicting SKU or inventory-id mappings. " +
        "Resolve those conflicts before enabling inventory creation.", innerException);
