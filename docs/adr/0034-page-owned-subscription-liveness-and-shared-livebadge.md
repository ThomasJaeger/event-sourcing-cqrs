# 0034. Page-Owned Subscription Liveness and the Shared LiveBadge

## Status

Accepted (June 2026). Extends ADR 0032.

## Context

The three subscriber pages (OrderDetail, InventoryDashboard, and the OrderCreate wizard) arm their in-process circuit subscription with one StartAsync call guarded by two catch arms. The OperationCanceledException arm covers teardown mid-arm: the page is going away and nothing needs surfacing. The Exception arm covers a failed arm: the page keeps its loaded data and stays up, but pushes never start, and until this slice nothing recorded or rendered that loss. P11.7 and P11.8 pinned both arms, and the arm comments on OrderDetail and InventoryDashboard carried the debt explicitly: a page-level liveness signal owed to a shared LiveBadge slice.

The degraded timer does not cover the gap. It is dispatch-scoped: armed when a command dispatches, cancelled by the settling push, surfacing the failed badge at its deadline. It observes nothing about the subscription. On OrderDetail and InventoryDashboard no timer runs at the arm, so an arm failure leaves no deadline to fire. On the wizard the timer covers the PlaceOrder outcome, not whether pushes can flow at all.

Four shapes were considered and rejected. Health plumbing on the subscription surface (an event, a status property, a completion task on ICircuitResourceSubscription) builds observation machinery for consumers that do not exist; the pages already sit on the only observation point, the StartAsync await, and learn the arm's outcome there without new plumbing. Extending the degraded timer to carry liveness misuses it: the timer observes nothing, is dispatch-scoped, and after a failed arm is absent on the two render-armed pages. A two-state vocabulary (Live, NotLive) renders a false alarm in the pre-arm window, where OnAfterRenderAsync has not run yet on the render-armed pages and the wizard spends its whole pre-dispatch life unarmed by design; neither is a failure. A shared page base class extracting the arm pattern trades visible, modestly repeated arm code for hidden lifecycle coupling; the three pages share a vocabulary, not a lifecycle: two arm at first interactive render, one arms mid-dispatch.

## Decision

Subscription liveness and the dispatch-outcome degraded timer are distinct axes, and they coexist. The timer answers whether the command's settling push arrived in the window. Liveness answers whether pushes can arrive at all. Neither subsumes the other. On the wizard both act on an arm failure: the deadline still surfaces the failed badge, and the liveness badge names the cause.

Liveness is decided once, at the StartAsync arm, and owned as per-page state. Each subscriber page holds a LivenessState field with four values: Idle renders nothing, Connecting spans the arm, Live is set when StartAsync returns, NotLive is set in the Exception arm. The render-armed pages initialize to Connecting; the wizard initializes to Idle and enters Connecting when PlaceOrder dispatches. The OperationCanceledException arm sets nothing: teardown is not a liveness outcome. There is no probe, no reconnection, and no re-evaluation after the arm; the field records the arm's verdict, and a reload is the recovery path the arm comments already promise.

The shared piece is presentational only. LiveBadge takes the LivenessState and an optional NotLiveDetail message and renders the pill; it has no DI, no subscription-surface plumbing, and no knowledge of why the page chose the state. Idle renders nothing, mirroring the BadgeState.Idle convention, so a page with no armed subscription shows no liveness chrome. Each non-Idle state renders a stable string under the #liveBadge id, the seam the page specs assert through.

## Consequences

The silent-not-live state is no longer silent. Each page renders the badge where its users already look: beside the OrderDetail heading, in the InventoryDashboard header, and beside the wizard's place controls, where the NotLive detail points the user at the orders list because the accepted order may still be processing. The owed-signal sentences in the two arm comments are replaced by sentences recording the surface.

The verdict is as coarse as the arm, in both directions. A subscription that arms and later loses its feed upstream still reads Live, because the page has no signal for that loss. The inverse also holds: the subscription registers on the dispatcher before its initial snapshot, so a snapshot-query failure faults StartAsync with the registration live, and the page then reads NotLive while pushes still update it. A cancellation that is not teardown, such as the authorize call's HTTP timeout surfacing as TaskCanceledException, lands in the OperationCanceledException arm and leaves the badge at Connecting. The badge is honest about the arm's verdict, not about every failure mode around it; sharpening any of these readings needs observation machinery the subscription surface does not carry and reopens the rejected health-plumbing alternative. The pages repeat the small liveness transitions rather than inheriting them, which keeps each page's arm readable on its own and keeps the component free of page lifecycle.

## Trigger for revisiting

A consumer that needs liveness after the arm (dispatcher health, missed-push detection, reconnection) reopens the health-plumbing alternative, since that signal cannot be read from the arm alone. A fourth subscriber page repeating the pattern is vocabulary repetition, not a base-class trigger; revisit the base class only if the page lifecycles themselves converge.

## Relationship to other ADRs

Extends ADR 0032 (In-Process Notification Dispatch): the liveness state records the outcome of the StartAsync arm 0032 defines and changes nothing about dispatch, authorization, or the subscription contract. The InventoryDashboard's collection-scoped subscription (ADR 0033) takes the same retrofit as the single-resource pages; the sentinel changes nothing about the arm.
