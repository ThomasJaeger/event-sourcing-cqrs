using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Application.Commands.Billing;

public sealed record AuthorizePayment(
    Guid PaymentId,
    Guid OrderId,
    Money Amount,
    string PaymentMethodReference) : IAuthorizedCommand
{
    public static Permission RequiredPermission => Permission.AuthorizePayment;
}

// Pattern A explicit creation: no LoadAsync, no AggregateNotFoundException.
// The factory creates a fresh aggregate, and SaveAsync appends with
// expectedVersion = 0; if the stream already exists, the event store throws
// ConcurrencyException, which is the right signal that "this PaymentId is
// already authorized, send a different command." Mirrors
// ScheduleShipmentHandler and CreateInventoryHandler.
public sealed class AuthorizePaymentHandler : ICommandHandler<AuthorizePayment>
{
    private readonly IEventStoreRepository<Payment> _repository;
    private readonly ICommandContextAccessor _accessor;

    public AuthorizePaymentHandler(
        IEventStoreRepository<Payment> repository,
        ICommandContextAccessor accessor)
    {
        _repository = repository;
        _accessor = accessor;
    }

    public Task HandleAsync(AuthorizePayment command, CancellationToken ct)
    {
        var utcNow = (_accessor.Current ?? CommandContext.System).UtcNow().UtcDateTime;
        var payment = Payment.Authorize(
            command.PaymentId,
            command.OrderId,
            command.Amount,
            command.PaymentMethodReference,
            utcNow);
        return _repository.SaveAsync(payment, ct);
    }
}
