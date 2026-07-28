"""Fail when a delivery phase is not self-contained.

A phase is self-contained when every requirement in it can be *proved* using only what that
phase and its predecessors deliver. The rule this enforces:

    No requirement's acceptance criteria may depend on a requirement delivered in a later phase.

Why this exists as a check rather than a convention. Delivery phases were originally derived
from a numeric heuristic over the requirement's area and number, with no reference to what
depended on what. The result was a backlog that could not be worked in order: `REQ-SIM-001`
sat in Phase 0 with the criterion "a clean generated signal demodulates with RMS EVM < 0.1 %",
which no amount of Phase 0 work could satisfy because the demodulator is Phase 2. Phase 1 was
declared complete while 37 Phase 0 issues stood open, and the same eleven blocked requirements
were re-examined and re-deferred session after session.

A forward dependency is not always a mistake in the phase assignment — sometimes it means the
requirement is really two requirements, one provable now and one later. Splitting it (see
`REQ-SIM-001` / `REQ-SIM-001a`) is the fix; moving the whole thing later is the alternative.
Either way this check is what says which requirements need the decision.

Not part of the product build. Run after `parse_requirements.py`:

    python tools/parse_requirements.py && python tools/check_phase_atomicity.py
"""
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RECORDS = os.path.join(ROOT, "tools", "requirements.json")

REQ_RE = re.compile(r"REQ-[A-Z0-9]+-\d+[a-z]?")

# The acceptance criteria are the only part that matters. A requirement's rationale or prose may
# legitimately point forward — "this is what makes REQ-DEM-002's estimation quality achievable"
# is an explanation, not a dependency, and forbidding it would push authors to write worse
# specifications to satisfy a checker.
AC_RE = re.compile(r"\*\*AC[^:]*:\*\*(.*?)(?=\n\*\*`REQ-|\Z)", re.S)

# References that name a requirement without depending on it being built. "as REQ-X states" and
# "split from REQ-X" describe where wording came from; "per REQ-X" inside a tolerance means the
# number is defined there. The first two are provenance, so they are exempt. A tolerance is a
# genuine dependency and is not exempt — a criterion that cannot state its own tolerance until a
# later phase defines it is exactly the kind that cannot be run.
PROVENANCE = re.compile(
    r"(?:split from|as stated in|restat\w+ (?:in|from)|see also|wording from)\s+`?(REQ-[A-Z0-9]+-\d+[a-z]?)",
    re.I,
)


def main():
    if not os.path.exists(RECORDS):
        sys.stderr.write("tools/requirements.json missing; run parse_requirements.py first\n")
        return 2

    with open(RECORDS, encoding="utf-8") as handle:
        records = json.load(handle)

    phase = {r["id"]: r["phase"] for r in records}
    violations = []

    for record in records:
        if record.get("ac_exempt"):
            continue

        blocks = AC_RE.findall(record["body"])
        if not blocks:
            continue

        criteria = "\n".join(blocks)
        exempt = set(PROVENANCE.findall(criteria))

        for other in sorted(set(REQ_RE.findall(criteria))):
            if other == record["id"] or other in exempt:
                continue
            if other not in phase:
                continue  # retired, or a typo parse_requirements.py already reports
            if phase[other] > record["phase"]:
                violations.append((record["id"], record["phase"], other, phase[other]))

    for rid, p, other, q in violations:
        print("PHASE %d  %-16s depends on %-16s delivered in phase %d" % (p, rid, other, q))

    counts = {}
    for record in records:
        counts[record["phase"]] = counts.get(record["phase"], 0) + 1

    print()
    print("phases:", dict(sorted(counts.items())))
    print("forward dependencies:", len(violations))

    if violations:
        print()
        print("Each of these makes its phase unclosable. Either split the requirement into the")
        print("clauses provable now and the clauses that are not, or move it to the later phase.")
        return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
