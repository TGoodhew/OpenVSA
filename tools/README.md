# Requirement-tracking tools

One-off authoring aids that turn [`requirements/OpenVSA-Requirements.md`](../requirements/OpenVSA-Requirements.md)
into the GitHub issue backlog. They are **not part of the product build** and are not
referenced by `OpenVSA.slnx`. Python 3.8+ and an authenticated `gh` CLI are required.

## Scripts, in the order they are used

| Script | Purpose |
|---|---|
| `parse_requirements.py` | Parses the specification into `requirements.json`, one record per `REQ-<AREA>-<nnn>`. Deterministic: same input always yields byte-identical output. |
| `create_issues.py` | Creates labels, milestones and one issue per requirement. Idempotent — skips requirements that already have an issue. |
| `sync_issues.py` | Reconciles existing issues with `requirements.json`, repairing drifted titles, bodies, labels and milestones. Run after re-parsing. |
| `dedupe_issues.py` | Deletes surplus duplicate issues, keeping the lowest issue number per requirement. |

Every script takes `--dry-run`. Always dry-run first; `dedupe_issues.py` deletes
irreversibly.

```sh
python tools/parse_requirements.py
python tools/create_issues.py --dry-run
python tools/create_issues.py
python tools/sync_issues.py --dry-run
```

## What the requirement records contain

`requirements.json` is generated, but it is committed so the backlog is reproducible and
auditable. Regenerate it rather than editing it by hand.

Two fields are derived rather than quoted, and should be read as such:

- **`phase`** — the delivery milestone. §18 of the specification pins most requirements
  explicitly; for the 41 it does not name, the phase is inferred by area in
  `phase_for()`. No inference contradicts §18, but an inferred milestone is a working
  assumption, not a statement of the specification.
- **`has_ac`** — whether the specification states mechanised acceptance criteria. Drives
  the `needs-ac` label. Requirements without acceptance criteria need them authored
  before implementation starts.
- **`ac_exempt`** — set by an `**AC exempt:**` paragraph. See below.

## How acceptance criteria are recognised

Three forms, all matched literally on the marker at the start of a line:

| Marker | Effect |
|---|---|
| `**AC:**` | Criteria for the requirement it follows. Clears `needs-ac`. |
| `**AC (all):**` | Criteria for every table-row requirement in the same `###` section. |
| `**AC exempt:**` | The requirement cannot be mechanised. Clears `needs-ac`; sets `ac_exempt`. |

`**AC (all):**` exists because §6.2 states one set of criteria for a whole table of
performance targets. A row requirement's body is only its own table cell, so without this
the shared criteria would belong to no record and all seven targets would parse as
un-mechanised while the specification plainly states them. The paragraph is copied into
each row's body, so every issue carries its criteria rather than referring to a section.

`**AC exempt:**` is for requirements no built artefact can be measured against —
`REQ-PLN-001` ("Phases 0–4 are the MVP") governs how the plan may change, not how the
product behaves. Marking it explicitly keeps `needs-ac` meaning "criteria are owed" rather
than quietly mixing in requirements for which criteria are impossible. Use it sparingly:
a requirement that is merely awkward to test is owed criteria, not an exemption.

## Issue titles

A title is `REQ-<AREA>-<nnn>: <name>`. `sync_issues.py` matches an issue to its
requirement on the ID prefix alone, so the name after the colon can be rewritten freely.

Most names come from the specification's own `**\`REQ-X-001\` (P0) — Short name.**` form.
Requirements stated as a bare sentence, or as a table row longer than the title cap, would
otherwise break off mid-clause; those have hand-written names in `TITLE_OVERRIDES` in
`parse_requirements.py`. Add an entry there rather than editing an issue title by hand,
or the next sync will revert it. Naming a requirement that does not exist is an error, so
a typo or a renamed requirement fails the parse rather than passing silently.

Prose that qualifies an already-defined requirement without restating its priority — for
example the paragraph beginning ``**`REQ-NFR-037` needs a qualification…`` — is appended
to that requirement's record. For the §15 table requirements this prose carries the
normative wording and the acceptance criteria, so it must not be dropped.

## Hand-maintained issues

`sync_issues.py` matches an issue to a requirement on its `REQ-<AREA>-<nnn>:` title prefix
and skips anything that does not match, reporting `no matching requirement — skipped`. That
makes hand-written tracking issues safe to keep alongside the generated backlog.

Issue **#387, "Phase follow-up"**, is one such: it collects open items raised while
authoring acceptance criteria that do not block the phase they came from. Append to it
rather than opening a new issue. Cross-links from requirement issues are *comments*, not
body edits — the generator rewrites bodies and would discard them, but leaves comments
alone.

## Concurrency

`create_issues.py` takes an exclusive lock (`tools/.create_issues.lock`) and writes its
issue body to a process-unique temp file. Both matter: an earlier version had neither, and
two overlapping runs produced 138 duplicate issues plus three issues whose body belonged
to a different requirement. If a run is killed, delete the stale lock file by hand.
