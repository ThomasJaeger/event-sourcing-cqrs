# 0001. .NET 10 LTS over .NET 8 LTS

## Status

Accepted (2026-04). Manuscript reconciliation complete (2026-04-25, see Supplement).

## Context

The manuscript's Part 4 specifies .NET 8 LTS as the framework and C# 12 as the language version. That decision was sound when the manuscript was drafted.

This decision is being made in April 2026, while the reference implementation is being built. At this point, both LTS releases are viable choices:

- .NET 8 LTS shipped November 2023. Support ends November 2026.
- .NET 10 LTS shipped November 2025. Support ends November 2028.

The book is in submission preparation in 2026 and has a commercial life of several years after publication. A reference implementation pinned to .NET 8 would ship against an LTS that exits support roughly seven months after a typical book's release date. .NET 10 was selected for the longer support window, which aligns the implementation's lifespan with the book's commercial life.

C# 14, which ships with .NET 10, introduces extension members, partial constructors, and field-backed properties. Each of these can make the value object and aggregate code in this repository read more directly. The book's voice favors clarity over cleverness, so C# 14 features will appear where they make patterns clearer and not where they would be reached for purely to use them.

The deviation from the manuscript is acknowledged. A manuscript reconciliation edit is required to update Part 4's framework and language references and the support-window claim accordingly. (See Supplement for completion record.)

## Decision

Pin the reference implementation to .NET 10 LTS via `global.json` with `rollForward: latestFeature`. Use C# 14 features where they make patterns clearer.

Edit Part 4 of the manuscript to:

- Replace ".NET 8 LTS" with ".NET 10 LTS".
- Replace "C# 12" with "C# 14".
- Update the support-window claim to "supported through November 2028".

## Consequences

- Contributors and readers must install the .NET 10 SDK to build the repo. This is documented in the README and in the "Stack and constraints" section of CLAUDE.md.
- The build fails loudly if a different SDK is in use, rather than rolling forward silently. This matches the rule in CLAUDE.md: "Do not target a .NET version other than 10".
- C# 14 features are available where they help. Reaching for them purely to use them is not. The book teaches concrete patterns; the language must serve the patterns.
- A manuscript edit is required and was completed on 2026-04-25 ahead of Phase 14. PLAN.md's Phase 14 done-when criteria for this item can be marked complete.
- If a future LTS release ships before publication and offers a meaningfully longer support window, this ADR is reopened.

## Supplement: 2026-04-25 — Manuscript reconciliation complete

The manuscript edit specified in the Decision section is complete. The book repo (github.com/ThomasJaeger/event-sourcing-cqrs-book) was updated in a Claude.ai book session on 2026-04-25.

Changes applied:

- `content_part4.js` Tech Stack section: rewritten to commit to .NET 10 LTS with November 2028 support window. Added a forward-compatibility sentence noting that teams on .NET 8 or .NET 9 can adapt the code with minor adjustments.
- `content_part5.js` Reference Implementation section: ".NET 8 / C# 12 codebase" updated to ".NET 10 / C# 14 codebase".
- `diagrams/fig_p4_01_solution_structure.svg` caption: ".NET 8 / C# 12" updated to ".NET 10 / C# 14".

Manuscript was rebuilt and remains stable at 439 pages. All auxiliary artifacts (sample, TOC, chapter summaries, competitive grid, executive deck, implementation pack) were rebuilt and shipped from the updated source.

Phase 14 of `docs/PLAN.md` has one fewer item as a result. The "manuscript edit" work item under Phase 14 can be marked complete or removed.

No further book-side work is owed by this ADR.
