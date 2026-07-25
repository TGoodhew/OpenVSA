# OpenVSA

An open, from-scratch vector signal analyser for Windows — a behavioural reimplementation of
the Keysight/Agilent 89600 VSA software line (89601A/B, now PathWave VSA).

> **Status: Phase 0, scaffold only.** No measurement functionality is implemented yet. Work is
> tracked as GitHub issues, one per requirement, grouped into milestones by delivery phase.

## What this is

A **behavioural** clone, not a source clone. Someone familiar with the 89600 VSA should find
the same measurement model, the same trace/format separation, the same demodulation setup
vocabulary and numerically comparable results.

The full requirements specification lives in [requirements/](requirements/) and is the
authoritative source for everything below.

### Explicitly not goals

- Reproducing Keysight source, proprietary file-format internals, or undocumented DSP.
- Licence-file or entitlement interoperability. The licensing model here is *analogous*, never
  interoperable.
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
