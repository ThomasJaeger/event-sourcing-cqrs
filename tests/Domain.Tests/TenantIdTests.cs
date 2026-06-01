using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Domain.Tests;

public class TenantIdTests
{
    private static readonly Guid Id = Guid.Parse("3f9a2c1d4b5e4f60a1b2c3d4e5f60718");

    [Fact]
    public void From_wraps_the_supplied_guid()
    {
        TenantId.From(Id).Value.Should().Be(Id);
    }

    [Fact]
    public void From_rejects_the_empty_guid()
    {
        var act = () => TenantId.From(Guid.Empty);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_accepts_a_well_formed_guid()
    {
        TenantId.Parse(Id.ToString()).Value.Should().Be(Id);
    }

    [Fact]
    public void Parse_accepts_the_dashless_n_format()
    {
        TenantId.Parse(Id.ToString("N")).Value.Should().Be(Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")] // parses to Guid.Empty
    public void Parse_rejects_malformed_input(string value)
    {
        var act = () => TenantId.Parse(value);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Tenant_ids_with_the_same_value_are_equal()
    {
        TenantId.From(Id).Should().Be(TenantId.From(Id));
    }

    [Fact]
    public void Tenant_ids_with_different_values_are_not_equal()
    {
        TenantId.From(Id).Should().NotBe(TenantId.From(Guid.NewGuid()));
    }

    [Fact]
    public void Parse_round_trips_a_constructed_tenant_id()
    {
        var original = TenantId.From(Id);
        TenantId.Parse(original.ToString()).Should().Be(original);
    }

    [Fact]
    public void ToString_returns_the_underlying_guid()
    {
        TenantId.From(Id).ToString().Should().Be(Id.ToString());
    }
}
