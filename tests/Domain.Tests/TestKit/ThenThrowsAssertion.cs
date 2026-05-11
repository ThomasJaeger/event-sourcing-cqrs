using FluentAssertions;

namespace EventSourcingCqrs.Domain.Tests.TestKit;

public sealed class ThenThrowsAssertion
{
    private readonly Exception _exception;

    public ThenThrowsAssertion(Exception exception)
    {
        _exception = exception;
    }

    public Exception Which => _exception;

    public ThenThrowsAssertion WithMessage(string pattern)
    {
        _exception.Message.Should().Match(pattern);
        return this;
    }
}
