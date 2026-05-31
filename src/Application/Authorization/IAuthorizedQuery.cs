namespace EventSourcingCqrs.Application.Authorization;

// A query that declares the permission a principal must hold to run it, the read-side twin of
// IAuthorizedCommand. It lives in Application, not Domain.Abstractions, because it references
// Permission, which is Application-resident runtime policy; placing the marker lower would pull
// Permission down with it and invert the layering rule ADR 0026 records. Unlike IAuthorizedCommand it
// does not extend the query contract: IQuery<TResult> is generic with no non-generic marker, so a query
// implements both IQuery<TResult> and IAuthorizedQuery, and the composition-time walk pairs the two
// through a reflection check that the type closes IQuery<> (the same check QueryTypeRegistry's guard
// uses, ADR 0022). The member is static abstract so the required permission is a property of the query
// type: the authorization behavior reads it off the closed generic without an instance.
public interface IAuthorizedQuery
{
    static abstract Permission RequiredPermission { get; }
}
