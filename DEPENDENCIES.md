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
| `WixToolset.UI.wixext` | 5.0.2 | MS-RL | The installer's `WixUI_InstallDir` dialogue set. **Build-time only, not shipped** — it authors the MSI, nothing from it is installed. The MSI bundles the shell, the analysis assemblies, the native FFT, the front-end plug-ins and the Syncfusion runtime; it deliberately carries no NI-VISA component (pre-installed dependency, GAC-resident) and no licence key **as a file** — `local.secrets.config` is excluded from the harvest by name, so a developer's own key cannot be swept up out of the payload directory. The shell binary it installs is a different matter: a release build compiles the Syncfusion key into `OpenVSA.exe`. See the contributor note below, which says why that is intended rather than an accident. |
| `WixToolset.Netfx.wixext` | 5.0.2 | MS-RL | The installer's `netfx:NativeImage` element, which schedules Ngen so the **first** launch does not jit the start-up path (`REQ-NFR-025`). **Build-time only, not shipped** — it authors the MSI's custom actions; nothing from the package is installed. NGen lived in `WixToolset.Util.wixext` in WiX v3 and moved here in v4. |
| `Syncfusion.Licensing` | 34.1.32 | Syncfusion Community (free, royalty-free) | Registers the Syncfusion key so the controls below do not render a trial banner. All six Syncfusion packages that follow depend on it, so it would arrive transitively in any case; it is referenced explicitly because `SyncfusionLicense.Register` calls `SyncfusionLicenseProvider.RegisterLicense` against it. That is the **only** call site in the tree, and an architecture test fails the build if a second one appears. |
| `Syncfusion.Tools.WPF` | 34.1.32 | Syncfusion Community (free, royalty-free) | `DockingManager` and Ribbon for the shell furniture of `REQ-UI-001`/`REQ-UI-060`. See the decision below. |
| `Syncfusion.SfGrid.WPF` | 34.1.32 | Syncfusion Community (free, royalty-free) | Marker, limit-line and demod error-summary tables. |
| `Syncfusion.Shared.WPF` | 34.1.32 | Syncfusion Community (free, royalty-free) | Numeric editors, used as the entry control inside the `REQ-UI-042` hot-spot framework. |
| `Syncfusion.SfSkinManager.WPF` | 34.1.32 | Syncfusion Community (free, royalty-free) | Applies a named skin to an element. `REQ-UI-083`: three of the seventeen chrome keys cannot reach the controls that need them, because `ListBoxItem`, `TabItem` and `MenuItem` hard-code their selection colours inside their own templates and only a full template reaches them. See the decision below. |
| `Syncfusion.Themes.FluentDark.WPF` | 34.1.32 | Syncfusion Community (free, royalty-free) | The templates the `Dark` chrome theme names. Carries full `ControlTemplate`s for the stock WPF controls as well as Syncfusion's own, which is what supplies the hover and pressed states a colour setter cannot reach. |
| `Syncfusion.Themes.FluentLight.WPF` | 34.1.32 | Syncfusion Community (free, royalty-free) | The same for the `Light` theme. Both ship, because a theme naming a skin that is not deployed falls back to stock templates rather than failing. |
| `IviFoundation.Visa` | 8.0.2 | IVI Foundation (permissive; redistribution of the shared components permitted) | `REQ-VISA-001`: `Ivi.Visa.dll` alone, referenced by `OpenVSA.Hal.Visa`. See the note below on why nothing is shipped. |
| `Newtonsoft.Json` | 13.0.3 | MIT | `REQ-STA-003`'s state format. See the note below. |

### Note: where these packages come from

Every package above is restored from **nuget.org and nothing else**. `NuGet.config` at the
repository root says so explicitly: it clears the package sources, the fallback folders and the
disabled-source list inherited from the machine, then names nuget.org alone.

