using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;

namespace EventSourcingCqrs.Application.Queries.Sales;

// Captures one bounded stream, then reconstructs each prefix with the aggregate's own rules.
// No repository save, command dispatch, or projection replay runs on this path.
public sealed class OrderHistoryReader
{
    public const int MaxEvents = 500;
    private readonly IBoundedEventStreamReader _reader;

    public OrderHistoryReader(IBoundedEventStreamReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    public Task<OrderHistoryView?> ReadAsync(TenantId tenant, Guid orderId, CancellationToken ct)
        => ReadAsync(tenant, orderId, ownerCustomerId: null, ct);

    public async Task<OrderHistoryView?> ReadAsync(
        TenantId tenant, Guid orderId, Guid? ownerCustomerId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ct.ThrowIfCancellationRequested();
        var stream = StreamId.ForAggregate<Order>(tenant, orderId);

        if (ownerCustomerId is not null)
        {
            // Read ownership before the rest of the history. A foreign customer's malformed or
            // oversized history must be indistinguishable from a missing order.
            try
            {
                var first = await ReadWindowAsync(stream, 1, ct);
                if (first.Events.Count == 0) return null;
                var draft = RequireDraft(first.Events[0], stream, tenant, orderId);
                if (draft.CustomerId != ownerCustomerId.Value) return null;
            }
            catch (OrderHistoryIncompleteException)
            {
                // No readable ownership proof means no permission to reveal this history's condition.
                return null;
            }
        }

        var window = await ReadWindowAsync(stream, MaxEvents, ct);
        if (window.Events.Count == 0) return null;
        var firstDraft = RequireDraft(window.Events[0], stream, tenant, orderId);
        if (ownerCustomerId is not null && firstDraft.CustomerId != ownerCustomerId.Value) return null;
        if (window.HasMore || window.Events.Count > MaxEvents) throw new OrderHistoryTooLongException();

        var order = new Order();
        var steps = new List<OrderHistoryStep>(window.Events.Count);
        foreach (var envelope in window.Events)
        {
            ct.ThrowIfCancellationRequested();
            // Comparable states require distinct active line identities. Removing a line releases
            // that identity; this check does not rerun today's command rules against stored events.
            if (envelope.StreamId != stream || envelope.Metadata.Tenant != tenant
                || envelope.StreamVersion != steps.Count + 1 || OrderIdOf(envelope.Payload) != orderId
                || (steps.Count > 0 && envelope.Payload is OrderDrafted)
                || (envelope.Payload is OrderLineAdded added && order.Lines.Any(line => line.LineId == added.LineId)))
            {
                throw new OrderHistoryIncompleteException();
            }
            try
            {
                var description = Describe(envelope.Payload, order);
                order.ApplyHistoric(envelope.Payload);
                steps.Add(new OrderHistoryStep(envelope.StreamVersion, envelope.OccurredUtc,
                    description, order.ToSnapshot(), order.Total));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or DomainException)
            {
                throw new OrderHistoryIncompleteException(ex);
            }
        }
        return new OrderHistoryView(orderId, steps.AsReadOnly());
    }

    private async Task<EventStreamWindow> ReadWindowAsync(StreamId stream, int limit, CancellationToken ct)
    {
        try
        {
            return await _reader.ReadAsync(stream, limit, ct);
        }
        catch (EventStreamReadException ex)
        {
            throw new OrderHistoryIncompleteException(ex);
        }
    }

    private static OrderDrafted RequireDraft(EventEnvelope first, StreamId stream, TenantId tenant, Guid orderId)
    {
        if (first.StreamVersion != 1 || first.StreamId != stream || first.Metadata.Tenant != tenant
            || first.Payload is not OrderDrafted draft || draft.OrderId != orderId)
        {
            throw new OrderHistoryIncompleteException();
        }
        return draft;
    }

    private static Guid? OrderIdOf(IDomainEvent payload) => payload switch
    {
        OrderDrafted e => e.OrderId,
        OrderLineAdded e => e.OrderId,
        OrderLineRemoved e => e.OrderId,
        ShippingAddressSet e => e.OrderId,
        OrderPlaced e => e.OrderId,
        OrderCancelled e => e.OrderId,
        OrderShipped e => e.OrderId,
        OrderCompleted e => e.OrderId,
        _ => null,
    };

    private static string Describe(IDomainEvent payload, Order before) => payload switch
    {
        OrderDrafted => "Order draft created",
        OrderLineAdded e => $"Added {e.Quantity} × {e.Sku}",
        OrderLineRemoved e => RemovedLineDescription(before, e.LineId),
        ShippingAddressSet e => $"Shipping address set to {e.ShippingAddress.Street}, {e.ShippingAddress.City}",
        OrderPlaced => "Order placed",
        OrderCancelled e => $"Order cancelled: {e.Reason}",
        OrderShipped e => $"Order shipped with {e.Carrier}; tracking {e.TrackingNumber}",
        OrderCompleted => "Order completed",
        _ => throw new OrderHistoryIncompleteException(),
    };

    private static string RemovedLineDescription(Order order, Guid lineId)
    {
        var line = order.Lines.FirstOrDefault(x => x.LineId == lineId);
        return line is null ? "Item removed" : $"Removed {line.Quantity} × {line.Sku}";
    }
}
