using EventSourcingCqrs.Hosts.Workers;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Workers.Tests;

// RED for slice 3's Kurrent provider value, against the Workers host's parser. It is the public twin
// of the Api host's internal one, and the two carry byte-identical Read and ValidateConnectionString
// bodies, so this covers the shared parsing logic. The Api host's parser is internal and has no unit
// home; it is exercised end to end by the integration switch fact, which boots the Api host through it.
//
// These run RED today without naming EventStoreProvider.Kurrent, which does not exist yet: the
// recognition fact reads the parsed value's name, the message fact reads the rejection text, and the
// validation facts compose Read with ValidateConnectionString exactly as the host composes them, so
// the provider value flows out of Read rather than out of a not-yet-declared enum member.
public class EventStoreProviderSelectionTests
{
    [Fact]
    public void Kurrent_is_recognized_case_insensitively()
    {
        EventStoreProviderSelection.Read("kurrent").ToString().Should().Be("Kurrent");
    }

    // Green on write, and declared rather than dressed up. The parser twins learned DynamoDb as
    // scaffolding so this slice's real RED would be assertion-level rather than a compile error, and
    // that scaffolding is what makes this pass the moment it is written. It is a characterization of
    // the mapping, not a discovery: the parsers use an explicit string.Equals branch per value, so a
    // new value costs a branch, and this pins that the branch exists and is case-insensitive like
    // its three siblings.
    //
    // Only the Workers twin has a unit home. The Api and AdminConsole twins are internal, and the
    // integration switch fact below covers them end to end by booting a host through them.
    [Fact]
    public void DynamoDb_is_recognized_case_insensitively()
    {
        EventStoreProviderSelection.Read("dynamodb").ToString().Should().Be("DynamoDb");
    }

    // The list this fact names opens with every engine the parser learns. It read "all three" when
    // three shipped; DynamoDb makes it four. The intent is unchanged and is the reason the fact
    // exists: an operator who mistypes the key is told what the recognized values are, so the
    // message has to name them all rather than a stale subset. That is a fact-body edit, flagged as
    // such, and it goes red the day a fifth engine lands without updating the message, which is
    // what it is for.
    [Fact]
    public void The_unrecognized_value_error_names_every_recognized_provider()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => EventStoreProviderSelection.Read("nonsense"));

        ex.Message.Should().Contain("Postgres").And.Contain("SqlServer")
            .And.Contain("Kurrent").And.Contain("DynamoDb");
    }

    [Fact]
    public void An_esdb_connection_string_validates_under_the_Kurrent_provider()
    {
        var act = () => EventStoreProviderSelection.ValidateConnectionString(
            EventStoreProviderSelection.Read("Kurrent"), "esdb://localhost:2113?tls=false");

        act.Should().NotThrow();
    }

    [Fact]
    public void A_garbage_connection_string_under_Kurrent_throws_the_configuration_shaped_error()
    {
        var act = () => EventStoreProviderSelection.ValidateConnectionString(
            EventStoreProviderSelection.Read("Kurrent"), "this is not a connection string");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*EVENT_STORE_CONNECTION_STRING*");
    }
}
