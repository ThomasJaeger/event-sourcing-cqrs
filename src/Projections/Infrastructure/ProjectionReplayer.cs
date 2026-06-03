using System.Reflection;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Projections.Infrastructure;

// Pattern from Chapter 13: rebuilding and catching up a projection. The
// replayer drives a projection's IEventHandler<TEvent> handlers from the event
// store instead of from the outbox. Same handler code as the live tail; the
// only difference is the driver. The caller passes fromPosition 0 to rebuild
// from empty, or a stored checkpoint to catch up.
//
// Single-projection-scoped: the replayer is constructed for one projection
// instance and reflects over it once, here in the constructor, to build the
// event-type-to-handler dispatch table. It needs no service provider and no
// ICheckpointStore: it does not resolve handlers from DI (the projection
// instance carries them), and the checkpoint advance lives inside the
// projection's own handler.
public sealed class ProjectionReplayer
{
    private readonly IEventStore _eventStore;
    private readonly IProjection _projection;
    private readonly ICurrentTenantAccessor _tenantAccessor;
    private readonly IReadOnlyDictionary<Type, HandlerInvoker> _dispatchTable;

    public ProjectionReplayer(
        IEventStore eventStore, IProjection projection, ICurrentTenantAccessor tenantAccessor)
    {
        ArgumentNullException.ThrowIfNull(eventStore);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(tenantAccessor);
        _eventStore = eventStore;
        _projection = projection;
        _tenantAccessor = tenantAccessor;
        _dispatchTable = BuildDispatchTable(projection);
    }

    public async Task ReplayAsync(long fromPosition, CancellationToken ct)
    {
        await foreach (var envelope in _eventStore.ReadAllAsync(fromPosition, ct))
        {
            // Events the projection has no handler for are skipped, not errors:
            // the same contract InProcessMessageDispatcher follows on the live tail.
            if (_dispatchTable.TryGetValue(envelope.Payload.GetType(), out var invoker))
            {
                // The projection write path reads the current tenant off this accessor
                // (ADR 0031). Set it from the event's metadata for the invoke and restore
                // in finally, mirroring the live dispatcher's set-point. The accessor is a
                // singleton over AsyncLocal, so the value flows through the async invoke
                // into the unit of work without a DI scope here.
                var previousTenant = _tenantAccessor.Current;
                _tenantAccessor.Current = envelope.Metadata.Tenant;
                try
                {
                    await invoker.InvokeAsync(_projection, envelope, ct);
                }
                finally
                {
                    _tenantAccessor.Current = previousTenant;
                }
            }
        }
    }

    private static Dictionary<Type, HandlerInvoker> BuildDispatchTable(IProjection projection)
    {
        var table = new Dictionary<Type, HandlerInvoker>();
        foreach (var implemented in projection.GetType().GetInterfaces())
        {
            if (!implemented.IsGenericType
                || implemented.GetGenericTypeDefinition() != typeof(IEventHandler<>))
            {
                continue;
            }
            var eventType = implemented.GetGenericArguments()[0];
            table[eventType] = HandlerInvoker.For(eventType, implemented);
        }
        return table;
    }

    // The closed-generic reflection for one event type, resolved once: the
    // IEventHandler<TEvent>.HandleAsync method and the EventContext<TEvent>
    // constructor. This duplicates the shape of InProcessMessageDispatcher's
    // reflection. The two consumers differ (the dispatcher fans out through DI
    // from an OutboxMessage; this invokes one given projection from an
    // EventEnvelope), so they are not shared. A third consumer earns the
    // extraction.
    private sealed class HandlerInvoker
    {
        private readonly MethodInfo _handleMethod;
        private readonly ConstructorInfo _contextConstructor;

        private HandlerInvoker(MethodInfo handleMethod, ConstructorInfo contextConstructor)
        {
            _handleMethod = handleMethod;
            _contextConstructor = contextConstructor;
        }

        public static HandlerInvoker For(Type eventType, Type handlerInterface)
        {
            var handleMethod = handlerInterface.GetMethod(
                nameof(IEventHandler<IDomainEvent>.HandleAsync))!;
            var contextType = typeof(EventContext<>).MakeGenericType(eventType);
            return new HandlerInvoker(handleMethod, contextType.GetConstructors().Single());
        }

        public Task InvokeAsync(IProjection projection, EventEnvelope envelope, CancellationToken ct)
        {
            var context = _contextConstructor.Invoke(
                new object[] { envelope.Payload, envelope.Metadata, envelope.GlobalPosition });
            return (Task)_handleMethod.Invoke(projection, new object[] { context, ct })!;
        }
    }
}
