using EventSourcingCqrs.Hosts.Web.Authentication;
using EventSourcingCqrs.Hosts.Web.Tests.TestDoubles;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Authentication;

// The durable proof that the provider reads the circuit's seeded principal correctly: it returns the
// name-identifier actor, and fails closed when there is none or it is not a Guid.
public class CircuitForwardedIdentityProviderTests
{
    [Fact]
    public async Task GetActorIdAsync_returns_the_name_identifier_of_the_circuit_principal()
    {
        var actorId = Guid.NewGuid();
        var authState = new MutableAuthenticationStateProvider();
        authState.SetActor(actorId);
        var provider = new CircuitForwardedIdentityProvider(authState);

        var result = await provider.GetActorIdAsync();

        result.Should().Be(actorId);
    }

    [Fact]
    public async Task GetActorIdAsync_throws_when_the_circuit_is_anonymous()
    {
        var provider = new CircuitForwardedIdentityProvider(new MutableAuthenticationStateProvider());

        await provider.Invoking(p => p.GetActorIdAsync())
            .Should().ThrowAsync<ForwardedIdentityUnavailableException>();
    }

    [Fact]
    public async Task GetActorIdAsync_throws_when_the_name_identifier_is_not_a_guid()
    {
        var authState = new MutableAuthenticationStateProvider();
        authState.SetNameIdentifier("not-a-guid");
        var provider = new CircuitForwardedIdentityProvider(authState);

        await provider.Invoking(p => p.GetActorIdAsync())
            .Should().ThrowAsync<ForwardedIdentityUnavailableException>();
    }
}
