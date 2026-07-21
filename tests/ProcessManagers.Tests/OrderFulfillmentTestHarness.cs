using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Infrastructure.EventStore.InMemory;
using EventSourcingCqrs.ProcessManagers.OrderFulfillment;

namespace EventSourcingCqrs.ProcessManagers.Tests;

internal sealed record RecordedDispatch(
    ICommand Command, EventMetadata Metadata, SystemActor Actor, string? IdempotencyKey);

// Captures every command a handler dispatches and returns a configurable outcome,
// short-circuiting the pipeline (so the commit-10 dispatcher-routing and commit-14
// InMemoryIdempotencyStore deferrals stay open). It mocks no store.
internal sealed class RecordingCausedCommandBus : ICausedCommandBus
{
    private readonly List<RecordedDispatch> _dispatched = [];

    /// <summary>
    /// The outcome to return for each dispatched command. Defaults to success.
    /// When returning <see cref="CommandOutcome.Failed"/>, the failure must be a
    /// <see cref="DomainException"/> or <see cref="ConcurrencyException"/>: the
    /// bus throws it, and the real <c>TrySendAsync</c> converts only those two
    /// types to a Failed outcome. Any other exception propagates, exactly as in
    /// production. Compensation tests satisfy this by failing with the expected
    /// type.
    /// </summary>
    public Func<ICommand, CommandOutcome> OutcomeFor { get; set; } = _ => CommandOutcome.Success();

    public IReadOnlyList<RecordedDispatch> Dispatched => _dispatched;

    public Task SendAsync(
        ICommand command,
        EventMetadata causingEventMetadata,
        SystemActor actor,
        string? idempotencyKey,
        CancellationToken ct)
    {
        _dispatched.Add(new RecordedDispatch(command, causingEventMetadata, actor, idempotencyKey));
        var outcome = OutcomeFor(command);
        if (!outcome.IsSuccess)
        {
            throw outcome.Failure!;
        }

        return Task.CompletedTask;
    }
}

internal sealed record ScheduledTimeout(
    ICommand Command, DateTimeOffset FireAtUtc, StreamId Stream, string Step,
    EventMetadata Causing, SystemActor Actor, string IdempotencyKey);

internal sealed record CancelledTimeout(StreamId Stream, string Step, string Reason);

// Captures ScheduleAsync/CancelAsync calls without persisting. Commit-24 tests
// assert the PM scheduled and cancelled the right timeouts at the right
// transitions; the delay-queue runtime (polling, dispatch, quarantine) is
// integration-tested at commit 17. CancelResult is configurable; the handler
// tests do not depend on the bool.
internal sealed class RecordingDelayQueue : IDelayQueue
{
    private readonly List<ScheduledTimeout> _scheduled = [];
    private readonly List<CancelledTimeout> _cancelled = [];

    public bool CancelResult { get; set; } = true;

    public IReadOnlyList<ScheduledTimeout> Scheduled => _scheduled;
    public IReadOnlyList<CancelledTimeout> Cancelled => _cancelled;

    public Task ScheduleAsync(
        ICommand command, DateTimeOffset fireAtUtc, StreamId scheduledByStream, string scheduledByStep,
        EventMetadata causingEventMetadata, SystemActor actor, string idempotencyKey, CancellationToken ct)
    {
        _scheduled.Add(new ScheduledTimeout(
            command, fireAtUtc, scheduledByStream, scheduledByStep, causingEventMetadata, actor, idempotencyKey));
        return Task.CompletedTask;
    }

    public Task<bool> CancelAsync(
        StreamId scheduledByStream, string scheduledByStep, string cancellationReason, CancellationToken ct)
    {
        _cancelled.Add(new CancelledTimeout(scheduledByStream, scheduledByStep, cancellationReason));
        return Task.FromResult(CancelResult);
    }
}

// The PM uses only the read path; the projection-write surface is unused here.
internal sealed class StubSkuToInventoryIdStore : ISkuToInventoryIdStore
{
    private readonly Dictionary<string, Guid> _map = [];

