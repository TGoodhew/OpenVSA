# The Result Length and the windows around it

The Result Length is how many symbols are demodulated and displayed. Two other things are measured
against it: the **pre-demodulation window**, which is deliberately wider, and the **limit** on how
long it may be for an offset format. This page covers both, and flags the one place where a name had
to be interpreted rather than looked up.

## The pre-demodulation window: 20 % wider, always

The pre-demodulation traces — **Time**, **Spectrum** and **Instantaneous Spectrum** — analyse a
window **1.2 × the Result Length**, centred on it. The result traces show what was *measured*; these
show what was *there*.

That is what makes a burst's transition regions visible. The result window is positioned to exclude a
transmitter's leading and trailing ramps — measuring them would put their EVM into your numbers — and
the extra 20 % is where you go to see the thing that was excluded, and on which side.

The extension is taken from **both** ends by preference, because a burst has a ramp at each and a
window extended only forwards would show one and hide the other, which reads as an asymmetric
transmitter rather than an asymmetric window. Where one end has no room — with no sync pattern and no
burst, the result window starts at the measurement filter's transient, only a few tens of samples
into the waveform — **the other end takes the remainder**, so the span stays 1.2 × whatever happens.
Only a record too short overall gives less, and the trace reports the span it actually got.

**Displaying a pre-demodulation trace cannot change your result.** There is no setting for it and no
flag in the chain: the wider window is cut on every measurement and nothing downstream ever reads it.
EVM over the Result Length is not merely *close* whether or not you look at these traces — it is the
same number, because there is no path by which it could be another one.

## Spectrum and Instantaneous Spectrum

Both use that same 1.2 × window. They differ in **how the spectrum is formed**:

| Trace | How | Read it for |
|---|---|---|
| **Instantaneous Spectrum** | one transform of the whole window | a burst edge, a transient — the spectrum of *this* record, transients and all |
| **Spectrum** | the averaged magnitudes of four half-overlapped segments of the same window | a noise floor, a spur — a steadier estimate at a quarter the resolution |

Magnitudes are averaged, not complex values. Averaging the transforms would let segments whose phases
differ cancel each other, and the noise floor you are reading the trace *for* would fall by the
square root of the segment count — an estimator that reports less noise the more of it you look at.

Where the window is too short to divide, the two coincide. That is correct, not a degenerate case.

> ### ⚠ An interpretation, not a specification
>
> `REQ-DEM-032` names all three pre-demodulation traces and gives them **the same** 1.2 × window. So
> Spectrum and Instantaneous Spectrum cannot differ by window, and the requirement does not say what
> else they differ by. **Making the difference the estimator — averaged against not averaged — is
> this build's reading.**
>
> It follows the reference product's sense of "instantaneous" as *not averaged*, and it is the only
> distinction left once the window is fixed. If it is ever changed, the change is local: the segment
> count and the averaging live in one method, and the tests assert the *relationship* between the two
> traces — that the averaged one is steadier and the instantaneous one resolves more finely — rather
> than any particular bin count.

## The limit on Result Length for offset formats

An offset format — OQPSK and its relatives — staggers I and Q by half a symbol, so it is demodulated
at **two instants per symbol** rather than one. A Result Length therefore costs twice the estimator
work and twice the state it would for a non-offset format of the same length.

The default limit is **2 048 symbols**. Turning on the **low SNR enhancement** raises it to
**40 000**. Non-offset formats were never subject to the shorter limit and are unaffected; setting
the option on one is harmless rather than an error, so that changing format never fails for a reason
you did not cause.

Asking for more than the limit is **refused, with a message naming the limit and the option that
lifts it** — not quietly shortened. A window that accepted 40 000 symbols and analysed 2 048 of them
would report the short window's steadiness under the long window's name, and nothing you could see
would say so.

### What the longer window buys, and what it does not

**It does not make EVM lower.** It makes the *estimate* of it steadier. The error metrics are
statistics over the symbols in the window, so their spread falls as the window grows — which is what
lets a weak signal be measured at all, instead of reading differently on every sweep.

Measured on a 12 dB signal, going from 2 048 to 40 000 symbols: the spread of the EVM estimate falls
by **7.6×**. The symbol count alone predicts √(40000/2048) = 4.4×, and the rest is the residual
carrier estimate, whose own spread falls **76×** over the same change — the error of a linear phase
fit improves far faster than a per-symbol statistic does. Two things steady, so the total steadies
faster than either.

### One thing to know when comparing short and long windows

A short Result Length reads **very slightly higher** EVM than a long one on the same signal — about
1.4 % of the reading, between 2 048 and 40 000 symbols.

This is not the length as such. The result window starts *at* the measurement filter's transient, so
a fixed number of symbols at its leading edge are slightly degraded, and a fixed number is a much
larger *fraction* of 2 048 symbols than of 40 000. Inside a single 40 000-symbol measurement, the
leading 2 048 symbols read 1.5 % above an interior 2 048 of the same result — the same effect, with
no length difference involved at all.

If you are comparing measurements at two Result Lengths and see a difference of this size, it is the
window's edge and not the signal.
