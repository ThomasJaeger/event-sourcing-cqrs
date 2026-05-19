using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Pipelines;

// Runs every IValidator<TCommand> the container can resolve for this command
// type, aggregates their errors into one ValidationException, and only then
// throws. The all-at-once aggregation matters: a caller fixes every problem
// they have rather than chasing them one at a time.
//
// Zero registered validators is a passthrough. Concrete validators land in
// Phase 4 alongside aggregates that need them; the seam exists from here on.
public sealed class ValidationCommandBehavior<TCommand> : ICommandPipelineBehavior<TCommand>
    where TCommand : ICommand
{
    private readonly IEnumerable<IValidator<TCommand>> _validators;

    public ValidationCommandBehavior(IEnumerable<IValidator<TCommand>> validators)
    {
        _validators = validators;
    }

    public async Task HandleAsync(TCommand command, CommandHandlerDelegate next, CancellationToken ct)
    {
        var errors = new List<ValidationError>();
        foreach (var validator in _validators)
        {
            errors.AddRange(await validator.ValidateAsync(command, ct));
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        await next();
    }
}
