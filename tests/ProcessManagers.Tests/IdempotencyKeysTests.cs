using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.ProcessManagers.Tests;

public sealed class IdempotencyKeysTests
{
    private static StreamId PmStream() =>
        StreamId.ForProcessManager(StreamPrefixes.OrderFulfillmentPm, WellKnownTenants.Default, Guid.NewGuid());

    [Fact]
    public void ForProcessManager_without_a_sub_id_joins_stream_and_step()
    {
        var stream = PmStream();

        var key = IdempotencyKeys.ForProcessManager(stream, "authorize-payment");

        key.Should().Be($"{stream.Value}:authorize-payment");
    }

    [Fact]
    public void ForProcessManager_with_a_sub_id_appends_it_in_n_format()
    {
        var stream = PmStream();
        var subId = Guid.NewGuid();

        var key = IdempotencyKeys.ForProcessManager(stream, "reserve", subId);

        key.Should().Be($"{stream.Value}:reserve:{subId:N}");
    }

    [Fact]
    public void ForProcessManager_rejects_a_non_process_manager_stream()
    {
        var aggregateStream = StreamId.ForAggregate<Order>(WellKnownTenants.Default, Guid.NewGuid());

        var act = () => IdempotencyKeys.ForProcessManager(aggregateStream, "reserve");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForProcessManager_rejects_a_blank_step(string step)
    {
        var act = () => IdempotencyKeys.ForProcessManager(PmStream(), step);

        act.Should().Throw<ArgumentException>();
    }
}
