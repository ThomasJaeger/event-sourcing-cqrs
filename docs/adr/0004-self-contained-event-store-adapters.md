# 0004. Self-Contained Event Store Adapters

## Status

Accepted (May 2026)

## Context

The reference implementation ships four first-class event store peers: PostgreSQL (hand-rolled), SQL Server (hand-rolled), KurrentDB, and DynamoDB. The two relational peers, PostgreSQL and SQL Server, both implement the outbox pattern as the manuscript describes in Chapter 8. The structural similarity between them raises a real architectural question: should row construction, the outbox table abstraction, optimistic-concurrency translation, and other relational mechanics be factored into a shared layer that both adapters consume, or should each adapter be self-contained with the apparent duplication accepted as the cost of independent evolvability?

The two relational adapters differ in engine-specific particulars even though their high-level shape is identical: different unique-constraint error codes, different JSON storage types, different filtered and partial index syntax, different transaction APIs. The high-level pattern is the same; the engine-specific code is different in nearly every detail.

CLAUDE.md's principle "do not generate generic abstractions ahead of need" sets the default toward duplication over premature abstraction. With two known first-class relational adapters, the duplication is concrete and reasonable in scope.

## Decision

Event store adapters are self-contained. Each adapter owns its row construction, INSERT SQL, concurrency-violation translation, and outbox mechanics inside its own project. PostgreSQL's mechanics live in `Infrastructure/EventStore.Postgres`. SQL Server's mechanics live in `Infrastructure/EventStore.SqlServer`. No shared `Infrastructure/EventStore.Relational` layer is introduced. The two relational adapters duplicate the structural pattern; engine-specific code differs in each.

This applies symmetrically to KurrentDB and DynamoDB adapters, which use native dispatch mechanisms (catch-up subscriptions and Streams) instead of the outbox. Each remains self-contained within its own project, free of cross-adapter coupling.

## Consequences

- Each adapter reads end-to-end as a teaching artifact. A reader studying the outbox pattern sees the full pattern in one place, with the engine-specific SQL inline, without following references into a shared abstraction. The reference implementation's pedagogical purpose is served by this transparency.
- Adapters evolve independently. If one relational adapter's outbox grows a new operational column that the other does not need, the change is contained inside the adapter that needs it. Cross-adapter coordination is unnecessary for changes that do not actually cross adapter boundaries.
- Adapters can be removed cleanly. Deleting an adapter is a folder deletion, not a refactor of a shared component.
- The cost is accepted duplication. Approximately 30 to 50 lines of row-construction and translation code repeat across the two relational adapters. This is the explicit cost of the self-contained principle.

## Trigger for revisiting

If a future change requires touching identical code in three or more adapters, the session making that change evaluates whether to factor the shared concern at that point. Two-adapter duplication is fine and expected under this ADR. Three-adapter duplication is the threshold for reconsideration. The reconsideration is not automatic refactoring; it is an evaluation step where the alternatives are weighed in light of the specific change driving the question.

KurrentDB and DynamoDB do not have outboxes, so the relational outbox abstraction would only become a three-consumer question if a third relational adapter is added to the implementation. No such addition is currently planned.

## Amendment (August 2026)

The pedagogical-transparency justification is replaced. ADR 0025 established that production
quality wins on every axis and named this ADR as one whose grounds it reframes, deferring the
reframe to the next change touching this file. Four adapters have landed since. This amendment
discharges that deferral. The decision is unchanged, the status is unchanged, and the
self-contained rule stays exactly as the Decision section states it.

The first Consequences bullet argued the rule on teaching grounds, that each adapter reads
end-to-end as a teaching artifact and that the reference implementation's pedagogical purpose
is served by the transparency. That argument is withdrawn as the rule's basis. It was never
the reason a production team would take this shape.

The production grounds are what the four adapters show. Their engine-specific mechanics are
not variations on one algorithm, they are different algorithms reaching the same contract.
Optimistic concurrency alone translates from four unrelated failure signals: a SQLSTATE on
PostgreSQL, error numbers 2627 and 2601 on SQL Server, a `WrongExpectedVersionException` from
the KurrentDB client, and a conditional-write rejection on DynamoDB. Two of the four carry an
outbox and two dispatch natively, through catch-up subscriptions and Streams. A shared layer
spanning these would hold the union of four engines' failure modes behind one signature, and
every adapter would pay for the three engines it is not.

The duplication cost is measured rather than estimated, and it is higher than this ADR said.
The Consequences section put it at 30 to 50 lines. ADR 0045 corrected that to roughly 234 for
the SQL Server adapter's dedicated seam files, noting the original count omitted the type
registries and the JSON seam. ADR 0048 then corrected the scope of that figure and collapsed
the versioning seam where the duplication had become shared in fact. The rule survived both
corrections because the revisit trigger did its work: the seam that reached three consumers
was factored, and the engine-specific mechanics that did not were left alone. A rule that
absorbs a fivefold cost correction without changing is resting on something other than the
cost estimate.

The trigger below is unchanged and remains the mechanism. Two-adapter duplication is expected;
three adapters touching identical code is the point at which factoring is evaluated. That
threshold, not pedagogical transparency, is what keeps this decision honest.
