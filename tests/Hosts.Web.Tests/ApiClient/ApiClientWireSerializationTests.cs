using System.Net;
using System.Text;
using System.Text.Json;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Authentication;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Hosts.Web.Authentication;
using FluentAssertions;
using Xunit;
using ApiClientSut = EventSourcingCqrs.Hosts.Web.ApiClient;

namespace EventSourcingCqrs.Hosts.Web.Tests.ApiClient;

public sealed class ApiClientWireSerializationTests
{
    [Fact]
    public async Task Command_body_preserves_concrete_fields_and_nested_money()
    {
        using var handler = new CapturingHandler(HttpStatusCode.Accepted,
            """{"acceptedUtc":"2026-10-07T12:00:00Z"}""");
        using var httpClient = CreateHttpClient(handler);
        var sut = BuildClient(httpClient);
        var orderId = Guid.Parse("b51a61a1-0c7a-41d7-84d1-d54c4c79e681");
        var lineId = Guid.Parse("1762ca7c-78c2-4086-8d64-ae119064625c");
        ICommand command = new AddOrderLine(orderId, lineId, "NOTEBOOK", 3, new Money(12.50m, Currency.EUR));

        await sut.SendCommandAsync(command, "add-notebook-line", CancellationToken.None);

        handler.Method.Should().Be(HttpMethod.Post);
        handler.Path.Should().Be("/commands");
        handler.IdempotencyKey.Should().Be("add-notebook-line");
        using var document = JsonDocument.Parse(handler.Body);
        document.RootElement.GetProperty("type").GetString().Should().Be(nameof(AddOrderLine));
        var payload = document.RootElement.GetProperty("payload");
        payload.EnumerateObject().Should().NotBeEmpty("the command's concrete fields must reach the API");
        payload.GetProperty("orderId").GetGuid().Should().Be(orderId);
        payload.GetProperty("lineId").GetGuid().Should().Be(lineId);
        payload.GetProperty("sku").GetString().Should().Be("NOTEBOOK");
        payload.GetProperty("quantity").GetInt32().Should().Be(3);
        payload.GetProperty("unitPrice").GetProperty("amount").GetDecimal().Should().Be(12.50m);
        payload.GetProperty("unitPrice").GetProperty("currency").GetProperty("code")
            .GetString().Should().Be("EUR");
        payload.Deserialize<AddOrderLine>(JsonSerializerOptions.Web).Should().Be(command);
    }

    [Theory]
    [InlineData("b51a61a1-0c7a-41d7-84d1-d54c4c79e681")]
    [InlineData("914bd52c-4256-4ac8-9f02-9fc5b63c085c")]
    public async Task Detail_query_body_preserves_requested_order_id(string orderIdText)
    {
        using var handler = new CapturingHandler(HttpStatusCode.NotFound, "");
        using var httpClient = CreateHttpClient(handler);
        var sut = BuildClient(httpClient);
        var orderId = Guid.Parse(orderIdText);
        IQuery<OrderDetailView?> query = new GetOrderDetail(orderId);

        await sut.QueryAsync(query, CancellationToken.None);

        handler.Method.Should().Be(HttpMethod.Post);
        handler.Path.Should().Be("/queries");
        using var document = JsonDocument.Parse(handler.Body);
        document.RootElement.GetProperty("type").GetString().Should().Be(nameof(GetOrderDetail));
        var payload = document.RootElement.GetProperty("payload");
        payload.EnumerateObject().Should().NotBeEmpty("the order ID must reach the API");
        payload.GetProperty("orderId").GetGuid().Should().Be(orderId);
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(17, 23)]
    public async Task List_query_body_preserves_offset_and_limit(int offset, int limit)
    {
        using var handler = new CapturingHandler(HttpStatusCode.OK, "[]");
        using var httpClient = CreateHttpClient(handler);
        var sut = BuildClient(httpClient);

        await sut.QueryAsync(new ListOrders(offset, limit), CancellationToken.None);

        handler.Method.Should().Be(HttpMethod.Post);
        handler.Path.Should().Be("/queries");
        using var document = JsonDocument.Parse(handler.Body);
        document.RootElement.GetProperty("type").GetString().Should().Be(nameof(ListOrders));
        var payload = document.RootElement.GetProperty("payload");
        payload.EnumerateObject().Should().NotBeEmpty("pagination must reach the API");
        payload.GetProperty("offset").GetInt32().Should().Be(offset);
        payload.GetProperty("limit").GetInt32().Should().Be(limit);
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler)
        => new(handler) { BaseAddress = new Uri("http://localhost") };

    private static ApiClientSut BuildClient(HttpClient httpClient)
    {
        var commands = new CommandTypeRegistry();
        commands.Register(typeof(AddOrderLine));
        var queries = new QueryTypeRegistry();
        queries.Register(typeof(GetOrderDetail));
        queries.Register(typeof(ListOrders));
        var signer = new ForwardedIdentitySigner(new ForwardedIdentitySigningKey(
            new ForwardedIdentitySigningOptions { Secret = "wire-serialization-test-forwarded-identity-secret" }));
        return new ApiClientSut(httpClient, commands, queries, new FixedActorIdentityProvider(), signer);
    }

    private sealed class FixedActorIdentityProvider : ICircuitForwardedIdentityProvider
    {
        public Task<Guid> GetActorIdAsync()
            => Task.FromResult(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    }

    private sealed class CapturingHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public string? IdempotencyKey { get; private set; }
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri?.AbsolutePath;
            IdempotencyKey = request.Headers.TryGetValues("Idempotency-Key", out var values)
                ? values.Single() : null;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
