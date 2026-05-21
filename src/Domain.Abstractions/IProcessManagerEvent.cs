namespace EventSourcingCqrs.Domain.Abstractions;

// Marker for process-manager internal events, parallel to and distinct from
// IDomainEvent per ADR 0012. Bare like IDomainEvent; metadata lives on the
// envelope, not the payload.
public interface IProcessManagerEvent { }
