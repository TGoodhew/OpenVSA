"""Create GitHub labels, milestones and one issue per requirement.

Idempotent: re-running skips labels/milestones/issues that already exist.
Usage:  python tools/create_issues.py [--dry-run] [--limit N]
"""
import argparse
import json
import os
import subprocess
import sys
import time

# Requirement titles contain typographic characters (≥, —, ×) that the default Windows
# console codepage cannot encode; without this a plain print() aborts the whole run.
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REQS = os.path.join(ROOT, "tools", "requirements.json")
REPO = "TGoodhew/OpenVSA"
DOC_PATH = "requirements/OpenVSA-Requirements.md"

AREA_LABELS = {
    "ACQ": ("area:acq", "c5def5", "Acquisition layer"),
    "AMP": ("area:amp", "c5def5", "Amplitude and correction chain"),
    "API": ("area:api", "c5def5", "Automation / programming interfaces"),
    "ARC": ("area:arc", "5319e7", "System architecture"),
    "CHM": ("area:chm", "c5def5", "Channel measurements (ACP/OBW)"),
    "DAT": ("area:dat", "c5def5", "Core data model"),
    "DEM": ("area:dem", "1d76db", "Flexible digital demodulation"),
    "DSP": ("area:dsp", "1d76db", "Core DSP engine"),
    "E44": ("area:e44", "0e8a16", "Agilent E4406A front end"),
    "HAL": ("area:hal", "0e8a16", "Front-end abstraction layer"),
    "LIC": ("area:lic", "fbca04", "Distribution terms and feature availability"),
    "LIM": ("area:lim", "c5def5", "Limit lines and limit tests"),
    "MKR": ("area:mkr", "c5def5", "Markers"),
    "NFR": ("area:nfr", "d4c5f9", "Non-functional requirements"),
    "PER": ("area:per", "fef2c0", "Measurement personalities"),
    "PLN": ("area:pln", "d4c5f9", "Delivery planning"),
    "REC": ("area:rec", "0e8a16", "Recording, playback, import/export"),
    "SIM": ("area:sim", "0e8a16", "Simulated signal source"),
    "STA": ("area:sta", "c5def5", "State save / recall"),
    "TRC": ("area:trc", "c5def5", "Traces, data types and formats"),
    "TRG": ("area:trg", "c5def5", "Triggering"),
    "TST": ("area:tst", "bfd4f2", "Verification and test strategy"),
    "UI": ("area:ui", "f9d0c4", "User interface"),
    "VISA": ("area:visa", "0e8a16", "VISA transport"),
}

PRIORITY_LABELS = {
    "P0": ("b60205", "Blocking — product unusable without it"),
    "P1": ("d93f0b", "Required for core feature parity"),
    "P2": ("fbca04", "Full parity, deferrable behind a phase boundary"),
    "P3": ("0e8a16", "Desirable parity nicety"),
}

EVIDENCE_LABELS = {
    "verified": ("0e8a16", "[V] — value verified against a cited source"),
    "unverified": ("e99695", "[U] — value unconfirmed; must be confirmed before use"),
    "design-choice": ("d4c5f9", "[DESIGN CHOICE] — spec selects a defensible algorithm"),
}

EXTRA_LABELS = {
    "requirement": ("ededed", "Tracks a REQ-<AREA>-<nnn> from the requirements spec"),
    "needs-ac": ("e99695", "Requirement has no mechanised acceptance criteria yet"),
}

MILESTONES = {
    0: ("Phase 0 — Foundations",
        "Solution structure, IqBlock, HAL contract, buffer pooling, simulated source, "
        "file playback of the native format, FFT/window core, minimal WPF shell with one "
        "spectrum trace, plot-surface and hot-spot prototypes. ROM 5-7 engineer-months."),
    1: ("Phase 1 — Core VSA",
        "All base trace data types and formats, RBW/time coupling, averaging, overlap, "
        "gating, markers, limit tests, ACP/OBW, trace math, amplitude/correction chain, "
        "state save/recall, docking layout, full annotation and in-place hot-spot editing, "
        "accessibility. ROM 10-15 engineer-months."),
    2: ("Phase 2 — Flexible demodulation",
        "Format catalogue, filters, block estimator, sync and pulse search, equaliser, "
        "full metrics suite, all demod result traces, impairment test matrix. "
        "ROM 10-14 engineer-months."),
    3: ("Phase 3 — Hardware",
        "VISA layer, E4406A driver (after SCPI verification), recording, import/export "
        "formats, cross-validation harness. ROM 5-7 engineer-months."),
    4: ("Phase 4 — Automation & packaging",
        ".NET API, headless operation, SCPI server, macros, command log, "
        "installer. ROM 4-6 engineer-months."),
    5: ("Phase 5 — Personality wave 1",
        "GSM/EDGE, W-CDMA/HSPA, cdmaOne - each validated against the E4406A. "
        "ROM 8-12 engineer-months."),
}


def gh(args, check=True, capture=True):
    return subprocess.run(["gh"] + args, check=check,
                          capture_output=capture, text=True, encoding="utf-8")


def ensure_labels(dry):
    existing = set()
    out = gh(["label", "list", "--repo", REPO, "--limit", "200",
              "--json", "name"]).stdout
    existing = {x["name"] for x in json.loads(out)}

    wanted = {}
    for _area, (name, colour, desc) in AREA_LABELS.items():
        wanted[name] = (colour, desc)
    for name, (colour, desc) in PRIORITY_LABELS.items():
        wanted[name] = (colour, desc)
    for name, (colour, desc) in EVIDENCE_LABELS.items():
        wanted[name] = (colour, desc)
    for name, (colour, desc) in EXTRA_LABELS.items():
        wanted[name] = (colour, desc)

    for name, (colour, desc) in sorted(wanted.items()):
        if name in existing:
            continue
        print("label +", name)
        if not dry:
            gh(["label", "create", name, "--repo", REPO,
                "--color", colour, "--description", desc])


