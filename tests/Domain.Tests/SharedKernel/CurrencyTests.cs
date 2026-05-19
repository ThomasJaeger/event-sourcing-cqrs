using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.SharedKernel;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Domain.Tests.SharedKernel;

public class CurrencyTests
{
    [Theory]
    [InlineData("USD")]
    [InlineData("EUR")]
    [InlineData("XYZ")]
    public void Constructs_with_valid_ISO_4217_format(string code)
    {
        new Currency(code).Code.Should().Be(code);
    }

    [Theory]
    [InlineData("usd")]
    [InlineData("US")]
    [InlineData("USDX")]
    [InlineData("US1")]
    [InlineData("")]
    public void Rejects_invalid_format(string code)
    {
        Action act = () => new Currency(code);
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Rejects_null_code()
    {
        Action act = () => new Currency(null!);
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Two_currencies_with_same_code_are_equal()
    {
        new Currency("USD").Should().Be(new Currency("USD"));
    }

    [Fact]
    public void Static_accessor_equals_explicit_construction()
    {
        Currency.USD.Should().Be(new Currency("USD"));
    }

    [Theory]
    [InlineData("USD", 2)]
    [InlineData("EUR", 2)]
    [InlineData("GBP", 2)]
    [InlineData("JPY", 0)]
    [InlineData("BHD", 3)]
    public void DecimalPlaces_returns_table_value_for_known_currencies(string code, int expected)
    {
        new Currency(code).DecimalPlaces.Should().Be(expected);
    }

    [Fact]
    public void DecimalPlaces_defaults_to_2_for_unknown_currency()
    {
        new Currency("XYZ").DecimalPlaces.Should().Be(2);
    }
}
