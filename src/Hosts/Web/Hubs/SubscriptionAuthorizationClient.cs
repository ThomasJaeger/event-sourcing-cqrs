using System.Net.Http.Json;
using System.Text.Json;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Authentication;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.Web.Authentication;

namespace EventSourcingCqrs.Hosts.Web.Hubs;

// Posts a subscription-authorization check to the Api host, signed with the actor's forwarded identity
// (P9.6). Symmetric with ApiClient's AttachForwardedIdentityAsync, with one deliberate difference: the
// actor is supplied by the caller, not read from the circuit-scoped identity provider. The dashboard
// hub runs on a SignalR connection, not a Blazor circuit, so it has the authenticated actor on
// Context.User but no circuit-scoped provider to sign through; it passes that actor here. The roles
// are empty by design, the same as ApiClient: the Api host loads the actor's authoritative roles and
// never trusts the wire.
internal sealed class SubscriptionAuthorizationClient : ISubscriptionAuthorizationClient
{
    private readonly HttpClient _httpClient;
    private readonly ForwardedIdentitySigner _signer;

    public SubscriptionAuthorizationClient(HttpClient httpClient, ForwardedIdentitySigner signer)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(signer);
        _httpClient = httpClient;
        _signer = signer;
    }

    public async Task<SubscriptionAuthorizationResult> AuthorizeAsync(
        Guid actorId, SubscriptionAuthorizationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var identityValue = ForwardedIdentityValue.Format(actorId, Array.Empty<Role>());
        var message = new HttpRequestMessage(HttpMethod.Post, "/subscriptions/authorize")
        {
            Content = JsonContent.Create(request, options: JsonSerializerOptions.Web),
        };
        message.Headers.Add(ForwardedIdentityHeaders.HeaderName, identityValue);
        message.Headers.Add(ForwardedIdentityHeaders.SignatureHeaderName, _signer.Sign(identityValue));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(message, ct);
        }
        // HttpClient's timeout convention: an elapsed Timeout surfaces as TaskCanceledException
        // carrying a TimeoutException inner, indistinguishable by type from teardown cancellation.
        // This client owns the transport, so it translates the shape here; callers read a timeout
        // as failure, not cancellation (ADR 0035). A TaskCanceledException without that inner is
        // genuine cancellation and propagates unchanged.
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            throw new TimeoutException("The subscription authorization call timed out.", ex);
        }
        // Fail closed: any non-success status (a 401 from a rejected signature, a 5xx) denies the
        // subscription rather than throwing into the hub method. The hub turns a false into its uniform
        // refusal.
        if (!response.IsSuccessStatusCode)
        {
            return new SubscriptionAuthorizationResult(false, null);
        }

        var body = await response.Content.ReadFromJsonAsync<SubscriptionAuthorizationResponse>(
            JsonSerializerOptions.Web, ct);
        // The wire tenant is present on allow and absent (null) on deny; pass it through to the hub.
        return new SubscriptionAuthorizationResult(body?.Allowed ?? false, body?.Tenant);
    }
}
