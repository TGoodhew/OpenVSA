# The filter group — REQ-DEM-020, 021, 022, 022a, 023

Five requirements, one piece of work, because they are five statements about the same object: what
the filters are, which two positions they go in, what the formulas say, how they are scaled, and how
long they are. Splitting them would have meant deciding the normalisation twice.

Four design questions came out of it, each filed with what was implemented and what unrolling it
would cost: **#425** (the EDGE pulse's integral limit), **#426** (two catalogue entries the
requirement never defines), **#427** (two requirements stating incompatible tolerances for the same
identity), **#428** (the two conventions the requirements delegate).

## The EDGE pulse is the standard's, checked against the standard

`edge-pulse.py` evaluates *c₀(t)* from 3GPP TS 45.004 subclause 3.5 and nothing else: the published
definition, in a different language, by a different numerical method — composite Simpson on the phase
integral where the implementation uses that integral's closed form. `edge-pulse.txt` is its output
and the 41 coefficients the test compares against.

**Agreement: 1.3e-12**, against a criterion of 1e-6.

That is worth more than the number suggests. Two independent readings of a specification agreeing to
twelve digits is evidence that the specification was read correctly; an implementation checked
against itself would agree to sixteen and mean nothing.

**And it is not a Gaussian.** Sweeping BT from 0.01 to 2.00 and comparing shapes at unit peak, the
nearest Gaussian is BT 0.21 and it is out by **0.0177 rms** — four orders above the tolerance the
pulse itself meets. The requirement puts that warning in a box; this measures it.

## Where the acceptance criteria came out

| Criterion | Required | Measured |
|---|---|---|
| EDGE against the published pulse | < 1e-6 | **1.3e-12** |
| Nearest Gaussian to EDGE | must fail | 0.0177 rms |
| RC / RRC / Gaussian taps against their formulas | < 1e-12 relative | passes at α = 0, 0.22, 0.35, 1.0 |
| Continuity across every removable singularity | < 1e-9 | exact (both sides return the limit) |
| Cascade RRC⊛RRC against RC, ±8 | < 1e-3 | **5.40e-4** |
| Cascade, ±64 | < 5e-6 | **3.18e-6** |
| Windowed sidelobes vs an abrupt cut, ±8 | must be below | −61.3 dB vs −55.7 dB |
| … at ±32 | must be below | −115.2 dB vs −81.6 dB |
| CW amplitude across spans 4 to 32 | < 0.01 dB | **1.5e-14 dB** |
| ISI at neighbouring symbol centres, matched pair | zero | 7.1e-4 of peak |
| … mismatched pair | must differ | 1.83e-2 — twenty-six times worse |
| EVM against span (±4 / ±8 / ±20) | must degrade as the trade says | 0.386 / 0.205 / 0.029 %rms |

The cascade row reproduces `REQ-DEM-022a`'s own quoted floors — 5.4e-4, 1.1e-4, 1.1e-5, 3.2e-6 —
which is what says the measurement and the requirement are talking about the same quantity.

## Three things the tests caught that review had not

**The reference table in the test was invented, not copied.** The first version of the EDGE
comparison failed at 4.06e-2 and looked like an implementation fault. It was not: the 41 coefficients
had been typed in by hand rather than pasted from the script's output, and were internally
inconsistent — the value at ±0.125T was larger than the peak at 0. A table that disagrees with its
own maximum is not a reference. The real table is now inserted programmatically from
`edge-pulse.txt`.

**The sidelobe probe ran past Nyquist.** It swept the discrete transform to the sample rate rather
than to half of it, so the response folded onto itself and every filter came back with a "worst
sidelobe" of exactly 0 dB — windowed and abrupt alike. A measurement that gives the same answer for
two things it is supposed to distinguish is broken, not surprising.

**One filter's singularity passed because another's failed first.** The root raised cosine appeared to
hold continuity at a band a thousand times narrower than the raised cosine needed — until the raised
cosine was fixed, the test got as far as the root, and it failed by 1.5e-7. Two filters with the same
shape of singularity have the same problem; one of them passing was the test running out of
assertions.

## Against the bench

`demod-check-filters.txt` — the whole cross-check re-run through the new filter path, with the span
default moved from 6 to 8 symbols and the measurement filter normalised to unit DC gain instead of
unit energy: **11 of 11 cases as expected**, EVM 0.86 to 0.96 %rms across the matrix. Cross-validation
10/10 and the feature exercise 97/97 alongside.

Nothing in those numbers moved outside its usual scatter, which is the point: the filters were
rebuilt from the requirement's formulas and the measurements they produce are the same measurements.
