using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing;

namespace EventSourcingCqrs.Application.Commands.Billing;

// RefundPayment carries a reason but no amount. The aggregate raises
// PaymentRefunded with the captured amount drawn from state; full-refund-only
// for v1. Partial refunds land as a separate command shape in Phase 12.
public sealed record RefundPayment(Guid PaymentId, string Reason) : IAuthorizedCommand
{
    public static Permission RequiredPermission => Permission.RefundPayment;
}

public sealed class RefundPaymentHandler : ICommandHandler<RefundPayment>
{
    private readonly IEventStoreRepository<Payment> _repository;
    private readonly ICommandContextAccessor _accessor;

    public RefundPaymentHandler(
        IEventStoreRepository<Payment> repository,
        ICommandContextAccessor accessor)
    {
        _repository = repository;
        _accessor = accessor;
    }

    public async Task HandleAsync(RefundPayment command, CancellationToken ct)
    {
        var payment = await _repository.LoadAsync(command.PaymentId, ct)
            ?? throw new AggregateNotFoundException(command.PaymentId);
        var utcNow = (_accessor.Current ?? CommandContext.System).UtcNow().UtcDateTime;
        payment.Refund(command.Reason, utcNow);
        await _repository.SaveAsync(payment, ct);
    }
}
