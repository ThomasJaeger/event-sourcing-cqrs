using EventSourcingCqrs.Application.Authentication;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Authentication;

// The producer side (ForwardedIdentityValue.Format) and the reader side (HeaderForwardedIdentityReader)
// are single-sourced on the same wire form, so a value formatted here parses back to the same actor
// and roles. Both types now live in Application, so this is a pure unit test that needs no container.
public class ForwardedIdentityValueRoundTripTests
{
    private readonly HeaderForwardedIdentityReader _reader = new();

    [Fact]
    public void A_formatted_value_round_trips_through_the_reader()
    {
        var actorId = Guid.NewGuid();

        var identity = _reader.Read(ForwardedIdentityValue.Format(actorId, new[] { Role.Support, Role.Admin }));

        identity.Should().NotBeNull();
        identity!.ActorId.Should().Be(actorId);
        identity.Roles.Should().Equal(Role.Support, Role.Admin);
    }

    [Fact]
    public void A_formatted_value_with_no_roles_round_trips_to_zero_roles()
    {
        var actorId = Guid.NewGuid();

        var identity = _reader.Read(ForwardedIdentityValue.Format(actorId, Array.Empty<Role>()));

        identity.Should().NotBeNull();
        identity!.ActorId.Should().Be(actorId);
        identity.Roles.Should().BeEmpty();
    }
}
