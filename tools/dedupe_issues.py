"""Delete surplus duplicate requirement issues, keeping the lowest issue number per ID.

Two concurrent create_issues.py runs produced a second issue for some requirements.
This removes the later copies. Usage:  python tools/dedupe_issues.py [--dry-run]
"""
import argparse
import collections
import json
import subprocess
import sys

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

REPO = "TGoodhew/OpenVSA"


def gh(args, check=True):
    return subprocess.run(["gh"] + args, check=check,
                          capture_output=True, text=True, encoding="utf-8")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    out = gh(["issue", "list", "--repo", REPO, "--state", "all", "--limit", "1000",
              "--json", "number,title"]).stdout
    issues = json.loads(out)

    groups = collections.defaultdict(list)
    for item in issues:
        title = item["title"]
        if ":" not in title:
            print("skipping non-requirement issue #{}: {}".format(
                item["number"], title))
            continue
        groups[title.split(":", 1)[0].strip()].append(item)

    surplus = []
    for req_id, items in sorted(groups.items()):
        if len(items) == 1:
            continue
        items.sort(key=lambda x: x["number"])
        keep, drop = items[0], items[1:]
        # Only ever delete an exact title match of the copy we are keeping.
        for d in drop:
            if d["title"] != keep["title"]:
                print("TITLE MISMATCH for {} — refusing to delete #{}".format(
                    req_id, d["number"]))
                return 1
            surplus.append((req_id, keep["number"], d["number"]))

    print("{} issues, {} unique requirement ids, {} surplus to delete".format(
        len(issues), len(groups), len(surplus)))

    for n, (req_id, keep, drop) in enumerate(surplus, 1):
        print("[{}/{}] {}: keep #{}, delete #{}".format(
            n, len(surplus), req_id, keep, drop))
        if args.dry_run:
            continue
        res = gh(["issue", "delete", str(drop), "--repo", REPO, "--yes"], check=False)
        if res.returncode != 0:
            print("   FAILED:", res.stderr.strip()[:300])
            return 1

    print("done")
    return 0


if __name__ == "__main__":
    sys.exit(main())
