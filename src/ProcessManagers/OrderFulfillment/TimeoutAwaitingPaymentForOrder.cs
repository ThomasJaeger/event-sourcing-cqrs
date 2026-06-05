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
        // Reversal of Option 1: the timeout loads the PM under the resurfaced command's tenant. The caused
        // bus set the accessor from the due row's tenant, so the stream-id load and the causing metadata
        // resolve from one tenant. A real context with an unset tenant is a wiring regression; no context
        // means the System fallback and the default tenant.
        var context = _accessor.Current;
        var tenant = context is null
            ? WellKnownTenants.Default
            : _tenantAccessor.Current ?? throw new MissingTenantContextException();

        var pm = await _pms.LoadAsync(
            OrderFulfillmentStreams.For(tenant, command.OrderId), OrderFulfillmentStreams.New, ct);

        // State guard: a late timeout, after PaymentAuthorized already advanced the
        // PM (the race ADR 0017 names), loads a PM past AwaitingPayment and no-ops.
        if (pm is null || pm.State != OrderFulfillmentState.AwaitingPayment)
        {
            return;
        }

        var causing = context is null
            ? EventMetadata.ForCommand(CommandContext.System, WellKnownTenants.Default)
            : EventMetadata.ForCommand(context, tenant);
        await _compensation.CompensateAuthorizeFailureAsync(
            pm, $"Payment authorization timed out for order {command.OrderId}.", causing, ct);
    }
}
