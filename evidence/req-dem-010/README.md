# The format catalogue — REQ-DEM-010

Everything in the requirement's table is demodulated except one row, and this is the measurement
that says why that row is a decision rather than an omission.

## SOQPSK is ternary, and that is the whole of it

`soqpsk-pulse.py` evaluates SOQPSK-TG from **IRIG Standard 106-15 (Part 1), Chapter 2, subclause
2.4.3.2** — equations 2-5 to 2-10 and Table 2-4 — and nothing else: the frequency pulse *g(t)*, the
amplitude *A* that makes it integrate to π/2, the phase integral, the ternary precoder, and the
principal Laurent pulse *c₀* built from them. `soqpsk-pulse.txt` is its output.

Two formats in this catalogue are already continuous-phase modulations — **MSK** and **GMSK** —
and they are in it because Laurent's decomposition writes a *binary* CPM as a linear pulse driven by
pseudo-symbols that walk around a ring, **exactly**. That pulse is what OpenVSA shapes with and
matches against, and it is why `PulseFilterType.Msk` and `PulseFilterType.Edge` exist.

SOQPSK's excitation is not binary. Equation 2-10 produces impulses in **{−1, 0, +1}**, and the zero
is what breaks the construction: when the phase does not move, two adjacent pulse tails add *in
phase* rather than in quadrature, and the linear model's envelope rises by exactly √2 on a waveform
that is constant-envelope by definition.

| Measured on the same rectangular pulse, where Laurent has one term and is exact | EVM of the linear model | Model's envelope |
|---|---|---|
| binary impulses {−1, +1} — which is MSK | **0.000000 %rms** | 0.000 dB |
| ternary impulses from equation 2-10 | **14.17 %rms** | **3.010 dB** |

3.010 dB is 20 log₁₀ √2, to three decimals. The control is the point: **the same arithmetic, the
same pulse, exact on one alphabet and wrong by fourteen per cent on the other.** It is not the
pulse, and it is not the implementation.

On SOQPSK-TG's own four-bit-period pulse the same comparison reads **13.21 %rms**, with the true
waveform's envelope flat to 3.9e-15 dB as a CPM's must be.

## What that leaves

Demodulating SOQPSK needs one of two things, and both are decisions:

- **a detector over the modulation's phase states** — a trellis, which the fourteen ordered steps of
  `REQ-DEM-001` have no place for and which changes what "demodulate" means in this product; or
- **the ternary PAM decomposition**, whose detection pulse comes from the literature rather than
  from the standard — a sourcing decision this project has escalated before (#425 was a *reading* of
  3GPP TS 45.004, not an implementation of it).

Until one is chosen, `Constellation.ByName` refuses the name and says which — rather than answering
with a constellation that would report a respectable EVM against a signal it does not describe.

## The pulse is ready when the decision is made

`soqpsk-pulse.py` is not only a refutation. The frequency pulse, the amplitude, the phase integral
and `c₀` are all evaluated from the standard and checked for the properties the mathematics
requires — c₀ symmetric about its centre to 7.5e-6 (the quadrature tolerance), peak 0.998983, energy
0.999107 — so whichever route is chosen, the definition it starts from is here and has been read
once already.
