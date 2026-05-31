using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Pipelines;

// Authorizes a query against the asking principal's roles before the handler runs, the read-side twin
// of AuthorizationCommandBehavior. Sits inside logging (ADR 0028): an unauthorized attempt is logged
// and then refused. The TQuery constraint adds IAuthorizedQuery to the behavior contract's
// IQuery<TResult>, so the open generic resolves only for queries that declare a required permission; a
// query that is not an IAuthorizedQuery does not close this behavior and the container omits it by
// constraint.
//
// Enforcement is on authenticated user queries only. The bare AskAsync path carries no authenticated
// principal and passes through, the same flag gate the command behavior uses. There is no caused query
// path to protect (queries are never dispatched by a process manager), so the gate keeps the bare path
// unenforced rather than guarding a workflow from a fault.
public sealed class AuthorizationQueryBehavior<TQuery, TResult> : IQueryPipelineBehavior<TQuery, TResult>
    where TQuery : IQuery<TResult>, IAuthorizedQuery
{
    private readonly IQueryContextAccessor _accessor;
    private readonly IPermissionAuthorizer _authorizer;

    public AuthorizationQueryBehavior(IQueryContextAccessor accessor, IPermissionAuthorizer authorizer)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(authorizer);
        _accessor = accessor;
        _authorizer = authorizer;
    }

    public Task<TResult> HandleAsync(TQuery query, QueryHandlerDelegate<TResult> next, CancellationToken ct)
    {
        var context = _accessor.Current;
        if (context is not { IsAuthenticatedUserQuery: true })
        {
            return next();
        }

        if (!_authorizer.IsAuthorized(context.Roles, TQuery.RequiredPermission))
        {
            throw new UnauthorizedQueryException(typeof(TQuery), TQuery.RequiredPermission);
        }

        return next();
    }
}
