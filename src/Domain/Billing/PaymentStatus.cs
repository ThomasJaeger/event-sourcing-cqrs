namespace EventSourcingCqrs.Domain.Billing;

public enum PaymentStatus
{
    Authorized,
    Captured,
    Refunded,
    Voided
}
