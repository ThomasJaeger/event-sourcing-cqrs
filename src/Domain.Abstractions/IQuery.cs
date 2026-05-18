namespace EventSourcingCqrs.Domain.Abstractions;

// TResult on the query type, not the handler, so the query bus can ask for a
// result whose type the call site cannot name as a generic parameter.
public interface IQuery<TResult> { }
