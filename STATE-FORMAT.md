# The state format

`REQ-STA-003`. A saved setup is human-readable, diffable, versioned JSON. The reference product
uses an opaque binary `.setx`; there is no interoperability requirement, and text is worth a great
deal for the things setups are actually used for — putting a measurement under version control,
sending one to somebody who cannot reproduce a result, and reading what a file says when the
software that wrote it will not load it.

## Files

| Extension | Contents | Written by |
|---|---|---|
| `.ovsa-state.json` | A setup: every measurement context's settings | `StateFile` |
| `.ovsa-math.json` | Trace-math definitions | `SidecarFile` |
| `.ovsa-registers.json` | Data registers | `SidecarFile` |
| `.ovsa-display.json` | Display preferences | `SidecarFile` |
| `.ovsa-recording.json` | The loaded recording and its position | `SidecarFile` |

The last four are `REQ-STA-002`'s exclusions. They are **not** in a state, and the save dialog says
so in its own text rather than leaving it to be discovered. Their lifetimes are separate: a setup
describes how to measure, a recording is data, a register holds a measurement already taken, math
is an analysis over both, and display preferences belong to the person rather than the measurement.
Folding them in would mean recalling a colleague's setup repainted your display and discarded the
reference trace you were comparing against.

## Versioning

The container carries `schemaVersion`; the measurements do not carry their own. There is therefore
one answer to "what shape is this file", and migration has one place to happen.

- **Current schema version: 4.**
- **Oldest readable: 1.**

A file with no `schemaVersion` is refused as not being an OpenVSA state. A file older than the
oldest readable version is refused by name, saying which versions this software does read — not by
a generic parse failure some way downstream of the cause.

### Adding a schema version

1. Add the new members to the model in `OpenVSA.Measurement/State/`, with a documented default on
   each. `REQ-STA-005`'s test walks the model, so a member without a default fails it.
2. Increment `ApplicationState.CurrentSchemaVersion`.
3. Add a `case` to `StateFile.Migrate` transforming a version *n* document into a version *n+1*
   one. Migrations run one step at a time in order, so a file two versions old goes through the
   same transformations a file one version old did; a migration never has to know about every
   version before it.
4. Add a row to the table below.
5. Only raise `OldestReadableSchemaVersion` when a migration genuinely cannot be written. Refusing
   to read an old setup is a real cost to somebody.

| From | To | Change |
|---|---|---|
| — | 1 | Initial schema. |
| 1 | 2 | Each measurement carries `demod`: the digital demodulator's format, symbol rate, filters, window lengths and equaliser settings. A version 1 file has none and the model's defaults supply them, so the migration transforms nothing. |
| 3 | 4 | `demod` carries `bitMapping` and, for a user-defined constellation, its definition as `customRings` or `customPoints` (`REQ-DEM-011`). A version 3 file has none: the natural mapping is what its formats meant, and no definition means a format from the catalogue. The migration transforms nothing. |
| 2 | 3 | `demod` carries `differentialReference`: what a symbol's bits are read against (`REQ-DEM-012`). A version 2 file has none, and the default — follow the format — is what such a file meant, so the migration transforms nothing. |

## Forward compatibility

**Unknown members survive a round trip, at any depth.** A file written by later software, loaded
here and saved again, comes back with its unrecognised members exactly as they were — including
members added inside a measurement, inside a nested object, or inside an element of an array.

Without this an older build is a one-way door: opening a colleague's setup would silently discard
everything it did not understand, and the loss would only surface later, on their machine.

The mechanism is a diff. On load, the document is deserialised into the model and the model is
re-serialised; whatever is in the file and not in that re-serialisation is a member from a later
schema, and is kept as text on `ApplicationState.UnknownMembersJson`. On save it is merged back.
Arrays are compared element by element, because a state's arrays are positional — the third trace
is the third trace — and treating them as opaque would lose everything a later schema added to any
of them.

## Contexts

`REQ-STA-004`. A multi-measurement state is matched to the application's contexts **by name**, and
the whole recall is validated before any of it is applied. Matching by position would apply one
measurement's settings to another whenever contexts had been reordered, and would do it silently.
Applying what matches and reporting the rest is worse still: it leaves the instrument in a
configuration that was never saved and that nobody chose.

Names are compared ordinally, not culture-aware — a context called `I` must not match one called
`ı` because the state happened to be recalled on a Turkish system.

On mismatch, `ContextMismatchException` names the contexts that did not match and what the
application has, because "recall failed" tells the user nothing they can act on.

## Presets

`REQ-STA-005`. The factory preset is the model's own defaults, constructed fresh — not a second
list of them that could drift. User presets are state files in a directory
(`%AppData%\OpenVSA\Presets`), so a preset can be copied to another machine, put in version
control, or sent to somebody. Applying one is recalling the state it was captured from, through
the same code path.

Neither disturbs the hardware setup (`REQ-UI-061`), structurally: a state carries no front end, no
resource string and no connection.
