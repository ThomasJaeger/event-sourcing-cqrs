using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Context;

// One context per query dispatch. The bus builds a new instance, pushes it onto the accessor for the
// duration of the pipeline, and lets it fall out of scope afterwards. Init-only properties keep the
// instance immutable while any behavior or handler holds a reference. Parallel to CommandContext but
// narrower: a query stamps no metadata, so it carries no clock, correlation, causation, or service
// name. Roles defaults to empty and IsAuthenticatedUserQuery to false, so the bare AskAsync overload
// builds a pass-through context without restating either.
public sealed class QueryContext : IQueryContext
{
    public required Guid ActorId { get; init; }

    public IReadOnlyCollection<Role> Roles { get; init; } = [];

    public bool IsAuthenticatedUserQuery { get; init; }
}
