# Event Storming Mapping

This document captures the Event Storming notation for each aggregate in the reference implementation. The format mirrors what a facilitator would see on a wall: orange events along a timeline, blue commands that produce them, lilac aggregates that own them, yellow actors that issue the commands.

Each aggregate has its own section using the same four-block template. Sections are appended in the order aggregates ship. Order ships in Phase 1, Weeks 1-2. Inventory, Shipment, and Payment ship in Phase 4. The OrderFulfillment and Return process managers ship in Phase 5.

## Order (Sales)

### Events (orange)

- OrderDrafted
- OrderLineAdded
- OrderLineRemoved
- ShippingAddressSet
- OrderPlaced
- OrderCancelled
- OrderShipped

### Commands (blue)

- Draft
- AddLine
- RemoveLine
- SetShippingAddress
- Place
- Cancel
- Ship

### Aggregate (lilac)

- Order

### Actors (yellow)

- Customer: Draft, AddLine, RemoveLine, SetShippingAddress, Place, Cancel
- Fulfillment Worker: Ship
