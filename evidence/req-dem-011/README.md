# REQ-DEM-011 — user-defined constellations and their labelling

Two things a point list does not say: which bits each point carries, and — it turned out — whether
step 3 can find the carrier of the constellation at all.

## The labelling, proved against a transmitter

`OpenVSA.Verify --demod-check`, E4438C into E4406A, 500 ksym/s, PN9 generated on this side from
ITU-T O.150's polynomial. Eleven cases, all as expected; `demod-check-gray.txt` is the run.

Three signals appear twice, once against each labelling. **Nothing about the demodulation changes
between the pairs** — same waveform, same decisions, same EVM to within the run-to-run scatter — and
only which bits the points are said to carry:

| Transmitted | Labelled | Bits against PN9 | Relabelling |
|---|---|---|---|
| `GRAYQPSK` | natural | best **75.10 %** | one accounts for 512 of 512 |
| `GRAYQPSK` | **Gray** | **1024 of 1024 (100 %)** | — |
| `P4DQPSK` | natural | best **74.95 %** | Gray, 511 of 511 |
| `P4DQPSK` | **Gray** | **1022 of 1022 (100 %)** | — |
| `D8PSK` | natural | best **66.60 %** | Gray, 511 of 511 |
| `D8PSK` | **Gray** | **1533 of 1533 (100 %)** | — |

The near-misses are the theory, not a coincidence: relabelling a 2-bit symbol with a Gray code
transposes two of its four values, leaving 75 % of bits right; on 3 bits the same arithmetic gives
16 of 24, or **66.7 %**. Measured 74.95 % and 66.60 %.

So this instrument Gray-labels its constellations and its phase changes, OpenVSA can now be told to,
and the difference between the two is the difference between a stream that is nobody's sequence and
one that is exactly the sequence.

**Gray means two different things and the code implements both.** On a ring — the phase-keyed family
— neighbouring points are neighbouring indices, so the labelling is the reflected binary code of the
index. On a square grid a point has neighbours on both axes, so the code is applied to each axis's
level separately. Applying the ring's version to a QAM would leave touching points differing in
several bits, which is the whole property Gray coding exists for. Where neither applies — a cross
QAM, a star, an arbitrary set of rings — there is no one Gray code, and it is refused rather than
guessed.

## 🔴 A correction to `../req-dem-012/`

That page reports the natural-labelled differential cases as scoring **50.10 %**, "exactly chance",
and reads that as the instrument not differentially encoding. **The 50.10 % figures were an artefact
of the measuring harness, not readings of the signal.**

`BitStreamAlignment.Find` was asked to search a single rotation for a differential stream, and that
was expressed by passing a *state count* of one — which also collapsed every symbol to zero in the
bit extraction. A stream of nothing but zeroes scores about half against any sequence. The tell was
in the output all along: **"best 50.10 % against a typical 50.10 %"** — the best candidate and the
typical one being equal, because every candidate reading was the same reading.

Corrected, those cases read **74.95 %** and **66.60 %** — the Gray-versus-natural mismatch above.

What does **not** change is the conclusion drawn there, because it never rested on that number: the
relabelling analyser found a Gray labelling accounting for 511 of 511 symbols of each, and it does
not go through the broken path. The instrument is differentially encoding, and the two Gray cases in
the table above now confirm it a second way, at 1022 and 1533 bits of 1022 and 1533.

The defect was found by adding the cases this requirement needed, not by review.

## The acceptance criterion, and what it uncovered

> A user-defined 32-APSK (4/12/16 ring structure) demodulates correctly from the simulator.

First attempt: **12.96 %rms, converged, 43 of 512 symbols recovered, and a reported carrier offset of
64 481 Hz on a signal that had none.** Not the labelling — step 3 again.

`stripping-quality.py` computes, for every format in the catalogue, how much of it survives being
raised to its own rotational symmetry — the coherent sum of the raised points over the sum of their
magnitudes, which is exactly the line step 3 looks for. `stripping-quality.txt` is the output:

```
              BPSK    2    1.000000          16QAM    4    0.515152
              QPSK    4    1.000000          64QAM    4    0.448276
              8PSK    8    1.000000        4096QAM    4    0.428871
             16PSK   16    1.000000   32QAM (cross)   4    0.145038
         16STARQAM    8    1.000000  2048QAM (cross)  4    0.132156
         32STARQAM   16    1.000000
                                        32APSK 4/12/16  4  0.000500
                                           16APSK 4/12  4  0.006053
```

The phase-keyed family and the stars are exactly 1. Square QAM is around a half, cross QAM around an
eighth — and **a multi-ring APSK is five ten-thousandths**. The reason is arithmetic: raised to the
fourth power, the twelve points of the middle ring land on three angles that cancel and the sixteen
of the outer ring land on four that cancel, so only the inner four survive — at the smallest radius
of the three, where the fourth power of the radius is a five-hundredth of the outer ring's.

There is therefore no carrier line to find, and the tallest line in that spectrum is something else.

**The threshold is a reading of the formats.** Three hundredths is the geometric middle of the gap
between the weakest format the step works for (0.132) and the strongest it does not (0.006) — a
factor of four below one and five above the other, with nothing in the catalogue in between. Below
it, step 3 declines and says so:

```
Step 3 did not estimate a carrier offset. Raising 32APSK to the power of 4 leaves 0.0005 of it
standing, against the 0.03 this step needs: its rings cancel one another and the carrier's line is
not the tallest one in that spectrum. Step 8 therefore starts from no offset, so any real one must
be inside what it can pull in (REQ-DEM-036).
```

With that, the criterion is met: **0.0142 %rms, 512 of 512 symbols, carrier error 0.00 Hz.**

Declining is not a workaround for a weak estimator — it is the estimator saying what it knows. The
alternative was a confident 64 481 Hz, a demodulation that recovered a twelfth of its symbols, and a
`Converged` flag that said yes.
