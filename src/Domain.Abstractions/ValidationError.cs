namespace EventSourcingCqrs.Domain.Abstractions;

public sealed record ValidationError(string Path, string Message);
