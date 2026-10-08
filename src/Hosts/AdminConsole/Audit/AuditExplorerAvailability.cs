namespace EventSourcingCqrs.Hosts.AdminConsole.Audit;

// Chapter 17: raw audit reads require a provider-specific metadata reader.
public sealed record AuditExplorerAvailability(bool IsAvailable, string? UnavailableReason)
{
    public static AuditExplorerAvailability Available { get; } = new(true, null);
    public static AuditExplorerAvailability Unavailable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(false, reason);
    }
}
