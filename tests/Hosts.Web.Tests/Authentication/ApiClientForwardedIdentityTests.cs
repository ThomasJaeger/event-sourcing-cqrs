using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Authentication;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.Web.Authentication;
using FluentAssertions;
using Xunit;
using ApiClientSut = EventSourcingCqrs.Hosts.Web.ApiClient;

namespace EventSourcingCqrs.Hosts.Web.Tests.Authentication;

// ApiClient attaches the circuit's signed forwarded identity on BOTH the command and the query path,
// and fails closed (no request leaves) when the circuit has no identity.
public class ApiClientForwardedIdentityTests
{
    private const string Secret = "api-client-forwarded-identity-test-secret-key";

    [Fact]
    public async Task SendCommandAsync_attaches_the_signed_forwarded_identity()
    {
        var actorId = Guid.NewGuid();
        var (sut, handler, signer) = Build(new FixedIdentityProvider(actorId), Accepted);

        await sut.SendCommandAsync(new CancelOrder(Guid.NewGuid(), "test", Guid.Empty), "key-1", CancellationToken.None);

        var value = $"{actorId:N};";
        handler.Captured.Should().ContainSingle();
        handler.Captured[0].Identity.Should().Be(value);
        handler.Captured[0].Signature.Should().Be(signer.Sign(value));
    }

    [Fact]
    public async Task QueryAsync_attaches_the_signed_forwarded_identity()
    {
        var actorId = Guid.NewGuid();
        var (sut, handler, signer) = Build(new FixedIdentityProvider(actorId), NotFound);

        await sut.QueryAsync(new GetOrderDetail(Guid.NewGuid()), CancellationToken.None);

        var value = $"{actorId:N};";
        handler.Captured.Should().ContainSingle();
        handler.Captured[0].Identity.Should().Be(value);
        handler.Captured[0].Signature.Should().Be(signer.Sign(value));
    }

    [Fact]
    public async Task SendCommandAsync_throws_and_sends_nothing_when_the_circuit_has_no_identity()
    {
        var (sut, handler, _) = Build(new UnavailableIdentityProvider(), Accepted);

        await sut.Invoking(s => s.SendCommandAsync(
                new CancelOrder(Guid.NewGuid(), "test", Guid.Empty), "key-1", CancellationToken.None))
            .Should().ThrowAsync<ForwardedIdentityUnavailableException>();

        handler.Captured.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryAsync_throws_and_sends_nothing_when_the_circuit_has_no_identity()
    {
        var (sut, handler, _) = Build(new UnavailableIdentityProvider(), NotFound);

        await sut.Invoking(s => s.QueryAsync(new GetOrderDetail(Guid.NewGuid()), CancellationToken.None))
            .Should().ThrowAsync<ForwardedIdentityUnavailableException>();

        handler.Captured.Should().BeEmpty();
    }

    private static HttpResponseMessage Accepted() =>
        new(HttpStatusCode.Accepted)
        {
            Content = JsonContent.Create(
                new CommandAcceptedResponse(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                options: JsonSerializerOptions.Web),
        };

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    private static (ApiClientSut Client, CapturingHandler Handler, ForwardedIdentitySigner Signer) Build(
        ICircuitForwardedIdentityProvider identity, Func<HttpResponseMessage> response)
    {
        var signer = new ForwardedIdentitySigner(new ForwardedIdentitySigningKey(
            new ForwardedIdentitySigningOptions { Secret = Secret }));
        var handler = new CapturingHandler(response);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var commandRegistry = new CommandTypeRegistry();
        commandRegistry.Register(typeof(CancelOrder));
        var queryRegistry = new QueryTypeRegistry();
        queryRegistry.Register(typeof(GetOrderDetail));
        var client = new ApiClientSut(httpClient, commandRegistry, queryRegistry, identity, signer);
        return (client, handler, signer);
    }

    private sealed class FixedIdentityProvider(Guid actorId) : ICircuitForwardedIdentityProvider
    {
        public Task<Guid> GetActorIdAsync() => Task.FromResult(actorId);
    }

    private sealed class UnavailableIdentityProvider : ICircuitForwardedIdentityProvider
    {
        public Task<Guid> GetActorIdAsync() => throw new ForwardedIdentityUnavailableException();
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _response;

        public CapturingHandler(Func<HttpResponseMessage> response) => _response = response;

        public List<(string? Identity, string? Signature)> Captured { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryGetValues(ForwardedIdentityHeaders.HeaderName, out var id);
            request.Headers.TryGetValues(ForwardedIdentityHeaders.SignatureHeaderName, out var sig);
            Captured.Add((id?.FirstOrDefault(), sig?.FirstOrDefault()));
            return Task.FromResult(_response());
        }
    }
}
