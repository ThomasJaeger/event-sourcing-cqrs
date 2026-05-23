using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.ProcessManagers.Returns.Events;

namespace EventSourcingCqrs.ProcessManagers.Returns;

// The returns workflow from Chapter 10's smaller second example, event-sourced on
// its own stream (pm-return:{orderId:N}) per ADR 0011 and 0012. It observes one
// inbound event (ShipmentReturned), restocks the returned lines, and voids the
// payment. The deliberate contrast with OrderFulfillmentProcessManager is the
// failure model: no compensation branches, a single Stuck terminal that halts for
// human intervention (Decision 12, Ch 10's stuck-state pattern).
//
// Transition methods stay thin and Apply stays guard-free, the same split Order
// and OrderFulfillmentProcessManager keep; the state guards live with the handler.
public sealed class ReturnProcessManager : ProcessManager
{
    private ReturnState _state = ReturnState.NotStarted;
    private Guid _orderId;
    private string? _stuckReason;

    public ReturnProcessManager(StreamId streamId) : base(streamId) { }

    public ReturnState State => _state;
    public Guid OrderId => _orderId;
    public string? StuckReason => _stuckReason;

    public void Start(Guid orderId) =>
        RecordTransition(new ReturnProcessingStarted(orderId));

    public void RecordRestock() =>
        RecordTransition(new InventoryRestockRecorded());

    public void RecordVoid() =>
        RecordTransition(new PaymentVoidRecorded());

    public void Complete() =>
        RecordTransition(new ReturnProcessingCompleted());

    public void MarkStuck(string reason) =>
        RecordTransition(new ReturnProcessingStuck(reason));

    protected override void Apply(IProcessManagerEvent @event)
    {
        switch (@event)
        {
            case ReturnProcessingStarted e:
                _orderId = e.OrderId;
                _state = ReturnState.RestockingInventory;
                break;

            case InventoryRestockRecorded:
                _state = ReturnState.VoidingPayment;
                break;

            case PaymentVoidRecorded:
                // Void dispatched; state holds at VoidingPayment until Complete
                // records the terminal, the same record-then-complete pair the
                // OrderFulfillment PM uses at delivery.
                break;

            case ReturnProcessingCompleted:
                _state = ReturnState.Completed;
                break;

            case ReturnProcessingStuck e:
                _stuckReason = e.Reason;
                _state = ReturnState.Stuck;
                break;

            default:
                throw new InvalidOperationException(
                    $"ReturnProcessManager does not handle event type {@event.GetType().Name}.");
        }
    }
}
