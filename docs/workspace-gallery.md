# Workspace gallery

Explore the business screens and the operations console from the current application on `main`.
These captures show webpage content using the running PostgreSQL demo. Desktop views are 1440 pixels wide;
phone views are 390 pixels wide. Open an image to see it at full size.

[Orders workspace](#orders-workspace) · [Operations console](#operations-console) · [Phone views](#phone-views)

To run the application, follow the [setup instructions](../README.md#running-it).
For a demonstration that connects business actions with their recorded history, use the
[demo guide](event-sourcing-demo-guide.md).

## Orders workspace

The overview connects orders, inventory, and activity, with a short walkthrough for comparing an order's history.

![Orders workspace overview with navigation, task cards, and an order-history walkthrough](ui-previews/2026-10-07-studio/business-home-desktop.png)

<details>
<summary>Orders</summary>

Review the displayed orders, their statuses, totals, and latest updates. Summary counts describe the loaded page.

![Orders list with status summaries and links to individual orders](ui-previews/2026-10-07-studio/orders-desktop.png)

</details>

<details>
<summary>My orders</summary>

View the orders available to the signed-in account.

![My orders with account-specific order totals and statuses](ui-previews/2026-10-07-studio/my-orders-desktop.png)

</details>

<details>
<summary>Inventory and adjustments</summary>

Check on-hand and reserved stock, create an inventory record, or enter an adjustment with its reason. The dialog captures show the forms before submission.

![Inventory quantities with reserved-stock bars](ui-previews/2026-10-07-studio/inventory-desktop.png)

![Create inventory form for a new SKU](ui-previews/2026-10-07-studio/inventory-create-dialog.png)

![Inventory adjustment form with quantity and reason fields](ui-previews/2026-10-07-studio/inventory-adjust-dialog.png)

</details>

<details>
<summary>Order details</summary>

Read the current status and total alongside line items and the recorded activity timeline.

![Cancelled order details with total, line items, and activity timeline](ui-previews/2026-10-07-studio/order-detail-desktop.png)

</details>

<details>
<summary>Order history</summary>

Compare two recorded steps and see the changes to status, line items, address, and total. Viewing history leaves the current order unchanged.

![Earlier and later order states with the placed-to-cancelled change highlighted](ui-previews/2026-10-07-studio/business-history-desktop.png)

</details>

<details>
<summary>Create an order</summary>

Move through customer, line items, shipping, and review. This capture shows the first step.

![New order form with a four-step progress strip](ui-previews/2026-10-07-studio/new-order-desktop.png)

</details>

<details>
<summary>Order activity</summary>

Inspect the latest reported throughput reading and its timestamp.

![Order activity view with the latest reported updates-per-minute reading](ui-previews/2026-10-07-studio/order-activity-desktop.png)

</details>

## Operations console

The console connects audit evidence, event inspection, reconstructed history, workflow tracing, and projection recovery.

![Operations console overview with six investigation and recovery tools](ui-previews/2026-10-07-studio/admin-home-desktop.png)

<details>
<summary>Audit explorer</summary>

Filter recorded changes by stream, actor, tenant, or correlation. Expand an event to read the original stored JSON and metadata. These views follow a cancelled order.

![Audit explorer filtered to one order stream](ui-previews/2026-10-07-studio/admin-audit-desktop.png)

![Cancellation event with its recorded reason and raw event evidence](ui-previews/2026-10-07-studio/admin-audit-evidence-desktop.png)

</details>

<details>
<summary>Event streams</summary>

Pick a stream ID from the catalog, then inspect its ordered events and interpreted payloads.

![Event stream catalog with selectable stream IDs](ui-previews/2026-10-07-studio/admin-streams-desktop.png)

![Selected order stream with event versions and payloads](ui-previews/2026-10-07-studio/admin-stream-events-desktop.png)

</details>

<details>
<summary>Correlation trace</summary>

Choose a correlation ID and follow its events across aggregate and process-manager streams. The detail view preserves the tenant context.

![Correlation catalog with counts of events, streams, and tenants](ui-previews/2026-10-07-studio/admin-correlations-desktop.png)

![Selected cancellation workflow with correlated aggregate and process-manager events](ui-previews/2026-10-07-studio/admin-correlation-trace-desktop.png)

</details>

<details>
<summary>Order replay lab</summary>

Reconstruct two order versions from their recorded events and compare the resulting states without changing the live data.

![Order replay lab comparing the placed and cancelled versions of an order](ui-previews/2026-10-07-studio/admin-order-history-desktop.png)

</details>

<details>
<summary>Projection status</summary>

Compare projection checkpoints with the event-store head. Positions behind describes a position gap, not a count of missing events.

![Projection checkpoints, event-store head, and position gaps](ui-previews/2026-10-07-studio/admin-projections-desktop.png)

</details>

<details>
<summary>Rebuild a view</summary>

Review the tool for rebuilding one tenant's order-throughput view from its events. This screenshot shows the entry form before review and confirmation.

![Tenant-specific order-throughput rebuild entry form](ui-previews/2026-10-07-studio/admin-rebuild-desktop.png)

</details>

## Phone views

Navigation and cards adapt to the narrower viewport. Wider data tables scroll inside their containers.
Expand a view below to inspect the phone layout.

<details>
<summary>Orders overview</summary>

<img src="ui-previews/2026-10-07-studio/business-home-phone.png" width="390" alt="Orders overview at a phone viewport width" />

</details>

<details>
<summary>Order list</summary>

<img src="ui-previews/2026-10-07-studio/orders-phone.png" width="390" alt="Order list at a phone viewport width" />

</details>

<details>
<summary>Inventory</summary>

<img src="ui-previews/2026-10-07-studio/inventory-phone.png" width="390" alt="Inventory at a phone viewport width" />

</details>

<details>
<summary>Order history</summary>

<img src="ui-previews/2026-10-07-studio/business-history-phone.png" width="390" alt="Order history at a phone viewport width" />

</details>

<details>
<summary>Create an order</summary>

<img src="ui-previews/2026-10-07-studio/new-order-phone.png" width="390" alt="Create an order at a phone viewport width" />

</details>

<details>
<summary>Inventory adjustment</summary>

<img src="ui-previews/2026-10-07-studio/inventory-adjust-dialog-phone.png" width="390" alt="Inventory adjustment at a phone viewport width" />

</details>

<details>
<summary>Operations overview</summary>

<img src="ui-previews/2026-10-07-studio/admin-home-phone.png" width="390" alt="Operations overview at a phone viewport width" />

</details>

[Back to the repository overview](../README.md#workspaces).
