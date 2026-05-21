using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Domain.Tests;

public class StreamIdTests
{
    private static readonly Guid Id = Guid.Parse("8d4f3e2a1b6c4d5e9f0a1b2c3d4e5f60");

    [Fact]
    public void ForAggregate_uses_the_lowercased_type_name_as_prefix()
    {
        StreamId.ForAggregate<Order>(Id).Value.Should().Be($"order:{Id:N}");
    }

    [Fact]
    public void ForProcessManager_uses_the_supplied_prefix()
    {
        StreamId.ForProcessManager(StreamPrefixes.OrderFulfillmentPm, Id)
            .Value.Should().Be($"pm-order-fulfillment:{Id:N}");
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

    [Theory]
    [InlineData("")]
    [InlineData("order")]                                              // no colon
    [InlineData(":8d4f3e2a1b6c4d5e9f0a1b2c3d4e5f60")]                  // empty prefix
    [InlineData("order:not-a-guid")]                                   // bad guid
    [InlineData("order:8d4f3e2a1b6c4d5e9f0a1b2c3d4e5f60:extra")]       // second colon
    [InlineData("order:8d4f3e2a-1b6c-4d5e-9f0a-1b2c3d4e5f60")]         // guid not in N format
    public void Parse_rejects_malformed_input(string value)
    {
        var act = () => StreamId.Parse(value);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Stream_ids_with_the_same_value_are_equal()
    {
        StreamId.ForAggregate<Order>(Id).Should().Be(StreamId.ForAggregate<Order>(Id));
    }

    [Fact]
    public void Stream_ids_with_different_values_are_not_equal()
    {
        StreamId.ForAggregate<Order>(Id)
            .Should().NotBe(StreamId.ForAggregate<Order>(Guid.NewGuid()));
    }

    [Fact]
    public void Parse_round_trips_a_constructed_stream_id()
    {
        var original = StreamId.ForAggregate<Order>(Id);
        StreamId.Parse(original.Value).Should().Be(original);
    }

    [Fact]
    public void ToString_returns_the_underlying_value()
    {
        StreamId.ForAggregate<Order>(Id).ToString().Should().Be($"order:{Id:N}");
    }
}
