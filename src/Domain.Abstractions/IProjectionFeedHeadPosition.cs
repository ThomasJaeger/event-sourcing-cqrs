namespace EventSourcingCqrs.Domain.Abstractions;

// The head of the feed projections consume: the maximum global_position among the events a
// projection could be handed, or 0 when that feed is empty. It is the last position a projection
// could reach, where IEventStoreHeadPosition reports the last position the log assigned.
//
// The two differ because process-manager events raise the head and never reach a projection. PM
// events persist to the same log as aggregate events and skip the outbox (ADR 0013), and the
// projection feed excludes their stream prefix, so a log whose last row is a PM row has a head no
// projection will ever checkpoint at. The gap is permanent rather than transient: nothing later
// carries those positions to a projection.
//
// This port exists because the two callers want different answers. A lag display wants a truthful
// tail of the log, and overstating a projection's lag by the PM tail is the conservative direction
// for a staleness tool. A wait wants a reachable target, and an unreachable one turns a completion
// condition into a bound that always expires. One port cannot be both, so the head keeps its
// meaning and this reports the other.
public interface IProjectionFeedHeadPosition
{
    Task<long> GetFeedHeadPositionAsync(CancellationToken ct);
}
