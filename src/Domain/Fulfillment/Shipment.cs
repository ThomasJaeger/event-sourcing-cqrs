using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Domain.Fulfillment;

public sealed class Shipment : AggregateRoot
{
    private readonly List<ShipmentLine> _lines = [];
    private ShipmentStatus _status;
    private Guid _orderId;

    public IReadOnlyList<ShipmentLine> Lines => _lines;
    public ShipmentStatus Status => _status;
    public Guid OrderId => _orderId;

    // Public for event-sourced rehydration. Use Shipment.Schedule(...) to
    // create a new shipment from a command.
    public Shipment() { }

    public static Shipment Schedule(
        Guid shipmentId,
        Guid orderId,
        Address destination,
        IReadOnlyList<ShipmentLine> lines,
        DateTime utcNow)
    {
        if (lines.Count == 0)
        {
            throw new DomainException("Cannot schedule shipment: at least one line is required.");
        }
        var shipment = new Shipment();
        shipment.Raise(new ShipmentScheduled(shipmentId, orderId, destination, lines, utcNow));
        return shipment;
    }

    public void Dispatch(string carrierReference, DateTime utcNow)
    {
        if (_status != ShipmentStatus.Scheduled)
        {
            throw new DomainException($"Cannot dispatch shipment {Id}: shipment is {_status}.");
        }
        if (string.IsNullOrWhiteSpace(carrierReference))
        {
            throw new DomainException($"Cannot dispatch shipment {Id}: carrier reference must be non-empty.");
        }
        Raise(new ShipmentDispatched(Id, carrierReference, utcNow));
    }

    public void Deliver(DateTime utcNow)
    {
        if (_status != ShipmentStatus.Dispatched)
        {
            throw new DomainException($"Cannot deliver shipment {Id}: shipment is {_status}.");
        }
        Raise(new ShipmentDelivered(Id, utcNow));
    }

    // Strict invariant: returns are valid only from Delivered. Mid-transit
    // returns are operational realism deferred, matching Inventory's strict
    // Adjust precedent.
    public void Return(string reason, DateTime utcNow)
    {
        if (_status != ShipmentStatus.Delivered)
        {
            throw new DomainException($"Cannot return shipment {Id}: shipment is {_status}.");
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException($"Cannot return shipment {Id}: reason must be non-empty.");
        }
        Raise(new ShipmentReturned(Id, reason, utcNow));
    }

    protected override void Apply(IDomainEvent @event)
    {
        switch (@event)
        {
            case ShipmentScheduled e:
                Id = e.ShipmentId;
                _orderId = e.OrderId;
                _status = ShipmentStatus.Scheduled;
                foreach (var line in e.Lines)
                {
                    _lines.Add(line);
                }
                break;

            case ShipmentDispatched:
                _status = ShipmentStatus.Dispatched;
                break;

            case ShipmentDelivered:
                _status = ShipmentStatus.Delivered;
                break;

            case ShipmentReturned:
                _status = ShipmentStatus.Returned;
                break;

            default:
                throw new InvalidOperationException(
                    $"Shipment does not handle event type {@event.GetType().Name}.");
        }
    }
}
