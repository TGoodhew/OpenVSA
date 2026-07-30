# Requirements Specification — Open Vector Signal Analyzer (OpenVSA)

**A from-scratch reimplementation of the Keysight/Agilent 89600 Vector Signal Analysis software**

| Field | Value |
|---|---|
| Document ID | OPENVSA-SRS-001 |
| Revision | 1.0 (initial issue) |
| Date | 25 July 2026 |
| Target platform | Microsoft .NET Framework 4.7.2, x64 |
| Language | C# 7.3 |
| UI framework | WPF (XAML, MVVM) |
| Instrument I/O | NI-VISA (`NationalInstruments.Visa` / `Ivi.Visa` .NET assemblies) |
| Reference product | Keysight 89600 VSA software (formerly Agilent 89601A/B; now "PathWave Vector Signal Analysis") |
| Primary bench hardware | Agilent E4406A VSA Series Transmitter Tester, GPIB primary address 17 |

---

## 0. How to read this document

Requirements are identified as **`REQ-<AREA>-<nnn>`** and carry a priority:

| Priority | Meaning |
|---|---|
| **P0** | Must be present for the product to be usable at all. Blocking. |
| **P1** | Required for feature parity with the reference product's core value. |
| **P2** | Required for full parity, but deferrable behind a phase boundary. |
| **P3** | Desirable; parity nicety or convenience. |

Every P0/P1 requirement carries **Acceptance Criteria (AC)** written so a test can be
mechanised. Where a numeric value is quoted from Keysight documentation it is marked
**[V]** (verified against a cited source) or **[U]** (unverified — the research pass could
not confirm it; see §17 *Open Questions*). Do not treat **[U]** values as settled: they are
placeholders that must be confirmed before they are used as pass/fail thresholds.

Mathematics is given in LaTeX. Where the reference product's exact internal algorithm is
not publicly documented, this specification **selects a defensible algorithm** and says so
explicitly, rather than pretending to reproduce an unknown one. Those choices are flagged
**[DESIGN CHOICE]**.

---

## 1. Purpose and scope

### 1.1 Purpose

This document specifies the functional and non-functional requirements for **OpenVSA**, a
ground-up reimplementation of the Keysight 89600 VSA software on a modern managed
Windows stack. The goal is a *behavioural* clone: a user familiar with the 89600 should be
able to sit down at OpenVSA and find the same measurement model, the same trace/format
separation, the same demodulation setup vocabulary, and numerically comparable results.

It is explicitly **not** a goal to reproduce Keysight's proprietary source, file-format
internals that are not publicly documented, or their exact undocumented DSP
implementations. Where public documentation stops, this specification substitutes
published, standard signal-processing technique and says where the substitution occurred.

### 1.2 Scope decision

Following scoping discussion, the agreed target is:

- **Full functional clone including standard-specific measurement personalities.** Not
  merely the core analyser plus generic demodulation, but the complete product shape:
  base vector signal analysis, the flexible (format-agnostic) digital demodulator, *and* a
  personality framework hosting standard-specific measurements (GSM/EDGE, W-CDMA,
  cdma2000, LTE, 5G NR, WLAN, and others).
- **Implementable engineering specification.** DSP algorithms with mathematics, data
  models, C#/WPF module and class breakdown, threading model, and per-requirement
  acceptance criteria.
- **Four data sources**, all first-class:
  1. Agilent **E4406A** over **NI-VISA / GPIB** (the bench instrument at GPIB 17);
  2. a **pluggable instrument abstraction** so further VISA instruments can be added
     without touching the DSP core;
  3. **file playback and recording** of IQ captures;
  4. a **simulated signal source** producing synthetic modulated IQ with controllable
     impairments, for development, demonstration and self-test.

Because the scope is a full clone, the specification is organised so that the *engine* is
built once and personalities are added incrementally as plug-ins (§12). The phased delivery
plan in §18 sequences this; nothing in this document should be read as requiring all
personalities to exist at first release.

### 1.3 A necessary naming correction

The request referred to "the 89600A software." Research shows this conflates two distinct
things, and the distinction matters because it determines what is actually being cloned:

- The **89600 Series** was a *hardware* family — VXI-based modular vector signal analysers
  (89610A/89611A baseband, 89640A/89641A RF to 2.7/6 GHz, and the 89600S system variants),
  successors to the earlier **89400 series** benchtop analysers (89410A, 89440A, 89441A). In
  the 89400 generation the flexible demodulator was instrument **firmware**, sold as
  **Option AYA**. **[V]**
  *Note:* there is no product literally designated "89600A" — the request's phrasing appears
  to blend the 89600 hardware series with the 89601 software. Both readings are addressed
  here, and the software is what is specified.
- **89601A** (circa 2000), later **89601B** (October 2010, a 64-bit .NET rewrite), and today
  **PathWave VSA**, is the *PC software* — the thing that decoupled vector signal analysis
  from dedicated VSA hardware and turned connected instruments into interchangeable
  front ends. **[V]**

**This document specifies a clone of the software line (89601A/B, "89600 VSA software"),**
not of the 89600A hardware. Throughout, "the reference product" means that software.

### 1.4 Out of scope

- Feature gating of any kind. OpenVSA ships as a **single edition with every feature
  available to every user** (§16). It has no licence files, licence server, entitlement
  cryptography, activation step or paid tier — neither Keysight's nor an analogous mechanism
  of its own. The reference product's option SKUs are described in this document only to
  explain what its documentation means, never as a structure to reproduce.
- Calibration of, or metrological traceability for, connected hardware. OpenVSA reports
  what the front end delivers; absolute amplitude accuracy is the instrument's property.
- Real-time streaming analysis with hard latency guarantees. Like the reference product,
  OpenVSA is a **block-based, non-real-time analyser** (§9.1) — this is an architectural
  decision, not a limitation to be engineered away.
- Manufacture of instrument drivers for hardware the team cannot test against.

---

## 2. Product understanding — what the 89600 actually is

This section records the understanding derived from Keysight's published documentation.
It exists so that a developer who has never used the product can make correct decisions
without re-reading the source material.

### 2.1 The central architectural idea

Keysight's own literature draws a hard line between acquisition and analysis. The
measurement front end is responsible for "connection to the device under test, signal
digitizing, signal capture capability, and data transfer to the PC in a sequential stream of
data blocks." The software is described as "fundamentally a digital system that uses data
and mathematical algorithms to perform analysis. All it requires is sampled data from an
instrument, software, or digital bus." **[V]**

Everything distinctive about the product follows from that one split:

- The same analysis engine runs identically against live hardware, a recorded file, or a
  simulator link — because all three reduce to *a stream of complex samples plus
  acquisition metadata* (centre frequency, sample rate, timestamp, reference level).
- Front ends are interchangeable. The reference product claims "over 45" supported
  Keysight instruments spanning signal analysers, oscilloscopes, logic analysers, PXI
  digitizers and AWGs, plus third-party hardware via a driver SDK. *(Marketing figure,
  varies by release — cited for the shape of the claim, not as an engineering constant.)*
- Instruments that have no demodulation capability of their own gain the full
  demodulation suite simply by acting as a digitizer. This is precisely why an E4406A —
  whose own firmware offers only standards-locked constellation displays inside licensed
  personalities — can produce a fully general constellation when driven by the software.

**This split is the single most important thing to preserve in the reimplementation.**
`REQ-ARC-001` makes it binding.

### 2.2 Acquisition parameter model

The reference product exposes two overlapping vocabularies — legacy FFT-analyser terms
and demodulation terms — over one underlying sampled-IQ pipeline. The documented
relationships are: **[V]**

| Relationship | Formula | Status |
|---|---|---|
| Sample rate from span (zoom / IF path) | $F_s = 1.28 \times \text{Span}$ | **[V]** legacy decimation-chain artefact |
| Sample rate from span (baseband path) | $F_s = 2.56 \times \text{Span}$ | **[V]** real baseband input |
| FFT size from displayed points — **zoom** | $N_{\text{FFT}} = 1.28 \times (N_f - 1)$ | **[V]** |
| FFT size from displayed points — **baseband** | $N_{\text{FFT}} = 2.56 \times (N_f - 1)$ | **[U]** — see note |
| Max time-record length | $T_{\max} = N_{\text{FFT}} \cdot \Delta t = (N_f - 1)/\text{Span}$ | **[V]** |
| RBW from record length | $T_{\text{rec}} = \mathrm{ENBW_{norm}} / \mathrm{RBW}$ | **[V]** window-dependent |

> **Consistency note on the baseband row.** Keysight's documentation gives the
> $N_{\text{FFT}} = 1.28(N_f-1)$ relation for *zoom* data only. Applying it unchanged to the
> baseband path would break $T_{\max} = (N_f-1)/\text{Span}$: with $N_f = 801$ and a 10 MHz
> span it yields $1024/25.6\,\mathrm{MHz} = 40\,\mu s$, not the 80 µs the identity requires.
> The $2.56\times$ form above restores consistency and is the algebraically necessary
> companion to $F_s = 2.56\,\text{Span}$, but it is an **inference**, not a quoted
> relationship. Confirm before relying on it (§20, Q12).

Worked example from the documentation: a 10 MHz span gives 12.8 MHz (zoom) or 25.6 MHz
(baseband) sample rate; $N_f = 801$ points gives $N_{\text{FFT}} = 1024$ and
$T_{\max} = 80\,\mu s$. A Hann window (normalised ENBW 1.5 Hz·s) at 100 kHz RBW gives a
15 µs record. **[V]**

Note the **inversion between measurement classes**: in spectrum measurements the user
sets RBW and the system derives record length; in digital demodulation the user sets span
and *Main Time Length* and the system derives RBW ($\mathrm{RBW} = \mathrm{ENBW_{norm}}/T$,
e.g. a 50 ms record with a Hann window gives 30 Hz). **[V]** OpenVSA must reproduce this
directional coupling (`REQ-DSP-020`).

### 2.3 Trace data versus trace format

A defining UI concept: **what is measured** and **how it is drawn** are orthogonal and
independently selectable per trace. One acquisition can be displayed simultaneously as a
log-magnitude spectrum, an IQ polar diagram, and an unwrapped phase plot, in three
different trace windows, with no re-acquisition. **[V]**

- **Trace *Data*** — Spectrum, Raw Main Time, Instantaneous Main Time, PSD,
  Autocorrelation, CCDF/CDF/PDF, Correction, Math, cross-channel results (Cross Spectrum,
  Cross Correlation, Coherence, Frequency Response, Impulse Response), and — when a demod
  measurement is active — the demodulation result traces.
- **Trace *Format*** — Log Magnitude, Linear Magnitude, Real, Imaginary, Wrapped Phase,
  Unwrapped Phase, Group Delay, IQ (polar/constellation), Eye, Spectrogram, and
  persistence/cumulative-history renderings.

This separation is mandated by `REQ-TRC-001` and is the backbone of the display data model
in §7.4.

### 2.4 The flexible demodulator

Sold today as **89601AYAC "Digital Demodulation Analysis"**, historically Option AYA. It
demodulates arbitrary formats given carrier frequency, symbol rate and filter description —
"over 40 digital modulation formats … types as simple as BPSK or as complex as 4096 QAM."
**[V]** Its distinguishing behaviours, all of which OpenVSA must reproduce:

- **The symbol clock is not estimated.** The user must supply the symbol rate exactly; the
  documentation states the rate "must be set to match exactly … because the symbol clock
  frequency is not estimated." A wrong rate produces the diagnostic signature of *EVM small
  at the centre of the result length and growing linearly toward both ends*. **[V]** This
  is a superb acceptance test and is used as one in `REQ-DEM-030`.
- **Block-based, not loop-based.** Analysis operates over a finite *Result Length* of
  symbols located within a larger *Search Length*, permitting non-causal, whole-block
  maximum-likelihood parameter estimation rather than streaming tracking loops.
- **Measurement and reference filters are separate.** The measurement filter is applied to
  the acquired signal (typically root-raised-cosine, matching the transmitter); the
  reference filter shapes the ideal waveform against which errors are computed.
- **Points per symbol does not affect EVM**, because EVM is evaluated only at symbol
  decision instants. Offset formats (OQPSK and relatives) are the documented exception,
  using two points per symbol because I and Q are offset by half a symbol. **[V]**

### 2.5 Bench context

The specific instrument this project must drive first is the E4406A at GPIB 17. The three
queries below **were executed live against the instrument** over NI-VISA during preparation
of this document, and the responses are verbatim:

```
*IDN?  ->  Hewlett-Packard,E4406A,US40062429,A.08.10    20041215  12:30:18
*OPT?  ->  "BAH","202","252","BAC","BAF","B7C"
:INST:SEL? -> BASIC
```

Decoding the options: **BAH** GSM, **202/252** EDGE (with GSM / retrofit), **BAC** cdmaOne,
**BAF** W-CDMA, **B7C** baseband I/Q inputs. **[V]**

**What was deliberately *not* done:** no measurement-configuration or waveform-fetch command
was sent, because the instrument was found in a user-selected state and changing it was out
of scope for a documentation exercise. That is why the IQ-retrieval SCPI in `REQ-E44-002`
remains **[U]** despite the instrument being reachable — it is an *unasked* question, not an
unanswerable one, and §20 Q1 is a short bench task rather than a research problem.

The installed option set directly sets personality
implementation priority in §18 — GSM/EDGE, cdmaOne and W-CDMA personalities can be
validated against the instrument's own native measurements, giving OpenVSA a free
cross-check that no simulator can provide.

---

## 3. Glossary

| Term | Definition |
|---|---|
| **ACP** | Adjacent Channel Power — power in offset channels relative to the carrier channel. |
| **CCDF** | Complementary Cumulative Distribution Function; $P(\text{inst. power} > \text{avg} + x\,\mathrm{dB})$ vs $x$. |
| **EVM** | Error Vector Magnitude — RMS magnitude of the symbol-instant error vector, as a percentage of a normalisation reference. |
| **ENBW** | Equivalent Noise Bandwidth of an FFT window, normalised in Hz·s. |
| **Front end** | Any source of complex samples: instrument, file, simulator. |
| **Main Time** | The acquired complex baseband time record for the current measurement. |
| **MER** | Modulation Error Ratio; the reference product names this quantity *SNR*. |
| **OBW** | Occupied Bandwidth. |
| **Personality** | A standard-specific measurement plug-in (LTE, W-CDMA, …). |
| **Result Length** | Number of symbols demodulated and displayed. |
| **Rho (ρ)** | Normalised correlated-power waveform quality factor, max 1.0. |
| **Search Length** | Window of signal, in symbols, searched for a sync pattern or burst. |
| **VISA** | Virtual Instrument Software Architecture — the instrument I/O API. |
---

## 4. System architecture

### 4.1 Layering

```
┌──────────────────────────────────────────────────────────────────────────┐
│  L6  Automation / Scripting        OpenVSA.Api  (COM + .NET + SCPI)      │
├──────────────────────────────────────────────────────────────────────────┤
│  L5  Presentation                  OpenVSA.Ui  (WPF, MVVM)               │
│      Trace windows · docking · dialogs · rendering · markers UI          │
├──────────────────────────────────────────────────────────────────────────┤
│  L4  Measurement orchestration     OpenVSA.Measurement                   │
│      Measurement contexts · sweep/arm state machine · averaging ·        │
│      trace graph · limit tests · personality hosting                     │
├──────────────────────────────────────────────────────────────────────────┤
│  L3  Analysis / DSP                OpenVSA.Dsp        OpenVSA.Demod      │
│      FFT · windows · resampling ·   │  formats · filters · sync ·        │
│      statistics · gating            │  equaliser · metrics               │
├──────────────────────────────────────────────────────────────────────────┤
│  L2  Capture session               OpenVSA.Capture                       │
│      Block assembly · metadata · recording · playback · buffer pool      │
├──────────────────────────────────────────────────────────────────────────┤
│  L1  Front-end abstraction (HAL)   OpenVSA.Hal   (IFrontEnd et al.)      │
├──────────────────────────────────────────────────────────────────────────┤
│  L0  Transport                     OpenVSA.Hal.Visa (NI-VISA)            │
│                                    OpenVSA.Hal.File · OpenVSA.Hal.Sim    │
└──────────────────────────────────────────────────────────────────────────┘
```

**`REQ-ARC-001` (P0) — Strict acquisition/analysis separation.**
Layers L3 and above shall have no compile-time reference to any assembly in L0/L1 other
than the HAL interface assembly. The DSP and measurement layers shall consume only
`IqBlock` values (§7.1) and shall be incapable of distinguishing a live instrument from a
file or simulator.
**AC:** The solution builds with `OpenVSA.Hal.Visa`, `.File` and `.Sim` removed from the
build, substituting a single stub front end; all DSP and measurement unit tests pass
unchanged. A static analysis rule (`NDepend` or a custom Roslyn analyser) fails the build on
any L3+ → L0/L1 reference.

**`REQ-ARC-002` (P0) — Front-end interchangeability at runtime.**
The active front end shall be selectable at runtime without restarting the application, and
a measurement configuration shall survive a front-end change wherever the new front end
can satisfy its parameters.
**AC:** With a spectrum measurement configured and running against the simulator, switching to
file playback and then to the E4406A retains measurement setup, trace layout, markers and
limit lines; only parameters the new source cannot honour are coerced, and each coercion
raises a user-visible event-log entry. The E4406A leg is exercised against the instrument on
the bench, not a mock, because coercion is precisely where a real front end's limits differ
from a simulated one's.

**`REQ-ARC-002a` (P0) — A demod measurement survives a front-end change.**
`REQ-ARC-002` shall hold for a demodulation measurement as well as a spectrum one.
**AC:** As `REQ-ARC-002`, with a demod measurement configured and running in addition to the
spectrum one; the demodulator's format, symbol rate and filter survive the change, and a
front end that cannot supply the sample rate the demodulator needs coerces it and says so.
*Split from `REQ-ARC-002`: the spectrum leg is provable now and the demod leg cannot be until
a demodulator exists. Tracked in the Needs Verification epic against the closed
implementation.*

**`REQ-ARC-003` (P1) — Personalities are plug-ins, not core code.**
Standard-specific measurements shall be delivered as discoverable plug-in assemblies
implementing `IMeasurementPersonality` (§12.1). No personality shall require modification
of L2–L4 code to be added.
**AC:** A new personality assembly dropped into `Personalities\` is discovered on next
launch, appears in the measurement-type selector, and runs — with no rebuild of the host.

### 4.2 Assembly breakdown

| Assembly | Responsibility | Key public types |
|---|---|---|
| `OpenVSA.Core` | Primitives, units, buffer pooling, event log | `Complex32`, `IqBlock`, `BufferPool`, `Frequency`, `Amplitude` |
| `OpenVSA.Hal` | Front-end contract & registry | `IFrontEnd`, `IFrontEndCapabilities`, `FrontEndRegistry`, `AcquisitionRequest` |
| `OpenVSA.Hal.Visa` | VISA transport, instrument drivers | `VisaSession`, `E4406AFrontEnd`, `ScpiFrontEndBase` |
| `OpenVSA.Hal.File` | Recording/playback front end | `FilePlaybackFrontEnd`, `RecordingWriter`, format readers |
| `OpenVSA.Hal.Sim` | Synthetic source | `SimulatedFrontEnd`, `ImpairmentModel` |
| `OpenVSA.Capture` | Session, block assembly, record store | `CaptureSession`, `Recording`, `RecordPlayer` |
| `OpenVSA.Dsp` | FFT, windows, filters, resampling, statistics | `FftProcessor`, `WindowFunction`, `Resampler`, `Averager` |
| `OpenVSA.Demod` | Flexible digital demodulation | `Demodulator`, `ModulationFormat`, `PulseFilter`, `Equalizer`, `ErrorMetrics` |
| `OpenVSA.Measurement` | Measurement graph, traces, markers, limits | `MeasurementContext`, `Trace`, `Marker`, `LimitTest` |
| `OpenVSA.Personality` | Personality SDK | `IMeasurementPersonality`, `PersonalityHost` |
| `OpenVSA.Ui` | WPF shell and views | `ShellWindow`, `TraceWindowView`, `PlotSurface` |
| `OpenVSA.Api` | Automation surface | `Application`, `Measurement`, `Display`, `Trace`, `Marker` |

### 4.3 Signal flow

```
 Front end ──▶ IqBlock (complex samples + metadata)
      │
      ├──▶ [optional] RecordingWriter ──▶ .ovsa / .mat / .csv / SDF
      │
      ▼
 CaptureSession ──▶ frame extraction (Main Time Length, overlap)
      │
      ├──────────────▶ Spectrum path:  window ─▶ FFT ─▶ magnitude/PSD ─▶ averaging
      │
      ├──────────────▶ Statistics path: |x|² ─▶ histogram ─▶ CCDF/CDF/PDF
      │
      └──────────────▶ Demod path:
                          resample to N pts/symbol
                       ─▶ coarse carrier & timing estimate
                       ─▶ measurement (matched) filter
                       ─▶ fine carrier/timing/phase refinement (iterated)
                       ─▶ symbol decisions ─▶ bit mapping
                       ─▶ reference regeneration through reference filter
                       ─▶ [optional] adaptive equaliser
                       ─▶ error metrics + demod result traces
                          │
                          ▼
                    Trace graph ──▶ format conversion ──▶ WPF render
```

---

## 5. Technology stack decisions

The mandated stack — .NET Framework 4.7.2, C#, WPF, NI-VISA — is workable for this
application but imposes specific constraints that must be designed around rather than
discovered late. This section records the decisions and their rationale.

### 5.1 Platform constraints and mitigations

**`REQ-NFR-001` (P0) — x64 only, large-object support enabled.**
The application shall build and ship as **x64 only** (`<PlatformTarget>x64`), and
`app.config` shall enable `<gcAllowVeryLargeObjects enabled="true"/>`.
*Rationale:* a 30-second capture at 25.6 MS/s of `Complex32` is 6.1 GB. 32-bit is
categorically unusable, and even on x64 the default 2 GB single-array ceiling is hit by a
single long recording.
*Ceiling, and it must be designed around:* even with `gcAllowVeryLargeObjects`, the maximum
element count for a non-`byte` array is **2 146 435 071** (0x7FEFFFFF). A single `float[]`
therefore holds at most ~1.07 G complex samples (≈8.6 GB). Captures above that **must** be
chunked across multiple arrays — `REQ-DAT-001`'s `SampleCount` is an `int` for exactly this
reason, and `REQ-REC-001` recordings must segment. *(Stated as rationale and not as
acceptance criteria: it is a consequence later phases must honour, and putting it inside the
criteria made this requirement unprovable until recordings existed.)*
**AC:** `new float[2_000_000_000]` (≈8 GB) succeeds on a machine with adequate RAM; the build
produces no AnyCPU or x86 output.

**`REQ-NFR-002` (P0) — Buffer pooling; bounded steady-state allocation.**
All IQ sample buffers on the acquisition and DSP hot paths shall be rented from a pool and
returned deterministically.

> **`ArrayPool<T>.Shared` is not adequate and must not be used for IQ buffers.** Its
> `MaxBufferSize` is 2²⁰ elements; anything larger is allocated fresh and **silently
> dropped** on `Return`, which defeats the requirement precisely for the buffers that matter.
> `ArrayPool<T>.Create(maxArrayLength, maxArraysPerBucket)` is itself capped at 2³⁰.
> OpenVSA shall therefore implement a **custom slab pool with an explicit maximum array
> length**, plus a separate strategy for buffers above that cap (`GC.AllocateArray` with
> `pinned:true` does not exist on 4.7.2 — use a manually-managed native slab via
> `Marshal.AllocHGlobal`, or accept LOH allocation with explicit compaction control).

**AC:** Over a 10-minute continuous simulated-source run at 20 frames/s, **DSP-attributable**
managed allocation is bounded and the DSP pipeline triggers no Gen-2 collections, measured
with an allocation profiler attributing by call site. *(Process-wide "zero Gen-2" is not a
realistic target in a WPF host and shall not be used as the criterion.)*

**`REQ-NFR-003` (P1) — SIMD-accelerated inner loops, with eyes open.**
Numeric kernels (window multiply, magnitude, power, complex multiply, FIR convolution)
shall use `System.Numerics.Vector<T>` where profiling shows benefit.

> **What .NET Framework 4.7.2 does *not* give you, and it matters here.**
> `Span<T>` from the `System.Memory` package is the **portable ("slow") span** — there is no
> `ByReference<T>` JIT intrinsic and bounds checks are not elided, so `Span<T>` indexing in a
> hot loop can be *slower* than raw `float[]` indexing. Also absent: `System.Runtime.Intrinsics`
> / `Vector128<T>`/`Vector256<T>` (no control over instruction selection), `MathF` (every
> `float` scalar op widens to `double` and back), `Math.FusedMultiplyAdd`, and `BitOperations`.
> `Vector<T>.Count` is not guaranteed to be 8 for `float` on this runtime.
> **Use `Span<T>` for API shape and safety, raw arrays with local bounds hoisting for hot loops.**

*(AC amended 2026-07-29 on measurement. The original read: "Window-multiply and magnitude
kernels demonstrate ≥2.5× throughput over the scalar reference on the target machine, measured
by BenchmarkDotNet — **or**, where `Vector<float>.Count == 4` on the target runtime, a
documented lower factor with the measured value recorded and a native-kernel fallback raised as
a decision item." It is retained here because what it got wrong is instructive.)*

**Why a single throughput ratio was the wrong instrument.** The original criterion asked for one
number at one size, and offered an escape clause conditioned on the **lane count**. Measured on
the reference machine at `Vector<float>.Count = 8` with hardware acceleration on, the escape
clause's condition is false and the target is missed anyway — but the lane count was never the
cause, and the two named kernels miss it in **opposite directions**:

| Working set | window multiply | magnitude squared |
|---:|---:|---:|
| 16 KiB | 5.37× | 0.93× |
| 64 KiB | 5.31× | 0.95× |
| 256 KiB | 4.90× | 1.02× |
| 1 MiB | 4.20× | 1.20× |
| 4 MiB | 3.07× | 1.43× |
| 16 MiB | 1.42× | 1.92× |

**Window multiply** vectorises extremely well — 5.37× where the data is cache-resident — and
decays to 1.42× as the working set leaves cache. Arithmetic width cannot help a loop waiting on
memory, and at 2²⁰ complex samples the interleaved buffer is 8 MB. **Magnitude squared** is
*slower than scalar* while cache-resident, because `REQ-DAT-003`'s interleaved layout forces
every vector to be squared and then folded pairwise across adjacent lanes; it only overtakes
scalar once both are memory-bound. A single figure at either end of that table misrepresents
both kernels, and a figure at 2²⁰ alone cannot distinguish a kernel that fails to vectorise from
one that vectorises perfectly and is waiting for memory — which want opposite responses.

**AC:** Each named kernel has a scalar and a `Vector<T>` form; they agree **exactly**, asserted
at sizes that straddle the lane count, because a vector kernel that is faster and wrong in its
tail is worse than none. The two forms are compared across a **working-set sweep spanning
cache-resident to memory-resident**, every figure recorded, re-runnable in one command
(`OpenVSA.Benchmarks --gate --kernels`). Selection between the forms is **driven by that sweep
and not asserted**: a kernel is vectorised in the product only where the sweep shows the vector
form faster at the working-set sizes the DSP pipeline actually runs at, and a kernel where it is
not is left scalar with its measured figures recorded. A vectorised kernel that the sweep shows
to be slower at pipeline sizes fails this criterion. A single-size comparison does not satisfy
it. No fixed speed-up ratio is required, because the achievable factor is a property of the
machine's memory system at these buffer sizes rather than of the code — where a ratio is wanted
for regression purposes it belongs in `REQ-TST-007`'s gate against a stored baseline, which is
what that gate exists for.

**`REQ-NFR-004` (P0) — FFT implementation choice.** **[DESIGN CHOICE]**
The FFT shall sit behind an `IFftProvider` interface with at least two implementations: a
fully managed default, and an optional native provider.
*Rationale and licensing:* FFTW is **GPL** unless a commercial licence is purchased — it must
not be linked into a closed-source product without that licence. Intel oneMKL and IPP, by
contrast, have been free to use and redistribute under the **Intel Simplified Software
Licence** since the oneAPI transition (2020) and are viable native options. A managed default
(Stockham or split-radix, or Math.NET Numerics) avoids the question entirely at some
performance cost. The interface makes the choice deployment-time, not design-time.
**AC:** At least two `IFftProvider` implementations are registered, one fully managed and one
native. A provider-parametrised suite runs the same forward/inverse round-trip and Parseval
checks against every registered provider and passes for each. Selecting the active provider
is a configuration change that recompiles no DSP code, demonstrated by running the suite
twice with different providers selected and the same binaries. The shipped default carries no
copyleft obligation, which `REQ-NFR-008` enforces.

**`REQ-NFR-004a` (P1) — FFT precision.** The reference provider shall be **double
precision**; a single-precision provider may be offered for throughput.
**AC:** Double provider satisfies Parseval to 1e-12 relative on a 1 M-point transform; single
provider to 1e-5. Cross-provider agreement is asserted at the tolerance of the *less* precise
provider, never at 1e-6 for a float32 path (accumulated float32 error at 2²⁰ points is
already ≈5e-7). Provider selection is configuration-driven; the shipped default carries no
copyleft obligation.

**`REQ-NFR-005` (P0) — WPF rendering strategy for large traces.**
*(Amended 2026-07-29 on measurement; see "What the measurement changed" below. The original
band table and its `D3DImage` interop clause are retained there rather than deleted, because
the reasoning that produced them is sound and only its premise turned out to be false.)*

Trace rendering shall **not** use WPF `Polyline`/`Path` elements with per-point geometry
above ~2 000 points. Drawn geometry shall be produced only after `REQ-NFR-006`'s min/max
pixel-column decimation, and the strategy for the geometry that survives decimation shall be:

| Drawn points | Strategy |
|---|---|
| ≤ 2 000 | `Polyline`/`Path` acceptable |
| ≤ ~20 000 | `DrawingVisual` + `StreamGeometry` |
| > ~20 000 | `WriteableBitmap` + software rasteriser |

*Rationale, stated precisely because the usual folk explanation is wrong:* the cost at high
point counts is **MilCore's anti-aliased geometry tessellation on the render thread, plus
per-`Point` managed→native marshalling** — not the visual tree as such. `StreamGeometry`
inside a `DrawingVisual` removes the per-*element* overhead but goes through the **same
tessellator**.

**What the measurement changed.** This requirement was written to defend against a cost that
`REQ-NFR-006` prevents, and the defence was more expensive than the threat.

- **Decimation runs first and bounds the drawn point count by the pixel width** — roughly twice
  it, so ~1 600 points at 800 px and ~7 700 on a 4K-wide window. A 2²⁰-point trace is drawn
  from at most a few thousand spans. The >20 000-point band is therefore unreachable in any
  window that fits on a display.
- **Rendering is 1.4 % of a frame and is invariant in point count** — 1.00 ms to rasterise a
  2²⁰-point frame against 924 µs for an 8 192-point one, because both have been decimated to
  one span per graticule column first. The whole 2²⁰ frame is 72.2 ms against `REQ-NFR-021`'s
  100 ms, of which **the FFT is 60.96 ms (84.4 %)**.

The original top band therefore offered `D3DImage` + a D3D9Ex shared-surface bridge
(`IDXGIResource::GetSharedHandle` → `IDirect3DDevice9Ex::CreateTexture` opened shared) as an
alternative to the software rasteriser, and §19 carried RISK-03 against it. **That alternative
is withdrawn, not merely deprioritised.** `D3DImage` degrades to software rendering under RDP
and without WDDM, so a design resting on it has no path in exactly the environments a bench
instrument is operated from — and it would be buying back 1.4 % of a frame. The software
rasteriser is the strategy, not the fallback, which also means the degraded case is the tested
case rather than an untested branch.

The original acceptance criterion also asked for a `DrawingVisual` + `StreamGeometry`
implementation to be **measured and recorded as failing** `REQ-NFR-021`. That clause is
withdrawn: `StreamGeometry` measures 95.8 updates/s at 100 000 points and does not fail as the
requirement assumed, and measuring it at a point count decimation makes unreachable would be
measuring a case that cannot occur. The band boundaries stand as engineering limits on drawn
geometry, and are honest about being limits rather than measured cliffs.

**AC:** The strategy selected for a trace is observable — a value a test can assert on, not a
branch inside a drawing method — and is tested either side of each band boundary (1 999/2 000/
2 001 and 19 999/20 000/20 001). Selecting per-point `Polyline`/`Path` geometry above 2 000
points fails the test, stated as a prohibition. Dropping to a *more* expensive strategy is
permitted at any size and dropping up to per-point geometry never is, so the asymmetry is
asserted in both directions. The product renders through the `WriteableBitmap` software
rasteriser, and no `D3DImage`, `HwndHost` or D3D9Ex shared-surface path is present — asserted
by a test over the shell's source, so reintroducing one is a deliberate act that fails the
build rather than a drift. The invariance that makes the top band unreachable is asserted
**structurally rather than by stopwatch** — at a fixed surface width the count of drawn spans is
identical for an 8 192-point and a 2²⁰-point trace, and is bounded by twice the pixel width —
because a timing ratio asserted in CI measures the runner's load as much as the code, while the
span count is the mechanism itself and is deterministic. The corresponding *timings* are
recorded by `REQ-TST-007`'s harness, where a stored baseline and a machine-class check make a
measurement meaningful.

**`REQ-NFR-006` (P0) — Pixel-column decimation.** Traces with more points than available
horizontal pixels shall be reduced by **min/max envelope decimation per pixel column**
(retaining both extrema per column), never by naive point-skipping.
*Rationale:* skipping points hides narrow spectral peaks and transients — the exact
features an analyser exists to reveal.
**AC:** A synthetic spectrum containing a single one-bin −60 dBc spur, rendered at 800 px
width from 524 288 points, visibly displays the spur at its correct amplitude; a
point-skipping implementation demonstrably does not.

**`REQ-NFR-007` (P1) — Per-monitor DPI awareness (V1).**
The application shall declare **per-monitor DPI awareness V1**, which WPF supports from .NET
Framework 4.6.2 via `app.manifest` plus the
`Switch.System.Windows.DoNotScaleForDpiChanges=false` configuration switch.

> **Do not specify PerMonitorV2 on this stack.** PMv2 is a Windows 10 1703+ awareness context
> whose behaviours (non-client-area scaling, child-HWND `WM_DPICHANGED` propagation, dialog
> scaling) WPF on .NET Framework does not implement — full PMv2 support arrived with .NET
> Core 3.0.
>
> *This note originally continued: "This interacts directly with `REQ-NFR-005`: an
> `HwndHost`/`D3DImage` plot surface will **not** receive child-window DPI-change notifications
> and must be recreated on DPI change. Budget for that." **That interaction no longer exists** —
> `REQ-NFR-005` was amended on 2026-07-29 to withdraw the `D3DImage` path, and the plot surface
> is a `WriteableBitmap` inside the WPF visual tree, which scales with it. The budget is
> released.*

**AC:** `app.manifest` declares per-monitor awareness V1 and does **not** declare PMv2, and
the `Switch.System.Windows.DoNotScaleForDpiChanges=false` switch is present — both asserted
by a test that reads the shipped manifest and configuration, so a later well-meaning edit to
PMv2 fails the build. Dragging the main window between monitors of differing DPI rescales
content without blurring and without layout loss, and a hosted `D3DImage`/`HwndHost` plot
surface is recreated at the new DPI with its trace and scaling intact.

**`REQ-NFR-007a` (P2) — Window scale factor.** Independently of monitor DPI, a user-settable
content scale factor over the range **0.8 to 2.0, default 1.0**, shall be provided (the
reference product exposes exactly this, bound to Ctrl+`+` / Ctrl+`-`). **[V]**
**AC:** Dragging the window between a 100 % and a 200 % display re-renders crisply without
restart; trace text stays legible and geometry correct; the plot surface is recreated cleanly
on DPI change with no leak.

### 5.2 NI-VISA binding

**`REQ-VISA-001` (P0) — Bind to `Ivi.Visa` only, never to a vendor assembly.**
`OpenVSA.Hal.Visa` shall reference **`Ivi.Visa.dll` alone** — the IVI Foundation **VISA.NET
Shared Components**, installed by NI-VISA and by Keysight IO Libraries alike — and shall open
every session through **`Ivi.Visa.GlobalResourceManager`**, letting the shared components
dispatch to whichever provider is registered. Direct references to
`NationalInstruments.Visa.dll` or `Keysight.Visa.dll` are **prohibited**. The VISA-COM interop
layer shall not be used on new code paths.

*Rationale:* referencing `NationalInstruments.Visa.dll` and its `ResourceManager` type
hard-binds the binary to NI-VISA and makes `REQ-VISA-002` unachievable — this is the most
common way vendor neutrality is lost in practice. `Ivi.Visa.dll` is authored by the IVI
Foundation, not by NI. VISA-COM additionally adds marshalling cost and an apartment-threading
hazard in a heavily multithreaded host.

**AC:** A `VisaSession` opens `GPIB0::17::INSTR` via `GlobalResourceManager`, performs
`*IDN?`, and returns the E4406A identification string. A dependency scan of the shipped
binaries shows `Ivi.Visa` and no vendor VISA assembly, and no `Interop.VisaComLib`.

**`REQ-VISA-002` (P1) — Vendor-neutral operation.**
The identical binary shall function against NI-VISA and against any other IVI-conformant
VISA.NET provider.
*Practical realities to design for:* vendor VISA.NET assemblies are strong-named and
version-locked per release, so **binding redirects are mandatory** in `app.config`; Keysight
IO Libraries gained VISA.NET provider support comparatively late (≈17.x/18.x), so older
installations may not qualify; and **side-by-side NI + Keysight VISA.NET installs are a known
conflict source**, so an explicit provider-selection setting shall be exposed rather than
relying on registration order. **[U]** — cross-vendor operation is an untested assumption
here and carries an open question (§20, Q13).
**AC:** The application connects to the E4406A on an NI-VISA machine and on a Keysight IO
Libraries machine using one binary, or the failure mode is documented and the provider
selector demonstrated.

**`REQ-VISA-003` (P1) — All VISA calls off the UI thread.**
No VISA operation shall execute on the WPF dispatcher thread. VISA sessions shall be
confined to a dedicated instrument thread or an affinitised task scheduler; sessions shall
not be shared concurrently across threads without explicit serialisation.
**AC:** A deliberate 30-second VISA timeout leaves the UI fully responsive, with an active
progress indication and a working Abort control.

**`REQ-VISA-004` (P1) — Binary block transfer.**
Bulk trace/IQ transfers shall use **IEEE 488.2 definite-length arbitrary block responses**
(`#<nd><len><payload>`, §8.7.9), with the data format selected by the **SCPI-99** commands
`FORMat:DATA REAL,32` and `FORMat:BORDer`, never ASCII.
*Rationale:* ASCII carries roughly 16–18 bytes per value against 4, so binary is about 4–5×
faster and far cheaper to parse. *(Note: ASCII at 8+ significant digits actually carries
**more** precision than `REAL,32` — throughput and parse cost are the reasons to prefer
binary, not accuracy.)*
**AC:** Bulk transfers issue `FORMat:DATA REAL,32` and set `FORMat:BORDer`, and the reader
parses the `#<nd><len><payload>` definite-length header correctly for header lengths across
the full 1–9 digit range. Byte order is honoured: a payload transferred under each
`FORMat:BORDer` setting decodes to the same values, which a hard-coded endianness fails. No
bulk path falls back to ASCII — a test fails on an ASCII bulk transfer. Measured throughput
is at least 4× the ASCII equivalent for the same record, per `REQ-NFR-027`'s measured figures
rather than a nominal one.

