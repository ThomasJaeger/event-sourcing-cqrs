using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Domain.Tests.TestKit;

public sealed class AggregateTest<TAggregate> where TAggregate : AggregateRoot, new()
{
    private readonly List<IDomainEvent> _given = new();
    private Action<TAggregate>? _when;

    public AggregateTest<TAggregate> Given(params IDomainEvent[] events)
    {
        _given.AddRange(events);
        return this;
    }

    public AggregateTest<TAggregate> When(Action<TAggregate> action)
    {
        _when = action;
        return this;
    }

    public void Then(params IDomainEvent[] expected)
    {
        var aggregate = Rehydrate();
        _when!(aggregate);
        var emitted = aggregate.DequeueUncommittedEvents();
        emitted.Should().BeEquivalentTo(expected, options => options
            .WithStrictOrdering()
            .RespectingRuntimeTypes());
    }

    public ThenThrowsAssertion ThenThrows<TException>() where TException : Exception
    {
        var aggregate = Rehydrate();
        var action = () => _when!(aggregate);
        var ex = Assert.Throws<TException>(action);
        return new ThenThrowsAssertion(ex);
    }

    private TAggregate Rehydrate()
    {
        var aggregate = new TAggregate();
        foreach (var @event in _given)
            aggregate.ApplyHistoric(@event);
        return aggregate;
    }
}
