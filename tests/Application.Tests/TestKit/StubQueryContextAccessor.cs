using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Tests.TestKit;

internal sealed class StubQueryContextAccessor : IQueryContextAccessor
{
    public IQueryContext? Current { get; set; }
}
