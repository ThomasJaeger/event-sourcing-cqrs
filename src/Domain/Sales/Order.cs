using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Domain.Sales;

public sealed class Order : AggregateRoot, ISnapshotSource<OrderSnapshot>
{
    private readonly List<OrderLine> _lines = [];
    private OrderStatus _status;
    private Guid _customerId;
    private Address? _shippingAddress;

    public Guid CustomerId => _customerId;
    public IReadOnlyList<OrderLine> Lines => _lines;
    public OrderStatus Status => _status;
    public Money Total => _lines.Aggregate(Money.Zero(Currency.USD), (sum, l) => sum + l.Subtotal);
    // Late-set: null until SetShippingAddress runs, which Place requires before
    // it succeeds. The OrderFulfillment PM reads it to build ScheduleShipment.
    public Address? ShippingAddress => _shippingAddress;

    // Public for event-sourced rehydration. Use Order.Draft(...) to create a new order from a command.
    public Order() { }

    public static Order Draft(Guid orderId, Guid customerId, DateTime utcNow, string channel)
    {
        CommandInput.RequireText(channel, "Order channel");
        var order = new Order();
        order.Raise(new OrderDrafted(orderId, customerId, utcNow, channel));
        return order;
    }

    public void AddLine(Guid lineId, string sku, int quantity, Money unitPrice, DateTime utcNow)
    {
        if (_status != OrderStatus.Draft)
        {
            throw new DomainException($"Cannot add line to order {Id}: order is {_status}.");
        }
        CommandInput.RequireText(sku, "SKU");
        if (quantity <= 0)
        {
            throw new DomainException($"Cannot add line {lineId}: quantity must be positive.");
        }
        if (_lines.Any(l => l.LineId == lineId))
        {
            throw new DomainException($"Line {lineId} already exists on order {Id}.");
        }
        EnsureValidLinePrice(quantity, unitPrice);
        Raise(new OrderLineAdded(Id, lineId, sku, quantity, unitPrice, utcNow));
    }

    private void EnsureValidLinePrice(int quantity, Money unitPrice)
    {
        CommandInput.RequireAmount(unitPrice, "Unit price");
        if (unitPrice.IsNegative)
            throw new DomainException("Unit price must not be negative.");
        if (unitPrice.Currency != Currency.USD)
            throw new DomainException("Order line currency must be USD.");
        CommandInput.RequireAmount(Total + unitPrice * quantity, "Order total");
    }

    public void RemoveLine(Guid lineId, DateTime utcNow)
    {
        if (_status != OrderStatus.Draft)
        {
            throw new DomainException($"Cannot remove line from order {Id}: order is {_status}.");
        }
        if (!_lines.Any(l => l.LineId == lineId))
        {
            throw new DomainException($"Line {lineId} not found on order {Id}.");
        }
        Raise(new OrderLineRemoved(Id, lineId, utcNow));
    }

    public void SetShippingAddress(Address address, DateTime utcNow)
    {
        if (_status != OrderStatus.Draft)
        {
            throw new DomainException($"Cannot set shipping address on order {Id}: order is {_status}.");
        }
        CommandInput.RequireAddress(address);
        Raise(new ShippingAddressSet(Id, address, utcNow));
    }

    public void Place(DateTime utcNow)
    {
        if (_status != OrderStatus.Draft)
        {
            throw new DomainException($"Cannot place order {Id}: order is {_status}.");
        }
        if (_lines.Count == 0)
        {
            throw new DomainException($"Cannot place order {Id}: no lines.");
        }
        if (_shippingAddress is null)
        {
            throw new DomainException($"Cannot place order {Id}: shipping address not set.");
        }
        Raise(new OrderPlaced(Id, _customerId, Total, utcNow));
    }

    public void Cancel(string reason, Guid issuedByUserId, DateTime utcNow)
    {
        if (_status == OrderStatus.Cancelled)
        {
            throw new DomainException($"Cannot cancel order {Id}: already cancelled.");
        }
        if (_status == OrderStatus.Shipped)
        {
            throw new DomainException($"Cannot cancel order {Id}: already shipped.");
        }
        if (_status == OrderStatus.Completed)
        {
            throw new DomainException($"Cannot cancel order {Id}: already completed.");
        }
        CommandInput.RequireText(reason, "Cancellation reason");
        Raise(new OrderCancelled(Id, reason, issuedByUserId, utcNow));
    }

    public void Ship(string carrier, string trackingNumber, DateTime utcNow)
    {
        if (_status != OrderStatus.Placed)
        {
            throw new DomainException($"Cannot ship order {Id}: order is {_status}.");
        }
        CommandInput.RequireText(carrier, "Carrier");
        CommandInput.RequireText(trackingNumber, "Tracking number");
        Raise(new OrderShipped(Id, carrier, trackingNumber, utcNow));
    }

    // The OrderFulfillment process manager marks the order completed when its
    // shipment is delivered (Decision 13). Sales may already have recorded
    // ShipOrder; both supported lifecycle paths converge at Completed.
    public void Complete(DateTime utcNow)
    {
        if (_status is not (OrderStatus.Placed or OrderStatus.Shipped))
        {
            throw new DomainException($"Cannot complete order {Id}: order is {_status}.");
        }
        Raise(new OrderCompleted(Id, utcNow));
    }

    protected override void Apply(IDomainEvent @event)
    {
        switch (@event)
        {
            case OrderDrafted e:
                Id = e.OrderId;
                _customerId = e.CustomerId;
                _status = OrderStatus.Draft;
                break;

            case OrderLineAdded e:
                _lines.Add(new OrderLine(e.LineId, e.Sku, e.Quantity, e.UnitPrice));
                break;

            case OrderLineRemoved e:
                _lines.RemoveAll(l => l.LineId == e.LineId);
                break;

            case ShippingAddressSet e:
                _shippingAddress = e.ShippingAddress;
                break;

            case OrderPlaced:
                _status = OrderStatus.Placed;
                break;

            case OrderCancelled:
                _status = OrderStatus.Cancelled;
                break;

            case OrderShipped:
                _status = OrderStatus.Shipped;
                break;

            case OrderCompleted:
                _status = OrderStatus.Completed;
                break;

            default:
                throw new InvalidOperationException(
                    $"Order does not handle event type {@event.GetType().Name}.");
        }
    }

    // The snapshot seam (Chapter 12). Capture takes a defensive copy of the lines, so the memento does
    // not alias the live list and a later command on this order leaves the snapshot alone.
    public OrderSnapshot ToSnapshot()
        => new(Id, _customerId, _status, [.. _lines], _shippingAddress);

    // Restore seats a pristine order at the snapshot's state and version. RestoreVersion runs first and
    // guards against a non-pristine instance, so a rejected restore mutates nothing; the lines copy
    // into this order's own list rather than aliasing the snapshot's.
    public void RestoreFrom(OrderSnapshot snapshot, int version)
    {
        RestoreVersion(version);
        Id = snapshot.OrderId;
        _customerId = snapshot.CustomerId;
        _status = snapshot.Status;
        _lines.Clear();
        _lines.AddRange(snapshot.Lines);
        _shippingAddress = snapshot.ShippingAddress;
    }
}
