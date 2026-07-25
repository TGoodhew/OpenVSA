# Requirement-tracking tools

One-off authoring aids that turn [`requirements/OpenVSA-Requirements.md`](../requirements/OpenVSA-Requirements.md)
into the GitHub issue backlog. They are **not part of the product build** and are not
referenced by `OpenVSA.slnx`. Python 3.8+ and an authenticated `gh` CLI are required.

## Scripts, in the order they are used

| Script | Purpose |
|---|---|
| `parse_requirements.py` | Parses the specification into `requirements.json`, one record per `REQ-<AREA>-<nnn>`. Deterministic: same input always yields byte-identical output. |
| `create_issues.py` | Creates labels, milestones and one issue per requirement. Idempotent — skips requirements that already have an issue. |
| `sync_issues.py` | Reconciles existing issues with `requirements.json`, repairing drifted bodies, labels and milestones. Run after re-parsing. |
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

Prose that qualifies an already-defined requirement without restating its priority — for
example the paragraph beginning ``**`REQ-NFR-037` needs a qualification…`` — is appended
to that requirement's record. For the §15 table requirements this prose carries the
normative wording and the acceptance criteria, so it must not be dropped.

## Concurrency

`create_issues.py` takes an exclusive lock (`tools/.create_issues.lock`) and writes its
issue body to a process-unique temp file. Both matter: an earlier version had neither, and
two overlapping runs produced 138 duplicate issues plus three issues whose body belonged
to a different requirement. If a run is killed, delete the stale lock file by hand.
