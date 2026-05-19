# 0008. Read-Side Store Interfaces Live With Their Bounded Context

## Status

Accepted (May 2026)

## Context

Session 0007 (commit `dbf2aba`) shipped `IOrderListStore` in `src/Projections/OrderList/` and added a `Projections` ProjectReference to `Application` so the `ListOrdersHandler` query handler could reach the port. The arrangement reversed the conventional hexagonal direction at the read seam: Application (the consumer) depended on Projections (which holds adapters). The Session 0007 close logged the layering as a deferred item.

Session 0008 (Phase 4) was about to ship three more aggregates with projections to follow in Phase 6. Each Phase 6 projection brings its own read-side store. Repeating the Session 0007 pattern four times would harden a layering the original commit's deferred entry called out for revisiting.

The first instinct was to promote `IOrderListStore` into `Domain.Abstractions`, which holds the codebase's other ports (`IEventStore`, `ICheckpointStore`, `IEventStoreRepository<T>`). Pre-flight walked the interface signature and surfaced a layering collision: `IOrderListStore` traffics in `OrderListRow`, which carries `Money` (Domain/SharedKernel) and `OrderStatus` (Domain/Sales). Putting the port in `Domain.Abstractions` would require either a `Domain.Abstractions` → `Domain` ProjectReference (creates a cycle, since `Domain` already references `Domain.Abstractions` for `IAggregateRoot` and `IDomainEvent`), promotion of `Money` and `OrderStatus` into `Domain.Abstractions` (stretches the "abstractions" concept to cover value objects), or replacement of the typed row shape with primitives (loses the deliberate type safety Session 0005 chose for the row).

The collision exposed a distinction the existing inhabitants of `Domain.Abstractions` share without it being named: all of them are either context-agnostic (pure cross-cutting infrastructure ports) or generic over `TAggregate`. None of them name a specific context's typed shapes. `IOrderListStore` is the first port that does.

## Decision

Read-side store interfaces that traffic in a specific bounded context's typed shapes (such as `IOrderListStore` returning `OrderListRow` with `OrderStatus` and `Money`) live at `Domain/{Context}/ReadModels/`, not in `Domain.Abstractions`. Context-agnostic ports (those generic over `TAggregate` or trafficking only in primitives or shared cross-cutting types) continue to live in `Domain.Abstractions`.

The rule, stated directly: `Domain.Abstractions` is for context-agnostic ports; `Domain/{Context}/` is for context-specific ports.

Concretely for the existing read-side surface: `IOrderListStore`, `IOrderListUnitOfWork`, and `OrderListRow` move to `src/Domain/Sales/ReadModels/`. Their namespace becomes `EventSourcingCqrs.Domain.Sales.ReadModels`. The `Projections` project keeps its adapter implementation (`OrderListProjection` plus the in-memory test double). The `Infrastructure.ReadModels.Postgres` project keeps its PostgreSQL adapter (`PostgresOrderListStore`, `PostgresOrderListUnitOfWork`). Both adapter projects reference `Domain`, which they already did.

`Application` drops its ProjectReference to `Projections`. The `ListOrdersHandler` query handler reaches `IOrderListStore` through `Application`'s existing `Domain` reference.

## Consequences

- The hexagonal direction at the read seam inverts cleanly. Application depends on Domain; Domain owns the port; Projections and Infrastructure adapter projects implement the port. The Session 0007 layering deferred item is closed.
- The row shape retains its typed Domain values. Consumers of `IOrderListStore.GetPageAsync` receive `OrderListRow` instances with `Money` and `OrderStatus` rather than primitives requiring reconstruction.
- `Domain.Abstractions` stays at zero ProjectReferences. The project is pure abstractions, as it was before this session.
- Phase 6 brings three more read-side stores (`IOrderDetailStore`, `ICustomerSummaryStore`, `IInventoryDashboardStore`). Each follows this ADR: stores trafficking in their context's typed shapes live at `Domain/{Context}/ReadModels/`. The precedent is set without being immediately stress-tested by Phase 4, which ships no new read-side stores.
- `CLAUDE.md`'s hexagonal-architecture amendment (introduced in commit `dbf2aba` as part of Session 0007) revises to describe the rule above.

## Trigger for revisiting

If a future read-side port emerges that traffics only in primitives or in types `Domain.Abstractions` can already see (cross-cutting types like `EventEnvelope`, `ICheckpointStore`'s positions), that port could legitimately live in `Domain.Abstractions`. The "context-agnostic vs context-specific" rule decides per-port; the location is not a project-wide convention to follow without checking the signature.

If a context-specific port grows a need to be consumed by a project that cannot or should not reference `Domain` directly (none is currently anticipated through Phase 14), the seam gets revisited at that point.
