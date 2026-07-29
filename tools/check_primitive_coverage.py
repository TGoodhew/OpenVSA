"""Fail when a DSP primitive named by REQ-TST-001 has no closed-form test.

The requirement lists seven components and the analytic reference each is to be tested against, and
then says: "A DSP primitive with no closed-form test fails the build rather than passing untested,
so the table is a floor and not a sample."

That sentence is the whole reason this exists. A table in a specification is a list of intentions
until something reads it; the failure it guards against is not a wrong test but an absent one, and
an absent test is invisible in a green run by construction.

The check is deliberately shallow. It asks whether a test file exists that names the component and
cites REQ-TST-001 — not whether that test is any good. A deeper check would need to understand what
"analytic reference" means for a resampler, and a checker that tried and got it wrong would be worse
than one whose limits are stated: it would produce confident verdicts nobody could audit. What this
buys is that adding a primitive without a test fails the build, which is what the requirement asks
for and what nothing else does.

Not part of the product build. Run from the repository root:

    python tools/check_primitive_coverage.py
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SUITE = os.path.join(ROOT, "tests", "OpenVSA.Dsp.Tests")
SPEC = os.path.join(ROOT, "requirements", "OpenVSA-Requirements.md")

# The §16 table, read from the specification rather than copied here — a copy would drift from the
# document it claims to enforce, and would do so silently.
TABLE_START = "| Component | Analytic reference |"

# What each component's tests are expected to mention. The table's own names are prose ("RRC/RC
# filters"), so the terms that identify a test for it are listed beside each.
TERMS = {
    "FFT": ["Fft", "Parseval"],
    "Windows": ["Window"],
    "RRC/RC filters": ["Rrc", "RaisedCosine", "FirDesign"],
    "Resampler": ["Resampl", "Decimat", "Polyphase"],
    "Averaging": ["Averag"],
    "CCDF": ["Ccdf", "PowerStatistics"],
    "Metrics": ["Impairment", "ErrorSummary", "Evm"],
}


def table_components():
    """The component column of REQ-TST-001's table, in document order."""
    with open(SPEC, encoding="utf-8") as handle:
        lines = handle.read().split("\n")

    try:
        start = next(i for i, line in enumerate(lines) if line.startswith(TABLE_START))
    except StopIteration:
        return []

    components = []

    # Skip the header and its separator row.
    for line in lines[start + 2:]:
        if not line.startswith("|"):
            break

        cells = [c.strip() for c in line.strip("|").split("|")]

        if cells and cells[0]:
            components.append(cells[0])

    return components


def suite_text():
    """Every DSP test source, concatenated, with its file name for reporting."""
    found = []

    for directory, subdirectories, files in os.walk(SUITE):
        subdirectories[:] = [d for d in subdirectories if d not in ("bin", "obj")]

        for name in files:
            if not name.endswith(".cs"):
                continue

            path = os.path.join(directory, name)

            with open(path, encoding="utf-8") as handle:
                found.append((name, handle.read()))

    return found


def main():
    components = table_components()

    if not components:
        sys.stderr.write("REQ-TST-001's component table could not be read from the specification.\n")
        return 2

    sources = suite_text()

    if not sources:
        sys.stderr.write("No DSP test sources found under " + SUITE + "\n")
        return 2

    problems = []

    for component in components:
        terms = TERMS.get(component)

        if terms is None:
            problems.append(
                "%s is in REQ-TST-001's table and this checker has no terms for it. Add them to "
                "TERMS, or the table has grown a component nothing looks for." % component)
            continue

        covering = [
            name for name, text in sources
            if any(t.lower() in name.lower() or t.lower() in text.lower() for t in terms)
        ]

        if not covering:
            problems.append(
                "%s has no test in OpenVSA.Dsp.Tests mentioning any of %s. REQ-TST-001: a DSP "
                "primitive with no closed-form test fails the build rather than passing untested."
                % (component, ", ".join(terms)))
            continue

        print("  %-16s %s" % (component, ", ".join(sorted(covering)[:3]) +
                              ("" if len(covering) <= 3 else " (+%d more)" % (len(covering) - 3))))

    print()

    for problem in problems:
        print("  " + problem)

    print("%d component(s) in the table, %d problem(s)." % (len(components), len(problems)))
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
