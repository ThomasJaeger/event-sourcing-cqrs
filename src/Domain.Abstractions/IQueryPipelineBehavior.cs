namespace EventSourcingCqrs.Domain.Abstractions;

public delegate Task<TResult> QueryHandlerDelegate<TResult>();

public interface IQueryPipelineBehavior<TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, QueryHandlerDelegate<TResult> next, CancellationToken ct);
}
