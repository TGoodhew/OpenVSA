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
            for tag in evidence(body):
                if tag not in rec["evidence"]:
                    rec["evidence"].append(tag)
            continue

        if kind == "cont":
            # Qualifies a requirement we have not seen defined — should not happen.
            print("WARNING: continuation for undefined {} at line {}".format(rid, i + 1))
            continue

        seen[rid] = len(records)
        records.append({
            "id": rid,
            "area": area,
            "num": num,
            "priority": pri,
            "title": short_title(seed),
            "body": body,
            "phase": phase_for(area, num),
            "evidence": evidence(body),
            "has_ac": "**AC:**" in body or "AC:" in body,
            "line": i + 1,
        })

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
    print("no AC:", sum(1 for r in records if not r["has_ac"]))

    mentioned = set(re.findall(r"REQ-[A-Z0-9]+-\d+[a-z]?", "\n".join(lines)))
    missing = sorted(mentioned - set(seen))
    print("mentioned but never defined ({}):".format(len(missing)))
    for rid in missing:
        print("   ", rid)


if __name__ == "__main__":
    sys.exit(main())
