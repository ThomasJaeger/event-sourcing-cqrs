using EventSourcingCqrs.Application.Authentication;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Authentication;

public class ForwardedIdentitySigningKeyTests
{
    private const string ValidSecret = "0123456789abcdef0123456789abcdef"; // exactly 32 characters

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructing_without_a_secret_throws_the_not_set_message(string? secret)
    {
        var act = () => new ForwardedIdentitySigningKey(
            new ForwardedIdentitySigningOptions { Secret = secret! });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("FORWARDED_IDENTITY_SIGNING_SECRET is not set.");
    }

    [Fact]
    public void Constructing_with_an_under_length_secret_throws_the_floor_message()
    {
        var thirtyOne = new string('a', 31);

        var act = () => new ForwardedIdentitySigningKey(
            new ForwardedIdentitySigningOptions { Secret = thirtyOne });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("FORWARDED_IDENTITY_SIGNING_SECRET must be at least 32 characters.");
    }

    [Fact]
    public void Constructing_with_a_thirty_two_character_secret_succeeds()
    {
        var act = () => new ForwardedIdentitySigningKey(
            new ForwardedIdentitySigningOptions { Secret = new string('a', 32) });

        act.Should().NotThrow();
    }

    [Fact]
    public void ComputeHash_is_deterministic_and_returns_a_thirty_two_byte_mac()
    {
        var key = new ForwardedIdentitySigningKey(
            new ForwardedIdentitySigningOptions { Secret = ValidSecret });

        var first = key.ComputeHash("the-value");
        var second = key.ComputeHash("the-value");

        first.Should().HaveCount(32);   // HMAC-SHA256 output width.
        first.Should().Equal(second);
    }
}
