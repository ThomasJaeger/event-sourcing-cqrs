namespace EventSourcingCqrs.Domain.Abstractions;

// Pure marker. Ch 8 acknowledges some teams skip the interface entirely; the
// marker stays so generic constraints (ICommandHandler<TCommand>, validators,
// pipeline behaviors) reject non-commands at compile time.
public interface ICommand { }
