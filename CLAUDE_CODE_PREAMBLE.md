# Claude Code Preamble

This file captures the working pattern for Claude Code sessions in this repository. Read it once at session start, alongside CLAUDE.md, docs/PLAN.md, and docs/ai-writing-style-source.txt.

## Standard opening

Every session starts with the standard opening from the human:

> Please re-read CLAUDE.md, docs/PLAN.md, and docs/ai-writing-style-source.txt. Then summarize back to me: (1) work items already complete based on what is in the repo, (2) work items remaining for the current phase. After I confirm, we will start on the next item.

Respond by reading the files, listing what is complete based on git state and the repo's actual contents, and listing what remains for the current phase per PLAN.md (or the active reconciled plan, if one supersedes PLAN.md).

## Working pattern

- **Propose before writing.** For any file creation or significant edit, describe what you plan to write before writing it. Wait for confirmation.
- **Stop and ask before deviating.** If the agreed plan turns out to be incorrect, flawed, or incomplete during execution, stop and surface the issue. Do not silently adjust scope.
- **Log cross-track flags as you go.** When a code-level discovery affects the manuscript (Track A) or the planning narrative (Track B), capture it in the session log under "Cross-track flags" with enough discovery context that the affected track can act on it without re-deriving.
- **Commit per logical unit, not per phase.** Multiple commits in a session is the norm. Match the existing commit-message convention.
- **Build green between steps.** Run `dotnet build` after each substantive change. Surface failures immediately.
- **CI green per push.** If a push lands red CI, the next action is fixing CI, not advancing the work item.

## Session boundaries

End a session deliberately when work pauses. Start a fresh session when work resumes. Long idle conversations consume context without producing work.

## When in doubt

The four canonical documents (this file, CLAUDE.md, docs/PLAN.md, docs/ai-writing-style-source.txt) are the source of truth for working patterns and scope. If a request from the human conflicts with these documents, surface the conflict before acting.