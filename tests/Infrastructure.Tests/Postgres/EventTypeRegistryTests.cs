using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

public class EventTypeRegistryTests
{
    [Fact]
    public void Register_succeeds_when_type_is_new()
    {
        var registry = new EventTypeRegistry()
            .Register<EventA>();

        registry.NameFor(typeof(EventA)).Should().Be(nameof(EventA));
        registry.TypeFor(nameof(EventA)).Should().Be(typeof(EventA));
    }

    [Fact]
    public void Register_returns_self_for_fluent_chaining()
    {
        var registry = new EventTypeRegistry()
            .Register<EventA>()
            .Register<EventB>();

        registry.NameFor(typeof(EventA)).Should().Be(nameof(EventA));
        registry.NameFor(typeof(EventB)).Should().Be(nameof(EventB));
    }

    [Fact]
    public void Register_with_explicit_name_uses_that_name()
    {
        var registry = new EventTypeRegistry()
            .Register<EventA>("event_a_v1");

        registry.NameFor(typeof(EventA)).Should().Be("event_a_v1");
        registry.TypeFor("event_a_v1").Should().Be(typeof(EventA));
    }

    [Fact]
    public void Register_throws_when_same_clr_type_registered_twice()
    {
        var registry = new EventTypeRegistry()
            .Register<EventA>();

        var act = () => registry.Register<EventA>("alias");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*EventA*already registered*alias*");
    }

    [Fact]
    public void Register_throws_when_same_storage_name_registered_twice()
    {
        var registry = new EventTypeRegistry()
            .Register<EventA>("collision");

        var act = () => registry.Register<EventB>("collision");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'collision'*already registered*EventA*EventB*");
    }

    [Fact]
    public void NameFor_throws_UnknownEventTypeException_when_type_not_registered()
    {
        var registry = new EventTypeRegistry();

        var act = () => registry.NameFor(typeof(EventA));

        var ex = act.Should().Throw<UnknownEventTypeException>().Which;
        ex.TypeName.Should().Contain(nameof(EventA));
        ex.StreamId.Should().BeNull();
    }

    [Fact]
    public void TypeFor_throws_UnknownEventTypeException_when_name_not_registered()
    {
        var registry = new EventTypeRegistry();

        var act = () => registry.TypeFor("not_a_real_type");

        var ex = act.Should().Throw<UnknownEventTypeException>().Which;
        ex.TypeName.Should().Be("not_a_real_type");
        ex.StreamId.Should().BeNull();
    }

    [Fact]
    public void TypeFor_is_case_sensitive()
    {
        var registry = new EventTypeRegistry()
            .Register<EventA>("EventA");

        var act = () => registry.TypeFor("eventa");

        act.Should().Throw<UnknownEventTypeException>();
    }

    private sealed record EventA : IDomainEvent;
    private sealed record EventB : IDomainEvent;
}
