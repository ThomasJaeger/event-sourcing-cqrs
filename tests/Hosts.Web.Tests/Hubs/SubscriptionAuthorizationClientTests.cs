using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Authentication;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.Web.Authentication;
using EventSourcingCqrs.Hosts.Web.Hubs;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Hubs;

// The hub's outbound authorize client (P9.6). Proves it signs the call with the supplied actor's
// forwarded identity exactly as the Api verifier expects (an empty role set, the same as ApiClient),
// and that it fails closed: any non-success status denies the subscription rather than throwing into
// the hub.
public class SubscriptionAuthorizationClientTests
{
    private const string Secret = "subscription-authorization-client-tests-secret";
    private static readonly Guid Actor = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task AuthorizeAsync_signs_the_supplied_actor_and_returns_the_allowed_decision()
    {
        var signer = NewSigner();
        var handler = new CapturingHandler(new SubscriptionAuthorizationResponse(Allowed: true), HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.localhost") };
        var client = new SubscriptionAuthorizationClient(httpClient, signer);

        var result = await client.AuthorizeAsync(
            Actor,
            new SubscriptionAuthorizationRequest(SubscriptionResourceType.Order, Guid.NewGuid().ToString()),
            CancellationToken.None);

        result.Allowed.Should().BeTrue();
        var expectedValue = ForwardedIdentityValue.Format(Actor, Array.Empty<Role>());
        handler.Request!.Headers.GetValues(ForwardedIdentityHeaders.HeaderName)
            .Should().ContainSingle().Which.Should().Be(expectedValue);
        handler.Request!.Headers.GetValues(ForwardedIdentityHeaders.SignatureHeaderName)
            .Should().ContainSingle().Which.Should().Be(signer.Sign(expectedValue));
    }

    [Fact]
    public async Task AuthorizeAsync_returns_false_when_the_endpoint_denies()
    {
        var handler = new CapturingHandler(new SubscriptionAuthorizationResponse(Allowed: false), HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.localhost") };
        var client = new SubscriptionAuthorizationClient(httpClient, NewSigner());

        var result = await client.AuthorizeAsync(
            Actor,
            new SubscriptionAuthorizationRequest(SubscriptionResourceType.Inventory, "SKU-1"),
            CancellationToken.None);

        result.Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task AuthorizeAsync_fails_closed_on_a_non_success_status()
    {
        var handler = new CapturingHandler(response: null, HttpStatusCode.Unauthorized);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.localhost") };
        var client = new SubscriptionAuthorizationClient(httpClient, NewSigner());

        var result = await client.AuthorizeAsync(
            Actor,
            new SubscriptionAuthorizationRequest(SubscriptionResourceType.Order, Guid.NewGuid().ToString()),
            CancellationToken.None);

        result.Allowed.Should().BeFalse();
    }

    // P11.10: an HttpClient send timeout reaches this client as TaskCanceledException wrapping
    // TimeoutException. That shape is infrastructure failure, not teardown; AuthorizeAsync translates
    // it at the HTTP boundary so callers classify a timeout as failure rather than cancellation.
    [Fact]
    public async Task A_send_timeout_surfaces_as_TimeoutException_not_cancellation()
    {
        var sendTimeout = new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.",
            new TimeoutException());
        var handler = new TimingOutHandler(sendTimeout);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.localhost") };
        var client = new SubscriptionAuthorizationClient(httpClient, NewSigner());

        var act = () => client.AuthorizeAsync(
            Actor,
            new SubscriptionAuthorizationRequest(SubscriptionResourceType.Order, Guid.NewGuid().ToString()),
            CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<TimeoutException>()).Which;
        thrown.InnerException.Should().BeSameAs(sendTimeout);
    }

    // Green on write: pins the discriminator's boundary. A TaskCanceledException without a
    // TimeoutException inner is genuine cancellation, not a timeout; the translation must not
    // widen to swallow it.
    [Fact]
    public async Task A_cancellation_without_a_timeout_inner_propagates_unchanged()
    {
        var cancellation = new TaskCanceledException("A task was canceled.");
        var handler = new TimingOutHandler(cancellation);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.localhost") };
        var client = new SubscriptionAuthorizationClient(httpClient, NewSigner());

        var act = () => client.AuthorizeAsync(
            Actor,
            new SubscriptionAuthorizationRequest(SubscriptionResourceType.Order, Guid.NewGuid().ToString()),
            CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<TaskCanceledException>()).Which;
        thrown.Should().BeSameAs(cancellation);
    }

    private static ForwardedIdentitySigner NewSigner() =>
        new(new ForwardedIdentitySigningKey(new ForwardedIdentitySigningOptions { Secret = Secret }));

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly SubscriptionAuthorizationResponse? _response;
        private readonly HttpStatusCode _status;

        public CapturingHandler(SubscriptionAuthorizationResponse? response, HttpStatusCode status)
        {
            _response = response;
            _status = status;
        }

        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            var message = new HttpResponseMessage(_status);
            if (_response is not null)
            {
                message.Content = JsonContent.Create(_response, options: JsonSerializerOptions.Web);
            }
            return Task.FromResult(message);
        }
    }

    // Faults the send the way an elapsed HttpClient.Timeout does: a TaskCanceledException carrying a
    // TimeoutException inner. CapturingHandler stays a canned-response double; faulting is a different
    // contract, so it gets its own handler.
    private sealed class TimingOutHandler : HttpMessageHandler
    {
        private readonly TaskCanceledException _sendTimeout;

        public TimingOutHandler(TaskCanceledException sendTimeout) => _sendTimeout = sendTimeout;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(_sendTimeout);
    }
}
