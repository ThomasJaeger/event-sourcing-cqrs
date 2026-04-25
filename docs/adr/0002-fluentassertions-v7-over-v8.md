# 0002. FluentAssertions v7 over v8

## Status

Accepted (April 2026)

## Context

FluentAssertions changed its license at v8.0, released in January 2025 in partnership with Xceed Software. v7.x remains under Apache 2.0 and continues to receive patch fixes. v8 onwards uses the Xceed Community License Agreement, which restricts free use to non-commercial purposes and requires a paid commercial license for any other use. Pricing currently ranges from $14.95 to $130 per developer per year depending on support tier.

This repository is MIT-licensed. It exists for readers of the book to clone, run, study, and adapt for their own systems, including commercial systems. A test dependency that requires its own commercial license for commercial use would either restrict reader use or push readers to pay a per-developer license to follow along with the book. Neither is acceptable for a teaching artifact.

The eleven test patterns in Chapter 16 use assertions extensively. The patterns themselves are framework-agnostic; the choice of assertion library affects only the readability of the test code, not the patterns.

## Decision

Pin FluentAssertions to v7.2.x (currently 7.2.2) in `Directory.Packages.props`. Apply uniformly across all test projects. Track patch updates within the 7.x line. Do not move to v8 or above.

## Consequences

- All assertion idioms used in tests must work on v7. v8-only APIs are off the table.
- The library is effectively frozen at v7 for the life of this repo. Maintenance fixes still arrive but no new features will.
- Removal trigger: a permissive (Apache 2.0, MIT, or BSD-style) assertion library that demonstrably reads better than v7 for the patterns from Chapter 16, or a renewed permissive license on the FluentAssertions line. Candidates worth tracking, in priority order: AwesomeAssertions (community fork of FluentAssertions pre-v8 under Apache 2.0, drop-in replacement for v7 functionality, lowest migration cost), Shouldly, Verify, and plain xUnit `Assert.*` for cases that do not benefit from a fluent style.
- If the trigger fires, migration is a single phase across all test projects, not phase-by-phase.
