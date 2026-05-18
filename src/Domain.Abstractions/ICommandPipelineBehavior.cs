namespace EventSourcingCqrs.Domain.Abstractions;

// Pipeline behaviors compose around the handler in registration order. Each
// behavior decides whether to call next() (short-circuiting is how validation
// rejects before the handler runs) and what to do before and after.
public delegate Task CommandHandlerDelegate();

public interface ICommandPipelineBehavior<TCommand>
    where TCommand : ICommand
{
    Task HandleAsync(TCommand command, CommandHandlerDelegate next, CancellationToken ct);
}
