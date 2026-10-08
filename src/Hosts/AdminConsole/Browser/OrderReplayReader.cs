using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;

namespace EventSourcingCqrs.Hosts.AdminConsole.Browser;

public sealed record OrderReplayResult(OrderHistoryView? History, string? Message);

public interface IOrderReplayLab
{
    Task<OrderReplayResult> ReadAsync(string streamId, CancellationToken ct);
}

// The console's host authorization permits cross-tenant investigation. The stream id supplies
// an explicit tenant; this path never uses the business site's ambient query identity.
public sealed class OrderReplayReader(OrderHistoryReader history) : IOrderReplayLab
{
    public async Task<OrderReplayResult> ReadAsync(string streamId, CancellationToken ct)
    {
        TenantId tenant;
        Guid orderId;
        try
        {
            var parts = StreamId.Parse(streamId.Trim()).Value.Split(':');
            if (parts[0] != "order") return new(null, "Choose an order stream. Other aggregates have their own state models.");
            tenant = parts.Length == 3 ? TenantId.From(Guid.ParseExact(parts[1], "N")) : WellKnownTenants.Default;
            orderId = Guid.ParseExact(parts[^1], "N");
            if (orderId == Guid.Empty) return new(null, "Enter an order stream with a non-empty order id.");
            var canonical = StreamId.ForAggregate<Order>(tenant, orderId).Value;
            if (!string.Equals(canonical, streamId.Trim(), StringComparison.Ordinal))
                return new(null, $"Use the canonical order stream id: {canonical}. The supplied spelling names a different stored stream.");
        }
        catch (ArgumentException)
        {
            return new(null, "Enter a valid order stream id. You can choose an order in Event streams and follow its history link.");
        }
        try
        {
            var result = await history.ReadAsync(tenant, orderId, ct);
            return result is null ? new(null, "No order events were found for this stream.") : new(result, null);
        }
        catch (OrderHistoryUnavailableException ex) { return new(null, ex.Message); }
        catch (OrderHistoryTooLongException ex) { return new(null, ex.Message); }
        catch (OrderHistoryIncompleteException ex) { return new(null, ex.Message); }
    }
}
