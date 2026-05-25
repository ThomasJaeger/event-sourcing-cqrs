using System.Net;
using System.Net.Http.Json;
using EventSourcingCqrs.Application;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Endpoints;

public class CommandsEndpointTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public CommandsEndpointTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Posting_a_valid_command_returns_202_with_accepted_response()
    {
        var client = _fixture.Factory.CreateClient();
        var response = await client.PostCommandAsync(
            "CreateInventory",
            new { inventoryId = Guid.NewGuid(), sku = "SKU-1" },
            Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<CommandAcceptedResponse>();
        body.Should().NotBeNull();
        body!.AcceptedUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Posting_without_an_idempotency_key_returns_400()
    {
        var client = _fixture.Factory.CreateClient();
        var response = await client.PostCommandAsync(
            "CreateInventory",
            new { inventoryId = Guid.NewGuid(), sku = "SKU-1" },
            idempotencyKey: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Posting_an_unknown_type_token_returns_400()
    {
        var client = _fixture.Factory.CreateClient();
        var response = await client.PostCommandAsync(
            "NotACommand",
            new { anything = 1 },
            Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Posting_a_payload_that_fails_to_deserialize_returns_400()
    {
        var client = _fixture.Factory.CreateClient();
        var response = await client.PostCommandAsync(
            "CancelOrder",
            new { orderId = "not-a-guid", reason = "x", issuedByUserId = Guid.NewGuid() },
            Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Cancelling_a_nonexistent_order_returns_404()
    {
        var client = _fixture.Factory.CreateClient();
        var response = await client.PostCommandAsync(
            "CancelOrder",
            new { orderId = Guid.NewGuid(), reason = "changed mind", issuedByUserId = Guid.NewGuid() },
            Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Creating_inventory_with_an_empty_sku_returns_422()
    {
        var client = _fixture.Factory.CreateClient();
        var response = await client.PostCommandAsync(
            "CreateInventory",
            new { inventoryId = Guid.NewGuid(), sku = "" },
            Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Creating_the_same_inventory_twice_with_different_keys_returns_409()
    {
        var client = _fixture.Factory.CreateClient();
        var inventoryId = Guid.NewGuid();

        var first = await client.PostCommandAsync(
            "CreateInventory", new { inventoryId, sku = "SKU-1" }, Guid.NewGuid().ToString());
        first.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var second = await client.PostCommandAsync(
            "CreateInventory", new { inventoryId, sku = "SKU-1" }, Guid.NewGuid().ToString());
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Replaying_a_command_with_the_same_idempotency_key_returns_202_both_times()
    {
        var client = _fixture.Factory.CreateClient();
        var inventoryId = Guid.NewGuid();
        var idempotencyKey = Guid.NewGuid().ToString();

        var first = await client.PostCommandAsync(
            "CreateInventory", new { inventoryId, sku = "SKU-1" }, idempotencyKey);
        first.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // Same key: the behavior dedupes before the would-conflict append. Without
        // dedup, a second create on the same stream returns 409; the 202 proves the
        // idempotency short-circuit fired ahead of the append.
        var second = await client.PostCommandAsync(
            "CreateInventory", new { inventoryId, sku = "SKU-1" }, idempotencyKey);
        second.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }
}
