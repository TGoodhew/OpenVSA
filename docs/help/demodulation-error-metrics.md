# The error summary: what the numbers are, and what they are relative to

Two things in this table are conventions rather than measurements, and both are the sort of thing
that makes two instruments appear to disagree about the same signal. They are set out here because
neither can be read off the number on screen.

## What the percentages are a percentage of

EVM and magnitude error are error vectors divided by a reference magnitude, `V_norm`, and there is
more than one defensible choice of it:

| Setting | `V_norm` |
|---|---|
| **RMS magnitude** (the default) | the square root of the mean power of the reference constellation |
| **Maximum magnitude** | the largest magnitude in the reference constellation |
| **User-specified** | whatever you supply |

**On a constant-modulus format the choice does nothing.** BPSK, QPSK, 8PSK and MSK put every point
the same distance from the origin, so the maximum and the RMS are one number. The summary says so
rather than leaving you to change the control and watch nothing happen.

**On a variable-envelope format it changes every percentage.** For 16-QAM the maximum magnitude is
√1.8 = 1.342 times the RMS, so an EVM referenced to the maximum reads 1.342 times *smaller* than the
same measurement referenced to the RMS. For 64-QAM the factor is 1.528. Neither number is wrong; a
comparison between two instruments that have made different choices is.

The line beneath the table always states which was used, its value, and what the other setting would
have read.

**The reference is the constellation, not the symbols in the window.** A 24-symbol window of 64-QAM
visits at most 24 of its 64 points, and a divisor computed from those would make the same signal
read differently from one acquisition to the next.

**IQ offset is deliberately not normalised this way.** Carrier feedthrough is a property of the
signal, so it is always referenced to the RMS magnitude of the reference constellation whatever the
EVM setting is. Were it to follow `V_norm`, the same leakage would be reported as two numbers
2.55 dB apart on 16-QAM depending on a display option that has nothing to do with it.

## Gain imbalance and quadrature error can stand in for one another

The summary reports both:

- **IQ Gain Imbalance** — the two axes are different lengths. Positive means Q is larger than I.
  Geometrically, a rectangular stretch along the I and Q axes.
- **IQ Quad. Error** — the two axes are not a right angle apart. Geometrically, a stretch along the
  45° lines *between* the axes.

Those are the same kind of transformation turned through 45°. **So a transmitter whose modulator
axes sit 45° from the axes your constellation names will show you its gain imbalance as a quadrature
error, and nothing in the signal can tell you otherwise.** The axes are a convention, and only the
constellation names them.

Measured, so that the size of it is concrete: 1.5 dB of gain imbalance injected 45° away from the
receiver's axes reads as 9.85° of quadrature error and no imbalance at all.

This is not a defect and it is not something the analyser can resolve for you. If the two numbers
look surprising for a transmitter you know, check the symbol-mapping convention before checking the
hardware.

### What *is* resolved

The other ambiguity — between quadrature error and carrier phase — **is** resolved, and
deterministically. The impairment fit uses the symmetric model, in which each axis is turned by half
the skew:

```
Re z = gI (Re r cos(psi/2) + Im r sin(psi/2)) + cI
Im z = gQ (Re r sin(psi/2) + Im r cos(psi/2)) + cQ
```

That transformation has no rotational component, so the carrier-phase estimate has nothing of the
skew to absorb. The alternative — putting the whole skew on Q, which is a shear — decomposes into a
rotation by half the skew composed with this one, and that rotation is indistinguishable from
carrier phase: half the quadrature error would silently become phase, and how much depended on the
order the estimators happened to run in.

Estimating the same signal from different carrier offsets returns the same split between phase and
skew to the last digit.

## SNR (MER)

One quantity, two names. The wider industry calls it modulation error ratio; the instrument family
this analyser follows calls it SNR. The label says both.

Its denominator is **everything** that moves a symbol off its ideal position — additive noise,
distortion and intersymbol interference alike. A signal with no noise at all but a mismatched
measurement filter therefore reports a finite figure rather than infinity, which is the point: the
number answers "how far are the symbols from where they should be", not "how much thermal noise is
there".
