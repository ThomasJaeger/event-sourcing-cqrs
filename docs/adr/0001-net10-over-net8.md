# 0001. .NET 10 LTS over .NET 8 LTS

## Status

Accepted (April 2026). Manuscript reconciliation completed (April 2026). Closed.

## Context

The manuscript's Part 4 originally specified .NET 8 LTS as the framework and C# 12 as the language version. That decision was sound when the manuscript was drafted.

This decision was made in April 2026, while the reference implementation was being built. At this point, both LTS releases were viable choices:

- .NET 8 LTS shipped November 2023. Support ends November 2026.
- .NET 10 LTS shipped November 2025. Support ends November 2028.

The book is in submission preparation in 2026 and has a commercial life of several years after publication. A reference implementation pinned to .NET 8 would ship against an LTS that exits support roughly seven months after a typical book's release date. .NET 10 was selected for the longer support window, which aligns the implementation's lifespan with the book's commercial life.

C# 14, which ships with .NET 10, introduces extension members, partial constructors, and field-backed properties. Each of these can make the value object and aggregate code in this repository read more directly. The book's voice favors clarity over cleverness, so C# 14 features will appear where they make patterns clearer and not where they would be reached for purely to use them.

## Decision

Pin the reference implementation to .NET 10 LTS via `global.json` with `rollForward: latestFeature`. Use C# 14 features where they make patterns clearer.

Update the manuscript Track A to reflect .NET 10 / C# 14:

- Replace ".NET 8 LTS" with ".NET 10 LTS" in Part 4 Technology Choices.
- Replace "C# 12" with "C# 14" in Part 4 Technology Choices and Part 5 Resources.
- Update the support-window claim to "supported through November 2028".
- Update cross-references in other chapters where the manuscript names the framework or language version.

## Consequences

- Contributors and readers must install the .NET 10 SDK to build the repo. This is documented in the README and in the "Stack and constraints" section of CLAUDE.md.
- The build fails loudly if a different SDK is in use, rather than rolling forward silently. This matches the rule in CLAUDE.md: "Do not target a .NET version other than 10".
- C# 14 features are available where they help. Reaching for them purely to use them is not. The book teaches concrete patterns; the language must serve the patterns.
- If a future LTS release ships before publication and offers a meaningfully longer support window, this ADR is reopened.

## Reconciliation

The Track A manuscript update was completed in April 2026, ahead of Phase 14. Part 4 Technology Choices, Part 5 Resources, and the cross-references in other chapters were updated in a single Track A pass. The manuscript and the implementation now agree on .NET 10 / C# 14.

The Phase 14 reconciliation step in PLAN.md no longer carries this item. Other reconciliation work that accumulates during Phases 2-13 is still owed in Phase 14.
