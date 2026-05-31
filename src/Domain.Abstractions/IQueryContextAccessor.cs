namespace EventSourcingCqrs.Domain.Abstractions;

// Carries the current IQueryContext through a query dispatch, the read-side twin of
// ICommandContextAccessor. The query bus sets Current before the pipeline runs and restores the
// previous value afterwards, so the authorization behavior and the ownership-filtering handlers read
// the asking actor without threading it through every signature.
public interface IQueryContextAccessor
{
    IQueryContext? Current { get; set; }
}
