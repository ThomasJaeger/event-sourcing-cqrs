namespace EventSourcingCqrs.Domain.Abstractions;

public sealed class ValidationException : Exception
{
    public IReadOnlyList<ValidationError> Errors { get; }

    public ValidationException(IReadOnlyList<ValidationError> errors)
        : base(FormatMessage(errors))
    {
        Errors = errors;
    }

    private static string FormatMessage(IReadOnlyList<ValidationError> errors)
    {
        if (errors.Count == 0)
        {
            return "Validation failed.";
        }

        return "Validation failed: " + string.Join("; ", errors.Select(e => $"{e.Path}: {e.Message}"));
    }
}
