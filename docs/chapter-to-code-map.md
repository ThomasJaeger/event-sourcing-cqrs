# Chapter-to-code map

Where the code for each chapter lives. The chapter list is taken from the manuscript's generated table of contents, which is derived from the book's own build rather than from anything in this repository; regenerate it there and re-check this table against it.

Pointers are to files and folders. They are not class names, because a rename would leave this document confidently wrong, and a map that has quietly drifted is worse than no map at all.

## Chapters with no code counterpart

Chapters 1 through 6 have no code here, and that is the intent rather than a gap. They cover why software projects fail people, the architecture styles that preceded this one, the benefits and the counter-arguments, when not to use event sourcing at all, and Event Storming. Event Storming produces a shared understanding and a set of sticky notes; what it produced for this system is recorded in `docs/event-storming-mapping.md`, which is the closest thing to an artifact those six chapters have.

Chapter 14, on analysis and machine learning, and Chapter 15, on task-based user interfaces, are partial cases. Chapter 14 argues that an event log is a better analytics substrate than a row of current state, and the reference implementation ships the log rather than the analytics built on it. Chapter 15's argument shows up in the shape of the Web host's command surface rather than in a project of its own.

A reader who finds no code for a chapter has not found a hole. The implementation demonstrates the patterns of Part 3; the chapters outside it do other work.

## The map

| Chapter | Where the code is |
| --- | --- |
| 7, Domain-Driven Design | `src/Domain/`, one folder per bounded context; `docs/architecture/cross-context-vocabulary.md` is the worked example of the chapter's context-mapping section |
| 8, Event Sourcing | `src/Domain.Abstractions/` for the ports; `src/Infrastructure/EventStore.Postgres/` and its three peers for the adapters; `src/Infrastructure/Outbox/` |
| 9, Aggregates in Practice | `src/Domain/Sales/`, `src/Domain/Fulfillment/`, `src/Domain/Billing/`, `src/Domain/Access/`; `tests/Domain.Tests/` |
| 10, Workflows and Sagas | `src/ProcessManagers/`; `tests/ProcessManagers.Tests/` |
| 11, Event Versioning | `src/Infrastructure/Versioning/` |
| 12, Snapshots | `src/Application/` for the snapshotting repository; `src/Infrastructure/EventStore.Postgres/` and `src/Infrastructure/EventStore.SqlServer/` for the two native snapshot stores. The other two engines compose the PostgreSQL store against the read-model database rather than shipping their own; ADR 0051 records why |
| 13, CQRS | `src/Projections/`; `src/Infrastructure/ReadModels.Postgres/`; `src/Application/` for the command and query buses |
| 16, Testing | `tests/`, thirteen projects; `tests/EventStore.ContractTests/` for the engine-agnostic suite |
| 17, Production Support | `src/Hosts/AdminConsole/`; `src/Projections/Infrastructure/` for the lag reader and the replayer |
| 18, Migrating Legacy Systems | `src/Migration/`, with its own README |

## What the chapters teach and this implementation does not build

Two of Chapter 8's sections are catalogues of patterns rather than descriptions of this system. Its uniqueness section works through six techniques and this implementation uses none of them, having no uniqueness rule that spans aggregates. Its sensitive-data section works through crypto-shredding, and no event here carries personal data to shred. Chapter 17 describes five operator tools and four ship, in `src/Hosts/AdminConsole/`.

None of that is drift. A chapter that catalogues six approaches is not claiming this system implements six, and a reference implementation that built every pattern its book describes would be a worse teaching artifact for the sprawl.