    public void Map(string sku, Guid inventoryId) => _map[sku] = inventoryId;

    public Task<Guid?> GetInventoryIdAsync(string sku, CancellationToken ct)
        => Task.FromResult(_map.TryGetValue(sku, out var id) ? id : (Guid?)null);

    public Task<ISkuToInventoryIdUnitOfWork> BeginAsync(CancellationToken ct)
        => throw new NotSupportedException();

    public Task TruncateAsync(CancellationToken ct)
        => throw new NotSupportedException();
}

// Drives OrderFulfillmentProcessManagerHandler and the two timeout command
// handlers against real repositories over a single shared InMemoryEventStore. The
// handlers are invoked directly: seed the aggregates a handler will load,
// configure the SKU map and the bus outcome, then Receive an inbound event or
// dispatch a timeout. Dispatched exposes commands sent; DelayQueue exposes
// timeouts scheduled and cancelled.
internal sealed class OrderFulfillmentTestHarness
{
    // The literal values of OrderFulfillmentSteps.AwaitPaymentTimeout and AwaitDispatchTimeout, which
    // are internal to the ProcessManagers assembly. The harness matches the scheduled timeout by
    // content, the same way DelayQueueSelfCancelTests does.
    private const string AwaitPaymentTimeoutStep = "await-payment-timeout";
    private const string AwaitDispatchTimeoutStep = "await-dispatch-timeout";

    private readonly InMemoryEventStore _store = new();
    private readonly ICommandContextAccessor _accessor = new AsyncLocalCommandContextAccessor();
    private readonly ICurrentTenantAccessor _tenantAccessor = new AsyncLocalCurrentTenantAccessor();
    private readonly StubSkuToInventoryIdStore _skuLookup = new();
    private readonly EventStoreRepository<Order> _orders;
    private readonly EventStoreRepository<Shipment> _shipments;
    private readonly ProcessManagerRepository<OrderFulfillmentProcessManager> _pms;
    private readonly OrderFulfillmentProcessManagerHandler _handler;
    private readonly TimeoutAwaitingPaymentForOrderHandler _paymentTimeoutHandler;
    private readonly TimeoutAwaitingDispatchForOrderHandler _dispatchTimeoutHandler;
    private long _position;

    public OrderFulfillmentTestHarness()
    {
        _orders = new EventStoreRepository<Order>(_store, _accessor, _tenantAccessor, new StubCurrentVersions());
        _shipments = new EventStoreRepository<Shipment>(_store, _accessor, _tenantAccessor, new StubCurrentVersions());
        _pms = new ProcessManagerRepository<OrderFulfillmentProcessManager>(_store, _accessor, _tenantAccessor);
        Bus = new RecordingCausedCommandBus();
        DelayQueue = new RecordingDelayQueue();
        var compensation = new OrderFulfillmentCompensation(Bus, _pms, DelayQueue);
        _handler = new OrderFulfillmentProcessManagerHandler(
            Bus, _pms, _orders, _shipments, _skuLookup, compensation, DelayQueue);
        _paymentTimeoutHandler =
            new TimeoutAwaitingPaymentForOrderHandler(_pms, compensation, _accessor, _tenantAccessor);
        _dispatchTimeoutHandler =
            new TimeoutAwaitingDispatchForOrderHandler(_pms, compensation, _accessor, _tenantAccessor);
    }

    public RecordingCausedCommandBus Bus { get; }

    public RecordingDelayQueue DelayQueue { get; }

    public IReadOnlyList<RecordedDispatch> Dispatched => Bus.Dispatched;

    public void MapSku(string sku, Guid inventoryId) => _skuLookup.Map(sku, inventoryId);

    public Task SeedOrder(Order order) => _orders.SaveAsync(order, CancellationToken.None);

    public Task SeedShipment(Shipment shipment) => _shipments.SaveAsync(shipment, CancellationToken.None);

