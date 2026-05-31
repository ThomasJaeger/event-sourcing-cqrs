using System.Net;
using System.Net.Http.Json;
using EventSourcingCqrs.Application.Authentication;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.IntegrationTests.Authentication;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Authorization;

// Proves command authorization end to end at the Api host: an authenticated principal whose
// authoritative roles do not grant a command's required permission is refused with 403, while the
// default administrator the fixture seeds is accepted. The default actor holds Admin (every
// permission); the second actor holds only Customer, which does not grant CreateInventory.
public class CommandAuthorizationTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public CommandAuthorizationTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_command_the_principals_roles_do_not_grant_is_refused_with_403()
    {
        var customerActor = Guid.NewGuid();
        await _fixture.SeedRoleAsync(customerActor, Role.Customer);

        var client = _fixture.Factory.CreateClient();
        var identity = ForwardedIdentityValue.Format(customerActor, Array.Empty<Role>());
        var request = new HttpRequestMessage(HttpMethod.Post, "/commands")
        {
            Content = JsonContent.Create(new
            {
                type = "CreateInventory",
                payload = new { inventoryId = Guid.NewGuid(), sku = "SKU-1" },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add(ForwardedIdentityHeaders.HeaderName, identity);
        request.Headers.Add(
            ForwardedIdentityHeaders.SignatureHeaderName,
            ForwardedIdentityTestHeader.SignatureFor(identity));

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_seeded_administrator_is_authorized_for_an_admin_only_command()
    {
        var client = _fixture.Factory.CreateClient();

        var response = await client.PostCommandAsync(
            "CreateInventory",
            new { inventoryId = Guid.NewGuid(), sku = "SKU-1" },
            Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }
}
