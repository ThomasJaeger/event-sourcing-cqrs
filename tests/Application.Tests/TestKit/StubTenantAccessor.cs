using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Tests.TestKit;

internal sealed class StubTenantAccessor : ICurrentTenantAccessor
{
    public TenantId? Current { get; set; }
}
