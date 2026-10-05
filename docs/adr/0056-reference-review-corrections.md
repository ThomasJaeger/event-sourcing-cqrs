# 0056. Reference implementation review corrections

## Status

Accepted (October 2026). Amends ADRs 0016, 0020, 0028, 0041, 0049, and 0055 where the behavior below differs.

## Authentication

The Web host issues the configured operator's cookie only after verifying a password against
`OperatorAuthentication:PasswordHash`. Missing or malformed hash configuration fails at startup.
The credential is required in every environment, including Development. The existing antiforgery
and secure-cookie controls remain. Login attempts are limited per remote address per Web instance.
The authentication scheme is versioned so cookies issued by the former passwordless login cannot
authenticate after upgrade, even when the data-protection key ring is retained.
All old Web instances must be retired before reopening access to the upgraded deployment.
External identity-provider integration and multi-user onboarding remain separate work.

The Web executable's `--hash-operator-password` command generates an Identity V3 salted hash without
starting a host. It reads the password from the terminal without echo, or from standard input when
redirected, and never accepts it as a command-line argument. README documents configuration.

## Invariants before append

Domain commands validate required text, nested values, and monetary bounds before raising events.
Sales order totals remain USD, matching their existing total calculation. Rejection leaves no new
events. Historical event constructors and stored payload shapes remain unchanged; these guards do
not rewrite or repair previously persisted malformed events.

Both supported Sales paths converge at completion: Placed to Completed and Shipped to Completed.
Money allocation distributes signed minor-unit remainders, uses a wide ratio sum, and never awards
a remainder to a zero-weight recipient.

## Durable inventory SKU ownership

SKU uniqueness is enforced before InventoryCreated is appended, independently of lookup projection
lag. The PostgreSQL companion database holds a durable registry with unique tenant/SKU and
tenant/inventory-ID pairs. All event-store providers use the same registry through a domain port.

The first claim performs a transactionally serialized backfill from the authoritative event feed.
No modern creator can claim until that initialization commits. A historical conflict aborts the
backfill and fails explicitly instead of choosing a hidden second inventory owner. Existing
deployments must stop old writers before upgrading because those writers do not consult the registry.

An exact mapping is retryable. A committed claim is never released automatically after append
failure: the append may have succeeded even if its acknowledgement was lost. Retry the original
mapping. Abandoned claims require explicit operator reconciliation against event history; projection
rebuilds cannot reclaim them. The registry is durable write-side state, not a read-model cache.

## Fulfillment recovery and bounded persistence

Fulfillment persists per-line progress in bounded batches that fit DynamoDB's 33-event append limit.
Reservation outcomes already persisted are not dispatched again. In particular, a recorded failure
cannot turn into an untracked successful reservation on redelivery.

Cancellation intent is durable before compensation effects. Releases are recorded after their
commands succeed and saved in bounded batches. Retried commands retain their original idempotency
keys. A process manager reaches its terminal state only after every required effect succeeds.
Recovery of reservations whose outcomes were not saved is bounded in the same way. These changes
preserve existing event payloads and do not split the event store's atomic append operation.

Bounded event counts do not remove DynamoDB's payload-size limits. Deployments must still keep each
event, including the shipment's line list, within the selected store's supported item size.

## Projection rebuilding and DynamoDB dispatch

A rebuild holds the projection's existing PostgreSQL checkpoint lock while capturing its ceiling,
resetting the tenant, and replaying. Live handlers and concurrent rebuilds of that projection wait
on the same row. ADR 0041 describes the lock-loss checks and the remaining nontransactional rebuild
semantics: reads can observe partial results, and an interrupted rebuild requires a rerun.
Retire old AdminConsole instances before allowing rebuilds; those binaries do not acquire the lock.

DynamoDB replay yields each query page before fetching the next. Cancellation or early termination
therefore stops without buffering the remaining backlog. Shard discovery follows every
DescribeStream continuation, including pages containing closed parent shards, so later live shards
can still wake the dispatcher.

The compensation demo waits for its own cancelled order and payment-void timeline entry. It no
longer requires projections that do not handle payment events to reach the global payment-event
position, and cannot report compensation complete while it is still running.

## Cross-track reconciliation

The companion implementation changes require manuscript reconciliation for operator authentication,
SKU ownership, fulfillment save boundaries, the Sales completion transition, and rebuild coordination.
No manuscript files are changed by this implementation work.
