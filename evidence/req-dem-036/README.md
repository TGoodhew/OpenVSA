# REQ-DEM-036 — carrier lock tolerance and diagnostics

Two measurements were needed to build the diagnostic, and both changed what it says.

## 1. The tolerance applies to what step 3 leaves, not to where the signal arrived

`REQ-DEM-036` states that the centre frequency "must be within roughly ±10 % of the symbol rate of
the true carrier for lock". Read as a statement about the **acquisition**, that is not true of this
chain, and a diagnostic built on it would accuse the centre frequency of failures it had nothing to
do with.

Step 3 estimates the carrier offset over the whole block and derotates the search window in place
before anything else runs. Measured, in a 4 MHz analysis span at a 1 MHz symbol rate:

| Offset on arrival | What step 3 left | EVM | Locked |
|---|---|---|---|
| 0 kHz | −13.7 kHz | 0.029 %rms | yes |
| 400 kHz — four times the tolerance | −13.7 kHz | 0.029 %rms | yes |
| 1200 kHz | 986 kHz | 70.97 %rms | no |
| 1500 kHz | 1986 kHz | 64.26 %rms | no |

The 400 kHz row is the point. Forty per cent of the symbol rate off centre is four times the
requirement's figure and it demodulates to the same EVM as a signal with no offset at all, because
step 3 removed it.

What breaks at 1200 kHz is not the size of the offset as such but that it is beyond the range
raising the signal to its rotational symmetry can distinguish — a quarter of the sample rate for a
four-fold symmetry, 1 MHz here. Past that the estimate aliases and step 3 subtracts the wrong
amount; the tolerance then bites on the ~1 MHz it left.

So `LockReport` reports both quantities and applies the tolerance to `ResidualOffsetHz`. The two
tests `ACentreFrequencyBeyondTheCoarseSearchIsNamedAsTheCentreFrequency` and
`ASignalOffCentreWithinTheCoarseSearchIsNotBlamedOnItsCentreFrequency` are the pair.

## 2. A short Result Length does not break lock in this chain

The requirement's fourth cause is "Result Length too short for the format". It could not be injected
on its own, because it does not produce a failure to lock. `result-length-sweep.txt` has the sweep:
four formats, Result Lengths from the format's recommendation down to **four symbols** — the
shortest the settings allow, and a sixty-fourth of the recommendation for 256-QAM — at
signal-to-noise ratios from noise-free to 15 dB. Every one of the 56 combinations locked, and none
was worse than 13 %rms.

That is the block estimation of `REQ-DEM-002` behaving as designed: one solution fitted across the
whole window, no loop to settle, and very few symbols needed to fit four parameters.

The recommendation in `Constellation.RecommendedResultLengthSymbols` therefore guards something
else — a window shorter than the constellation has points cannot *visit* most of them, so the
measurement is unrepresentative rather than wrong. The diagnosis reports the short window as a
contributing cause on a demodulation that has failed, and never as the sole cause of one. See #429.

## Reproducing

```
dotnet test tests/OpenVSA.Demod.Tests/OpenVSA.Demod.Tests.csproj \
    --filter "FullyQualifiedName~LockDiagnosticTests" -l "console;verbosity=detailed"
```
