namespace EventSourcingCqrs.Domain.Abstractions;

// Thrown when a command context is in flight but no tenant was set on the async
// flow. The command bus sets the tenant accessor in the same block as the command
// context, so a present context with an unset tenant is a dispatch-wiring
// regression, not a caller error. Standalone and named (not a DomainException
// subclass, which is the aggregate-rejection type) so the write path fails closed
// here rather than stamping an event with the default tenant silently.
public sealed class MissingTenantContextException : Exception
{
    public MissingTenantContextException()
        : base("A command context is in flight but no tenant was set on the async flow. " +
               "The command bus sets the tenant accessor alongside the command context; an " +
               "unset tenant here is a dispatch-wiring regression.")
    {
    }
}
