using System.Net;
using System.Net.Http.Json;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Authentication;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.IntegrationTests.Authentication;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Authorization;

// Proves the subscription-authorization endpoint end to end at the Api host with the real authz
// substrate (P9.6): the permission gate, the operational-versus-owner split, and the existence-hiding
// invariant (a not-found order denies byte-identically to a not-owned one). The forwarded identity
// carries the actor id with an empty role set the way the Web host signs it; the Api host loads the
// actor's authoritative roles from the read model the fixture seeds.
public class SubscriptionAuthorizationEndpointTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    private static readonly DateTime SeededAt =
        new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Guid OtherTenant = Guid.Parse("55555555-5555-5555-5555-555555555555");

    public SubscriptionAuthorizationEndpointTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_owner_may_subscribe_to_its_own_order()
    {
        var customerActor = Guid.NewGuid();
        await _fixture.SeedRoleAsync(customerActor, Role.Customer);
        // actor-equals-customer: the owned order's CustomerId is the actor id.
        var orderId = await SeedOrderAsync(ownerCustomerId: customerActor);

        var response = await PostAuthorizeAsAsync(
            new SubscriptionAuthorizationRequest(SubscriptionResourceType.Order, orderId.ToString()),
            customerActor);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SubscriptionAuthorizationResponse>();
        body!.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task An_owner_scoped_principal_is_denied_another_customers_order_and_a_missing_one_identically()
    {
        var customerActor = Guid.NewGuid();
        await _fixture.SeedRoleAsync(customerActor, Role.Customer);
        var othersOrderId = await SeedOrderAsync(ownerCustomerId: Guid.NewGuid());

        var notOwned = await ReadAuthorizeBodyAsAsync(
            new SubscriptionAuthorizationRequest(SubscriptionResourceType.Order, othersOrderId.ToString()),
            customerActor);
        var notFound = await ReadAuthorizeBodyAsAsync(
            new SubscriptionAuthorizationRequest(SubscriptionResourceType.Order, Guid.NewGuid().ToString()),
            customerActor);

        // Both deny, and the bytes match: a customer cannot tell an order it does not own from one that
        // does not exist (existence-hiding).
        notOwned.Should().Be("{\"allowed\":false}");
        notFound.Should().Be(notOwned);
    }

    [Fact]
    public async Task An_operational_principal_may_subscribe_to_any_order()
    {
        var supportActor = Guid.NewGuid();
        await _fixture.SeedRoleAsync(supportActor, Role.Support);
        var someoneElsesOrder = await SeedOrderAsync(ownerCustomerId: Guid.NewGuid());

        var response = await PostAuthorizeAsAsync(
            new SubscriptionAuthorizationRequest(SubscriptionResourceType.Order, someoneElsesOrder.ToString()),
            supportActor);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<SubscriptionAuthorizationResponse>())!
            .Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Inventory_subscription_requires_ViewInventory()
    {
        var supportActor = Guid.NewGuid();
        await _fixture.SeedRoleAsync(supportActor, Role.Support);
        var customerActor = Guid.NewGuid();
        await _fixture.SeedRoleAsync(customerActor, Role.Customer);

        var supportResponse = await PostAuthorizeAsAsync(
            new SubscriptionAuthorizationRequest(SubscriptionResourceType.Inventory, "SKU-1"), supportActor);
        var customerResponse = await PostAuthorizeAsAsync(
            new SubscriptionAuthorizationRequest(SubscriptionResourceType.Inventory, "SKU-1"), customerActor);

        (await supportResponse.Content.ReadFromJsonAsync<SubscriptionAuthorizationResponse>())!
            .Allowed.Should().BeTrue();
        (await customerResponse.Content.ReadFromJsonAsync<SubscriptionAuthorizationResponse>())!
            .Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task An_unauthenticated_request_is_rejected_with_401()
    {
        var client = _fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/subscriptions/authorize",
            new SubscriptionAuthorizationRequest(SubscriptionResourceType.Order, Guid.NewGuid().ToString()));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_owner_is_denied_a_subscription_to_its_order_seeded_under_another_tenant()
    {
        var customerActor = Guid.NewGuid();
        await _fixture.SeedRoleAsync(customerActor, Role.Customer);
        var orderId = Guid.NewGuid();
        // The header records this actor as its owner but lives under another tenant. The endpoint
        // reads the header under the caller's default tenant, so the row is invisible and ownership
        // cannot be confirmed. Contrast An_owner_may_subscribe_to_its_own_order, the same-tenant case.
        await SeedOrderHeaderUnderTenantAsync(orderId, ownerCustomerId: customerActor, tenant: OtherTenant);

        var response = await PostAuthorizeAsAsync(
            new SubscriptionAuthorizationRequest(SubscriptionResourceType.Order, orderId.ToString()),
            customerActor);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<SubscriptionAuthorizationResponse>())!
            .Allowed.Should().BeFalse();
    }

    private async Task<Guid> SeedOrderAsync(Guid ownerCustomerId)
    {
        var orderId = Guid.NewGuid();
        var store = _fixture.Factory.Services.GetRequiredService<IOrderDetailStore>();
        await using (var uow = await store.BeginAsync(default))
        {
            await uow.CreateHeaderAsync(orderId, ownerCustomerId, SeededAt, default);
            await uow.CommitAsync($"subscription-authz-seed-{orderId:N}", 1, default);
        }
        return orderId;
    }

    // Posts to the authorize endpoint signing a forwarded identity for the actor the way the Web host
    // does: the actor id with an empty role set, since the Api host loads the actor's authoritative
    // roles from the read model the fixture seeds with SeedRoleAsync.
    private Task<HttpResponseMessage> PostAuthorizeAsAsync(SubscriptionAuthorizationRequest request, Guid actorId)
    {
        var identity = ForwardedIdentityValue.Format(actorId, Array.Empty<Role>());
        var client = _fixture.Factory.CreateClient();
        var message = new HttpRequestMessage(HttpMethod.Post, "/subscriptions/authorize")
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Add(ForwardedIdentityHeaders.HeaderName, identity);
        message.Headers.Add(
            ForwardedIdentityHeaders.SignatureHeaderName, ForwardedIdentityTestHeader.SignatureFor(identity));
        return client.SendAsync(message);
    }

    private async Task<string> ReadAuthorizeBodyAsAsync(SubscriptionAuthorizationRequest request, Guid actorId)
    {
        var response = await PostAuthorizeAsAsync(request, actorId);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }

    // Direct-inserts an order-detail header under an explicit tenant, the way a second tenant's
    // projection would have written it. The unit-of-work seed path writes the default tenant only,
    // so a cross-tenant row is seeded by raw insert (ApiFixture exposes the connection string).
    private async Task SeedOrderHeaderUnderTenantAsync(Guid orderId, Guid ownerCustomerId, Guid tenant)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO read_models.order_detail (order_id, customer_id, status, last_updated_utc, tenant_id) " +
            "VALUES (@order_id, @customer_id, @status, @last_updated_utc, @tenant_id)";
        command.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        command.Parameters.AddWithValue("customer_id", NpgsqlDbType.Uuid, ownerCustomerId);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Text, "Placed");
        command.Parameters.AddWithValue("last_updated_utc", NpgsqlDbType.TimestampTz, SeededAt);
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, tenant);
        await command.ExecuteNonQueryAsync();
    }
}
