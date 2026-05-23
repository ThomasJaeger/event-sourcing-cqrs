using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Projections.Tests;

// In-memory ICustomerSummaryStore for the projection tests and the in-memory
// behaviour tests. Writes apply immediately to the backing dictionaries and
// CommitAsync just records the checkpoint, because the projection always
// commits and the tests assert on the committed result. Rollback is exercised
// against the real database in PostgresCustomerSummaryStoreTests.
internal sealed class InMemoryCustomerSummaryStore : ICustomerSummaryStore
{
    private readonly Dictionary<Guid, CustomerSummaryRow> _summaries = [];
    private readonly Dictionary<(Guid CustomerId, Guid OrderId), CustomerSummaryOrderRow> _orders = [];

    // Exposed so tests can assert the checkpoint advanced with the write.
    public Dictionary<string, long> Checkpoints { get; } = [];

    public Task<ICustomerSummaryUnitOfWork> BeginAsync(CancellationToken ct)
        => Task.FromResult<ICustomerSummaryUnitOfWork>(new UnitOfWork(this));

    public Task<CustomerSummaryRow?> GetAsync(Guid customerId, CancellationToken ct)
        => Task.FromResult(_summaries.GetValueOrDefault(customerId));

    public Task TruncateAsync(CancellationToken ct)
    {
        _summaries.Clear();
        _orders.Clear();
        return Task.CompletedTask;
    }

    private sealed class UnitOfWork(InMemoryCustomerSummaryStore store) : ICustomerSummaryUnitOfWork
    {
        public Task<long> GetCheckpointAsync(string projectionName, CancellationToken ct)
            => Task.FromResult(store.Checkpoints.GetValueOrDefault(projectionName));

        public Task ApplyPlacementAsync(
            Guid customerId, Money total, DateTime placedUtc, DateTime lastUpdatedUtc, CancellationToken ct)
        {
            if (store._summaries.TryGetValue(customerId, out var existing))
            {
                store._summaries[customerId] = existing with
                {
                    OrderCount = existing.OrderCount + 1,
                    LifetimeValue = existing.LifetimeValue + total,
                    LastOrderUtc = placedUtc > existing.LastOrderUtc ? placedUtc : existing.LastOrderUtc,
                    LastUpdatedUtc = lastUpdatedUtc,
                };
            }
            else
            {
                store._summaries[customerId] = new CustomerSummaryRow(
                    CustomerId: customerId,
                    OrderCount: 1,
                    LifetimeValue: total,
                    LastOrderUtc: placedUtc,
                    LastUpdatedUtc: lastUpdatedUtc);
            }
            return Task.CompletedTask;
        }

        public Task ApplyCancellationAsync(
            Guid customerId, Money total, DateTime lastUpdatedUtc, CancellationToken ct)
        {
            // The projection calls this only after a found lookup row, so the
            // summary row exists. last_order_utc is left as-is (ADR 0019).
            if (store._summaries.TryGetValue(customerId, out var existing))
            {
                store._summaries[customerId] = existing with
                {
                    OrderCount = existing.OrderCount - 1,
                    LifetimeValue = existing.LifetimeValue - total,
                    LastUpdatedUtc = lastUpdatedUtc,
                };
            }
            return Task.CompletedTask;
        }

        public Task InsertOrderAsync(CustomerSummaryOrderRow row, CancellationToken ct)
        {
            store._orders[(row.CustomerId, row.OrderId)] = row;
            return Task.CompletedTask;
        }

        public Task<CustomerSummaryOrderRow?> GetOrderByOrderIdAsync(Guid orderId, CancellationToken ct)
            // The in-memory double scans by order id; the Postgres adapter uses
            // the ix_customer_summary_orders_order_id index.
            => Task.FromResult(store._orders.Values.FirstOrDefault(o => o.OrderId == orderId));

        public Task DeleteOrderAsync(Guid customerId, Guid orderId, CancellationToken ct)
        {
            store._orders.Remove((customerId, orderId));
            return Task.CompletedTask;
        }

        public Task CommitAsync(string projectionName, long position, CancellationToken ct)
        {
            // GREATEST, mirroring PostgresCheckpointStore's UPSERT.
            store.Checkpoints[projectionName] = Math.Max(
                store.Checkpoints.GetValueOrDefault(projectionName), position);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
