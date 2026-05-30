using System.Net;
using System.Net.Http.Json;
using EventSourcingCqrs.Hosts.Api.Authentication;
using EventSourcingCqrs.IntegrationTests.Authentication;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Endpoints;

// The introspection endpoints read the in-memory type registries, so these tests
// need no read-model seeding: the catalog is fixed by the Api host's registered
// providers. GET returns the discriminators POST accepts; the round-trip test
// closes that loop against the shared registry.
public class IntrospectionEndpointTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public IntrospectionEndpointTests(ApiFixture fixture) => _fixture = fixture;

    private async Task<string[]> GetTokensAsync(string path)
    {
        var client = _fixture.Factory.CreateClient();
        var tokens = await client.GetFromJsonAsync<string[]>(path);
        tokens.Should().NotBeNull();
        return tokens!;
    }

    [Fact]
    public async Task GetCommands_returns_exactly_the_fourteen_user_command_discriminators()
    {
        var tokens = await GetTokensAsync("/commands");

        tokens.Should().BeEquivalentTo(new[]
        {
            "DraftOrder", "AddOrderLine", "RemoveOrderLine", "SetOrderShippingAddress",
            "PlaceOrder", "ShipOrder", "CancelOrder", "MarkOrderCompleted",
            "CreateInventory", "AdjustInventory", "DispatchShipment", "DeliverShipment",
            "ReturnShipment", "RefundPayment",
        });
    }

    [Fact]
    public async Task GetCommands_excludes_process_manager_and_timeout_commands()
    {
        var tokens = await GetTokensAsync("/commands");

        // The per-host registry composition (Commit 11) registers only the user
        // command providers in the Api host, so the PM-dispatched commands and the
        // deferred CapturePayment never reach this catalog.
        tokens.Should().NotContain(new[]
        {
            "AuthorizePayment", "VoidPayment", "CapturePayment",
            "ReserveInventory", "ReleaseInventory", "ScheduleShipment",
        });
    }

    [Fact]
    public async Task GetQueries_returns_exactly_the_five_query_discriminators()
    {
        var tokens = await GetTokensAsync("/queries");

        tokens.Should().BeEquivalentTo(new[]
        {
            "ListOrders", "GetOrderDetail", "GetCustomerSummary",
            "GetAllInventoryDashboard", "GetInventoryDashboardBySku",
        });
    }

    [Fact]
    public async Task Introspection_responses_are_sorted_ordinal()
    {
        var commands = await GetTokensAsync("/commands");
        var queries = await GetTokensAsync("/queries");

        commands.Should().BeInAscendingOrder(StringComparer.Ordinal);
        queries.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_command_discriminator_from_introspection_is_accepted_by_POST_commands()
    {
        var client = _fixture.Factory.CreateClient();
        var tokens = await client.GetFromJsonAsync<string[]>("/commands");
        tokens.Should().Contain("CreateInventory");

        // A token the catalog advertises is one POST accepts: dispatch CreateInventory
        // by that token and confirm it is not rejected as an unknown type. 202 proves
        // the round trip through the registry both endpoints share.
        var request = new HttpRequestMessage(HttpMethod.Post, "/commands")
        {
            Content = JsonContent.Create(new
            {
                type = "CreateInventory",
                payload = new { inventoryId = Guid.NewGuid(), sku = $"SKU-{Guid.NewGuid():N}" },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        // /commands is gated (Phase 9); this round-trip is about the type registry, not auth, so it
        // carries the default forwarded identity and its signature (P9.3b).
        request.Headers.Add(ForwardedIdentityDefaults.HeaderName, ForwardedIdentityTestHeader.Default);
        request.Headers.Add(
            ForwardedIdentityDefaults.SignatureHeaderName,
            ForwardedIdentityTestHeader.SignatureFor(ForwardedIdentityTestHeader.Default));
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }
}