That file is not housekeeping, and the reason it exists is specific to this table. Without it
NuGet composes its configuration from whatever is installed on the machine doing the build, and
**installing Syncfusion Essential Studio registers the licensed installer's own package folder as
a fallback for every project in the solution** — through a `.config` in
`C:\Program Files (x86)\NuGet\Config`, with nothing in the repository to say it happened. The
assemblies in that folder come from the licensed installer and are **pre-licensed**, so a build
resolving from it renders no trial banner whether or not a key was ever registered. The shell
would look right on a developer machine with Essential Studio installed and wrong everywhere
else, and the difference would not appear in any diff.

Nothing resolved from it in practice — it holds 34.1.29 and only the `syncfusion.ui.wpf.net`
meta-package, while this solution pins the individual packages at 34.1.32. The point is that the
gap was one version bump wide and turned on a file outside the checkout.

Measured rather than assumed, because clearing the machine's sources could have broken the build
instead of tightening it: three packages here had been coming from the Visual Studio offline
source rather than nuget.org (`System.Reflection.Emit`, `System.Reflection.Metadata`,
`System.Runtime.InteropServices.RuntimeInformation`). With `NuGet.config` in place the whole
solution restores into an **empty** packages folder — 80 package versions, every one recording
nuget.org as its source, no other source consulted — and the installer project does the same.

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
   silently, which is the normal state of a fresh clone;
3. a key **embedded at build time**, present only if the build asked for one.

**The third source is the one a release uses, and it puts the key inside the shipped binary.**
`/p:EmbedSyncfusionLicenseKey=true` — which `tools/build_installer.ps1 -EmbedLicenseKey` passes,
and which an ordinary developer build and CI never do — writes `SYNCFUSION_LICENSE_KEY` from the build
environment into a generated file under `obj/`. That file is git-ignored and never added to the
tree, so the key still does not reach source control; but it compiles to a `const string`, and
anyone holding the MSI can recover it from `OpenVSA.exe` with a regular expression. **This is
intended, not an oversight.** Syncfusion's distribution model is that a shipped application
carries a registered key, and it is what lets somebody install OpenVSA without a Syncfusion
account of their own and without a banner. The reasoning, and the one condition that would reopen
it, are recorded beside the MSBuild target in `OpenVSA.Ui.csproj`. The exposure the two guards
above exist to prevent is a key in **source**, which is a different and worse thing: it would be
copied by anyone reading the repository and reused for their own development.

The embedded key is deliberately tried **last**, so a developer's own key still wins on their own
machine even against a build that carries one.

A build that asks to embed and finds no key in its environment is a **warning, not an error**, so
a fork with no Syncfusion account can still produce an installer. It produces one that shows the
evaluation banner, and the warning says so — worth knowing before cutting a release, because the
two MSIs are indistinguishable until one is run.

Registration happens in the **`App` constructor, not `OnStartup`** — and there is a comment
saying so, because it looks like the wrong place. The generated entry point runs `new App()`
before `app.InitializeComponent()`, and `InitializeComponent` is what loads `App.xaml` and its
merged resource dictionaries; `OnStartup` runs later still, during `Run()`. If a Syncfusion theme
dictionary is ever merged into `App.xaml`, registering in `OnStartup` would come after those
controls were constructed — producing a banner despite a valid key, with the cause some distance
from the symptom.

**And not only in `App`.** Not every path that builds a Syncfusion control goes through `App`:
the test host starts its own STA thread and constructs a `ShellWindow` directly, and the soak
harness does the same. So `SyncfusionLicense.Register()` is called from each host that can be
first — `ShellWindow`, `ToolWindowHost`, `SourceControlWindow`, `ThemeCatalogue`, `DockingChrome`
— before the control is constructed. It is idempotent under a lock, so the ordinary path pays
nothing for the extra calls, and `NoUnregisteredSyncfusionHostsTests` fails the build when a file
in `src/` names a Syncfusion type without one. The rule is enforced rather than documented
because the failure it prevents already happened: an unlicensed control raises a **modal** trial
dialog as it is constructed, that dialog blocked a test dispatcher, and the snapshot soak
reported tearing 49 s later — a long way from the cause.

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
