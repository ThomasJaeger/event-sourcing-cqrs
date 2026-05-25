namespace EventSourcingCqrs.IntegrationTests.Commands.Sales;

internal static class OrderSetup
{
    public static async Task DraftAsync(HttpClient client, Guid orderId)
    {
        var response = await client.PostCommandAsync(
            "DraftOrder",
            new { orderId, customerId = Guid.NewGuid() },
            idempotencyKey: Guid.NewGuid().ToString());
        response.EnsureSuccessStatusCode();
    }

    public static async Task DraftWithLineAsync(HttpClient client, Guid orderId, Guid lineId)
    {
        await DraftAsync(client, orderId);
        var response = await client.PostCommandAsync(
            "AddOrderLine",
            new
            {
                orderId,
                lineId,
                sku = "SKU-001",
                quantity = 1,
                unitPrice = new { amount = 10.00m, currency = new { code = "USD" } },
            },
            idempotencyKey: Guid.NewGuid().ToString());
        response.EnsureSuccessStatusCode();
    }

    public static async Task DraftWithLineAndAddressAsync(HttpClient client, Guid orderId)
    {
        await DraftWithLineAsync(client, orderId, Guid.NewGuid());
        var response = await client.PostCommandAsync(
            "SetOrderShippingAddress",
            new
            {
                orderId,
                shippingAddress = new
                {
                    street = "1 Test St",
                    city = "Reno",
                    postalCode = "89501",
                    country = "US",
                },
            },
            idempotencyKey: Guid.NewGuid().ToString());
        response.EnsureSuccessStatusCode();
    }

    public static async Task PlaceOrderAsync(HttpClient client, Guid orderId)
    {
        await DraftWithLineAndAddressAsync(client, orderId);
        var response = await client.PostCommandAsync(
            "PlaceOrder",
            new { orderId },
            idempotencyKey: Guid.NewGuid().ToString());
        response.EnsureSuccessStatusCode();
    }

    public static async Task ShipOrderAsync(HttpClient client, Guid orderId)
    {
        await PlaceOrderAsync(client, orderId);
        var response = await client.PostCommandAsync(
            "ShipOrder",
            new { orderId, carrier = "TestCarrier", trackingNumber = "TRK-001" },
            idempotencyKey: Guid.NewGuid().ToString());
        response.EnsureSuccessStatusCode();
    }
}