**`REQ-VISA-005` (P0) — Termination-character handling for binary reads.**
Read termination-character detection shall be **disabled**
(`MessageBasedSession.TerminationCharacterEnabled = false`) for the duration of any binary
block read.
*Rationale:* a 0x0A byte occurring inside a float payload will otherwise truncate the read.
This is the classic VISA binary-transfer defect and it presents as intermittent short reads
that look like instrument faults.
The reader shall additionally handle `#0` indefinite-length blocks and respect the 9-digit
(999 999 999-byte) definite-length ceiling, chunking above it.
**AC:** Measured transfer of a fixed record is ≥4× faster in binary than ASCII and agrees
numerically to float32 precision; a synthetic payload containing embedded 0x0A bytes reads
back complete and byte-exact.

### 5.3 Third-party dependency policy

**`REQ-NFR-008` (P2)** — Every third-party dependency shall be recorded in a
`DEPENDENCIES.md` with its licence and the justification for its use. Copyleft
(GPL/LGPL-static) dependencies shall not be introduced into shipped binaries without
written approval.
*Specifically flagged:* FFTW (GPL), any GPL HDF5 tooling, and MATLAB-file libraries with
restrictive terms.
**AC:** A CI check enumerates every package reference across the solution and fails the build
when one has no entry in `DEPENDENCIES.md`, when an entry omits its licence or its
justification, or when an entry in `DEPENDENCIES.md` names a package no project references
any longer. A dependency whose licence is GPL, or LGPL linked statically, fails the same
check unless its entry records a written approval.

---

## 6. Threading, concurrency and performance model

### 6.1 Thread topology

**`REQ-NFR-010` (P0) — Defined thread topology.**

| Thread / scheduler | Role | Constraints |
|---|---|---|
| **UI (dispatcher)** | WPF rendering, input, view-model updates | Never performs I/O or DSP. No blocking wait > 16 ms. |
| **Instrument thread(s)** | One per open VISA session; arm, trigger-wait, transfer | Owns the session exclusively. |
| **Acquisition pump** | Assembles `IqBlock`s, applies metadata, feeds pipeline & recorder | Bounded queue; drops with an explicit counter on overrun. |
| **DSP pool** | TPL Dataflow blocks / `Parallel.For` over frames and traces | Bounded parallelism = `Environment.ProcessorCount - 1`. |
| **Render marshal** | Converts completed trace data to render primitives | Produces immutable snapshots for the UI thread. |

**AC:** A thread-affinity assertion helper (`Assert.OnUiThread` / `Assert.NotOnUiThread`) is
present at every layer boundary and is active in Debug builds; the test suite includes a
run that fails on any violation.

**`REQ-NFR-011` (P0) — Immutable snapshot hand-off to UI.**
Trace results crossing into the UI thread shall be immutable snapshots. No UI code shall
read a buffer that a DSP thread may concurrently mutate.
**AC:** Under a 30-minute soak at maximum update rate with the UI actively resized and
markers dragged, zero torn-frame artefacts and zero data races are reported by a
concurrency-checked build.

**`REQ-NFR-012` (P1) — Back-pressure, not unbounded buffering.**
When analysis cannot keep pace with acquisition, the pipeline shall apply back-pressure or
deliberately drop frames, maintaining a visible dropped-frame counter. Unbounded queue
growth is prohibited.
**AC:** With an artificially slowed DSP stage, memory remains bounded and the UI reports a
monotonically increasing dropped-frame count.

### 6.2 Performance targets

These are targets for the reference development machine (an 8-core x64 workstation with
32 GB RAM). They are **product requirements**, not aspirations, and each is measurable.

| ID | Target | Priority |
|---|---|---|
| `REQ-NFR-020` | Spectrum, 8 192-point FFT, single trace, **rendered**: ≥60 updates/s sustained | P1 |
| `REQ-NFR-021` | Spectrum, 1 048 576-point FFT, **rendered with min/max decimation**: ≥10 updates/s | P1 |
| `REQ-NFR-022` | Flexible demod, 16-QAM, 4 096 symbols, 4 pts/symbol, equaliser off: complete analysis ≤50 ms | P1 |
| `REQ-NFR-023` | Flexible demod, 1024-QAM, 4 000 symbols, equaliser on (31 symbols): ≤400 ms | P2 |
| `REQ-NFR-024` | 20 simultaneous trace windows updating: aggregate ≥10 updates/s, UI input latency <100 ms | P1 |
| `REQ-NFR-025` | Cold start to first trace displayed (simulated source): ≤3 s | P2 |
| `REQ-NFR-026` | Playback of a 4 GB recording: sustained ≥1× real-time at the recorded sample rate | P2 |

*Note on `REQ-NFR-020`/`021`:* these are deliberately set **end-to-end including render**, not
FFT-only. An 8 192-point FFT is ~2 ms and a 1 M-point FFT ~50 ms even in managed code, so
compute-only versions of these targets would pass on day one and gate nothing.

*This note originally continued "The real risk is rendering (RISK-03), so the targets are
written to exercise it." **The measurement reversed that.** In a 2²⁰-point frame of 72.2 ms the
FFT is 60.96 ms (84.4 %) and rasterising is 1.00 ms (1.4 %), invariant in point count because
`REQ-NFR-006`'s decimation runs first. End-to-end remains the right shape for these targets —
a compute-only target would still gate nothing, and the render path must stay measured or it
stops being measured at all — but the budget they are defending is the transform. See
`REQ-NFR-005` and §19 RISK-03, both amended 2026-07-29.*

**AC (all):** An automated benchmark harness (BenchmarkDotNet plus a headless measurement
driver, and a windowed harness for the rendered targets) runs in CI and fails the build on a
>15 % regression against a stored baseline.

### 6.3 A warning about GPIB throughput

**`REQ-NFR-027` (P0) — Honest throughput expectations for GPIB front ends.**
The UI shall display the achieved transfer rate and the resulting maximum sustainable
duty cycle for the active front end.
*Rationale, and this must be understood before the E4406A driver is designed:* IEEE-488.1
sustains roughly 1 MB/s and HS488 up to about 8 MB/s. A `Complex32` sample is 8 bytes, so even
at an optimistic 8 MB/s a GPIB link carries only about 1 MS/s. **The E4406A is a 1996-era
instrument and does not support HS488** — realistic sustained throughput on this bench is on
the order of **100–300 kB/s**, i.e. roughly 12–40 kS/s of complex samples. Quoting the HS488
figure for this instrument would flatter the link by an order of magnitude or more.

Consequently the E4406A front end is in practice a **block capture** device: arm, capture into
instrument memory, transfer, analyse, repeat, with unavoidable dead time between blocks.

*But state the rule, not a blanket prohibition:* gap-free streaming is achievable precisely
when $8 F_s <$ measured link throughput — which at genuinely narrow spans it can be.
`SupportsGapFreeStreaming` shall therefore be **computed per acquisition plan from the
measured transfer rate**, not hard-coded `false` for all GPIB front ends.
**AC:** The status bar shows measured bytes/s and computed duty cycle; the capability is
evaluated per plan and reported; no UI affordance implies gap-free capture when the computed
value is false; the E4406A driver's advertised throughput reflects measurement, not the HS488
headline figure.
---

## 7. Core data model

### 7.1 The IQ block — the universal currency

```csharp
namespace OpenVSA.Core
{
    /// <summary>Interleaved single-precision complex sample buffer plus acquisition metadata.</summary>
    public sealed class IqBlock : IDisposable
    {
        // Interleaved I,Q,I,Q…, exactly 2*SampleCount elements. A METHOD, not a property
        // returning the pooled array — see REQ-DAT-001a, which prohibits raw public array
        // exposure on a pooled IDisposable. Throws ObjectDisposedException after Dispose.
        public Span<float> GetSamples();

        public int      SampleCount { get; }
        public double   SampleRateHz { get; }
        public double   CenterFrequencyHz { get; }
        public bool     IsBaseband { get; }      // true => real baseband; false => complex zoom/IF
        public double   FullScaleVolts { get; }  // ADC full scale referred to input, for absolute amplitude
        public double   ReferenceLevelDbm { get; }
        public long     SequenceNumber { get; }
        public DateTime AcquiredUtc { get; }
        public double   TriggerOffsetSeconds { get; }   // negative => pre-trigger
        public bool     TriggerCorrectionsApplied { get; }
        public FrontEndId Source { get; }
        public IReadOnlyDictionary<string,object> Extended { get; }  // per-front-end extras
        public void Dispose();                   // returns Samples to the pool
    }
}
```

**`REQ-DAT-001` (P0)** — `IqBlock` shall be the sole data type crossing the HAL boundary.
Every field above is mandatory for every front end; a front end that cannot supply a value
shall supply a documented, explicitly-flagged default rather than a silent zero.
**AC:** A conformance suite runs against every front-end implementation and asserts metadata
completeness and self-consistency: $F_s > 0$; **`GetSamples().Length == 2 × SampleCount`**;
centre frequency within the front end's declared range.

> *Why exact equality here, when a pooled array would forbid it.* `ArrayPool.Rent` returns an
> array of **at least** the requested size, so an earlier form of this criterion asserted
> `Samples.Length ≥ 2 × SampleCount` against the raw array. `REQ-DAT-001a` prohibits exposing
> that array at all, and the accessor it mandates returns a view of exactly the meaningful
> region — so the criterion becomes an equality, which is stronger. The oversize rented
> buffer is an implementation detail no caller can observe, which is the point.

**`REQ-DAT-001a` (P1) — Buffer ownership and use-after-dispose.**
Because `Samples` is a pooled array exposed publicly on an `IDisposable`, use-after-`Dispose`
would silently read another consumer's live data. `IqBlock` shall therefore either (a) expose
samples only through a method that throws `ObjectDisposedException` after disposal, or (b) be
handed to the DSP layer exclusively as an immutable, ref-counted view. Raw public array
exposure combined with pooling is **prohibited** — it makes `REQ-NFR-011` unenforceable.
**AC:** No public member of `IqBlock` returns the pooled array itself, asserted by a test
over the public surface. The use-after-dispose case is proved rather than assumed: dispose a
block, rent the same buffer for a second block, then access the first block's samples — the
access throws `ObjectDisposedException` instead of returning the second block's data. A
build in which the accessor omits its disposal check fails that test.

**`REQ-DAT-002` (P1) — Trigger-correction fidelity flag.**
`TriggerCorrectionsApplied` shall record whether trigger delay/phase corrections have been
applied to the samples.
*Rationale:* the reference product documents that trigger corrections are **not** applied to
exported data except in its SDF (Fast) format — a real fidelity trap. **[V]** OpenVSA shall
track this explicitly and propagate it into exported files rather than silently losing it.
**AC:** Blocks acquired with and without trigger corrections carry different
`TriggerCorrectionsApplied` values, and the flag survives every transformation the analysis
layers apply — extraction, gating and downconversion each preserve it rather than defaulting
it, asserted per transformation.

**`REQ-DAT-002a` (P1) — The fidelity flag survives export and re-import.**
**AC:** Each value survives a round trip through every supported export and re-import format.
A test enumerates the export writers and fails any that writes a constant or defaulted value
for the flag, since that is precisely the silent loss this requirement exists to prevent.
*Split from `REQ-DAT-002`: the export formats are `REQ-REC-005`'s, and none exists yet, so
this criterion cannot run until they do. Tracked in the Needs Verification epic.*

**`REQ-DAT-003` (P2) — `Complex32` value type.** A 8-byte `readonly struct Complex32`
with SIMD-friendly layout shall be provided for interpreted access to `Samples`, but bulk
DSP shall operate on the raw interleaved `float[]` to permit vectorisation.
**AC:** `sizeof(Complex32)` is 8 and its field order matches the interleaved I,Q layout, so
reinterpreting a `float[2N]` as `Complex32[N]` yields the same values element for element.
A test over the public DSP surface fails if a bulk kernel takes `Complex32[]` rather than
`float[]`.

### 7.2 Front-end contract

```csharp
namespace OpenVSA.Hal
{
    public interface IFrontEnd : IDisposable
    {
        FrontEndId              Id           { get; }
        string                  DisplayName  { get; }
        IFrontEndCapabilities   Capabilities { get; }
        FrontEndState           State        { get; }

        Task ConnectAsync(CancellationToken ct);
        Task DisconnectAsync();

        /// <summary>Validate and coerce a request to what this front end can honour.</summary>
        AcquisitionPlan Negotiate(AcquisitionRequest request);

        Task ConfigureAsync(AcquisitionPlan plan, CancellationToken ct);
        Task ArmAsync(CancellationToken ct);

        /// <summary>
        /// Pull the next block. Returns null when the source is exhausted (end of recording).
        /// Deliberately a pull model, not IAsyncEnumerable — see note below.
        /// </summary>
        Task<IqBlock> AcquireNextAsync(CancellationToken ct);

        Task AbortAsync();
        event EventHandler<FrontEndEvent> Notification;   // overload, unlock, hw error, range change
    }

    public interface IFrontEndCapabilities
    {
        FrequencyRange  CenterFrequencyRange { get; }
        double          MaxSpanHz            { get; }
        double          MinSpanHz            { get; }
        double          MaxSampleRateHz      { get; }
        int             MaxSamplesPerBlock   { get; }     // int: bounded by float[] ceiling
        long            MaxCaptureSamples    { get; }     // deep capture, may span many blocks
        bool            SupportsGapFreeStreaming { get; } // computed per plan, see REQ-NFR-027
        bool            SupportsBasebandIq   { get; }
        int             ChannelCount         { get; }     // ≥1
        bool            SupportsPhaseCoherentChannels { get; }
        IReadOnlyList<TriggerStyle> TriggerStyles { get; }
        AmplitudeRange  ReferenceLevelRange  { get; }
        bool            SupportsExternalRef  { get; }
    }
}
```

> **Why a pull model rather than `IAsyncEnumerable<T>`.** Async streams require **C# 8.0**
> plus `Microsoft.Bcl.AsyncInterfaces`; the mandated language level is C# 7.3, under which
> `IAsyncEnumerable<T>`, `await foreach` and async iterators simply do not compile. Rather
> than make the core HAL contract depend on raising `LangVersion` (which is possible on
> net472 but unsupported by Microsoft), the contract uses a plain `Task`-returning pull.
> Back-pressure then falls out naturally: the consumer sets the rate. If the team elects to
> raise `LangVersion` to 8.0, `IAsyncEnumerable<IqBlock>` may be offered as an *additional*
> convenience wrapper — never as the primary contract. See §20 Q7.

**`REQ-HAL-001` (P0) — Negotiate-then-configure.**
Every front end shall implement `Negotiate` as a **pure** function returning an
`AcquisitionPlan` that states, for each requested parameter, the honoured value and — where
coerced — the reason. Configuration shall never silently alter a user's request.
**AC:** Requesting a 50 MHz span from a front end capable of 10 MHz returns a plan with
`Span = 10 MHz`, `Coerced = true`, `Reason = "exceeds front-end maximum span"`; the UI
surfaces the coercion; no hardware command is sent during `Negotiate`.

**`REQ-HAL-002` (P0) — Capability-driven UI.**
The UI shall enable, disable and range-limit controls purely from `IFrontEndCapabilities`.
No UI code shall contain instrument-specific conditionals.
**AC:** A code search for instrument model names (`E4406`, etc.) in `OpenVSA.Ui` returns no
matches; switching front ends visibly re-ranges the affected controls.

**`REQ-HAL-003` (P1) — Front-end discovery and registration.**
Front ends shall be discovered from plug-in assemblies at start-up via a
`[FrontEndProvider]` attribute, and VISA resources shall be enumerable so users can pick a
discovered instrument.
**AC:** The connection dialog lists all VISA resources returned by the resource manager,
identifies each with `*IDN?` where safe, and marks those for which a driver exists.

### 7.3 Measurement context

**`REQ-DAT-010` (P1)** — Following the reference product's model, OpenVSA shall support
multiple named **measurement contexts** (the reference calls these "Analyzer
Configurations"), each holding a complete measurement setup, and shall present them as
first-class, addressable, nameable objects in the UI, in saved states and in the automation
API. **[V]**
**AC:** Two contexts (e.g. "Spectrum" and "QPSK demod") run concurrently against one
capture session, each with its own trace windows and markers; both are saved and recalled
by name; a state file whose context names do not match existing contexts raises a specific,
actionable error rather than partially applying.

### 7.4 Trace object model

```csharp
public sealed class Trace
{
    public TraceDataSource Data   { get; set; }   // Spectrum | MainTime | PSD | CCDF | Demod.* | Math | …
    public TraceFormat     Format { get; set; }   // LogMag | LinMag | Real | Imag | WrapPhase |
                                                  // UnwrapPhase | GroupDelay | IQ | Eye | Spectrogram
    public XScale  XScale { get; set; }
    public YScale  YScale { get; set; }           // ReferenceLevel, ReferencePosition, PerDivision, Units
    public IList<Marker>   Markers { get; }       // ≤20 per REQ-MKR-002
    public LimitTest       LimitTest { get; set; }
    public TraceMath       Math { get; set; }
    public MeasurementContextId Context { get; set; }
}
```

**`REQ-TRC-001` (P0) — Orthogonal data and format.**
`Data` and `Format` shall be independently settable. Changing `Format` shall **not** trigger
re-acquisition or re-computation of the underlying data.
**AC:** With acquisition paused on a held block, cycling a trace through every valid format
produces correct displays with zero calls into L1/L2 and zero recomputation of the FFT.

**`REQ-TRC-001a` (P0) — Accumulators are a third axis, not formats.**
**Spectrogram, Digital Persistence and Cumulative History are *not* trace formats.** They
accumulate across many acquisitions, so they cannot satisfy `REQ-TRC-001`'s "no recomputation
on format change". They shall be modelled as a distinct **`TraceAccumulator`** property
(`None | Spectrogram | DigitalPersistence | CumulativeHistory`), orthogonal to both Data and
Format. This matches the reference product, which groups exactly these three as "3D Map"
modes on their own toolbar. **[V]**
**AC:** `TraceAccumulator` is a property distinct from both Data and Format, and the three
accumulating modes appear only in its enumeration — a test over the Format enumeration fails
if Spectrogram, Digital Persistence or Cumulative History appears there. Changing Format
while an accumulator is active preserves the accumulated history and does not restart it,
whereas changing `TraceAccumulator` does discard it; both are asserted directly, since the
whole point of the separation is that `REQ-TRC-001`'s no-recomputation rule cannot apply to
data accumulated across acquisitions.

**`REQ-TRC-002` (P1) — Validity matrix over (Data × Averaging × Format).**
Valid formats shall be declared in metadata and enforced, and validity shall be a function of
the **averaging type as well as the data source**. In particular an RMS-averaged Spectrum has
no phase, so Wrapped Phase, Unwrapped Phase, Group Delay and IQ are invalid for it, whereas
they are valid for the same data source under coherent (Time) averaging or no averaging.
Invalid combinations shall be unselectable rather than erroring after the fact.
**AC:** Switching a phase-displaying trace from Time to RMS averaging disables the phase
formats with an explanatory tooltip rather than displaying meaningless data.

**`REQ-TRC-003` (P1) — Composition order is defined once.**
The order in which gating, windowing, FFT, averaging, accumulation and format conversion are
applied shall be specified in one place and implemented in exactly that order, and the legal
combinations shall be enumerated.
*Rationale:* each of these is individually specified elsewhere in this document; without a
stated composition order, two developers will reasonably implement gate-then-average and
average-then-gate and both will believe they are correct.
**AC:** The order is declared once, in one enumeration or pipeline definition, and the
implementation is driven by that declaration rather than restating it — a test fails if any
stage is invoked out of declared order. The enumerated legal combinations are exhaustive over
the cross-product of gating, windowing, averaging, accumulation and format, with every
combination either legal or rejected by a named error; none is silently ignored. The
gate-then-average and average-then-gate cases are distinguished by a test on a signal whose
gated and ungated averages provably differ, so the order is pinned by measurement rather than
by comment.

---

## 8. Acquisition layer requirements

### 8.1 General acquisition

**`REQ-ACQ-001` (P0) — Span/sample-rate relationships.**
The acquisition planner shall implement the relationships of §2.2, **with the path-dependent
FFT-size factor matching the path-dependent sample-rate factor**:

| Path | $F_s$ | $N_{\text{FFT}}$ | $T_{\max}$ |
|---|---|---|---|
| Complex zoom / IF | $1.28\,\text{Span}$ | $1.28\,(N_f-1)$ | $(N_f-1)/\text{Span}$ |
| Real baseband | $2.56\,\text{Span}$ | $2.56\,(N_f-1)$ | $(N_f-1)/\text{Span}$ |

Using the $1.28$ FFT factor on the baseband path breaks the $T_{\max}$ identity and is a
defect, not a simplification.
**AC:** Span = 10 MHz, $N_f = 801$, zoom → $F_s$ = 12.8 MHz, $N_{\text{FFT}}$ = 1024,
$T_{\max}$ = 80 µs (matching Keysight's worked example exactly). The same settings on the
baseband path → $F_s$ = 25.6 MHz, $N_{\text{FFT}}$ = 2048, $T_{\max}$ = 80 µs — the identity
holding on **both** paths is the test.

**`REQ-ACQ-002` (P1) — Main Time Length clamping with actionable guidance.**
Requested Main Time Length exceeding the span/points limit shall be clamped, and the UI
shall state the specific remedy — reduce span and/or increase frequency points — rather
than reporting a bare error. **[V]**
**AC:** Requesting 1 ms at a setting permitting 80 µs clamps to 80 µs and displays a message
naming both remedies with the numeric values that would be required.

**`REQ-ACQ-003` (P1) — Overlap processing.**
Frame extraction shall support overlap from **0 % to 99.99 %**, with independent maxima for
averaging-on and averaging-off, matching the reference product. **[V]** Frame advance shall
be $\lfloor (1-\text{overlap}) \cdot N_{\text{rec}} \rfloor$, minimum 1 sample, where
$N_{\text{rec}}$ is the **analysed time-record length in samples** — which under time gating
(`REQ-DSP-050`) and under the RBW/time coupling is *not* the same as $N_{\text{FFT}}$.
Defining advance on $N_{\text{FFT}}$ gives wrong frame counts whenever the two differ.
*Rationale to document in help text:* overlap recovers information that window tapering
would otherwise weight to zero at frame edges, at the cost of correlated averages — so more
averages are needed for equivalent variance reduction. **[V]**
**AC:** At 50 % overlap the frame count from a fixed recording is (within one frame) twice
that at 0 %; measured noise-variance reduction per average degrades as predicted.

**`REQ-ACQ-004` (P2) — Auto-ranging.** Where the front end supports input range control,
an auto-range function shall adjust reference level to avoid both overload and excessive
headroom, with a user-visible indication when it acts.
**AC:** Presented with a signal 20 dB below the current range and then one that overloads it,
auto-range settles in both cases to a reference level leaving the signal peak within a stated
headroom band, and settles rather than oscillating — repeated invocation on an unchanging
signal produces no further change. Each adjustment raises the user-visible indication of
`REQ-UI-007`. Where the front end does not support range control the function is
unavailable rather than silently doing nothing.

### 8.1a Amplitude scaling and the correction chain

The reference product lists "Correction" as a trace data type but the mechanics are not
public. OpenVSA must nonetheless define its own arithmetic explicitly — absolute amplitude
that is *almost* right is worse than none.

**`REQ-AMP-001` (P0) — Defined amplitude chain.**
The conversion from raw ADC codes to displayed absolute amplitude shall be specified as a
single documented expression combining `FullScaleVolts`, `ReferenceLevelDbm`, the reference
impedance, any front-end gain/attenuation state, window coherent gain (`REQ-DSP-011`), and
FFT scaling. It shall be stated once and implemented once.
**AC:** A front end reporting a known full-scale, presented with a CW tone of known absolute
power, yields the correct dBm reading under every window, every span, and every FFT size, to
within 0.05 dB.

**`REQ-AMP-002` (P1) — Units and impedance.**
Supported amplitude units: dBm, dBmV, dBµV, dBV, V (peak and RMS), W. Reference impedance
shall be settable (50 Ω default, 75 Ω supported), and every unit conversion involving power
shall use it explicitly.
**AC:** Switching between 50 Ω and 75 Ω changes dBm readings for a fixed voltage by the
analytic 1.76 dB.

**`REQ-AMP-003` (P1) — User frequency-response correction.**
Amplitude/phase correction tables shall be loadable from file (frequency, magnitude dB, phase
degrees), interpolated across the analysis span, applied optionally, and shown as the
"Correction" trace data type. Multiple tables shall be combinable (e.g. cable loss plus
antenna factor).
**AC:** Applying a correction of known shape to a flat input produces exactly the inverse
shape in the corrected trace, within interpolation error.

**`REQ-AMP-004` (P2) — De-embedding.** Complex (magnitude and phase) de-embedding of a
measured fixture response.
**AC:** A signal passed through a synthetic fixture of known complex response and then
de-embedded with that same response recovers the original to within 0.05 dB in magnitude and
0.5° in phase across the measurement band. De-embedding is complex, not magnitude-only,
proved by a fixture whose response is flat in magnitude but not in phase: without phase
correction the recovered EVM is degraded, with it the original EVM is restored.

**`REQ-ACQ-010` (P1) — Time-stamping.**
`IqBlock.AcquiredUtc` shall be sourced from a monotonic high-resolution clock
(`Stopwatch.GetTimestamp` disciplined to UTC at session start), **not** from `DateTime.UtcNow`
alone, whose granularity is ~1–15 ms. The relationship between `AcquiredUtc`,
`TriggerOffsetSeconds` and the first sample shall be defined explicitly and documented: the
timestamp refers to the **first sample of the block**, and the trigger event lies
`TriggerOffsetSeconds` after it (negative for pre-trigger).
**AC:** Timestamps across a continuous run advance by exactly `SampleCount / SampleRateHz`
within the front end's clock accuracy; the documented relationship is asserted in tests.

### 8.2 Triggering

**`REQ-TRG-001` (P0) — Trigger styles.**
The following trigger styles shall be modelled, with availability driven by front-end
capability: **Free Run**, **External**, **Channel/IF magnitude level**, **Periodic**, and
**Frequency Mask Trigger (FMT)**. **[V]** FMT shall be declared unsupported unless the front
end reports real-time capability.
**AC:** With the E4406A connected, the trigger selector offers exactly the styles that
instrument supports and greys the rest with an explanatory tooltip.

**`REQ-TRG-002` (P0) — Trigger delay including pre-trigger.**
Trigger delay shall accept positive values (wait after trigger) and **negative values
(pre-trigger)**, the latter served from front-end capture memory where available. **[V]**
**AC:** With the simulated source generating a burst, a −10 ms delay yields a record whose
first sample precedes the trigger event by 10 ms, verified against the injected burst
position to within one sample.

**`REQ-TRG-003` (P1) — Holdoff with three styles.**
Holdoff shall implement the reference product's three styles: **Conventional** (fixed
blanking window after each trigger), **Below Level** (signal must remain below the trigger
level for the whole holdoff before re-arming) and **Above Level** (the mirror case).
Negative holdoff shall be rejected. **[V]**
**AC:** Against a simulated pulse train of known period, each style produces the
analytically predicted trigger instants; a negative value is rejected at input validation.

### 8.3 The E4406A front end

**`REQ-E44-001` (P0) — Identification and option interrogation.**
On connect, the driver shall issue `*IDN?` and `*OPT?`, verify the model is `E4406A`, parse
the option list, and expose the installed personalities as capability metadata.
**AC:** Connecting to GPIB 17 yields model `E4406A`, firmware `A.08.10`, and the decoded
option set {BAH: GSM, 202/252: EDGE, BAC: cdmaOne, BAF: W-CDMA, B7C: baseband I/Q}.

**`REQ-E44-002` (P0) — Basic-mode IQ capture path. [V — verified on the instrument]**

The driver shall place the instrument in **Basic** mode (`:INSTrument:SELect BASIC`), select
the **Waveform** (time-domain) measurement via `:CONFigure:WAVeform`, and retrieve results
using the three result suffixes below.

> **Bench-verified 25 July 2026** against the E4406A at GPIB 17, firmware A.08.10, over
> NI-VISA. Every value in this requirement was measured, not inferred. The instrument was
> found in Basic mode / Channel Power at 2 GHz and was restored to that state afterwards.

**The three Waveform result suffixes:**

| Query | Returns |
|---|---|
| `:FETCh:WAVeform0?` | **Raw interleaved I,Q pairs** — `I₀,Q₀,I₁,Q₁,…`, exactly `2N` values |
| `:FETCh:WAVeform1?` | **7 scalars** (see below) |
| `:FETCh:WAVeform2?` | **Power versus time**, one value per sample, in dBm |

`:READ:WAVeform<n>?` behaves identically but arms and acquires first.

**Scalar block returned by `:FETCh:WAVeform1?`**, in order — verified against a real capture:

| # | Quantity | Example |
|---|---|---|
| 1 | **Sample interval** $T_s$, seconds | `+7.33333333E-007` |
| 2 | Mean power over the record, dBm | `-9.06845018E+001` |
| 3 | Mean power over the burst/gate, dBm | `-9.06845018E+001` |
| 4 | **Number of samples $N$** | `+15` |
| 5 | Peak-to-mean ratio, dB | `+3.90391283E+000` |
| 6 | Maximum point, dBm | `-8.67805890E+001` |
| 7 | Minimum point, dBm | `-1.09926433E+002` |

**AC:** The driver selects Basic mode and configures the Waveform measurement, and all three
result suffixes parse: `WAVeform0?` yields exactly $2N$ interleaved values with $N$ taken
from scalar 4, `WAVeform1?` yields exactly 7 scalars in the documented order, and
`WAVeform2?` yields $N$ power values. Scalar order is asserted positionally against a live
capture, since a transposed pair — mean power and burst power are equal on an ungated
capture — would otherwise pass unnoticed; the test uses a gated capture where they differ.
`:READ:WAVeform<n>?` arms and acquires where `:FETCh` does not, verified by fetching twice
and reading once and comparing which data changes. The driver restores the instrument's prior
mode and centre frequency on disconnect, as the bench procedure did.

**`REQ-E44-002a` (P0) — I/Q scaling. [V]**
The values returned by `:FETCh:WAVeform0?` are **volts, peak-referenced**. Instantaneous power
is therefore

$$P = \frac{I^2 + Q^2}{2 R},\qquad R = 50\,\Omega$$

**This was confirmed by reconstruction, not assumption:** computing mean, peak-to-mean, max and
min from a raw 15-sample capture reproduced all four of the instrument's own reported scalars
to three decimal places (mean −90.685 dBm vs −90.6845 reported; peak-to-mean 3.904 vs 3.904;
max −86.781 vs −86.781; min −109.926 vs −109.926). Treating the values as RMS volts gives
−87.674 dBm and is **wrong by 3.01 dB** — exactly the factor-of-two trap this requirement
exists to prevent.
**AC:** The bench capture recorded in this requirement is kept as a fixture, and the driver's
scaling reproduces all four instrument-reported scalars from its raw I/Q to three decimal
places: mean −90.685 dBm, peak-to-mean 3.904 dB, max −86.781 dBm, min −109.926 dBm. The
same test asserts that an RMS interpretation yields −87.674 dBm and **fails** — the wrong
answer is pinned as well as the right one, because the two differ by exactly 3.01 dB and
nothing else in the system would reveal the substitution. Power is computed as
$(I^2+Q^2)/2R$ with $R = 50\,\Omega$.

**`REQ-E44-002b` (P0) — Sample rate is quantised, and is set by RBW. [V]**
$T_s$ is always an integer multiple of **1/15 MHz ≈ 66.667 ns**, giving a maximum sample rate
of **7.5 MHz**. Measured values:

| RBW | $T_s$ | $F_s$ | multiple of 66.667 ns |
|---|---|---|---|
| 10 kHz | 7.533 µs | 132.7 kHz | 113 |
| 100 kHz | 733.33 ns | 1.3636 MHz | 11 |
| 257.5 kHz | 266.67 ns | 3.75 MHz | 4 |
| 505 kHz | 200 ns | 5.0 MHz | 3 |
| 752.5 kHz – 1 MHz | 133.33 ns | 7.5 MHz | 2 |

The driver shall therefore **never assume a requested sample rate is honoured**: set
`:SENSe:WAVeform:BANDwidth:RESolution`, then **read $T_s$ back from scalar 1** and place that
value in `IqBlock.SampleRateHz`. `REQ-HAL-001`'s negotiate-then-configure contract shall
report the coercion.
*Note this is a different law from the reference product's $F_s = 1.28\,\text{Span}$
(§2.2) — the E4406A's waveform path is RBW-driven with integer decimation, which is precisely
why the HAL negotiates rather than assumes.*
**AC:** `IqBlock.SampleRateHz` is derived from $T_s$ **read back from scalar 1**, never from
the requested rate — a test requests a rate the instrument cannot honour and asserts the
block carries the coerced value, with the coercion reported through `REQ-HAL-001`'s
negotiate-then-configure contract. Every $T_s$ returned is an integer multiple of 1/15 MHz,
checked across the RBW settings tabulated above, and each tabulated RBW reproduces its
measured $T_s$. A driver that echoes the requested rate fails, as does one applying the
reference product's $F_s = 1.28\,\text{Span}$ law to this front end.

**`REQ-E44-002c` (P0) — Record length and the truncation trap. [V]**
$N = \text{sweep time}/T_s + 1$ (measured: 20 ms at 7.5 MHz → `+150001`).
Sweep time accepts **1 µs to 100 s**, but capture memory caps the record at
**950 000 samples** (≈7.6 MB of I/Q). Beyond that the instrument **silently truncates the
acquisition and continues**, pushing

```
+22,"Memory limit caused Data Acquisition to be truncated"
```

into the error queue. Requesting 200 ms at 7.5 MHz returns `N = 950000`, not the 1 500 001
requested — with no failed query and no obvious symptom.

**The driver shall poll `:SYSTem:ERRor?` after every acquisition and surface error 22 as a
first-class condition**, and shall independently verify that the returned $N$ matches the
requested record length. *This is the single most dangerous behaviour found on the instrument:
a caller that trusts its own sweep-time setting will silently analyse a shorter record than it
believes it has.* Note also that the error queue is **sticky** — issue `*CLS` before an
acquisition whose error state matters.
**AC:** $N$ satisfies $\text{sweep time}/T_s + 1$, checked against the measured case of 20 ms
at 7.5 MHz returning 150 001. The truncation case is tested on the instrument: requesting
200 ms at 7.5 MHz returns $N = 950\,000$, and the driver raises a first-class error rather
than returning the short record as if complete. Both detection paths are asserted
independently — the `:SYSTem:ERRor?` poll surfacing error 22, **and** the returned $N$
compared against the requested length — since either alone can miss a truncation the other
catches. `*CLS` is issued before any acquisition whose error state matters, verified by a
test that leaves a stale error queued and confirms it is not misattributed to the next
acquisition. Sweep times outside 1 µs to 100 s are rejected before reaching the instrument.

**`REQ-E44-002d` (P1) — Transfer format. [V for acceptance]**
`:FORMat:DATA REAL,32` is accepted and reads back as `REAL,+32`; `:FORMat:BORDer` reads back
`NORM`. Both shall be set explicitly per `REQ-VISA-004`.
**[U] — throughput unmeasured.** An attempt to measure GPIB transfer rate during the bench
session produced identical elapsed times for a 2 001-sample and a 20 003-sample fetch, which
means the measurement harness performed a bounded read rather than draining the response. No
throughput figure was obtained, so `REQ-NFR-027`'s 100–300 kB/s estimate for this instrument
remains an **estimate**. Measure it properly with a real VISA read during Phase 3 — it sets
the duty cycle the whole front end operates at.

**AC:** A capture of a known CW tone at a known level returns I/Q whose computed magnitude
matches the applied level to within the instrument's amplitude accuracy, and whose frequency
matches the offset from centre to within 1 Hz; a deliberately over-long sweep time is detected
via both error 22 and the $N$ mismatch, and reported rather than silently accepted.

**`REQ-E44-003` (P1) — Baseband I/Q input support (Option B7C).**
Where Option B7C is present, the driver shall expose the front-panel I and Q inputs as an
alternative input path, supporting `:INPut:IMPedance:IQ`, `:INPut:IMPedance:REFerence`,
`:INPut:IQ:ALIGn`, `:INPut:OFFSet:I`, `:INPut:OFFSet:Q`,
`:SENSe:VOLTage:IQ:RANGe:UPPer` and `:SENSe:POWer:IQ:RANGe:UPPer`, and shall set
`IsBaseband = true` on resulting blocks so the $2.56\times$ span relationship is applied.
**AC:** With B7C detected, the baseband path appears as a selectable input; impedance and
range commands round-trip; blocks carry `IsBaseband = true`.

**`REQ-E44-004` (P2) — B7C alignment and calibration passthrough.**
The driver shall expose `:CALibration:GIQ`, `:CALibration:IQ:CMR`,
`:CALibration:IQ:FLATness` and `:CALibration:IQ:OFFSet` as explicit user-initiated
maintenance actions, never automatically.
**AC:** All four commands are reachable as maintenance actions, and none is issued on
connect, on mode change, on acquisition, or on any other automatic path — asserted by
recording the SCPI traffic across a full connect-configure-acquire-disconnect cycle and
failing if a calibration command appears. Each action reports completion or failure to the
user rather than running silently, and calibration state is reflected in the status bar per
`REQ-UI-006`.

**`REQ-E44-005` (P1) — Remote-operation courtesy.**
The driver shall document (and the UI shall display) that front-panel controls are disabled
during remote operation, shall provide an explicit **Return to Local** action, and shall
restore the instrument's prior mode on disconnect.
**AC:** After a session against an instrument found in `BASIC` mode, disconnect leaves it in
`BASIC` mode and in local control.

**`REQ-E44-006` (P2) — Screen capture passthrough.**
The driver shall optionally retrieve the instrument's own display (HP-GL plot or PCL print
hardcopy) for inclusion in reports — useful for cross-checking OpenVSA's results against the
instrument's native personality measurements.
**AC:** A screen capture retrieved from the instrument renders as an image and embeds in a
`REQ-NFR-040` report. Both offered forms are handled, or the unsupported one is declared
unavailable rather than producing a corrupt file. The capture is optional and off by default:
a normal acquisition cycle issues no hardcopy command, asserted from the SCPI traffic, since
these transfers are slow enough to matter on a GPIB link (§6.3). Failure to retrieve degrades
the report gracefully instead of failing the measurement.

