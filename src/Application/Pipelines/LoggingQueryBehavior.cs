using System.Diagnostics;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Extensions.Logging;

namespace EventSourcingCqrs.Application.Pipelines;

// Logs query type plus duration. Queries do not populate CommandContext per
// setup decision 3, so there is no correlation ID to read on this side; if a
// later session introduces a query-side correlation seam, this behavior is the
// place to thread it through.
public sealed class LoggingQueryBehavior<TQuery, TResult> : IQueryPipelineBehavior<TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    private readonly ILogger<LoggingQueryBehavior<TQuery, TResult>> _logger;

    public LoggingQueryBehavior(ILogger<LoggingQueryBehavior<TQuery, TResult>> logger)
    {
        _logger = logger;
    }

    public async Task<TResult> HandleAsync(TQuery query, QueryHandlerDelegate<TResult> next, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await next();
            stopwatch.Stop();
            _logger.LogInformation(
                "Query {QueryType} completed in {ElapsedMs}ms",
                typeof(TQuery).Name, stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(
                ex,
                "Query {QueryType} failed in {ElapsedMs}ms",
                typeof(TQuery).Name, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
