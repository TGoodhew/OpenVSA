# How OpenVSA demodulates a signal

The digital demodulator works through a fixed chain of fourteen steps. The order matters: several
of the steps could be arranged differently and would still produce a number, but a different number.
This is the order OpenVSA uses, and it is the order every result you see was produced by.

```
1. Extract Search Length window from Main Time
2. Burst / pulse search (optional)
3. Coarse carrier estimate
4. Resample to N points/symbol
5. Measurement (matched) filter
6. Sync-pattern search (optional)
7. Position Result Length window
8. Joint refinement, iterated to convergence: carrier frequency · carrier phase · symbol timing · amplitude
9. Symbol decisions → detected bits
10. Reference regeneration: bits → ideal symbols → reference filter → ideal waveform
11. Adaptive equaliser (optional; re-enters at 8 on update)
12. Impairment estimation: IQ offset, gain imbalance, quadrature skew, amplitude droop
13. Error metric computation at symbol instants
14. Result trace generation
```

## The three optional steps

**Burst / pulse search** (step 2) and **sync-pattern search** (step 6) locate the part of the
acquisition worth analysing. Turn them off for a continuous signal with no sync word: there is
nothing for them to find, and the Result Length window is then positioned from the start of the
Search Length window instead.

**The adaptive equaliser** (step 11) removes linear distortion — the frequency response of a cable,
a filter or an amplifier, which shows up as inter-symbol interference and inflates EVM. Turn it off
when you want to measure the signal as it arrives rather than as it would have arrived through a
perfect channel.

Turning an optional step off changes nothing about the order of the rest. The steps around it run
exactly where they run when it is on.

## Why the equaliser sends the chain round again

Step 11 is the chain's only loop. When the equaliser updates its coefficients it has changed the
waveform that step 8 estimated carrier frequency, phase, timing and amplitude from — so those
estimates are answers to a question that has changed, and the chain re-enters at step 8 to ask it
again of the equalised waveform. Each pass produces a complete result, so you can see what the
equaliser bought: on a signal with real inter-symbol interference the second pass's EVM is a great
deal lower than the first's, and on a signal with none the equaliser finds nothing to change and
there is no second pass at all.

The number of passes is bounded. If the equaliser is still making changes when the chain reaches
that bound, the result says so rather than presenting the last pass as though it were the answer the
equaliser was heading towards.

## Why step 8 iterates, and what "converged" means

Carrier frequency, carrier phase, symbol timing and amplitude cannot be measured one at a time. A
frequency error looks like a phase that grows along the block; a timing error on a pulse-shaped
signal looks like an amplitude that has shrunk; an amplitude error looks like a constellation that
has moved inwards. Step 8 therefore fits all four together, over the whole Result Length, and
repeats the fit until the estimates stop moving.

"Stop moving" is a stated criterion, not an impression: every parameter must change by less than a
set tolerance on an iteration — frequency in cycles per symbol, phase in radians, timing in samples,
amplitude as a fraction. The iteration count is bounded, and if the bound is reached before the
criterion is met the result reports it. An estimate that had not finished is still shown, because
it is usually the most informative thing available about a difficult signal, but it is never shown
as though it had.

## The two filters are not the same filter

Step 5 applies the **measurement filter** to the acquired signal and step 10 shapes the ideal
waveform with the **reference filter**. They are separate settings because they do different jobs.
A transmitter using root-raised-cosine shaping splits the Nyquist filtering between itself and the
receiver; OpenVSA's measurement filter emulates the receiver's half, and so must match what the
transmitter used. The reference filter shapes the ideal waveform the measurement is compared
against, which has been through both halves — the full Nyquist filter. Setting both to the same
root-raised cosine is a common mistake and puts a few per cent of EVM on a perfect signal.

## Where this order is written down

The order is declared once, in the demodulator's own code, and everything else is generated from
that declaration — including the list at the top of this page. A test compares the two on every
build, so this page cannot drift from what the software does.
