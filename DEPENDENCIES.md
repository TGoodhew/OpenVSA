# Dependencies

Required by `REQ-NFR-008`: every third-party dependency is recorded here with its licence and
the justification for its use.

**Policy.** Copyleft dependencies (GPL, or LGPL linked statically) must not be introduced into
shipped binaries without written approval. Specifically flagged as traps: **FFTW** (GPL unless
a commercial licence is purchased), GPL HDF5 tooling, and MATLAB-file libraries with
restrictive terms.

## Current dependencies

| Package | Version | Licence | Justification |
|---|---|---|---|
| `System.Memory` | 4.6.0 | MIT | `Span<T>`/`Memory<T>` for API shape. Note this is the **portable ("slow") span** on .NET Framework — no JIT intrinsic, no bounds-check elision — so hot loops use raw arrays instead (`REQ-NFR-003`). |
| `System.Numerics.Vectors` | 4.6.0 | MIT | `Vector<float>` for `REQ-NFR-003`'s kernels. Without it the type resolves to the non-accelerated fallback in mscorlib and every measurement would compare scalar code against scalar code wearing a vector's name. Measured 8 lanes on the reference machine. |
| `xunit` | 2.9.2 | Apache-2.0 | Test framework. Test-only, not shipped. |
| `xunit.runner.visualstudio` | 2.8.2 | Apache-2.0 | Test runner. Test-only, not shipped. |
| `Microsoft.NET.Test.Sdk` | 17.11.1 | MIT | Test host. Test-only, not shipped. |
| `BenchmarkDotNet` | 0.15.8 | MIT | The performance gates of `REQ-NFR-020`–`026`. Test-only, not shipped. |
| `WixToolset.UI.wixext` | 5.0.2 | MS-RL | The installer's `WixUI_InstallDir` dialogue set. **Build-time only, not shipped** — it authors the MSI, nothing from it is installed. The MSI bundles the shell, the analysis assemblies, the native FFT, the front-end plug-ins and the Syncfusion runtime; it deliberately carries no NI-VISA component (pre-installed dependency, GAC-resident) and no licence key of any kind. |
| `Syncfusion.Licensing` | 34.1.32 | Syncfusion Community (free, royalty-free) | Registers the Syncfusion key so the controls below do not render a trial banner. Pulled in transitively by the three packages that follow, and referenced explicitly because `App` calls `SyncfusionLicenseProvider.RegisterLicense` directly. |
| `Syncfusion.Tools.WPF` | 34.1.32 | Syncfusion Community (free, royalty-free) | `DockingManager` and Ribbon for the shell furniture of `REQ-UI-001`/`REQ-UI-060`. See the decision below. |
| `Syncfusion.SfGrid.WPF` | 34.1.32 | Syncfusion Community (free, royalty-free) | Marker, limit-line and demod error-summary tables. |
| `Syncfusion.Shared.WPF` | 34.1.32 | Syncfusion Community (free, royalty-free) | Numeric editors, used as the entry control inside the `REQ-UI-042` hot-spot framework. |
| `IviFoundation.Visa` | 8.0.2 | IVI Foundation (permissive; redistribution of the shared components permitted) | `REQ-VISA-001`: `Ivi.Visa.dll` alone, referenced by `OpenVSA.Hal.Visa`. See the note below on why nothing is shipped. |
| `Newtonsoft.Json` | 13.0.3 | MIT | `REQ-STA-003`'s state format. See the note below. |

### Note: why a JSON library rather than the in-box serialiser

`REQ-STA-003` asks for three things at once: human-readable and diffable output, a schema version,
and **unknown fields preserved byte-for-byte on round-trip**. The last is what settles it. Without
it an older build is a one-way door — opening a colleague's setup silently discards everything it
does not understand, and the loss surfaces later, on their machine.

`DataContractJsonSerializer` is in the box but writes unindented output, and `IExtensibleDataObject`
preserves unknown members only for the data-contract shapes it controls. Hand-rolling a parser that
gets string escapes, surrogate pairs and number formats right is a large amount of code with real
risk and no requirement asking for it. `JObject` models exactly what the requirement describes: a
document whose unrecognised members are still there after a load and a save, at any depth.

Referenced by `OpenVSA.Measurement` only. Nothing below L4 knows the state is JSON, and the state
model itself carries no attribute from the library — the preserved members travel as text.

### Note: the VISA package ships nothing

For .NET Framework the package supplies a **reference assembly only** (`ref/net40`); the
implementation is installed into the GAC by the VISA.NET Shared Components that come with NI-VISA
and Keysight IO Libraries alike. Nothing is copied into the output, and no VISA assembly is
redistributed with OpenVSA.

That is also what makes `REQ-NFR-032` work without special handling. On a machine with no VISA
installed, the GAC has no `Ivi.Visa`, so `OpenVSA.Hal.Visa` fails to load, and `FrontEndRegistry`
lists it among the unavailable sources with the reason — the application still starts and the
simulated source still works.

### Contributor step: the Syncfusion licence key

Syncfusion controls require a key registered at start-up or they display an unlicensed banner.
The key is a **per-developer credential and is never committed** — this repository is public, so
a key in the tree is a leaked credential regardless of what it costs.

