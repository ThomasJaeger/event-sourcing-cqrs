namespace EventSourcingCqrs.Application.Authorization;

public interface IOrderCancellationGuard
{
    Task EnsureCanCancelAsync(Guid orderId, CancellationToken ct);
}
