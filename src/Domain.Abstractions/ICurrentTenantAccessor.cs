namespace EventSourcingCqrs.Domain.Abstractions;

// AsyncLocal-backed in production: the single source of the current tenant on an
// async flow. The command bus sets it once at the dispatch entry point, the same
// place and block it sets the command context, so the write path reads the tenant
// from one ambient rather than a hardcode at each construction site. EventMetadata
// and stream-id construction read it now; read isolation reads it later. Mirrors
// the ICommandContextAccessor pattern. Returns null when unset (no dispatch in
// flight); the write path treats a present command context with a null tenant as a
// dispatch-wiring regression and throws MissingTenantContextException rather than
// stamping the default tenant silently.
public interface ICurrentTenantAccessor
{
    TenantId? Current { get; set; }
}
