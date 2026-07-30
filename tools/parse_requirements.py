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


# A requirement's delivery phase is **the earliest phase in which every clause of its
# acceptance criteria can be executed** — not the phase in which its code gets written.
#
# That distinction is the whole point. Assigning by where the work happens produces phases that
# cannot be closed: Phase 0 held REQ-SIM-001, whose criterion is "demodulates with RMS EVM
# < 0.1 %", so Phase 0 could not finish until Phase 2 delivered a demodulator, while Phase 1 was
# declared complete with 37 Phase 0 issues open. Where a requirement's criteria genuinely straddle
# phases, the requirement is split (`REQ-SIM-001` / `REQ-SIM-001a`) rather than filed under
# whichever phase happened to be closer.
#
# The area defaults below hold for requirements whose criteria need nothing but their own area's
# code. PHASE_OVERRIDES carries every requirement for which that is untrue, each with the
# dependency that forced it — a bare number here with no reason beside it is how the previous
# heuristic went wrong.
AREA_PHASE = {
    "ARC": 0, "DAT": 0, "HAL": 0, "SIM": 0, "PLN": 0,
    "ACQ": 1, "AMP": 1, "TRC": 1, "MKR": 1, "LIM": 1, "CHM": 1, "STA": 1, "UI": 1, "TRG": 1,
    "DEM": 2,
    "VISA": 3, "E44": 3, "REC": 3,
    "API": 4, "LIC": 4,
    "PER": 5,
}

# Two areas divide at a boundary the specification itself draws, so the split is structural
# rather than a guess at a number:
#   DSP  §9.1-§9.2 is the processing model and the primitives every measurement rests on;
#        §9.3 onward is measurement-facing work (zoom, trace math, cross-channel).
#   NFR  §6 is performance and runtime behaviour; §7 onward is operational — installer,
#        localisation, reporting, signing — which is what Phase 4 packages.
AREA_SECTION_SPLIT = {
    "DSP": (20, 0, 1),
    "NFR": (30, 0, 4),
}

