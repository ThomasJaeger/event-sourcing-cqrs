using System.Globalization;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.SharedKernel;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Domain.Tests.SharedKernel;

public class MoneyTests
{
    [Fact]
    public void Addition_returns_sum_when_currencies_match()
    {
        var sum = new Money(10m, Currency.USD) + new Money(5m, Currency.USD);
        sum.Should().Be(new Money(15m, Currency.USD));
    }

    [Fact]
    public void Addition_throws_on_currency_mismatch()
    {
        Action act = () => _ = new Money(10m, Currency.USD) + new Money(5m, Currency.EUR);
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Subtraction_returns_difference_when_currencies_match()
    {
        var diff = new Money(10m, Currency.USD) - new Money(3m, Currency.USD);
        diff.Should().Be(new Money(7m, Currency.USD));
    }

    [Fact]
    public void Subtraction_throws_on_currency_mismatch()
    {
        Action act = () => _ = new Money(10m, Currency.USD) - new Money(3m, Currency.EUR);
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Scalar_multiplication_returns_product()
    {
        (new Money(10m, Currency.USD) * 3m).Should().Be(new Money(30m, Currency.USD));
    }

    [Theory]
    [InlineData(10.005, 10.00)] // banker's rounds .5 toward even (0 is even)
    [InlineData(10.015, 10.02)] // banker's rounds .5 toward even (2 is even)
    public void Scalar_multiplication_rounds_to_currency_precision_using_bankers_rounding(
        decimal amount, decimal expected)
    {
        var result = new Money(amount, Currency.USD) * 1m;
        result.Should().Be(new Money(expected, Currency.USD));
    }

    [Fact]
    public void Scalar_multiplication_on_JPY_rounds_to_zero_decimal_places()
    {
        var result = new Money(100.5m, Currency.JPY) * 1m;
        result.Should().Be(new Money(100m, Currency.JPY));
    }

    [Fact]
    public void Comparison_operators_compare_amounts_when_currencies_match()
    {
        var ten = new Money(10m, Currency.USD);
        var alsoTen = new Money(10m, Currency.USD);
        var five = new Money(5m, Currency.USD);
        (ten > five).Should().BeTrue();
        (ten >= five).Should().BeTrue();
        (five < ten).Should().BeTrue();
        (five <= ten).Should().BeTrue();
        (ten >= alsoTen).Should().BeTrue();
        (ten <= alsoTen).Should().BeTrue();
    }

    [Fact]
    public void Comparison_throws_on_currency_mismatch()
    {
        Action act = () => _ = new Money(10m, Currency.USD) < new Money(5m, Currency.EUR);
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void IsNegative_classifies_amount()
    {
        new Money(-1m, Currency.USD).IsNegative.Should().BeTrue();
        new Money(0m, Currency.USD).IsNegative.Should().BeFalse();
        new Money(1m, Currency.USD).IsNegative.Should().BeFalse();
    }

    [Fact]
    public void IsZero_true_only_for_zero_amount()
    {
        new Money(0m, Currency.USD).IsZero.Should().BeTrue();
        new Money(1m, Currency.USD).IsZero.Should().BeFalse();
        new Money(-1m, Currency.USD).IsZero.Should().BeFalse();
    }

    [Fact]
    public void Zero_factory_returns_zero_for_given_currency()
    {
        var zero = Money.Zero(Currency.USD);
        zero.Amount.Should().Be(0m);
        zero.Currency.Should().Be(Currency.USD);
    }

    [Fact]
    public void Allocate_int_with_no_remainder_returns_equal_parts()
    {
        var parts = new Money(30m, Currency.USD).Allocate(3);
        parts.Should().Equal(
            new Money(10m, Currency.USD),
            new Money(10m, Currency.USD),
            new Money(10m, Currency.USD));
    }

    [Fact]
    public void Allocate_int_pushes_remainder_to_leading_elements()
    {
        var parts = new Money(10m, Currency.USD).Allocate(3);
        parts.Should().Equal(
            new Money(3.34m, Currency.USD),
            new Money(3.33m, Currency.USD),
            new Money(3.33m, Currency.USD));
    }

    [Fact]
    public void Allocate_int_with_n_of_1_returns_whole()
    {
        var parts = new Money(10m, Currency.USD).Allocate(1);
        parts.Should().Equal(new Money(10m, Currency.USD));
    }

    [Fact]
    public void Allocate_by_ratios_distributes_proportionally()
    {
        var parts = new Money(10m, Currency.USD).Allocate(new[] { 7, 2, 1 });
        parts.Should().Equal(
            new Money(7m, Currency.USD),
            new Money(2m, Currency.USD),
            new Money(1m, Currency.USD));
    }

    [Fact]
    public void Allocate_by_ratios_sum_equals_original()
    {
        var original = new Money(10m, Currency.USD);
        var parts = original.Allocate(new[] { 1, 1, 1 });
        parts.Aggregate(Money.Zero(Currency.USD), (a, b) => a + b).Should().Be(original);
    }

    [Fact]
    public void Allocate_by_ratios_with_single_ratio_returns_whole()
    {
        var parts = new Money(10m, Currency.USD).Allocate(new[] { 5 });
        parts.Should().Equal(new Money(10m, Currency.USD));
    }

    [Fact]
    public void ToString_renders_USD_with_dollar_sign_and_two_decimals()
    {
        var money = new Money(100.50m, Currency.USD);

        money.ToString().Should().Be("$100.50");
    }

    [Fact]
    public void ToString_renders_USD_thousands_with_comma_separator()
    {
        var money = new Money(1_234_567.89m, Currency.USD);

        money.ToString().Should().Be("$1,234,567.89");
    }

    [Fact]
    public void ToString_renders_non_USD_with_code_suffix()
    {
        var money = new Money(50m, Currency.EUR);

        money.ToString().Should().Be("50.00 EUR");
    }

    [Fact]
    public void ToString_respects_decimal_places_from_currency()
    {
        var money = new Money(1000m, Currency.JPY);

        money.ToString().Should().Be("1,000 JPY");
    }

    [Fact]
    public void ToString_uses_invariant_culture_regardless_of_thread_culture()
    {
        var originalCulture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

            var money = new Money(1234.56m, Currency.USD);

            money.ToString().Should().Be("$1,234.56");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = originalCulture;
        }
    }
}
