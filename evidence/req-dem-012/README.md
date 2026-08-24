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

## 🔴 CORRECTED 24 August 2026 — the 50.10 % figures below were an artefact

The differential cases are reported here as scoring **50.10 %**, "exactly chance". That number came
from the harness, not from the signal: `BitStreamAlignment.Find` was asked to search one rotation for
a differential stream by passing a *state count* of one, which also collapsed every symbol to zero in
the bit extraction, and a stream of zeroes scores about half against anything. The tell is visible in
the tables below — **"best 50.10 % against a typical 50.10 %"**, the best and the typical being equal
because every candidate reading was the same one.

Corrected, those cases read **74.95 %** and **66.60 %**: the Gray-versus-natural mismatch, which for
two bits is 75 % and for three is 66.7 %.

**The conclusions on this page stand**, because none of them rested on that number — they came from
the relabelling analyser, which does not go through the broken path, and they were confirmed a second
way once `REQ-DEM-011` could apply a Gray labelling: the same signals then returned 1022 and 1533
bits of 1022 and 1533. See `../req-dem-011/README.md`.

## Against the bench, 24 August 2026

`OpenVSA.Verify --demod-check`, E4438C into E4406A at 500 ksym/s, root raised cosine α = 0.35, PN9
generated on this side from ITU-T O.150's polynomial so the instrument supplies no part of the
reference. Eight cases, all as expected; the full run is in
`demod-check-offset-differential.txt`.

| Case | EVM (%rms) | Bits against PN9 | Relabelling |
|---|---|---|---|
| QPSK | 0.78 | **1024 of 1024** | — |
| QPSK, spectrum inverted | 0.88 | 1024 of 1024, inverted | — |
| GRAYQPSK demodulated as QPSK | 0.81 | no reading; best 75.10 % | one accounts for 512 of 512 |
| OQPSK, acquisition 1 | 0.96 | no reading; best 75.10 % | **none accounts for it** |
| OQPSK, acquisition 2 | 1.05 | **1024 of 1024** | — |
| π/4-DQPSK, differential | 0.87 | no reading; best 50.10 % | **Gray, 511 of 511** |
| π/4-DQPSK, reference None | 1.03 | no reading; best 76.56 % | none accounts for it |
| D8PSK, differential | 0.91 | no reading; best 50.10 % | **Gray, 511 of 511** |
| D8PSK, reference None | 0.98 | no reading; best 69.27 % | none accounts for it |

### The offset half is proved by the second OQPSK acquisition

**1024 of 1024 bits**, against a typical reading of 50.00 %, at 1.05 %rms through both instruments —
the same region QPSK reads through the same chain (0.78–0.88). The stagger, its direction and the
bit pairing are all the transmitter's.

The first acquisition is the ambiguity, live: 75.10 %, no match. Which pairing a capture lands on
depends on where it started relative to the transmitter's symbol clock, so it is a coin toss per
acquisition — which is why the case takes four and passes on the first that matches. **If the mapping
were wrong, none of them would ever match.**

### 75.10 % means two entirely different things, and the bits cannot tell them apart

A mis-paired OQPSK reading scores **75.10 %** — the *same* number a Gray-labelled QPSK gives, and the
same number the GRAYQPSK case has scored since 24 August. That cost an hour: the four mis-paired
acquisitions of an earlier run were read as evidence that this instrument's OQPSK was Gray labelled,
and the conclusion was written down before a fifth acquisition matched 1024 of 1024 and refuted it.

The arithmetic behind the coincidence: an offset format's alternate pairing is `(Q of symbol k, −I of
symbol k+1)`, which on a serial bit stream is the sequence shifted by one bit with alternate bits
inverted. Half the bits are then the sequence's and half are a coin toss — exactly 75 %.

**What tells them apart is the relabelling line, and that is why it exists.** A Gray labelling is a
bijection on symbol values, so one relabelling accounts for every symbol; a mis-pairing is not a
relabelling of anything, and none does. Measured: GRAYQPSK 512 of 512 explained, mis-paired OQPSK
best 50.20 % and refused.

### The differential half is proved, and the convention is not ours

The bits miss and a **Gray relabelling accounts for 511 of 511 symbols** of both π/4-DQPSK and D8PSK.
D8PSK's is `0, 1, 3, 2, 6, 7, 5, 4` — the Gray code exactly, unrotated. So:

- **The E4438C's Custom `P4DQPSK` and `D8PSK` are symbol-differentially encoded.** Read absolutely,
  nothing accounts for them under any labelling (53.13 % and 32.42 %). The manual gave the opposite
  impression — those softkeys "load an I/Q map", and differential encoding is documented as a
  separate feature — and the measurement settles it.
- **OpenVSA's differential decoding is right**: the direction of the difference, the reference, and
  π/4-DQPSK's de-rotation all recover the transmitted sequence, symbol for symbol.
- **The instrument Gray-labels its phase changes and OpenVSA labels them naturally.** That is a
  convention, it is `REQ-DEM-011`'s to offer, and it is not a defect in this requirement.

The pair of cases per format is `REQ-DEM-012`'s own criterion on hardware: the same waveform read
with the reference the format asks for and with it forced to None. The wrong one does not degrade
the answer, it destroys it — while converging and reporting a perfectly respectable 1.03 %rms.
