using EventSourcingCqrs.Application.Authorization;
namespace EventSourcingCqrs.Application.Tests.TestKit;
internal sealed class NoShipmentCancellationGuard : IOrderCancellationGuard
{
    public Task EnsureCanCancelAsync(Guid orderId, CancellationToken ct) => Task.CompletedTask;
}
