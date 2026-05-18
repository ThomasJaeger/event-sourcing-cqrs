namespace EventSourcingCqrs.Domain.Abstractions;

// No ICommandContext parameter. Ch 8's handler signature flows context via a
// scoped service the handler resolves from DI when it needs to. The accessor
// is set by the bus before the pipeline runs.
public interface ICommandHandler<TCommand>
    where TCommand : ICommand
{
    Task HandleAsync(TCommand command, CancellationToken ct);
}
