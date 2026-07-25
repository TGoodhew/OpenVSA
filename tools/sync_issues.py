"""Reconcile existing requirement issues with tools/requirements.json.

Repairs bodies and labels that have drifted from the parsed specification — including
issues whose body belongs to a different requirement, a defect the shared-temp-file race
in an earlier create_issues.py could produce. Safe to re-run; only issues that actually
differ are touched.

Usage:  python tools/sync_issues.py [--dry-run]
"""
import argparse
import json
import os
import sys

from create_issues import (AREA_LABELS, EVIDENCE_LABELS, MILESTONES, REPO,
                           build_body, gh)

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REQS = os.path.join(ROOT, "tools", "requirements.json")
EVIDENCE_NAMES = set(EVIDENCE_LABELS)


def wanted_labels(rec):
    labels = {"requirement", rec["priority"], AREA_LABELS[rec["area"]][0]}
    labels |= set(rec["evidence"])
    if not rec["has_ac"]:
        labels.add("needs-ac")
    return labels


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    with open(REQS, encoding="utf-8") as fh:
        records = {r["id"]: r for r in json.load(fh)}

    out = gh(["issue", "list", "--repo", REPO, "--state", "all", "--limit", "1000",
              "--json", "number,title,body,labels,milestone"]).stdout
    issues = json.loads(out)

    # Only labels this tool owns; anything a human added by hand is left alone.
    managed = ({"requirement", "needs-ac", "P0", "P1", "P2", "P3"}
               | EVIDENCE_NAMES | {n for n, _c, _d in AREA_LABELS.values()})

    tmp = os.path.join(ROOT, "tools", "_sync.{}.md".format(os.getpid()))
    changed = 0
    for item in sorted(issues, key=lambda x: x["number"]):
        rid = item["title"].split(":", 1)[0].strip()
        rec = records.get(rid)
        if rec is None:
            print("#{} {!r}: no matching requirement — skipped".format(
                item["number"], item["title"]))
            continue

        fixes = []
        # Matching is on the `REQ-<AREA>-<nnn>:` prefix, so the rest of the title can be
        # rewritten freely without breaking the link between issue and requirement.
        want_title = "{}: {}".format(rec["id"], rec["title"])
        if item["title"] != want_title:
            fixes.append("title")

        expected = build_body(rec)
        if item["body"].replace("\r\n", "\n").strip() != expected.strip():
            fixes.append("body")

        have = {x["name"] for x in item["labels"]} & managed
        want = wanted_labels(rec)
        add, remove = sorted(want - have), sorted(have - want)
        if add:
            fixes.append("+" + ",".join(add))
        if remove:
            fixes.append("-" + ",".join(remove))

        ms = item["milestone"]["title"] if item["milestone"] else None
        want_ms = MILESTONES[rec["phase"]][0]
        if ms != want_ms:
            fixes.append("milestone->" + want_ms)

        if not fixes:
            continue
        changed += 1
        print("#{} {}: {}".format(item["number"], rid, "; ".join(fixes)))
        if args.dry_run:
            continue

        cmd = ["issue", "edit", str(item["number"]), "--repo", REPO]
        if "title" in fixes:
            cmd += ["--title", want_title]
        if "body" in fixes:
            with open(tmp, "w", encoding="utf-8") as fh:
                fh.write(expected)
            cmd += ["--body-file", tmp]
        for lab in add:
            cmd += ["--add-label", lab]
        for lab in remove:
            cmd += ["--remove-label", lab]
        if ms != want_ms:
            cmd += ["--milestone", want_ms]
        res = gh(cmd, check=False)
        if res.returncode != 0:
            print("   FAILED:", res.stderr.strip()[:300])
            return 1

    if os.path.exists(tmp):
        os.remove(tmp)
    print("{} issue(s) {}".format(changed, "need changes" if args.dry_run else "updated"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
