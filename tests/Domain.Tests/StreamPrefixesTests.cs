using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Domain.Tests;

// The family-prefix invariant the event store's SQL depends on. Four C# read paths route on
// StreamPrefixes.ProcessManagerPrefix to tell a PM stream from an aggregate one, and the event store's
// SQL carries the same prefix as a literal ('pm-%') that no const can reach. If a PM prefix ever stopped
// starting with the family prefix, the C# guards and the SQL filters would disagree about which rows are
// a process manager's, and the disagreement would be silent.
public class StreamPrefixesTests
{
    [Fact]
    public void Every_process_manager_prefix_starts_with_the_family_prefix()
    {
        StreamPrefixes.OrderFulfillmentPm.Should().StartWith(StreamPrefixes.ProcessManagerPrefix);
        StreamPrefixes.ReturnPm.Should().StartWith(StreamPrefixes.ProcessManagerPrefix);
    }

    [Fact]
    public void The_family_prefix_is_the_value_the_event_stores_sql_filters_on()
    {
        // Pinned as a literal on purpose. The SQL filters cannot reference the constant, so this is the
        // one place the two are stated together and checked against each other.
        StreamPrefixes.ProcessManagerPrefix.Should().Be("pm-");
    }
}
