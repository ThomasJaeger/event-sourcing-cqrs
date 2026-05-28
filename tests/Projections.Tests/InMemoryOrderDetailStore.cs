using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Projections.Tests;

// In-memory IOrderDetailStore for the projection tests and the in-memory behaviour
// tests. Five dictionaries, one transaction: writes apply immediately and CommitAsync
// records the checkpoint, because the projection always commits and the tests assert
// on the committed result. Rollback is exercised against the real database in
// PostgresOrderDetailStoreTests (commit 15).
internal sealed class InMemoryOrderDetailStore : IOrderDetailStore
{
    private readonly Dictionary<Guid, OrderDetailRow> _headers = [];
    private readonly Dictionary<(Guid OrderId, Guid LineId), OrderDetailLineRow> _lines = [];
    private readonly Dictionary<(Guid OrderId, long GlobalPosition), OrderDetailTimelineRow> _timeline = [];
    private readonly Dictionary<Guid, OrderDetailShipmentRow> _shipments = [];
    private readonly Dictionary<Guid, OrderDetailPaymentRow> _payments = [];

    // Exposed so tests can assert the checkpoint advanced with the write.
    public Dictionary<string, long> Checkpoints { get; } = [];

    // Records each committed unit of work's staged notification, flushed on
    // CommitAsync so an uncommitted unit stages nothing the tests can observe.
    public List<NotificationEnvelope> StagedNotifications { get; } = [];

    public Task<IOrderDetailUnitOfWork> BeginAsync(CancellationToken ct)
        => Task.FromResult<IOrderDetailUnitOfWork>(new UnitOfWork(this));

    public Task<OrderDetailRow?> GetHeaderAsync(Guid orderId, CancellationToken ct)
        => Task.FromResult(_headers.GetValueOrDefault(orderId));

