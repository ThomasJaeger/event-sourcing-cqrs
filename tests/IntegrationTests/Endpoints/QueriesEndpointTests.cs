using System.Net;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Endpoints;

public class QueriesEndpointTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public QueriesEndpointTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Posting_an_unknown_query_type_returns_400()
    {
        var client = _fixture.Factory.CreateClient();
        var response = await client.PostQueryAsync("NotAQuery", new { anything = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Posting_a_payload_that_fails_to_deserialize_returns_400()
    {
        var client = _fixture.Factory.CreateClient();
        var response = await client.PostQueryAsync("GetOrderDetail", new { orderId = "not-a-guid" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
