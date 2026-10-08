# Demonstrating event sourcing and CQRS

Use the business site to make a change and the Operations console to explain it. The same order connects the two sites. Each site has its own operator sign-in.

## A five-minute demonstration

1. In the business site, create an order using a stocked SKU, add a shipping address, and place it. Open the order detail page and choose **Order history**.
2. Compare the draft with the placed order. Select different recorded steps to show when items, the address, and the total appeared. These states are reconstructed from the order's events; they are not saved screen images.
3. While the current order can still be cancelled, cancel it with a useful reason. Refresh its history. Compare the steps before and after cancellation. The earlier state and the cancellation reason remain available.
4. In Admin, open **Event streams**, choose the matching order stream, and follow **Compare order history**. The replay lab reads the history once, then lets you compare recorded versions in memory. It changes no events or projections.
5. Follow **Audit this order**. Expand the cancellation's stored payload and metadata. Inspect the reason, actor, source, tenant, correlation, and causation. Follow the correlation link to see related payment, inventory, and process-manager events.

An existing cancelled order also works. A failed inventory reservation is a useful example: the trace can show the failure and the compensating actions that followed. Show the actual trace before describing which actions occurred.

## What each view demonstrates

| View | Demonstration |
| --- | --- |
| Business order history | Earlier order details remain available after later changes. Selecting two steps explains the difference. |
| Admin order replay lab | The aggregate's current C# event handlers reconstruct recorded state without executing commands. |
| Admin audit explorer | Event payloads and metadata answer what happened and provide links to its context. Filters combine by exact stream, correlation, tenant, and actor. |
| Correlation trace | A workflow can span aggregate and process-manager streams while retaining a shared correlation ID. |
| Projection status | Each read model progresses independently. Its checkpoint can trail the event-store head. |
| Rebuild a view | A tenant's order-activity read model can be rebuilt from retained events. This is a real recovery operation with a confirmation step. |

## An optional CQRS recovery demonstration

Open **Projection status** to inspect the event-store head and each checkpoint. If workers are caught up, say so. A zero gap is a valid result; do not promise visible lag on a small local demonstration.

**Rebuild a view** resets and replays one tenant's order-throughput buckets up to the captured checkpoint. Review the tenant and confirm only when you intend to change that read model. Other tenant buckets and the shared checkpoint remain separate. Readers can observe partial results while the rebuild runs. The business activity page uses a retention window, so old sample orders may produce an empty current chart even after a successful rebuild.

## Reading the evidence correctly

- Business history is authorized with `ViewOrder`, the server-resolved tenant, and customer ownership. An order can be reconstructed before its read-model projection exists.
- Admin tools are restricted by `AccessAdminConsole` and deliberately support investigation across tenants. The audit page identifies this scope.
- History comparisons cover the Sales order's status, items, address, and total. Payment, shipment, and return activity belong to other streams. The business order timeline combines selected activity and is not a complete audit log.
- Stream versions determine reconstruction order. Recorded event timestamps can be backdated. Historic schemas are upcast and interpreted by today's domain model. Use Audit explorer for the original stored schema and JSON.
- An actor ID may be inherited from the initiating command. It is not proof that a person directly wrote every event. Empty or absent actor metadata is shown as **System or not recorded**.
- Audit pages read at most 50 rows, newest global position first, and retain a captured upper position while paging. Refresh or applying filters captures a new boundary. Filters bound returned rows, not the amount of database work needed to find them; tenant and actor predicates have no new dedicated indexes in this slice.
- History reads fetch at most 501 rows to enforce a 500-event limit. Oversized or incomplete histories are reported rather than displayed as complete reconstructions.
- The bounded history reader and audit explorer currently support PostgreSQL. Other configured event stores show an explicit availability message. Their existing supported tools remain available.
- Malformed metadata can make an audit page fail. The view reports that failure; it is not a general repair tool for corrupt storage.
- Retained events support an audit trail. This implementation does not claim cryptographic tamper evidence or regulatory certification.

No schema migration or additional package is required for these views.
