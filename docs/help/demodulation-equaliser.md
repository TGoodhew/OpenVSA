# The adaptive equaliser

The equaliser measures the channel between the transmitter's output and the analyser's input, and
takes it back out of the signal before the error metrics are computed. It is told nothing about that
channel: its coefficients come from the measured signal and the demodulator's own regenerated
reference, and nothing else.

This page covers the three controls — **Filter Length**, **Convergence factor**, and the
**Run / Hold / Reset** mode — and one thing about the filter's shape that is worth knowing before
you lengthen it.

## Filter Length is in symbols, and an N-symbol filter has 2N taps

The taps are spaced **half a symbol apart**, so:

| Filter Length | Taps |
|---|---|
| 5 symbols | 10 |
| 11 symbols (the default) | 22 |
| 21 symbols | 42 |

This trips people up often enough to be worth stating twice: **length is in symbols, the tap count
is twice it.**

The half-symbol spacing is not a convention. Taps spaced a whole symbol apart can only correct what
the channel does within the band up to half the symbol rate, and a real signal occupies more than
that once its roll-off is counted — a fractionally spaced filter can correct across the whole
occupied band, and it does not care where the sampling instants happen to fall.

Longer is not automatically better. Every tap is a coefficient fitted from the same block of signal,
so a longer filter is a less certain fit; a filter far longer than the channel's delay spread spends
its extra freedom describing noise, and a filter long enough to reach the symbols at the ends of the
result window has less of them to work with. Lengthen it because the channel needs it — a long echo,
a slow group-delay ripple — rather than as a matter of course.

## Where the impulse sits, and why it moves

A filter that corrects nothing is a single impulse: one tap of unity, the rest zero. Where that
impulse sits within the filter decides how the filter's length is divided between the two different
things an equaliser does.

- Taps **before** the impulse undo what arrives *early* — the anticausal part, which for a
  well-behaved signal is bounded by the reach of the pulse shaping itself.
- Taps **after** it undo what arrives *late* — echoes, and the decaying series that cancels them.
  This is unbounded, and it is the reason you lengthen the filter.

So while the filter is short, the impulse sits at its **centre** and the two halves are equal. Once
the filter is longer than twice the pulse's reach, the impulse stops moving further in and every
further tap is added after it: its position **as a fraction of the filter moves towards the start**,
which is what makes the extra length available to a long delay spread.

A fixed-centre equaliser cannot do that. Half of every tap you add goes in front of an impulse that
has nothing to do there, so a channel whose delay spread is longer than half the filter stays
uncorrected however long you make it.

## Run, Hold and Reset

The mode decides what happens to the coefficients **between measurements** — not between the
internal passes of one measurement, which the equaliser manages itself.

- **Run** fits from the current measurement and carries the result into the next. The coefficients
  change from measurement to measurement, because each measurement fits its own; what it inherits
  from the last one is the standard the new fit has to beat, so a block whose own fit comes out
  worse keeps the filter it was handed rather than throwing a good one away.
- **Hold** freezes the coefficients. They are still *applied* to every measurement — Hold is not the
  same as switching the equaliser off — but nothing is fitted, so successive measurements are
  corrected by identical taps. Use it once the equaliser has settled on a channel that is not
  changing, to take the fit's own variation out of the numbers you are reading.
- **Reset** returns the filter to a unit impulse, which corrects nothing. Selecting Run afterwards
  starts adapting again from nothing.

Hold with nothing yet held, and Hold after the filter length has been changed, both apply a unit
impulse and say so: coefficients fitted for one tap count mean nothing at another, so they are
dropped rather than stretched. Run the equaliser once at the new length to give Hold something to
freeze.

## Which algorithm fits the filter

**Least squares (the default).** The exact solution, computed in one shot from the whole block. The
analyser has already regenerated the ideal symbol sequence by the time the equaliser runs, so the
filter that minimises the error against it can be written down directly rather than crept up on. It
is optimal, it has no step size, and it gives the same coefficients every time it is run on the same
samples — run-to-run variation in a least-squares equaliser is a defect, not a tolerance.

**LMS** and **normalised LMS** adapt incrementally, one update per symbol. They are offered because
an equaliser with a convergence factor and Run/Hold/Reset controls implies incremental adaptation,
and because a transient can be worth seeing. They are not better: with the reference already in hand
there is nothing an iterative method can find that the exact solution has not.

Expect a gradient mode to land a little short of the exact one, and know what sets the distance. An
LMS filter carries an excess error over the optimum of about **µ·L·Pₓ/2** of its mean-square error,
where L is the tap count and Pₓ the signal's power — so at 42 taps a convergence factor of 0.01
costs about 0.8 dB of EVM before convergence is even in question, and 0.003 costs a quarter of a
decibel. Halve the step or shorten the filter, not both at once.

## Convergence factor

The **convergence factor** is the step size — how far the coefficients move on each symbol in
response to the error at that symbol. Small values settle quietly and take longer; large ones move
quickly and leave more of their own noise on the coefficients; past a bound they do not converge at
all but **diverge**.

That bound is **µ < 2/(L·Pₓ)**. It depends on the tap count and on the power of the signal actually
presented, so a step size that is safe on one measurement is not necessarily safe on the next, and
it is not something you should have to recompute when the reference level changes. The analyser
evaluates it from the measurement's own samples and **refuses** a step size outside it, reporting the
bound it violated. Nothing is quietly clamped: a refused step leaves the coefficients as they were,
which is a measurement you can account for.

**Normalised LMS** exists to make that go away. Its step is divided by the input's own energy, so it
is stable for any step below **2** whatever the signal level or the tap count — the setting means the
same thing every time. Its excess error is about µ̃/(2 − µ̃), so 0.1 costs about a fifth of a decibel
and 0.5 costs more than one.

Neither number affects the least-squares fit, which has no step size at all.

## Getting started when the eye is closed

Decision-directed adaptation measures its error against the nearest constellation point, which is the
right error only when the nearest point is the one that was sent. On a channel severe enough to close
the eye it is not, and the filter is then driven confidently towards the wrong symbols. Two
**acquisition** modes start a gradient equaliser without that assumption:

- **Constant modulus (blind).** Asks only that the output have the right magnitude and says nothing
  about which symbol it is, so it needs no knowledge at all. It is also blind to phase, which is why
  it cannot finish the job.
- **Data-aided.** Where a sync pattern is set and found, the symbols under the pattern are *known*
  rather than decided, so the ordinary error can be formed from them however closed the eye is.
  Better than blind where it applies — it fixes the phase too — and it applies only there.

Either way, once EVM falls below the **handover threshold** the equaliser switches to
decision-directed adaptation for the rest of the block, and says in the measurement's notices how
many sweeps that took. On this analyser both take a two-ray channel whose second ray is seven tenths
of the first from an unmeasurable 45 % EVM to about 1 %.

Data-aided acquisition updates only under the pattern — a few tens of symbols in a window of
hundreds — so it is given the same number of *updates* as any other mode rather than the same number
of sweeps. Without that it would fail for want of arithmetic rather than for want of information.
