using EventSourcingCqrs.Application.Authentication;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Authentication;

// The reader's fail-closed branches: a malformed claim parses to null rather than a partial or guessed
// identity. The happy path is covered by ForwardedIdentityValueRoundTripTests.
public class HeaderForwardedIdentityReaderTests
{
    private readonly HeaderForwardedIdentityReader _reader = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Read_returns_null_for_a_missing_value(string? headerValue)
    {
        _reader.Read(headerValue).Should().BeNull();
    }

    [Fact]
    public void Read_returns_null_for_a_non_guid_actor()
    {
        _reader.Read("not-a-guid;").Should().BeNull();
    }

    [Fact]
    public void Read_returns_null_for_the_empty_guid_actor()
    {
        _reader.Read($"{Guid.Empty:N};").Should().BeNull();
    }

    [Theory]
    [InlineData("NotARole")]
    [InlineData("999")]
    public void Read_returns_null_for_an_undefined_role(string roleToken)
    {
        _reader.Read($"{Guid.NewGuid():N};{roleToken}").Should().BeNull();
    }
}
