using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Pipelines;

// Authorizes a command against the dispatching principal's roles before the handler runs. Sits inside
// logging and before idempotency (ADR 0028): an unauthorized attempt is logged and consumes no
// idempotency storage. The TCommand constraint is IAuthorizedCommand, so the open-generic resolves
// only for commands that declare a required permission; for a plain ICommand (the timeout commands)
// the container omits this behavior by constraint.
//
// Enforcement is on authenticated user dispatch only. The caused (process-manager) path, the System
// fallback, and the bare or worker paths carry no authenticated principal and pass through;
// authorization under the system actor lands at the caused-command commit. The flag gate is
// load-bearing: a throw on the caused path would escape TrySendAsync, which catches only domain and
// concurrency failures, and fault the workflow into outbox redelivery.
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
        if (context is not { IsAuthenticatedUserDispatch: true })
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
