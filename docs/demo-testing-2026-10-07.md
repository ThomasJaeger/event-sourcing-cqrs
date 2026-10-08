# Live demo verification, 7 October 2026

The demo was started from reference commit `e0d67b7` with a separate PostgreSQL 16.6
container on loopback port 55432. Workers applied all 29 migrations and projected the
configured bootstrap administrator. The repository's clean, compensation, and tenant
scenarios completed against that isolated database.

## Runtime fixes

Actual browser interaction found two issues that component rendering did not exercise.

- The Blazor framework script returned HTTP 404. Both browser hosts now map static assets.
  The Web script returned HTTP 200 with a 200,575-byte body after the fix. This follows the
  [.NET 10 Blazor static-asset setup](https://learn.microsoft.com/en-us/aspnet/core/blazor/project-structure?view=aspnetcore-10.0).
- The Web API client serialized interface-typed commands and queries as empty objects.
  Concrete runtime payload serialization preserves identifiers, line items, nested Money
  values, and query pagination. Five transmitted-request tests failed before the fix and
  passed after it; the isolated Web suite passed all 278 tests.

The previously unfinished AdminConsole account flow was also completed for this run. It
uses the configured salted operator password, a separate secure cookie, antiforgery-protected
forms, a login rate limit, and the existing permission check against current roles. Its login
page renders without requiring an anonymous interactive circuit. Tool routes and the hub
remain protected. The Web and Admin hosts share the verifier source without a new package
or an executable-to-executable dependency.

The activity display now says "order updates per minute". Its existing projection counts
Sales events, so a single new order can contribute a draft, line, address, and placement.
The count is unchanged; the label now describes it.

## Verification

A real browser signed in, created a one-line NOTEBOOK order for $12.50, saved its shipping
address, and placed it. The wizard navigated to the live order detail with payment
authorization and shipment scheduling visible. The order list included that record, and
the inventory projection increased NOTEBOOK reserved stock from two to three while on-hand
stock stayed at 100. Orders, detail, inventory, and activity navigation worked without a
runtime or network error in the final navigation phase.

The Admin browser flow separately challenged the signed-in Web operator, accepted the
Admin password form, and loaded all eight registered projections. Navigation to Event
streams showed the completed order's seven events. Expanding OrderPlaced metadata and
following its correlation showed thirteen related events across order, fulfillment,
payment, inventory, and shipment streams, with tenant identifiers visible. Admin logout
revoked that browser session while preserving the separate Web session.

The initial demo and account work passed these checks:

- 278 Web tests, including five request-serialization regressions.
- 34 Admin component tests.
- 36 focused HTTP integration tests covering both account flows, Admin authorization,
  provider composition, and actual Blazor framework responses.
- Both browser-host builds, with no warnings or errors, and `git diff --check`.

Independent agents challenged the serialization fix and account implementation and ran
the live browser checks. No blocking findings remained in those reviews. The HTTP tests
include rejected login attempts, antiforgery enforcement, return-URL validation, configured
actor identity, and cookie isolation. Static-asset regressions were also demonstrated by
removing the mapping, observing failed tests, and restoring it. These checks cover the
reported changes; existing permission revalidation for long-lived circuits is unchanged.

The original component-preview report remains a record of that earlier presentation pass.
This run adds real browser and HTTP evidence; it does not certify every provider or a
production deployment. PostgreSQL is the only backend used in this live demo.

## Local use

On the prepared machine, the business UI is at `https://localhost:5101/login` and Admin is
at `https://localhost:5102/login`. Local credentials and process controls are in the private
`/tmp/esrcq-live-demo` directory, outside this repository. The demo uses its own database
and volume. No existing application database was reset or migrated.

Use the current README's configuration and startup commands to reproduce the setup on
another machine. The local test controller and sample-specific walkthrough are temporary
files, not a replacement for repository configuration.

## Stale account forms

A follow-up browser test reproduced a login error with two tabs. Both tabs loaded the
anonymous login form; signing in through the first succeeded, but submitting the second
sent its old anonymous token with the new authenticated cookie. Antiforgery correctly
rejected the request. The response was a raw JSON error with no way to continue.

Web also inherited interactive rendering for its account page despite a comment describing
it as static. Account forms now render through explicit HTTP endpoints, outside the
interactive router. This follows the separation of cookie-based account pages from
interactive components described in the [Blazor render-mode guidance](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/render-modes?view=aspnetcore-10.0).

Both hosts still return HTTP 400 for invalid antiforgery tokens and perform no sign-in or
sign-out action. They render a current account page with a brief message, a fresh token,
and a continuation link for operators who are already signed in. Rejected requests do not
supply the replacement page's return URL. Account responses are not cached. Refreshing an
old login tab or opening `/login` directly also loads the current account state.

Seven additional regression cases cover static Web account rendering, stale login and
logout recovery on each host, and two unsafe Web return URLs. Each new case failed before
the fix. The combined 36 account and authorization integration tests passed afterward.
The recovered forms are submitted in the tests to prove their replacement tokens work.

After restarting both UI hosts, a separate browser repeated four live cases: stale login
and stale logout for Web and Admin. All four showed a usable current account page while
retaining HTTP 400 for the rejected submission. The continuation links, replacement
sign-in forms, Account navigation, and ordinary sign-out all worked. No application data
was changed by these checks. The complete Web component suite also passed all 278 tests.

## Stream and correlation discovery

The PostgreSQL Admin tools now offer lists of stored IDs before an operator enters one.
Each page contains at most 25 distinct IDs in ID order. Previous and Next move between
pages; Refresh starts at the beginning. Selecting a row uses the existing stream inspector
or correlation tracer, and direct entry remains available if discovery fails.

The metadata-only reader first selects a bounded page of IDs, then summarizes only those
IDs. It reuses the stream and correlation indexes without a migration and never loads an
event payload or resolves its CLR type. The response size is bounded; database work still
depends on how many events share the selected IDs. Pagination is a live view, not a frozen
snapshot: refresh to discover IDs added before the current cursor.

Stream choices exclude process-manager streams because the existing aggregate inspector
does not support them. Correlation choices include process-manager events, while excluding
missing IDs and the empty sentinel. Both lists preserve Admin's cross-tenant visibility.
The current deployment uses PostgreSQL. KurrentDB and DynamoDB retain their existing direct
stream inspection and tracing limitations; their catalogs are explicitly unavailable.

Validation for this follow-up passed 17 real PostgreSQL catalog tests, all 55 Admin
component tests, and 30 focused HTTP tests covering catalogs, provider composition,
page rendering, login, and authorization. The new database and HTTP cases failed before
implementation. The component run initially recorded 20 missing-picker failures and one
passing existing capability guard. Independent source review found no blockers.

The live browser displayed 21 aggregate streams and 35 correlation IDs. It exercised
correlation paging (25 then 10), Previous, Refresh, selection, metadata, and direct entry.
Selecting the completed order loaded seven events. The selected correlation loaded nine
events across five streams, including process-manager rows, with tenant labels visible.
The stream list fit one live page; its pagination is covered by the database and component
tests. Cross-tenant summary counts are also covered by the database tests.

A phone check caught loaded results appearing below the viewport. Both tools now focus
the loaded detail heading after the read and render complete. A second real-pointer browser
run verified that heading was visible and focused at 390px. All ten final desktop and phone
screenshots were inspected, with no horizontal page overflow or remaining visual blocker.
The browser recorded no application HTTP errors, exceptions, or network failures.

## Remaining limits

The new-order wizard can miss an already-cancelled outcome after very fast compensation;
it currently waits for an exact Placed state. For the first manual order use a stocked SKU
such as NOTEBOOK, then inspect Orders if an outcome is uncertain. The current business UI
has no tenant switcher. The seeded two-tenant example can be inspected through Admin.

The failed pre-fix request created a diagnostic empty-ID draft in the isolated demo. It is
preserved as evidence. No production data was used. Existing Tailwind CDN warnings and
browser-specific development-certificate trust are separate from authentication behavior.


## Order history, audit, and replay demonstration

The business site now has `/orders/{orderId}/history`, linked from order details. It compares
recorded status, items, shipping address, total, and the descriptions of selected changes,
including cancellation reasons. Admin adds `/audit` and `/order-history`, with connected links
to the existing stream browser and correlation tracer. Both home pages describe a practical
route through the demonstration. See [the demo guide](event-sourcing-demo-guide.md).

The new bounded history port and PostgreSQL adapter fetch at most 501 rows before hydration.
The application refuses histories beyond 500 events and checks stream identity, tenant,
contiguous versions, the initial draft, and active line identity before returning copied states.
Customer ownership is proved before reading the full history or exposing its integrity errors.
Reconstruction uses the Order aggregate's historical-event handler, including current upcasters.
It does not dispatch commands or change projections.

The audit reader uses raw JSON, including unknown payload types and process-manager events,
with exact optional stream, correlation, tenant, and actor filters. Pages contain at most 50
events in descending global-position order and preserve a captured upper bound while paging.
Malformed metadata can still fail a page; the UI reports the failure rather than presenting
partial evidence. PostgreSQL supports both new ports; other providers have explicit capability
responses and do not fall back to scanning all history.

Independent reviews found and corrected three concrete defects: ambiguous alternate stream
spellings, changed query links dropped while a previous read was pending, and duplicate active
line IDs in malformed histories reaching UI comparison code. The navigation and line-identity
regressions failed before their corrections. Removing and then re-adding a line ID remains valid.
Unprovable customer ownership now returns the same missing-order result instead of disclosing
a history-integrity problem.

Validation includes 296 Web component/client tests, 83 Admin component tests, 35 focused
Application tests, 13 real PostgreSQL
bounded-history tests, 16 real PostgreSQL audit tests, and 74 focused HTTP/integration tests.
The HTTP suite covers login, permission gates, current-tenant isolation, ownership, provider
composition, query inventories, and linked investigation pages. A database fingerprint before
and after HTTP investigation proves that events, outbox entries, projection checkpoints, and
activity buckets are unchanged.

Live browser checks exercised the existing cancelled order: audit pages of 50 and 35 events,
an exact-stream filter returning five events, raw cancellation reason, correlation links with
process-manager rows, versions 4 and 5 as Placed and Cancelled, the original empty draft, refresh,
and links from the business order detail. Desktop (1440px) and phone (390px) views had no horizontal
page overflow. The successful run recorded no application HTTP errors or JavaScript exceptions.
Evidence is retained locally under `/tmp/esrcq-live-demo/history-proof/`.

The final polish run verified anonymous order-history guidance and its preserved sign-in return
path, then repeated the connected live flow against the restarted sites. All 517 automated checks
in the suites listed above passed. The final desktop and phone captures were inspected; heading
focus no longer draws a box around non-interactive titles, and ordinary text wraps without splitting
words. No application HTTP errors or JavaScript exceptions were recorded in the final browser run.
