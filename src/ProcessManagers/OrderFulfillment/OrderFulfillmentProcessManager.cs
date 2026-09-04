using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.ProcessManagers.OrderFulfillment.Events;

namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment;

// The four-branch fulfillment saga from Chapter 10, event-sourced on its own
// stream (pm-order-fulfillment:{orderId:N}) per ADR 0011 and 0012. It observes
// four inbound events (OrderPlaced, PaymentAuthorized, ShipmentDispatched,
// ShipmentDelivered) and learns reservation outcomes from the ReserveInventory
// fan-out's CommandOutcome rather than from InventoryReserved. That is why a
// reservation result lands here as a ReservationSucceeded or ReservationFailed PM
// event rather than an observed domain event; the information-flow divergence
// from Chapter 10 is a recorded manuscript divergence.
//
// Commit 20 ships the state machine and the transition vocabulary. The handler
// that drives it (the four IProcessManagerHandler implementations, the per-line
// fan-out, the timeout scheduling, the compensation branches) lands in commits 21
// to 24. Transition methods stay thin and Apply stays guard-free, the same split
// Order keeps between its command methods and Apply; the state guards live with
// the handler in commit 21.
public sealed class OrderFulfillmentProcessManager : ProcessManager
{
    private readonly Dictionary<Guid, ReservationLineState> _reservations = [];

    private OrderFulfillmentState _state = OrderFulfillmentState.NotStarted;
    private Guid _orderId;
    private Money? _orderTotal;
    private Guid _paymentId;
    private Guid _shipmentId;
    private string? _cancellationReason;

    public OrderFulfillmentProcessManager(StreamId streamId) : base(streamId) { }

    public OrderFulfillmentState State => _state;
    public Guid OrderId => _orderId;
    public Money? OrderTotal => _orderTotal;
    public Guid PaymentId => _paymentId;
    public Guid ShipmentId => _shipmentId;
    public string? CancellationReason => _cancellationReason;
    public IReadOnlyDictionary<Guid, ReservationLineState> Reservations => _reservations;

    // Transition methods. Each records one PM event and lets Apply mutate state.
    // Guards land with the handler in commit 21.
    public void Start(Guid orderId, Money orderTotal, Guid paymentId) =>
        RecordTransition(new OrderFulfillmentStarted(orderId, orderTotal, paymentId));

    public void RecordPaymentAuthorized() =>
        RecordTransition(new PaymentAuthorizationRecorded());

    public void RecordLineReserved(Guid lineId, string sku, int quantity, Guid inventoryId) =>
        RecordTransition(new ReservationSucceeded(lineId, sku, quantity, inventoryId));

    public void RecordLineReservationFailed(Guid lineId, string sku, int quantity, string reason) =>
        RecordTransition(new ReservationFailed(lineId, sku, quantity, reason));

    public void CompleteReservations() =>
        RecordTransition(new InventoryReservationsCompleted());

    public void RequestShipmentScheduling(Guid shipmentId) =>
        RecordTransition(new ShipmentSchedulingRequested(shipmentId));

    public void RecordShipmentDispatched() =>
        RecordTransition(new ShipmentDispatchRecorded());

    public void RecordShipmentDelivered() =>
        RecordTransition(new ShipmentDeliveryRecorded());

    public void StartCancellation(string reason) =>
        RecordTransition(new OrderFulfillmentCancellationStarted(reason));

    public void ReleaseReservation(Guid lineId) =>
        RecordTransition(new ReservationReleased(lineId));

    public void RequestVoid(string reason) =>
        RecordTransition(new PaymentVoidRequested(reason));

    public void Complete() =>
        RecordTransition(new OrderFulfillmentCompleted(OrderFulfillmentOutcome.Completed));

    public void CompleteAsCancelled() =>
        RecordTransition(new OrderFulfillmentCompleted(OrderFulfillmentOutcome.Cancelled));

    protected override void Apply(IProcessManagerEvent @event)
    {
        switch (@event)
        {
            case OrderFulfillmentStarted e:
                _orderId = e.OrderId;
                _orderTotal = e.OrderTotal;
                _paymentId = e.PaymentId;
                _state = OrderFulfillmentState.AwaitingPayment;
                break;

            case PaymentAuthorizationRecorded:
                _state = OrderFulfillmentState.AwaitingInventory;
                break;

            case ReservationSucceeded e:
                _reservations[e.LineId] = new ReservationLineState(
                    e.Sku, e.Quantity, e.InventoryId, ReservationLineStatus.Reserved, FailureReason: null);
                break;

            case ReservationFailed e:
                _reservations[e.LineId] = new ReservationLineState(
                    e.Sku, e.Quantity, InventoryId: null, ReservationLineStatus.Failed, e.Reason);
                break;

            case InventoryReservationsCompleted:
                _state = OrderFulfillmentState.AwaitingShipment;
                break;

            case ShipmentSchedulingRequested e:
                _shipmentId = e.ShipmentId;
                _state = OrderFulfillmentState.AwaitingDispatch;
                break;

            case ShipmentDispatchRecorded:
                _state = OrderFulfillmentState.AwaitingDelivery;
                break;

            case ShipmentDeliveryRecorded:
                // Delivery observed; state holds at AwaitingDelivery until Complete
                // records the terminal once MarkOrderCompleted dispatches.
                break;

            case OrderFulfillmentCancellationStarted e:
                _cancellationReason = e.Reason;
                break;

            case ReservationReleased e:
                _reservations[e.LineId] = _reservations[e.LineId] with
                {
                    Status = ReservationLineStatus.Released
                };
                _state = OrderFulfillmentState.ReleasingInventory;
                break;

            case PaymentVoidRequested:
                _state = OrderFulfillmentState.VoidingPayment;
                break;

            case OrderFulfillmentCompleted e:
                _state = e.Outcome == OrderFulfillmentOutcome.Completed
                    ? OrderFulfillmentState.Completed
                    : OrderFulfillmentState.Cancelled;
                break;

            default:
                throw new InvalidOperationException(
                    $"OrderFulfillmentProcessManager does not handle event type {@event.GetType().Name}.");
        }
    }
}
