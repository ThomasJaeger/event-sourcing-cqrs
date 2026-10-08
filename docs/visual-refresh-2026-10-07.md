# Visual refresh, 7 October 2026

The business site now has a more playful studio style: warm paper backgrounds, violet controls, coral and lime accents, larger headings, a persistent navigation rail, and original parcel artwork. Admin uses the same colors with a darker masthead and an illustration connecting recorded changes to business state.

## What changed

- The business overview has illustrated artwork, colorful task cards, and a short order-history walkthrough.
- Orders and My Orders show real counts from the displayed page, clear status pills, compact identifiers, and readable tables. The counts explicitly describe the loaded page, not all orders.
- Inventory has quantity summaries and bars showing each SKU's reserved share. Its existing create and adjustment actions remain available.
- Order detail emphasizes status and total, with a connected activity timeline. History has distinct earlier/later panels and a visible change summary. Creation has a numbered progress strip and a prompt for each step.
- Admin has illustrated navigation and six distinct tool cards. Investigation forms, tables, payloads, and account screens share the visual theme.

Business data, command handlers, and permissions remain unchanged by this presentation pass. The one asset-routing change allows the Admin account stylesheet to load before sign-in; tool routes still require authorization. Its focused HTTP regression test verifies both conditions.

The artwork is local SVG. No font service, UI package, or image dependency was added. The hosts retain their existing Tailwind CDN dependency.

## Verification

Both host builds passed with zero warnings or errors. The complete Web and Admin component suites passed 296 and 83 tests respectively. The focused authentication, event catalog, and investigation integration suite passed 37 tests, for 416 distinct automated checks. The seven navigation tests were also rerun after final wording and artwork corrections.

A live browser checked the sites against the existing PostgreSQL demo at 1440 × 1000 and 390 × 844. The review covered home, orders, My Orders, inventory, order detail, history, creation, accounts, audit, replay, and linked stream/correlation inspection. Audit pagination returned 50 then 35 events; the selected order filter returned its five recorded changes. Business and Admin history comparisons, refresh, and investigation links worked. No browser application errors were recorded in the completed checks.

Phone tables scroll within their cards without widening the document. Create and adjust inventory dialogs opened and cancelled correctly. Reduced-motion preferences disable decorative animation, and the default parcel motion ends after 3.5 seconds. A 1024 × 400 check verified that the sidebar scrolls to its remaining navigation. Existing creation controls were inspected without submitting another demo order.

Independent agents reviewed the source and live screenshots. Their findings led to darker small text, a scrollable short-window sidebar, a finite animation, accurate cancellation instructions, and a wider illustration label. Keyboard focus remains visible on interactive controls.

## Selected live captures

- [Business overview](ui-previews/2026-10-07-studio/business-home-desktop.png)
- [Business overview on a phone](ui-previews/2026-10-07-studio/business-home-phone.png)
- [Orders](ui-previews/2026-10-07-studio/orders-desktop.png)
- [Inventory](ui-previews/2026-10-07-studio/inventory-desktop.png)
- [Order history](ui-previews/2026-10-07-studio/business-history-desktop.png)
- [New order](ui-previews/2026-10-07-studio/new-order-desktop.png)
- [Admin overview](ui-previews/2026-10-07-studio/admin-home-desktop.png)

These images show the local demo. They supersede the earlier presentation captures linked from the original UI revision record.