    public async Task Receive<TEvent>(TEvent payload, EventMetadata? metadata = null)
        where TEvent : IDomainEvent
    {
        var causing = metadata ?? DefaultMetadata();
        var context = new EventContext<TEvent>(payload, causing, ++_position);
        var handler = (IProcessManagerHandler<TEvent>)(object)_handler;
        await InCausedContextAsync(
            causing, _handler.Actor, () => handler.HandleAsync(context, CancellationToken.None));
    }

    public Task DispatchTimeoutAwaitingPayment(Guid orderId)
        => DispatchTimeoutAsync(
            AwaitPaymentTimeoutStep,
            () => _paymentTimeoutHandler.HandleAsync(
                new TimeoutAwaitingPaymentForOrder(orderId), CancellationToken.None));

    public Task DispatchTimeoutAwaitingDispatch(Guid orderId)
        => DispatchTimeoutAsync(
            AwaitDispatchTimeoutStep,
            () => _dispatchTimeoutHandler.HandleAsync(
                new TimeoutAwaitingDispatchForOrder(orderId), CancellationToken.None));

    // Mirrors the delay queue's dispatch (ADR 0017). The due row persists the causing event's
    // correlation, causation, actor, and tenant at schedule time, and the command pipeline
    // establishes a context from them before the timeout handler runs. The harness recovers the same
    // values from the timeout the process manager scheduled, so a timeout dispatched here carries
    // what one dispatched in production carries rather than a stub.
    private Task DispatchTimeoutAsync(string step, Func<Task> dispatch)
    {
        var scheduled = DelayQueue.Scheduled.LastOrDefault(s => s.Step == step)
            ?? throw new InvalidOperationException(
                $"No '{step}' timeout was scheduled, so the harness has no causing event to dispatch " +
                "it under. Drive the process manager to the state that schedules it first.");

        return InCausedContextAsync(scheduled.Causing, scheduled.Actor, dispatch);
    }

    // The same push-and-restore InProcessMessageDispatcher performs around each process-manager
    // handler (ADR 0042): the tenant comes off the causing event, the command context off that event's
    // metadata and the handler's declared actor, and both restore in a finally so one drive does not
    // leak into the next.
    private async Task InCausedContextAsync(EventMetadata causing, SystemActor actor, Func<Task> work)
    {
        var previousContext = _accessor.Current;
        var previousTenant = _tenantAccessor.Current;
        _accessor.Current = new CausedCommandContext(causing, actor, TimeProvider.System);
        _tenantAccessor.Current = causing.Tenant;
        try
        {
            await work();
        }
        finally
        {
            _accessor.Current = previousContext;
            _tenantAccessor.Current = previousTenant;
        }
    }

    public Task<OrderFulfillmentProcessManager?> LoadPm(Guid orderId)
        => _pms.LoadAsync(
            StreamId.ForProcessManager(StreamPrefixes.OrderFulfillmentPm, WellKnownTenants.Default, orderId),
            sid => new OrderFulfillmentProcessManager(sid),
            CancellationToken.None);

    // The tenant-form twin of LoadPm: reads the PM from the stream composed under the supplied tenant,
    // so a cross-tenant case can assert which tenant's stream the handler saved the PM to.
    public Task<OrderFulfillmentProcessManager?> LoadPmForTenant(TenantId tenant, Guid orderId)
        => _pms.LoadAsync(
            StreamId.ForProcessManager(StreamPrefixes.OrderFulfillmentPm, tenant, orderId),
            sid => new OrderFulfillmentProcessManager(sid),
            CancellationToken.None);

    private static EventMetadata DefaultMetadata() => new(
        EventId: Guid.NewGuid(),
        CorrelationId: Guid.NewGuid(),
        CausationId: Guid.NewGuid(),
        ActorId: Guid.NewGuid(),
        Source: "test",
        SchemaVersion: 1,
        OccurredUtc: DateTime.UtcNow,
        Tenant: WellKnownTenants.Default);
}
