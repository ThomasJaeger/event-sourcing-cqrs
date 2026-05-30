using System.Net;
using System.Net.Http;
using System.Text;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Application.Authentication;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.Web;
using EventSourcingCqrs.Hosts.Web.Authentication;
using FluentAssertions;
using Xunit;
using ApiClientSut = EventSourcingCqrs.Hosts.Web.ApiClient;

namespace EventSourcingCqrs.Hosts.Web.Tests.ApiClient;

// Exercises ApiClient.ThrowMappedAsync against canned HTTP responses: the
// dispatch-time validation 400 (HttpValidationProblemDetails), the four
// pre-dispatch endpoint 400s (which carry { code, message } and no field errors),
// the 422 and 409 arms, the command-path 404 fold into infrastructure, the
// query-path 404 short-circuit to null, the 500 arm, and the two parse-failure
// fallbacks (non-JSON body, malformed JSON). The StubApiClient the component tests
// use bypasses this mapping, so this is the only coverage of the wire-parse path.
// ApiClient is internal; the test reaches it through InternalsVisibleTo, and the
// ApiClientSut alias keeps the type from colliding with this namespace's last
// segment.
public sealed class ApiClientWireParseTests
{
    private static readonly Guid AnyOrderId = Guid.NewGuid();

    [Fact]
    public async Task DispatchTime_400_with_problem_details_errors_throws_with_populated_Errors()
    {
        var sut = BuildClient(
            HttpStatusCode.BadRequest,
            """{"title":"One or more validation errors occurred.","status":400,"errors":{"Reason":["Reason is required"]}}""");

        var act = () => sut.SendCommandAsync(SampleCommand(), "key-1", CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<ApiValidationException>()).Which;
        ex.Errors.Should().ContainKey("Reason");
        ex.Errors["Reason"].Should().Contain("Reason is required");
        ex.Code.Should().BeNull();
        ex.Message.Should().Be("The request was invalid.");
    }

    [Fact]
    public async Task PreDispatch_400_missing_idempotency_key_carries_code_and_message()
    {
        var sut = BuildClient(
            HttpStatusCode.BadRequest,
            """{"code":"MISSING_IDEMPOTENCY_KEY","message":"The Idempotency-Key header is required."}""");

        var act = () => sut.SendCommandAsync(SampleCommand(), "key-1", CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<ApiValidationException>()).Which;
        ex.Errors.Should().BeEmpty();
        ex.Code.Should().Be("MISSING_IDEMPOTENCY_KEY");
        ex.Message.Should().Be("The Idempotency-Key header is required.");
    }

    [Fact]
    public async Task PreDispatch_400_missing_type_carries_code_and_message()
    {
        var sut = BuildClient(
            HttpStatusCode.BadRequest,
            """{"code":"MISSING_TYPE","message":"The command envelope's type is required."}""");

        var act = () => sut.SendCommandAsync(SampleCommand(), "key-1", CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<ApiValidationException>()).Which;
        ex.Errors.Should().BeEmpty();
        ex.Code.Should().Be("MISSING_TYPE");
        ex.Message.Should().Be("The command envelope's type is required.");
    }

    [Fact]
    public async Task PreDispatch_400_unknown_type_carries_code_and_message()
    {
        var sut = BuildClient(
            HttpStatusCode.BadRequest,
            """{"code":"UNKNOWN_TYPE","message":"'Frobnicate' is not a known command type."}""");

        var act = () => sut.SendCommandAsync(SampleCommand(), "key-1", CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<ApiValidationException>()).Which;
        ex.Errors.Should().BeEmpty();
        ex.Code.Should().Be("UNKNOWN_TYPE");
        ex.Message.Should().Be("'Frobnicate' is not a known command type.");
    }

    [Fact]
    public async Task PreDispatch_400_malformed_payload_carries_code_and_message()
    {
        var sut = BuildClient(
            HttpStatusCode.BadRequest,
            """{"code":"MALFORMED_PAYLOAD","message":"The payload could not be read as 'CancelOrder'."}""");

        var act = () => sut.SendCommandAsync(SampleCommand(), "key-1", CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<ApiValidationException>()).Which;
        ex.Errors.Should().BeEmpty();
        ex.Code.Should().Be("MALFORMED_PAYLOAD");
        ex.Message.Should().Be("The payload could not be read as 'CancelOrder'.");
    }