**`REQ-E44-007` (P1) — Cross-validation harness.**
A test harness shall compare OpenVSA's flexible-demod results against the E4406A's own
personality measurements for the same signal (W-CDMA via BAF, EDGE via 202/252, cdmaOne via
BAC).
*Rationale:* this is the highest-value verification available on this bench — an independent,
vendor-calibrated reference for EVM and related metrics that no simulator can supply.
**AC:** For a stable test signal, OpenVSA's RMS EVM agrees with the instrument's reported
RMS EVM to within 0.5 percentage points, or the discrepancy is analysed and documented.

### 8.4 Recording and playback

**`REQ-REC-001` (P0) — Native recording format.**
OpenVSA shall define a native container (`.ovsa`) comprising a versioned, self-describing
header plus one or more sample arrays. The header shall carry at minimum: format version,
sample rate, centre frequency, baseband/complex flag, full-scale reference, reference level,
UTC start timestamp, per-block trigger offsets, trigger-corrections-applied flag, front-end
identification, and a free-form user annotation block.
**AC:** A recording written and re-read reproduces samples bit-exactly and metadata field
for field; a file from format version *n* is readable by version *n+1* code.

**`REQ-REC-002` (P0) — Record while measuring.**
Recording shall be possible concurrently with live analysis without degrading the analysis
update rate by more than 10 %.
**AC:** Measured update rate with recording enabled is within 10 % of the same measurement
with recording disabled, at ≥25 MS/s to a local SSD.

**`REQ-REC-003` (P1) — Playback as a first-class front end.**
`FilePlaybackFrontEnd` shall implement `IFrontEnd` identically to a hardware front end, with
transport controls: play, pause, stop, single-step by frame, seek, loop, and playback rate.
**AC:** Every measurement and every trace type produces identical results from a recording
as from the live source that produced it (bit-identical where the DSP is deterministic).

**`REQ-REC-004` (P1) — Zoom range bound, applied consistently.**
Re-analysis by digital downconversion and decimation shall be permitted down to **1/256** of
the source span, matching the reference product's documented playback bound. **[V]**

**This bound applies identically to live-block zoom (`REQ-DSP-023`) and to playback** — the
two must not disagree, since both perform the same DDC on the same captured samples. Where
`REQ-DSP-023` speaks of "arbitrarily narrow" analysis, it means arbitrary within this bound.
**AC:** A live block captured at 10 MHz span analyses correctly at spans down to 39.0625 kHz;
narrower requests are rejected with an explanatory message naming the bound and the span that
was asked for.

**`REQ-REC-004a` (P1) — The bound holds identically on playback.**
**AC:** A recording made at 10 MHz span analyses correctly at spans down to 39.0625 kHz, and
a narrower request is rejected with the same bound and the same message as live zoom — the
message text is compared between the two paths, not merely asserted to exist in each, since
two independently worded messages are how the two bounds drift apart.
*Split from `REQ-REC-004`: the bound is defined here but first used by live-block zoom
(`REQ-DSP-023`) in Phase 1, and a Phase 1 requirement cannot depend on a Phase 3 one for the
message it must produce.*

**`REQ-REC-005` (P1) — Import and export format support.**
The following formats shall be supported for import (I) and/or export (E), matching the
reference product's catalogue: **[V]**

| Format | Extensions | I | E | Notes |
|---|---|---|---|---|
| OpenVSA native | `.ovsa` | ✓ | ✓ | full fidelity, all metadata |
| CSV | `.csv` | ✓ | ✓ | ASCII, metadata in header comments |
| Tab-delimited text | `.txt` | ✓ | ✓ | |
| MATLAB MAT-file (v7.3/HDF5) | `.mat`, `.h5`, `.hdf` | ✓ | ✓ | large-file capable |
| MATLAB MAT-file v4 | `.mat` | ✓ | ✓ | legacy |
| BINF | `.binf` | ✓ | ✓ | little-endian real32 |
| SDF (Fast / Export) | `.sdf`, `.dat` | ✓ | ✓ | legacy 89400-series interchange — see caveat |
| VITA 49 / 49A / 49.2 | `.vita49`, `.pcap`, `.pcapng` | ✓ | ✓ | standard RF metadata packets |
| N5106A / N5110A waveform | `.bin` | ✓ | ✓ | big- and little-endian variants |
| E3238S time snapshot | `.cap` | ✓ | ✗ | recall only, legacy |

> **⚠ [U] — SDF binary layout.** The byte-level SDF header field layout (offsets for centre
> frequency, sample rate, timestamps) was **not** obtained during research. SDF was
> historically publicly documented via the 89400-series "SDF File Format Utilities" manual;
> that document must be obtained before the SDF reader/writer is implemented. Until then,
> treat SDF as P2 and do not schedule it into an early phase.

**AC:** Each format supports exactly the directions ticked — a test asserts that E3238S is
import-only and that attempting to export it is refused with a named error rather than
silently producing nothing. Every format marked for both directions round-trips: a block
exported and re-imported returns sample values within that format's stated precision, and
the native `.ovsa` format round-trips **bit-identically** with all metadata intact, which is
what "full fidelity" claims. Formats with endianness variants are tested in both. Any
metadata a format cannot carry is reported per `REQ-REC-006`. SDF remains unimplemented
until its documented byte-level layout is obtained; a stub that writes a plausible-looking
but unverified SDF file fails this criterion.

**`REQ-REC-006` (P1) — Export fidelity honesty.**
Where an export format cannot carry trigger corrections or full metadata, the export dialog
shall say so explicitly before writing, and the written file shall record the limitation.
*Rationale:* the reference product has exactly this trap and it is a known source of
confusion. **[V]**
**AC:** Exporting to a format that cannot carry trigger corrections or full metadata raises
an explicit statement of what will be lost **before** the file is written, naming the fields
— a warning shown afterwards, or a generic "some data may be lost", fails. The written file
records the limitation in its own metadata where the format allows, so the loss is
discoverable from the file alone. The lossy set is derived from each writer's declared
capabilities rather than hard-coded, so a new format cannot be added without declaring what
it drops. Exporting `TriggerCorrectionsApplied = false` data is itself flagged, per
`REQ-DAT-002`.

**`REQ-REC-007` (P2) — Recording size estimation.** The UI shall show projected file size
and remaining disk time before a recording starts.
**AC:** Before a recording starts the UI shows a projected file size and a remaining-disk
time. The projection is checked against reality: for a completed recording the actual size is
within 5 % of the projection, across sample rates and formats. Remaining disk time uses free
space on the target volume at the time of the estimate, and a recording projected to exceed
free space is warned about before it starts rather than failing partway.

**`REQ-REC-008` (P1) — Recording robustness.**
Recordings shall: segment automatically at a configurable maximum file size (default 2 GB) with
an index linking segments; monitor free disk space during capture and stop cleanly with a
clear message before exhausting it; and be **crash-safe**, such that a recording interrupted
by process termination or power loss remains readable up to the last completely written block.
*Rationale:* multi-gigabyte captures are routine in this application, and losing a long
acquisition to a full disk or a crash is an operational failure the user cannot recover from.
**AC:** Killing the process mid-recording leaves a file that opens and plays back to the last
whole block; filling the disk produces a clean stop, not a corrupt file.

**`REQ-REC-009` (P1) — Robust parsing of untrusted files.**
All import parsers (VITA 49/pcap, MAT v4 and v7.3/HDF5, BINF, SDF, N5106A/N5110A, E3238S,
CSV) shall treat input as untrusted: bounds-checked, allocation-limited, and non-crashing on
malformed input.
**AC:** Each parser survives a fuzzing campaign (≥1 M mutated inputs per format) with no
crash, no unbounded allocation, and no hang; malformed files produce a diagnostic, not an
exception dialog.

### 8.5 Simulated signal source

**`REQ-SIM-001` (P0) — Synthetic modulated source.**
`SimulatedFrontEnd` shall generate IQ for any modulation format supported by the
demodulator (§11.2), with settable symbol rate, pulse-shaping filter and roll-off, carrier
offset, and amplitude.
**AC:** For every supported format, the generated waveform is checked back against the
parameters it was asked for, from its own samples and without demodulating: the symbol
sequence recovered at the known symbol instants matches the constellation the format
declares, the symbol rate and carrier offset are recovered to within 1e-6 relative, and the
pulse-shaping filter's roll-off is recovered from the spectrum to within 1 %.

**`REQ-SIM-001a` (P0) — The synthetic source proven by demodulation.**
The source of `REQ-SIM-001` shall be correct to the standard the metrics engine needs, which
only a demodulator can establish.
**AC:** For every supported format, a clean generated signal demodulates with RMS EVM
< 0.1 % — the residual being numerical only.
*Split from `REQ-SIM-001` because that criterion cannot run until a demodulator exists.
Keeping the two together made a requirement whose implementation belonged to one phase and
whose proof belonged to another, so neither phase could be closed honestly.*

**`REQ-SIM-002` (P0) — Controllable impairments.**
The generator shall inject, independently and quantitatively: AWGN (specified SNR),
carrier frequency offset (Hz), carrier phase offset, IQ gain imbalance (dB), quadrature skew
(degrees), IQ origin offset (dB), amplitude droop (dB/symbol), timing offset (fraction of a
symbol), symbol-clock error (ppm), phase noise (mask-specified), AM/AM and AM/PM
compression, and multipath (tapped-delay-line channel).
**AC:** Each impairment is measured back from the generated samples — not from a
demodulator — and matches the magnitude requested to within 1 %: SNR from the noise power in
a signal-free band, carrier offset and phase from the complex mean at the symbol instants,
gain imbalance and quadrature skew from the I and Q second moments, origin offset from the
mean, droop from a fit to the symbol magnitudes, timing offset and clock error from the
recovered symbol instants. **Independence is the harder half and is tested explicitly:**
injecting one impairment leaves every other's measured value unchanged to within its own
tolerance, so a generator that couples two of them fails.

**`REQ-SIM-002a` (P0) — Impairments recovered by the metrics engine.**
Every impairment of `REQ-SIM-002` shall be recovered by the measurement that reports it.
**AC:** Each impairment injected at a known magnitude is recovered by the demodulator's
corresponding metric to within 5 % or 0.1 dB, whichever is looser. **This is the primary
correctness proof for the entire metrics engine** and is elaborated in §17.2.
*Split from `REQ-SIM-002` for the reason given under `REQ-SIM-001a`. The generator can be
proved correct without a demodulator; the metrics engine cannot, and it is the metrics engine
this criterion actually tests.*

**`REQ-SIM-003` (P1) — Deterministic, seeded generation.**
All stochastic elements shall derive from an explicit seed so that any generated scenario is
exactly reproducible.
**AC:** Two runs with identical seed and parameters produce bit-identical sample streams.

**`REQ-SIM-004` (P2) — Burst and pulse scenarios.** The generator shall produce bursted
signals with settable on/off times, ramp shapes and inter-burst noise floor, for exercising
pulse search (§11.5).
**AC:** A generated burst, measured back from its own samples, reproduces the requested on
and off times to within one sample, the requested ramp shape to within 1 % of its specified
transition time, and the requested inter-burst noise floor to within 0.5 dB. Generation is
seeded and reproducible under `REQ-SIM-003`, so these are exact comparisons against the
requested parameters rather than against a previous run.

**`REQ-SIM-005` (P2) — Standard-signal presets.** Presets producing structurally correct
signals for each implemented personality (GSM burst, W-CDMA frame, LTE resource grid, …)
shall be provided for personality development without hardware.
**AC:** Each preset is decoded by the personality it targets with no manual parameter entry,
and the recovered structure — burst, frame and slot boundaries, and modulation format —
matches the preset's declared parameters. A preset naming a personality that is not yet
implemented fails the parse rather than shipping as a preset that cannot be decoded.
---

## 9. Core DSP engine

### 9.1 Processing model

**`REQ-DSP-001` (P0) — Block-based, non-real-time analysis.**
Analysis shall operate on finite blocks with full random access to the whole block. Nothing
in the DSP design shall assume causality or streaming operation.
*Rationale:* this is what makes the reference product's estimation quality achievable —
whole-block maximum-likelihood estimation of carrier, timing and phase strictly outperforms
causal tracking loops, and it is why the reference product can lock reliably on short
bursts. It is a deliberate architectural choice, not a compromise.
**AC:** An architecture test over the public DSP surface fails if any analysis entry point
takes an incremental or push-style form — a per-sample `Process`, a `Push`, or a stateful
accumulator that survives between calls — rather than a complete block. Block-position
invariance is tested with what the DSP layer already estimates: the spectrum of a short burst,
and the burst's own measured position and power, are invariant to where in the block the burst
sits, to within the numerical tolerance of the transform.

**`REQ-DSP-001a` (P0) — Block-based estimation proven on the estimators.**
The estimation advantage claimed for whole-block analysis shall be demonstrated on the
estimators that claim it.
**AC:** Carrier, timing and phase estimates for a short burst are invariant, to the tolerances
of `REQ-SIM-002a`, to where in the block the burst sits. A causal tracking implementation
cannot satisfy that, so the test discriminates.
*Split from `REQ-DSP-001` because there are no carrier, timing or phase estimators until the
demodulator exists. The architectural rule — no push-style entry point — is enforceable from
the first commit and stays where it belongs.*

**`REQ-DSP-002` (P0) — Double-precision accumulation.**
Sample storage may be single precision, but all accumulations (averaging, correlation, sums
of squares, metric computation) shall accumulate in `double`.
**AC:** Averaging 100 000 frames of a constant-amplitude signal produces a result whose
error from the analytic value is < 1e-9 relative; the same test in single-precision
accumulation demonstrably fails.

### 9.2 Windowing

**`REQ-DSP-010` (P0) — Window function set.**
The following windows shall be provided with the stated normalised ENBW (Hz·s) and sidelobe
figures, matching the reference product: **[V]**

| Window | Normalised ENBW | Peak sidelobe | Notes |
|---|---|---|---|
| Uniform (rectangular) | 1.0000 | −13.3 dB | self-windowing/transient signals |
| Hann ("Hanning") | 1.5000 | −31.5 dB | general purpose, random noise; unsuitable for bursts/chirps |
| Gaussian Top | 2.2153 | — | high dynamic range, better resolution than Hann |
| Flat Top | 3.8194 | — | **default**; amplitude accuracy over resolution |
| Blackman-Harris (4-term) | 2.0044 | −92.0 dB | |
| Kaiser-Bessel (πα = 11.9) | 2.0013 | −89.1 dB | faster sidelobe roll-off than Blackman-Harris |
| Gaussian (α = 3.58, σ = 0.1397) | 2.0212 | −73.5 dB | special purpose only |

**AC:** Every window in the table is implemented and selectable, and Flat Top is the default.
Each window's measured ENBW, computed as $\mathrm{ENBW} = N \sum w_n^2 / (\sum w_n)^2$,
matches its tabulated value to within 0.1 %, and its measured peak sidelobe matches the quoted
figure to within 0.5 dB where one is quoted. These are the closed-form figures required by
`REQ-TST-001`, not values captured from a previous run. `REQ-DSP-010a` fixes the window
definition the tabulated figures assume and the FFT sizes over which the tolerance holds.

**`REQ-DSP-010a` (P0) — Periodic (DFT-even) window definitions.**
All windows shall use the **periodic** (DFT-even) definition, $w_n = f(n/N)$ for
$n = 0 \ldots N-1$, **not** the symmetric definition $f(n/(N-1))$.
*Rationale:* the ENBW figures in the table above are the periodic values. Symmetric Hann gives
$1.5\,N/(N-1)$ — a 0.03 % error at $N = 4096$ but **1.6 % at $N = 64$**, which fails the
acceptance criterion below at the small end of the supported range ($N_f = 51 \Rightarrow
N_{\text{FFT}} = 64$). Spectral analysis wants the periodic form regardless; this just makes
it binding.

**AC:** Each window's measured ENBW, computed as
$\mathrm{ENBW} = N \sum w_n^2 / (\sum w_n)^2$, matches the table to within 0.1 % **at every
supported FFT size from 64 to 2²⁰**; measured peak sidelobe matches the quoted figure to
within 0.5 dB where one is quoted.
*Note:* the Flat Top default is deliberate and must be preserved — it surprises users who
expect Hann, but it is the reference product's documented behaviour.

**`REQ-DSP-010b` (P0) — Window parameter provenance.** **[V]**
The tabulated ENBW is the binding figure; where the reference product does not publish a
window's coefficients, the implementation shall choose parameters that reproduce it, and
shall record how they were chosen. Two cases arose in implementation and are settled here so
that a later reader does not "correct" them:

| Window | Settled as |
|---|---|
| Kaiser-Bessel | "πα = 11.9" is the Kaiser shape parameter **β = 11.9 directly**, not $\alpha = 11.9/\pi$. β = 11.9 reproduces *both* tabulated figures — ENBW 2.001266 against 2.0013 and peak sidelobe −89.09 dB against −89.1 dB — whereas the other reading gives ENBW 1.23. Two independent figures agreeing is what settles it. |
| Gaussian | σ = 0.1397 as stated (α = 3.58 ⇒ σ = 0.5/α) is used unaltered: it gives ENBW 2.020682 against 2.0212, 0.026 % low, and −73.46 dB against −73.5 dB. |
| Gaussian Top | Not documented beyond its ENBW. Implemented as a Gaussian with σ solved for ENBW 2.2153 exactly, which also yields −85.2 dB — consistent with the "high dynamic range" the table claims for it. No sidelobe figure is tabulated, so only the ENBW binds. |
| Flat Top | **No published flat top has ENBW 3.8194.** The nearest are Heinzel's HFT95 (3.8112) and HFT90D (3.8832), and the SRS/MATLAB flat top is 3.7702 — all outside the 0.1 % tolerance. Implemented as the convex blend of HFT95 and HFT90D at t = 0.115777, solved for 3.819400. The result is a sound flat top in its own right: −94.1 dB sidelobes and 0.0039 dB amplitude flatness over a bin, both between its parents. |

*Why ENBW is the figure to match rather than the coefficients:* ENBW is the behaviourally
visible quantity — it sets RBW through `REQ-DSP-020` and the noise-density correction through
`REQ-DSP-011`. A window with the right ENBW and slightly different sidelobes behaves as the
reference product does everywhere the user can observe; one with published coefficients and
the wrong ENBW does not.

**AC:** Every parameter not taken verbatim from the specification's table carries a recorded
derivation in the implementation, and the ENBW it produces is asserted against the table by
`REQ-DSP-010a`'s criterion rather than restated as a constant.

**`REQ-DSP-011` (P1) — Coherent and incoherent gain correction.**
Window amplitude correction (coherent gain, $\sum w_n / N$) shall be applied for
discrete-tone amplitude readout, and noise-power correction (ENBW) for noise-density
readout, with the correct one selected automatically by trace data type.
**AC:** A full-scale CW tone reads its correct amplitude under every window to within
0.05 dB; a white-noise input reads the correct power spectral density under every window to
within 0.1 dB.

**`REQ-DSP-012` (P2) — Zero-span channel filter shape.** In zero-span/power-spectrum
operation, "window type" shall be replaced by **Channel Filter Shape** (Gaussian, or
None/anti-alias only), mirroring the reference product. **[V]**
**AC:** Entering zero-span or power-spectrum operation replaces the window-type control with
a Channel Filter Shape control offering Gaussian and None/anti-alias-only, and no window-type
selection remains reachable in that mode. The selected shape is recorded in the trace state
and in exported metadata, so a saved measurement records which filter produced it.

### 9.3 Spectrum computation and RBW

**`REQ-DSP-020` (P0) — Bidirectional RBW/time coupling.**
$\mathrm{RBW} = \mathrm{ENBW_{norm}} / T_{\text{rec}}$ shall be enforced in both directions:
spectrum-class measurements take RBW as the independent variable and derive record length;
demodulation-class measurements take Main Time Length as independent and derive RBW. **[V]**
**AC:** Hann window, 100 kHz RBW → 15 µs record. Hann window, 50 ms record → 30 Hz RBW.
Both match the documented worked examples exactly.

**`REQ-DSP-021` (P1) — RBW range and coupling modes.**
RBW shall span **< 1 Hz to > 0.287 × maximum span**, with a **ResBW Mode** selecting between
spectrum-analyser-style emulation and arbitrary RBW, and a **ResBW Coupling** control
governing how RBW tracks span. A **Span-to-ResBW Ratio** shall be settable. **[V]**
**AC:** RBW is settable below 1 Hz and up to at least 0.287 × the maximum span, with values
outside the achievable range rejected with the achievable bound reported rather than silently
clamped. In coupled mode a span change updates RBW to hold the Span-to-ResBW Ratio, and the
resulting RBW satisfies `REQ-DSP-020`'s $\mathrm{RBW} = \mathrm{ENBW_{norm}}/T_{\text{rec}}$
exactly; in uncoupled mode the same span change leaves RBW untouched. Both ResBW Modes are
selectable and produce demonstrably different RBW sequences over a span sweep.

**`REQ-DSP-022` (P0) — Frequency-point range.**
Displayed frequency points shall range **51 to 409 601**, with $N_f - 1$ constrained to
$50 \cdot 2^k$ so that $N_{\text{FFT}} = 1.28 (N_f - 1)$ is an integer power of two; the
corresponding FFT-size ceiling is $2^{19} = 524\,288$. An **Auto** mode shall derive point
count from span, RBW/time length and window type. **[V for the 409 601 figure]**

> **Documented inconsistency, flagged rather than silently resolved.** The 89601B datasheet
> states "51 to 524 288 displayed (51 to 409 601 calibrated)". But $1.28 \times 524\,287 =
> 671\,087.36$ — not an integer and not a power of two — so 524 288 cannot be a displayed
> point count under the product's own $N_{\text{FFT}}$ relation; it is exactly $2^{19}$, i.e.
> the maximum **FFT size**. The datasheet figure appears to be a transcription of the FFT-size
> ceiling into the points row. OpenVSA adopts the self-consistent reading above. Confirm
> against the product before treating either number as a threshold (§20, Q8).

**AC:** 51 and 409 601 are accepted; 50 and 409 602 are rejected with clear messages; every
accepted $N_f$ yields an integer power-of-two $N_{\text{FFT}}$; Auto mode reproduces the
documented relationships.

**`REQ-DSP-023` (P1) — Band-selectable (zoom) analysis.**
Span shall be settable independently of centre frequency, implemented by digital
downconversion and decimation, permitting arbitrarily narrow analysis within the front
end's captured bandwidth. A **Select Area** trace tool shall allow dragging a region and
zooming to it, with a **Full Span** control to return. A **Zoom If Span Change** option
shall govern whether reducing span re-centres or holds the start frequency at 0 Hz. **[V]**
Zoom depth is bounded at **1/256 of the source span** by `REQ-REC-004`, which applies to live
blocks and playback alike.
**AC:** A 1 kHz-wide feature 4 MHz from centre in a 10 MHz span is resolvable by zooming to a
39.0625 kHz span (the 1/256 floor) without re-acquisition, using only the captured block; a
request below the floor is rejected with the message required by `REQ-REC-004`.

**`REQ-DSP-023a` (P0) — DDC and decimation filter specification.**
The digital downconverter used for zoom and playback re-analysis shall meet stated, testable
filter requirements — currently absent from the reference product's public documentation and
therefore **specified here as design targets**: **[DESIGN CHOICE]**

| Parameter | Target |
|---|---|
| Passband ripple | ≤ 0.05 dB peak-to-peak over the analysis span |
| Passband flatness (amplitude) | ≤ ±0.02 dB over the central 80 % of span |
| Stopband / alias rejection | ≥ 100 dB |
| Spurious-free dynamic range through the DDC | ≥ 100 dBc |
| Phase response | linear phase (symmetric FIR) unless explicitly configured otherwise |

*Rationale:* zoom is central to this product (`REQ-DSP-023`, `REQ-REC-004`) and every
demodulation result passes through it. `REQ-TST-001` requires "measured SFDR against design
target" — this table is that target. Without it the requirement is untestable.
**AC:** A full-scale tone swept across the span shows amplitude variation within the ripple
figure and no alias or spur above −100 dBc at any decimation factor.

**`REQ-DSP-024` (P1) — Max FFT size control.** A **Max FFT Size** parameter shall bound
transform size for power measurements, and a **Noise Correction** option shall subtract a
characterised instrument noise floor. **[U]** — the reference product's numeric ceiling for
Max FFT Size was not obtained; OpenVSA shall default to 2²⁰ and make it configurable.
**AC:** Max FFT Size defaults to 2²⁰ and is configurable; a measurement whose natural
transform would exceed it is bounded to it rather than failing, and the bound is visible in
the trace annotation so the user knows the resolution was capped. With Noise Correction on,
measuring a signal at a known level above a characterised noise floor returns a power closer
to the analytic value than with it off, and correcting a noise-only input does not produce a
negative power — it floors at the reported measurement limit.

### 9.4 Averaging

**`REQ-DSP-030` (P0) — Averaging types.**
The following shall be implemented, matching the reference product: **[V]**

| Type | Behaviour |
|---|---|
| **RMS (Video)** | Power-domain (incoherent) average; runs to the specified count, then stops |
| **RMS (Video) Exponential** | Power-domain, exponentially weighted, runs indefinitely |
| **Time** | Coherent (vector) average of complex time records; runs to count |
| **Time Exponential** | Coherent, exponentially weighted, indefinite |
| **Peak Hold** | Per-bin maximum, runs to count |
| **Continuous Peak Hold** | Per-bin maximum, indefinite |

Linear (non-exponential) modes stop at the count; exponential modes ramp from $N=1$ to
steady state, thereafter weighting $\alpha = 1/N_{\text{count}}$. A **Repeat Average**
option shall control re-arm behaviour on completion.

> **Note on terminology — [U].** Research could not confirm the literal string "vector
> averaging" in current Keysight help; the **Time** / **Time Exponential** types are the
> coherent averages (they average the complex time record, preserving phase, and therefore
> suppress noise uncorrelated with the trigger). This specification uses "coherent/vector"
> and "Time" interchangeably and requires the *behaviour*, which is unambiguous.

**AC:** Coherent (Time) averaging of a triggered CW tone in AWGN improves SNR by
$10\log_{10}N$ dB. Incoherent (RMS) averaging of the same signal reduces the **variance of the
spectral estimate** by a factor of $N$ (standard deviation by $\sqrt N$) while leaving the
estimated SNR unchanged. These two distinct behaviours are the definitive test that the types
have not been conflated — a classic and easily-missed implementation error.

**`REQ-DSP-031` (P1) — Averaging interacts correctly with overlap.**
Where overlapped frames are averaged, the effective number of independent averages shall be
computed and displayed, since overlapped frames are correlated. **[V]**
**AC:** At 0 % overlap the effective count equals the frame count. At non-zero overlap it is
strictly less, and the displayed value predicts the observed variance reduction of an
averaged noise measurement to within 10 % — the check that the figure is the honest effective
count and not the raw frame count relabelled. Reporting the raw count at 75 % overlap fails
the test.

### 9.5 Trace data types

**`REQ-DSP-040` (P0) — Base (non-demodulation) trace data types.**
The following shall be available without any demodulation licence, matching the reference
product's Option 200 set: **[V]**

Spectrum · Raw Main Time · Instantaneous Main Time · PSD (power spectral density) ·
Autocorrelation · CCDF · CDF · PDF · Correction · Math · No Data.

**AC:** Every listed type is selectable and produces a trace with the demodulation assemblies
absent from the load path — the error this guards against is a base type made accidentally
dependent on demodulation code. (The reference product enforced this split with an option
SKU; OpenVSA has no such gate per `REQ-LIC-010`, so the check is an assembly-dependency one,
which is stricter.) Each type is checked
against its closed-form result per `REQ-TST-001` where one exists: PSD of band-limited white
noise of known density reads that density to within 0.1 dB, autocorrelation of white noise is
a unit impulse at zero lag, and the CCDF of Gaussian noise matches the analytic Rayleigh
envelope curve to within 0.1 dB over 0–10 dB above average power.

