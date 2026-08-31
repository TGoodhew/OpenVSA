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

## Convergence factor

The **convergence factor** is the step size of the LMS adaptation — how far the coefficients move on
each symbol in response to the error at that symbol. Small values converge slowly and settle quietly;
large ones converge quickly and leave more of their own noise on the coefficients, and past a bound
that depends on the signal's power and the tap count they do not converge at all.

It has no effect on the default least-squares fit, which is solved in one shot from the whole block
and has no step size to set. That is why it is the default: with the reference sequence already
regenerated, the exact solution is available directly, it is the same every time it is run on the
same samples, and there is nothing to tune.
