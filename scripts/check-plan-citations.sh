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
# Scope
#   docs/PLAN.md targets only, which is the obligation this check was written for.
#   Widening it to every markdown target is the natural next step and needs one
#   citation repaired first: ADR 0048 cites ADR 0004 by line number.
#   docs/sessions/ is excluded, because session logs are immutable history and a
#   citation inside one records what was true when it was written.
#
# Exit codes
#   0  No line-number citations into docs/PLAN.md.
#   1  At least one found; each is printed with its location.
#
# Usage
#   ./scripts/check-plan-citations.sh

set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

# Matches PLAN.md:123 and docs/PLAN.md:123. A bare :123 continuing an earlier
# citation on the same line rides along with its explicit anchor, so anchoring on
# the explicit form finds every offending line.
if hits=$(git grep -nE '(docs/)?PLAN\.md:[0-9]+' -- . ':!docs/sessions' 2>/dev/null); then
    printf '%s\n' "docs/PLAN.md is cited by line number. Cite it by content instead:" >&2
    printf '%s\n' "  name the phase and the clause, as in \"PLAN.md's Phase 2 provider-switch done-when\"." >&2
    printf '\n%s\n' "$hits" >&2
    exit 1
fi

printf '%s\n' "No line-number citations into docs/PLAN.md."
