extern alias WebHost;
using System.Net;
using System.Net.Http.Json;
using EventSourcingCqrs.Application.Authentication;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;
using WebHost::EventSourcingCqrs.Hosts.Web.Authentication;

namespace EventSourcingCqrs.IntegrationTests.Authentication;

// Cross-implementation acceptance: the real Web ForwardedIdentitySigner produces a header the real
// Api ForwardedIdentitySignatureVerifier accepts, for both a command and a query. Both ends use the
// production types over the shared test secret, so this proves the wire MAC agrees end to end.
public class ForwardedIdentitySignerAcceptanceTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public ForwardedIdentitySignerAcceptanceTests(ApiFixture fixture) => _fixture = fixture;

    private static ForwardedIdentitySigner Signer() =>
        new(new ForwardedIdentitySigningKey(
            new ForwardedIdentitySigningOptions { Secret = ForwardedIdentityTestHeader.SigningSecret }));

    [Fact]
    public async Task A_command_signed_by_the_web_signer_is_accepted_by_the_api_verifier()
    {
        var client = _fixture.Factory.CreateClient();
        var value = ForwardedIdentityValue.Format(ForwardedIdentityTestHeader.DefaultActorId, Array.Empty<Role>());
        var request = new HttpRequestMessage(HttpMethod.Post, "/commands")
        {
            Content = JsonContent.Create(new
            {
                type = "CreateInventory",
                payload = new { inventoryId = Guid.NewGuid(), sku = "SKU-1" },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add(ForwardedIdentityHeaders.HeaderName, value);
        request.Headers.Add(ForwardedIdentityHeaders.SignatureHeaderName, Signer().Sign(value));

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task A_query_signed_by_the_web_signer_is_accepted_by_the_api_verifier()
    {
        var client = _fixture.Factory.CreateClient();
        var value = ForwardedIdentityValue.Format(ForwardedIdentityTestHeader.DefaultActorId, Array.Empty<Role>());
        var request = new HttpRequestMessage(HttpMethod.Post, "/queries")
        {
            Content = JsonContent.Create(new { type = "ListOrders", payload = new { offset = 0, limit = 50 } }),
        };
        request.Headers.Add(ForwardedIdentityHeaders.HeaderName, value);
        request.Headers.Add(ForwardedIdentityHeaders.SignatureHeaderName, Signer().Sign(value));

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