**`REQ-DSP-040a` (P2) — Cross-channel trace data types.**
Cross Spectrum · Cross Correlation · Coherence · Frequency Response · Impulse Response.

These are **P2, not P0**, and are gated on multi-channel support in the HAL
(`IFrontEndCapabilities.ChannelCount` and `SupportsPhaseCoherentChannels`,
`REQ-HAL-001`). They require phase-coherent, common-timebase acquisition of two channels,
which no front end in Phase 3 provides; specifying them as P0 against a single-channel
`IqBlock` would be incoherent. When implemented, `IqBlock` shall be extended to carry a
channel index, or grouped into an `IqBlockSet` with a declared coherence guarantee.
**AC:** Each cross-channel type is offered only when the active front end declares
`ChannelCount ≥ 2` and `SupportsPhaseCoherentChannels`; against a single-channel front end
they are absent from the selectable set rather than present and erroring. Once a coherent
two-channel source exists, coherence of a signal with itself is 1.0 to within 1e-6 at every
frequency, coherence of two independent noise sources tends to $1/N_{\text{avg}}$, and the
frequency response of a known synthetic filter is recovered to within 0.1 dB and 1°.

**`REQ-DSP-041` (P0) — Trace formats.**
Log Magnitude · Linear Magnitude · Real · Imaginary · Wrapped Phase · Unwrapped Phase ·
Group Delay · IQ (polar/constellation) · Eye · Spectrogram · digital persistence ·
cumulative history. **[V]**
**AC:** Every listed format is selectable for a trace whose data type supports it, and
switching between the non-accumulating formats satisfies `REQ-TRC-001` — no reacquisition and
no recomputation from the source block, asserted by a counter on the acquisition path that
must not advance across a format change. The three accumulating entries are reached through
`TraceAccumulator` per `REQ-TRC-001a`, not through this format list. Log and Linear Magnitude
of the same data agree to within 0.01 dB after conversion, and Real/Imaginary recombine to
the Magnitude value, so the formats are views of one computation rather than parallel paths.

**`REQ-DSP-042` (P1) — CCDF definition.**
CCDF shall plot $P\{P_{\text{inst}} > P_{\text{avg}} + x\}$ against $x$ in dB (log
probability axis), with a Gaussian-noise reference curve overlaid, and shall report
peak-to-average ratio at the 0.01 %, 0.1 % and 1 % probability points.
**AC:** For band-limited Gaussian noise the measured curve lies within 0.2 dB **horizontally**
(i.e. in $x$, the probability axis being logarithmic) of the theoretical
$P(x) = \exp(-10^{x/10})$ over 0–10 dB, using **≥10⁷ independent samples**.
*Both qualifications matter:* "within 0.2 dB" is meaningless on a probability axis, and at
$x = 10$ dB the true probability is $e^{-10} \approx 4.5\times10^{-5}$, so the sample count is
part of the criterion rather than an implementation detail.

**`REQ-DSP-043` (P1) — Spectrogram.**
Spectrogram shall render a scrolling time–frequency intensity map with configurable depth,
colour map, and time-axis marker positioning via a trace-select marker. **[V]**
**AC:** A swept-tone input renders as a diagonal ridge whose frequency at each history row
matches the tone's frequency at that time to within one bin, which verifies that the time and
frequency axes are oriented and scaled correctly rather than merely that something drew.
Depth is configurable and rows beyond it are discarded oldest-first. Moving the trace-select
marker selects the history row at that time and the spectrum trace updates to that row's
data. Colour mapping follows `REQ-UI-024`.

**`REQ-DSP-044` (P1) — Phase unwrapping.** Unwrapped phase shall use a standard
$\pm\pi$-threshold unwrap with a configurable jump tolerance, and shall document its
reference point.
**AC:** A synthetic phase ramp of known slope crossing many $2\pi$ boundaries unwraps to that
ramp with no residual discontinuity, and the recovered slope matches the analytic value to
within 1e-9 relative. The reference point is stated in the trace annotation and is the same
point a second run selects, so unwrapped phase is reproducible rather than dependent on where
the record happened to start. Wrapped and unwrapped traces of the same data agree modulo
$2\pi$ at every point.

**`REQ-DSP-045` (P2) — Group delay.** Group delay shall be computed as
$-\,d\phi/d\omega$ using a configurable aperture (in bins), with the aperture shown in the
trace annotation.
**AC:** A pure delay of $\tau$ seconds returns a flat group delay of $\tau$ across the band
to within 1e-9 s, the closed-form check. The aperture is configurable, appears in the trace
annotation, and widening it visibly smooths the trace on a noisy input — so the annotation
records the setting that produced the displayed result, which is the point of showing it.

**`REQ-DSP-046` (P2) — Trace math.**
A trace-math facility shall support at least: add, subtract, multiply, divide (trace/trace
and trace/constant), magnitude, conjugate, and register store/recall.
**[U]** — the reference product's exact operator list was not confirmed; the set above is a
reasonable floor and shall be extensible.
**AC:** Every listed operator is available for both trace/trace and trace/constant operands
where meaningful, and each is checked against the arithmetic performed directly on the
underlying data. Traces whose X axes are not commensurate are rejected with a named error
rather than combined by index. Division by zero yields the `NAN`/`INF` readouts of
`REQ-UI-032` rather than throwing or silently producing zero. A register survives store and
recall with bit-identical values, and adding an operator requires no change to the trace-math
dispatch, demonstrating the extensibility this requirement asks for.

### 9.6 Time gating

**`REQ-DSP-050` (P1) — Time-gated spectrum.**
Time gating shall select a sub-interval of the time record for spectral analysis, with gate
delay and gate length; under gating, RBW shall track gate length rather than full record
length. **[V]**
**AC:** For a two-tone signal where the tones are present in disjoint time intervals,
gating to each interval reveals only the corresponding tone, with the RBW annotation
correctly reflecting gate length.

---

## 10. Markers, limits and derived measurements

### 10.1 Markers

**`REQ-MKR-001` (P0) — Marker types.**
**Normal** (diamond glyph), **Delta** (referenced to another marker on the same or a
different trace, annotated e.g. "3Δ1"), and **Fixed** (position-locked, "X" glyph). **[V]**
**AC:** All three types are creatable. A Normal marker's readout tracks its data point as the
trace updates; a Fixed marker's X position does not move when the trace updates, which is
what distinguishes it; a Delta marker reads the difference from its reference, and changing
the reference marker changes the delta readout accordingly. Deleting a marker that is another
marker's reference is either refused with a named error or re-homes the dependant — it never
leaves a Delta marker referencing a marker that no longer exists. Glyphs and labels follow
`REQ-UI-030` and `REQ-UI-031`.

**`REQ-MKR-002` (P1) — Marker count.**
**20 markers per trace**, with the number of traces — and therefore the total marker count —
bounded only by available memory. **[V]**
*(This reconciles the reference product's two apparently conflicting claims: the marker help
states 20 per trace, while the marketing copy says "unlimited traces, each with unlimited
markers". The engineering figure of 20 per trace governs; "unlimited" refers to the trace
count. `REQ-UI-001` quotes the marketing phrasing for the docking requirement only.)*
**AC:** Twenty markers can be placed on a trace and the twenty-first is refused with a named
error stating the limit, not silently dropped or allowed. The limit is per trace, so twenty
markers on each of several traces all coexist. Trace count is not capped by a constant: a
test creates traces until memory pressure, and fails if a fixed ceiling below that is
enforced.

**`REQ-MKR-003` (P1) — Marker calculation modes.**
**Band Power** (with Power and Density sub-types; band edges set by left/right marker
positions in the frequency domain, full span in the time domain; units auto-selected from
%, dB, dBm, dB/Hz, Vrms, W and SI-prefixed variants), **Occupied Bandwidth (OBW)**, and
**Adjacent Channel Power (ACP)**. **[V]**
**AC:** Band power over a band containing a single CW tone equals the tone's power to within
0.05 dB. The **99 % OBW** of a root-raised-cosine signal with α = 0.35 equals
**1.167 · R_sym** to within 2 %.

> **Do not use $(1+\alpha)R_{\text{sym}}$ as the 99 % figure — it is the *absolute*
> (null-to-null) bandwidth** and is wrong by 11–16 % across the usual roll-off range:
> α = 0.22 → 99 % OBW 1.085·R_sym (vs 1.22, −11.1 %); α = 0.35 → 1.167 (vs 1.35, −13.6 %);
> α = 0.50 → 1.268 (vs 1.50, −15.5 %). Reserve $(1+\alpha)R_{\text{sym}}$ for the 100 % /
> x-dB-down criterion. This is a common and quietly damaging error in EVM/OBW test code.

**`REQ-MKR-004` (P1) — Marker coupling.** Markers with matching numbers shall couple
across traces, moving together where the X axes are commensurate. **[V]**
**AC:** With coupling on, moving marker 3 on one trace moves marker 3 on every other trace
whose X axis is commensurate to the same X value — not the same pixel or the same sample
index, which a test using traces of differing point counts over the same span distinguishes.
Markers of other numbers do not move. Traces whose X axes are not commensurate — a spectrum
and a time trace — are left alone rather than coupled on a meaningless shared coordinate.
With coupling off, no marker but the dragged one moves.

**`REQ-MKR-005` (P1) — Marker functions.** Peak search, next peak, peak tracking, minimum
search, marker-to-centre-frequency, marker-to-reference-level, and "copy value to
parameter". **[V]**
**AC:** On a synthetic spectrum with tones at known frequencies and descending amplitudes,
peak search finds the largest and repeated next-peak visits the remainder in strictly
descending amplitude order, each within one bin of its true frequency, without revisiting a
peak. Minimum search finds the analytic minimum. With peak tracking on, a tone that drifts
between acquisitions keeps the marker on it. Marker-to-centre-frequency sets centre frequency
to the marker's X value exactly; marker-to-reference-level sets reference level to its Y
value; copy-value-to-parameter writes the marker value into the named parameter and the
measurement updates.

**`REQ-MKR-006` (P2) — Marker readout surfaces.** A dedicated Markers window listing all
markers with readouts, plus an active-marker readout above the trace grid. **[V]**
**AC:** Both surfaces exist and show the same value for the same marker at the same time —
the Markers window row and the above-grid readout are compared after a marker move and must
agree, since two independently computed readouts drifting apart is the failure this guards
against. The above-grid readout follows the active marker when the active marker changes, and
sits above the grid per `REQ-UI-040`. The Markers window lists every marker on every trace,
not only those on the active trace.

**`REQ-MKR-007` (P2) — Spectrogram time-axis markers.** Spectrogram traces shall support
time-axis positioning via trace-select markers. **[V]**
*(Split 2026-07-29. This requirement previously also required three-dimensional marker
placement on OFDM-class personalities, which is now `REQ-MKR-007a` — see there for why.)*
**AC:** A spectrogram carries the two markers of `REQ-UI-054` on different axes, and moving
the trace-select marker to a history row makes the spectrum trace show that row's data,
matching the data captured at that time.

**`REQ-MKR-007a` (P2) — Multi-dimensional markers on an OFDM personality.** OFDM-class
personalities shall support three-dimensional marker placement (symbol × subcarrier × value).
**[V]**

> **Split from `REQ-MKR-007` on 2026-07-29, and the reason is worth recording.** The clause
> was written into a Phase 1 requirement whose other half is provable from the spectrogram
> alone, so Phase 1 could not close while it stood. Worse, `tools/check_phase_atomicity.py`
> could not see the problem: the checker looks for references to other requirement **IDs** in
> an acceptance criterion, and "an OFDM personality" names none. It is the same class of gap
> that once let Phase 1 be declared complete with 37 Phase 0 issues open.
>
> **No requirement in this specification delivers an OFDM personality** — this was, before the
> split, the only mention of OFDM anywhere in it. So this is not a forward dependency on a
> later phase's work; it is a dependency on work that is not scheduled at all. It is placed in
> the personality phase because that is where such a personality would belong, and it cannot
> close until one is specified and built. Anyone reaching for it should raise the personality
> requirement first rather than treat this as a marker defect.

**AC:** On an OFDM personality a marker placed at a given symbol and subcarrier reads the
value at exactly that cell, verified against a generated resource grid with a known
distinguishable value per cell, so a transposed symbol/subcarrier index fails rather than
passing by symmetry.

### 10.2 Limit tests

**`REQ-LIM-001` (P1) — Limit test structure.**
A three-level hierarchy shall be implemented: **Limit Test → Limit Line → Limit Point**,
each user-named. Limit Points carry X/Y coordinates plus a *connect to previous point*
flag, forming connected segments. Each Limit Line carries a **Side** (Upper or Lower)
determining margin direction. **[V]**
**AC:** All three levels exist, each user-named, and names round-trip through save and
recall. A point with *connect to previous* clear starts a new segment, so a trace passing
through the gap between segments is not tested there — verified by a trace that would fail
were the segments joined. Side governs direction: an Upper line fails a trace above it and an
Lower line fails one below, and a test asserts both, since an inverted comparison is the
defect most easily shipped here. Margin is applied on the pass side of the limit, never the
fail side.

**`REQ-LIM-002` (P1) — Pass/fail evaluation and reporting.**
Overall status shall be PASS only if every line passes, otherwise FAIL. Per-line status and
numeric margin shall be available in marker readouts and the status bar. Default rendering:
limit lines red, margin lines yellow. **[V]**
**AC:** A trace crossing an upper limit reports FAIL with the correct worst-case margin and
its X location; a trace exactly touching the limit reports the boundary condition per a
documented, tested convention.

**`REQ-LIM-003` (P2) — Limit tests in the automation API.** Limit count and results shall
be queryable programmatically (the reference product exposes `:TRACe:LIMit:COUNt` and
`:TRACe:LIMit:RESult`). **[V]**
**AC:** Count and result are queryable through the automation API, and the queried result
matches the on-screen pass/fail for the same trace in every case a test exercises — including
the failing case, so the API is not reporting a stale or separately computed verdict. The
query is answerable while a measurement is running, and reports the state of a completed
evaluation rather than a partially updated one.

### 10.3 Channel measurements

**`REQ-CHM-001` (P1) — ACP.** Adjacent channel power shall support configurable carrier
and offset channel definitions (offset frequency, integration bandwidth, filter shape
including root-raised-cosine), reporting absolute and relative power per offset, upper and
lower.
**AC:** A synthetic carrier with adjacent-channel content injected at a known level below it
reports that ratio to within 0.2 dB, per offset, upper and lower reported separately and
correctly distinguished — asymmetric injection is used so a swapped upper/lower fails.
Absolute power agrees with the band power over the same integration bandwidth to within
0.1 dB. Changing the filter shape to root-raised-cosine with a stated roll-off changes the
result in the direction and by the amount the filter's known noise bandwidth predicts.

**`REQ-CHM-002` (P1) — OBW.** Occupied bandwidth shall support the percentage-of-power
criterion (default 99 %) and the x-dB-down criterion.
**AC:** For a signal of analytically known spectrum the 99 % occupied bandwidth matches the
closed-form value to within one bin, and the default is 99 %. The x-dB-down criterion on a
shape whose width at that level is known analytically — a Gaussian, say — matches to within
one bin. The two criteria are independently selectable and give the different answers theory
predicts for the same signal, which is the check that both are genuinely implemented rather
than one aliased onto the other.

**`REQ-CHM-003` (P2) — Spectral emission mask.** A mask-based emission measurement with
per-segment limits and pass/fail, reusing the limit-test engine.
**AC:** A mask of several segments with differing limits evaluates each segment against its
own limit, verified by a signal that passes in one segment and fails in an adjacent one, with
the reported failure naming the offending segment. The measurement reuses the `REQ-LIM-001`
engine rather than reimplementing comparison — a test asserts the shared code path, since a
second implementation is where the Upper/Lower inversion returns.
---

## 11. Flexible digital demodulation engine

This is the heart of the product and the section where implementation risk concentrates.
It corresponds to the reference product's **89601AYAC Digital Demodulation Analysis**
(historically Option AYA).

### 11.1 Demodulation chain

**`REQ-DEM-001` (P0) — Documented processing order.**
The demodulator shall implement the following chain, and the order shall be documented in
code and in user help:

```
1.  Extract Search Length window from Main Time
2.  Burst / pulse search        (optional) ─┐ locate region of interest
3.  Coarse carrier estimate                │
4.  Resample to N points/symbol            │
5.  Measurement (matched) filter           │
6.  Sync-pattern search         (optional) ┘
7.  Position Result Length window
8.  Joint refinement, iterated to convergence:
        carrier frequency · carrier phase · symbol timing · amplitude
9.  Symbol decisions → detected bits
10. Reference regeneration: bits → ideal symbols → reference filter → ideal waveform
11. Adaptive equaliser            (optional; re-enters at 8 on update)
12. Impairment estimation: IQ offset, gain imbalance, quadrature skew, amplitude droop
13. Error metric computation at symbol instants
14. Result trace generation
```

**AC:** The order is declared once and the implementation is driven by that declaration, as
for `REQ-TRC-003`; a test fails if any step executes out of declared order. The optional
steps (2, 6, 11) are skippable without disturbing the order of the rest. Step 11's re-entry
at step 8 is a genuine loop: with the equaliser on, the joint refinement of step 8 runs again
after coefficients update, verified by a signal whose EVM improves on the second pass. Step 8
iterates to a stated convergence criterion with a bounded iteration count, and reaching that
bound is reported rather than silently accepted. The same order appears in the user help, and
a test compares the documented sequence against the declaration so the two cannot drift.

**`REQ-DEM-002` (P0) — Whole-block estimation, not tracking loops.** **[DESIGN CHOICE]**
Steps 3 and 8 shall use block-based maximum-likelihood / least-squares estimation over the
Result Length, not causal PLL/DLL tracking.
*Rationale:* the reference product's documented behaviours — reliable lock on short bursts, and
the "EVM minimum at block centre, growing toward both ends" signature under symbol-rate error —
are **consistent with** a block estimator that fits one frequency/phase/timing solution across
the whole block, and a causal tracking loop would additionally show a settling transient at the
start that the documentation does not describe. Block estimation is also independently the
better engineering choice here: it is non-causal, uses every sample, and has no convergence
transient.
*Honesty about the inference:* the documented signature does not **prove** a block estimator —
a converged tracking loop with a one-shot frequency estimate and a mid-block phase reference
would produce a similar shape. The block approach is therefore a **[DESIGN CHOICE]** supported
by the evidence, not a deduction from it.
**AC:** The symbol-rate-error signature test in `REQ-DEM-030` passes.

### 11.2 Modulation formats

**`REQ-DEM-010` (P0) — Format catalogue.**
The following shall be supported. Formats marked **[V]** are explicitly documented in the
reference product; those marked **[U]** are strongly implied (by the presence of the
corresponding filter or standard preset) but were not directly confirmed, and are
nonetheless required here for completeness.

| Family | Variants | Status |
|---|---|---|
| BPSK | — | [V] |
| QPSK | QPSK, OQPSK, DQPSK, π/4-DQPSK, SOQPSK, shaped OQPSK | [V] QPSK/OQPSK/SOQPSK; [U] π/4-DQPSK |
| 8PSK | 8PSK, 3π/8-8PSK (EDGE), D8PSK | [V] 8PSK; [U] 3π/8, D8PSK |
| Higher PSK | 16PSK | [U] |
| QAM | 16, 32, 64, 128, 256, 512, 1024, 2048, 4096 | [V] |
| QAM variants | DVB-QAM, Star QAM | [V] |
| APSK | custom APSK; user-defined rings | [V] — up to **8 arbitrarily-spaced rings, 256 points** |
| MSK | MSK type 1, MSK type 2 | [V] MSK treated specially in metrics; [U] type 1/2 naming |
| GMSK | with BT parameter | [U] but required |
| FSK | 2FSK, 4FSK, 8FSK, 16FSK | [V] |
| VSB | 8VSB, 16VSB | [V] |
| ASK / OOK | on-off keying | [V] (cited under custom APSK) |
| Custom | user-defined constellation | [V] |

**AC:** Each format round-trips through the simulator: generate → demodulate → recovered
bits identical to transmitted bits at high SNR, RMS EVM < 0.1 %.

**`REQ-DEM-010a` (P1) — Analog demodulation.**
**AM, FM and PM** demodulation shall be provided alongside the digital formats — a first-class
capability of this product line since the 89400 series and a routine bench need.
Required results: demodulated AM depth (%), FM deviation (Hz, peak and RMS), PM deviation
(radians/degrees), modulation rate, SINAD, distortion (THD), and residual FM/AM. Required
traces: demodulated waveform versus time, demodulated audio spectrum, and carrier
frequency/amplitude versus time.
Audio-band de-emphasis and configurable low-pass/high-pass post-detection filtering shall be
supported.
**AC:** A simulated AM signal at known depth and rate, and an FM signal at known deviation and
rate, are each recovered to within 1 % of the injected value; measured SINAD on a clean signal
exceeds 60 dB.

**`REQ-DEM-011` (P1) — User-defined constellations.**
Users shall be able to define a constellation by explicit point list (I, Q, symbol value) or
by ring specification (up to 8 rings, each with radius, point count and phase offset;
≤256 points total), with a chosen bit mapping (Gray, natural, or explicit table).
**AC:** A user-defined 32-APSK (4/12/16 ring structure) demodulates correctly from the
simulator.

**`REQ-DEM-012` (P1) — Differential and offset handling.**
Differential formats shall support differential decoding with a selectable reference; offset
formats (OQPSK and relatives) shall be processed at **2 points per symbol**, because I and Q
are offset by half a symbol. **[V]**
**AC:** A differentially encoded signal demodulates to the transmitted bits with the correct
reference selected, and to a predictably wrong bit stream with the wrong one — so the
reference selection is shown to be effective rather than ignored. Offset formats are
processed internally at 2 points per symbol regardless of the display points-per-symbol of
`REQ-DEM-034`; a test asserts the internal rate, since processing OQPSK at 1 point per symbol
yields plausible-looking but wrong EVM. An OQPSK signal generated with a known half-symbol
I/Q offset demodulates to near-zero EVM, which it cannot do if the offset is mishandled.

**`REQ-DEM-013` (P2) — Low SNR enhancement.** An option extending maximum Result Length
for offset formats from ~2 048 to ~40 000 symbols, matching the reference product. **[V]**
**AC:** With the option off, Result Length for offset formats is capped near 2 048 symbols;
with it on, at least 40 000 symbols are accepted and demodulated. The longer length delivers
the SNR benefit it exists for: on a low-SNR signal the EVM estimate's variance falls
approximately as $1/\sqrt{N}$ between the two lengths, which distinguishes a genuinely longer
analysis from a raised limit that truncates internally.

### 11.3 Pulse-shaping filters

**`REQ-DEM-020` (P0) — Separate measurement and reference filters.**
The measurement filter (applied to the acquired signal) and the reference filter (shaping
the ideal waveform) shall be independently selectable in type and parameter. **[V]**
*Rationale, to be reproduced in help text:* matched root-raised-cosine filters split
Nyquist filtering between transmitter and receiver for optimum SNR; the analyser emulates
the receiver half, so its measurement filter must match the transmitter's shaping, and the
composite response must be the full Nyquist filter for zero ISI at symbol centres.
**AC:** Measurement and reference filters are independently settable in both type and
parameter — a test sets them to different types with different alphas and asserts each takes
effect on its own path. The Nyquist relationship is verified numerically: RRC measurement and
RRC reference at matched alpha give a composite response equal to the raised cosine to within
1e-9, with zero ISI at symbol centres, whereas a mismatched pair does not. The help text
states the transmitter/receiver split.

**`REQ-DEM-021` (P0) — Filter catalogue.**
Root Raised Cosine · Raised Cosine · Gaussian · **EDGE** · Half Sine · Rectangular ·
Low-pass · User-defined FIR · None. **[V]**
Parameters: **alpha** (excess-bandwidth roll-off, for RC/RRC) and **BT** (bandwidth–time
product, for Gaussian).

> **The EDGE filter is not a Gaussian.** It is the **linearised-GMSK main pulse $c_0(t)$**
> defined in 3GPP TS 45.004 — the principal component of the Laurent decomposition of the GMSK
> modulator, used as the transmit pulse for 3π/8-8PSK EDGE. It must be implemented from that
> definition as a distinct filter type, not as a Gaussian with a particular BT.

**AC:** All nine filter types are selectable for both measurement and reference roles, with
alpha exposed for RC/RRC and BT for Gaussian, and each parameter demonstrably changing the
response. The EDGE filter is a distinct type whose coefficients match the linearised-GMSK
main pulse $c_0(t)$ of 3GPP TS 45.004 to within 1e-6 — a test compares against those
published coefficients and fails a Gaussian approximation at any BT, since substituting one
is the specific error this note exists to prevent. `None` applies no shaping, verified by an
output identical to the input.

**`REQ-DEM-022` (P1) — Filter mathematics.**
Raised cosine, in the time domain:

$$h_{RC}(t)=\operatorname{sinc}\!\left(\frac{t}{T}\right)\frac{\cos\left(\pi\alpha t/T\right)}{1-\left(2\alpha t/T\right)^{2}}$$

Root raised cosine:

$$h_{RRC}(t)=\frac{\sin\!\left(\pi\frac{t}{T}(1-\alpha)\right)+4\alpha\frac{t}{T}\cos\!\left(\pi\frac{t}{T}(1+\alpha)\right)}{\pi\frac{t}{T}\left(1-\left(4\alpha\frac{t}{T}\right)^{2}\right)}$$

Gaussian, with bandwidth–time product $BT$:

$$h_{G}(t)=\frac{1}{\sqrt{2\pi}\sigma T}e^{-t^{2}/(2\sigma^{2}T^{2})},\qquad \sigma=\frac{\sqrt{\ln 2}}{2\pi \cdot BT}$$

**AC:** Each filter's coefficients match its formula above to within 1e-12 relative at every
tap, evaluated independently rather than by the implementation under test. The removable
singularities are checked explicitly — $t=0$ for all three, $t = \pm T/(2\alpha)$ for raised
cosine and $t = \pm T/(4\alpha)$ for root raised cosine — and each returns the analytic limit
rather than `NaN` or a divide-by-zero, which is where a formula transcribed correctly still
fails in practice. $\alpha = 0$ reduces the raised cosine to a sinc.

**`REQ-DEM-022a` (P0) — One normalisation convention, stated once.**
The three expressions above are written in their conventional analytic forms — RC and RRC at
**unit peak** (omitting the customary $1/T$ scale), Gaussian at **unit area**. Left as-is these
are three incompatible conventions in adjacent requirements. The implementation shall
therefore: build every filter in its analytic form, then apply **one** documented
normalisation step at a single point in the code, and state which normalisation applies to the
measurement filter and which to the reference filter.

**AC:** Singularities at $t=0$, $t=\pm T/(2\alpha)$ (RC) and $t=\pm T/(4\alpha)$ (RRC) are
handled by analytic limits, not epsilon fudging; unit tests assert continuity across those
points to 1e-9.

**AC — cascade identity, with the scaling and truncation made explicit:** with filters at unit
peak and the discrete convolution scaled by $1/\text{sps}$, a cascade of two RRC filters of
equal α matches the corresponding RC filter to **< 5e-6 RMS at ±64-symbol span**, and to
**< 1e-3 RMS at the ±8-symbol default**.
*Both qualifications are necessary.* Without the $1/\text{sps}$ scale the cascade overshoots RC
by a large factor (≈3.8× at α = 0.35 under unit-energy normalisation), and truncation alone
sets the achievable floor: ±8 sym → 5.4e-4, ±16 → 1.1e-4, ±32 → 1.1e-5, ±64 → 3.2e-6. A flat
"1e-6" tolerance is unreachable at the recommended default length and would fail a correct
implementation.

**`REQ-DEM-023` (P1) — Filter length and truncation.** Filter span (in symbols) shall be
user-settable with a documented default (≥ 8 symbols each side recommended for RRC), windowed
truncation, and the normalisation of `REQ-DEM-022a`. The filter-span/accuracy trade shown
above shall be reproduced in the user help so the default is an informed choice.
**AC:** Filter span is user-settable in symbols with a documented default of at least 8
symbols each side for RRC. Truncation is windowed, not abrupt: the truncated response's
stopband sidelobes are below those of a rectangularly truncated filter of the same span, and
normalisation follows `REQ-DEM-022a` so changing the span does not change the measured
amplitude of a CW tone by more than 0.01 dB. Reducing the span degrades EVM on a
known-clean signal in the direction and magnitude the filter-span/accuracy trade predicts,
and that trade appears in the user help.

### 11.4 Demodulation parameters

**`REQ-DEM-030` (P0) — Symbol rate is supplied, never estimated.**
The symbol rate shall be a user parameter, applied exactly as entered; the demodulator shall
**not** estimate or correct it. On first selection of digital demodulation the default shall
be Span/2. **[V]**
**AC:** *The signature test.* With a deliberate symbol-rate error of 100 ppm, EVM versus symbol
index shall exhibit a minimum near the centre of the Result Length, growing approximately
linearly toward both ends. This reproduces the reference product's documented diagnostic
behaviour and is a **strong indicator** that `REQ-DEM-002`'s block estimator behaves as
intended. *(It is a necessary, not a sufficient, condition — see the honesty note under
`REQ-DEM-002`. Treat a failure as conclusive and a pass as corroborating.)*

**`REQ-DEM-031` (P0) — Result Length.**
Number of symbols demodulated and displayed. Minimum viable values scale with modulation
order — approximately 50 symbols for QPSK/16-QAM rising to about 4 000 symbols for
2048/4096-QAM for reliable carrier lock. **[V]** The UI shall warn when Result Length is
below the recommended minimum for the chosen format.
**AC:** Selecting 1024-QAM with Result Length 50 produces a visible, specific warning naming
the recommended minimum.

**`REQ-DEM-032` (P1) — Pre-demodulation trace window.**
Pre-demodulation traces (Time, Spectrum, Instantaneous Spectrum) shall use a window **20 %
larger** than the Result Length so transition regions are visible. **[V]**
**AC:** For each of the three pre-demodulation traces the analysed window spans 1.2 × Result
Length, checked across several Result Lengths so the factor is proportional rather than a
fixed padding. Result traces are unaffected: EVM computed over the Result Length is identical
whether or not a pre-demodulation trace is displayed. On a bursted signal the transition
regions are visible in the pre-demodulation trace and absent from the result traces, which is
the point of the wider window.

**`REQ-DEM-033` (P0) — Search Length.**
The window searched for sync or burst, expressed in symbols, shall be ≥ Result Length. For
pulse search, Search Length shall satisfy
$\text{Search Length} \geq 2 \times \text{MaxOn} + \text{MaxOff}$. **[V]**
*(The relationship is the **minimum** needed to guarantee one complete pulse falls inside the
window; enforcing equality would prohibit longer searches for no reason.)*
**AC:** Search Length is expressed in symbols and a value below Result Length is rejected
with the minimum reported. For pulse search the constraint
$\text{Search Length} \geq 2\,\text{MaxOn} + \text{MaxOff}$ is enforced as an inequality, not
an equality — a longer Search Length is accepted, and a test asserts that, since enforcing
equality is the plausible misreading. At exactly the minimum, a pulse placed at the least
favourable phase relative to the window is still found complete.

**`REQ-DEM-034` (P1) — Points per symbol is a display parameter.**
Points per symbol shall be settable (typically 1, 2, 4, 5, 10, 20) and shall affect **trace
resolution only**. It shall have **no effect on computed EVM**, since EVM is evaluated at
symbol decision instants. **[V]**
**AC:** All of 1, 2, 4, 5, 10 and 20 points per symbol are settable, and each changes the
point count of the IQ Measured Time and trajectory traces accordingly. EVM, magnitude error,
phase error and every other symbol-instant metric are **bit-identical** across all six
settings for the same input — asserted as exact equality rather than a tolerance, because any
difference means metrics are being evaluated somewhere other than the decision instants.
Offset formats retain their internal 2 points per symbol per `REQ-DEM-012` regardless of this
display setting.

**`REQ-DEM-034a` (P0) — Internal processing rate is independent of the display setting.**
The demodulator shall internally resample to **≥ 4 samples/symbol** (≥ 2 absolute minimum)
regardless of the points-per-symbol display setting.
*Rationale:* an RRC-shaped signal occupies $(1+\alpha)/T > 1/T$. At 1 sample/symbol the signal
is **below Nyquist** and the matched filter cannot be applied without aliasing — so a
requirement that EVM be invariant across a set that includes 1 pt/sym is unsatisfiable unless
the display setting and the processing rate are decoupled. Offset formats additionally require
the I/Q half-symbol stagger to be representable.
**AC:** For a fixed input and a non-offset format, EVM computed with display points/symbol of
1, 4 and 20 is identical to within 1e-9, the internal rate being fixed at ≥4 sps in all three
cases.

**`REQ-DEM-035` (P1) — Mirror frequency spectrum.** An option conjugating the input
spectrum, required for e.g. VSB signals with a high-side pilot. **[V]**
**AC:** With the option on, the analysed spectrum is the conjugate of the input: a tone at
$+f$ appears at $-f$, and applying the option twice returns the original to bit-identical
values. A spectrally inverted signal that fails to demodulate with the option off demodulates
to the correct bits with it on, which is the case the option exists for. The option is
recorded in the metric provenance of `REQ-DEM-072`.

**`REQ-DEM-036` (P1) — Carrier lock tolerance and diagnostics.**
Centre frequency must be within roughly ±10 % of the symbol rate of the true carrier for
lock. **[V]** Failure to lock shall produce a specific diagnostic naming the likely causes —
in documented order of likelihood: wrong symbol rate, wrong filter type or alpha, centre
frequency too far off, Result Length too short for the format.
**AC:** Each of those four fault conditions, injected deliberately, produces the
corresponding diagnostic rather than a bare "demodulation failed".

### 11.5 Search: sync pattern and burst

**`REQ-DEM-040` (P1) — Sync-pattern search.**
Search shall locate a user-specified bit pattern (a multiple of bits-per-symbol in length)
within Search Length, with a **Search Offset** positioning the Result Length window relative
to the start of the pattern. Only the **first** match shall be used. Sync search shall be
**optional** — carrier locking shall not depend on it. **[V]**
**AC:** With a known pattern inserted at a known position by the simulator, the Result
Length window lands at the specified offset from that position, to the symbol.

**`REQ-DEM-041` (P1) — Burst/pulse search.**
Pulse search shall locate the first *complete* pulse within Search Length. Detection
requires the pulse to be at least **15 dB** above the noise floor. **[V]** Without sync
search, the Result Length window shall be auto-centred on the detected pulse; with sync
search, positioning shall follow the pattern and offset instead. Only the first pulse in a
capture is analysed. **[V]**
**AC:** A simulated burst 20 dB above noise is found and centred; the same burst at 10 dB
above noise is reported as not found, rather than silently mis-locating.

### 11.6 Adaptive equalisation

**`REQ-DEM-050` (P1) — Adaptive equaliser.**
An adaptive equaliser shall derive its coefficients from the measured signal, correcting
linear channel impairments: group-delay distortion, frequency-response error, and
reflections/multipath. **[V]**
**AC:** Each of the three impairment classes is injected separately by the generator of
`REQ-SIM-002` at a known magnitude — group-delay distortion, frequency-response tilt, and a
two-ray multipath channel — and in each case enabling the equaliser reduces EVM to within
1 dB of the unimpaired value. Coefficients derive from the measured signal, not from the
injected channel: the test supplies no knowledge of the impairment. On an unimpaired signal
the equaliser leaves EVM unchanged to within 0.1 dB rather than degrading it, and the
estimated channel response matches the injected one to within 0.5 dB and 5° in band.

