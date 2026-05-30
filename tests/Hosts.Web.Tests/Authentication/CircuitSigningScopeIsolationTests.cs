using System.Net;
using System.Net.Http;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Authentication;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.Web;
using EventSourcingCqrs.Hosts.Web.Authentication;
using EventSourcingCqrs.Hosts.Web.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ApiClientSut = EventSourcingCqrs.Hosts.Web.ApiClient;

namespace EventSourcingCqrs.Hosts.Web.Tests.Authentication;

// The test that earns signing-inside-ApiClient. ApiClient is resolved from each DI scope, so the
// circuit identity provider it reads is that scope's; two scopes carrying different principals sign
// their requests with their own actors. A DelegatingHandler reading a scoped accessor could not do
// this: the typed client's handler chain is pooled across scopes and rotated on a timer, so it would
// not see a per-scope identity.
public class CircuitSigningScopeIsolationTests
{
    [Fact]
    public async Task Each_scope_signs_its_request_with_its_own_circuit_actor()
    {
        var capturing = new CapturingHandler();
        var httpClient = new HttpClient(capturing) { BaseAddress = new Uri("http://localhost") };

        var services = new ServiceCollection();
        services.AddSingleton(new ForwardedIdentitySigningKey(
            new ForwardedIdentitySigningOptions { Secret = "scope-isolation-forwarded-identity-secret" }));
        services.AddSingleton<ForwardedIdentitySigner>();
        services.AddSingleton(new CommandTypeRegistry());
        services.AddSingleton(QueryRegistry());
        services.AddScoped<MutableAuthenticationStateProvider>();
        services.AddScoped<AuthenticationStateProvider>(
            sp => sp.GetRequiredService<MutableAuthenticationStateProvider>());
        services.AddScoped<ICircuitForwardedIdentityProvider, CircuitForwardedIdentityProvider>();
        services.AddScoped<IApiClient>(sp => new ApiClientSut(
            httpClient,
            sp.GetRequiredService<CommandTypeRegistry>(),
            sp.GetRequiredService<QueryTypeRegistry>(),
            sp.GetRequiredService<ICircuitForwardedIdentityProvider>(),
            sp.GetRequiredService<ForwardedIdentitySigner>()));
        await using var root = services.BuildServiceProvider();

        var actorA = Guid.NewGuid();
        var actorB = Guid.NewGuid();

        await using (var scopeA = root.CreateAsyncScope())
        {
            scopeA.ServiceProvider.GetRequiredService<MutableAuthenticationStateProvider>().SetActor(actorA);
            await scopeA.ServiceProvider.GetRequiredService<IApiClient>()
                .QueryAsync(new ListOrders(0, 50), CancellationToken.None);
        }

        await using (var scopeB = root.CreateAsyncScope())
        {
            scopeB.ServiceProvider.GetRequiredService<MutableAuthenticationStateProvider>().SetActor(actorB);
            await scopeB.ServiceProvider.GetRequiredService<IApiClient>()
                .QueryAsync(new ListOrders(0, 50), CancellationToken.None);
        }

        capturing.CapturedIdentities.Should().Equal($"{actorA:N};", $"{actorB:N};");
    }

    private static QueryTypeRegistry QueryRegistry()
    {
        var registry = new QueryTypeRegistry();
        registry.Register(typeof(ListOrders));
        return registry;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string?> CapturedIdentities { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryGetValues(ForwardedIdentityHeaders.HeaderName, out var values);
            CapturedIdentities.Add(values?.FirstOrDefault());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
