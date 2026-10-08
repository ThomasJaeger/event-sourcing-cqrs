# Reference UI revision, 7 October 2026

This follows the completed manuscript revision. The application and AdminConsole now have a consistent visual layout and navigation around the existing Blazor screens. The author chose a realistic business application, with technical inspection kept in the admin UI. The work is based on companion commit `8f2a320b046c3d02dc8451bd1f32d630dc66f459`.

## Application

A new workspace page provides useful entry points for orders, inventory, and order activity. Persistent navigation connects the existing routes. Order and customer identifiers become links to their detail screens. Business labels replace raw event-type names in the order activity timeline, and long references are visually shortened without losing their full accessible text. Forms, page headings, empty states, and table containers receive consistent spacing and clearer visual hierarchy. Status and total follow the order reference in the table so phone users see the business outcome before secondary identifiers. The existing account form keeps its POST actions and antiforgery protection.

## AdminConsole

The landing page links to all four operator tools. Every tool keeps the same navigation. Forms fit narrow screens, long identifiers wrap, and event payloads and tables scroll within their containers. Provider-specific correlation-tracing limits stay visible. Projection lag appears beside the projection name, ahead of the underlying head and checkpoint positions.

The replay screen also fixes one interaction problem: a rebuild in progress keeps a running state visible and hides the entry and confirmation controls until the operation finishes. A pending-task test verifies that a second rebuild cannot be submitted through those controls. A retry after failure returns to the normal review and confirmation step.

## Boundaries

The existing API and host permission checks remain in place. This change does not connect the Web and AdminConsole authentication cookies. The original presentation pass left AdminConsole sign-in as a separate implementation gap and preserved its host-wide authorization gate. The live-demo follow-up below closes the account-surface gap. No anonymous bypass was added for visual review.

The existing cancellation retry/unknown-outcome behavior was identified during review and left outside this presentation change. Styling does not make that operation's transport recovery contract stronger.

The hosts continue using their existing Tailwind CDN. No package or component library was introduced. Navigation contains real destinations and the screens display existing application data; no decorative metrics were added.

## Validation

Both host builds passed with zero warnings or errors. The complete Web test project passed 273 tests, and AdminConsole passed 34. New behavior was checked with failing tests before implementation: row navigation, activity labels, rebuild running state, retry confirmation, and the revised table priority. Existing account POST/antiforgery controls and host authorization wiring were checked against the baseline.

Independent reviewers checked the other host's changes and found no remaining integration blocker. All existing Web Razor handler blocks remain unchanged. Admin reader and tracer handler blocks also remain unchanged; the rebuild screen's state transitions are the explicit behavior change.

The visual review covered 25 component states at 1440px and 390px, plus six checks at 320px. All 56 final screenshots were inspected. There was no document-level horizontal overflow. Wider tables and event payloads retain deliberate, keyboard-reachable scrolling. The review prompted the order and projection table priority fixes described above.

The visual proofs render the actual Razor components with explicit test data through the existing bUnit stack. They verify presentation at desktop and phone widths. They are not a claim that a complete deployment, external authentication, or every backing store was exercised.

## Selected visual proofs

These captures use synthetic fixture data and show the final reviewed components.

- [Business workspace, desktop](ui-previews/2026-10-07/web-home-desktop.png)
- [Orders, 320px phone](ui-previews/2026-10-07/web-orders-loaded-phone320.png)
- [Order details and activity, desktop](ui-previews/2026-10-07/web-order-detail-desktop.png)
- [New order, phone](ui-previews/2026-10-07/web-order-create-phone.png)
- [Admin workspace, desktop](ui-previews/2026-10-07/admin-home-desktop.png)
- [Projection status, 320px phone](ui-previews/2026-10-07/admin-projections-loaded-phone320.png)
- [Event browser, phone](ui-previews/2026-10-07/admin-streams-loaded-phone.png)
- [Rebuild in progress, phone](ui-previews/2026-10-07/admin-replay-running-phone.png)

## Live-demo account follow-up

Actual host testing found that the framework script was not mapped and that AdminConsole still had no usable sign-in surface. Both hosts now map their framework assets. AdminConsole adds a static password form, token-protected login and logout, rate limiting, and a separate secure cookie for the configured bootstrap actor. The current-role permission gate remains on tools and hub negotiation. The account page remains reachable after role revocation so the operator can sign out.

The password verifier is shared as source between the two hosts; no package or executable dependency was added. This follow-up is covered by real-host HTTP tests with an owned current-roles test port. Browser and live-database verification are recorded separately after integration.

## Studio visual refresh

The later [visual refresh record](visual-refresh-2026-10-07.md) covers the more colorful business and Admin designs, current live screenshots, and their validation.
