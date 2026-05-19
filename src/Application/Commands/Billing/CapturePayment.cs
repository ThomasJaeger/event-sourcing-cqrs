using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing;

namespace EventSourcingCqrs.Application.Commands.Billing;

// CapturePayment carries no amount. The aggregate raises PaymentCaptured with
// the authorized amount drawn from state; full-capture-only for v1. Partial
// captures land as a separate command shape in Phase 12.
public sealed record CapturePayment(Guid PaymentId) : ICommand;

public sealed class CapturePaymentHandler : ICommandHandler<CapturePayment>
{
    private readonly IEventStoreRepository<Payment> _repository;
    private readonly ICommandContextAccessor _accessor;

    public CapturePaymentHandler(
        IEventStoreRepository<Payment> repository,
        ICommandContextAccessor accessor)
    {
        _repository = repository;
        _accessor = accessor;
    }

    public async Task HandleAsync(CapturePayment command, CancellationToken ct)
    {
        var payment = await _repository.LoadAsync(command.PaymentId, ct)
            ?? throw new AggregateNotFoundException(command.PaymentId);
        var utcNow = (_accessor.Current ?? CommandContext.System).UtcNow().UtcDateTime;
        payment.Capture(utcNow);
        await _repository.SaveAsync(payment, ct);
    }
}
