# OpenVSA

An open, from-scratch vector signal analyser for Windows — a behavioural reimplementation of
the Keysight/Agilent 89600 VSA software line (89601A/B, now PathWave VSA).

> **Status: Phase 0.** The first measurement runs end to end: pick the simulated source under
> **Hardware**, then **Acquisition → Start**, and a live calibrated spectrum is displayed. Most
> of the product is still absent — no markers, no demodulation, no real front end, one fixed
> measurement setup. Work is tracked as GitHub issues, one per requirement, grouped into
> milestones by delivery phase.

## What this is

A **behavioural** clone, not a source clone. Someone familiar with the 89600 VSA should find
the same measurement model, the same trace/format separation, the same demodulation setup
vocabulary and numerically comparable results.

The full requirements specification lives in [requirements/](requirements/) and is the
authoritative source for everything below.

### Explicitly not goals

- Reproducing Keysight source, proprietary file-format internals, or undocumented DSP.
- Feature gating of any kind. OpenVSA is **one edition, free, with everything in it** — no
  licence files, no entitlements, no activation, no paid tier (`REQ-LIC-010`). The reference
  product's option SKUs appear in the specification only to explain its documentation.
- Metrological traceability. OpenVSA reports what the front end delivers; absolute amplitude
  accuracy is a property of the instrument.
- Real-time streaming with hard latency guarantees. Like the reference product, OpenVSA is a
  **block-based, non-real-time analyser** — an architectural decision, not a shortcoming.

## The central architectural idea

Acquisition and analysis are strictly separated. The analysis engine consumes nothing but a
stream of complex samples plus acquisition metadata (`IqBlock`), so it runs identically against
live hardware, a recorded file, or the simulator — and cannot tell which it is talking to.

This is `REQ-ARC-001`, and it is enforced by automated tests in
[tests/OpenVSA.Architecture.Tests](tests/OpenVSA.Architecture.Tests), not by convention.

```
L6  Automation        OpenVSA.Api            COM + .NET + SCPI
L5  Presentation      OpenVSA.Ui             WPF, MVVM
L4  Orchestration     OpenVSA.Measurement    contexts, trace graph, markers, limits
L3  Analysis          OpenVSA.Dsp            OpenVSA.Demod
L2  Capture session   OpenVSA.Capture        block assembly, recording, playback
L1  Front-end HAL     OpenVSA.Hal            IFrontEnd and friends
L0  Transport         OpenVSA.Hal.Visa       OpenVSA.Hal.File   OpenVSA.Hal.Sim

    Signal synthesis  OpenVSA.Synthesis      modulation, impairments, bursts
```

`OpenVSA.Synthesis` sits outside the stack rather than in it. It generates signals and measures
impairments back out of samples, and it references `OpenVSA.Core` alone — so the simulated transport
can transmit a modulated signal without an analysis assembly appearing beneath it, and the analysis
stack's own tests can use it without a transport appearing inside them.

Four data sources are first-class: the Agilent **E4406A** over NI-VISA/GPIB, a **pluggable
instrument abstraction** for further VISA instruments, **file playback and recording**, and a
**simulated source** with controllable impairments.

## Platform constraints

These are fixed, and are recorded here because they look like free choices and are not:

| Constraint | Why |
|---|---|
| .NET Framework 4.7.2, C# 7.3 | Required to consume the IVI/NI-VISA .NET assemblies. Consequences: portable-only `Span<T>`, no `System.Runtime.Intrinsics`, no `MathF`, no async streams — hence the pull-based `AcquireNextAsync` HAL contract. |
| x64 only, `gcAllowVeryLargeObjects` | A 30 s capture at 25.6 MS/s of `Complex32` is 6.1 GB. Even so, a single `float[]` tops out near 8.6 GB, so long captures must be chunked. |
| WPF | Mandated. The plot surface is the main technical risk; see RISK-03. |

## Building

Requires Visual Studio 2022+ (or Build Tools) with the .NET Framework 4.7.2 targeting pack.
Full MSBuild is needed rather than `dotnet build`, because the WPF targets for .NET Framework
ship only with MSBuild.

