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
| `xunit` | 2.9.2 | Apache-2.0 | Test framework. Test-only, not shipped. |
| `xunit.runner.visualstudio` | 2.8.2 | Apache-2.0 | Test runner. Test-only, not shipped. |
| `Microsoft.NET.Test.Sdk` | 17.11.1 | MIT | Test host. Test-only, not shipped. |

## Planned, and the licensing decision attached to each

| Component | Decision |
|---|---|
| **FFT provider** (`REQ-NFR-004`) | Sits behind `IFftProvider` so the choice is deployment-time, not design-time. The shipped default must carry **no copyleft obligation** — a managed implementation (Stockham or split-radix, or Math.NET Numerics under MIT). Intel oneMKL/IPP are viable native options, being free to use and redistribute under the Intel Simplified Software Licence since the oneAPI transition. **FFTW must not be linked** without a purchased commercial licence. |
| **VISA** (`REQ-VISA-001`) | Reference `Ivi.Visa.dll` **alone** — the IVI Foundation VISA.NET Shared Components, authored by the IVI Foundation and installed by NI-VISA and Keysight IO Libraries alike. Referencing `NationalInstruments.Visa.dll` or `Keysight.Visa.dll` is prohibited: it hard-binds the binary to one vendor and is the most common way vendor neutrality is lost in practice. |
| **HDF5 / MAT v7.3** (`REQ-REC-005`) | Licence must be checked before selection; some tooling is GPL. |
| **BenchmarkDotNet** | MIT. Test-only, for the performance regression gates of `REQ-NFR-020`–`026`. |
| **Docking window library** (`REQ-UI-001`) | Licence to be assessed. AvalonDock (MS-PL) is the leading candidate. |
