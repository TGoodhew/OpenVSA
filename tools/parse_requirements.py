"""Parse requirements/OpenVSA-Requirements.md into a JSON list of requirement records.

Emits tools/requirements.json with one record per REQ-<AREA>-<nnn> definition.
Not part of the product build; a one-off authoring aid for GitHub issue creation.
"""
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOC = os.path.join(ROOT, "requirements", "OpenVSA-Requirements.md")
OUT = os.path.join(ROOT, "tools", "requirements.json")

# **`REQ-ARC-001` (P0) — Strict acquisition/analysis separation.**
# **`REQ-TST-001` (P0)** — Every DSP primitive shall ...
# **`REQ-NFR-008` (P2)** — Every third-party dependency ...
DEF_RE = re.compile(
    r"^\*\*`(REQ-([A-Z0-9]+)-(\d+[a-z]?))`\s*\((P[0-3])\)"
    r"(?:\s*(?:—|--|-)\s*(?P<t1>[^*]*?))?\*\*"
    r"\s*(?:(?:—|--|-)\s*)?(?P<t2>.*)$"
)

# | `REQ-NFR-020` | Spectrum, 8 192-point FFT ... | P1 |
ROW_RE = re.compile(
    r"^\|\s*`(REQ-([A-Z0-9]+)-(\d+[a-z]?))`\s*\|\s*(?P<text>.+?)\s*\|\s*(?P<pri>P[0-3])\s*\|\s*$"
)

# Prose that qualifies an already-defined requirement, carrying no priority of its own:
#   **`REQ-NFR-037` needs a qualification, or it silently contradicts the threading model.**
# For the §15 table requirements this prose holds the normative restatement and the AC,
# so dropping it loses the actual requirement text.
CONT_RE = re.compile(r"^\*\*`(REQ-([A-Z0-9]+)-(\d+[a-z]?))`(?!\s*\(P[0-3]\))")

HEADING_RE = re.compile(r"^#{1,4}\s")

# §6.2 states one set of acceptance criteria for a whole table of performance targets. A
# table row's body is only its own cell, so without this the shared criteria belong to no
# record and all seven targets parse as un-mechanised when the specification does state them.
SHARED_AC_RE = re.compile(r"^\*\*AC \(all\):\*\*")

# Requirements that cannot be mechanised at all — planning constraints and the like. Marked
# explicitly so `needs-ac` keeps meaning "criteria are owed" rather than silently mixing in
# requirements for which no criteria are possible.
AC_EXEMPT = "**AC exempt:**"


# Most requirements carry their own short name in the specification
# ("**`REQ-X-001` (P0) — Short name.**") and derive a good title automatically. These do
# not: they are stated as a bare sentence, or as a table row longer than the title cap, so
# the derived title breaks off mid-clause. The names below are hand-written to say the same
# thing in full. REQ-NFR-022 keeps its complete statement, matching its sibling rows.
TITLE_OVERRIDES = {
    "REQ-DAT-010": "Named measurement contexts",
    "REQ-NFR-008": "Third-party dependency register and licence policy",
    "REQ-NFR-022": "Flexible demod, 16-QAM, 4 096 symbols, 4 pts/symbol, equaliser off: "
                   "complete analysis ≤50 ms",
    "REQ-NFR-032": "Runs with no hardware and no VISA installed",
    "REQ-NFR-035": "Diagnostic report on unhandled exception, without loss to in-progress "
                   "recordings",
    "REQ-NFR-037": "Bit-for-bit reproducible numeric results",
    "REQ-NFR-040": "Report generation to PDF/HTML from a template",
    "REQ-NFR-041": "Plug-in load directories and signature enforcement",
    "REQ-NFR-042": "Automation API exception contract, versioning and deprecation policy",
    "REQ-PER-010": "Wave 1 personality validation against the E4406A",
    "REQ-PER-011": "Declared standard revision and documented deviations",
    "REQ-PLN-001": "Phases 0–4 are the minimum viable product",
    "REQ-TST-001": "DSP primitives tested against closed-form analytic results",
}


# Requirement IDs that were withdrawn and whose numbers are deliberately not reused. The
# specification still names them where it records why they went, so without this the
# "mentioned but never defined" check — which exists to catch typos and renames — would carry
# permanent false positives and stop being worth reading.
RETIRED = {
    "REQ-LIC-001": "entitlement-based feature gating",
    "REQ-LIC-002": "licence models",
    "REQ-LIC-003": "selective checkout",
    "REQ-LIC-004": "ungated development mode",
}


def phase_for(area, num):
    n = int(re.match(r"\d+", num).group(0))
    if area == "NFR":
        if n <= 29:
            return 0
        return 4
    if area == "DSP":
        return 0 if n < 20 else 1
    if area == "TST":
        return 2 if n <= 3 else 3
    if area == "UI":
        return 0 if n in (10, 42) else 1
    return {
        "ARC": 0, "DAT": 0, "HAL": 0, "SIM": 0, "PLN": 0,
        "ACQ": 1, "AMP": 1, "TRC": 1, "MKR": 1, "LIM": 1, "CHM": 1, "STA": 1,
        "DEM": 2,
        "VISA": 3, "E44": 3, "REC": 3,
        "API": 4, "LIC": 4,
        "PER": 5,
    }.get(area, 1)


def evidence(text):
    tags = []
    for tag, label in (("[V]", "verified"), ("[U]", "unverified"),
                       ("[DESIGN CHOICE]", "design-choice")):
        if tag in text:
            tags.append(label)
    return tags


