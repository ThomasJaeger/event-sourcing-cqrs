using EventSourcingCqrs.Application.Commands.Billing;
using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Sales;
using Microsoft.Extensions.DependencyInjection;

namespace EventSourcingCqrs.Application.Pipelines;

public sealed class WorkflowCommandBehavior<TCommand>(IServiceProvider services,
    ICurrentTenantAccessor tenantAccessor) : ICommandPipelineBehavior<TCommand> where TCommand : ICommand
{
    public async Task HandleAsync(TCommand command, CommandHandlerDelegate next, CancellationToken ct)
    {
        Guid? orderId = command is IOrderWorkflowCommand orderCommand ? orderCommand.OrderId : null;
        if (command is DispatchShipment dispatch)
        {
            using var scope = services.CreateScope();
            var shipment = await scope.ServiceProvider.GetRequiredService<IEventStoreRepository<Shipment>>()
                .LoadAsync(dispatch.ShipmentId, ct) ?? throw new AggregateNotFoundException(dispatch.ShipmentId);
            orderId = shipment.OrderId;
        }
        if (orderId is null) { await next(); return; }
        await services.GetRequiredService<IWorkflowLock>().RunAsync(
            tenantAccessor.Current ?? throw new MissingTenantContextException(), orderId.Value, async () =>
            {
                if (command is DispatchShipment or ScheduleShipment or AuthorizePayment)
                {
                    using var scope = services.CreateScope();
                    var order = await scope.ServiceProvider.GetRequiredService<IEventStoreRepository<Order>>()
                        .LoadAsync(orderId.Value, ct);
                    if (order?.Status == OrderStatus.Cancelled)
                        throw new DomainException("Cannot continue fulfillment of a cancelled order.");
                }
                await next();
            }, ct);
    }
}
