#!/usr/bin/env bash
#
# check-plan-citations.sh
#
# Fails if anything in the repository cites docs/PLAN.md by line number.
#
# Why
#   A line number pointing into another document goes stale the moment anything
#   above it is edited, and it goes stale silently: the citation still resolves
#   and now names the wrong line. CLAUDE_CODE_PREAMBLE.md's working pattern rules
#   that documents are cited by content, and that an edit shifting lines in a
#   cited document owns the citations into it. This script is the structural half
#   of that rule: the obligation is checkable rather than remembered.
#
#   PLAN.md earned the check. Before Phase 17's Arc C it carried 60 line-number
#   citations across 38 files, so a single inserted line above them invalidated
#   all 60 at once, and nothing on disk would have said so.
#
#   The three outcomes stay distinct. git grep exits 0 when it found matches, 1 when
#   it found none, and above that when it did not answer at all, so folding the last
#   two together reports a broken check as a clean one. CLAUDE_CODE_PREAMBLE.md rules
#   the same thing for the CI gate: an unreadable gate is unknown, never presumed.
#   A checker gets the same treatment, and git's own diagnostics stay unsuppressed so
#   the unknown can say what went wrong.
#
# Scope
#   docs/PLAN.md targets only, which is the obligation this check was written for.
#   Widening it to every markdown target is the natural next step and needs one
#   citation repaired first: ADR 0048 cites ADR 0004 by line number.
#   docs/sessions/ is excluded, because session logs are immutable history and a
#   citation inside one records what was true when it was written.
#   This file is excluded too, and the exclusion is the file rather than any line in
#   it. A checker that documents the pattern it matches carries example citations in
#   its own comments, so it reports itself; exempting the one line that trips it today
#   leaves the next example someone writes to trip it again. Nothing else leaves scope,
#   and the price is that a real citation written here would be missed.
#
# Exit codes
#   0  Clean. git answered and found no line-number citations into docs/PLAN.md.
#   1  Violations. git answered and found citations; each is printed with its location.
#   2  Unknown. git did not answer, so the check did not run. Whatever git reported is
#      on stderr. Callers that treat every non-zero exit as a failed check still stop,
#      and callers that care can tell a failed check from a broken one.
#
# Usage
#   ./scripts/check-plan-citations.sh

set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

# Matches PLAN.md:123 and docs/PLAN.md:123. A bare :123 continuing an earlier
# citation on the same line rides along with its explicit anchor, so anchoring on
# the explicit form finds every offending line.
# Those two example citations are why the second exclusion names this file.
# The status is captured rather than discarded. An if-condition is exempt from set -e,
# so the capture needs no set +e around it and leaves no window where a failure passes.
if hits=$(git grep -nE '(docs/)?PLAN\.md:[0-9]+' \
    -- . ':!docs/sessions' ':!scripts/check-plan-citations.sh'); then
    status=0
else
    status=$?
fi

if [ "$status" -gt 1 ]; then
    printf '%s\n' "docs/PLAN.md could not be checked: git grep exited $status." >&2
    printf '%s\n' "Any message git produced is above. This is unknown, not clean." >&2
    exit 2
fi

if [ "$status" -eq 0 ]; then
    printf '%s\n' "docs/PLAN.md is cited by line number. Cite it by content instead:" >&2
    printf '%s\n' "  name the phase and the clause, as in \"PLAN.md's Phase 2 provider-switch done-when\"." >&2
    printf '\n%s\n' "$hits" >&2
    exit 1
fi

printf '%s\n' "No line-number citations into docs/PLAN.md."