PHASE_OVERRIDES = {
    # --- Verifiable only once a demodulator exists (Phase 2) ---------------------------------
    "REQ-SIM-001a": (2, "criterion is RMS EVM from a demodulator"),
    "REQ-SIM-002a": (2, "impairments recovered by the demodulator's metrics"),
    "REQ-DSP-001a": (2, "carrier/timing/phase estimators are the thing under test"),
    "REQ-ARC-002a": (2, "needs a demod measurement to survive the front-end change"),
    "REQ-NFR-022": (2, "benchmarks flexible demod at 16-QAM"),
    "REQ-NFR-032a": (2, "an error summary is the demodulator's output"),
    "REQ-DAT-002a": (3, "REQ-REC-005's export formats do not exist yet"),
    "REQ-DSP-012a": (3, "REQ-REC-005's export formats do not exist yet"),
    "REQ-NFR-023": (2, "benchmarks flexible demod at 1024-QAM with an equaliser"),

    # --- Verifiable only once a personality exists (Phase 5) ----------------------------------
    #
    # Same class as REQ-DSP-040a below: not a forward dependency on a later phase's work but a
    # dependency on work that is not scheduled at all. Before REQ-MKR-007 was split on
    # 2026-07-29 this clause was the ONLY mention of OFDM in the whole specification, so nothing
    # delivers the personality it needs. Placed where such a personality would belong; it cannot
    # close until one is specified. The atomicity checker could not catch this on its own --
    # "an OFDM personality" names no requirement ID for it to follow.
    "REQ-MKR-007a": (5, "needs an OFDM personality, which no requirement delivers"),

    # --- Verifiable only once recording and a real front end exist (Phase 3) ------------------
    "REQ-NFR-026": (3, "plays back a 4 GB recording; REQ-REC-001 delivers recordings"),
    "REQ-DSP-040a": (3, "needs phase-coherent two-channel acquisition; see the issue's own "
                        "note that no planned front end provides it"),

    # --- Verifiable only once a personality exists (Phase 5) ----------------------------------
    "REQ-SIM-005": (5, "each preset must be decoded by the personality it targets"),
    # No gating machinery is a property of the code from the first commit, and both REQ-DSP-040
    # and REQ-NFR-036 rest on it. Only the catalogue enumeration waits.
    "REQ-LIC-010": (0, "the absence of gating machinery is asserted over every build"),
    "REQ-LIC-010a": (2, "enumerates REQ-DEM-010's demodulation formats"),
    "REQ-NFR-036a": (4, "the SCPI listener it bounds is REQ-API-004"),
    "REQ-E44-006": (4, "the capture must embed in a REQ-NFR-040 report"),
    "REQ-REC-004": (1, "defines the bound live-block zoom REQ-DSP-023 must apply"),

    # --- Needed EARLIER than its area default -------------------------------------------------
    # The seven performance targets of REQ-NFR-020..026 share one criterion: the harness. If the
    # harness itself lands in Phase 3 then no Phase 0 target can be met, which is how six of them
    # sat open with no way to close.
    "REQ-TST-007": (0, "delivers the benchmark harness REQ-NFR-020..026 are all measured by"),

    # --- Needs the Phase 1 display and settings surfaces ---------------------------------------
    "REQ-DSP-012": (1, "replaces the window-type control, so it needs the settings UI"),
    "REQ-DAT-010": (1, "contexts must own trace windows and markers to be demonstrated"),
    "REQ-UI-010": (1, "sampling rendered zones needs the rasteriser"),
    "REQ-NFR-024": (1, "measures 20 simultaneous trace windows"),
    "REQ-NFR-025": (1, "cold start to first trace *displayed*"),

    # --- §16 test strategy: each suite lands with the thing it tests ---------------------------
    "REQ-TST-001": (0, "the DSP primitives it tests are Phase 0, and REQ-DSP-010 cites it"),
    "REQ-TST-007a": (3, "the seventh target is a 4 GB recording playback"),
    "REQ-TST-002": (2, "injected-impairment recovery matrix needs the metrics engine"),
    "REQ-TST-003": (2, "cross-impairment isolation needs the metrics engine"),
    "REQ-TST-004": (3, "compares against the E4406A"),
    "REQ-TST-004a": (3, "the budget is stated against REQ-TST-004's E4406A comparison"),
    "REQ-TST-005": (3, "golden recordings need REQ-REC recording"),
    "REQ-TST-006": (3, "every stored output must carry REQ-TST-005's provenance"),
    "REQ-TST-008": (1, "UI automation smoke tests over window creation and traces"),
    "REQ-TST-009": (1, "the 8-hour soak runs against the simulator and the shell"),

    # --- §7 operational NFRs that are nonetheless foundations ----------------------------------
    "REQ-NFR-030": (0, "the platform floor everything else is built on"),
    "REQ-NFR-032": (0, "runs with no hardware and no VISA — a Phase 0 architectural property"),
    "REQ-NFR-034": (0, "structured logging is needed by every later phase's diagnosis"),
    "REQ-NFR-036": (0, "no egress without opt-in; a property of the code from the start"),
    "REQ-NFR-037": (0, "bit-for-bit reproducibility constrains the DSP core's arithmetic"),
    "REQ-NFR-035": (3, "'without loss to in-progress recordings' needs recordings"),
    "REQ-NFR-039": (2, "the 90 % floor is over OpenVSA.Dsp *and* OpenVSA.Demod"),
}


def phase_for(area, num, req_id=None):
    if req_id in PHASE_OVERRIDES:
        return PHASE_OVERRIDES[req_id][0]
    if area in AREA_SECTION_SPLIT:
        boundary, before, after = AREA_SECTION_SPLIT[area]
        return before if int(re.match(r"\d+", num).group(0)) < boundary else after
    return AREA_PHASE.get(area, 1)


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
            "phase": phase_for(area, num, rid),
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