`SyncfusionLicense.ResolveKey()` checks, in order:

1. the `SYNCFUSION_LICENSE_KEY` environment variable;
2. `appSettings["SyncfusionLicenseKey"]`, which `App.config` merges in from
   `local.secrets.config` via its `file` attribute. That file is git-ignored; copy
   `local.secrets.config.example` to create one. The `file` attribute ignores a missing file
   silently, which is the normal state of a fresh clone.

Registration happens in the **`App` constructor, not `OnStartup`** — and there is a comment
saying so, because it looks like the wrong place. The generated entry point runs `new App()`
before `app.InitializeComponent()`, and `InitializeComponent` is what loads `App.xaml` and its
merged resource dictionaries; `OnStartup` runs later still, during `Run()`. If a Syncfusion theme
dictionary is ever merged into `App.xaml`, registering in `OnStartup` would come after those
controls were constructed — producing a banner despite a valid key, with the cause some distance
from the symptom.

**A missing key is not fatal.** Registration is skipped and the application launches in trial
mode. A contributor who has not yet obtained a free Community key still gets a working build.
This matters because OpenVSA ships as one free edition with everything included: the key is a
build-time step for contributors, never a gate on anything a user receives, and redistribution
is royalty-free.

## Planned, and the licensing decision attached to each

| Component | Decision |
|---|---|
| **FFT provider** (`REQ-NFR-004`) | Sits behind `IFftProvider` so the choice is deployment-time, not design-time. The shipped default must carry **no copyleft obligation** — a managed implementation (Stockham or split-radix, or Math.NET Numerics under MIT). Intel oneMKL/IPP are viable native options, being free to use and redistribute under the Intel Simplified Software Licence since the oneAPI transition. **FFTW must not be linked** without a purchased commercial licence. |
| **VISA** (`REQ-VISA-001`) | Reference `Ivi.Visa.dll` **alone** — the IVI Foundation VISA.NET Shared Components, authored by the IVI Foundation and installed by NI-VISA and Keysight IO Libraries alike. Referencing `NationalInstruments.Visa.dll` or `Keysight.Visa.dll` is prohibited: it hard-binds the binary to one vendor and is the most common way vendor neutrality is lost in practice. |
| **HDF5 / MAT v7.3** (`REQ-REC-005`) | Licence must be checked before selection; some tooling is GPL. |
| **BenchmarkDotNet** | MIT. Test-only, for the performance regression gates of `REQ-NFR-020`–`026`. |
| **Docking window library** (`REQ-UI-001`) | **Settled: Syncfusion `DockingManager`.** Free and royalty-free under the Community licence, actively maintained, and covers ribbon, grids and editors from the same vendor. AvalonDock (MS-PL) was the alternative and is thinly maintained. |


## Decision: Syncfusion for the shell, our own rasteriser for trace geometry

Syncfusion is taken for the **generic shell furniture** — docking, grids, editors, ribbon — and
deliberately **not** for drawing traces.

The reasoning is not about the licence, which is free and royalty-free and imposes only the
contributor step above. It is that a chart control has very little left to contribute once two
requirements are honoured:

- **`REQ-NFR-006` requires min/max envelope decimation to be ours and verifiable.** Its criterion
  demands that a one-bin −60 dBc spur survive at its correct amplitude *and* that point-skipping
  demonstrably fail. A control's own "fast series" downsampling is typically undocumented and
  often point-skipping, so we would pre-decimate and then fight the control not to decimate again.
- **`REQ-UI-042`'s hot spots are not a charting feature.** Hover-underline, click-to-numeric-pad,
  wheel adjust and in-place editing across the annotation band have to be our own elements
  whichever control draws the trace. That requirement is explicit that retrofitting in-place
  editing later is the expensive path.

What is left for a chart control is axes, gridlines and zoom — against which the software
rasteriser already verifies `REQ-UI-010` by sampling rendered pixels **headlessly in CI**, which a
third-party control could not do without standing up a WPF render host in the test suite.

**The measurements support this.** One 2²⁰-point spectrum frame, measured stage by stage
(`OpenVSA.Benchmarks`, short job, x64 Release):

| Stage | 8 192 points | 2²⁰ points | Share of the 2²⁰ frame |
|---|---:|---:|---:|
| Window | 9.8 µs | 2.15 ms | 3.0 % |
| **FFT (double)** | 210 µs | **60.96 ms** | **84.4 %** |
| Magnitude → dB | 7.7 µs | 1.16 ms | 1.6 % |
| Decimate | 9.8 µs | 0.77 ms | 1.1 % |
| **Rasterise whole frame** | 924 µs | **1.00 ms** | **1.4 %** |
| **Whole frame** | **1.21 ms** | **72.2 ms** | |

`REQ-NFR-021` allows 100 ms and `REQ-NFR-020` allows 16.7 ms; both are met. Rendering is **1.4 %**
of the 2²⁰ frame and is essentially **constant** in point count (1.00 ms at 2²⁰ against 924 µs at
8 192) because decimation has already reduced the trace to one span per column. Choosing a faster
renderer would optimise 1.4 % of the budget; the transform is 84 %.