```powershell
msbuild OpenVSA.slnx /restore /p:Configuration=Debug /p:Platform=x64
```

The application runs with **no hardware and no VISA installed** (`REQ-NFR-032`), against the
simulated source and file playback. That is an architectural constraint, not a convenience —
it is what makes the DSP developable, testable in CI and demonstrable.

## Setup

### Syncfusion licence key

The shell uses Syncfusion WPF controls for its generic furniture — docking, grids, editors,
ribbon. They require a licence key registered at start-up, or they display an unlicensed banner.

**A key is not needed to build or run.** Without one the application launches in trial mode and
says so. Nothing is gated: OpenVSA ships as one free edition with everything included, and the
key is a build-time step for contributors, never a gate on anything a user receives.

To remove the banner, get your own key — a **free Community licence** is available from
Syncfusion for qualifying individuals and organisations — and supply it either way.

#### Getting a key

The licence is yours, not the project's: OpenVSA cannot supply one, and a key committed here
would be a leaked credential (see the warning below).

1. **Check you qualify.** At the time of writing Syncfusion's
   [Community License](https://www.syncfusion.com/products/communitylicense) is free for
   individuals, and for organisations with **under $1M USD annual gross revenue, 5 or fewer
   developers, and 10 or fewer total employees**, that have never taken more than $3M USD from
   an outside source such as private equity or venture capital. Non-profits under a $1M USD
   annual budget also qualify. **These terms are Syncfusion's and they change — read the linked
   page rather than this paragraph**, which is a signpost and not the licence.
2. **Register.** Create a Syncfusion account from that page. Registering through the Community
   License link routes you to the request form automatically. Syncfusion raises a ticket to
   verify eligibility; a LinkedIn or Xing profile speeds that up.
3. **Generate the key** once approved, from **License & Downloads** in your Syncfusion account
   (**Trial & Downloads** if you are on a trial). Pick the edition and generate against it.
4. **Match the version.** Keys are **version- and edition-specific**. This repository pins
   Syncfusion at the version recorded in [DEPENDENCIES.md](DEPENDENCIES.md) — generate the key
   for that major version, or registration succeeds and the banner appears anyway. From v31.1.17
   Syncfusion moved from per-platform to per-edition keys, so a key generated for an older
   release under the previous scheme will not do.

Then supply it either way:

**Environment variable** (takes precedence):

```powershell
[Environment]::SetEnvironmentVariable('SYNCFUSION_LICENSE_KEY', '<your key>', 'User')
```

**Or a local config file:**

```powershell
copy src\OpenVSA.Ui\local.secrets.config.example src\OpenVSA.Ui\local.secrets.config
# then edit it and replace the placeholder with your key
```

> **The key must never be committed.** This repository is public, so a key in the tree is a
> leaked credential regardless of what it costs. `local.secrets.config` and `*.secrets.config`
> are git-ignored; only the `.example` template is tracked. Do not put the key in `App.config`,
> which *is* tracked.

### Building the installer

```powershell
.\tools\build_installer.ps1 -Version 0.1.0 -EmbedLicenseKey
```

Produces `installer\OpenVSA.Installer\bin\x64\Release\OpenVSA.msi`: a per-machine MSI with a
Start Menu entry, a proper uninstall, and upgrade handling that replaces rather than accumulates.

- **`-EmbedLicenseKey`** reads `SYNCFUSION_LICENSE_KEY` from the build environment and embeds it,
  so the installed application shows no Syncfusion banner and **an end user needs no Syncfusion
  account**. The key is written only into `obj\`, which is git-ignored; it is never committed and
  never placed in the MSI as a file. Omit the switch and the build warns and produces an installer
  that runs in evaluation mode.
- **NI-VISA is not bundled.** It is a pre-installed dependency: its implementation is placed in the
  GAC by NI's own installer, and only a reference assembly exists in the package feed. The
  installer detects it and, if it is absent, **says so on the final page and installs anyway** —
  `REQ-NFR-032` requires OpenVSA to run with no VISA at all, against the simulated source and file
  playback, so refusing to install would contradict the software being installed.
- The script checks the MSI's own file table before it finishes, and fails if a
  `local.secrets.config`, an `Ivi.Visa` or a `NationalInstruments` file got in. A developer machine
  may have a real secrets file sitting in the payload directory, and a leaked key is not something
  to discover after publishing.

The installer project is **not** a member of `OpenVSA.slnx`, for the same reason the C++ FFT
project is not: the dotnet CLI cannot evaluate every project type, and `dotnet test OpenVSA.slnx`
has to keep working. The script builds both.

## Releases

Every release is a **prerelease** until the final phase of the requirements is complete; only
then does OpenVSA take a major version number. A release is cut at each phase boundary, when that
phase's milestone reaches zero open issues, CI is green and the full bench verification has run.

Newest first.

### v0.1.0 — Phase 0, Foundations (2026-07-29)

**The first release. There is no previous version to compare against**, so what follows is what
the release contains rather than what changed.

An installable, runnable vector signal analyser with the foundations proved: 49 requirements
closed, 1 952 unit tests, 94 of 94 bench features exercised against real hardware, 6 of 6
cross-validation scenarios.

**Architecture** — strict acquisition/analysis separation with the analysis stack buildable with
every transport absent (`REQ-ARC-001`); front ends interchangeable at run time and discovered
rather than referenced (`REQ-ARC-002`, `REQ-HAL-003`); measurement personalities as plug-ins
dropped into `Personalities\` with no rebuild of the host (`REQ-ARC-003`).

**DSP** — block-based analysis, double-precision accumulation, the window set with periodic
definitions and gain correction, a managed FFT and a native one behind one interface, min/max
pixel-column decimation, and bit-for-bit reproducible results.

**Signal generation** — a synthetic modulated source with deterministic seeding, twelve
controllable impairments, and burst/pulse scenarios.

**Shell** — docking layout, trace windows, the three-zone plot surface with in-place hot-spot
editing, a software rasteriser for trace geometry, per-monitor DPI awareness and a content scale
factor.

**Non-functional** — x64 with large-object support, bounded steady-state allocation with **zero
gen-2 collections over a ten-minute run at 2²⁰ points**, immutable snapshot hand-off to the UI,
back-pressure rather than unbounded buffering, a defined thread topology, structured logging with
support-bundle export, no telemetry or network egress, and a performance regression gate in CI.

**Known limitations, stated rather than omitted:**

- **Demodulation is not in this release.** `Phase 2` is where it arrives. The constellation, eye
  and symbol-table displays exist and are verified against generated signals, but nothing yet
  produces a live demodulated result from a hardware acquisition.
- **`REQ-NFR-007a` (window scale factor) is deferred**, not done: its acceptance criterion needs
  two monitors at different DPI scalings. The feature is implemented and unit-tested; the
  verification is not. Closed under `deferred:revisit`.
- **Two requirements were amended on measurement rather than implemented as written** —
  `REQ-NFR-005` withdrew the `D3DImage` rendering path (decimation makes the cost it defended
  against unreachable, and it degrades to software under RDP), and `REQ-NFR-003` replaced a fixed
  SIMD throughput ratio with a measured working-set sweep. Both retain the original text and the
  reasoning in the specification.
- **NI-VISA is a prerequisite for hardware**, not bundled. Without it OpenVSA still runs against
  the simulated source and file playback.

## Verification

DSP defects are quiet: the software produces a plausible number that is wrong, and nobody
notices for months. The test strategy is built around making wrongness loud.

- **Analytic unit tests** — every DSP primitive checked against a closed-form result, never
  against a previous run of itself.
- **The impairment round-trip matrix** — for every modulation format and every impairment,
  inject a known magnitude and assert the corresponding metric recovers it. This closes the
  loop generator → channel → demodulator → metric and is the primary correctness proof.
- **Hardware cross-validation** — OpenVSA's flexible demod compared against the E4406A's own
  W-CDMA, EDGE and cdmaOne personality measurements. The only truly independent reference
  available on the bench.

## Licence

MIT. See [LICENSE](LICENSE).

**Note on intellectual property:** this project clones documented *behaviour and interfaces*
only. It does not decompile the reference product, and does not reproduce proprietary
file-format internals or trade dress.