**`REQ-DEM-051` (P1) — Equaliser parameters and modes.**
**Filter Length** in symbols; **Convergence factor** (LMS step size); modes **Run** (update
coefficients from the current measurement for use on the next), **Hold** (freeze), **Reset**
(return to a unit-impulse response). **[V]**
Impulse-position behaviour shall match the documented behaviour: for short filter lengths
the impulse sits at the filter centre; as length grows the impulse "moves proportionally
towards the start of the filter" to accommodate channels with large delay spread. **[V]**
**AC:** Filter Length is settable in symbols and Convergence factor as a step size. **Run**
updates coefficients from the current measurement and applies them to the next, so
coefficients change between successive measurements; **Hold** freezes them, asserted by
bit-identical coefficients across measurements; **Reset** returns a unit-impulse response.
The impulse position follows the documented behaviour: at short filter lengths the impulse
sits at the centre, and as length grows its index moves proportionally toward the start —
measured across at least three lengths and checked as a trend, since a fixed-centre
implementation passes the short case and fails the long one.

**`REQ-DEM-052` (P1) — Equaliser algorithm: least-squares primary, LMS for parity.**
**[DESIGN CHOICE]**

*Primary solution.* Because `REQ-DSP-001`/`REQ-DEM-002` mandate whole-block, non-causal
processing **and** step 10 of `REQ-DEM-001` already regenerates the full reference sequence
$r_k$, the equaliser shall by default compute the **exact regularised least-squares (Wiener)
solution** in one shot:

$$\mathbf{w}=\left(\mathbf{X}^{H}\mathbf{X}+\lambda\mathbf{I}\right)^{-1}\mathbf{X}^{H}\mathbf{d}$$

This is optimal, deterministic, has no convergence dependence, and needs no step size. With
the reference already in hand, an iterative gradient method is strictly worse here.

*LMS mode, retained for behavioural parity.* A decision-directed complex LMS mode shall also be
provided, since the reference product's exposed controls (filter length, convergence factor,
Run/Hold/Reset) imply incremental adaptation and users may depend on its transient behaviour:

$$y_n=\mathbf{w}^{H}\mathbf{x}_n,\qquad e_n=d_n-y_n,\qquad \mathbf{w}\leftarrow\mathbf{w}+\mu\, e_n^{*}\,\mathbf{x}_n$$

Required additions, absent from the reference documentation and specified here:

- **Stability bound** $0 < \mu < 2/(L\,P_x)$, enforced and reported, or **NLMS**
  ($\mu_n = \tilde\mu/(\varepsilon + \|\mathbf{x}_n\|^2)$) selected instead.
- **Acquisition mode** for when decisions are unreliable at start-up: CMA (blind) or
  data-aided from a known sync sequence, switching to decision-directed once EVM falls below a
  threshold.
- **Tap count disambiguation:** "Filter Length in symbols" at T/2 spacing means **2N taps for
  an N-symbol filter**. State this in the UI; it is a frequent source of confusion.

*Honesty note:* the reference product does not publish its adaptation algorithm. The exposed
parameter set (length, convergence factor, run/hold/reset) is equally consistent with LMS,
NLMS, CMA and RLS-with-forgetting-factor, so it constitutes **no evidence** for any particular
choice. The selection above is made on engineering grounds.

**AC:** The least-squares solution is the default and is exact: on a channel with a known
finite-impulse-response, the computed $\mathbf{w}$ matches the analytic regularised solution
to within 1e-9, and repeated runs on identical input give bit-identical coefficients — it has
no convergence dependence, so any run-to-run variation is a defect. LMS mode converges on the
same channel to within 1 dB of the least-squares EVM. The stability bound
$0 < \mu < 2/(L P_x)$ is enforced and a violating step size is rejected with the bound
reported, or NLMS is selected instead; a test drives $\mu$ past the bound and asserts the
equaliser does not diverge. Both acquisition modes — CMA and data-aided — bring a signal
whose initial decisions are unreliable to a locked state, then hand over to
decision-directed at the stated EVM threshold. An N-symbol filter at T/2 spacing reports
**2N taps**, and the UI states that relationship.

**`REQ-DEM-053` (P1) — Equaliser output traces.**
**Equalizer Impulse Response** and **Channel Frequency Response** shall be available as trace
data. The channel response is the inverse of the equaliser response, with magnitude and phase
(and optionally group delay). **[V]**

**Regularisation is mandatory here.** A pointwise $1/W(e^{j\omega})$ diverges wherever the
equaliser response has a null, producing spikes that look like real channel features. The
inversion shall be regularised — $W^*/(|W|^2+\varepsilon)$ with $\varepsilon$ set from the
noise floor, or equivalent — and the regularisation shall be documented and annotated on the
trace.
**AC:** With a simulated two-ray multipath channel of known delay and amplitude, the
recovered channel frequency response matches the analytic response to within 0.5 dB in
magnitude across the occupied band, and equalisation reduces EVM to within 1.2× the
unimpaired value.

### 11.7 Error metrics — definitions and mathematics

Let $z_k$ be the measured symbol-instant samples after matched filtering, carrier/timing
correction and normalisation; $r_k$ the ideal reference symbols regenerated from the
detected bits through the reference filter; $N$ the Result Length; and $V_{\text{norm}}$ the
**EVM Normalisation Reference**.

**`REQ-DEM-060` (P0) — EVM.**

$$\mathrm{EVM_{RMS}}=\frac{\sqrt{\dfrac{1}{N}\displaystyle\sum_{k=1}^{N}\left|z_k-r_k\right|^{2}}}{V_{\text{norm}}}\times100\%$$

EVM shall be computed **only at symbol decision instants**. **[V]** Peak EVM shall be reported
as

$$\mathrm{EVM_{peak}}=\frac{\max_k\left|z_k-r_k\right|}{V_{\text{norm}}}\times100\%$$

together with the index $k$ at which it occurs. **[V]**

**AC:** For a constellation impaired by additive noise of known variance, EVM matches the
closed-form value to within 0.1 % relative — the closed-form check `REQ-TST-001` requires.
A single symbol displaced by a known amount produces the analytically predictable
$\mathrm{EVM_{peak}}$, and the reported index $k$ is that symbol's index exactly. EVM is
computed only at decision instants, which `REQ-DEM-034`'s points-per-symbol invariance
establishes. An unimpaired reference signal returns EVM below 1e-6 %, confirming no error is
introduced by the measurement chain itself.

**`REQ-DEM-061` (P0) — EVM normalisation reference.**
$V_{\text{norm}}$ shall be user-selectable among **maximum constellation magnitude**, **RMS
(mean power) of the reference constellation**, and a **user-specified value**. **[V]**

The choice only has consequences for **variable-envelope** formats (QAM, APSK, Star QAM); for
constant-modulus formats (BPSK, QPSK, 8PSK, MSK) the maximum and RMS magnitudes are the same
number and the setting is inert. The default for variable-envelope formats shall be stated
explicitly in the UI rather than inherited silently.
**AC:** Switching normalisation between max-magnitude and RMS for a 16-QAM signal changes
reported EVM by exactly the analytically predicted ratio
($\sqrt{P_{\max}/P_{\text{avg}}}$), confirming the normalisation is applied and not
hard-coded. *This is a frequent source of apparent disagreement between instruments and
must be explicit in the UI readout.*

**`REQ-DEM-062` (P1) — Offset EVM.** For offset formats, an **Offset EVM** variant shall
be computed using one point per symbol formed from a complex point whose real and imaginary
parts are taken from different time locations. **[V]**
**AC:** For offset formats, Offset EVM forms one point per symbol from I and Q sampled half a
symbol apart, and on a clean OQPSK signal returns near-zero where conventional EVM computed
at a common instant does not — the difference between the two is what shows the half-symbol
stagger is honoured. On a non-offset format the variant is unavailable rather than computing
a meaningless value.

**`REQ-DEM-063` (P0) — Magnitude error.**

$$\mathrm{MagErr}=\frac{\sqrt{\dfrac{1}{N}\displaystyle\sum_{k}\left(\left|z_k\right|-\left|r_k\right|\right)^{2}}}{V_{\text{norm}}}\times100\%$$

Not applicable to FSK/CPM-FM formats, where amplitude is not the modulated parameter. **[V]**
**AC:** A constellation impaired by a known pure magnitude error returns that value to within
0.1 % relative, and a signal impaired by pure phase error returns a magnitude error near zero
— the pair establishes that the metric separates magnitude from phase rather than mixing
them. For FSK and CPM-FM formats the row is absent from the error summary per `REQ-DEM-071`,
not shown as zero or `NAN`.

**`REQ-DEM-064` (P0) — Phase error.**

$$\mathrm{PhErr}=\frac{180}{\pi}\sqrt{\dfrac{1}{N}\sum_{k}\left[\arg\!\left(z_k r_k^{*}\right)\right]^{2}}\quad\text{degrees}$$

with $\arg(\cdot)$ returning the principal value in $(-\pi, \pi]$. The explicit $180/\pi$ is
not decoration — the bare expression returns radians and the reported quantity is degrees.
**AC:** A constellation impaired by a known pure phase error returns that value **in
degrees** to within 0.1 % relative. The units check is explicit: a signal with a 1 radian
error reports approximately 57.3, not 1 — the radians/degrees slip is the defect this
requirement calls out, so it is asserted rather than left to inspection. Symbols whose error
approaches $\pm\pi$ are handled by the principal-value branch without wrapping to the wrong
sign, tested at the boundary.

**`REQ-DEM-065` (P0) — Frequency error.**
The carrier frequency error relative to the analyser centre frequency, in Hz — equivalently
the frequency shift the analyser applied to achieve carrier lock. **[V]**
**AC:** A simulated 1 kHz carrier offset is reported as 1 kHz ± 0.1 Hz.

**`REQ-DEM-066` (P0) — IQ origin offset (carrier feedthrough).**

$$\mathrm{IQoffset}=20\log_{10}\!\left(\frac{\left|c\right|}{V_{\mathrm{ref,RMS}}}\right)\ \mathrm{dB},\qquad c = c_I + j c_Q$$

where $(c_I, c_Q)$ are the offset terms from the impairment fit of `REQ-DEM-067` and
$V_{\mathrm{ref,RMS}}$ is the **RMS magnitude of the reference constellation**. Zero carrier
feedthrough gives $-\infty$ dB. Computed at symbol times, **except for MSK, which uniquely uses
all points rather than only symbol instants**. **[V]**

Two deliberate choices here, both departing from the obvious implementation:

- **Normalise to a fixed reference, not to $V_{\text{norm}}$.** Using the EVM normalisation
  reference would make the reported IQ offset depend on an unrelated user setting — the same
  signal would report two values 2.55 dB apart for 16-QAM depending on the EVM normalisation
  selection. IQ offset is a property of the signal and must not move when an EVM display option
  changes.
- **Take the offset from the impairment fit, not from $\frac1N\sum_k z_k$.** The raw mean is
  biased whenever a short block carries an unbalanced symbol sequence; $\frac1N\sum_k (z_k-r_k)$
  or the fitted $(c_I,c_Q)$ is unbiased and stays consistent with the gain-imbalance and skew
  estimates, which are derived from the same fit.

**AC:** A signal with a known injected carrier feedthrough returns that value to within
0.1 dB, and a signal with none returns $-\infty$ dB (or the stated floor) rather than a large
negative artefact. Both departures are asserted directly. First, changing the EVM
normalisation selection leaves the reported IQ offset **bit-identical** — for 16-QAM the
naive implementation shifts by 2.55 dB, so this test discriminates. Second, on a short block
with a deliberately unbalanced symbol sequence the reported offset matches the injected value
within tolerance, where $\frac1N\sum_k z_k$ would be measurably biased. MSK is computed over
all points rather than symbol instants, and a test asserts MSK differs from the
symbol-instant computation for the same signal.

**`REQ-DEM-067` (P1) — IQ gain imbalance and quadrature skew.** **[DESIGN CHOICE for the
estimator]**
Fit, by least squares over the symbol set, the **symmetric** affine impairment model — each
axis rotated by half the skew angle:

$$\Re\{z_k\}=g_I\left(\Re\{r_k\}\cos\tfrac{\psi}{2}+\Im\{r_k\}\sin\tfrac{\psi}{2}\right)+c_I$$
$$\Im\{z_k\}=g_Q\left(\Re\{r_k\}\sin\tfrac{\psi}{2}+\Im\{r_k\}\cos\tfrac{\psi}{2}\right)+c_Q$$

then report **gain imbalance** $=20\log_{10}(g_Q/g_I)$ dB and **quadrature error** $=\psi$
degrees. Gain imbalance manifests as a rectangular stretch along the ideal I/Q axes;
quadrature skew as a stretch along a 45° line. **[V for the geometric descriptions]**

**Why symmetric and not a one-sided shear.** Putting the whole skew on Q
($\Im\{z\}=g_Q(\Re\{r\}\sin\psi+\Im\{r\}\cos\psi)$) is a *shear*, and a shear decomposes as a
rotation by $\psi/2$ composed with the symmetric skew. That $\psi/2$ rotation is
indistinguishable from carrier phase and is silently absorbed by the step-8 phase estimate —
so $\psi$ becomes unidentifiable and the reported value depends on estimator ordering. The
symmetric form has no rotational component, which makes $\psi$ identifiable. It also matches
the "stretch along a 45° line" description, which the shear form does not.

**AC:** Signals with known injected gain imbalance and quadrature skew return those values to
within 0.05 dB and 0.1° respectively, including when both are present together — the
cross-term case, where a one-at-a-time estimator passes the singles and fails the pair. The
model fitted is the symmetric one: injecting pure quadrature skew and then estimating carrier
phase returns a phase near zero, whereas the one-sided shear model absorbs $\psi/2$ into
phase and reports a non-zero value, so this test distinguishes the two forms directly. Gain
imbalance is positive when Q exceeds I, asserted against the stated convention.

**`REQ-DEM-067a` (P1) — Joint estimation of phase and skew.**
Carrier phase (step 8) and quadrature skew shall be **jointly fitted**, or the phase estimate
shall be explicitly constrained, so that the $\psi/2$ ambiguity above is resolved
deterministically. The chosen convention shall be documented and asserted in tests.

> **Documented interaction that must be reproduced.** Quadrature skew can be mis-attributed as
> gain imbalance (and vice versa) depending on the transmitter's symbol-mapping convention
> relative to the receiver's reference axes. **[V]** The UI shall document this, and the test
> suite shall include the ambiguous case so the behaviour is characterised and stable rather
> than accidental. The sign convention for gain imbalance shall be stated explicitly
> (positive = Q larger than I, per the formula above).

**AC:** Phase and skew are estimated from one joint fit, or the phase estimate carries an
explicit documented constraint; either way, estimating the same signal twice from different
initial conditions returns the same split between phase and skew to within 0.01°, which is
what "resolved deterministically" means and what an unconstrained sequential estimator cannot
deliver. The documented convention is asserted in tests rather than only described. The
ambiguous case — a symbol-mapping convention under which skew and gain imbalance trade off —
is in the test suite with its outcome pinned, so the behaviour is characterised and a change
to it shows up as a test failure rather than as a silently different number.

**`REQ-DEM-068` (P1) — Rho.**

$$\rho=\frac{\left|\sum_k z_k r_k^{*}\right|^{2}}{\left(\sum_k\left|z_k\right|^{2}\right)\left(\sum_k\left|r_k\right|^{2}\right)}$$

The normalised correlated power between measured and reference signals — the waveform
quality factor, maximum 1.0 for a perfect match. **[V]**
**AC:** A perfect match returns $\rho = 1.0$ to within 1e-12, and $\rho$ never exceeds 1.0
for any input — asserted over randomised impairments, since a normalisation error shows up as
$\rho > 1$. For a signal impaired by additive noise of known SNR, $\rho$ matches the
closed-form relation to EVM to within 1e-6. $\rho$ is invariant to a common scaling or a
common phase rotation of the measured signal, which is what makes it a waveform-quality
figure rather than an amplitude comparison.

**`REQ-DEM-069` (P1) — SNR (MER).**

$$\mathrm{SNR_{dB}}=10\log_{10}\frac{\sum_k\left|r_k\right|^{2}}{\sum_k\left|z_k-r_k\right|^{2}}$$

Defined by the reference product as the ratio of average symbol power to noise power, where
"noise power includes anything that causes the symbol to deviate from the ideal state
position, including additive noise, distortion, and ISI." **[V]** Applicable to QAM,
DVB-QAM, 8PSK, QPSK, APSK and VSB; VSB uses a real-only variant. **[V]**
*Naming note:* the wider industry calls this MER. The reference product calls it SNR.
OpenVSA shall display "SNR (MER)" to avoid ambiguity.
**AC:** For a signal with additive noise at a known SNR the reported value matches to within
0.1 dB, and it satisfies the closed-form identity with EVM to within 1e-6. Distortion and ISI
count as noise: a signal degraded by ISI alone, with no additive noise, reports a finite SNR
rather than infinity — the check that the definition quoted here was implemented rather than
a conventional additive-noise-only one. The label renders exactly `SNR (MER)`. It is offered
for QAM, DVB-QAM, 8PSK, QPSK, APSK and VSB, with VSB using the real-only variant, and is
absent for other formats.

**`REQ-DEM-070` (P1) — Format-specific metrics.**
**Amplitude droop** (dB/symbol, from a linear fit of log magnitude versus symbol index —
MSK/GSM class), **FSK error** and **FSK deviation** (FSK formats), **carrier offset**,
**pilot level** (VSB), and **time offset** shall be computed where applicable to the
selected format. **[V]**
**AC:** Each metric is checked against a deliberately injected impairment of known magnitude
and matches within the `REQ-SIM-002` tolerance: amplitude droop in dB/symbol from a signal
with a known log-magnitude slope, FSK error and deviation against a generated FSK signal of
known deviation, carrier offset against a known frequency offset, pilot level against a VSB
signal with a known pilot, and time offset against a known timing shift. Amplitude droop
comes from a linear fit of log magnitude versus symbol index, verified by a signal whose
droop is exactly linear returning a fit residual near zero. Each metric appears only for
formats it applies to, per `REQ-DEM-071`.

**`REQ-DEM-071` (P0) — Error summary table.**
All applicable metrics shall be presented in an Error Summary table trace, with units,
and with rows automatically shown or hidden according to format applicability. **[V]**
**AC:** For each supported format the visible rows are exactly the applicable metrics —
enumerated per format, so an inapplicable row appearing (magnitude error on FSK, say) fails,
as does an applicable row missing. Rows appear and disappear on a format change with no
manual step. Every row carries units, and the layout follows `REQ-UI-053`. A metric that is
applicable but not yet computed shows `NAN` per `REQ-UI-032` rather than a stale value from
the previous format.

**`REQ-DEM-072` (P1) — Metric provenance.**
The UI shall make visible, for the active measurement, which normalisation reference, which
filters, and which compensations (equaliser on/off, IQ offset removed or not) were in force
when the metrics were computed.
*Rationale:* EVM numbers are meaningless without this context, and disagreements between
instruments almost always trace to it.
**AC:** For the active measurement the UI shows the normalisation reference in force, both
filter selections with their parameters, and the state of each compensation — equaliser on or
off, IQ offset removed or not, mirror spectrum on or off. Changing any of them updates the
displayed provenance in the same measurement cycle as the metric it qualifies, so the two can
never disagree; a test changes a compensation and asserts the metric and its provenance
update together. The provenance travels with exported results and saved states, since a
number recalled without its context is the failure this prevents.

### 11.8 Demodulation result traces

**`REQ-DEM-080` (P0) — Result trace catalogue.**
The following shall be available as trace data sources: **[V]**

| Trace | Content |
|---|---|
| IQ Measured Time | Measured complex waveform |
| IQ Reference Time | Regenerated ideal complex waveform |
| Constellation | Symbol-instant points in the I/Q plane |
| IQ Vector / Trajectory | Continuous inter-symbol path in the I/Q plane |
| Eye Diagram (I-Eye, Q-Eye) | I or Q versus time, folded on the symbol clock |
| Trellis | Phase versus time, folded — **[U]**, not confirmed in the reference product but conventional and cheap to provide |
| Error Vector Time | $|z_k - r_k|$ versus time/symbol |
| Error Vector Spectrum | Spectrum of the error vector sequence — reveals systematic/periodic impairments |
| Magnitude Error | Magnitude error versus time/symbol |
| Phase Error | Phase error versus time/symbol |
| Symbol Table / Bit Stream | Detected symbols and bits, hex or binary, with sync-pattern highlighting |
| Error Summary | The metrics of §11.7 |
| Equalizer Impulse Response | Equaliser coefficients |
| Channel Frequency Response | Estimated channel magnitude/phase |

**AC:** Every listed trace is selectable as a data source and produces data for a demodulated
signal. Each is checked for correctness rather than mere presence, against a signal with one
known injected impairment: Error Vector Time peaks at the symbol carrying the injected error,
Error Vector Spectrum shows a line at the rate of a periodic impairment, IQ Reference Time
matches the generator's ideal waveform to within 1e-9, and Constellation and IQ Vector differ
exactly as `REQ-UI-050` requires. Traces that depend on an optional stage — Equalizer Impulse
Response, Channel Frequency Response — are unavailable rather than empty when the equaliser
is off. Trellis is provided per the **[U]** note and is not claimed as reference parity.

**`REQ-DEM-081` (P1) — Eye diagram construction.**
The eye shall be built by superimposing the measured I (or Q) waveform across the Result
Length, folded on the symbol clock, with a configurable eye length in symbols (default 2)
and optional persistence shading. **[V]**
**AC:** The eye is built from the measured waveform across the whole Result Length, folded on
the symbol clock — the trace count equals the number of folds the Result Length and eye
length imply, so a partial build fails. Eye length defaults to 2 symbols and is configurable
over the `REQ-UI-051` range. Persistence shading, when on, makes frequently traversed paths
visibly denser than rare ones, and turning it off leaves the eye's geometry unchanged. The
rendered eye satisfies `REQ-UI-051`'s centring and reference-line criteria.

**`REQ-DEM-082` (P1) — Constellation rendering quality.**
Constellation and vector traces shall support: ideal-point overlay, per-point colouring by
error magnitude, decision-boundary overlay, digital persistence with configurable decay, and
optional density (heat-map) rendering for large symbol counts.
**AC:** 100 000 symbols render at ≥10 fps with persistence enabled (see `REQ-NFR-005`).

**`REQ-DEM-083` (P2) — Symbol table interaction.** Selecting a symbol in the table shall
highlight the corresponding point in the constellation and position in the eye, and vice
versa.
**AC:** Selecting symbol $k$ in the table highlights the constellation point and eye position
for symbol $k$ specifically — verified against a signal in which one symbol is displaced so
the correct point is identifiable, which an off-by-one selection fails. Selection propagates
in both directions and settles: selecting in the constellation highlights the table row
without re-triggering a further selection. Selection survives a measurement update if the
symbol still exists, and clears cleanly if it does not.

---

## 12. Standard-specific measurement personalities

### 12.1 Personality framework

**`REQ-PER-001` (P0) — Personality SDK.**
A documented plug-in contract shall allow a personality to declare its measurements,
contribute setup UI, consume the capture stream, reuse core DSP and demodulation services,
and publish trace data types. A personality declares no entitlement and is never gated: if
it loads, it runs (`REQ-LIC-010`).

```csharp
public interface IMeasurementPersonality
{
    PersonalityId Id { get; }
    string        DisplayName { get; }
    IReadOnlyList<MeasurementDescriptor> Measurements { get; }
    IMeasurementInstance Create(MeasurementDescriptor descriptor, IAnalysisServices services);
}

public interface IAnalysisServices          // what the host lends to personalities
{
    IFftProcessor      Fft        { get; }
    IDemodulator       Demod      { get; }
    IResampler         Resampler  { get; }
    IFilterFactory     Filters    { get; }
    ITraceSink         Traces     { get; }
    IMarkerService     Markers    { get; }
    ILimitService      Limits     { get; }
}
```

**AC:** A reference personality implemented purely against this interface — with no access
to host internals — produces correct results, proving the contract is sufficient.

**`REQ-PER-002` (P1) — Personality isolation.** A personality fault shall not terminate
the host. Personalities shall load into a controlled context with exception isolation at
the measurement boundary, and a faulting personality shall be disabled with a clear report.
**AC:** A deliberately faulting personality is exercised across the failure modes that
actually occur — throwing during `Create`, throwing mid-measurement, returning malformed
results, and hanging — and in every case the host survives, the measurement is abandoned
cleanly, and the personality is disabled with a report naming it and the fault. Other
loaded personalities keep running, which is what distinguishes isolation from a global
try/catch. Buffers the faulting personality held are returned to the pool rather than
leaked, per `REQ-NFR-011`, so a repeatedly faulting personality cannot exhaust memory —
asserted by faulting in a loop and checking pool occupancy returns to its starting level. A
disabled personality stays disabled until explicitly re-enabled, rather than being retried
on the next acquisition. This covers faults only; plug-in *malice* is `REQ-NFR-041`.

**`REQ-PER-003` (P2) — Third-party SDK parity.** The SDK shall be documented and
distributable to third parties, mirroring the reference product's Option 301 multi-vendor
approach. **[V]**
**AC:** The parity claim is tested rather than asserted: a personality built **only** against
the published SDK, referencing no internal assembly, implements a complete measurement and
runs in the host — so a first-party personality reaching past the SDK for something it does
not expose fails the test. The SDK ships as a versioned package with the documentation of
`REQ-NFR-038` and a worked example that builds from a clean checkout. Its surface follows
`REQ-NFR-042`'s versioning and deprecation policy, since third parties carry the cost of
breaking changes. Distribution is unrestricted per `REQ-LIC-010` — there is no Option 301
equivalent to buy, and no entitlement gates SDK use.

### 12.2 Personality catalogue and priority

Full-clone scope implies the catalogue below. Priority is set by **(a)** what can be
validated against the E4406A on this bench, then **(b)** breadth of usefulness.

| Wave | Personality | Rationale |
|---|---|---|
| **1** | **GSM / EDGE** (GMSK + 3π/8-8PSK) | Validatable against E4406A options BAH + 202/252 |
| **1** | **W-CDMA / HSPA** | Validatable against E4406A option BAF |
| **1** | **cdmaOne (IS-95)** | Validatable against E4406A option BAC |
| **2** | **cdma2000 / 1xEV-DO** | Natural extension of the CDMA engine |
| **2** | **NADC / PDC** | π/4-DQPSK; small increment once the engine exists |
| **2** | **Custom OFDM** | Foundation for all later OFDM personalities |
| **3** | **802.11a/g/n/ac** | Builds directly on custom OFDM |
| **3** | **802.11ax/be** | OFDMA extension |
| **3** | **Bluetooth / BLE** | GFSK; reuses the FSK path |
| **4** | **LTE FDD / TDD** | Large; requires full resource-grid modelling |
| **4** | **LTE-Advanced** | Carrier aggregation on the LTE base |
| **5** | **5G NR** | Largest single personality; flexible numerology |
| **5** | **DVB-S2/S2X** | APSK; reuses the APSK constellation work |
| **5** | **DOCSIS** | QAM/OFDM cable |
| **6** | **TETRA, APCO P25, DECT, iDEN** | Land-mobile/professional radio family |
| **6** | **802.15.4 / Zigbee** | O-QPSK DSSS |
| **6** | **RFID, UWB** | Specialist |
| **6** | **SOQPSK / IRIG-106 telemetry** | Aerospace telemetry |

**`REQ-PER-010` (P1)** — Wave 1 personalities shall each be validated by direct numerical
comparison against the E4406A's own measurement of the same signal (see `REQ-E44-007`).
**AC:** Each wave 1 personality — GSM/EDGE, W-CDMA/HSPA, cdmaOne — is measured against the
E4406A's own personality on the **same capture**, not merely the same signal generator
settings, so the comparison isolates analysis from acquisition. Agreement is within
`REQ-TST-004a`'s tolerance: relative to the measured value plus the residual budget, never a
bare absolute figure. The comparison runs under the `REQ-TST-004` harness, so a divergence
beyond tolerance either fails or is recorded in the register with its explanation — a
personality cannot be declared validated with an unexplained divergence outstanding. Where
the E4406A does not implement a measurement the personality provides, that gap is stated
rather than passed over, since an unvalidated metric shipping alongside validated ones is
the trap here.

**`REQ-PER-011` (P2)** — Each personality shall declare the standard revision it
implements, and shall state clearly in its help where it deviates or is incomplete.
*Rationale:* silent partial conformance to a telecom standard is worse than a documented
subset, because users will make pass/fail decisions on it.
**AC:** Every personality declares the standard and revision it implements — including the
issue or release date, since "LTE" without a revision identifies nothing — and that
declaration is machine-readable, surfaced in the UI, and carried into exported results and
reports alongside the provenance of `REQ-DEM-072`. A personality shipping without a
declared revision fails the build rather than defaulting to "latest". Deviations and
unimplemented parts are listed explicitly in its help, and the list is testable rather than
prose: each declared deviation names the clause it departs from. A measurement whose
conformance is partial says so where the result is read, not only in a help page the user
must go looking for — the whole point being that someone is about to make a pass/fail
decision on the number.
---

## 13. User interface — appearance and behaviour

This section is deliberately the most detailed in the document, because the reference
product's interface is not incidental: its layout conventions, its annotation placement and
above all its **clickable-annotation ("hot spot") interaction model** are what make it fast to
use, and they are the part a reimplementation most easily gets wrong by substituting
conventional modern-app design.

**Evidence grading is used throughout and matters here more than anywhere else.** Keysight's
documentation is unusually literal about *structure* — window classes, docking behaviour,
annotation positions, marker glyph shapes, message strings — and almost entirely silent about
*pixels*: default colour values, graticule division counts, typefaces and point sizes appear
nowhere in the published material. Those live only inside screenshots. Every such item is
marked **[U]** below with a recommended value, so the team knows exactly which choices are
theirs to make and which are constrained.

### 13.1 Overall window anatomy

**`REQ-UI-001` (P0) — Docking-window application, not an instrument emulator.**
The shell shall be a docking-window application with two distinct window classes: **[V]**

- **Document windows** — dock into the **central document area** as **tabs in a tab group**.
  Only trace-bearing windows may be document windows.
- **Tool windows** — dock **around the edges** of the document area and carry a title bar
  rather than a tab.

**There shall be no right-hand softkey column.** The reference *software* has none; that
belongs to the 89400-series *hardware* (§13.10). Adding one would be a retro affectation, not
fidelity. **[V-negative]**

Top-to-bottom the shell is: **title bar → menu bar → toolbar band (multiple dockable
toolbars) → document area with trace tab groups, tool windows docked around its edges →
status bar.** **[V for the components; U for the exact order, though it is conventional.]**
**AC:** A trace window docks only into the central document area and appears there as a tab
in a tab group; a tool window docks only around the edges and carries a title bar, not a tab.
Attempting to dock a tool window into the document area, or a trace window to an edge, is
refused rather than accepted — the two classes are distinct, not merely conventionally used
differently. No right-hand softkey column exists anywhere in the shell, asserted by a test
over the shell's visual tree so it cannot be reintroduced unnoticed. The shell's top-level
children appear in the stated order.

**`REQ-UI-002` (P1) — Named tool windows.**
Markers · Output · Player · SCPI Log · Event Log · Contexts · Block Diagram · Macros. **[V]**
**AC:** All eight exist under exactly these names, are openable from the Window or Marker
menu per `REQ-UI-061`, and each is a tool window by the `REQ-UI-001` test. Each one's docked
position, size and open/closed state persist across a restart.

**`REQ-UI-003` (P1) — Detachable trace windows are full secondary windows.**
Dragging a trace out of the document area shall create a **Trace Window**: "a regular
application window with menus and toolbars, similar to the main VSA application window,
except that the Trace Window contains a subset of the menu items" — supporting all
trace-specific operations (markers, spectrograms, adding/deleting traces, scaling) plus
limited measurement control (restart, pause, set selected measurement). **[V]**
**AC:** A detached trace window has its own working menu bar and toolbar, operates
independently on a second monitor, and is captured in saved layout state.

**`REQ-UI-004` (P1) — Tab group conventions.**
- The **close button sits to the *left* of all the tabs**, not on each tab and not at the
  right. **[V]** *(Unusual, easily "corrected" by a developer into the conventional position —
  don't.)*
- The **active trace's tab title is rendered in bold**. **[V]**
- Users resize by dragging the boundary between windows; a **Resize Traces** command evenly
  redistributes. **[V]**

**AC:** The close button's rendered bounds sit to the left of the leftmost tab's bounds, and
no per-tab close button exists — asserted on geometry, not on a style name, because this is
the detail a developer will "fix" into the conventional right-hand position. The active
trace's tab is the only one rendered bold, and changing the active trace moves the bold.
Dragging a boundary resizes the adjacent windows only; **Resize Traces** leaves all trace
windows within one pixel of equal size.

**`REQ-UI-005` (P1) — Trace layout presets.**
The layout menu shall offer, by these names: **[V]**

| Name | Behaviour |
|---|---|
| **Single** | All visible traces placed in a single tab group |
| **Stack N** | N evenly spaced trace windows stacked vertically |
| **Grid N×M** | Grid of trace windows, N rows by M columns |
| **Custom** | User-defined arrangement |
| **Tile Visible** | Auto-layout over all open traces, allocating space as evenly as possible; **traces currently hidden as tabs are promoted to their own space** |
| **Previous Layout** | Revert to the prior layout |

*Historical note for reference:* the Agilent era used fixed presets — "Single", "Stack 2",
"Grid 2x2", "Quad 4", "Grid 6" — with six configurable measurement grids. The modern
parameterised form supersedes it. **[V]**

**AC:** All six entries appear in the layout menu under exactly these names. Stack N and
Grid N×M are parameterised, not a fixed list of presets, so arbitrary N and N×M are
accepted within the space available. **Tile Visible** promotes traces that were hidden as
tabs into their own space — checked by starting from a tab group of several traces and
asserting each becomes separately visible, since this is the clause that distinguishes it
from Single. **Previous Layout** restores the arrangement in force before the last layout
change, including a Custom one.

**`REQ-UI-006` (P1) — Status bar contents.**
A status bar at the bottom of the main window shall show, with measurement status messages
specifically at the **bottom left**: **[V]**

- **Measurement status** — e.g. *Average Complete*, *Measurement running*, *Waiting for
  Trigger*, *Real-Time Measurement*, *Filling Time Record*
- **Calibration status** for the selected hardware
- **External reference lock state**
- **Spectrum Rate indicator** — the speed at which measurements are being made
- **Beta features in use** count *(adopt as a "preview features" indicator)*

To these OpenVSA adds, per `REQ-NFR-012` and `REQ-NFR-027`: **dropped-frame count** and
**measured front-end transfer rate / duty cycle**.

**AC:** Every listed field is present, and measurement status messages render at the bottom
left specifically — asserted on position, since that placement is quoted from the reference
product. Each status string appears when its condition holds: driving the measurement into
*Waiting for Trigger* and *Average Complete* shows those strings. Calibration status,
reference lock state and spectrum rate track their underlying conditions rather than showing
a fixed value. Dropped-frame count is monotonic per `REQ-NFR-012`, and the transfer rate is
the measured figure of `REQ-NFR-027`, not a nominal one.

**`REQ-UI-007` (P1) — Prominent fault and lock indicators.**
Conditions that invalidate a measurement shall be indicated in the trace's upper-right corner
using the strings of `REQ-UI-041`, not buried in a log: ADC overload (`OVx`), unlocked
reference, uncalibrated state (`CAL?`), demodulation lock failure (`CARRIER LOCK?`), sync or
pulse not found, and dropped frames.
*Rationale:* every one of these means the number on screen is wrong. A user reading EVM must
not have to go looking to discover that the input was overloaded.
**AC:** Each listed condition is provoked in turn — ADC overload, unlocked reference,
uncalibrated state, demodulation lock failure, sync not found, pulse not found, dropped
frames — and each raises its `REQ-UI-041` string in the trace's upper-right corner within one
display update, using the `Indicator` colour. The indicator clears when the condition
clears. A test asserts the string is on the trace rather than only in the event log, since
"buried in a log" is precisely the failure this forbids.

### 13.2 The plot surface: three layered zones

**`REQ-UI-010` (P0) — Three independently coloured zones.**
This is the single most load-bearing layout fact, taken from the reference product's own
display-colour enumeration: **[V]**

```
┌─────────────────────────────────────────────────┐  ← AnnotationBackground
│  annotation text (Annotation colour)            │     (outer band)
│   ┌───────────────────────────────────────┐     │
│   │                                       │     │  ← TraceBackground
│   │   graticule (Grid colour)             │     │     (inner rectangle,
│   │   trace geometry                      │     │      behind the graticule)
│   │                                       │     │
│   └───────────────────────────────────────┘     │
│  annotation text                                │
└─────────────────────────────────────────────────┘
```

Verbatim definitions: `TraceBackground` — "Color for the background of the trace data (behind
the graticule)"; `Grid` — "Color for the graticule lines"; `Annotation` — "Color for the trace
annotation (text outside of the graticule)"; `AnnotationBackground` — "Color for background of
area outside of the trace graticule".

