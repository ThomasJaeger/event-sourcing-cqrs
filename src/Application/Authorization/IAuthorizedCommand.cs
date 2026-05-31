using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Authorization;

// A command that declares the permission a principal must hold to dispatch it. The marker lives in
// Application, not Domain.Abstractions, because it references Permission, which is Application-resident
// runtime policy; placing the marker lower would pull Permission down with it and invert the layering
// rule ADR 0026 records. The member is static abstract so the required permission is a property of the
// command type: the authorization behavior reads it off the closed generic without an instance.
public interface IAuthorizedCommand : ICommand
{
    static abstract Permission RequiredPermission { get; }
}
