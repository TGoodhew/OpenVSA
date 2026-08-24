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
