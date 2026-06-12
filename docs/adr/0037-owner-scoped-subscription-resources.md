# 0037. Owner-Scoped Subscription Resources

## Status

Accepted (June 2026). Sibling of ADR 0033; extends ADR 0032.

## Context

The customer-facing order dashboard (MyOrders) is a collection page: its live view spans every order the calling customer owns. ADR 0033's answer for collection pages is a stable sentinel resource id shared by the publishing projection and every subscriber, which fits the InventoryDashboard because every operator may see every SKU. It does not fit a customer's order list. The rows are owner-scoped: ListOrders filters to the calling customer, and the subscription gate enforces the same ownership rule a read runs under (ADR 0027, ADR 0028). A subscription surface for this page has to respect that boundary at the routing layer, not only at the query.

Three shapes were considered and rejected. An AllOrders collection sentinel mirrors ADR 0033 but fans every order event out to every subscribed circuit in the tenant, each of which re-queries a list that almost never changed for them; worse, delivery itself is a timing side channel, telling an owner-scoped subscriber that someone's order changed even though its re-queried list shows nothing. Per-order subscriptions (one registration per row, OrderDetail-style) multiply registrations per circuit ahead of need and leave a newly placed order unsubscribed until a re-arm, exactly the row a customer is watching for. No live surface at all keeps the page a one-shot read and forfeits the slice's purpose.

## Decision

A customer-scoped collection subscribes under the owning customer's id. The resource id is the customer id in Guid "D" string form, on both sides of the dispatch:

* The publishing projection stages one envelope per row change, keyed by the changed row's owning customer id. OrderPlaced carries the customer id in its payload; the update paths recover it from the row itself through the unit of work's tenant-scoped RETURNING contract (UpdateStatusAsync and MarkReturnedAsync return the matched row's customer id, null when no row matched under the current tenant), so a zero-row change on the update paths stages nothing and no second read runs. OrderPlaced stages from the event payload unconditionally, so a conflicted insert stages a benign self-keyed notification.
* The page resolves its own actor id from the circuit identity and subscribes under it: the actor-is-customer convention (P9.5) makes the actor id the customer id it owns. Only that customer's circuits re-query on a change.
* The gate authorizes without a read-model read: deny without ViewOrder; allow any customer id for an operational principal holding ViewCustomer; otherwise allow if and only if the ownership resolver's customer id for the actor equals the requested resource id. For an owner-scoped principal, a malformed id is a denied decision, not a 500; an operational principal holding ViewCustomer is allowed any string id, and such a subscription can never receive a dispatch, since projections stage only Guid form keys.

The convention is the owner-scoped sibling of ADR 0033's collection sentinel: same envelope shape, same dispatcher, same tenant-qualified ResourceKey, different key discipline. A sentinel says everyone watching this type; an owner key says the one principal whose rows these are.

## Consequences

Notification fan-out matches data visibility: a circuit is woken only for changes it could see, and the timing side channel of a shared sentinel never exists. The projection pays one RETURNING clause instead of a lookup table or a second query.

A future operational live order list (Support or Admin watching all orders) is not this convention: it would be sentinel-shaped under a ViewCustomer-class gate, the ADR 0033 shape behind an operational permission, because its viewers may see every row. Reusing the owner key there would force an operational page to impersonate customers one at a time.

The order-list unit of work's UPDATE statements gained tenant predicates with the RETURNING contract. The remaining projection units of work whose UPDATEs still filter on primary key alone warrant the same audit; their ids are globally unique Guids today, so the missing predicates are hardening rather than a live defect, but the asymmetry is now visible and recorded.

The P11.12 slice title's collection-sentinel framing now covers only the second dashboard (the admin metrics surface); the customer order dashboard ships under this owner-scoped convention instead.

## Trigger for revisiting

A second owner-scoped subscriber (per-customer notifications, a customer-scoped returns view) confirms the convention. A consumer needing sub-customer granularity, a single order watched from the collection page, reopens the per-order alternative for that page. An operational live order list triggers the sentinel-shaped sibling, not a change here.

## Relationship to other ADRs

Sibling of ADR 0033 (Collection-Scoped Notification Subscriptions): both key a collection page's subscription; 0033 keys by a shared sentinel, this ADR keys by the owning principal's id. Extends ADR 0032 (In-Process Notification Dispatch): the envelope, dispatcher, and tenant-qualified ResourceKey are unchanged. Leans on ADR 0028's permission model and the P9.5 actor-is-customer ownership resolver for the gate's compare, and on ADR 0031's tenant discriminator for the RETURNING contract's tenant predicate.
