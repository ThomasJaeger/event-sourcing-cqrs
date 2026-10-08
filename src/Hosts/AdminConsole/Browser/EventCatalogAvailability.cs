namespace EventSourcingCqrs.Hosts.AdminConsole.Browser;

// Catalog discovery is optional. A provider without an indexed catalog still supports direct
// stream inspection through IStreamInspector; it must never fall back to a whole-store scan.
public sealed record EventCatalogAvailability(bool IsAvailable, string? UnavailableReason)
{
    public static EventCatalogAvailability Available { get; } = new(true, null);

    public static EventCatalogAvailability Unavailable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(false, reason);
    }
}
