using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Domain.Sales;

public sealed class Order : AggregateRoot
{
    private readonly List<OrderLine> _lines = [];
    private OrderStatus _status;
    private Guid _customerId;
    private Address? _shippingAddress;

    public IReadOnlyList<OrderLine> Lines => _lines;
    public OrderStatus Status => _status;
    public Money Total => _lines.Aggregate(Money.Zero, (sum, l) => sum + l.Subtotal);

    // Public for event-sourced rehydration. Use Order.Draft(...) to create a new order from a command.
    public Order() { }

    public static Order Draft(Guid orderId, Guid customerId, DateTime utcNow)
    {
        var order = new Order();
        order.Raise(new OrderDrafted(orderId, customerId, utcNow));
        return order;
    }

    public void AddLine(Guid lineId, string sku, int quantity, Money unitPrice, DateTime utcNow)
    {
        if (_status != OrderStatus.Draft)
        {
            throw new DomainException($"Cannot add line to order {Id}: order is {_status}.");
        }
        if (quantity <= 0)
        {
            throw new DomainException($"Cannot add line {lineId}: quantity must be positive.");
        }
        if (_lines.Any(l => l.LineId == lineId))
        {
            throw new DomainException($"Line {lineId} already exists on order {Id}.");
        }
        Raise(new OrderLineAdded(Id, lineId, sku, quantity, unitPrice, utcNow));
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
        Raise(new OrderCancelled(Id, reason, issuedByUserId, utcNow));
    }

    public void Ship(string carrier, string trackingNumber, DateTime utcNow)
    {
        if (_status != OrderStatus.Placed)
        {
            throw new DomainException($"Cannot ship order {Id}: order is {_status}.");
        }
        Raise(new OrderShipped(Id, carrier, trackingNumber, utcNow));
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

            default:
                throw new InvalidOperationException(
                    $"Order does not handle event type {@event.GetType().Name}.");
        }
    }
}