    [Fact]
    public async Task Response_422_throws_business_rule_with_code_and_message()
    {
        var sut = BuildClient(
            HttpStatusCode.UnprocessableEntity,
            """{"code":"RULE_VIOLATED","message":"The order has already shipped and cannot be cancelled."}""");

        var act = () => sut.SendCommandAsync(SampleCommand(), "key-1", CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<ApiBusinessRuleException>()).Which;
        ex.Code.Should().Be("RULE_VIOLATED");
        ex.Message.Should().Contain("already shipped");
    }

    [Fact]
    public async Task Response_409_throws_concurrency_with_expected_version()
    {
        var sut = BuildClient(
            HttpStatusCode.Conflict,
            """{"code":"CONCURRENCY","message":"The order was modified concurrently.","expectedVersion":5}""");

        var act = () => sut.SendCommandAsync(SampleCommand(), "key-1", CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<ApiConcurrencyException>()).Which;
        ex.ExpectedVersion.Should().Be(5);
    }

    [Fact]
    public async Task Command_path_404_folds_into_infrastructure()
    {
        var sut = BuildClient(
            HttpStatusCode.NotFound,
            """{"code":"NOT_FOUND","message":"The order was not found.","aggregateId":"00000000-0000-0000-0000-000000000000"}""");

        var act = () => sut.SendCommandAsync(SampleCommand(), "key-1", CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<ApiInfrastructureException>()).Which;
        ex.StatusCode.Should().Be(404);
        ex.Code.Should().Be("NOT_FOUND");
        ex.Message.Should().Contain("not found");
    }

    [Fact]
    public async Task Query_path_404_short_circuits_to_null()
    {
        var sut = BuildClient(HttpStatusCode.NotFound, string.Empty);

        var result = await sut.QueryAsync(new GetOrderDetail(AnyOrderId), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Response_500_throws_infrastructure_with_code_and_status()
    {
        var sut = BuildClient(
            HttpStatusCode.InternalServerError,
            """{"code":"INTERNAL","message":"An error occurred. Please try again."}""");

        var act = () => sut.SendCommandAsync(SampleCommand(), "key-1", CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<ApiInfrastructureException>()).Which;
        ex.StatusCode.Should().Be(500);
        ex.Code.Should().Be("INTERNAL");
    }

    [Fact]
    public async Task Non_json_body_falls_back_to_status_templated_infrastructure_message()
    {
        var sut = BuildClient(
            HttpStatusCode.BadGateway,
            "<html><body>502 Bad Gateway</body></html>",
            mediaType: "text/html");

        var act = () => sut.SendCommandAsync(SampleCommand(), "key-1", CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<ApiInfrastructureException>()).Which;
        ex.StatusCode.Should().Be(502);
        ex.Code.Should().BeNull();
        ex.Message.Should().Be("Api request failed with status 502.");
    }

    [Fact]
    public async Task Malformed_json_body_on_422_falls_back_to_generic_business_rule()
    {
        var sut = BuildClient(
            HttpStatusCode.UnprocessableEntity,
            "{ this is not valid json");

        var act = () => sut.SendCommandAsync(SampleCommand(), "key-1", CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<ApiBusinessRuleException>()).Which;
        ex.Code.Should().Be("BUSINESS_RULE_VIOLATION");
        ex.Message.Should().Be("The command violated a business rule.");
    }

    private static CancelOrder SampleCommand() => new(AnyOrderId, "User cancelled via UI", Guid.Empty);

    private static ApiClientSut BuildClient(
        HttpStatusCode status,
        string body,
        string mediaType = "application/json")
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
        };
        var httpClient = new HttpClient(new StubHttpMessageHandler(response))
        {
            BaseAddress = new Uri("http://localhost"),
        };
        var commandRegistry = new CommandTypeRegistry();
        commandRegistry.Register(typeof(CancelOrder));
        var queryRegistry = new QueryTypeRegistry();
        queryRegistry.Register(typeof(GetOrderDetail));
        var signer = new ForwardedIdentitySigner(new ForwardedIdentitySigningKey(
            new ForwardedIdentitySigningOptions { Secret = "wire-parse-test-forwarded-identity-secret" }));
        return new ApiClientSut(
            httpClient, commandRegistry, queryRegistry, new FixedActorIdentityProvider(AnyOrderId), signer);
    }

    private sealed class FixedActorIdentityProvider(Guid actorId) : ICircuitForwardedIdentityProvider
    {
        public Task<Guid> GetActorIdAsync() => Task.FromResult(actorId);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StubHttpMessageHandler(HttpResponseMessage response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_response);
    }
}
