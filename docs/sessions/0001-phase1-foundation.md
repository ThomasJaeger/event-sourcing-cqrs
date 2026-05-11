# Session 0001: Phase 1 Foundation

Date: 2026-05-11
Phase: 1 (Weeks 1-2)

Scope: in-memory event store, Order aggregate with seven commands and seven events, Given-When-Then test scaffolding from Chapter 16, CI green. No PostgreSQL adapter (that's the next work item, Phase 1, Weeks 3-4).

## Commits

1. `a6f585f` Add Domain.Abstractions ports and types
2. `c8d9e60` Add SharedKernel, in-memory event store, and test scaffolding
3. `342dbcb` Add Sales bounded context with Order aggregate and tests
4. `10d479e` Add Infrastructure.Tests with event-store and repository coverage

27 tests passing (22 in Domain.Tests, 5 in Infrastructure.Tests). CI green at every push.

## Cross-track flags

These items surfaced during the session and need future action in Track A (manuscript) or in a later Track C phase. Captured here with enough context that a future session can act on them without re-deriving the discovery.

1. **ISnapshotStore and IProjectionCheckpoint deferred.** PLAN.md Phase 1 lists both ports. Neither shipped in this work item because no concrete consumer exists yet: ISnapshotStore arrives in Phase 12 when snapshots are introduced, IProjectionCheckpoint in Weeks 5-6 with the first projection. Track A reconciliation: update PLAN.md Phase 1 wording so it reflects the actual delivery schedule and removes the abstractions-ahead-of-need risk PLAN.md itself warns about for Phase 11.

2. **EventMetadata.ForCommand factory deferred.** Ch 8 shows a `ForCommand(ICommand, ICommandContext)` static factory. Both parameter types are Phase 2 Application-layer concepts and do not exist yet. The factory will be added alongside ICommandContext when the Application command pipeline arrives in Phase 2. ForCausedEvent (instance method) shipped in this work item; it operates on EventMetadata alone and has no application-layer dependency.

3. **Ch 16 OrderPlaced example has wrong arity.** Ch 16's OrderTests calls `new OrderPlaced(OrderId, CustomerId, Now.AddHours(-1))` (three args). The Ch 9 case study calls `Raise(new OrderPlaced(Id, _customerId, Total, utcNow))` (four args including Money Total). Ch 9 is authoritative; the implementation matches the four-arg shape. Track A needs to update the Ch 16 example to include the Money Total argument.

4. **In-memory event store is teaching scaffolding plus a dual-purpose fixture, not a fourth peer.** CLAUDE.md and PLAN.md commit to three production peers (PostgreSQL, KurrentDB, DynamoDB). The in-memory store ships in Phase 1 so the Order aggregate is runnable before the PostgreSQL adapter arrives in Weeks 3-4, and stays useful as a fake event store for Application.Tests later. Phase 14 reconciliation: add a clarifying sentence to PLAN.md so a reader does not mistake the in-memory adapter for a production option.

5. **EventStoreRepository fills metadata with placeholders.** Until the Application command pipeline arrives in Phase 2, the repository sets `CorrelationId`, `CausationId`, and `ActorId` to `Guid.Empty` and `Source` to the literal `"Domain"`. Real metadata flows once ICommandContext exists and the command pipeline injects it. The repository's BuildEnvelopes method is the single point of change.

6. **Ch 16 AggregateTest<T>.Then helper is missing .RespectingRuntimeTypes().** As printed in Ch 16, the helper calls `emitted.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering())`. With FluentAssertions v7 this throws `InvalidOperationException: No members were found for comparison`, because both the subject (IReadOnlyList<IDomainEvent>) and the expectation (IDomainEvent[]) carry the empty IDomainEvent marker as their static element type, and FA's default comparison looks at declared types. The reference implementation adds `.RespectingRuntimeTypes()` to make the comparison see the concrete event records. Track A options: add the same call to the printed helper, or switch the example to a manual per-index loop comparison to remove the FA-version coupling from the book's central testing example.

## Notes for the next work item

PostgreSQL adapter is next (Phase 1, Weeks 3-4). The Domain.Abstractions surface and the IEventStore contract land in this work item unchanged. The repository's metadata stub (flag 5) gets replaced by real ICommandContext-driven metadata in Phase 2, not in the PostgreSQL work.
