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

    [Fact]
    public void The_unrecognized_value_error_names_all_three_recognized_providers()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => EventStoreProviderSelection.Read("nonsense"));

        ex.Message.Should().Contain("Postgres").And.Contain("SqlServer").And.Contain("Kurrent");
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
