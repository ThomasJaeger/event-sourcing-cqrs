# Track B Claude Project — Setup and Maintenance

This document describes the Track B Claude Project ("Event Sourcing & CQRS — Track B") on claude.ai. The Project is the code-planning track's persistent context layer: every new claude.ai conversation that opens against the Project starts with the files below in context, which removes the need to re-upload them per session.

The Project exists to support the three-track workflow described in `docs/session_setup.md`. Track A is book content, Track B is code planning in claude.ai, Track C is code execution via Claude Code. This file is Track B's own setup record.

## Why the Project exists

Without the Project, every Track B claude.ai session opens with no context. The conversation re-uploads `CLAUDE.md`, `docs/PLAN.md`, `docs/rules.txt`, the cumulative session logs, and any prior decisions worth carrying forward. Over a six-month build of twelve to fifteen sessions, the re-upload overhead compounds.

With the Project, those files are standing context. New conversations start with the cumulative state of the build already loaded, and the per-session entry point (`docs/session_setup.md`) becomes a short scoping message rather than a full re-context.

The discipline cost is real but small: the Project's files must be refreshed when the underlying repo files change, otherwise new conversations work against stale context.

## What lives in the Project

Files uploaded to the Project (all paths relative to the reference-implementation repo root unless noted):

- `CLAUDE.md` — repo-wide rules for Claude Code, referenced by every track
- `docs/PLAN.md` — the 14-phase long-arc plan
- `docs/rules.txt` — writing rules applied throughout every Track B conversation
- `docs/session_setup.md` — the current session's entry point, rotated per session
- `docs/sessions/*.md` — accumulated session logs and `-setup.md` companion files
- `docs/event-storming-mapping.md` — the sticky-note legend for the Order aggregate, extended over time
- `CLAUDE_CODE_PREAMBLE.md` — reference for the Track C kickoff pattern
- ADRs from `docs/adr/` (currently 0001 .NET 10, 0002 FluentAssertions v7, 0003 xUnit v2)
- From the book repo: the manuscript TOC docx, `HANDOFF.md` and `WHICH_SESSION.md` if present

Files deliberately not in the Project:

- The full manuscript. ~1MB of text, ~250-300k tokens. Eats too much context for every conversation that opens against the Project. Upload per-conversation when a session needs specific chapters.
- Track C working state (trading bot, LTM consulting, anything outside the book and reference-implementation repos).
- Personal context from the wider userMemories that isn't book-and-implementation scoped.

## Project custom instructions

The Project's custom instructions field carries this paragraph, which primes every new conversation:

> This Project is Track B for the *Event Sourcing & CQRS* book's reference implementation. Track B is the code-planning track that runs in claude.ai. Each conversation works from `docs/session_setup.md` for the current session's scope and ends with a Claude Code instruction that Track C executes against the public repo. Track A (book content) and Track C (Claude Code in the terminal) are separate; cross-track work is captured as flags in `docs/sessions/NNNN-<description>.md` and routed by the human at session boundaries. Writing rules in `docs/rules.txt` apply throughout. The code-first vs manuscript routing rule applies: when implementation and manuscript disagree, implementation wins and a Track A flag is logged.

Edit the instructions if the workflow shifts. The Project remembers the current text; refresh discipline below doesn't touch this field.

## Refresh discipline

Two paths, pick whichever sticks. Both keep the Project's files aligned with the repo's truth.

**Per-session refresh.** At the end of each Track B session, before closing the conversation: upload the new `docs/sessions/NNNN-*.md` log and `-setup.md` files that landed during the session, upload the next session's `docs/session_setup.md` (which replaces the current one), and remove the now-stale prior `session_setup.md` from the Project so only the current entry point is present.

**Lazy refresh.** Update Project files only when the underlying file actually changes. The session logs accumulate without re-upload until close-of-session; manuscript TOC, CLAUDE.md, PLAN.md, and rules.txt get refreshed when edited, not on a schedule.

The per-session pattern is tighter and protects against drift. The lazy pattern is lower-friction and works when the repo files don't change much between sessions. The choice is the human's; the Project doesn't care.

## What never goes in the Project

Three categories that look like they belong and don't.

**Track A files beyond the TOC.** The Project is Track B only. Putting the manuscript or book-repo working files in this Project blurs the track boundaries and defeats the three-track design. Track A has its own discipline; let it stay separate.

**Claude Code's working state.** Track C reads context from `CLAUDE.md` and the repo itself, not from a claude.ai Project. Mixing those concerns is friction without payoff.

**The session logs as a substitute.** The logs are the durable record of decisions and live in the repo. The Project is the convenience layer that gives new conversations a head start. If the Project ever feels like it's replacing the logs, something has drifted; restore the logs as the source of truth and treat the Project as the read-only convenience.

## Verification

A new conversation in the Project should respond to "What's the current `session_setup.md` asking for?" with an accurate framing of the current session's open questions, leans, and locked decisions. If the response is generic or asks for re-upload, the Project's files didn't land cleanly or the entry-point file is missing. Re-upload `docs/session_setup.md` first and test again.

The Project's first successful verification was after Session 0002 closed, against the Session 0003 entry-point file. The response correctly named the four adapter decisions (serializer, type-name resolution, transaction shape, unique-violation mapping) and folded in Chapter 11's upcasting context unprompted, which confirmed the manuscript TOC was reading correctly.

## Reconstructing the Project from scratch

If the Project is lost or you want to seed a parallel one (e.g., for a second contributor), the recipe is:

1. Create a new Project in claude.ai. Name format: "Event Sourcing & CQRS — Track B" or equivalent track-scoped name.
2. Paste the custom-instructions paragraph from the section above.
3. Upload every file in the "What lives in the Project" list. The repo paths are authoritative; pull current versions from the repo, not from any prior Project's snapshot.
4. Verify with the test prompt from the previous section.
5. Close the verification conversation.

The whole recipe takes under ten minutes if the repo is local. The Project carries no information that isn't in the repo or in the human's head; reconstruction is straightforward.
