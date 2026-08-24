# REQ-DEM-012 — differential and offset handling

What was measured while building it, and the one finding that changed a step of the chain other
than the two this requirement owns.

## Why the coarse carrier estimator had to change

`CoarseCarrierStep` strips the modulation by raising the signal to the power of the constellation's
rotational symmetry, which leaves the carrier's line at that multiple of the offset. π/4-DQPSK is
four points and eight positions, so that power is eight — and the first demodulation of one reported
**125 362 Hz of carrier offset on a 1 Msym/s signal that had none**, and demodulated it at **46.9 %
EVM**. 125 kHz is Rs/8: the estimator had found a line at the symbol rate and divided it by eight.

`carrier-line-probe.py` settles what that line is, in numpy, with no OpenVSA code involved — so that
the answer is about the signals rather than about this implementation. It builds each format from
its own definition (root raised cosine, α = 0.35, span 20, 16 samples a symbol, 4 000 symbols),
raises it to the power the chain would use, and lists the tallest lines of the result as fractions of
the tallest. `carrier-line-probe.txt` is its output:

```
8PSK         : peak at +0.0000 Rs;  tops  +0.000Rs:1.00, -1.000Rs:0.57, +1.000Rs:0.56
PI4DQPSK     : peak at +1.0000 Rs;  tops  +1.000Rs:1.00, -1.000Rs:0.98, +0.000Rs:0.57
PI4 derotated: peak at +0.0000 Rs;  tops  +0.000Rs:1.00, +1.000Rs:0.37, -1.000Rs:0.35
QPSK         : peak at +0.0000 Rs;  tops  +0.000Rs:1.00, +1.000Rs:0.37, -1.000Rs:0.35
```

Raising a signal to a power raises its **envelope** to that power too, and a pulse-shaped envelope is
periodic at the symbol rate — so every one of these has lines at ±Rs that move with nothing. For
8PSK the carrier's line beats them 1.00 to 0.57. **For π/4-DQPSK they beat the carrier's line, 1.00
and 0.98 to 0.57**, because alternating between two QPSK sets makes that format's envelope far more
strongly periodic than an eight-point ring's. Nothing about it is a defect in the estimator: the
tallest line in that spectrum genuinely is not the carrier.

The fix is to exclude those lines by name rather than to out-compete them. The symbol rate is
supplied exactly (`REQ-DEM-030`), the envelope's contributions are impulses at its multiples, and the
transform's own resolution says how wide an impulse can be, so the peak search skips three
resolutions either side of each multiple. What that costs is an offset that lands the raised line
exactly on one of them — Rs/order — which no estimator could have told from the envelope anyway.

The fourth row is the control: de-rotated first and raised to the fourth power instead, π/4-DQPSK
looks exactly like QPSK. That is the arithmetic saying the alternation, and not the format's
constellation, is what moved the peak.

## What the chain then measured

From `DifferentialAndOffsetTests`, 24 August 2026, generated at 16 samples a symbol with a transmit
pulse spanning 20 symbols and demodulated with a matched one:

| Case | EVM (%rms) | Carrier error |
|---|---|---|
| OQPSK, read at two instants a symbol | 0.0151 | — |
| OQPSK, read at one instant a symbol | **38.32**, converged | — |
| OQPSK at 2 / 4 / 8 points per symbol | 0.0159 / 0.0151 / 0.0151 | — |
| DQPSK | 0.0151 | −0.001 Hz |
| D8PSK | 0.0142 | −0.001 Hz |
| π/4-DQPSK | 0.0137 | 0.000 Hz |

The second row is the requirement's own warning measured: reading an offset signal at one instant a
symbol does not fail, it converges and reports 38 %. The last row is the estimator fix — 0.000 Hz
against the 125 000 Hz that the rotation looks like if it is fitted as frequency.

## The ambiguity, which is not a defect

An OQPSK signal read half a symbol late and turned by 90° gives the pair `(Q of symbol k, −I of
symbol k+1)`. Every one of those is an exact constellation point, so that reading has the same
near-zero EVM and different bits. Measured: the same record demodulated aligned and half a symbol
late reads **0.015124** and **0.015116 %rms**, and recovers the transmitted symbols under one
pairing and the shifted pairing under the other.

So an EVM that comes back near zero is **not** evidence that the bits are paired the way the
transmitter paired them. Only the sync-pattern search of `REQ-DEM-040` settles that. This is the
same shape of blind spot as the bit check's inability to see a mirrored spectrum, recorded in
`../req-e44-007/README.md`.

The chain prefers the alignment nearest the one step 7 nominated, and by a factor of two rather than
a rounding error. That buys repeatability — the same signal measured twice gives the same
constellation rather than one turned by a quarter — and it resolves nothing.
