namespace EventSourcingCqrs.Infrastructure.Versioning;

// Marker for a registered upcaster link. A composition root enumerates the IEventUpcaster instances
// a host contributes and hands them to EventUpcasterPipeline, the same way it enumerates
// IEventTypeProvider to build the registry. Implemented by Upcaster<TFrom, TTo>, so a host registers
// each concrete link as this type. Pattern from Chapter 11: Upcasting.
public interface IEventUpcaster { }
