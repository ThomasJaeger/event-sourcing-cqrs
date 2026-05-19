using System.Diagnostics;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Extensions.Logging;

namespace EventSourcingCqrs.Application.Pipelines;

// Wraps every command with one log line on success and one on failure, both
// carrying the command type, the elapsed milliseconds, and the correlation ID
// the command bus put on ICommandContextAccessor. The accessor read falls back
// to Guid.Empty when nothing has populated Current, which keeps the behavior
// usable on dispatch paths that bypass the scope-creating bus (system-emitted
// commands today; intentional rather than accidental).
public sealed class LoggingCommandBehavior<TCommand> : ICommandPipelineBehavior<TCommand>
    where TCommand : ICommand
{
    private readonly ILogger<LoggingCommandBehavior<TCommand>> _logger;
    private readonly ICommandContextAccessor _accessor;

    public LoggingCommandBehavior(
        ILogger<LoggingCommandBehavior<TCommand>> logger,
        ICommandContextAccessor accessor)
    {
        _logger = logger;
        _accessor = accessor;
    }

    public async Task HandleAsync(TCommand command, CommandHandlerDelegate next, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var correlationId = _accessor.Current?.CorrelationId ?? Guid.Empty;
        try
        {
            await next();
            stopwatch.Stop();
            _logger.LogInformation(
                "Command {CommandType} completed in {ElapsedMs}ms (correlation {CorrelationId})",
                typeof(TCommand).Name, stopwatch.ElapsedMilliseconds, correlationId);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(
                ex,
                "Command {CommandType} failed in {ElapsedMs}ms (correlation {CorrelationId})",
                typeof(TCommand).Name, stopwatch.ElapsedMilliseconds, correlationId);
            throw;
        }
    }
}
