# 0035. Timeout Translation at the HTTP Boundary

## Status

Accepted (June 2026). Extends ADR 0034.

## Context

The three subscriber pages classify the StartAsync arm's outcome by exception type (ADR 0034): OperationCanceledException is teardown and sets nothing, every other exception is failure and reads NotLive. HttpClient breaks that classification for one failure mode. An elapsed HttpClient.Timeout surfaces as TaskCanceledException, a subclass of OperationCanceledException, so a timed-out authorize call landed in the teardown arm and stranded the badge at Connecting. ADR 0034's Consequences carried the misreading as a known residual. The authorize registration also ran on the 100-second HttpClient default, so the stranded state could outlast any defensible wait for an in-cluster call.

The runtime already disambiguates the shape. Since .NET 5, a timeout-bred TaskCanceledException carries a TimeoutException as its InnerException, and a caller-driven cancellation does not. The inner exception is HttpClient's documented convention for telling the two apart.

Four shapes were considered and rejected. Per-page exception filters on every subscriber arm replicate HTTP-boundary knowledge across every page, including the two queued dashboards, and each new subscriber inherits the obligation. Translation inside CircuitResourceSubscription.StartAsync puts a middleman in the business of pattern-matching another component's transport convention; the subscription surface does not own the HttpClient and should not know its idioms. A new domain exception type buys nothing: no consumer distinguishes it from TimeoutException, which makes it abstraction ahead of need. Document-only leaves a live misclassification on disk that the two queued dashboard pages would inherit. ADR 0036 revisits the component-translation rejection for the snapshot leg, where the component owns the cancellation source it must discriminate against; the authorize-leg rejection stands.

## Decision

SubscriptionAuthorizationClient translates the timeout shape at the HTTP boundary it owns. The send is wrapped narrowly in an exception filter: catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException) throws a new TimeoutException naming the subscription authorization call, with the caught TaskCanceledException as InnerException. The pages' Exception arm then reads the timeout as NotLive with no page change. A TaskCanceledException without the TimeoutException inner does not match the filter and propagates unchanged: that shape is genuine cancellation, and the teardown arm keeps it.

The discriminator is the inner exception, not the caller token's state. A check on ct.IsCancellationRequested reads an ambient signal that races: a caller token cancelled in the same window as a genuine timeout would misclassify the timeout as teardown. The subscriber pages also pass CancellationToken.None today, which makes a token-state check vacuous now and fragile the day a real token arrives. The inner TimeoutException is the runtime's deliberate disambiguation and depends on nothing outside the exception itself.

The authorize registration sets a 10-second timeout in place of the 100-second HttpClient default. The call is an in-cluster round trip from the Web host to the Api host's authorize endpoint; ten seconds is generous for that path, and a subscribe that cannot be authorized inside it should fail the arm and surface NotLive rather than hold the arm open.

## Consequences

A timed-out authorize call now faults the StartAsync arm with TimeoutException, lands in the pages' Exception arm, and reads NotLive. ADR 0034's Consequences paragraph is amended to record the repaired reading and point here.

The residual this ADR does not repair: an ApiClient timeout during the initial snapshot is swallowed by the catch (OperationCanceledException) arm in CircuitResourceSubscription.RefreshAsync, so StartAsync completes and the page reads Live with no snapshot delivered. That false-Live leg is its own slice; the dispatcher delivery path's exception handling gets grounded before any repair there, because the same RefreshAsync runs on notification-driven refreshes inside the dispatcher's drain loop. The residual is repaired by ADR 0036.

The translation is deliberately local to the one client. ApiClient keeps its untranslated transport exceptions; its callers own command-dispatch and query semantics with their own exception taxonomy (the ApiClientException hierarchy), and widening this decision to that surface is a separate decision with its own consumers to ground.

## Trigger for revisiting

A second HttpClient-backed client whose callers classify timeout against cancellation reopens the question of where the translation lives; one repeat is a pattern to name, not yet a shared helper. Repairing the snapshot-leg swallow in RefreshAsync revisits the Consequences residual directly. The trigger fired: ADR 0036 records the revisit.

## Relationship to other ADRs

Extends ADR 0034 (Page-Owned Subscription Liveness and the Shared LiveBadge): the translation makes the authorize timeout reach the Exception arm 0034 defines, and changes nothing about the liveness vocabulary, the badge, or the subscription contract (ADR 0032).
