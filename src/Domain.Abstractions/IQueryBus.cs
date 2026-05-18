namespace EventSourcingCqrs.Domain.Abstractions;

public interface IQueryBus
{
    Task<TResult> AskAsync<TResult>(IQuery<TResult> query, CancellationToken ct);
}