    public Task<IReadOnlyList<OrderDetailLineRow>> GetLinesAsync(Guid orderId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<OrderDetailLineRow>>(
            _lines.Values.Where(l => l.OrderId == orderId).ToList());

    public Task<IReadOnlyList<OrderDetailTimelineRow>> GetTimelineAsync(Guid orderId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<OrderDetailTimelineRow>>(
            _timeline.Values
                .Where(t => t.OrderId == orderId)
                .OrderBy(t => t.GlobalPosition)
                .ToList());

    public Task TruncateAsync(CancellationToken ct)
    {
        _headers.Clear();
        _lines.Clear();
        _timeline.Clear();
        _shipments.Clear();
        _payments.Clear();
        return Task.CompletedTask;
    }

    private sealed class UnitOfWork(InMemoryOrderDetailStore store) : IOrderDetailUnitOfWork
    {
        private NotificationEnvelope? _staged;

        public Task<long> GetCheckpointAsync(string projectionName, CancellationToken ct)
            => Task.FromResult(store.Checkpoints.GetValueOrDefault(projectionName));

        public Task CreateHeaderAsync(
            Guid orderId, Guid customerId, DateTime lastUpdatedUtc, CancellationToken ct)
        {
            // ON CONFLICT DO NOTHING: a redelivered draft keeps the first row.
            if (!store._headers.ContainsKey(orderId))
            {
                store._headers[orderId] = new OrderDetailRow(
                    OrderId: orderId,
                    CustomerId: customerId,
                    Status: OrderStatus.Draft,
                    PlacedUtc: null,
                    ShippedUtc: null,
                    CancelledUtc: null,
                    CompletedUtc: null,
                    ReturnedUtc: null,
                    Total: null,
                    ShippingAddress: null,
                    LastUpdatedUtc: lastUpdatedUtc);
            }
            return Task.CompletedTask;
        }

        public Task SetShippingAddressAsync(
            Guid orderId, Address shippingAddress, DateTime lastUpdatedUtc, CancellationToken ct)
            => Mutate(orderId, h => h with
            {
                ShippingAddress = shippingAddress,
                LastUpdatedUtc = lastUpdatedUtc,
            });

        public Task ApplyPlacedAsync(
            Guid orderId, Money total, DateTime placedUtc, DateTime lastUpdatedUtc, CancellationToken ct)
            => Mutate(orderId, h => h with
            {
                Status = OrderStatus.Placed,
                PlacedUtc = placedUtc,
                Total = total,
                LastUpdatedUtc = lastUpdatedUtc,
            });

        public Task ApplyShippedAsync(
            Guid orderId, DateTime shippedUtc, DateTime lastUpdatedUtc, CancellationToken ct)
            => Mutate(orderId, h => h with
            {
                Status = OrderStatus.Shipped,
                ShippedUtc = shippedUtc,
                LastUpdatedUtc = lastUpdatedUtc,
            });

        public Task ApplyCancelledAsync(
            Guid orderId, DateTime cancelledUtc, DateTime lastUpdatedUtc, CancellationToken ct)
            => Mutate(orderId, h => h with
            {
                Status = OrderStatus.Cancelled,
                CancelledUtc = cancelledUtc,
                LastUpdatedUtc = lastUpdatedUtc,
            });

        public Task ApplyCompletedAsync(
            Guid orderId, DateTime completedUtc, DateTime lastUpdatedUtc, CancellationToken ct)
            => Mutate(orderId, h => h with
            {
                Status = OrderStatus.Completed,
                CompletedUtc = completedUtc,
                LastUpdatedUtc = lastUpdatedUtc,
            });

        public Task MarkReturnedAsync(
            Guid orderId, DateTime returnedUtc, DateTime lastUpdatedUtc, CancellationToken ct)
            => Mutate(orderId, h => h with
            {
                ReturnedUtc = returnedUtc,
                LastUpdatedUtc = lastUpdatedUtc,
            });

        // Header updates no-op when the header is absent, mirroring an UPDATE that
        // touches zero rows. OrderDrafted creates the header before any later event,
        // so a valid stream always finds it.
        private Task Mutate(Guid orderId, Func<OrderDetailRow, OrderDetailRow> update)
        {
            if (store._headers.TryGetValue(orderId, out var existing))
            {
                store._headers[orderId] = update(existing);
            }
            return Task.CompletedTask;
        }

        public Task InsertLineAsync(OrderDetailLineRow row, CancellationToken ct)
        {
            // A remove frees the (OrderId, LineId) key, so the indexer matches the
            // plain INSERT the adapter runs: a re-add lands the new values.
            store._lines[(row.OrderId, row.LineId)] = row;
            return Task.CompletedTask;
        }

        public Task DeleteLineAsync(Guid orderId, Guid lineId, CancellationToken ct)
        {
            store._lines.Remove((orderId, lineId));
            return Task.CompletedTask;
        }

        public Task AppendTimelineAsync(OrderDetailTimelineRow row, CancellationToken ct)
        {
            // ON CONFLICT DO NOTHING: a redelivery at the same global position no-ops.
            store._timeline.TryAdd((row.OrderId, row.GlobalPosition), row);
            return Task.CompletedTask;
        }

        public Task InsertShipmentMappingAsync(OrderDetailShipmentRow row, CancellationToken ct)
        {
            store._shipments.TryAdd(row.ShipmentId, row);
            return Task.CompletedTask;
        }

        public Task<Guid?> GetOrderIdByShipmentIdAsync(Guid shipmentId, CancellationToken ct)
            => Task.FromResult(
                store._shipments.TryGetValue(shipmentId, out var row) ? row.OrderId : (Guid?)null);

        public Task InsertPaymentMappingAsync(OrderDetailPaymentRow row, CancellationToken ct)
        {
            store._payments.TryAdd(row.PaymentId, row);
            return Task.CompletedTask;
        }

        public Task<Guid?> GetOrderIdByPaymentIdAsync(Guid paymentId, CancellationToken ct)
            => Task.FromResult(
                store._payments.TryGetValue(paymentId, out var row) ? row.OrderId : (Guid?)null);

        public void PublishOnCommit(NotificationEnvelope envelope)
        {
            if (_staged is not null)
            {
                throw new InvalidOperationException(
                    "A unit of work stages at most one notification: one projection " +
                    "handler processes one event and makes one logical change per commit.");
            }
            _staged = envelope;
        }

        public Task CommitAsync(string projectionName, long position, CancellationToken ct)
        {
            // GREATEST, mirroring PostgresCheckpointStore's UPSERT.
            store.Checkpoints[projectionName] = Math.Max(
                store.Checkpoints.GetValueOrDefault(projectionName), position);
            // Flush the staged notification on commit, mirroring the Postgres unit
            // of work issuing pg_notify inside CommitAsync.
            if (_staged is not null)
            {
                store.StagedNotifications.Add(_staged);
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
