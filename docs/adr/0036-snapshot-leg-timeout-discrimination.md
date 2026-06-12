# 0036. Snapshot-Leg Timeout Discrimination

## Status

Accepted (June 2026). Extends ADR 0035.

## Context

ADR 0035 repaired the authorize leg and named the residual this ADR repairs: an ApiClient timeout during the initial snapshot surfaced as TaskCanceledException carrying a TimeoutException inner, the catch (OperationCanceledException) arm in CircuitResourceSubscription.RefreshAsync swallowed it, StartAsync completed, and the page read Live with a registration armed and no snapshot delivered. The badge overstated the arm's verdict on the one leg ADR 0034 left it to report. The notification leg carried the same swallow with less visibility: RefreshAsync consumed the timeout before the dispatcher drain loop's isolation could log it, so a timed-out re-query produced neither a log line nor a state change.

Five shapes were considered and rejected. Translation at the ApiClient boundary spends blast radius over every IApiClient consumer on a discriminator that lives in the component; command and query callers own their own exception taxonomy, the locality ADR 0035 chose to preserve. Configuring a Timeout on IApiClient in this slice conflates two changes: the repair works against the 100-second default's exception signature, and a tighter timeout changes every page load's failure envelope, which is its own slice. Swallow-but-log in the arm preserves the false Live and repairs nothing user-visible. Per-delivery failure surfacing, degrading the badge when a push-leg re-query times out, adds a contract surface across three pages against ADR 0034's page-owned liveness for a transient the drain loop already logs. Unwinding the registration on a snapshot fault spends the self-healing that register-before-snapshot bought and contradicts the pinned arm-failure contract, which keeps the subscription in place when the arm throws.

## Decision

RefreshAsync replaces its single catch (OperationCanceledException) arm with a two-filter catch surface, ordered. First, catch (OperationCanceledException) when (_cts.IsCancellationRequested) returns quietly: the component's own token is the only cancellation it owns, and the filter sits first so a teardown racing a timeout resolves as teardown. The filter reads IsCancellationRequested on the source rather than the Token property, which throws ObjectDisposedException when the catch runs after disposal. Second, catch (TaskCanceledException) when the inner exception is TimeoutException throws a new TimeoutException carrying the original, the ADR 0035 filter shape. Anything else, bare or foreign cancellation included, propagates unchanged.

This is a deliberate deviation from ADR 0035, which rejected translation inside CircuitResourceSubscription as a middleman pattern-matching another component's transport convention. That rejection was scoped to the authorize leg, where the component sits over another client's call and owns no cancellation source on it. On the snapshot leg the component owns _cts, so the teardown-versus-timeout discriminator exists only at this boundary, and translating at the ApiClient boundary instead would widen the exception envelope of every page consumer. The component is the principal on this leg, not a middleman.

## Consequences

On the snapshot leg the timeout faults StartAsync, lands in the pages' existing Exception arms, and surfaces the not-Live badge with no page changes. The registration stays armed: register-before-snapshot survives the fault, the badge understates rather than overstates, and a later delivery re-queries and applies.

On the notification leg the translated timeout escapes RefreshAsync into the dispatcher drain loop's catch in ResourceNotificationDispatcher, where it is logged and isolated and the next delivery recovers. The timeout that produced neither a log line nor a state change now produces the log line.

Five tests in CircuitResourceSubscriptionTests are the slice's tests of record. Two were RED: A_snapshot_timeout_faults_StartAsync_as_TimeoutException pins the StartAsync fault and the translation shape, and A_bare_cancellation_from_the_query_propagates_unchanged pins the discriminator's boundary. Three were declared green-on-write pins: A_snapshot_timeout_leaves_the_registration_armed_so_a_later_delivery_applies (registration survival), A_teardown_cancellation_during_the_snapshot_still_returns_quietly (teardown's quiet return), and A_push_delivery_timeout_is_isolated_and_a_following_delivery_still_applies (the notification leg's apply-nothing-then-recover observable, which holds across the throw's move from the arm's swallow to the drain loop's isolation).

One reading this ADR accepts rather than repairs: a bare or foreign cancellation propagating from either leg lands in the pages' cancellation arms, which set nothing, so the badge stays at Connecting on a page that is not going away. No producer of that shape exists in the current call graph: the component handles its own token internally, the pages pass CancellationToken.None into StartAsync, and an elapsed HttpClient timeout takes the translated path. Every code repair considered either alters pinned page behavior or degrades the real marshal-leg teardown case the cancellation arms exist to absorb. A real foreign cancellation source on these legs, for example a page passing a live token into StartAsync, reopens this reading.

## Trigger for revisiting

A third site discriminating timeout from cancellation makes the filter pair a candidate for a named helper; two sites name the pattern. A Timeout configured on IApiClient is its own slice and revisits the 100-second default signature this repair works against. A consumer that needs the badge to degrade on a push-leg timeout reopens ADR 0034's rejected health plumbing, not this catch surface.

## Relationship to other ADRs

Extends ADR 0035 (Timeout Translation at the HTTP Boundary): repairs the residual its Consequences carried, and revisits its component-translation rejection for the snapshot leg on ownership grounds while the authorize-leg rejection stands. The pages' liveness vocabulary and the badge (ADR 0034) and the subscription contract and dispatcher isolation (ADR 0032) are unchanged.
