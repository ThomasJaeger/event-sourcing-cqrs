using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Access.Events;

namespace EventSourcingCqrs.Domain.Access;

// Access bounded context's contribution to the event type registry, so the event store resolves
// the Access event payload types on read and replay.
public sealed class AccessEventTypeProvider : IEventTypeProvider
{
    public IEnumerable<Type> GetEventTypes() =>
    [
        typeof(RoleAssigned),
        typeof(RoleRevoked),
    ];
}
