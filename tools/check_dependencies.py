"""Fail when DEPENDENCIES.md and the solution's package references disagree.

`REQ-NFR-008`: a CI check enumerates every package reference across the solution and fails the
build when one has no entry in `DEPENDENCIES.md`, when an entry omits its licence or its
justification, or when an entry names a package no project references any longer. A dependency
whose licence is GPL, or LGPL linked statically, fails the same check unless its entry records a
written approval.

Why the last clause matters more than it looks. The specification names the traps by name: FFTW is
GPL unless a commercial licence is bought, and it is exactly what somebody reaching for a faster
transform under `REQ-NFR-004` would find first. A register nobody checks would record the licence
correctly and still not stop the build.

Why both directions are checked. A package with no entry is the obvious failure. An entry naming a
package nothing references any longer is the quiet one: the register grows stale, a reader trusts
it, and the next licence review is done against a document that describes a different product.

Not part of the product build. Run from the repository root:

    python tools/check_dependencies.py
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REGISTER = os.path.join(ROOT, "DEPENDENCIES.md")

PACKAGE_RE = re.compile(
    r"<PackageReference\s+[^>]*Include\s*=\s*\"([^\"]+)\"[^>]*?"
    r"(?:Version\s*=\s*\"([^\"]+)\")?[^>]*/?>",
    re.I,
)

# | `Package` | 1.2.3 | MIT | Why it is here. |
ROW_RE = re.compile(
    r"^\|\s*`([^`]+)`\s*\|\s*([^|]*?)\s*\|\s*([^|]*?)\s*\|\s*(.*?)\s*\|\s*$"
)

# Licences that may not be shipped without a recorded decision. Matched on the whole licence
# cell, so "LGPL-2.1 (dynamically linked)" is caught and read by a human rather than guessed at
# here — this check refuses, it does not adjudicate.
COPYLEFT_RE = re.compile(r"\b(?:A?GPL|LGPL)\b", re.I)

APPROVAL_RE = re.compile(r"approved by|written approval|approval recorded", re.I)


def package_references():
    """Every (package, version, project) triple in the tree."""
    found = []

    for directory, subdirectories, files in os.walk(ROOT):
        # Build output holds generated project files that reference the same packages again.
        subdirectories[:] = [
            d for d in subdirectories
            if d not in ("bin", "obj", ".git", ".github", "packages")
        ]

        for name in files:
            # Not only .csproj. Directory.Build.props is exactly where a solution puts the
            # references every project shares — the test framework lives there — and a checker
            # that read project files alone reported xunit as recorded-but-unreferenced while
            # every test project used it.
            if not (name.endswith(".csproj") or
                    name.endswith(".props") or
                    name.endswith(".targets")):
                continue

            path = os.path.join(directory, name)

            with open(path, encoding="utf-8") as handle:
                text = handle.read()

            for package, version in PACKAGE_RE.findall(text):
                found.append((package, version, os.path.relpath(path, ROOT)))

    return found


def register_entries():
    """Every row of the register's dependency table, by package name."""
    entries = {}

    with open(REGISTER, encoding="utf-8") as handle:
        for number, line in enumerate(handle, start=1):
            match = ROW_RE.match(line.rstrip("\n"))

            if not match:
                continue

            package, version, licence, justification = match.groups()

            # The header separator row parses as a row whose cells are dashes.
            if set(licence) <= set("-: ") and set(version) <= set("-: "):
                continue

            entries[package] = {
                "version": version,
                "licence": licence,
                "justification": justification,
                "line": number,
            }

    return entries


def main():
    if not os.path.exists(REGISTER):
        sys.stderr.write("DEPENDENCIES.md is missing; REQ-NFR-008 requires it.\n")
        return 2

    references = package_references()
    entries = register_entries()

    referenced = {}

    for package, version, project in references:
        referenced.setdefault(package, {"versions": set(), "projects": set()})
        referenced[package]["versions"].add(version or "")
        referenced[package]["projects"].add(project)

    problems = []

    for package in sorted(referenced):
        entry = entries.get(package)

        if entry is None:
            problems.append(
                "%s is referenced by %s and has no entry in DEPENDENCIES.md."
                % (package, ", ".join(sorted(referenced[package]["projects"])))
            )
            continue

        if not entry["licence"]:
            problems.append("%s has no licence recorded (line %d)." % (package, entry["line"]))

        if not entry["justification"]:
            problems.append(
                "%s has no justification recorded (line %d). An entry that says what a package is"
                " but not why it is here cannot be reviewed." % (package, entry["line"])
            )

        # A stale version in the register is not a licence problem, but it is how a register stops
        # being worth reading: the next review is done against a document describing a different
        # build.
        versions = {v for v in referenced[package]["versions"] if v}

        if versions and entry["version"] and entry["version"] not in versions:
            problems.append(
                "%s is recorded as %s but referenced at %s."
                % (package, entry["version"], ", ".join(sorted(versions)))
            )

        if COPYLEFT_RE.search(entry["licence"]) and not APPROVAL_RE.search(entry["justification"]):
            problems.append(
                "%s is licensed '%s' and its entry records no written approval (line %d). "
                "REQ-NFR-008 requires one before copyleft ships."
                % (package, entry["licence"], entry["line"])
            )

    for package in sorted(entries):
        if package not in referenced:
            problems.append(
                "%s has an entry in DEPENDENCIES.md (line %d) but no project references it. "
                "Remove the entry, or the register describes a product that no longer exists."
                % (package, entries[package]["line"])
            )

    for problem in problems:
        print("  " + problem)

    print()
    print("%d package(s) referenced, %d recorded, %d problem(s)."
          % (len(referenced), len(entries), len(problems)))

    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
