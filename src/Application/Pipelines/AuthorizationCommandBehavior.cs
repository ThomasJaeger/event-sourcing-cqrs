using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Pipelines;

// Authorizes a command against the dispatching principal's roles before the handler runs. Sits inside
// logging and before idempotency (ADR 0028): an unauthorized attempt is logged and consumes no
// idempotency storage. The TCommand constraint is IAuthorizedCommand, so the open-generic resolves
// only for commands that declare a required permission; for a plain ICommand (the timeout commands)
// the container omits this behavior by constraint.
//
// Enforcement runs for both authenticated user dispatch (AuthenticatedUser) and process-manager
// caused dispatch (SystemActor); the gate passes through only on None, the unenforced default for the
// worker and bare paths. The caused path dispatches under Role.System, whose permission set equals the
// commands the process managers dispatch, so every real caused command authorizes here. A caused
// command outside that set is a drift: it is denied, and the UnauthorizedCommandException (absent from
// TrySendAsync's domain-and-concurrency catch set) faults the workflow rather than becoming a Failed
// outcome the process manager would compensate on.
public sealed class AuthorizationCommandBehavior<TCommand> : ICommandPipelineBehavior<TCommand>
    where TCommand : IAuthorizedCommand
{
    private readonly ICommandContextAccessor _accessor;
    private readonly IPermissionAuthorizer _authorizer;

    public AuthorizationCommandBehavior(ICommandContextAccessor accessor, IPermissionAuthorizer authorizer)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(authorizer);
        _accessor = accessor;
        _authorizer = authorizer;
    }

    public Task HandleAsync(TCommand command, CommandHandlerDelegate next, CancellationToken ct)
    {
        var context = _accessor.Current;
        if (context is null or { AuthorizationMode: DispatchAuthorizationMode.None })
        {
            return next();
        }

        if (!_authorizer.IsAuthorized(context.Roles, TCommand.RequiredPermission))
        {
            throw new UnauthorizedCommandException(typeof(TCommand), TCommand.RequiredPermission);
        }

        return next();
    }
}
