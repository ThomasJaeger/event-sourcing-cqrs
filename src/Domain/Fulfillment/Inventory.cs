using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.Events;

namespace EventSourcingCqrs.Domain.Fulfillment;

public sealed class Inventory : AggregateRoot
{
    private string _sku = "";
    private int _totalAdjusted;
    private readonly List<ReservationLine> _reservations = [];

    public string Sku => _sku;
    public int TotalAdjusted => _totalAdjusted;
    public int Reserved => _reservations.Sum(r => r.Quantity);
    public int Available => _totalAdjusted - Reserved;
    public IReadOnlyList<ReservationLine> Reservations => _reservations;

    // Public for event-sourced rehydration. Use Inventory.Create(...) to create
    // a new per-SKU inventory aggregate from a command. The per-SKU boundary
    // means the caller is responsible for the 1:1 InventoryId-to-SKU mapping;
    // routing invariants live at the dispatch layer, not here.
    public Inventory() { }

    public static Inventory Create(Guid inventoryId, string sku, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(sku))
        {
            throw new DomainException("Cannot create inventory: SKU must be non-empty.");
        }
        var inventory = new Inventory();
        inventory.Raise(new InventoryCreated(inventoryId, sku, utcNow));
        return inventory;
    }

    // Strict invariant: an adjustment that would make Available negative throws.
    // Deliberate teaching choice; the permissive model (overcommit then
    // compensate downstream) is a known alternative that a future operational
    // realism pass could revisit.
    public void Adjust(int quantityDelta, string reason, DateTime utcNow)
    {
        if (quantityDelta == 0)
        {
            throw new DomainException($"Cannot adjust inventory {_sku}: delta must be non-zero.");
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException($"Cannot adjust inventory {_sku}: reason must be non-empty.");
        }
        if (_totalAdjusted + quantityDelta - Reserved < 0)
        {
            throw new DomainException(
                $"Cannot adjust inventory {_sku} by {quantityDelta}: would make available stock negative.");
        }
        Raise(new InventoryAdjusted(Id, _sku, quantityDelta, reason, utcNow));
    }

    public void Reserve(Guid orderId, Guid lineId, int quantity, DateTime utcNow)
    {
        if (quantity <= 0)
        {
            throw new DomainException($"Cannot reserve from inventory {_sku}: quantity must be positive.");
        }
        if (_reservations.Any(r => r.LineId == lineId))
        {
            throw new DomainException(
                $"Cannot reserve from inventory {_sku}: line {lineId} already has an active reservation.");
        }
        if (Available < quantity)
        {
            throw new DomainException(
                $"Cannot reserve {quantity} units of {_sku} for line {lineId}: only {Available} available.");
        }
        Raise(new InventoryReserved(Id, orderId, lineId, _sku, quantity, utcNow));
    }

    public void Release(Guid lineId, string reason, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException(
                $"Cannot release reservation from inventory {_sku}: reason must be non-empty.");
        }
        var reservation = _reservations.FirstOrDefault(r => r.LineId == lineId);
        if (reservation is null)
        {
            throw new DomainException(
                $"Cannot release reservation from inventory {_sku}: line {lineId} has no active reservation.");
        }
        Raise(new InventoryReleased(Id, reservation.OrderId, lineId, reason, utcNow));
    }

    protected override void Apply(IDomainEvent @event)
    {
        switch (@event)
        {
            case InventoryCreated e:
                Id = e.InventoryId;
                _sku = e.Sku;
                break;

            case InventoryAdjusted e:
                _totalAdjusted += e.QuantityDelta;
                break;

            case InventoryReserved e:
                _reservations.Add(new ReservationLine(e.OrderId, e.LineId, e.Sku, e.Quantity));
                break;

            case InventoryReleased e:
                _reservations.RemoveAll(r => r.LineId == e.LineId);
                break;

            default:
                throw new InvalidOperationException(
                    $"Inventory does not handle event type {@event.GetType().Name}.");
        }
    }
}