**AC:** `TraceBackground`, `Grid`, `Annotation` and `AnnotationBackground` are four
independently settable colours. Set each to a distinct value and render: sampling the
rendered frame returns `TraceBackground` inside the graticule rectangle away from any grid
line or trace, `Grid` on a graticule line, `AnnotationBackground` in the surrounding band away
from text, and `Annotation` on annotation glyphs. Changing any one of the four leaves the
other three regions' sampled values unchanged — the zones are genuinely independent, not two
colours with a shared background.

**`REQ-UI-011` (P1) — The annotation band reflows.**
**Show Annotation** off shall remove all trace annotation, with **the graticule expanding to
fill the reclaimed space**. **Show Grid Lines** off shall remove the graticule lines
independently. **[V]**
**AC:** Toggling Show Annotation changes the plot rectangle's size, not merely text
visibility.

**`REQ-UI-012` (P1) — Graticule geometry.**
The graticule shall be **10 horizontal × 10 vertical divisions** by default, configurable.
**[U — recommended value, not documented.]**

> **Why 10×10 despite being undocumented.** No Keysight source for either product states a
> division count. The strongest available evidence is arithmetic: the 89400 series specifies
> "**Display points/trace 401**" — 400 intervals, exactly what a 10-division-wide graticule at
> 40 px/division implies, and the 89600's geometry is inherited from it. That is an inference
> from a real published number, not a quotation. Make the count a setting so the choice is
> cheap to revise.

**AC:** The graticule renders 10 × 10 divisions by default, counted from the rendered frame,
and the count is a setting that takes effect without restart. Division lines are evenly
spaced to within one pixel across the grid, and the outermost lines coincide with the grid
boundary rather than falling inside or outside it.

**`REQ-UI-013` (P1) — Reference position defaults, which set the annotation layout.**
X reference position 0–100 % (0 % = left edge, 100 % = right edge). Y reference position
0–100 % in 1 % increments, **defaulting to 100 % for Log Mag, Lin Mag and Log Mag (lin)** —
i.e. the reference line at the **top** of the grid — **and 50 % for all other formats**, i.e.
centred, which is what time-domain and IQ displays need. **[V]**
*This default is what puts the reference-level annotation at top-left for spectra and centres
the origin for IQ displays; it is a layout decision as much as a scaling one.*
**AC:** Y reference position defaults to 100 % for Log Mag, Lin Mag and Log Mag (lin), and to
50 % for every other format — enumerated over the full `REQ-DSP-041` format list so a format
added later without a default fails the test. Both axes accept 0–100 %, Y in 1 % increments,
and the reference line lands at the requested fraction of the grid height to within one
pixel. At the 100 % default the reference-level annotation sits top-left per `REQ-UI-040`;
at 50 % an IQ display's origin is centred.

**`REQ-UI-014` (P1) — Colour configuration and persistence.**
All display colours shall be user-settable through a standard colour picker and shall persist
in a display-preferences file (the reference product uses `.dspx`). **[V]**
**AC:** Every element of `REQ-UI-022` is reachable from the colour picker — the list is
enumerated from the theme resource dictionary so an element added without a picker entry
fails. Changed colours survive a restart via the display-preferences file, and that file is
separate from saved state per `REQ-STA-002`: recalling a state does not alter display
colours, and loading display preferences does not alter measurement settings.

**`REQ-UI-015` (P1) — Default background.**
The default trace background shall be **black** (or very dark). **[U — inferred, not stated.]**

> The only textual trace of the default in the entire documentation is the Print dialog's
> **"Force white background"** option, offered because "large areas of black do not print well
> on inkjet-style printers", with "very light colors will print black so they can be seen."
> A feature justified by "large areas of black" only makes sense over a black default. That is
> strong circumstantial evidence, not a statement. **OpenVSA shall implement the same
> force-white-background print option regardless** — it is needed either way.

**AC:** `TraceBackground` defaults to black or a very dark value on a fresh profile. The
print path offers **Force white background**, and with it enabled the printed or exported
output has a white trace background with every trace and annotation colour still legible
against it — specifically, very light trace colours are darkened rather than left invisible,
which is the behaviour the reference product's own note describes.

### 13.3 Colour model

**`REQ-UI-020` (P0) — Traces are lettered, and colours come from a 20-entry indexed table.**
Traces shall be identified by **letter** (Trace A, B, C, D, …), **not** by number — trace
*numbers* would collide with marker numbering, which is exactly why the reference product
letters them. Colours are held in an indexed table of **20**. **[V]**
**AC:** Traces are identified by letter throughout — tab titles, the active-trace toolbar
button of `REQ-UI-063`, annotation, and the delta-marker label of `REQ-UI-031` — asserted by
a test that fails if any trace identifier renders as a bare number. The colour table holds
exactly 20 entries; the twenty-first trace reuses index 0 rather than extending the table or
failing. Trace lettering continues past Z in a defined way rather than colliding or
truncating.

**`REQ-UI-021` (P0) — A trace's line and its annotation text share one colour.**
`Trace` is defined as "Color for specified trace **and its annotation**". **[V]**
*This is a genuine visual signature of the product — the numbers describing a trace are
tinted to match the trace itself, which is how a user reads a four-trace overlay at a glance.
Corroborated from the other direction by an 89400 firmware defect report: "Trace A shows wrong
annotation color after preset."*
**AC:** A trace's line and its annotation text sample to the same colour in the rendered
frame, for every trace in a multi-trace overlay. They stay equal after the operations that
historically broke it: changing the trace colour, preset, state recall, and theme change —
the 89400 defect quoted above is exactly the post-preset case, so preset is tested
explicitly. One colour setting drives both; there is no separate annotation-colour entry per
trace to drift out of step.