def shared_ac_blocks(lines):
    """Find every **AC (all):** paragraph and the section of the document it governs.

    Returns (section_start, section_end, text) triples, the bounds being the enclosing
    headings. A shared block applies to the table rows in its own section only.
    """
    blocks = []
    for i, line in enumerate(lines):
        if not SHARED_AC_RE.match(line):
            continue
        end_para = i
        while end_para < len(lines) and lines[end_para].strip():
            end_para += 1
        start = 0
        for k in range(i, -1, -1):
            if HEADING_RE.match(lines[k]):
                start = k
                break
        end = len(lines)
        for k in range(i + 1, len(lines)):
            if HEADING_RE.match(lines[k]):
                end = k
                break
        blocks.append((start, end, "\n".join(lines[i:end_para]).strip()))
    return blocks


def short_title(text):
    t = text.strip().rstrip(".")
    t = re.sub(r"\*\*|`|\[V\]|\[U\]|\[DESIGN CHOICE\]", "", t).strip()
    t = re.sub(r"\s+", " ", t)
    if len(t) > 90:
        cut = t[:90].rsplit(" ", 1)[0]
        t = cut + "…"
    return t


def main():
    with open(DOC, encoding="utf-8") as fh:
        lines = fh.read().split("\n")

    # Pass 1: locate every definition start.
    starts = []  # (index, kind, id, area, num, priority, title_seed)
    for i, line in enumerate(lines):
        m = DEF_RE.match(line)
        if m:
            seed = (m.group("t1") or m.group("t2") or "").strip()
            starts.append((i, "block", m.group(1), m.group(2), m.group(3), m.group(4), seed))
            continue
        m = ROW_RE.match(line)
        if m:
            starts.append((i, "row", m.group(1), m.group(2), m.group(3),
                           m.group("pri"), m.group("text").strip()))
            continue
        m = CONT_RE.match(line)
        if m:
            starts.append((i, "cont", m.group(1), m.group(2), m.group(3), None, ""))

    seen = {}
    records = []
    row_ids = set()
    for idx, (i, kind, rid, area, num, pri, seed) in enumerate(starts):
        if kind == "row":
            body = seed
        else:
            end = starts[idx + 1][0] if idx + 1 < len(starts) else len(lines)
            chunk = []
            for j in range(i, end):
                if j > i and (HEADING_RE.match(lines[j]) or lines[j].strip() == "---"):
                    break
                chunk.append(lines[j])
            body = "\n".join(chunk).strip()

        if rid in seen:
            # Later mention is a restatement/qualification; append it.
            rec = records[seen[rid]]
            rec["body"] += "\n\n---\n\n" + body
            # The qualification may carry the AC and the evidence grade.
            rec["has_ac"] = rec["has_ac"] or "**AC:**" in body
            rec["ac_exempt"] = rec["ac_exempt"] or AC_EXEMPT in body
            for tag in evidence(body):
                if tag not in rec["evidence"]:
                    rec["evidence"].append(tag)
            continue

        if kind == "cont":
            # Qualifies a requirement we have not seen defined — should not happen.
            print("WARNING: continuation for undefined {} at line {}".format(rid, i + 1))
            continue

        seen[rid] = len(records)
        if kind == "row":
            row_ids.add(rid)
        records.append({
            "id": rid,
            "area": area,
            "num": num,
            "priority": pri,
            "title": TITLE_OVERRIDES.get(rid) or short_title(seed),
            "body": body,
            "phase": phase_for(area, num),
            "evidence": evidence(body),
            "has_ac": "**AC:**" in body or "AC:" in body,
            "ac_exempt": AC_EXEMPT in body,
            "line": i + 1,
        })

    # Give each table-row requirement the acceptance criteria its section states for the
    # whole table, so the issue carries them rather than pointing at a document section.
    for start, end, text in shared_ac_blocks(lines):
        for rec in records:
            if rec["id"] in row_ids and start < rec["line"] - 1 < end and not rec["has_ac"]:
                rec["body"] += "\n\n" + text
                rec["has_ac"] = True

    stale = sorted(set(TITLE_OVERRIDES) - set(seen))
    if stale:
        print("ERROR: TITLE_OVERRIDES names requirements that do not exist:", stale)
        return 1

    # A retired ID that has come back is a mistake in one direction or the other: either the
    # number was reused, or the entry is stale. Both are worth failing on.
    revived = sorted(set(RETIRED) & set(seen))
    if revived:
        print("ERROR: RETIRED names requirements that are defined after all:", revived)
        return 1

    with open(OUT, "w", encoding="utf-8") as fh:
        json.dump(records, fh, indent=1, ensure_ascii=False)

    areas = {}
    pris = {}
    phases = {}
    for r in records:
        areas[r["area"]] = areas.get(r["area"], 0) + 1
        pris[r["priority"]] = pris.get(r["priority"], 0) + 1
        phases[r["phase"]] = phases.get(r["phase"], 0) + 1
    print("total:", len(records))
    print("areas:", dict(sorted(areas.items())))
    print("priorities:", dict(sorted(pris.items())))
    print("phases:", dict(sorted(phases.items())))
    print("no AC:", sum(1 for r in records
                        if not r["has_ac"] and not r["ac_exempt"]),
          "(plus {} exempt)".format(sum(1 for r in records if r["ac_exempt"])))

    print("retired ({}): {}".format(len(RETIRED), ", ".join(sorted(RETIRED))))

    mentioned = set(re.findall(r"REQ-[A-Z0-9]+-\d+[a-z]?", "\n".join(lines)))
    missing = sorted(mentioned - set(seen) - set(RETIRED))
    print("mentioned but never defined ({}):".format(len(missing)))
    for rid in missing:
        print("   ", rid)


if __name__ == "__main__":
    sys.exit(main())
