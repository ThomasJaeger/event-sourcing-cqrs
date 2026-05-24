using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Commands.Billing;

// Billing bounded context's user-dispatched commands for HTTP by-name dispatch
// (ADR 0023). RefundPayment is the only one in v1. AuthorizePayment and
// VoidPayment are process-manager-dispatched, and CapturePayment is not
// dispatched in v1 (the no-capture stance, F-0009-Q), so none register here;
// born-at-consumer applies to registration.
public sealed class BillingCommandTypeProvider : ICommandTypeProvider
{
    public IEnumerable<Type> GetCommandTypes() =>
    [
        typeof(RefundPayment),
    ];
}
