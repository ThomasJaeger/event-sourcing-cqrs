using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing.Events;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Domain.Billing;

public sealed class Payment : AggregateRoot
{
    private PaymentStatus _status;
    private Guid _orderId;
    private Money? _authorizedAmount;
    private Money? _capturedAmount;

    public PaymentStatus Status => _status;
    public Guid OrderId => _orderId;
    public Money? AuthorizedAmount => _authorizedAmount;
    public Money? CapturedAmount => _capturedAmount;

    // Public for event-sourced rehydration. Use Payment.Authorize(...) to
    // create a new payment from a command.
    public Payment() { }

    public static Payment Authorize(
        Guid paymentId,
        Guid orderId,
        Money amount,
        string paymentMethodReference,
        DateTime utcNow)
    {
        if (amount.IsNegative || amount.IsZero)
        {
            throw new DomainException(
                $"Cannot authorize payment {paymentId}: amount must be strictly positive.");
        }
        if (string.IsNullOrWhiteSpace(paymentMethodReference))
        {
            throw new DomainException(
                $"Cannot authorize payment {paymentId}: payment method reference must be non-empty.");
        }
        var payment = new Payment();
        payment.Raise(new PaymentAuthorized(paymentId, orderId, amount, paymentMethodReference, utcNow));
        return payment;
    }

    public void Capture(DateTime utcNow)
    {
        if (_status != PaymentStatus.Authorized)
        {
            throw new DomainException($"Cannot capture payment {Id}: payment is {_status}.");
        }
        // Full-capture-only for v1: the aggregate raises the event with the
        // amount from state. Partial captures deferred to Phase 15. The
        // status-check above guarantees _authorizedAmount is non-null.
        Raise(new PaymentCaptured(Id, _authorizedAmount!, utcNow));
    }

    public void Refund(string reason, DateTime utcNow)
    {
        if (_status != PaymentStatus.Captured)
        {
            throw new DomainException($"Cannot refund payment {Id}: payment is {_status}.");
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException($"Cannot refund payment {Id}: reason must be non-empty.");
        }
        // Full-refund-only for v1. The status-check guarantees _capturedAmount
        // is non-null. Partial refunds deferred to Phase 15.
        Raise(new PaymentRefunded(Id, _capturedAmount!, reason, utcNow));
    }

    // Strict invariant: void is valid only from Authorized. Once money has
    // moved (Status == Captured), the reversal path is Refund. Matches real
    // payment-processor semantics.
    public void Void(string reason, DateTime utcNow)
    {
        if (_status != PaymentStatus.Authorized)
        {
            throw new DomainException($"Cannot void payment {Id}: payment is {_status}.");
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException($"Cannot void payment {Id}: reason must be non-empty.");
        }
        Raise(new PaymentVoided(Id, reason, utcNow));
    }

    protected override void Apply(IDomainEvent @event)
    {
        switch (@event)
        {
            case PaymentAuthorized e:
                Id = e.PaymentId;
                _orderId = e.OrderId;
                _authorizedAmount = e.Amount;
                _status = PaymentStatus.Authorized;
                break;

            case PaymentCaptured e:
                _capturedAmount = e.Amount;
                _status = PaymentStatus.Captured;
                break;

            case PaymentRefunded:
                _status = PaymentStatus.Refunded;
                break;

            case PaymentVoided:
                _status = PaymentStatus.Voided;
                break;

            default:
                throw new InvalidOperationException(
                    $"Payment does not handle event type {@event.GetType().Name}.");
        }
    }
}
