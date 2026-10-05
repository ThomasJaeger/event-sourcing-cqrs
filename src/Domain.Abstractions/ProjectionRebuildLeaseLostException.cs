namespace EventSourcingCqrs.Domain.Abstractions;

public sealed class ProjectionRebuildLeaseLostException(string projectionName, Exception innerException)
    : InvalidOperationException(
        $"The rebuild lease for projection '{projectionName}' was lost. Its read model may be incomplete; rerun the rebuild.",
        innerException);