def ensure_milestones(dry):
    out = gh(["api", "repos/{}/milestones?state=all&per_page=100".format(REPO)]).stdout
    existing = {m["title"]: m["number"] for m in json.loads(out)}
    numbers = {}
    for phase, (title, desc) in sorted(MILESTONES.items()):
        if title in existing:
            numbers[phase] = existing[title]
            continue
        print("milestone +", title)
        if dry:
            numbers[phase] = -1
            continue
        res = gh(["api", "repos/{}/milestones".format(REPO), "-X", "POST",
                  "-f", "title=" + title, "-f", "description=" + desc])
        numbers[phase] = json.loads(res.stdout)["number"]
    return numbers


def existing_issue_ids():
    out = gh(["issue", "list", "--repo", REPO, "--state", "all",
              "--limit", "1000", "--json", "title"]).stdout
    ids = set()
    for item in json.loads(out):
        title = item["title"]
        if ":" in title:
            ids.add(title.split(":", 1)[0].strip())
    return ids


def needs_ac(rec):
    """Whether acceptance criteria are still owed for this requirement.

    A requirement marked `**AC exempt:**` in the specification is one no built artefact
    can be measured against — a planning constraint, say. It is not owed criteria, so it
    does not carry the label; the exemption and its reasoning are in the issue body.
    """
    return not rec["has_ac"] and not rec.get("ac_exempt")


def build_body(rec):
    if rec["has_ac"]:
        ac_state = "yes"
    elif rec.get("ac_exempt"):
        ac_state = "exempt — not mechanisable"
    else:
        ac_state = "**no**"
    lines = [
        "> Auto-generated from [`{doc}`]({doc}) (revision 1.0), line {line}.".format(
            doc=DOC_PATH, line=rec["line"]),
        "",
        "| Field | Value |",
        "|---|---|",
        "| Requirement ID | `{}` |".format(rec["id"]),
        "| Priority | **{}** |".format(rec["priority"]),
        "| Area | `{}` |".format(rec["area"]),
        "| Delivery phase | {} |".format(MILESTONES[rec["phase"]][0]),
        "| Evidence grade | {} |".format(
            ", ".join(rec["evidence"]) if rec["evidence"] else "n/a"),
        "| Acceptance criteria in spec | {} |".format(ac_state),
        "",
        "## Specification text",
        "",
        rec["body"],
        "",
    ]
    if needs_ac(rec):
        lines += [
            "## Note",
            "",
            "The specification does not state mechanised acceptance criteria for this "
            "requirement. Acceptance criteria must be authored before implementation "
            "starts (see the `needs-ac` label).",
            "",
        ]
    lines += [
        "---",
        "",
        "_Do not implement yet — this issue exists to track the requirement._",
    ]
    return "\n".join(lines)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--delay", type=float, default=1.0)
    args = ap.parse_args()

    with open(REQS, encoding="utf-8") as fh:
        records = json.load(fh)

    # Two concurrent runs each take their own snapshot of "already created" and then
    # both create the same issues; that is how the first pass produced 138 duplicates.
    lock = os.path.join(ROOT, "tools", ".create_issues.lock")
    if not args.dry_run:
        try:
            fd = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
        except OSError:
            print("another run holds {} — refusing to start a second one.\n"
                  "If no run is active, delete that file.".format(lock))
            return 1
        os.write(fd, str(os.getpid()).encode())
        os.close(fd)

    try:
        return run(args, records)
    finally:
        if not args.dry_run and os.path.exists(lock):
            os.remove(lock)


def run(args, records):
    ensure_labels(args.dry_run)
    milestones = ensure_milestones(args.dry_run)
    done = existing_issue_ids()

    todo = [r for r in records if r["id"] not in done]
    if args.limit:
        todo = todo[:args.limit]
    print("{} requirements, {} already have issues, creating {}".format(
        len(records), len(records) - len([r for r in records if r["id"] not in done]),
        len(todo)))

    # Process-unique: a shared path here lets two concurrent runs overwrite each
    # other's body between the write and gh's read, producing issues whose body
    # belongs to a different requirement (or is empty).
    tmp = os.path.join(ROOT, "tools", "_body.{}.md".format(os.getpid()))
    for n, rec in enumerate(todo, 1):
        title = "{}: {}".format(rec["id"], rec["title"])
        labels = ["requirement", rec["priority"], AREA_LABELS[rec["area"]][0]]
        labels += rec["evidence"]
        if needs_ac(rec):
            labels.append("needs-ac")
        print("[{}/{}] {}".format(n, len(todo), title))
        if args.dry_run:
            continue
        with open(tmp, "w", encoding="utf-8") as fh:
            fh.write(build_body(rec))
        cmd = ["issue", "create", "--repo", REPO, "--title", title,
               "--body-file", tmp, "--milestone", MILESTONES[rec["phase"]][0]]
        for lab in labels:
            cmd += ["--label", lab]
        res = gh(cmd, check=False)
        if res.returncode != 0:
            print("   FAILED:", res.stderr.strip()[:400])
            time.sleep(10)
            res = gh(cmd, check=False)
            if res.returncode != 0:
                print("   FAILED AGAIN, stopping.")
                return 1
        time.sleep(args.delay)

    if os.path.exists(tmp):
        os.remove(tmp)
    print("done")
    return 0


if __name__ == "__main__":
    sys.exit(main())
