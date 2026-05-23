using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing.ReadModels;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Infrastructure.EventStore.InMemory;
using EventSourcingCqrs.ProcessManagers.Returns;

namespace EventSourcingCqrs.ProcessManagers.Tests;

// Stub OrderId-to-PaymentId lookup for the Return PM harness. Read path only; the
// projection-write surface is unused here.
internal sealed class StubOrderIdToPaymentIdStore : IOrderIdToPaymentIdStore
{
    private readonly Dictionary<Guid, Guid> _map = [];

    public void Map(Guid orderId, Guid paymentId) => _map[orderId] = paymentId;

    public Task<Guid?> GetPaymentIdAsync(Guid orderId, CancellationToken ct)
        => Task.FromResult(_map.TryGetValue(orderId, out var id) ? id : (Guid?)null);

    public Task<IOrderIdToPaymentIdUnitOfWork> BeginAsync(CancellationToken ct)
        => throw new NotSupportedException();

    public Task TruncateAsync(CancellationToken ct)
        => throw new NotSupportedException();
}

// Drives ReturnProcessManagerHandler against real repositories over a single
// shared InMemoryEventStore. Reuses RecordingCausedCommandBus and
// StubSkuToInventoryIdStore from the OrderFulfillment harness; adds the
// OrderId-to-PaymentId stub. No delay queue: the Return PM schedules no timeouts.
internal sealed class ReturnProcessManagerTestHarness
{
    private readonly InMemoryEventStore _store = new();
    private readonly ICommandContextAccessor _accessor = new AsyncLocalCommandContextAccessor();
    private readonly StubSkuToInventoryIdStore _skuLookup = new();
    private readonly StubOrderIdToPaymentIdStore _paymentLookup = new();
    private readonly EventStoreRepository<Shipment> _shipments;
    private readonly ProcessManagerRepository<ReturnProcessManager> _pms;
    private readonly ReturnProcessManagerHandler _handler;
    private long _position;

    public ReturnProcessManagerTestHarness()
    {
        _shipments = new EventStoreRepository<Shipment>(_store, _accessor);
        _pms = new ProcessManagerRepository<ReturnProcessManager>(_store, _accessor);
        Bus = new RecordingCausedCommandBus();
        _handler = new ReturnProcessManagerHandler(Bus, _pms, _shipments, _skuLookup, _paymentLookup);
    }

    public RecordingCausedCommandBus Bus { get; }

    public IReadOnlyList<RecordedDispatch> Dispatched => Bus.Dispatched;

    public void MapSku(string sku, Guid inventoryId) => _skuLookup.Map(sku, inventoryId);

    public void MapPayment(Guid orderId, Guid paymentId) => _paymentLookup.Map(orderId, paymentId);

    public Task SeedShipment(Shipment shipment) => _shipments.SaveAsync(shipment, CancellationToken.None);

    public Task Receive(ShipmentReturned payload, EventMetadata? metadata = null)
    {
        var context = new EventContext<ShipmentReturned>(payload, metadata ?? DefaultMetadata(), ++_position);
        return _handler.HandleAsync(context, CancellationToken.None);
    }

    public Task<ReturnProcessManager?> LoadPm(Guid orderId)
        => _pms.LoadAsync(
            StreamId.ForProcessManager(StreamPrefixes.ReturnPm, orderId),
            sid => new ReturnProcessManager(sid),
            CancellationToken.None);

    private static EventMetadata DefaultMetadata() => new(
        EventId: Guid.NewGuid(),
        CorrelationId: Guid.NewGuid(),
        CausationId: Guid.NewGuid(),
        ActorId: Guid.NewGuid(),
        Source: "test",
        SchemaVersion: 1,
        OccurredUtc: DateTime.UtcNow);
}
