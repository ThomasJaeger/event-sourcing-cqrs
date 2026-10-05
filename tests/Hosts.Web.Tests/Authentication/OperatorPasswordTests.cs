using EventSourcingCqrs.Hosts.Web.Authentication;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Authentication;

public sealed class OperatorPasswordTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a password hash")]
    public void Missing_or_malformed_configuration_fails_closed(string? hash)
    {
        Action create = () => new OperatorPassword(hash);
        create.Should().Throw<InvalidOperationException>().WithMessage("*OperatorAuthentication:PasswordHash*");
    }

    [Fact]
    public void Structurally_invalid_hash_configuration_is_rejected_at_startup()
    {
        var bytes = new byte[61];
        bytes[0] = 1;
        Action create = () => new OperatorPassword(Convert.ToBase64String(bytes));
        create.Should().Throw<InvalidOperationException>().WithMessage("*OperatorAuthentication:PasswordHash*");
    }

    [Fact]
    public void Only_the_password_matching_the_salted_hash_is_accepted()
    {
        const string password = "A test-only operator password!";
        var hash = new PasswordHasher<string>().HashPassword("operator", password);
        var credential = new OperatorPassword(hash);
        credential.Verify(password).Should().BeTrue();
        credential.Verify("incorrect").Should().BeFalse();
        credential.Verify(null).Should().BeFalse();
        credential.Verify(new string('x', 1025)).Should().BeFalse();
    }
}
