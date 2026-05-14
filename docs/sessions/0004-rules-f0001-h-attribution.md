# Session 0004 docs arc: F-0001-H attribution convention

Date: 2026-05-14
Type: docs-only rules codification (not an execution session)

## Naming convention

This log carries the `0004-` prefix rather than a new sequence number. The convention: when a docs-only commit ships in a docs-reconciliation arc that branches off an execution session, it carries that session's number as a prefix rather than consuming a new one. The `0004-` prefix associates this work with the Session 0004 arc that surfaced F-0001-H (commits c82d568, 55cefbf), and keeps `0005` reserved for the first-projection execution session. The convention applies forward: future docs-only logs that trace to a specific execution session carry that session's prefix, not strict next-in-sequence.

## The edit

Commit `cd78c30` ("Codify F-0001-H attribution convention in CLAUDE.md") inserts a `## Attribution convention` section in this repo's CLAUDE.md, between `## Source-of-truth hierarchy` and `## What "good" looks like in this repository`. The placement keeps the two symmetric cross-repo rules adjacent in the document's top framing cluster.

## The rule wording

> Commit messages, session log content, and doc-edit prose in this repository carry no Co-Authored-By or other Claude / Anthropic attribution. The AI-assistance pattern is internal to the working model, not a public artifact. Historical commits carrying attribution stay as-is; enforcement is forward-only.
>
> This rule applies symmetrically across both Claude Code instances and the Claude.ai planner. Same rule appears in the book repo's CLAUDE.md and HANDOFF.md.

## Context

F-0001-H was surfaced by the F-0001-A + F-0001-E session (commit c82d568, addendum 55cefbf): no rules document in either repo codified the no-attribution convention, though that session executed under it by direction. This commit closes the rules-doc gap on the code-repo side. The book repo's CLAUDE.md and HANDOFF.md ship the same rule in a parallel book-repo session, following the symmetric-rule pattern the source-of-truth rule established in commit f9d1a81.
