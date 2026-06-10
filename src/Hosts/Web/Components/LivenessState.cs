namespace EventSourcingCqrs.Hosts.Web.Components;

/// <summary>
/// State of the subscription-liveness badge for a page's live subscription.
/// Idle: no subscription armed; the badge renders nothing. Connecting: the
/// arm is in flight. Live: the arm succeeded and pushes are flowing. NotLive:
/// the arm failed; the page keeps its loaded data but stops updating until a
/// reload.
/// </summary>
public enum LivenessState
{
    Idle,
    Connecting,
    Live,
    NotLive,
}
