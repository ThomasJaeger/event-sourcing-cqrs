namespace EventSourcingCqrs.Domain.Abstractions;

// Process managers and host edges call SendAsync; the bus resolves the handler
// for the runtime command type, runs the pipeline, and invokes the handler in
// a new service scope per command.
public interface ICommandBus
{
    Task SendAsync(ICommand command, CancellationToken ct);
}
