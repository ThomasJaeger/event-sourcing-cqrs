namespace EventSourcingCqrs.IntegrationTests.Commands.Fulfillment;

internal static class InventorySetup
{
    public static async Task CreateAsync(HttpClient client, Guid inventoryId, string sku = "SKU-001")
    {
        var response = await client.PostCommandAsync(
            "CreateInventory",
            new { inventoryId, sku },
            idempotencyKey: Guid.NewGuid().ToString());
        response.EnsureSuccessStatusCode();
    }
}
