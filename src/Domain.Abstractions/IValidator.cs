namespace EventSourcingCqrs.Domain.Abstractions;

// Zero or more validators per command type. ValidationBehavior runs them all
// and aggregates errors into a single ValidationException, so a caller sees
// every problem at once rather than fix-one-rerun-find-next.
public interface IValidator<TCommand>
    where TCommand : ICommand
{
    Task<IReadOnlyList<ValidationError>> ValidateAsync(TCommand command, CancellationToken ct);
}
