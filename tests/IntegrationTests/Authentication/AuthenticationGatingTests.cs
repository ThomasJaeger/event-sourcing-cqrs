using System.Net;
using System.Net.Http.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using EventSourcingCqrs.Application.Authentication;

namespace EventSourcingCqrs.IntegrationTests.Authentication;

// Proves the Api establishment path: a gated POST without a valid signed forwarded identity is
// rejected, and a validly signed forwarded identity stamps the real actor onto event metadata. P9.3b
// makes the shared-secret signature the enforced credential the Api verifies before parsing the
// claim; the browser half that produces the signature is a later commit. (ADR 0028.)
public class AuthenticationGatingTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public AuthenticationGatingTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Posting_a_command_without_a_forwarded_identity_returns_401()
    {
        var client = _fixture.Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/commands")
        {
            Content = JsonContent.Create(new
            {
                type = "CreateInventory",
                payload = new { inventoryId = Guid.NewGuid(), sku = "SKU-1" },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Posting_a_query_without_a_forwarded_identity_returns_401()
    {
        var client = _fixture.Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/queries")
        {
            Content = JsonContent.Create(new { type = "ListOrders", payload = new { } }),
        };

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_authenticated_command_is_accepted_and_stamps_the_actor_onto_event_metadata()
    {
        var client = _fixture.Factory.CreateClient();
        var inventoryId = Guid.NewGuid();

        // PostCommandAsync sends the default forwarded identity for ForwardedIdentityTestHeader.DefaultActorId.
        var response = await client.PostCommandAsync(
            "CreateInventory", new { inventoryId, sku = "SKU-1" }, Guid.NewGuid().ToString());
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        using var scope = _fixture.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var envelopes = await store.ReadStreamAsync(
            StreamId.ForAggregate<Inventory>(inventoryId), fromVersion: 0, CancellationToken.None);

        envelopes.Should().ContainSingle();
        envelopes[0].Metadata.ActorId.Should().Be(ForwardedIdentityTestHeader.DefaultActorId);
    }

    [Fact]
    public async Task The_introspection_endpoints_stay_open_without_authentication()
    {
        var client = _fixture.Factory.CreateClient();

        (await client.GetAsync("/commands")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/queries")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("not-a-guid;Admin")]
    [InlineData("00000000000000000000000000000000;Admin")]
    [InlineData("0a9f7c2e4d6b4c1a9f3e2b8d5e7a1c40;NotARole")]
    public async Task Posting_a_command_with_a_malformed_forwarded_identity_returns_401(string header)
    {
        var client = _fixture.Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/commands")
        {
            Content = JsonContent.Create(new
            {
                type = "CreateInventory",
                payload = new { inventoryId = Guid.NewGuid(), sku = "SKU-1" },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add(ForwardedIdentityHeaders.HeaderName, header);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Posting_a_command_with_an_unsigned_forwarded_identity_returns_401()
    {
        // The well-formed default identity with no signature header: this is exactly the input P9.3a
        // accepted. P9.3b rejects it, which locks the unsigned hole closed.
        var client = _fixture.Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/commands")
        {
            Content = JsonContent.Create(new
            {
                type = "CreateInventory",
                payload = new { inventoryId = Guid.NewGuid(), sku = "SKU-1" },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add(ForwardedIdentityHeaders.HeaderName, ForwardedIdentityTestHeader.Default);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("AAAA")]   // valid base64url, but not the MAC of the identity value
    [InlineData("@@@@")]   // not base64url at all: the decode fails and the verifier returns false
    public async Task Posting_a_command_with_an_invalid_signature_returns_401(string signature)
    {
        // A well-formed identity with a signature that is not its MAC under the shared secret. The
        // wrong-MAC case fails FixedTimeEquals; the non-base64url case fails to decode. Both must be
        // a 401, and the decode failure must not surface as a 500.
        var client = _fixture.Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/commands")
        {
            Content = JsonContent.Create(new
            {
                type = "CreateInventory",
                payload = new { inventoryId = Guid.NewGuid(), sku = "SKU-1" },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add(ForwardedIdentityHeaders.HeaderName, ForwardedIdentityTestHeader.Default);
        request.Headers.Add(ForwardedIdentityHeaders.SignatureHeaderName, signature);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Posting_a_command_with_a_validly_signed_but_unparseable_identity_returns_401()
    {
        // The signature is correct for this identity value, so verification passes and the request
        // reaches the reader, which rejects the unknown role token. This is the inner-parser-rejection
        // proof the unsigned malformed cases above no longer give once every unsigned request 401s on
        // the signature.
        var client = _fixture.Factory.CreateClient();
        var identityValue = $"{Guid.NewGuid():N};NotARole";
        var request = new HttpRequestMessage(HttpMethod.Post, "/commands")
        {
            Content = JsonContent.Create(new
            {
                type = "CreateInventory",
                payload = new { inventoryId = Guid.NewGuid(), sku = "SKU-1" },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add(ForwardedIdentityHeaders.HeaderName, identityValue);
        request.Headers.Add(
            ForwardedIdentityHeaders.SignatureHeaderName,
            ForwardedIdentityTestHeader.SignatureFor(identityValue));

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

}
