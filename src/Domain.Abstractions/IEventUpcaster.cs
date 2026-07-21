namespace EventSourcingCqrs.Domain.Abstractions;

// Marker for a registered upcaster link, declared in Domain.Abstractions beside IDomainEvent so a
// domain lineage's upcaster references only domain types. A composition root enumerates the
// IEventUpcaster instances a host contributes and hands them to the EventUpcasterPipeline in the
// Versioning project, the same way it enumerates IEventTypeProvider to build the registry.
// Implemented by Upcaster<TFrom, TTo>, so a host registers each concrete link as this type. Pattern
// from Chapter 11: Upcasting.
public interface IEventUpcaster { }