**`REQ-UI-022` (P1) — Themeable element set.**
The following shall each be independently colourable, forming the WPF theme resource
dictionary. **[V — this is the reference product's actual list.]**

*Global (all traces):* ACP · ACP annotation · Annotation · Annotation Background · Grid ·
Indicator · Limit · Fail Limit · Margin · Fail Margin · Marker Window Background · Mod Type N ·
Selected Marker · Not Selected Marker · OBW · OBW annotation · Slot Annotation · Slot Data ·
Slot MAC · Slot Midamble · Slot Pilot (+ Downlink/Uplink) · Slot Preamble · Slot Selected ·
Trace Background.

*Per trace:* Trace · Symbol · Average · Pilot · Spectrogram Marker · Trace Select ·
Emitter 1–32 · Group 1–48.

**AC:** Every named element resolves to a distinct key in the WPF theme resource dictionary,
enumerated by a test against this list so a missing or misspelled key fails rather than
silently falling back to a default brush. Global entries apply across all traces and
per-trace entries are indexed per trace, including the Emitter 1–32 and Group 1–48 ranges at
full extent. Every element is reachable from the colour picker per `REQ-UI-014`, and no
element is hard-coded: a test fails on a literal colour in the plot-surface rendering path.

**`REQ-UI-023` (P1) — Limit/margin colouring, including the non-obvious part.**
Four separate colour entries: **Limit**, **Margin**, **Fail Limit**, **Fail Margin**. **[V]**

> **The "fail" colours recolour *the trace itself* at the failing points — not the limit
> line.** This is counter-intuitive and easy to implement backwards, and it is the behaviour
> that makes a failure legible at a glance. The user can set pass and fail indication for the
> limit, the margin, or both. **[V]**

*Recommended defaults:* limit red, margin yellow. **[U — widely assumed but stated nowhere;
adopt as a default, not as a fidelity claim.]**

**AC:** All four entries exist independently. On a trace failing part of a limit, the
rendered frame shows **the trace's own points recoloured** to `Fail Limit` over the failing
span while the limit line keeps its `Limit` colour — the inverse, recolouring the limit line,
fails the test, since implementing this backwards is the stated risk. Margin failure behaves
the same way with `Fail Margin`. Pass and fail indication is independently enablable for the
limit, the margin, or both. Defaults are limit red, margin yellow.

**`REQ-UI-024` (P1) — Spectrogram colour maps.**
The one hard colour statement in the entire reference documentation, and therefore to be
implemented exactly: **[V]**

| Map | Definition |
|---|---|
| **Color Normal** *(default)* | Spectrum of **64 colours**; **maximum → red, minimum → blue** |
| **Color Reverse** | As above, reversed |
| **Grey Normal** | **64 shades of grey**; maximum → lightest, minimum → darkest |
| **Grey Reverse** | As above, reversed |
| **User Defined** | User map; **colour index 0 is at the bottom**; reducing the count discards colours from the **top**; a "Sample Map" preview shows the full map with horizontal marks indicating the active selection |

*Note:* the brochure remarks that "grey-scale views provide even greater resolution" — worth
surfacing as a tooltip, since it is a real perceptual point about luminance ramps.

**AC:** All five maps are selectable and Color Normal is the default. Color Normal and Grey
Normal each contain exactly 64 entries; in Color Normal the maximum-valued cell renders red
and the minimum blue, and in Grey Normal lightest and darkest respectively — sampled from a
rendered spectrogram of a ramp input, and the Reverse variants produce the exact reversal of
their counterparts. For User Defined, index 0 sits at the bottom of the map, and reducing the
entry count discards from the **top** — asserted directly, because discarding from the bottom
is the natural implementation and the wrong one. The Sample Map preview shows the full map
with marks indicating the active selection.

### 13.4 Markers — glyphs and readouts

**`REQ-UI-030` (P0) — Marker glyph shapes and placement.** **[V — quoted verbatim]**

| Type | Glyph | Placement |
|---|---|---|
| **Normal** | Diamond | **Directly *above*** the data point |
| **Delta** | Diamond | Directly above the data point |
| **Fixed** | **"X"** | **Centre of the X *over*** the data point |

"**A solid diamond represents the currently selected marker**" — so selected markers are
filled and unselected hollow. Colour is by selection state (`SelectedMarker` /
`NotSelectedMarker`), **not** by marker index. **[V]**
*Two subtleties a developer will otherwise miss: the diamond is offset above the point while
the X is centred on it, and selection is conveyed by fill rather than colour index.*
**AC:** Measured from the rendered frame: a Normal or Delta marker's diamond has its bounds
entirely above the data point, while a Fixed marker's X is centred on it — the centroid of
the X coincides with the data point to within one pixel and the diamond's does not. The
selected marker's diamond is filled and unselected ones are hollow. Marker colour is drawn
from `SelectedMarker`/`NotSelectedMarker` by selection state, so two unselected markers of
different numbers render the same colour; colouring by marker index fails the test.

**`REQ-UI-031` (P0) — Delta-marker label format.**
`XΔTR`, where X is the delta marker number, T the reference trace letter **shown only when the
reference is on a different trace**, and R the reference marker number. Thus **`3Δ1`** for a
same-trace reference and **`2ΔB1`** for a cross-trace reference. In the Markers Window the
form is `Mkr NΔTR`. **[V]**
**AC:** A delta marker referencing another marker on the same trace renders `3Δ1` — with no
trace letter — and one referencing a marker on trace B renders `2ΔB1`. Both cases are
asserted as exact strings, since emitting the trace letter unconditionally is the obvious
implementation and is wrong for the same-trace case. The Markers Window uses the `Mkr NΔTR`
form. Trace identifiers in the label are letters per `REQ-UI-020`.

**`REQ-UI-032` (P1) — Markers Window.**
A dedicated tool window with its own background colour and its own font slot. Readout labels:
`Mkr N` (X, Y and Z values) · `Mkr NΔTR` · `Freq N` (frequency counter) · `OBW` · `BW`
(x-dB bandwidth) · `ACP Ref` · `Power` (band power) · `Density` (band-power density) ·
`Limit`, plus fields Carrier, Channel Type, Layer, Sym. Invalid values render **`NAN`**,
overflow **`INF`**. For two-dimensional IQ formats the readout is "Mag & Phase" or "Real &
Imag", **defaulting to Mag & Phase**. **[V]**
**AC:** Every listed readout label and field appears with exactly the spelling given. An
invalid value renders the literal `NAN` and an overflow the literal `INF`, not a
framework-default "NaN"/"∞" or a blank — asserted as exact strings. Two-dimensional IQ
formats offer both readout pairs and default to Mag & Phase. The window has its own
background colour (`MarkerWindowBackground`) and its own font slot per `REQ-UI-080`,
independent of the Annotation slot.

**`REQ-UI-033` (P1) — Fixed-width font in the Markers Window.**
The help is explicit: "**always select a fixed-width font for the Markers Window**". **[V]**
OpenVSA shall default to a fixed-width face there rather than leaving it to the user to
discover.
**AC:** The Markers Window's default font is fixed-width, asserted by querying the resolved
typeface's pitch rather than its name, so a proportional face substituted for a missing font
fails. Marker readouts of differing digit content align in a column, which is the property
the fixed-width face exists to provide.

### 13.5 Annotation placement and the hot-spot model

**`REQ-UI-040` (P0) — Annotation positions.** **[V — each individually sourced]**

| Position | Content |
|---|---|
| **Above the trace grid, right** | Active-marker readout |
| **Upper-right corner, *inside* the grid** | Trace indicator messages (own `Indicator` colour) |
| **Band outside the graticule** | All other trace annotation |
| **Bottom left of the main window** | Measurement status messages |

Within the outer band, the recommended placement — **[U for position, V for the fact that each
string exists on screen]** — follows from the Y-reference default of 100 % for log-magnitude:
**Y-axis top scale** top-left, **Y-axis per-division** on the left edge below it, **Y-axis
bottom scale** bottom-left, **trace format / resolution bandwidth / trigger channel** in the
upper band, **centre frequency** and **main time length** centred beneath the X axis.

**AC:** Measured from the rendered frame: the active-marker readout sits above the grid and
to the right; trace indicator messages sit inside the grid's upper-right corner in the
`Indicator` colour; all other trace annotation lies outside the graticule in the annotation
band; measurement status is at the bottom left of the main window per `REQ-UI-006`. The
indicator strings are the only annotation drawn inside the graticule — a test fails if any
other annotation's bounds intersect the grid rectangle. Each recommended string is present at
its stated position.

**`REQ-UI-041` (P0) — Trace indicator strings.**
The following shall be displayed in the grid's upper-right corner, in this priority order:
**[V]**

`NO DATA` · `DATA?` · `OVx` (overload, x = channel) · `CAL?` · `PULSE NOT FOUND` ·
`SYNC NOT FOUND` · `CARRIER LOCK?` · `EQ` (equaliser active) · `RNG` · `ALL POINTS` ·
`INACTIVE CHAN` · `MEAS OFFSET?` · `PULSE TOO SHORT` · `IQ COMP` (IQ mismatch compensated out).

*These map one-to-one onto conditions this specification already requires be detected —
`REQ-DEM-036` carrier lock, `REQ-DEM-040`/`041` sync and pulse search, `REQ-DEM-050`
equalisation, `REQ-UI-007` overload. Adopt the strings verbatim; they are terse, unambiguous,
and familiar to anyone who has used the reference product.*
**AC:** Each string renders exactly as listed, including the `OVx` channel suffix and the
question marks on `DATA?`, `CAL?`, `CARRIER LOCK?` and `MEAS OFFSET?` — asserted as exact
literals, since these are quoted and a tidied-up wording would break familiarity for no gain.
With several conditions true at once the display order follows the stated priority, tested by
provoking overload and carrier-lock failure together. The strings render in the grid's
upper-right corner per `REQ-UI-040`.

**`REQ-UI-042` (P0) — Clickable annotation ("hot spots"). The signature interaction.**
Measurement parameters displayed as trace annotation shall be **directly editable in place**.
Required behaviour, verbatim from the reference product: **[V]**

| Interaction | Result |
|---|---|
| **Hover** | Cursor changes from arrow to a **hand**, and the value is **underlined** |
| **Single click** | Pointer changes to indicate a **numeric entry pad**; mouse wheel, arrow keys, or typing adjust the value live |
| **Double click** | Opens a **data-entry dialog** for value and units |
| **Right click** | Opens an edit popup menu (copy/paste) |

Confirmed editable hot spots: **Y-axis top scale**, **Y-axis per-division scale**, **Y-axis
bottom scale**, **trace format**, **resolution bandwidth**, **trigger channel**, **main time
length**, **centre frequency**. Additionally: trace context menu (right-click in the grid),
Y-axis and X-axis context menus, a **trace data dropdown** (click the dropdown button or
right-click), the markers readout, and the tab select/close buttons. **[V]**

> **This is the highest-value single feature to copy, and the easiest to omit.** It collapses
> the usual dialog round-trip — *find the menu, find the tab, find the field, apply, close* —
> into a click on the number you are already reading. A reimplementation that puts every
> parameter behind a dialog will be functionally complete and will feel nothing like the
> original. Build the hot-spot framework early, in Phase 0, because retrofitting in-place
> editing onto a finished plot surface is far more expensive than designing for it.

**AC:** Every parameter listed above is editable in place with all four interactions;
hovering visibly underlines and switches the cursor; wheel and arrow keys adjust the hovered
value without opening a dialog.

### 13.6 Measurement display specifics

**`REQ-UI-050` (P1) — Constellation.**
Points drawn **only at symbol decision instants, with no connecting lines** — explicitly "an
IQ diagram but information is shown only at specified time intervals", and "similar to the IQ
trace format but without the lines that connect the points". The IQ/Vector format is the same
data *with* the inter-symbol trajectory. Ideal states shall be overlaid as **crosshairs or
circles** (user-selectable) — **not** as filled dots, which would be confusable with measured
symbols. Symbol points carry their own colour, separate from the trace line, and may be
coloured **by modulation type** for signals carrying mixed modulations. **[V]**
**AC:** A constellation trace of a known modulation draws exactly one point per symbol, at
the decision instants, with no connecting geometry — a test asserts the rendered primitive
count equals the symbol count and that no line segments join them. The same data in IQ/Vector
format draws the connecting trajectory, which is the difference between the two. Ideal states
overlay as crosshairs or circles, user-selectable, and never as filled dots. Symbol points
use the `Symbol` colour, distinct from the trace line's, and a mixed-modulation signal
colours symbols by modulation type via the `Mod Type N` entries.

**`REQ-UI-051` (P1) — Eye diagram.**
X axis in **symbols**, Y axis in volts (I or Q). The eye shall be **centred in the display**,
a one-symbol eye spanning −½ to +½ symbol. Rendering is accumulative — "the VSA draws the
first trace, then overlays the second trace, the third trace, and so on". **Vertical reference
lines shall be drawn at the symbol positions**, where maximum eye opening should occur,
corresponding to the symbol clock. An m-level modulation shows **m−1 eyes stacked
vertically**. Eye length adjustable **0.1 to 10 symbols**. **[V]**
*Axis annotation from a real screen, worth matching:* `I - Eye`, `-1 Sym`, `1 Sym`.
**AC:** The X axis is in symbols and the eye is centred: a one-symbol eye spans −½ to +½
symbol about the display centre, measured from the rendered frame. Vertical reference lines
fall at the symbol positions, coinciding with the points of maximum eye opening for a clean
signal — checked against the generated signal's known symbol clock, so a half-symbol offset
fails. An m-level modulation shows m−1 eyes stacked vertically, counted for at least two
values of m. Eye length is settable over 0.1 to 10 symbols with values outside rejected.
Rendering is accumulative: successive acquisitions overlay rather than replace.

**`REQ-UI-052` (P1) — Symbol table and error summary are ONE trace, split top and bottom.**
**[V]** — this is a structural point, not a styling one, and getting it wrong means building
two traces where the product has one.

- **Top portion:** the error-summary metrics of §11.7.
- **Bottom portion:** the detected symbol/bit stream.
- **Left gutter:** in binary format, "the number to the left of each row indicates the **bit
  offset** of the first bit in the row"; in hex format it is the **symbol offset**. Hex
  requires ≥4 bits/symbol.
- **Grouping:** characters "organized in groupings of eight characters followed by a space".
- **Font:** fixed-width. **[U for the symbol table specifically — inferred, but strongly: the
  fixed-column grouping and left-gutter offsets only align in a monospaced face. Note the
  reference product exposes only two font slots (Annotation and Marker), so the symbol table
  almost certainly inherits the Annotation font — meaning that font must itself be monospaced,
  or OpenVSA should add a third slot.]**

**AC:** The symbol table and error summary are one trace with two portions, not two traces —
asserted structurally: the trace list contains a single entry, and selecting it selects both
portions. In binary format the left-gutter number is the bit offset of the row's first bit;
in hex it is the symbol offset; both are checked against a known symbol stream, and hex is
unavailable below 4 bits/symbol. Characters group in eights separated by a space. The
portions render in the fixed-width Tabular slot of `REQ-UI-080`.

**`REQ-UI-053` (P1) — Error summary text layout.**
The following is the actual on-screen text from a real analyser of this family and shall be
the layout model — label, `=` at a fixed column, RMS value, peak value, "at symbol N",
engineering prefixes on units: **[V]**

```
EVM        =  248.7475 m%rms   732.2379 m% pk at symbol 73
Mag Error  =  166.8398 m%rms  -729.4476 m% pk at symbol 73
Phase Error=  251.9865 mdeg    1.043872 deg pk at symbol 168
Freq Error =  -384.55 Hz
IQ Offset  =  -67.543 dB      SNR = 40.58 dB
```

Row labels shall use the reference product's terse abbreviation style: **Amp Droop, Carr
Ofst, EVM, EVM Pk, Freq Err, Mag Err, Offset EVM, Phase Err, Pilot Lvl, Time Offset, IQ
Offset, IQ Gain Imbalance, IQ Quad. Error, IQ Timing Skew, SymClk Err, RSSI**. **[V]**
*Note the house style: short, truncated, no-space-where-possible — `Carr Ofst`, not "Carrier
Offset". Scalars occupy one column; two related metrics occasionally share a line.*
**AC:** Rendered against a signal of known impairments, the error summary reproduces the
layout above: `=` at a fixed column across all rows, RMS then peak then "at symbol N", and
engineering prefixes on units (`m%rms`, `mdeg`) rather than exponent notation. Row labels are
exactly the listed abbreviations, asserted as literals — `Carr Ofst`, not "Carrier Offset".
Scalar-only metrics render one value and omit the peak columns rather than padding them with
zeros. The block renders in the fixed-width Tabular slot, which is what holds the `=` column
aligned.

**`REQ-UI-054` (P1) — Spectrogram / 3-D map displays.**
Spectrogram, Digital Persistence and Cumulative History shall be presented as a group on a
dedicated toolbar (consistent with `REQ-TRC-001a` treating them as accumulators). Each
spectrogram carries **two markers on different axes**: a **vertical** spectrogram marker and a
**horizontal** trace-select marker. Controls: **Enhance**, **Threshold**, **Map Colour
Scheme**. **[V]**
**AC:** The three accumulating modes appear together on one dedicated toolbar per
`REQ-UI-063`, and are reached through `TraceAccumulator` rather than the format list, per
`REQ-TRC-001a`. A spectrogram carries both markers on perpendicular axes — the spectrogram
marker vertical, the trace-select marker horizontal — and each moves only along its own axis.
Enhance, Threshold and Map Colour Scheme are present and each visibly changes the rendering:
raising Threshold removes cells below it, and Map Colour Scheme switches between the
`REQ-UI-024` maps.

### 13.7 Menus, toolbars and shortcuts

**`REQ-UI-060` (P1) — Menu bar.**
The menu bar shall be, in order: **[V for the names; U for the exact order]**

**File | Edit | Hardware | Acquisition | Analysis | Trace | Marker | Utilities | Window | Help**

> **This corrects a natural but outdated assumption.** The obvious guess — *File, Input,
> MeasSetup, Display, Trace, Marker, Control* — is the **Agilent-era (89601A)** menu bar and is
> no longer current. Three menus were renamed and two demoted: **Input → Acquisition**
> ("selects and configures one or more input channels and selects the data source"),
> **MeasSetup → Analysis**, **Markers → Marker** (singular); **Control** became a submenu
> (*Acquisition > Control > Sweep*); **Display** disappeared, its layout functions moving under
> Window and Trace; **Source** moved under Hardware. OpenVSA shall adopt the modern names.

**AC:** The menu bar contains exactly these ten menus, with these names, in this order —
asserted as an exact ordered list. The superseded Agilent-era names are absent: a test fails
on a top-level menu named Input, MeasSetup, Display or Control, since those are the names a
developer working from older documentation would naturally produce.

**`REQ-UI-061` (P1) — Menu contents.** **[V]**

- **File** — Recall (Setup / Recording / Trace / Layout / Demo), Preset ▸ (Measurement,
  Measurement to Standard, Measurement to Defaults, Setup, Traces, Application and Traces,
  Display Preferences, Toolbars, Factory Defaults), Save, Export, Print, Exit.
  *Note: **"Preset never changes the hardware setup"** — preserve that separation.*
- **Edit** — Copy ("write the contents of a trace, marker readout, or trace hotspot to the
  clipboard"), Copy Markers, Paste.
- **Hardware** — Instruments…, Configurations…, Rediscover, Analyzer, Frequency Reference…,
  Calibration…, Disconnect, Source, Source Control…, Switch.
- **Acquisition** — Data, Channels, Amplitude…, External Mixer…, Extended Settings…, Trigger…,
  Segmented Capture…, Digital…, Gate Trigger…, Playback Trigger…, User Correction…, Player
  Window, Recording/Playback…, Control ▸.
- **Analysis** *(in order)* — Type, Properties…, Frequency…, ResBW…, Time…, Detectors…,
  Conversion…, Average…, Heatmaps…, Measurements…, New Measurement, Duplicate Measurement.
- **Trace** — Trace List, New Trace, *(embedded trace toolbar)*, Data; **Properties:** Format,
  Coupling, Y Scale, X Scale, Average, Digital Demod; **Calculation:** Results Window, OBW…,
  ACP…, Limit Tests…; then Spectrogram / Colour Map, Math Functions, Stimulus-Response / X-Y…,
  Auto Scale, Overlay, Copy Trace.
- **Marker** — Markers Window, New Marker, *(embedded markers toolbar)*, Position…,
  Calculation…, Peak Search, Copy Marker To, Couple Markers, Copy to Clipboard, All Markers
  Off.
- **Utilities** — Macros…, Event-Based Actions…, Trend/Statistics…, General Preferences…, SCPI
  Preferences…, Display Preferences…, Toolbars…, Manage Registers…, Extension Manager….
  *(The reference product has a **Licenses…** item here. OpenVSA has none — there is nothing
  to license, per `REQ-LIC-010` — and `REQ-UI-061`'s exact-list criterion means adding one
  fails the build.)*
- **Window** — Output, SCPI Log, Event Log, Contexts, Block Diagram, Macros, Trace Layout, New
  Trace Window, Resize Traces, Collect Traces.
- **Help** — Help (F1), Dynamic Help, Getting Started, Demos, Examples, API Reference, SCPI
  Reference, Support ▸, Privacy, About.

**AC:** Every listed item is present under its listed menu, with the Analysis menu in the
stated order, verified by walking the menu tree against this list; an item present in the
tree but not in the list also fails, so the menus stay as specified rather than accreting.
Each item is either enabled and functional or disabled with a reason — none is present and
inert. **Preset never changes the hardware setup**: a test alters hardware configuration,
invokes each Preset variant, and asserts the hardware setup is unchanged while the targeted
settings are reset, since that separation is called out explicitly and is easy to lose.

**`REQ-UI-062` (P2) — Embedded toolbars inside menus.**
The **Trace** and **Marker** menus shall each host an embedded toolbar at the top — the trace
toolbar selects the active trace and adds/removes/hides traces; the markers toolbar does the
same for markers. **[V]**
*A distinctive touch worth keeping: it puts the most frequent actions one click inside the
menu that owns them, rather than requiring a separate toolbar hunt.*
**AC:** The Trace and Marker menus each contain a toolbar as their topmost element, not a
list of commands standing in for one. The trace toolbar selects the active trace and
adds, removes and hides traces from within the open menu; the markers toolbar does the
equivalent for markers. Acting on the embedded toolbar takes effect without first dismissing
the menu.

**`REQ-UI-063` (P1) — Toolbars.**
Five preconfigured toolbars, plus a macro-button bar: **[V]**

| Toolbar | Contents |
|---|---|
| **Control** | **Restart** ("starts a measurement or restarts one that was paused… all current measurement data including averaging is discarded") · **Pause/Single** ("pauses a running measurement; a second click single-steps when Sweep is Single, or continues when Continuous") · **Single/Continuous Sweep** toggle · **Auto-range** — a **split button**: main click auto-ranges all input channels, dropdown auto-ranges a chosen channel |
| **Marker Tools** | Radio group of mouse modes: **Pointer** → **Area Select** ("select a rectangular area of any trace for closer examination"; can scale X and/or Y, **or set centre frequency and span**) → **Marker** (click to place) → **Band Power** (drag left/right band limits) → **Time Gate** (isolate a portion of a time record) |
| **Record** | **Record** · **Toggle Data Source** (hardware ↔ recording) · **Disconnect** (releases hardware from VSA control) |
| **Trace / Block Diagram** | **Active Trace** (shows the active trace's **letter**) · **Trace Layout** dropdown · **Block Diagram** |
| **Spectrogram / Colour Map** | **Spectrogram** · **Digital Persistence** · **Cumulative History** · **Enhance** · **Threshold** · **Map Colour Scheme** |
| **Macro Buttons** | Managed by the macros utility; not user-editable through the toolbar customiser |

**AC:** All six toolbars exist with the listed contents. Behaviours quoted from the reference
product are tested, not just present: **Restart** discards current measurement data
*including averaging*, asserted by a non-zero average count returning to zero; **Pause** then
a second click single-steps under Single sweep and continues under Continuous — both branches
tested, since collapsing them is the likely shortcut. **Auto-range** is a split button whose
main click ranges all channels and whose dropdown ranges a chosen one. Marker Tools is a
radio group: selecting one mouse mode deselects the others. **Area Select** can scale X, Y,
or set centre frequency and span. The macro bar is not editable through the toolbar
customiser.

**`REQ-UI-064` (P2) — Toolbar customisation.** Users shall be able to define custom toolbars
(list of toolbars, control picker, contents editor), with reset via File > Preset > Toolbars.
**[V]**
**AC:** A custom toolbar can be created, populated from the control picker, reordered and
deleted, and survives a restart. File > Preset > Toolbars restores the five preconfigured
toolbars of `REQ-UI-063` to their default contents and removes custom ones. The macro-button
bar is absent from the customiser per `REQ-UI-063`.

**`REQ-UI-065` (P1) — Keyboard shortcuts.**
Adopt the reference product's bindings — they are muscle memory for existing users and cost
nothing to match: **[V]**

| Key | Action |
|---|---|
| **Space** | Pause / resume |
| **Ctrl+Shift+Space** | Restart |
| **Ctrl+N** | New trace |
| **Ctrl+W** | Auto-scale |
| **Ctrl+K** | Marker position |
| **Ctrl+H** | Player window |
| **Ctrl+O** | Output window |
| **Ctrl+B** | Save bitmap |
| **F1** | Context help |
| **Ctrl+F1** | Dynamic help |
| **Ctrl + `+` / `-`** | Window content scaling (`REQ-NFR-007a`) |

**AC:** Every listed binding invokes its listed action, driven through the input system rather
than by calling the command directly, so a binding that is declared but unreachable fails.
None is shadowed by a conflicting binding, asserted by a uniqueness check over the whole
binding table. Space pauses and resumes without stealing input from a focused text field —
the case that makes a bare-Space binding go wrong.

### 13.8 Dialog conventions

**`REQ-UI-070` (P1) — Tabbed, modeless, live dialogs.**
Setting dialogs shall be **tabbed, modeless, and live** — changing a parameter updates the
measurement immediately, with no OK/Apply round-trip. **[V]**
*This is why the hot-spot model and the dialogs coexist comfortably: both edit the same live
state, and neither blocks the display.*
**AC:** No setting dialog is modal: with one open, the measurement continues to update and
the main window remains interactive, including the hot-spot controls. Changing a parameter
applies immediately — a test asserts the measurement reflects the new value with no OK or
Apply invoked, and that no such button exists on the dialog. Editing the same parameter from
a hot spot and from its dialog drives one piece of state: each surface reflects a change made
from the other without needing to be reopened.

**`REQ-UI-071` (P1) — Dialog framework options.**
The dialog framework shall expose, globally: **[V]**

| Option | Values / behaviour |
|---|---|
| **Default Mode** | "Tabs on top" · "Tabs on left" · expanders vertical · expanders horizontal |
| **Fixed Size** | Dialogs sized to the **largest tab they contain**, so switching tabs does not resize the window |
| **Keep on Top** | Whether dialogs may go behind the main window |
| **Persist Mode** | Dialogs reopen in the mode they were closed with, **across restarts** |
| **Tabs Collapsed by Default** | Applies to "Tabs on left" only |

In WPF terms: a `TabControl` whose `TabStripPlacement` is user-switchable Top/Left, with an
alternative `Expander`-stack rendering of identical content; the dialog measures to the union
of all tabs; modeless with optional topmost.

**AC:** All four Default Modes render the same content and every control reachable in one is
reachable in the others. With **Fixed Size** on, switching between tabs leaves the dialog's
outer bounds unchanged and the size equals the union of all tabs, so the largest tab is not
clipped. **Keep on Top** governs whether the dialog can fall behind the main window, tested
both ways. **Persist Mode** restores the mode a dialog was closed in across an application
restart, not merely within a session. **Tabs Collapsed by Default** applies under "Tabs on
left" and is inert under the others.

**`REQ-UI-072` (P1) — Analysis (MeasSetup) tab set.**
**Frequency | ResBW | Time | Detectors | Conversion | Average | Heatmaps**. **[V]**
**AC:** The Analysis dialog contains exactly these seven tabs, with these names, in this
order, and no others. Each tab hosts the settings its name implies and each is populated —
none is a placeholder. The dialog obeys the `REQ-UI-070` and `REQ-UI-071` framework rules.

**`REQ-UI-073` (P1) — Display Preferences tab set.**
**Trace | Colour | User Map Colour | Font | Window**. **[V]** *(There is no separate
General/Theme/Appearance tab — theming lives under Window and Colour.)*
**AC:** The Display Preferences dialog contains exactly these five tabs and no others — a
test fails on a tab named General, Theme or Appearance, since adding one is the natural
instinct and would split theming away from Colour and Window where this specification places
it. Colour exposes the `REQ-UI-022` element set, User Map Colour the `REQ-UI-024` user map,
and Font the three slots of `REQ-UI-080`. Changes made here persist per `REQ-UI-014`.

### 13.9 Typography, theming and visual era

**`REQ-UI-080` (P1) — Font slots.**
The reference product exposes exactly **two** font slots: **Annotation** ("font for Trace
windows") and **Marker** ("font for Marker window"), applied globally. **[V]**
OpenVSA shall provide these two **plus a third, Tabular**, for the symbol table and error
summary — because those require a fixed-width face while general trace annotation reads better
proportional, and the reference product's two-slot scheme forces an unhappy compromise there
(`REQ-UI-052`).
**[U] Default typeface and size are unpublished.** Recommended: **Segoe UI 9 pt** for chrome
and annotation, **Consolas 9 pt** for Markers, symbol table and error summary.
**AC:** Exactly three font slots exist — Annotation, Marker and Tabular — each independently
settable from the Font tab of `REQ-UI-073` and each applied globally to its own surfaces.
Setting one leaves the other two unchanged. Marker and Tabular resolve to fixed-width
typefaces by default, asserted on the resolved face's pitch; Annotation may be proportional.
The symbol table and error summary of `REQ-UI-052` draw from Tabular, not Annotation, which
is the whole reason the third slot exists. Defaults are Segoe UI 9 pt and Consolas 9 pt, with
a documented fallback where either is unavailable.

**`REQ-UI-081` (P2) — Theming and the honest answer to "what era does it look like".**
The reference product themes its chrome with stock Windows and Office shells, and the
available theme list is the clearest single statement of its visual era: **[V]**

`AeroNormalColor · Classic · HighContrast · LunaHomestead · LunaMetallic · LunaNormalColor ·
Office2007Black · Office2007Blue · Office2007Silver · Office2010Black · Office2010Blue ·
Office2010Silver · RoyaleNormalColor`

Luna = Windows XP, Royale = Server 2003/Media Center, Aero = Vista/7, Office2007/2010 = the
ribbon-era Office skins. **It is a Win32/WinForms docking application skinned with a
third-party UI suite — visually an Office 2007/2010-era program, not a WPF-native or
Fluent-era one.** Themes style **chrome only**; the graticule and traces are governed entirely
by the colour settings of §13.3.

**AC:** This requirement is documentary — it records the reference product's visual era so
`REQ-UI-082` can depart from it deliberately. What is testable is the separation it states:
switching theme changes chrome only, and every colour of `REQ-UI-022` — graticule, traces,
annotation, backgrounds — samples identically before and after a theme change. A theme that
alters a plot-surface colour fails. OpenVSA ships the themes of `REQ-UI-082`, not the listed
Luna/Royale/Office set, which is quoted here as evidence rather than as a requirement.

**`REQ-UI-082` (P2) — OpenVSA's own visual direction.** **[DESIGN CHOICE]**
OpenVSA shall *not* reproduce the Office-2007 chrome. It shall adopt a clean, neutral modern
Windows appearance (light and dark themes, per `REQ-UI-013`… see `REQ-UI-090`) while
reproducing exactly the parts that carry meaning: the three-zone plot layout, the annotation
positions, the marker glyphs, the indicator strings, the error-summary text form, and above all
the hot-spot interaction. **Fidelity is owed to the interaction model and the information
design, not to a fifteen-year-old button style.**
**AC:** The chrome is not Office-2007: no theme reproducing that skin ships, and the shipped
theme set is the Light/Dark pair of `REQ-UI-083`. The parts named as load-bearing are each
covered by their own criteria and all must pass — three-zone layout (`REQ-UI-010`),
annotation positions (`REQ-UI-040`), marker glyphs (`REQ-UI-030`), indicator strings
(`REQ-UI-041`), error-summary text form (`REQ-UI-053`) and the hot-spot interaction. This
requirement is met when those pass under a modern chrome, which is what distinguishes a
deliberate visual departure from an incomplete implementation.

**`REQ-UI-083` (P1) — Two shipped themes, and nothing that forecloses more.**
OpenVSA shall ship exactly two chrome themes, **Light and Dark**. User-authored or custom
themes are **not** in scope at this stage. However, the theming mechanism shall be built so
that adding them later is a matter of supplying another resource dictionary, not of
reworking the rendering code.

*This is a scope decision with an architectural obligation attached, and the obligation is
the part that will be got wrong.* Shipping two themes is easy to do in a way that makes a
third expensive: hard-coded brushes, colours resolved through a `switch` on a two-valued
enum, or a bool `IsDarkMode` threaded through view models all satisfy "light and dark" today
and have to be unpicked later. None of that is more work to avoid now than to do.

Concretely, and testably:

- Every themed value resolves through a **resource dictionary keyed by name**, swappable at
  runtime. Adding a theme means adding a dictionary; no code names a theme to decide a
  colour.
- Theme identity is **not a closed two-valued type**. No `enum Theme { Light, Dark }`
  switched over to pick values, and no boolean "is dark" anywhere in the rendering or
  view-model layers.
- The chrome/plot separation of `REQ-UI-081` holds, so a future theme cannot silently
  change measurement colours.

**AC:** Exactly two themes ship, named Light and Dark, both selectable with no restart. The
non-foreclosure obligation is verified by **actually doing the thing that is being deferred**:
a test supplies a third resource dictionary at runtime and asserts the application renders
with it correctly, with **no product code changed** — the only honest test of "a later custom
theme is not made harder", since every weaker check passes on an implementation that has
hard-coded the two. Architecture tests fail on a literal colour in any chrome or
plot-surface rendering path (extending `REQ-UI-022`'s rule from plot elements to chrome), on
a `switch` over a theme identity to select a value, and on a boolean dark-mode flag in the
rendering or view-model layers. Every key in the shipped dictionaries is present in both, so
a theme cannot be partially defined; the `REQ-UI-081` separation test passes for the third
dictionary too.

**`REQ-UI-090` (P2) — Themes and accessibility of colour.**
Light and dark themes shall be provided, with trace colours distinguishable without relying on
hue alone (differing also in luminance and, optionally, dash pattern), and a high-contrast
theme shall be supported.
**AC:** Light, dark and high-contrast themes are all selectable, and switching between them
requires no restart. In each theme, every adjacent pair in the 20-entry trace colour table of
`REQ-UI-020` differs in relative luminance by a stated minimum as well as in hue, so the
table is checked numerically rather than by eye; simulating the common forms of colour
vision deficiency leaves every pair distinguishable, optionally with the aid of dash pattern.
Trace and annotation contrast against `TraceBackground` meets a stated contrast ratio in all
three themes. The high-contrast theme follows the system high-contrast setting when one is
active.

> **Deferred — possible future enhancement.** This requirement is not scheduled. Its issue
> (#281) is closed and labelled `future-enhancement`; it is the only requirement in this
> specification without an open issue, and the exception is deliberate rather than an
> oversight in the backlog.
>
> *What is owed before it can be picked up.* The criteria above name "a stated minimum"
> luminance delta and "a stated contrast ratio" without stating either. Those numbers are a
> design decision, not a mechanical one: WCAG 1.4.3/1.4.11 (4.5:1 for text, 3:1 for non-text)
> is the obvious starting point, but a graticule line is not body text, and pairwise trace
> distinguishability is a property those ratios do not address at all. Until the thresholds
> are chosen the criteria are not testable, which is why the requirement is deferred rather
> than merely unimplemented.
>
> *What is unaffected.* Light and dark themes are still required, by `REQ-UI-083`, which
> also carries the obligation not to foreclose further themes. What is deferred here is the
> **accessibility guarantee** — the numeric luminance and contrast floors, colour-vision
> simulation, and system high-contrast integration. When it is picked up, the third theme it
> adds is exactly the case `REQ-UI-083`'s non-foreclosure test already exercises.

**`REQ-UI-091` (P1) — Accessibility.**
The application shall be operable entirely from the keyboard; all interactive elements shall
expose UI Automation properties with meaningful names; the plot surface shall expose trace and
marker values to assistive technology as text; and no information shall be conveyed by colour
alone.
*Rationale:* a custom-rendered plot surface (`REQ-UI-010`) is invisible to screen readers unless
an automation peer is written for it deliberately. That work must be scheduled, not discovered.
**AC:** A full measurement setup — connect, configure, place markers, read values, save state —
is completable with no mouse; an automation inspection tool reports named elements for every
control and readable values for markers.

### 13.10 The 89400-series ancestor, for context

Included because the software's visual language descends directly from this hardware, and
because several conventions only make sense once you have seen where they came from.

**The defining sentence, repeated across every 89400 manual: [V]**
> "The analyzer's screen is divided into two main areas. The **menu area, a narrow column at
> the screen's right edge, displays softkey labels**. The **data area, the remaining portion of
> the screen, displays traces and other data**."

Softkey behaviour: a function is indicated by "a video label to the left of the key"; toggle
softkeys change "which word is highlighted"; unavailable functions are "ghosted" and "appear
less bright than a normal softkey". Hardkeys are fixed-function front-panel buttons. **[V]**

Display: colour CRT, 7.1 in diagonal, driven by a TI TMS34020 graphics system processor, 400
horizontal lines at 60 Hz, capable of "60 updates per second". **"Display points/trace 401"**,
"Number of colors: User-definable palette", graticule on/off. Layout: "one to four traces on
one, two, or four grids or a quad display", traces lettered **A/B/C/D**. **[V]**

Annotation differs from the software in two informative ways: the **marker readout sits at the
top** of the display, and computed marker values (band power, carrier-to-noise, noise density)
appear in the **lower-left corner**. Marker glyphs: "a diamond shows the location of the
marker", "a square shows the location of the **offset** marker". **[V]**

**Lineage worth noting:** the diamond survived unchanged into the software; the square *offset*
marker became the diamond *delta* marker with the `3Δ1` label; the X was newly assigned to
Fixed markers. And the 89400's data-format vocabulary — `Polar: Constellation (dots only)`
versus `Polar: Vector (dots plus intersymbol paths)` — is the 89600's Constellation-versus-IQ
distinction, verbatim, thirty years on. **[V]**

### 13.11 What the team must decide, because no source states it

Consolidated so these are decided deliberately rather than by whichever developer reaches
them first. All are **[U]**.

| # | Undocumented item | Recommendation |
|---|---|---|
| 1 | Graticule division count | 10 × 10, configurable (§13.2 rationale) |
| 2 | Default trace background | Black |
| 3 | Default trace colours A, B, C, D… (20 indexed) | Choose for luminance separation on black; document the palette |
| 4 | Limit / margin colours | Red / yellow |
| 5 | Default typefaces and sizes | Segoe UI 9 pt; Consolas 9 pt tabular |
| 6 | Toolbar icon artwork | Commission or adopt an open icon set; the reference icons are images without alt text |
| 7 | Whether the graticule outline persists when grid lines are hidden | Keep the outline |

*Only the spectrogram map (blue → red, 64 steps) is documented. Everything else in this table
exists solely inside screenshots, and a screenshot is the only way to settle it exactly — the
full-window captures in the PathWave technical overview (5992-4210) and the 89600 VSA brochure
(5990-6553) are the best candidates if pixel fidelity is ever required.*
---

### 13.12 State management

**`REQ-STA-001` (P0) — Setup save and recall.**
A saved state shall contain, matching the reference product's documented content:
measurement type, frequency and span, trigger configuration, resolution bandwidth, input
settings (range, coupling, digital, trigger, external mixer), analysis parameters, trace
window positions, trace data and overlay state, trace display properties (format, X/Y
scaling, digital demodulation configuration, spectrogram settings), marker types, positions
and calculations, and source parameters. **[V]**
**AC:** A state is saved from a configuration in which every listed item has been set away
from its default, the application is reset, and the state recalled: each listed item returns
to its saved value. The check enumerates the list rather than sampling it, so an item added
to the state model without save/recall support fails. Recall is complete before the first
post-recall acquisition, so the first trace reflects the recalled settings rather than the
previous ones.

**`REQ-STA-002` (P1) — Documented exclusions.**
A state shall **not** contain recordings, math functions, data registers or display
preferences; these shall be saved and recalled independently. **[V]** The save dialog shall
state this explicitly rather than leaving users to discover it.
**AC:** With a recording loaded, math functions defined, data registers populated and display
preferences changed, saving and recalling a state leaves all four untouched — neither
captured into the state nor cleared by the recall, both of which are failures. Each is
separately saveable and recallable through its own command. The save dialog names these four
exclusions in its own text, so the user is told rather than left to find out.

**`REQ-STA-003` (P1) — Format and versioning.** **[DESIGN CHOICE]**
State shall be stored as human-readable, diffable, versioned JSON in a container carrying a
schema version, with forward-compatible loading (unknown fields preserved on round-trip).
*Rationale:* the reference product uses an opaque binary `.setx`; a text format is superior
for version control, support, and diagnosis, and there is no interoperability requirement.
**AC:** A state file from schema version *n* loads in version *n+1* software with documented
migration; round-tripping preserves unknown fields byte-for-byte.

**`REQ-STA-004` (P1) — Context-name matching on recall.**
Recalling a multi-measurement state shall match measurements to existing contexts by name,
raising a specific, actionable error on mismatch rather than partially applying. **[V]**
**AC:** Recalling a multi-measurement state into an application whose contexts carry the same
names restores each measurement to its own context. Where a name does not match, the recall
is refused as a whole and the pre-recall configuration is intact — asserted by comparing full
configuration before and after the failed recall, since a partial apply is the failure mode
this exists to prevent. The error names the context or contexts that did not match and what
was expected, rather than reporting a generic recall failure.

**`REQ-STA-005` (P2) — Presets.** A factory preset returning all settings to documented
defaults, plus user-definable presets.
**AC:** Factory preset returns every setting to the default this specification documents for
it, enumerated over the settings model so a setting whose default is undocumented fails the
test rather than passing unnoticed. It leaves the hardware setup alone per `REQ-UI-061`.
User-defined presets can be created, applied and deleted, survive a restart, and applying one
is equivalent to recalling the state it was captured from.

---

## 14. Automation and programming interfaces

**`REQ-API-001` (P1) — .NET automation API.**
A managed API shall expose the object model
`Application → Measurement(s) → Input / Trigger / Display → Trace(s) → Marker(s)`, plus
limit tests and recordings, sufficient to configure, run and read every measurement
without the UI.

**[DESIGN CHOICE]** — **this is OpenVSA's own object model, and source-compatibility with
the reference API is explicitly not a goal.**

> *Where the shape came from.* The reference product's exact .NET class hierarchy could not
> be extracted (its API reference site is a JavaScript frameset). The hierarchy above is
> inferred from the documented COM model and from MATLAB integration examples that use
> `Measurement`, `MeasurementExtension` and `InputExtension` objects with
> `GetParameter`/`SetParameter`/`ParameterNames()`. **[V for those three type names]**
>
> *Why parity is not pursued.* Matching the reference API would mean reproducing not just
> class names but the **string keys** of `REQ-API-003`'s `GetParameter`/`SetParameter`
> surface — far more numerous than the types, equally unverified, and a permanent
> compatibility burden. No migration path for existing 89600 scripts has been asked for, and
> the API is more useful designed around OpenVSA's own model. The inferred hierarchy is
> adopted because it is a sound design, not because it matches anything.
>
> Should source-compatibility ever be wanted, it is a new requirement and begins with
> browsing the reference API site directly. `REQ-API-005`'s COM surface would be the better
> target, being the documented one.

**AC:** The object model exposes the stated hierarchy, and it is **complete**: enumerated
over the measurement catalogue, every measurement can be configured, run and read entirely
through the API, with every trace data type of `REQ-DSP-040`/`REQ-DSP-041`, every marker
function of `REQ-MKR-005`, limit tests and recordings reachable — a measurement reachable
only from the UI fails the test. Combined with `REQ-API-002`, the same suite runs with no UI
loaded. Parity with the reference API is **not** asserted by any test, and no test names a
reference type or parameter string as an expected value; claiming untested compatibility is
the failure mode this criterion guards against.

**`REQ-API-002` (P1) — Headless operation.**
The API shall be usable with no UI loaded, so that measurements can run in automated test
systems and in CI.
**AC:** A console application referencing only `OpenVSA.Api` and the HAL configures a demod
measurement against a recording and prints the error summary — with WPF never loaded.

**`REQ-API-003` (P2) — Generic parameter access.**
In addition to strongly-typed members, a `GetParameter`/`SetParameter`/`ParameterNames()`
string-keyed surface shall be provided, mirroring the reference product and enabling
scripting from environments with weak generics support (notably MATLAB via
`NET.addAssembly`). **[V]**
**AC:** `ParameterNames()` returns every parameter reachable through the strongly-typed
members, and each name round-trips through `SetParameter`/`GetParameter` to the same value
the typed member reports — the two surfaces drive one piece of state, so a parameter
settable only one way fails. The generic surface is checked from a weak-generics caller, not
only from C#, since that is what it exists for. An unknown parameter name raises the
documented exception of `REQ-NFR-042` rather than being silently ignored. Parameter names
are OpenVSA's own and are not asserted against the reference product's, per `REQ-API-001`.

**`REQ-API-004` (P2) — SCPI server.**
An optional SCPI-over-TCP server shall expose a documented subset of the API, so that
OpenVSA can be driven from existing test frameworks. Command syntax shall follow SCPI-99.
**AC:** The server is **off by default** and binds only when enabled, which `REQ-NFR-036`'s
no-egress-without-opt-in rule requires; a test asserts no listening socket exists in a
default install. Enabled, it accepts SCPI-99 syntax including the common commands, long and
short mnemonic forms, compound headers and query suffixes, and rejects malformed input with
a SCPI error rather than dropping the connection. The exposed subset is documented, and a
test compares the documentation against the implemented command table so the two cannot
drift. Every command maps onto the same API state as `REQ-API-001`, verified by setting a
parameter over SCPI and reading it back through the .NET API. Activity appears in the
`REQ-API-007` log.

**`REQ-API-005` (P3) — COM interop surface.** A COM-callable wrapper for legacy clients.
*Note:* the reference product's own COM API is documented as obsoleted and no longer
updated **[V]**; OpenVSA should treat COM as legacy from the outset.
**AC:** A COM client creates the object model, configures and runs a measurement and reads
results, exercising the same paths as `REQ-API-001`. The wrapper adds no capability of its
own — it delegates, so a feature reachable through COM but not through the .NET API fails.
Being P3 and legacy, it is permitted to expose a documented subset rather than the whole
surface; what it omits is listed, not discovered. Registration is optional and a default
install that never registers COM is fully functional.

**`REQ-API-006` (P2) — Macros.**
User extensions shall be loadable as compiled .NET assemblies from a macros folder, with a
Macros window listing and running them — mirroring the reference product, whose macros are
full Visual Studio solutions rather than a scripting language. **[V]**
**AC:** A compiled assembly dropped into the macros folder appears in the Macros window and
runs, driving the measurement through `REQ-API-001`. Loading obeys `REQ-NFR-041` in full:
only configured directories are searched, and an unsigned assembly raises the trust prompt
rather than loading silently. A macro that throws is contained — the application survives
with an error reported to the Macros window and the measurement unaffected. Removing the
assembly removes the entry without a restart.

**`REQ-API-007` (P2) — Command log window.**
An Output/SCPI Log window shall echo API and SCPI activity, so users can discover the
programmatic equivalent of UI actions they perform. **[V]**
*Rationale:* this is the single most effective automation-discoverability feature an
instrument application can offer.
**AC:** Performing an action in the UI echoes the equivalent API or SCPI call into the log,
and **that echoed text, replayed verbatim, reproduces the action** — the property that makes
the window useful rather than decorative, and the one a paraphrased log fails. Coverage is
enumerated over the settable parameters of `REQ-API-003`, so a UI control with no logged
equivalent fails. The log is copyable, is bounded so a long session cannot exhaust memory
per `REQ-NFR-012`'s spirit, and states when it has truncated rather than dropping entries
silently.

---

## 15. Non-functional requirements (consolidated)

| ID | Requirement | Priority |
|---|---|---|
| `REQ-NFR-030` | Windows 10 21H2 or later, x64; .NET Framework 4.7.2 present | P0 |
| `REQ-NFR-031` | Installer deploys without administrative rights where VISA is already present | P2 |
| `REQ-NFR-032` | Application starts and runs usefully with **no** hardware and **no** VISA installed (simulator + file playback only) | P0 |
| `REQ-NFR-033` | All user-visible strings externalised for localisation; en-GB default | P2 |
| `REQ-NFR-034` | Structured logging with per-subsystem levels; log bundle export for support | P1 |
| `REQ-NFR-035` | Unhandled exceptions produce a diagnostic report without data loss to in-progress recordings | P1 |
| `REQ-NFR-036` | No telemetry or network egress without explicit opt-in | P0 |
| `REQ-NFR-037` | Numeric results reproducible bit-for-bit across runs for identical input and settings on the same machine — **see the qualification below** | P1 |
| `REQ-NFR-040` | Report generation: export a measurement (traces, settings, error summary, optional instrument screen capture) to PDF/HTML with a template | P2 |
| `REQ-NFR-041` | Plug-in assemblies (personalities, macros) loaded only from configured directories, with optional Authenticode signature enforcement and a clear trust prompt for unsigned code | P1 |
| `REQ-NFR-042` | Automation API: documented exception contract, semantic API versioning, and a published deprecation policy | P2 |
| `REQ-NFR-038` | Public API surface documented with XML doc comments; docs generated in CI | P2 |
| `REQ-NFR-039` | Code coverage ≥70 % overall, ≥90 % for `OpenVSA.Dsp` and `OpenVSA.Demod` | P1 |

**`REQ-NFR-030`** fixes the platform floor.
**AC:** The installer and the application both refuse to run below Windows 10 21H2 or on a
non-x64 process, with a message naming the unmet requirement rather than failing obscurely.
A missing .NET Framework 4.7.2 is detected before any managed entry point runs and reported
the same way. The build emits x64 only, per `REQ-NFR-001` and the solution's single platform
— a test fails on an AnyCPU or x86 output, since a 32-bit process cannot hold the buffers
§6 requires.

**`REQ-NFR-031`** governs installation privilege.
**AC:** With VISA already present, a per-user installation completes as a standard user with
no elevation prompt, and the installed application starts and runs. Where elevation is
genuinely needed — a machine-wide install, or installing VISA itself — it is requested
explicitly with the reason stated, never assumed. Uninstall as the same user removes
everything the install wrote outside the user profile's data directories.

**`REQ-NFR-032` deserves emphasis.** The ability to run the full analysis stack with no
instrument attached is what makes the DSP developable, testable in CI, and demonstrable.
It should be treated as an architectural constraint, not a convenience feature.
**AC:** On a machine with no VISA runtime installed and no instrument attached, the
application starts and the simulator and file-playback front ends are available. A test
asserts the VISA assemblies are not loaded in that configuration, so the capability cannot
regress into "starts, but only because VISA happened to be present". `REQ-NFR-036`'s
no-egress rule holds throughout.

**`REQ-NFR-032a` (P0) — A full measurement runs with no hardware.**
**AC:** On the same machine, a full demodulation measurement runs to an error summary.
*Split from `REQ-NFR-032`: an error summary is the demodulator's output and Phase 2 delivers
it. The half that matters architecturally — that the product starts and offers its sources
with no VISA present — is provable from the first build and stays where it belongs.*

**`REQ-NFR-033` needs a scope decision, or it contradicts the exact-string requirements.**
This requirement is **externalisation only: no second locale is shipped.** Strings live in
resources rather than in code so that translation is possible later; en-GB is the only
locale provided, and no translation work is in scope.

Two carve-outs, without which this requirement would break criteria already agreed
elsewhere:

- **Terminology is not localised.** The trace indicator strings of `REQ-UI-041`, the `NAN`
  and `INF` readouts of `REQ-UI-032`, the error-summary row labels of `REQ-UI-053` and the
  `SNR (MER)` label of `REQ-DEM-069` are fixed in every locale, exactly as SCPI mnemonics
  are. Those requirements assert them as exact literals; making them translatable would
  falsify their acceptance criteria. Chrome, menus, dialogs and messages localise;
  measurement vocabulary does not.
- **Files and exports use invariant culture.** Every path that writes a number to a file —
  the export formats of `REQ-REC-005`, saved state, logs, reports — formats with
  `CultureInfo.InvariantCulture`, independent of UI language. A comma decimal separator
  would corrupt CSV outright, and `REQ-NFR-037`'s bit-for-bit reproducibility requires the
  written form not to depend on machine locale.

**AC:** No user-visible string is a literal in code — asserted by a check over the UI
assemblies that fails on a hard-coded display string — and every one resolves through a
resource lookup with en-GB satisfying all of them. The carve-outs are enforced rather than
merely documented: the terminology above is asserted to come from a non-localised source, so
adding it to a resource file fails; and a test formats numbers under a comma-decimal culture
and asserts every export, state and log path still writes `.`, which catches the
locale-dependent formatting bug at its source. Running the application under a non-en-GB
system culture falls back to en-GB without missing strings or layout loss.

**`REQ-NFR-034`** covers diagnosability in the field.
**AC:** Log level is settable per subsystem and takes effect without a restart, verified by
raising one subsystem's level and asserting only its output increases. Entries are
structured — machine-parseable fields, not formatted prose — so a support bundle can be
queried rather than read. The bundle export gathers logs, configuration and version
information into one file in a single action, and **redacts nothing silently**: whatever it
omits is listed in the bundle. Writing a log entry never blocks the measurement thread,
which `REQ-NFR-011` and `REQ-NFR-012` make a correctness matter rather than a performance
one.

**`REQ-NFR-035`** is about not compounding a crash with data loss.
**AC:** An unhandled exception produces a diagnostic report containing the exception, the
build version and the active measurement configuration. The recording clause is tested
directly rather than assumed: with a recording in progress, an exception is injected and the
recording is afterwards found complete and readable up to the point of failure, not
zero-length or truncated mid-record. The report is written before the process exits, and a
failure to write it does not itself prevent the recording being closed cleanly — the
ordering matters, so it is asserted.

**`REQ-NFR-036`** is a privacy guarantee, and is tested as one.
**AC:** In a default installation the application makes **no outbound network connection of
any kind** — asserted by running the full start-configure-measure-exit cycle under a network
monitor with the simulator as source and failing on any egress, not merely by absence of a
telemetry component. There is no update check, no usage reporting and no crash upload
without opt-in; opt-in is off by default and, where offered, states what would be sent
before it is enabled. Nothing about this depends on a licence or entitlement check, which
`REQ-LIC-010` removes entirely. A test asserts the process opens no listening socket, which
holds trivially until a server exists and keeps holding afterwards.

**`REQ-NFR-036a` (P2) — The SCPI server is the only listener.**
Extends the privacy guarantee of `REQ-NFR-036` over the one listener the product may have.
**AC:** `REQ-API-004`'s SCPI server is the only listening socket the product ever opens, and
it is off by default: enumerate the process's listening sockets in a default installation and
find none, enable the server and find exactly one, on the configured port only.
*Split from `REQ-NFR-036` so the no-egress guarantee can be asserted from the first build
rather than waiting on a server that Phase 4 delivers.*

**`REQ-NFR-040`** covers report output.
**AC:** A report generated from a measurement contains its traces, settings, error summary
and, when requested, the `REQ-E44-006` instrument screen capture, in both PDF and HTML. The
template is user-supplied: substituting a different template changes the layout without a
rebuild, and a template referencing a field that does not exist fails with that field named
rather than emitting a blank. Numbers in the report use invariant culture per `REQ-NFR-033`,
and the metric provenance of `REQ-DEM-072` travels with the results, since a report is
exactly where a number gets separated from its conditions.

**`REQ-NFR-042`** covers the API's contract with its callers.
**AC:** Every public API member documents the exceptions it can throw, and a test asserts
that the exceptions actually thrown are within the documented set — an undocumented
exception type escaping the API surface fails. Assembly and package versions follow semantic
versioning, checked against the previous release by an API-diff tool so a breaking change
without a major bump fails the build. The deprecation policy is published, and every member
marked obsolete names its replacement and the version in which it will be removed.

**`REQ-NFR-037` needs a qualification, or it silently contradicts the threading model.**
Bit-for-bit reproducibility is incompatible with unordered parallel reduction: floating-point
addition is not associative, so `Parallel.For`-style accumulation at
`ProcessorCount − 1` parallelism (`REQ-NFR-010`) and runtime-width `Vector<T>` partial sums
both give run-to-run variation in the last bits. The requirement shall therefore read:

> Numeric results shall be reproducible bit-for-bit **given a fixed partition schedule**. All
> accumulating stages shall use fixed-size, index-determined partitions and a deterministic
> reduction tree, and shall not use unordered `Parallel.ForEach`. Where a stage cannot meet
> this, it shall be documented and excluded by name.

**AC:** 100 consecutive runs over a fixed recording produce byte-identical result buffers on
the same machine, at every supported degree of parallelism.

**`REQ-NFR-041` — why plug-in trust is a requirement and not paranoia.** `REQ-ARC-003` and
`REQ-API-006` load arbitrary .NET assemblies from disk directories, and `REQ-REC-005` parses
ten binary file formats from untrusted sources. `REQ-PER-002` addresses plug-in *faults* but
says nothing about plug-in *malice*, and `REQ-REC-009` covers parser robustness. Together
these are the product's entire attack surface and they should be treated as such deliberately.
**AC:** Assemblies load only from the configured directories: a plug-in placed elsewhere on
disk, or reachable by a relative path escaping a configured directory, is not loaded — the
traversal case is tested explicitly, since a naive path join permits it. With signature
enforcement on, an unsigned or invalidly signed assembly is refused and a validly signed one
loads; with enforcement off, an unsigned assembly raises a trust prompt naming the file and
its publisher, and declining leaves it unloaded. The prompt is not suppressible by the
plug-in itself. Refusal is reported to the user rather than logged and swallowed, since a
personality that silently fails to appear is indistinguishable from one that was never
installed.

**`REQ-NFR-038`** keeps the API documented as it changes.
**AC:** Every public type and member carries XML doc comments — the build treats a missing
comment on the public surface as an error, so documentation cannot lag behind the code — and
CI generates the documentation on every run, failing on a malformed reference or a broken
`cref`. The generated output is published as a build artefact.

**`REQ-NFR-039`** sets the coverage floor.
**AC:** CI measures line and branch coverage and fails below 70 % overall, or below 90 % for
`OpenVSA.Dsp` and `OpenVSA.Demod` individually — the per-project floors are checked
separately, since a high overall figure can hide a weak DSP project. Coverage is measured
over the closed-form and property tests of `REQ-TST-001`, not inflated by tests that execute
code without asserting on it; a test with no assertion fails review. The figure is reported
per project on every run so a decline is visible before it crosses the threshold.

---

## 16. Licensing and feature availability

The reference product is commercial software sold in tiers: Option 200 for base analysis,
89601AYAC for digital demodulation, a BH-series option per standard. Much of its
documentation is organised around which option provides what, and this specification cites
those SKUs where they explain what a feature is.

**OpenVSA does not work that way.** It is one edition, free, with everything in it. The
requirements below exist to state that as a binding property rather than a present-day
accident, because feature gating is easy to introduce later and hard to remove.

*Superseded:* `REQ-LIC-001` through `REQ-LIC-004` previously specified entitlement-based
gating, licence models, selective checkout and an ungated development build. They were
withdrawn when the product decision was taken that OpenVSA is fully functional for every
user. The IDs are retired, not reused.

**`REQ-LIC-010` (P0) — One edition, no feature gating.**
Every feature OpenVSA ships is available to every user, unconditionally. There shall be no
entitlement check, licence file, licence server, activation step, dongle, trial period,
feature flag serving as a paid tier, or build configuration that produces a reduced edition.
Personalities and plug-ins are covered too: a personality that loads, runs (`REQ-PER-001`).
*Rationale:* this is a deliberate product decision, not an unimplemented feature. Anyone who
downloads OpenVSA gets all of it. Stating it normatively means a later change of heart has to
be argued for and re-specified, rather than arriving one `if` statement at a time.
**AC:** No entitlement or licence-check type, interface or method exists in any shipped
assembly, asserted by an architecture test over the public and internal surface that fails on
a member matching entitlement/licence-gating naming, and by the absence of any
licensing project from the solution. Exactly one edition is produced: the build emits a single
distributable, and a test fails if a build configuration name or an `#if` symbol partitions
the feature set. The application starts and runs with no network access at all, which
`REQ-NFR-036` already requires and which no activation step could satisfy.

**`REQ-LIC-010a` (P0) — No feature arrives gated, over the full catalogues.**
The guarantee of `REQ-LIC-010` shall be enforced against every feature the product ends up
having, not only against the absence of gating machinery.
**AC:** Every measurement, trace data type, demodulation format and personality can be
instantiated and run in a default installation with no configuration — enumerated over the
full catalogues of `REQ-DSP-040`, `REQ-DSP-041` and `REQ-DEM-010`, so a newly added feature
cannot quietly arrive gated.
*Split from `REQ-LIC-010`: the absence of gating machinery is provable from the first build
and is the guarantee that matters most, while enumerating the catalogues cannot run until the
demodulation formats of `REQ-DEM-010` exist.*

**`REQ-LIC-011` (P1) — Distribution terms.**
OpenVSA shall be distributed under a permissive open-source licence (currently **MIT**, see
`LICENSE`), and nothing in the shipped product shall impose a usage restriction beyond it —
no runtime terms acceptance, no phone-home, no per-seat condition.
*Interaction with `REQ-NFR-008`:* the dependency register exists partly to protect this. A
copyleft dependency linked into a shipped binary would impose obligations the project's own
licence does not, which is why `REQ-NFR-004` keeps the default FFT provider managed and
copyleft-free.
**AC:** A `LICENSE` file stating the project's terms is present in the repository and in the
distributable. No shipped code path presents licence terms for acceptance or blocks
functionality pending it. The `REQ-NFR-008` CI check passes, so no dependency in a shipped
binary carries terms more restrictive than the project's own licence; introducing a GPL
dependency fails that check and therefore this requirement too.

---

## 17. Verification and test strategy

Verification is where a project of this kind succeeds or fails. DSP defects are quiet: the
software produces a plausible number that is wrong, and nobody notices for months. The
strategy below is built around making wrongness loud.

### 17.1 Analytic unit tests

**`REQ-TST-001` (P0)** — Every DSP primitive shall be tested against a closed-form
analytic result, not against a previous run of itself.

| Component | Analytic reference |
|---|---|
| FFT | Known transform pairs; Parseval's theorem to **1e-12 for the double-precision provider, 1e-5 for single** (`REQ-NFR-004a`) |
| Windows | ENBW and coherent gain computed from coefficients, against §9.2 table |
| RRC/RC filters | Cascade identity; analytic values at singular points |
| Resampler | Pure tone in, pure tone out; measured SFDR against design target |
| Averaging | Coherent vs incoherent SNR behaviour (`REQ-DSP-030` AC) |
| CCDF | Rayleigh/Gaussian analytic distribution |
| Metrics | Closed-form values for deliberately impaired constellations |

**AC:** Every component in the table has at least one test whose expected value is computed
from the analytic reference named beside it, independently of the implementation under test.
The prohibition is enforced, not merely stated: no DSP test compares against a stored output
of a previous run, asserted by a check that fails on a golden-output or approved-file
comparison anywhere in the DSP test suite — that pattern is exactly how a wrong result
becomes the baseline it is later validated against. A DSP primitive with no closed-form test
fails the build rather than passing untested, so the table is a floor and not a sample. Where
a primitive genuinely has no closed form, the fallback is a property-based test asserting an
invariant (linearity, energy conservation, an inverse round trip), never a recorded output.

### 17.2 Impairment round-trip — the primary correctness proof

**`REQ-TST-002` (P0) — Injected-impairment recovery matrix.**
For each supported modulation format, and for each impairment in `REQ-SIM-002`, a test shall
inject a known magnitude and assert that the corresponding metric recovers it within
tolerance (5 %, or 0.1 dB, whichever is looser).

**Four of the twelve impairments have no scalar metric and need explicit handling** — without
this the matrix has silent holes exactly where they are hardest to notice:

| Impairment | Metric, or qualitative criterion |
|---|---|
| **Symbol-clock error (ppm)** | No direct metric is possible — `REQ-DEM-030` (P0) forbids estimating symbol rate. Test the **signature** instead: EVM-versus-symbol-index shall be V-shaped with minimum near block centre, and the fitted slope shall be proportional to the injected ppm. Report the fitted slope as `SymClk Err` (the reference product carries such a row for LTE). |
| **Phase noise** | Integrated RMS phase error over a defined offset range, and the shape of the **Error Vector Spectrum** — assert that injected noise at a known offset frequency appears at that offset. |
| **AM/AM and AM/PM compression** | Fit gain and phase versus instantaneous amplitude across the symbol set and report the AM/AM and AM/PM curves; assert recovery of the injected compression characteristic. |
| **Multipath** | The estimated **Channel Frequency Response** of `REQ-DEM-053`; assert it matches the analytic two-ray response (already required by that requirement's own AC). |

**AC:** Every cell of the matrix is either a quantitative recovery assertion or an explicitly
labelled qualitative signature assertion. **No cell may be blank or skipped** — a missing cell
is a gap in the correctness proof and shall fail the build.

This matrix is the single most valuable artefact in the whole test suite, because it closes
the loop end-to-end: generator → channel → demodulator → metric. A defect anywhere in that
chain shows up as a failed cell.

**AC:** The matrix runs in CI on every commit affecting `OpenVSA.Dsp`, `OpenVSA.Demod` or
`OpenVSA.Hal.Sim`, and is published as a report showing recovered-versus-injected for every
cell.

**`REQ-TST-003` (P1) — Cross-impairment isolation.**
Injecting impairment A shall not materially perturb the metric for unrelated impairment B.
**AC:** With 3 dB of gain imbalance injected and no quadrature skew, reported quadrature
error remains below 0.5° — **except** in the documented ambiguous mapping case of
`REQ-DEM-067`, which shall be its own explicitly characterised test rather than an exclusion.

### 17.3 Hardware cross-validation

**`REQ-TST-004` (P1) — E4406A comparison suite.**
Per `REQ-E44-007`, OpenVSA results shall be compared against the E4406A's own personality
measurements for W-CDMA, EDGE and cdmaOne signals. Divergence beyond tolerance shall be
investigated and either fixed or documented with an explanation.
*This is the only truly independent check available on this bench and should be run at every
release.*
**AC:** The suite runs W-CDMA, EDGE and cdmaOne signals through both the E4406A's own
personality and OpenVSA's demodulator on the same capture, and compares the metrics they
report. Tolerances are those of `REQ-TST-004a` — relative to the measured value plus the
residual budget, not a bare absolute figure. Divergence beyond tolerance fails the run, and a
divergence accepted as understood is recorded with its explanation in a checked-in register,
so an unexplained divergence cannot be quietly tolerated by adjusting the tolerance. The
suite runs at every release and is skipped, with an explicit skip report, when the instrument
is absent rather than silently passing.

**`REQ-TST-004a` (P1) — OpenVSA's own residual-EVM budget.**
OpenVSA shall define and verify a **self-noise / residual-EVM budget** — the EVM it reports for
an ideal, impairment-free simulated signal — of **< 0.1 % RMS** for QPSK through 64-QAM and
**< 0.3 %** for 1024-QAM and above.
*Rationale:* `REQ-E44-007`'s "within 0.5 percentage points" is unanchored without this. Half a
point is generous at 15 % EVM and absurd at 1 %. Comparison tolerances shall be expressed
**relative to the measured value plus the residual budget**, not as a bare absolute figure.
*(Keysight's published residual-EVM specification for the reference demodulator was not located
during research — §20 Q9 — so this budget is self-imposed rather than matched.)*
**AC:** An ideal impairment-free simulated signal is demodulated for each supported format
and the reported EVM is below 0.1 % RMS for QPSK through 64-QAM and below 0.3 % for 1024-QAM
and above; a format exceeding its budget fails. The budget is a published figure, recorded
where users and the comparison suite can read it, not merely a test constant. `REQ-TST-004`'s
tolerances are computed from the measured value **plus** this residual, and a test asserts
that composition — a bare 0.5 percentage-point comparison fails, since that is the unanchored
form this requirement exists to replace.

**`REQ-TST-005` (P2) — Golden recordings.**
A corpus of recorded IQ captures with known-correct expected results shall be maintained in
version control (or in an artefact store, given size), and replayed in CI.
**AC:** The corpus is versioned alongside the code, replayed in CI on every run, and each
entry's expected results are **traceable to an independent source** — a closed-form value, a
generator parameter, or an E4406A comparison per `REQ-TST-004` — recorded with the entry.
An entry whose expected value is simply a previous OpenVSA output fails review; that is the
distinction `REQ-TST-001` draws, and it is what makes this corpus a check rather than a
record of past behaviour. Adding an entry requires its provenance; regenerating an expected
value requires a stated reason.

### 17.4 Regression and performance

**`REQ-TST-006` (P1)** — Numerical regression: stored expected outputs for a fixed corpus,
compared with explicit tolerances rather than exact equality, with tolerances justified in
comments.

> **Scope, against `REQ-TST-001`.** These two requirements appear to conflict and do not.
> `REQ-TST-001` governs **DSP primitives**, which must be checked against closed-form
> analytic results and never against a previous run. This requirement governs **end-to-end
> numerical regression**, which exists to detect unintended change across the whole chain.
> A stored expected output is legitimate here and illegitimate there. The boundary is not
> stylistic: a primitive validated against its own past output can be wrong and stay wrong,
> whereas a whole-chain regression is comparing a system whose parts are independently
> verified.

**AC:** The regression suite lives separately from the primitive tests of `REQ-TST-001`, and
a test asserts that separation so a primitive check cannot migrate into the regression suite
and lose its analytic reference. Every comparison uses an explicit tolerance — exact equality
fails review — and each tolerance carries a comment justifying its magnitude in terms of the
numerical path, not "this is what it produced". Every stored expected output records the
provenance required by `REQ-TST-005`. A regression failure reports the observed and expected
values with their difference expressed against the tolerance.

**`REQ-TST-007` (P1)** — Performance regression per `REQ-NFR-020`–`REQ-NFR-026`, failing
the build on >15 % regression.
**AC:** The harness those requirements' shared criteria describe exists and measures every
target whose feature has been delivered, including the rendered ones, and a deliberately
introduced 20 % slowdown fails the build while a 5 % one does not — the threshold is tested,
not merely configured. Baselines are stored per machine class, since the targets are stated
for the reference machine, and a run on unrecognised hardware reports that rather than
comparing against an inapplicable baseline. Measurements report variance, and a run too noisy
to distinguish 15 % is reported as inconclusive rather than passed. **A target whose feature
does not yet exist is reported as not-yet-measured and fails the run if it is silently
skipped** — the harness may not quietly shrink to the targets that happen to be implemented.

**`REQ-TST-007a` (P1)** — Every performance target actually under the harness.
**AC:** All seven targets of `REQ-NFR-020`–`REQ-NFR-026` are measured, none remaining in the
not-yet-measured state `REQ-TST-007` requires the harness to report.
*Split from `REQ-TST-007` because the harness is needed from Phase 0 — every one of the seven
targets shares its criteria, so no target could be met until it existed — while the last of
the seven targets is a 4 GB recording playback that Phase 3 delivers.*

**`REQ-TST-008` (P2)** — UI automation smoke tests covering window creation, trace
configuration, state save/recall and marker interaction.
**AC:** All four areas are exercised through the UI automation layer rather than by calling
view models directly, so a binding or command-routing break is caught. The suite creates
trace and tool windows, configures a trace's data and format, saves and recalls a state and
asserts the recalled configuration matches per `REQ-STA-001`, and places, moves and couples
markers per `REQ-MKR-004`. It runs headless in CI, and a failure identifies the step and
captures the visual tree at that point rather than reporting only a timeout.

**`REQ-TST-009` (P1)** — A long-duration soak (≥8 hours) against the simulator asserting
bounded memory, no handle leaks, and no degradation in update rate.
**AC:** The soak runs at least 8 hours against the simulator with traces updating and the UI
live. Managed and unmanaged memory are bounded: measured against a trend line over the run,
not merely against a ceiling, so a slow leak that stays under the cap still fails. Handle and
GDI object counts return to their starting range after windows and traces are created and
destroyed repeatedly. Update rate over the final hour is within 5 % of the first hour, which
catches degradation that a start-and-end sample would miss. Pooled buffers per `REQ-NFR-011`
show no net growth, and the dropped-frame counter of `REQ-NFR-012` is reported at the end
rather than checked only for boundedness.

---

## 18. Delivery plan

Effort figures are **rough order-of-magnitude estimates in engineer-months** for a small
experienced team, offered for sequencing and expectation-setting, not as commitments. They
assume competent DSP and WPF experience already on the team; without in-house DSP
experience, multiply the Phase 2 and Phase 5+ figures substantially.

| Phase | Content | Key requirements | ROM effort |
|---|---|---|---|
| **0 — Foundations** | Solution structure, `IqBlock`, HAL contract, buffer pooling, simulated source, file playback of the native format, FFT/window core, minimal WPF shell with one spectrum trace, **plot-surface and hot-spot prototypes** | ARC-001..003, DAT-001, HAL-001..003, SIM-001..003, DSP-010, NFR-001..007, UI-010, UI-042 | 5–7 |
| **1 — Core VSA** | All base trace data types and formats, RBW/time coupling, averaging, overlap, gating, markers, limit tests, ACP/OBW, trace math, amplitude/correction chain, state save/recall, docking layout, **full annotation and in-place hot-spot editing**, accessibility | DSP-020..050, AMP-001..004, TRC-001..003, MKR-001..007, LIM-001..003, CHM-001..003, STA-001..005, UI-001..091 | 10–15 |
| **2 — Flexible demodulation** | Format catalogue, filters, block estimator, sync and pulse search, equaliser, full metrics suite, all demod result traces, impairment test matrix | DEM-001..083, TST-001..003 | 10–14 |
| **3 — Hardware** | VISA layer, E4406A driver (after SCPI verification), recording, import/export formats, cross-validation harness | VISA-001..004, E44-001..007, REC-001..007, TST-004 | 5–7 |
| **4 — Automation & packaging** | .NET API, headless operation, SCPI server, macros, command log, installer | API-001..007, LIC-010..011, NFR-030..039 | 4–6 |
| **5 — Personality wave 1** | GSM/EDGE, W-CDMA/HSPA, cdmaOne — each validated against the E4406A | PER-001..003, PER-010..011 | 8–12 |
| **6 — Personality wave 2** | cdma2000/1xEV-DO, NADC/PDC, custom OFDM | | 6–9 |
| **7 — Personality wave 3** | 802.11a/g/n/ac/ax, Bluetooth/BLE | | 8–12 |
| **8 — Personality wave 4** | LTE FDD/TDD, LTE-A | | 10–16 |
| **9 — Personality wave 5+** | 5G NR, DVB-S2/S2X, DOCSIS, and the wave-6 long tail | | 20–40+ |

**Phases 0–4 constitute a genuinely useful product** — a general-purpose vector signal
analyser with flexible demodulation, driving the E4406A. That is the point at which the
project delivers the capability that motivated it. Everything from Phase 5 onward is
breadth, and each wave is independently valuable and independently deferrable.

**`REQ-PLN-001` (P0)** — Phases 0–4 shall be treated as the minimum viable product and
shall not be descoped in favour of starting personality work early.
*Rationale:* personalities built on an unproven core inherit and disguise its defects.
**AC exempt:** *Planning constraint, not a product behaviour.* This requirement governs how
the delivery plan may be changed, so no property of a built artefact can be measured to
satisfy or violate it. It is honoured structurally instead: the backlog's milestones carry
Phases 0–4, and a breach shows as Phase 5+ work starting while Phase 0–4 issues remain open —
visible in the backlog, not in a test. Recorded as exempt rather than left unmechanised so
that `needs-ac` continues to mean "criteria are owed", not "criteria are impossible".

---

## 19. Risks

| ID | Risk | Impact | Mitigation |
|---|---|---|---|
| ~~RISK-01~~ | ~~E4406A SCPI for raw IQ retrieval is unconfirmed.~~ **CLOSED** — verified on the instrument 25 July 2026 (`REQ-E44-002`). | — | — |
| **RISK-01a** | **Silent acquisition truncation at 950 000 samples** (E4406A error 22). A caller trusting its own sweep-time setting analyses a shorter record than it believes it has, with no failed query. | Wrong results, silently | `REQ-E44-002c`: poll `:SYSTem:ERRor?` after every acquisition, `*CLS` beforehand, and independently verify returned $N$ against the request. |
| **RISK-02** | **GPIB throughput** makes continuous capture impossible (§6.3). | User expectation mismatch | Declare `SupportsGapFreeStreaming = false`; show duty cycle; design UI around block capture from the start. |
| **RISK-03** | ~~**WPF rendering performance** at high point counts and update rates.~~ **Retired 2026-07-29 — the prototype was built and the risk did not materialise.** Rasterising a 2²⁰-point frame costs 1.00 ms of a 72.2 ms frame (1.4 %) and is invariant in point count, because `REQ-NFR-006`'s decimation bounds drawn geometry by the pixel width before anything is drawn. The remaining budget risk is the **transform** at 60.96 ms (84.4 %). | ~~Core UX failure~~ Retired | ~~keep `D3DImage`/Direct2D as a designed-for fallback~~ — **withdrawn**: `D3DImage` degrades to software under RDP and without WDDM, so it has no path in the environments a bench instrument is operated from, and it would buy back 1.4 % of a frame. The software rasteriser is the strategy, not the fallback. See `REQ-NFR-005`, amended the same day. |
| **RISK-04** | **DSP correctness defects are silent.** | Wrong results trusted | The impairment matrix (`REQ-TST-002`) and E4406A cross-validation (`REQ-TST-004`) as gating CI. |
| **RISK-05** | **Confirmed fixed constraint: .NET Framework 4.7.2 / C# 7.3**, required for the NI-VISA assemblies — no longer a question, now a permanent design boundary. Portable-only `Span<T>` (no JIT intrinsic, no bounds-check elision), no `System.Runtime.Intrinsics`/`Vector128`, no `MathF`, no `FusedMultiplyAdd`, no async streams, older JIT, no ongoing Microsoft performance work, constrained library choice. | Permanent performance ceiling; fixes the §7.2 contract shape; recruitment | Accept and design around it: native kernels behind `IFftProvider`/SIMD interfaces where managed code cannot reach the target (`REQ-NFR-003`, `REQ-NFR-004`); raw arrays not `Span<T>` in hot loops; benchmark early so the ceiling is known, not discovered at Phase 2. |
| **RISK-06** | **Personality scope is effectively unbounded.** Full parity is a multi-year programme. | Never-finished project | Wave structure; each wave shippable; MVP explicitly at Phase 4. |
| **RISK-07** | **Undocumented reference behaviour** — several [U] items remain. | Subtle behavioural divergence | §20 tracks each; resolve before the dependent requirement is implemented, not after. |
| **RISK-08** | **Legal/IP.** Cloning a commercial product's behaviour. | Programme risk | Clone documented *behaviour and interfaces* only; never decompile; do not reproduce proprietary file-format internals or trade dress; take legal advice before any distribution beyond internal use. |
| **RISK-09** | **Third-party licence contamination** (FFTW/GPL and similar). | Distribution blocked | `REQ-NFR-004`, `REQ-NFR-008`; managed FFT default. |

---

## 20. Open questions

Each of these is a **[U]** item from the research pass. They are listed with the requirement
they block, so none is discovered late.

| # | Question | Blocks | How to resolve |
|---|---|---|---|
| ~~**Q1**~~ | ~~Exact E4406A SCPI for raw interleaved I/Q retrieval.~~ **RESOLVED 25 July 2026** by direct bench measurement — see `REQ-E44-002`/`002a`/`002b`/`002c`. Suffix 0 = raw interleaved I/Q; values are **peak volts** ($P=(I^2+Q^2)/100$); $T_s$ quantised to multiples of 1/15 MHz, $F_s^{max}$ = 7.5 MHz, RBW-driven; $N_{max}$ = **950 000 samples**, beyond which acquisition is **silently truncated** with error 22. | — | Done. |
| **Q1b** | E4406A GPIB transfer throughput — unmeasured; the bench harness performed bounded reads. | `REQ-NFR-027`, `REQ-E44-002d` | Time a real VISA read of a known-size binary block in Phase 3. |
| **Q2** | Byte-level SDF header layout (offsets for centre frequency, sample rate, timestamps). | `REQ-REC-005` (SDF) | Obtain the 89400-series "SDF File Format Utilities" manual. |
| **Q3** | Reference product's exact Trace Layout grid presets. | `REQ-UI-002` | Trial installation or direct help-tree browsing. |
| **Q4** | Literal menu order and keyboard shortcut table. | `REQ-UI-004` | Trial installation. |
| **Q5** | Reference .NET API class hierarchy. | `REQ-API-001` | Browse the API reference site directly (frameset defeated automated fetch). |
| **Q6** | Exact Trace Math operator list. | `REQ-DSP-046` | Trial installation. |
| ~~**Q7**~~ | ~~Is .NET Framework 4.7.2 / C# 7.3 a hard constraint?~~ **RESOLVED — yes, it is fixed**, required to consume the NI-VISA assemblies. All consequences in RISK-05 stand and must be designed around rather than deferred: portable-only `Span<T>`, no `System.Runtime.Intrinsics`, no `MathF`, and **no async streams** — hence the pull-based `AcquireNextAsync` contract in §7.2, which is now permanent rather than provisional. | — | Done. |
| **Q8** | Numeric ceiling of the reference product's "Max FFT Size". | `REQ-DSP-024` | Trial installation or datasheet. |
| **Q9** | Published residual-EVM/accuracy specifications for the reference demodulator. | Non-functional targets for `REQ-DEM-060` | Locate the specific datasheet; the technical overviews consulted are functional descriptions without accuracy tables. |
| **Q10** | Confirmation of "vector/coherent averaging" terminology versus the "Time" average types. | `REQ-DSP-030` terminology only (behaviour is unambiguous) | Help tree or SCPI `AVER:TYPE` enumeration. |
| **Q11** | Precise software version in which E4406A front-end support was dropped from the reference product. | Historical context only | Keysight KB pages (robots-blocked to automated fetch; accessible by browser). |
| **Q12** | Does $N_{\text{FFT}} = 2.56(N_f-1)$ hold on the real baseband path, as algebra requires? | `REQ-ACQ-001`, §2.2 | Trial installation: set baseband input, read back the achieved record length. |
| **Q13** | Does one binary genuinely work across NI-VISA and Keysight IO Libraries VISA.NET providers, and how are side-by-side installs resolved? | `REQ-VISA-002` | Two test machines. Cheap to settle, expensive to discover late. |
| **Q14** | Default colour values, graticule division count, and typefaces — the items in §13.11. | §13 visual fidelity | Only obtainable from screenshots; see §13.11. Low risk, since all are configurable. |

---

## 21. Bibliography

Sources consulted in preparing this specification. Items marked ★ are the most load-bearing.

**Product architecture and connectivity**

- ★ [Basic Vector Signal Analysis and Hardware Connectivity — 89600 VSA Software Option 200, Technical Overview (5990-6405)](https://www.keysight.com/us/en/assets/7018-02679/technical-overviews/5990-6405.pdf)
- ★ [89600 VSA Measurement Platforms (89600B online help)](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/products/content/prod_meas_platforms.htm)
- [Hardware Measurement Platforms for the Agilent 89600 Series — Data Sheet, archived (5989-1753)](https://www.keysight.com/us/en/assets/7018-08722/data-sheets-archived/5989-1753.pdf)
- [89600 VSA Software Multi-Vendor Hardware Connectivity (flyer)](https://www.keysight.com/us/en/assets/3121-1442/flyers/89600-VSA-Software-Multi-Vendor-Hardware-Connectivity.pdf)
- ★ [Agilent E4406A Vector Signal Analyzer Performance Guide Using 89601A (5988-2906EN)](https://hpwiki.mcguirescientificservices.com/_media/application_notes:5988-2906en.pdf)
- ["Vector Signal Analysis – 20 years on!", Tim Masson, Agilent (ARMMS 2012)](https://www.armms.org/media/uploads/15_armms_nov12_tmasson.pdf)
- [Agilent 89600 Vector Signal Analysis Software — Technical Overview](https://www.transcat.com/media/pdf/Agilent89600.pdf)
- [Direct Data Connectivity to the 89600 VSA, Option 89601101C — Technical Overview](https://www.keysight.com/us/en/assets/3121-1455/technical-overviews/Direct-Data-Connectivity-to-the-89600-VSA-89601101C.pdf)

**Acquisition, time/frequency parameters, triggering**

- ★ [Understanding Time and Frequency Parameters (89600B help)](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/understandingtimeandfreqparameters.htm)
- [Main Time Length (89600B help)](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/meassetup_time_maintimelength.htm)
- [Overlap Processing (89600B help)](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/meassetup_time_maxoverlap.htm)
- [Span (89600B help)](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/meassetup_frequency_span.htm)
- [Input > Trigger menu (89600B help)](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/mnu_input_trigger.htm)
- [Trigger Holdoff (89600B help)](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/input_trigger_holdoff.htm)

**Core analysis, windows, averaging, markers, limits**

- ★ [Window Types (89600B help)](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/meassetup_resbw_windowtypes.htm)
- [Windowing Frequency Response (89600B help)](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/windows_windowing_freq_response.htm)
- [ResBW Tab (89600B help)](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/dlg_meassetup_resbw_tab.htm)
- [Selecting Trace Data (89600B help)](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/tracedata_to_select_trace_data.htm)
- [Selecting an Averaging Type (89600B help)](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/average_sel_an_avg_type.htm)
- [About Markers (89600B help)](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/mkrs_about_markers.htm)
- [Band Power Marker Type (89600B help)](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/mkrs_calc_bandpowertype.htm)
- [About Limit Tests (89600B help)](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/mnu_utilities_limittests.htm)
- [PathWave Vector Signal Analysis (89600 VSA) Datasheet](https://assets-us-01.kc-usercontent.com/ecb176a6-5a2e-0000-8943-84491e5fc8d1/34d8430a-b780-498f-8c00-9a8bcc98de4b/KT-89601B_VSA_Datasheet.pdf)

**Digital demodulation**

- ★ [Digital Modulation Analysis, 89600 VSA Option 89601AYAC — Technical Overview (5992-4228)](https://www.keysight.com/zz/en/assets/7018-06908/technical-overviews/5992-4228.pdf)
- [Vector Modulation Analysis, 89600 VSA Option AYA — Technical Overview (5990-6387)](https://www.keysight.com/us/en/assets/7018-02672/technical-overviews/5990-6387.pdf)
- ★ [Vector Signal Analysis Basics — Application Note 150-15 (5989-1121EN)](https://www.keysight.com/us/en/assets/7018-01220/application-notes/5989-1121.pdf)
- ★ [Making and Interpreting EVM Measurements (5989-3144)](https://www.keysight.com/us/en/assets/7018-01305/application-notes/5989-3144.pdf)
- [Setting up a Digital Demodulation Measurement (89600B help)](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/digdemod/content/digdemod_setting_up_a_meas.htm)
- [Symbol Rate](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/digdemod/content/dlg_digdemod_fmt_symrate.htm) · [Result Length](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/digdemod/content/dlg_digdemod_fmt_resultlgth.htm) · [Sync Search](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/digdemod/content/dlg_digdemod_srch_syncsrch.htm) · [Pulse Search](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/digdemod/content/dlg_digdemod_srch_pulsesrch.htm)
- [Root Raised Cosine Filter](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/digdemod/content/dlg_digdemod_fltr_rootraisedcosine.htm) · [Equalization Filter](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/digdemod/content/dlg_digdemod_comp_equalfilter.htm)
- Error metrics: [EVM](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/digdemod/content/digdemod_symtblerrdata_evm.htm) · [Mag Err](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/digdemod/content/digdemod_symtblerrdata_magerr.htm) · [Freq Err](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/digdemod/content/digdemod_symtblerrdata_freqerr.htm) · [IQ Offset](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/digdemod/content/digdemod_symtblerrdata_iqoffset.htm) · [Rho](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/digdemod/content/digdemod_symtblerrdata_rho.htm) · [SNR](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/digdemod/content/digdemod_symtblerrdata_snr.htm) · [Error Summary overview](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/digdemod/content/digdemod_symtblerrdata.htm)
- [IQ Gain Imbalance and Quadrature Skew Interaction](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/digdemod/content/digdemod_para_interact_iqgainimb_quadskewerr.htm)
- [Eye Diagrams (I-Eye / Q-Eye)](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/dlg_trfmt_eye_digdemod.htm)
- [PathWave VSA Digital Demodulation Analysis — Configuration Guide (89601AYAC)](https://assets-us-01.kc-usercontent.com/ecb176a6-5a2e-0000-8943-84491e5fc8d1/4a5b4cc1-db81-496d-b0fe-483e5744c7b4/89601AYAC%20PathWave%20VSA%20Digital%20Demodulation%20Analysis%20Config%20guide.pdf)

**UI, state, formats, automation**

- [Trace Window (89600B help)](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gettingstarted/content/vsa_trace_window.htm) · [Window Menu](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/mnu_window.htm) · [Block Diagram](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/mnu_window_blockdiagram.htm)
- [Standard Features and Measurement Capabilities](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/std_featandmeas.htm)
- ★ [Saving and Recalling Instrument Setups](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/mnu_file_save_savesetup.htm)
- ★ [Supported File Formats](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/sharing/content/supportedfilefmts.htm) · [Saving and Recalling Recordings](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/sharing/content/saving_and_recalling_recordings.htm) · [SDF (Fast) format](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/sharing/content/sdf_file_format.htm)
- [About Programming the 89600 VSA](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/program/content/about_programming_the_89600b_vsa.htm) · [MATLAB and the 89600 VSA API](https://helpfiles.keysight.com/csg/89600B/WebHelp-apiref/DotNetApi-MatlabNotes.html)
- [89600 VSA Software — Installation Guide (9018-03424)](https://www.keysight.com/us/en/assets/9018-03424/installation-guides/9018-03424.pdf) · [Quick Start Guide (9018-03882)](https://www.keysight.com/us/en/assets/9018-03882/quick-start-guides/9018-03882.pdf) · [Configuration Guide (5990-6386)](https://www.keysight.com/us/en/assets/7018-02671/configuration-guides/5990-6386.pdf)
- [Keysight Vector Signal Analysis (89600 VSA) — Brochure (5990-6553)](https://www.keysight.com/us/en/assets/7018-02714/brochures/5990-6553.pdf)
- [89600 VSA Software product page](https://www.keysight.com/us/en/products/software/pathwave-test-software/89600-vsa-software.html)

**User interface appearance (§13)**

- ★ [Docking Window Manager](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gettingstarted/content/docking_window_manager.htm) · [Types of Windows](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gettingstarted/content/docking_types_of_windows.htm) · [Trace Window](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gettingstarted/content/vsa_trace_window.htm) · [Trace Layout](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gettingstarted/content/mnu_display_layout.htm) · [Active Trace](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gettingstarted/content/mnu_display_activetrace.htm)
- ★ [Display Hot Spots](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gettingstarted/content/display_hotspots.htm) — the in-place editing model of `REQ-UI-042`
- [Keyboard Shortcuts](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gettingstarted/content/keyboard_shortcuts.htm)
- ★ [DisplayColor enumeration (.NET API reference)](https://helpfiles.keysight.com/csg/89600B/WebHelp-apiref/Agilent.SA.Vsa.Interfaces~Agilent.SA.Vsa.DisplayColor.html) — the three-zone plot model and the full colourable-element list
- [Display members (.NET API reference)](https://helpfiles.keysight.com/csg/89600B/WebHelp-apiref/Agilent.SA.Vsa.Interfaces~Agilent.SA.Vsa.Display_members.html) · [Display.Theme](https://helpfiles.keysight.com/csg/89600B/WebHelp-apiref/Agilent.SA.Vsa.Interfaces~Agilent.SA.Vsa.Display~Theme.html) · [WindowScaleFactor](https://helpfiles.keysight.com/csg/89600B/WebHelp-apiref/Agilent.SA.Vsa.Interfaces~Agilent.SA.Vsa.Display~WindowScaleFactor.html)
- Display Preferences tabs: [Trace](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/dlg_dispappear_trace_tab.htm) · [Colour](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/dlg_dispappear_color_tab.htm) · [User Map Colour](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/dlg_dispappear_usermapcolor_tab.htm) · [Font](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/dlg_dispappear_font_tab.htm) · [Window](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/dlg_dispappear_window_tab.htm)
- [Spectrogram Map Colour](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/trace_spectrogram_mapcolor.htm) — the only documented default colours
- [Y Per Division](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/trace_yscale_yperdivision.htm) · [Y Reference Position](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/trace_yscale_referenceposition.htm) · [X Reference Position](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/trace_xscale_refpos.htm)
- Toolbars: [Control](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/toolbar_control.htm) · [Marker Tools](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/toolbar_markertools.htm) · [Record](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/toolbar_record.htm) · [Trace](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/toolbar_trace.htm) · [Spectrogram](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/toolbar_spectrogram.htm) · [Toolbar customisation](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/dlg_utilities_toolbars.htm)
- Menus: [Acquisition (was Input)](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/mnu_input.htm) · [Analysis (was MeasSetup)](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/mnu_meassetup.htm) · [Trace](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/mnu_trace.htm) · [Utilities](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/mnu_utilities.htm) · [Print](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/mnu_file_print.htm)
- [Marker Readouts](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/mkrs_marker_readouts.htm) · [Marker Type / delta notation](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/markers_position_markertype.htm)
- [Constellation trace format](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/gui/content/dlg_trfmt_constellation_digdemod.htm) · [About the Symbol Table](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/digdemod/content/digdemod_about_the_sym_tbl.htm) · [LTE Error Summary](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/lte/content/trc_error_summary.htm)
- [Error and Status Messages](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/troubleshooting/content/error_and_status_messages.htm) · [Measurement Status Messages](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/troubleshooting/content/error_measstatusmsg.htm) · [Trace Indicators](https://helpfiles.keysight.com/csg/89600B/Webhelp/Subsystems/troubleshooting/content/error_trace_indicator.htm)
- [PathWave VSA Basic Vector Signal Analysis Technical Overview (5992-4210, mirror)](https://maybo.az/wp-content/uploads/2022/10/5992-4210.pdf) — the labelled hot-spot callout diagram
- [89601B brochure](https://www.transcat.com/media/pdf/89601B.pdf) · [89600 VSA brochure (5990-6553)](https://docs.ampnuts.ru/eevblog.docs/HP_Agilent_Keysight/5990-6553EN%2089600%20VSA%20Software%20-%20Brochure%20c20141009%20%5B9%5D.pdf)
- [89600 VSA lab tutorial (Universidad del País Vasco)](https://aholab.ehu.eus/users/inma/labpsc/tutorialVSA.pdf) — Agilent-era menu paths
- [89600 VSA release notes archive](https://helpfiles.keysight.com/csg/89600B_ReleaseNotes/Content/VSA%20Previous.htm)

**89400-series ancestor (§13.10)**

- [89410A Getting Started Guide (9018-40438)](https://www.keysight.com/us/en/assets/9018-40438/installation-guides/9018-40438.pdf) · [89441A Getting Started Guide (9018-40543)](https://www.keysight.com/us/en/assets/9018-40543/installation-guides/9018-40543.pdf) · [89400 Operator's Guide (9018-40436)](https://www.keysight.com/ug/en/assets/9018-40436/user-manuals/9018-40436.pdf)
- [89400-90038 Operator's Guide (mirror)](https://www.testunlimited.com/pdf/an/89400-90038.pdf) · [89410A technical data](https://assets.testequity.com/te1/Documents/pdf/89410a.pdf) · [89441A data](https://www.rlscientific.com/documenti/HP89441A.pdf) · [89441A datasheet](https://www.testequipmenthq.com/datasheets/Agilent-89441A-Datasheet.pdf)
- [Product Note 89400-8](https://hpwiki.mcguirescientificservices.com/_media/application_notes:pn-89400-8.pdf) · [Product Note 89400-14](https://hpwiki.mcguirescientificservices.com/_media/application_notes:pn-89400-14.pdf) — real on-screen error-summary and symbol-table text
- [89410A GPIB Command Reference](http://www.ece.uprm.edu/~etclab/resources/equipment/agilent89410a/agilent89410a_gpibreference.pdf) · [HP Journal, December 1993](https://archive.org/stream/hp_journal_1993-12/hp_journal_1993-12_djvu.txt)

**E4406A**

- [E4406A VSA Transmitter Tester, 7 MHz to 4 GHz — product page](https://www.keysight.com/us/en/product/E4406A/vsa-transmitter-tester-7-mhz-to-4-ghz.html)
- [Agilent/HP E4406A Datasheet](https://www.testequipmenthq.com/datasheets/Agilent-E4406A-Datasheet.pdf)
- [E4406A option-code listing](https://4gte.com/products/hp-agilent-e4406a-202-252-bac-bah-vector-signal-analyzer/)
- [E4406A VSA Series User's Guide](https://xdevs.com/doc/HP_Agilent_Keysight/HP%20E4406A%20VSA%20User.pdf)
- [E4406A VSA Firmware History (9018-06887)](https://www.keysight.com/gt/en/assets/9018-06887/release-notes/9018-06887.pdf)

---

*End of specification. Revision 1.0.*
