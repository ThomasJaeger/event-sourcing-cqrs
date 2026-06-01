using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Domain.Tests;

public class StreamIdTests
{
    private static readonly Guid Id = Guid.Parse("8d4f3e2a1b6c4d5e9f0a1b2c3d4e5f60");
    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("1f2e3d4c5b6a79887766554433221100"));

    [Fact]
    public void ForAggregate_for_the_default_tenant_renders_the_two_segment_form()
    {
        StreamId.ForAggregate<Order>(WellKnownTenants.Default, Id)
            .Value.Should().Be($"order:{Id:N}");
    }

    [Fact]
    public void ForAggregate_for_a_non_default_tenant_renders_the_three_segment_form()
    {
        StreamId.ForAggregate<Order>(Tenant, Id)
            .Value.Should().Be($"order:{Tenant.Value:N}:{Id:N}");
    }

    [Fact]
    public void ForProcessManager_for_the_default_tenant_renders_the_two_segment_form()
    {
        StreamId.ForProcessManager(StreamPrefixes.OrderFulfillmentPm, WellKnownTenants.Default, Id)
            .Value.Should().Be($"pm-order-fulfillment:{Id:N}");
    }

    [Fact]
    public void ForProcessManager_for_a_non_default_tenant_renders_the_three_segment_form()
    {
        StreamId.ForProcessManager(StreamPrefixes.OrderFulfillmentPm, Tenant, Id)
            .Value.Should().Be($"pm-order-fulfillment:{Tenant.Value:N}:{Id:N}");
    }

    [Fact]
    public void The_tenant_segment_is_the_compact_form_not_hyphenated()
    {
        StreamId.ForAggregate<Order>(Tenant, Id)
            .Value.Should().Be($"order:{Tenant.Value:N}:{Id:N}")
            .And.NotContain(Tenant.Value.ToString());
    }

    [Fact]
    public void Parse_accepts_a_well_formed_aggregate_stream_id()
    {
        StreamId.Parse($"order:{Id:N}").Value.Should().Be($"order:{Id:N}");
    }

    [Fact]
    public void Parse_accepts_a_hyphenated_pm_prefix()
    {
        StreamId.Parse($"pm-order-fulfillment:{Id:N}")
            .Value.Should().Be($"pm-order-fulfillment:{Id:N}");
    }

    [Fact]
    public void Parse_of_a_two_segment_id_resolves_the_default_tenant()
    {
        StreamId.Parse($"order:{Id:N}")
            .Should().Be(StreamId.ForAggregate<Order>(WellKnownTenants.Default, Id));
    }

    [Fact]
    public void Parse_of_a_three_segment_id_recovers_the_non_default_tenant()
    {
        StreamId.Parse($"order:{Tenant.Value:N}:{Id:N}")
            .Should().Be(StreamId.ForAggregate<Order>(Tenant, Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("order")]                                                                                          // no colon
    [InlineData(":8d4f3e2a1b6c4d5e9f0a1b2c3d4e5f60")]                                                              // empty prefix
    [InlineData("order:not-a-guid")]                                                                               // bad guid
    [InlineData("order:8d4f3e2a1b6c4d5e9f0a1b2c3d4e5f60:extra")]                                                   // three segments, last not a guid
    [InlineData("order:not-a-guid:8d4f3e2a1b6c4d5e9f0a1b2c3d4e5f60")]                                              // three segments, middle not a guid
    [InlineData("order:8d4f3e2a1b6c4d5e9f0a1b2c3d4e5f60:8d4f3e2a1b6c4d5e9f0a1b2c3d4e5f60:8d4f3e2a1b6c4d5e9f0a1b2c3d4e5f60")]  // four segments
    [InlineData("order:8d4f3e2a-1b6c-4d5e-9f0a-1b2c3d4e5f60")]                                                     // guid not in N format
    public void Parse_rejects_malformed_input(string value)
    {
        var act = () => StreamId.Parse(value);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Stream_ids_with_the_same_value_are_equal()
    {
        StreamId.ForAggregate<Order>(WellKnownTenants.Default, Id)
            .Should().Be(StreamId.ForAggregate<Order>(WellKnownTenants.Default, Id));
    }

    [Fact]
    public void Stream_ids_with_different_values_are_not_equal()
    {
        StreamId.ForAggregate<Order>(WellKnownTenants.Default, Id)
            .Should().NotBe(StreamId.ForAggregate<Order>(WellKnownTenants.Default, Guid.NewGuid()));
    }

    [Fact]
    public void Parse_round_trips_a_default_tenant_stream_id()
    {
        var original = StreamId.ForAggregate<Order>(WellKnownTenants.Default, Id);
        StreamId.Parse(original.Value).Should().Be(original);
    }

    [Fact]
    public void Parse_round_trips_a_non_default_tenant_stream_id()
    {
        var original = StreamId.ForAggregate<Order>(Tenant, Id);
        StreamId.Parse(original.Value).Should().Be(original);
    }

    [Fact]
    public void ToString_returns_the_underlying_value()
    {
        StreamId.ForAggregate<Order>(WellKnownTenants.Default, Id).ToString()
            .Should().Be($"order:{Id:N}");
    }

    [Fact]
    public void A_three_segment_pm_stream_id_keeps_the_leading_pm_prefix()
    {
        StreamId.ForProcessManager(StreamPrefixes.OrderFulfillmentPm, Tenant, Id)
            .Value.Should().StartWith("pm-");
    }

    [Fact]
    public void A_three_segment_aggregate_stream_id_has_no_leading_pm_prefix()
    {
        StreamId.ForAggregate<Order>(Tenant, Id)
            .Value.Should().NotStartWith("pm-");
    }
}
