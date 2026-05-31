using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing;

namespace EventSourcingCqrs.Application.Commands.Billing;

public sealed record VoidPayment(Guid PaymentId, string Reason) : IAuthorizedCommand
{
    public static Permission RequiredPermission => Permission.VoidPayment;
}

public sealed class VoidPaymentHandler : ICommandHandler<VoidPayment>
{
    private readonly IEventStoreRepository<Payment> _repository;
    private readonly ICommandContextAccessor _accessor;

    public VoidPaymentHandler(
        IEventStoreRepository<Payment> repository,
        ICommandContextAccessor accessor)
    {
        _repository = repository;
        _accessor = accessor;
    }

    public async Task HandleAsync(VoidPayment command, CancellationToken ct)
    {
        var payment = await _repository.LoadAsync(command.PaymentId, ct)
            ?? throw new AggregateNotFoundException(command.PaymentId);
        var utcNow = (_accessor.Current ?? CommandContext.System).UtcNow().UtcDateTime;
        payment.Void(command.Reason, utcNow);
        await _repository.SaveAsync(payment, ct);
    }
}
