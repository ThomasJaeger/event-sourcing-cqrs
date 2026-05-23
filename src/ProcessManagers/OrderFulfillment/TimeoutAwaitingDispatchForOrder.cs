using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment;

// Dispatched by the delay queue when the AwaitingDispatch timeout fires (ADR
// 0017): the shipment was scheduled but never dispatched. OrderId routes to the
// PM. Routes into the same with-releases compensation a ScheduleShipment failure
// takes (branch 4 collapses into branch 3, commit 23).
public sealed record TimeoutAwaitingDispatchForOrder(Guid OrderId) : ICommand;

public sealed class TimeoutAwaitingDispatchForOrderHandler : ICommandHandler<TimeoutAwaitingDispatchForOrder>
{
    private readonly IProcessManagerRepository<OrderFulfillmentProcessManager> _pms;
    private readonly OrderFulfillmentCompensation _compensation;
    private readonly ICommandContextAccessor _accessor;

    public TimeoutAwaitingDispatchForOrderHandler(
        IProcessManagerRepository<OrderFulfillmentProcessManager> pms,
        OrderFulfillmentCompensation compensation,
        ICommandContextAccessor accessor)
    {
        _pms = pms;
        _compensation = compensation;
        _accessor = accessor;
    }

    public async Task HandleAsync(TimeoutAwaitingDispatchForOrder command, CancellationToken ct)
    {
        var pm = await _pms.LoadAsync(
            OrderFulfillmentStreams.For(command.OrderId), OrderFulfillmentStreams.New, ct);

        // State guard: a late timeout, after ShipmentDispatched already advanced
        // the PM, loads a PM past AwaitingDispatch and no-ops.
        if (pm is null || pm.State != OrderFulfillmentState.AwaitingDispatch)
        {
            return;
        }

        var causing = EventMetadata.ForCommand(_accessor.Current ?? CommandContext.System);
        await _compensation.CompensateWithReleasesAsync(
            pm, $"Shipment dispatch timed out for order {command.OrderId}.", causing, ct);
    }
}
