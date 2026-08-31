# Demodulation filters

A digital demodulator applies two filters, and they are not the same filter doing two jobs. This
page says what each is for, why the pair matters, and what the filter span costs you.

## Why there are two

Nyquist filtering is **split between the transmitter and the receiver**. The transmitter shapes its
symbols with one half — conventionally a root raised cosine — and the receiver applies the matching
half. Two things follow, and they are the reason both filters are yours to choose:

- **The measurement filter emulates the receiver.** It must match the shaping the *transmitter*
  used. Matched root-raised-cosine filters give the best signal-to-noise ratio a linear receiver
  can, which is why the split exists at all.
- **The composite of the two must be the full Nyquist filter**, so that the response is zero at
  every symbol centre but its own — no intersymbol interference. A root raised cosine cascaded with
  another root raised cosine of the same roll-off *is* a raised cosine, which is that full filter.

The **reference filter** shapes the ideal waveform your measurement is compared against. Because the
measured signal has already been through the transmitter's half and the analyser's half, the ideal
it is compared with must be shaped by the **composite** — the raised cosine — and not by a root.
That is why the reference filter defaults to Raised Cosine while the measurement filter defaults to
Root Raised Cosine. Setting the reference to a root instead puts several per cent of EVM on a
perfectly good signal: the two waveforms then differ *between* the symbol instants even when every
symbol is right.

Both filters are independently selectable in type and in parameter. That is deliberate: a signal
whose transmitter used something other than a root raised cosine needs a measurement filter that
matches *it*, and demonstrating what a mismatched pair does is a legitimate thing to want to do.

## The catalogue

| Filter | Parameter | What it is |
|---|---|---|
| Root Raised Cosine | roll-off α | The receiver's half of a Nyquist pair. The usual choice. |
| Raised Cosine | roll-off α | The full Nyquist pulse the matched pair composes to. |
| Gaussian | BT | The GSM/GMSK family's shaping. |
| EDGE | — | The linearised-GMSK main pulse *c₀(t)* of 3GPP TS 45.004. **Not a Gaussian.** |
| Half Sine | — | One half-period of a cosine across **one** symbol; the shaping of an offset format's staggered axes. |
| MSK | — | The linearised-MSK main pulse: half a cosine across **two** symbols. MSK's shaping at the bit rate. |
| Rectangular | — | Unity across one symbol, zero outside it. |
| Low-pass | cutoff | An ideal brick wall, which is a sinc in time. At the default cutoff of half the symbol rate it is the Nyquist sinc. |
| User-defined FIR | taps | Your own coefficients, at a stated number of samples per symbol. |
| None | — | No shaping at all. |

**Half Sine and MSK are one factor of two apart, and it matters more than it sounds.** MSK is two
points that turn a right angle every symbol, shaped by a half cosine spanning **two** symbol periods
— that overlap is what makes its envelope constant, and a constant envelope is the whole reason the
format exists. Shape the same symbols with the one-symbol Half Sine instead and the envelope falls
to **zero at every symbol boundary**. Measured on this analyser, that signal demodulates at
0.000000 %rms: a perfect measurement of something that is not MSK. Use **MSK** for MSK at the bit
rate, and **Half Sine** for the staggered axes of an offset format.

**EDGE is a distinct filter, not a Gaussian at some particular BT.** It is the principal component
of the Laurent decomposition of GMSK — the pulse an EDGE transmitter actually sends its 3π/8-rotated
8PSK symbols through. The nearest Gaussian to it is not close: sweeping BT over the whole useful
range, the best any of them manages is a root-mean-square difference of about 0.02 against a pulse
whose peak is 0.93. Choosing one for the other would be a measurement of the wrong thing.

## Which filter a format needs

Most formats are shaped by a root raised cosine and matched by another, which is why that is the
default. Four are not, and selecting the format does not select the filter — the two are independent
choices on purpose (a transmitter that shaped its signal some other way is a thing you may need to
measure):

| Format | Transmit pulse | Measurement filter | Reference filter |
|---|---|---|---|
| MSK type 1, MSK type 2 | MSK | None | MSK |
| GMSK | EDGE (*c₀*) | None | EDGE |
| 3π/8-8PSK (EDGE) | EDGE (*c₀*) | None | EDGE |

**The measurement filter is None for all of them, and that is not an oversight.** A root raised
cosine is *half* of a Nyquist filter and the receiver applies the other half; these pulses are the
whole shaping, already applied by the transmitter. Matching a half-cosine transmit pulse with a
half-cosine receive filter would apply the shaping twice.

**EDGE and GMSK need the equaliser to reach their best.** The *c₀* pulse spans about three symbols
and is not a Nyquist pulse: it puts intersymbol interference into the signal by construction, which
is the price paid for keeping the envelope under control, and a real receiver for these formats
equalises. Measured on this analyser with a perfect signal: EDGE reads 35.1 %rms with the equaliser
off and 0.002 %rms with it on. EDGE also needs more passes than the default three — its eight points
seen through that ISI take more than one round of decisions to become trustworthy — and the
measurement says so when it runs out of them. MSK's pulse is zero at every symbol instant but its
own, so it needs neither.

## Filter span, and what it costs

The span is how far the filter reaches either side of its centre, in symbols. It is yours to set,
and the default is **8 symbols**, which is the shortest this analyser recommends for a root raised
cosine.

A root raised cosine cut off after a few symbols is no longer the filter whose cascade with its
matched pair is a Nyquist pulse, so what is left over is **intersymbol interference** — a real error
in the measurement, not a rounding difference. Measured on this analyser, transmit and receive spans
matched, at 16 samples a symbol:

| span (symbols) | EVM |
|---|---|
| 6 | 0.287 %rms |
| 8 | 0.212 %rms |
| 10 | 0.273 %rms |
| 12 | 0.139 %rms |
| 16 | 0.098 %rms |
| 20 | 0.020 %rms |

**The trade is not monotone.** Ten is worse than eight, and twenty-four is worse than twenty. The
tail of the pulse changes sign where it is cut, so a longer filter is not automatically a better one
— which is exactly why this table is here rather than a rule of thumb. If you need to measure a
tenth of a per cent, ask for the span that reaches it; if you need the analysis to be quick, know
what the short one costs you.

The cost of a longer span is time: the number of taps, and the work per sample, grow in proportion.

**Changing the span does not change the amplitude you measure.** The filters are normalised so that
an unmodulated carrier passes at exactly the level it arrived at, whatever the span — so you can
change the span to trade accuracy against speed without your absolute readings moving underneath
you.

**Truncation is windowed, not abrupt.** A filter that simply stops at the end of its span rings in
the frequency domain; the taps here are tapered smoothly to zero over the outer eighth at each end,
which puts the stopband sidelobes below what an abrupt cut gives at every span. The taper is a
quarter of the length rather than a full window because a full one would spoil the cascade
relationship above — the two were measured against each other.
