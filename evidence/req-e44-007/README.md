# REQ-E44-007 stage 1 — the demodulator against a real generator

**Measured 2026-08-23.** E4406A `US40062429` firmware `A.08.10` on `GPIB0::17::INSTR`; E4438C
`MY45090927` firmware `C.05.85` on `TCPIP0::192.168.1.85::inst1::INSTR`.

Reproduce with `OpenVSA.Verify --demod-check`. Raw captures: `demod-check-run1.txt`,
`demod-check-run2.txt` (single-case runs, before the matrix was added) and
`demod-check-matrix.txt` (all three cases).

## What this proves that nothing else did

Every other check on the demodulator compares it with itself. EVM says the symbols landed near the
constellation. A round trip through OpenVSA's own generator says the two halves of OpenVSA agree.
Neither would notice a symbol-to-bit mapping that was consistently wrong, because both sides would
be wrong together.

This compares the recovered bits with **PN9 generated on this side from its polynomial**
(`OpenVSA.Synthesis.PnSequence`, ITU-T O.150's x⁹ + x⁵ + 1). The instrument supplies no part of the
reference. PN9 repeats only every 511 bits, so agreement over 1024 bits is not luck.

Four things are unknown when a demodulator first meets a real transmitter, none of them a defect:
which rotation of the constellation it locked to, whether the instrument sends the most or least
significant bit of a symbol first, whether the sequence or its complement is on air, and where in the
period the analysed block began. `BitStreamAlignment` searches all four and reports what it found
alongside **what a typical reading scored**, because a match of 100 % would mean nothing if
everything scored 98 %.

## The result

| case | rotation | bit order | offset | bits | EVM %rms | outcome |
|---|---|---|---|---|---|---|
| QPSK, run 1 | 0 | MSB first | 237 | 1024 of 1024 (100 %) | 0.8825 | match |
| QPSK, run 2 | 2 | MSB first | 44 | 1024 of 1024 (100 %) | — | match |
| QPSK, run 3 | 1 | MSB first | 184 | 1024 of 1024 (100 %) | 0.8478 | match |
| QPSK, matrix | 2 | MSB first | 178 | 1024 of 1024 (100 %) | 0.8899 | match |
| QPSK, spectrum inverted | 3 | MSB first, **inverted** | 178 | 1024 of 1024 (100 %) | 0.8205 | match |
| GRAYQPSK | — | — | — | best 75.10 % | 0.8296 | **no match, as expected** |

A typical reading scored 50.00 % in every case.

Four independent QPSK runs found four different rotations and four different start offsets and
matched every bit each time. That is the shape the method predicts: rotation and start phase are free
parameters, the agreement is not.

## The two cases that were expected not to pass, and what they showed

**A check that only ever passes proves nothing about what it would catch.** So the matrix includes
two cases whose outcome is informative either way.

**GRAYQPSK: best reading 75.10 %, no match.** This is the negative control, and the number is more
than a rejection. Gray and natural QPSK differ only in the low bit of symbols 2 and 3 — `10`/`11`
against `11`/`10` — so half the symbols carry one wrong bit out of two, and a natural-mapping decoder
should agree on exactly 75 % of the bits. The measurement is 75.10 %. That confirms quantitatively
both that **OpenVSA decodes with the natural mapping** and that the Gray transposition is the *only*
difference between the two formats on this instrument. No rotation undoes a transposition, which is
why the search cannot rescue it — and that is precisely the failure a wrong mapping would produce.

**Spectrum inverted: matched, with `inverted` set, at the same offset.** This was recorded as a
prediction before it was run, and the prediction was half wrong in a way worth keeping. The reasoning
was that the search's rotations and bit orders together span the reflections as well as the rotations,
so a mirrored signal should still match with the *bit order* flipped. It matched — but by flipping the
*bit inversion*, not the bit order, because complementing both bits of a two-bit symbol is itself a
reflection (s → 3−s) under the natural mapping. The mechanism was not the one predicted; the
consequence is, and it is the important half:

> **This check cannot detect a mirrored spectrum.** The degrees of freedom it searches over to be
> robust against a free rotation also absorb a conjugation. A mirror has to be detected some other
> way — `REQ-DEM-035`'s control exists for that — and a passing bit check must not be read as
> evidence that the spectral sense is right.

## Incidental measurements worth keeping

**The carrier error is real and repeatable: 49.9 to 50.6 Hz across five runs.** At 1 GHz that is
0.05 ppm between the two instruments' references, which are not locked to each other. It is a
frequency difference, not noise — noise would not repeat to a tenth of a hertz.

**EVM on a real signal, whole path: 0.82 to 0.89 %rms.** For comparison, the same chain measures
1.9e-6 %rms on a synthetic signal through the shell. The difference is the two instruments, the
cabling and a 6.7 MHz analysis bandwidth holding a 675 kHz signal.

**The instrument reported 15.0000 MS/s where the acquisition plan estimated 7.5000 MS/s, and
6.7 MHz of bandwidth where 5.0 MHz was asked for.** This is not a fault in the data path — the front
end labels its planning figure an estimate and every block carries the instrument's own answer, which
is why the demodulation worked at 30 samples a symbol. But it contradicts `REQ-E44-002b`, which gave
the maximum sample rate as 7.5 MHz. See `docs/INSTRUMENT-FIRMWARE-DEVIATIONS.md` deviation 7 for the
reconciliation; the short version is that the requirement read its maximum off the end of a table
that stopped at 1 MHz RBW, and the crossover to 15 MS/s lies somewhere between 1 MHz and 5 MHz
commanded, still unmeasured.

## What stage 1 does not cover

- **Only QPSK, and only at 500 ksym/s.** The format catalogue is `#125`; each format's mapping
  deserves the same treatment and only QPSK and GRAYQPSK have had it.
- **Only PN9.** The other patterns share the machinery but have not been run.
- **Nothing about the spectral sense**, for the reason given above.
- **No EDGE or cdmaOne**, which is stage 2 (`#346`, `#64`) and needs `#125` and `REQ-DEM-021`'s EDGE
  pulse first.

---

# The analyser's bandwidth-to-sample-rate law

**Measured 2026-08-24.** `OpenVSA.Verify --probe-bandwidth`; raw output in `bandwidth-law.txt`,
readings in `bandwidth-law.tsv`.

The demodulation check above turned up a 2× disagreement between the acquisition plan's estimated
sample rate and the instrument's own answer. Root-causing it exposed something worse than a wrong
constant: nobody had measured the *shape* of the relationship, so two successive models had been
fitted to a handful of points and both were wrong in ways that produced plausible numbers rather than
failures.

- A **linear interpolation** from zero to the rate at the widest bandwidth. Measured against the
  sweep it reports **×0.170** of the truth at 1.70 MHz commanded — a 5.9× under-estimate.
- A **fixed 1.5× of the bandwidth** (deviation 3 in `docs/INSTRUMENT-FIRMWARE-DEVIATIONS.md`). That
  was one real reading taken at the extreme top of the range, where the ratio genuinely is 1.5, read
  as though it held everywhere. Below the clamp the ratio is 4.83871.

## Method

Forty points, geometric, over the instrument's whole reported range of 10 Hz to 10 MHz. At each,
command the bandwidth and read back **both** the bandwidth actually in force and the sample period —
two coercions, not one, since the instrument first picks a filter it can afford and then picks a
decimation from that.

A ladder alone only *brackets* a step: between two rungs with different periods the boundary could be
anywhere. So every such gap was then **bisected**, to better than 130 Hz. That is what turns "somewhere
between 1 MHz and 5 MHz" into a number.

## Result

$$W_{actual} = \frac{W_1}{n}, \qquad F_s = \frac{F_s^{max}}{n}, \qquad
F_s = \frac{F_s^{max}}{W_1} W_{actual} = 4.83871\,W_{actual}$$

with $W_1 = 3.1$ MHz, $F_s^{max} = 15$ MHz, and $n$ the number of 1/15 MHz ticks in the sample period.

| what | result |
|---|---|
| Sample period a whole number of 1/15 MHz ticks | **yes, all 40 points**, $n$ from 1 to 308 805 |
| Distinct values of $F_s / W_{actual}$ over the sweep | **three**: 4.83871, 2.2388, 1.5 |
| $F_s = 4.83871 \times W_{actual}$ | **exact** wherever $W_{actual} \le 3.1$ MHz |
| Commanded bandwidth is rounded | **up**, to the next available step — so $n$ is a floor |
| 5 → 7.5 MS/s boundary | **1.0368 MHz** commanded (±87 Hz) |
| 7.5 → 15 MS/s boundary | **1.5578 MHz** commanded (±124 Hz) |
| Above 3.1 MHz actual | rate **clamps at 15 MS/s**, filter widens alone (3.1, 6.7, 10 MHz seen) |
| Worst error of the old linear model | **×0.170** at 1.70 MHz commanded |

**The practical consequence, which was not obvious before:** a wider span buys *bandwidth*, not
samples per symbol. Everything from about 1.56 MHz commanded to the full 10 MHz samples at 15 MS/s, so
the widest span is the right choice only until the signal fits inside it.

## What the sweep did not establish, stated so it is not mistaken for known

**That every integer $n$ is available.** Forty points landed on 35 distinct ones. Predicting the rate
from a *commanded* bandwidth is therefore exact only at and above **17 kHz** commanded, and within
**1.40 %** below it — the instrument chose 3 094 ticks at 1 kHz where $W_1/n$ gives 3 100, and 308 805
at 10 Hz where it gives 310 000.

That residual is left alone on purpose. The prediction exists to size a block *before* there is an
instrument to ask, and `REQ-HAL-001`'s negotiation means the driver reads the true period back at
every configuration and on every block. A per cent does not matter there; 490 % did.

## What changed in the product

`E4406ASampleRate` is the law as a pure function, so it can be tested — which the two wrong models
never could, living as one of them did inside a private nested class in a VISA transport.
`E4406ASampleRateTests` asserts it against these readings, both the exact region and the measured
1.40 % gap, with the boundaries and the clamped region pinned. Connect measures $W_1$ from one reading
taken at a thirty-second of the maximum bandwidth, which is far enough inside the tracking region that
the clamp cannot corrupt it.

**Re-verified on the bench after the change**, because it alters real block sizing: cross-validation
10 of 10, feature exercise 97 of 97, `--demod-check` 3 of 3 with 1024 of 1024 PN9 bits again (block
length moved from 23 048 to 22 109 samples, now bounded from the true rate). GRAYQPSK reproduced its
75.10 % to two decimals.
