using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Hosts.Web.Components.History;

// Compare recorded states, not event names. Two lines with the same SKU can still be distinct items.
public static class OrderHistoryComparison
{
    public static IReadOnlyList<string> Describe(OrderHistoryStep earlier, OrderHistoryStep later)
    {
        List<string> changes = [];
        if (earlier.Snapshot.Status != later.Snapshot.Status)
            changes.Add($"Status changed from {earlier.Snapshot.Status} to {later.Snapshot.Status}.");
        var before = earlier.Snapshot.Lines.ToDictionary(line => line.LineId);
        var after = later.Snapshot.Lines.ToDictionary(line => line.LineId);
        foreach (var line in earlier.Snapshot.Lines)
        {
            if (!after.TryGetValue(line.LineId, out var next))
                changes.Add($"Removed {DescribeLine(line)}.");
            else if (line.Sku != next.Sku || line.Quantity != next.Quantity || line.UnitPrice != next.UnitPrice)
                changes.Add($"Changed {DescribeLine(line)} to {DescribeLine(next)}.");
        }
        foreach (var line in later.Snapshot.Lines)
        {
            if (!before.ContainsKey(line.LineId)) changes.Add($"Added {DescribeLine(line)}.");
        }
        if (earlier.Snapshot.ShippingAddress != later.Snapshot.ShippingAddress)
            changes.Add($"Shipping address changed from {FormatAddress(earlier.Snapshot.ShippingAddress)} to {FormatAddress(later.Snapshot.ShippingAddress)}.");
        if (earlier.Total != later.Total)
            changes.Add($"Order total changed from {earlier.Total} to {later.Total}.");
        return changes;
    }

    public static string FormatAddress(Address? address)
        => address is null ? "Not set" : $"{address.Street}, {address.City}, {address.PostalCode}, {address.Country}";

    private static string DescribeLine(OrderLine line)
        => $"{line.Quantity} × {line.Sku} at {line.UnitPrice} each";
}
