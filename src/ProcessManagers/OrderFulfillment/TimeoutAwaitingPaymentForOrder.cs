using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment;

// Dispatched by the delay queue when the AwaitingPayment timeout fires (ADR 0017).
// OrderId routes to the PM. A PM-internal orchestration command, so it lives with
// the PM rather than in Application alongside the bounded-context commands the PM
// dispatches outward.
public sealed record TimeoutAwaitingPaymentForOrder(Guid OrderId) : ICommand;

public sealed class TimeoutAwaitingPaymentForOrderHandler : ICommandHandler<TimeoutAwaitingPaymentForOrder>
{
    private readonly IProcessManagerRepository<OrderFulfillmentProcessManager> _pms;
    private readonly OrderFulfillmentCompensation _compensation;
    private readonly ICommandContextAccessor _accessor;
    private readonly ICurrentTenantAccessor _tenantAccessor;

    public TimeoutAwaitingPaymentForOrderHandler(
        IProcessManagerRepository<OrderFulfillmentProcessManager> pms,
        OrderFulfillmentCompensation compensation,
        ICommandContextAccessor accessor,
        ICurrentTenantAccessor tenantAccessor)
    {
        _pms = pms;
        _compensation = compensation;
        _accessor = accessor;
        _tenantAccessor = tenantAccessor;
    }

    public async Task HandleAsync(TimeoutAwaitingPaymentForOrder command, CancellationToken ct)
    {
        var pm = await _pms.LoadAsync(
            OrderFulfillmentStreams.For(command.OrderId), OrderFulfillmentStreams.New, ct);

        // State guard: a late timeout, after PaymentAuthorized already advanced the
        // PM (the race ADR 0017 names), loads a PM past AwaitingPayment and no-ops.
        if (pm is null || pm.State != OrderFulfillmentState.AwaitingPayment)
        {
            return;
        }

        // The metadata tenant tracks the command context: a real context means the bus set the tenant
        // too (an unset tenant there is a wiring regression); no context means the System fallback and
        // the default tenant. The OrderFulfillmentStreams.For load above stays Default by design,
        // independent of this metadata tenant (Option 1).
        var context = _accessor.Current;
        var causing = context is null
            ? EventMetadata.ForCommand(CommandContext.System, WellKnownTenants.Default)
            : EventMetadata.ForCommand(
                context, _tenantAccessor.Current ?? throw new MissingTenantContextException());
        await _compensation.CompensateAuthorizeFailureAsync(
            pm, $"Payment authorization timed out for order {command.OrderId}.", causing, ct);
    }
}
