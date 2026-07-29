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
```

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
