namespace EventSourcingCqrs.Application.Authorization;

// Thrown at composition when the structural check finds a concrete Application command that declares
// no required permission. A loud startup failure, mirroring the role-to-permission registry's
// constructor guard: a command that reached production without a permission declaration would dispatch
// unauthorized, so the gap is closed before the host serves a request.
public sealed class CommandPermissionDeclarationException : Exception
{
    public IReadOnlyCollection<Type> UndeclaredCommands { get; }

    public CommandPermissionDeclarationException(IReadOnlyCollection<Type> undeclaredCommands)
        : base("These command types declare no required permission and must implement " +
               "IAuthorizedCommand: " +
               string.Join(", ", undeclaredCommands.Select(t => t.Name)) + ".")
    {
        UndeclaredCommands = undeclaredCommands;
    }
}
