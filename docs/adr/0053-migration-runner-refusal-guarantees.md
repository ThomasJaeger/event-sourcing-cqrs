# 0053. Migration Runner Refusal Guarantees

## Status

Accepted (August 2026)

## Context

Two relational adapters ship their own migration runner, self-contained per ADR 0004:
`MigrationRunner` in `src/Infrastructure/Migrations.Postgres/` and `SqlServerMigrationRunner`
in `src/Infrastructure/EventStore.SqlServer/`. They are structurally twins that share no code,
because the lock primitive, the tracking-table existence probe, and the batch handling are all
engine-specific.

Between them they enforce three refusals that no ADR records. Each one stops a run that would
otherwise proceed and produce a schema nobody asked for. Each one is duplicated across two
independently-changeable runners, so the two copies can drift apart without a compiler or a
test noticing. And each was introduced as an implementation detail rather than as a decision,
the same way ADR 0024 found scope-per-dispatch living undocumented inside `CommandBus`.

The third refusal, ordering, landed in Phase 17 and prompted this record. Writing an ADR for
the newest guarantee alone would have documented the instance and left its two peers
undefended, so all three go here.

## Decisions

**A migration runner refuses rather than proceeds.** Every guarantee below is a refusal. None
of them repairs, skips, warns, or falls back. An operator gets an exception naming the offending
migration and decides what to do, because every alternative disposition silently produces a
database whose schema does not match its migration history.

### Checksum verification

The embedded migration set is checksummed with SHA-256 over the raw resource bytes, and a
version already recorded in `event_store.schema_migrations` must match the checksum stored
beside it. A mismatch throws before any DDL runs: `MigrationChecksumMismatchException` from
`MigrationRunner.cs:156`, `SqlServerMigrationChecksumMismatchException` from
`SqlServerMigrationRunner.cs:175`.

It prevents an edited migration from being silently accepted. A file changed after it was
applied means the schema on disk and the schema the file describes have diverged, and every
database that applied the old bytes now differs from every database that would apply the new
ones. The exception names the version, the name, the stored checksum, and the computed one, so
the operator can tell which direction the edit went.

### Duplicate-version rejection

Two embedded files claiming the same four-digit version fail at load time, before a connection
is opened: `MigrationRunner.cs:265`, `SqlServerMigrationRunner.cs:319`.

It prevents an ambiguous set. Without it, whichever file sorted second would fail on a
primary-key violation inserting into the tracking table, after the first had already applied
its DDL, leaving a partially migrated database and an error naming a constraint rather than the
two files that collided. Rejecting at load turns a storage error into a set error and names
both filenames.

### Ordering: pending must follow applied

When at least one migration has been applied, a pending migration numbered below the highest
applied version is refused, naming the offending version and the highest applied one:
`MigrationOutOfOrderException` from `MigrationRunner.cs:186`,
`SqlServerMigrationOutOfOrderException` from `SqlServerMigrationRunner.cs:205`.

The mechanism this guards is specific. Pending selection is a per-version set difference,
`migrations.Where(m => !applied.ContainsKey(m.Version))`, not a watermark comparison, so a file
numbered below what is applied is selected exactly like any other file that has not run. The
embedded set is then sorted ascending before the apply loop, so that back-filled file runs
*first*, against a schema every higher-numbered migration has already shaped. A migration
written to alter a table that migration 0022 created, but numbered 0005, would run before the
table existed on a fresh database and after it existed on an established one. That is a silent
data-corruption path, and it is the reason this guarantee is a decision rather than a
convenience.

The check lives inside the composite guard both branches of `RunPendingAsync` call, so the
dry-run branch and the apply branch inherit it from one site and neither can pick up one
refusal and miss another. Checksums run first, so a set that is both tampered and back-filled
reports the tampering, which is the narrower and older fault.

An empty applied set has no highest version, so the check does not fire and a first run against
a fresh database proceeds.

## Consequences

- **Enforcement is asymmetric, and this ADR records that rather than implying otherwise.**
  Ordering is pinned on both runners, four facts each:
  `PostgresMigrationRunnerOrderingTests` and `SqlServerMigrationRunnerOrderingTests`, each
  carrying `A_migration_numbered_below_the_highest_applied_version_is_refused_and_nothing_is_applied`,
  `The_refusal_names_the_offending_version_and_the_highest_applied_version`,
  `A_dry_run_surfaces_the_out_of_order_migration_rather_than_listing_it_as_pending`, and
  `A_pending_migration_above_the_highest_applied_version_applies_normally`. Checksum
  verification is pinned on PostgreSQL alone, by
  `PostgresMigrationRunnerTests.Checksum_mismatch_throws_with_migration_identity_in_message`;
  no fact asserts `SqlServerMigrationChecksumMismatchException`. Duplicate-version rejection is
  pinned on neither. Two of the three guarantees therefore rest on code review on at least one
  runner, which is the exposure this ADR exists to make visible.

- The refusals cost a class of operation that looks legitimate. Renumbering a migration to fix
  a merge, editing a typo in an applied file, and back-filling a migration into a gap are all
  things an operator may reasonably attempt, and all three now fail loudly. The remedy in every
  case is a new migration with a higher number, never an edit to an applied one.

- Two copies exist by ADR 0004's posture, and nothing structurally holds them equal. A change
  to one runner's refusal set has to be made twice by hand. ADR 0004's revisit trigger, three
  adapters touching identical code, does not fire here: KurrentDB and DynamoDB have no
  migration runner, so the count stays at two.

- The composite guard placement means adding a fourth refusal is one edit per runner rather
  than two, because both `RunPendingAsync` branches call one method.

## Rejected alternatives

**Contiguity of the applied set.** Asserting that applied versions form an unbroken sequence
was considered in Phase 17 and declined. A gap in the applied set is a legitimate state: the
Postgres set and the SQL Server set are numbered independently, a migration can be withdrawn
before release, and the test fixtures for the ordering facts deliberately apply a sparse set.
Refusing on a gap would break a state the system produces on purpose.

**Hole detection in the embedded set.** Asserting that the embedded files form an unbroken
sequence was likewise considered and declined. It would fire during ordinary development, when
a migration is being authored and its predecessor has not merged yet, and it protects against
nothing: a hole in the embedded set is a numbering choice, whereas a hole that a later file
fills is what the ordering guarantee already refuses at the point it would do damage.

**An opt-out or override flag.** Not offered. A flag that lets an operator proceed past any of
the three refusals converts a guarantee into a default, and the failure it exists to prevent is
one an operator under pressure would override.

## Trigger for revisiting

- A third relational adapter arrives. At three runners the duplication reaches ADR 0004's
  stated threshold and the refusal set becomes a candidate for factoring into the
  engine-agnostic runner project, which already exists for PostgreSQL.

- A deployment model appears where migrations are applied out of band, by a schema-management
  tool rather than by a host at startup. The refusals assume the runner owns the tracking table
  and is the only writer to it.

- A legitimate need to renumber an applied migration surfaces, most plausibly during a
  branch-merge workflow that the current single-trunk model does not exercise. The remedy would
  be a documented renumbering procedure that rewrites the tracking table alongside the files,
  not a relaxation of the checksum or ordering refusals.

- Duplicate-version rejection or SQL Server checksum verification acquires a fact. That would
  close the asymmetry above and is the smallest change that would make this ADR's Consequences
  section stale.
