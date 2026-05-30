using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using EventSourcingCqrs.Application.Authentication;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Authentication;

public class ForwardedIdentityValueTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef"; // exactly 32 characters

    [Fact]
    public void Format_joins_the_actor_and_roles_with_the_wire_separators()
    {
        var actorId = Guid.NewGuid();

        var value = ForwardedIdentityValue.Format(actorId, new[] { Role.Support, Role.Admin });

        value.Should().Be($"{actorId:N};Support,Admin");
    }

    [Fact]
    public void Format_with_no_roles_yields_the_actor_and_a_trailing_separator()
    {
        var actorId = Guid.NewGuid();

        var value = ForwardedIdentityValue.Format(actorId, Array.Empty<Role>());

        // The empty-roles canonical vector: the separator stays, the roles segment is empty.
        value.Should().Be($"{actorId:N};");
    }

    [Fact]
    public void The_empty_roles_value_signs_to_the_mac_of_that_literal_string()
    {
        var actorId = Guid.NewGuid();
        var value = ForwardedIdentityValue.Format(actorId, Array.Empty<Role>());

        var signature = Base64Url.EncodeToString(
            new ForwardedIdentitySigningKey(new ForwardedIdentitySigningOptions { Secret = Secret })
                .ComputeHash(value));

        // Recompute the MAC independently over the literal "{actorId:N};" to pin the signed bytes.
        var expected = Base64Url.EncodeToString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes($"{actorId:N};")));
        signature.Should().Be(expected);
    }
}
