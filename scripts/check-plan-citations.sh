#!/usr/bin/env bash
#
# check-plan-citations.sh
#
# Fails if anything in the repository cites a document by line number.
#
# Why
#   A line number pointing into another document goes stale the moment anything
#   above it is edited, and it goes stale silently: the citation still resolves
#   and now names the wrong line. Documents are cited by content and never by line
#   number, and an edit that shifts lines in a cited document owns the citations
#   into it. This script is the structural half of that rule: the obligation is
#   checkable rather than remembered.
#
#   PLAN.md earned the check. Before Phase 17's Arc C it carried 60 line-number
#   citations across 38 files, so a single inserted line above them invalidated
#   all 60 at once, and nothing on disk would have said so.
#
#   The three outcomes stay distinct. git grep exits 0 when it found matches, 1 when
#   it found none, and above that when it did not answer at all, so folding the last
#   two together reports a broken check as a clean one. An unreadable gate is
#   unknown, never presumed, and never reported as green or red on a guess.
#   A checker gets the same treatment, and git's own diagnostics stay unsuppressed so
#   the unknown can say what went wrong.
#
# Scope
#   Every markdown target, plus the `ADR NNNN:123` form, which names a document by
#   its title rather than its path and which the earlier PLAN.md-only pattern could
#   not see. Three of the four citations this widening had to clear used that form.
#   Source files are out of scope. A line number into a .cs file goes stale the same
#   way, but the working-pattern rule this script enforces is about documents, and
#   several ADRs cite adapter code by line deliberately.
#   This file is excluded too, and the exclusion is the file rather than any line in
#   it. A checker that documents the pattern it matches carries example citations in
#   its own comments, so it reports itself; exempting the one line that trips it today
#   leaves the next example someone writes to trip it again. Nothing else leaves scope,
#   and the price is that a real citation written here would be missed.
#
# Exit codes
#   0  Clean. git answered and found no line-number citations into any document.
#   1  Violations. git answered and found citations; each is printed with its location.
#   2  Unknown. git did not answer, so the check did not run. Whatever git reported is
#      on stderr. Callers that treat every non-zero exit as a failed check still stop,
#      and callers that care can tell a failed check from a broken one.
#
# Usage
#   ./scripts/check-plan-citations.sh

set -euo pipefail

# The substitution runs before the cd, so a failed rev-parse yields an empty string
# and `cd ""` succeeds without moving. set -e does not catch it, because cd's own
# status is 0, and the check would then run against whatever tree the caller stood
# in. The status is captured, the empty result is rejected, and the cd is guarded.
# Every path that cannot establish the root exits 2, the code this script already
# reserves for "the check did not run", rather than reporting a wrong tree as clean.
if ! root=$(git rev-parse --show-toplevel); then
    printf '%s\n' "Could not find the repository root: git rev-parse failed." >&2
    exit 2
fi
if [ -z "$root" ]; then
    printf '%s\n' "Could not find the repository root: git rev-parse returned nothing." >&2
    exit 2
fi
if ! cd "$root"; then
    printf '%s\n' "Could not enter the repository root at $root." >&2
    exit 2
fi

# Matches PLAN.md:123 and docs/PLAN.md:123. A bare :123 continuing an earlier
# citation on the same line rides along with its explicit anchor, so anchoring on
# the explicit form finds every offending line.
# Those two example citations are why the second exclusion names this file.
# The status is captured rather than discarded. An if-condition is exempt from set -e,
# so the capture needs no set +e around it and leaves no window where a failure passes.
if hits=$(git grep -nE '[A-Za-z0-9_./-]+\.md:[0-9]+|ADR [0-9]{4}:[0-9]+' \
    -- . ':!scripts/check-plan-citations.sh'); then
    status=0
else
    status=$?
fi

if [ "$status" -gt 1 ]; then
    printf '%s\n' "Citations could not be checked: git grep exited $status." >&2
    printf '%s\n' "Any message git produced is above. This is unknown, not clean." >&2
    exit 2
fi

if [ "$status" -eq 0 ]; then
    printf '%s\n' "A document is cited by line number. Cite it by content instead:" >&2
    printf '%s\n' "  name the section or quote the sentence, as in \"PLAN.md's scope section\"" >&2
    printf '%s\n' "  or \"ADR 0004's Trigger for revisiting section\"." >&2
    printf '\n%s\n' "$hits" >&2
    exit 1
fi

printf '%s\n' "No line-number citations into any document."
