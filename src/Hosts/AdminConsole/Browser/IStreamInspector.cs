using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Hosts.AdminConsole.Browser;

// The Event Store Browser's read seam (Chapter 17): it inspects a single aggregate stream for operator
// display. It owns the parse guard (a malformed input returns InvalidFormat), the pm-prefix pre-detection
// (a process-manager stream returns ProcessManagerUnsupported rather than letting the aggregate read throw
// on an unregistered event type), and the read itself (Found when the stream has events, Empty when it
// does not). Authorization is the host fallback gate (ADR 0040), upstream of the page that calls this.
//
// There is no UnknownKind outcome, by decision. A typo'd but valid-format prefix returns Empty, and the
// page's empty-state message covers the whole valid-format-but-matches-nothing class: wrong prefix, wrong
// guid, wrong tenant. The set of aggregate prefixes has no disk-authoritative source, so the seam does not
// invent one to reject against.
public interface IStreamInspector
{
    Task<StreamInspectionResult> InspectStreamAsync(string streamIdInput, CancellationToken ct);
}
