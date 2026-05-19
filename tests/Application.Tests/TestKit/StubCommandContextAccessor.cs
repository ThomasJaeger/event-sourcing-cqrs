using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Tests.TestKit;

internal sealed class StubCommandContextAccessor : ICommandContextAccessor
{
    public ICommandContext? Current { get; set; }
}
