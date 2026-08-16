using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Projections.Tests;

// Records which unit-of-work members a projection invoked, so the coverage harness can
// close over the mutating surface rather than over the registry. Every member delegates to the
// real port, so the recorded run is the real run: the decorator observes, it does not stand in.
//
// This is a decorator on a port this repository owns, not a stand-in for a client it does not,
// so the don't-mock-what-you-don't-own rule is not in play. The connection underneath is a real
// PostgreSQL from the shared container.
internal sealed class RecordingOrderDetailStore(IOrderDetailStore inner, ISet<string> invoked)
    : IOrderDetailStore
{
    public async Task<IOrderDetailUnitOfWork> BeginAsync(CancellationToken ct)
        => new RecordingOrderDetailUnitOfWork(await inner.BeginAsync(ct), invoked);

    public Task<OrderDetailRow?> GetHeaderAsync(Guid orderId, CancellationToken ct)
        => inner.GetHeaderAsync(orderId, ct);

    public Task<IReadOnlyList<OrderDetailLineRow>> GetLinesAsync(Guid orderId, CancellationToken ct)
        => inner.GetLinesAsync(orderId, ct);

    public Task<IReadOnlyList<OrderDetailTimelineRow>> GetTimelineAsync(Guid orderId, CancellationToken ct)
        => inner.GetTimelineAsync(orderId, ct);

    public Task TruncateAsync(CancellationToken ct) => inner.TruncateAsync(ct);
}

internal sealed class RecordingOrderDetailUnitOfWork(IOrderDetailUnitOfWork inner, ISet<string> invoked)
    : IOrderDetailUnitOfWork
{
    private T Record<T>(string member, T result)
    {
        invoked.Add(member);
        return result;
    }

    public Task<long> GetCheckpointAsync(string projectionName, CancellationToken ct)
        => inner.GetCheckpointAsync(projectionName, ct);

    public Task CreateHeaderAsync(Guid orderId, Guid customerId, DateTime lastUpdatedUtc, CancellationToken ct)
        => Record(nameof(CreateHeaderAsync), inner.CreateHeaderAsync(orderId, customerId, lastUpdatedUtc, ct));

    public Task SetShippingAddressAsync(
        Guid orderId, Address shippingAddress, DateTime lastUpdatedUtc, CancellationToken ct)
        => Record(
            nameof(SetShippingAddressAsync),
            inner.SetShippingAddressAsync(orderId, shippingAddress, lastUpdatedUtc, ct));

    public Task ApplyPlacedAsync(
        Guid orderId, Money total, DateTime placedUtc, DateTime lastUpdatedUtc, CancellationToken ct)
        => Record(nameof(ApplyPlacedAsync), inner.ApplyPlacedAsync(orderId, total, placedUtc, lastUpdatedUtc, ct));

    public Task ApplyShippedAsync(Guid orderId, DateTime shippedUtc, DateTime lastUpdatedUtc, CancellationToken ct)
        => Record(nameof(ApplyShippedAsync), inner.ApplyShippedAsync(orderId, shippedUtc, lastUpdatedUtc, ct));

    public Task ApplyCancelledAsync(Guid orderId, DateTime cancelledUtc, DateTime lastUpdatedUtc, CancellationToken ct)
        => Record(nameof(ApplyCancelledAsync), inner.ApplyCancelledAsync(orderId, cancelledUtc, lastUpdatedUtc, ct));

    public Task ApplyCompletedAsync(Guid orderId, DateTime completedUtc, DateTime lastUpdatedUtc, CancellationToken ct)
        => Record(nameof(ApplyCompletedAsync), inner.ApplyCompletedAsync(orderId, completedUtc, lastUpdatedUtc, ct));

    public Task MarkReturnedAsync(Guid orderId, DateTime returnedUtc, DateTime lastUpdatedUtc, CancellationToken ct)
        => Record(nameof(MarkReturnedAsync), inner.MarkReturnedAsync(orderId, returnedUtc, lastUpdatedUtc, ct));

    public Task InsertLineAsync(OrderDetailLineRow row, CancellationToken ct)
        => Record(nameof(InsertLineAsync), inner.InsertLineAsync(row, ct));

    public Task DeleteLineAsync(Guid orderId, Guid lineId, CancellationToken ct)
        => Record(nameof(DeleteLineAsync), inner.DeleteLineAsync(orderId, lineId, ct));

    public Task AppendTimelineAsync(OrderDetailTimelineRow row, CancellationToken ct)
        => Record(nameof(AppendTimelineAsync), inner.AppendTimelineAsync(row, ct));

    public Task InsertShipmentMappingAsync(OrderDetailShipmentRow row, CancellationToken ct)
        => Record(nameof(InsertShipmentMappingAsync), inner.InsertShipmentMappingAsync(row, ct));

    public Task<Guid?> GetOrderIdByShipmentIdAsync(Guid shipmentId, CancellationToken ct)
        => inner.GetOrderIdByShipmentIdAsync(shipmentId, ct);

    public Task InsertPaymentMappingAsync(OrderDetailPaymentRow row, CancellationToken ct)
        => Record(nameof(InsertPaymentMappingAsync), inner.InsertPaymentMappingAsync(row, ct));

    public Task<Guid?> GetOrderIdByPaymentIdAsync(Guid paymentId, CancellationToken ct)
        => inner.GetOrderIdByPaymentIdAsync(paymentId, ct);

    public void PublishOnCommit(NotificationEnvelope envelope) => inner.PublishOnCommit(envelope);

    public Task CommitAsync(string projectionName, long position, CancellationToken ct)
        => inner.CommitAsync(projectionName, position, ct);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
