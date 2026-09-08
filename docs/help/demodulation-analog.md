# Analog demodulation

AM, FM and PM demodulation, beside the digital formats. This page says what each detector measures,
what the numbers beside it mean, and — importantly — **which of them are interpretations rather than
things the specification settled**, so you know what is safe to rely on and what may change.

## The three detectors

| Type | What is measured | The recovered audio is |
|---|---|---|
| **AM** | the envelope, `\|z\|` | a fraction of the carrier |
| **FM** | the rate of change of phase | hertz about the mean |
| **PM** | the phase, with the carrier's ramp removed | radians |

All three are exact rather than approximate, and that is worth knowing because it is what the
accuracy figures rest on.

- The envelope of `A(1 + m·cos)·e^(jθ)` is `A|1 + m·cos|`. There is no approximation in taking a
  magnitude.
- The frequency is the angle of `z[n]·conj(z[n−1])` — the phase advance across one sample, already
  in the right interval. **It needs no unwrapping and cannot slip a cycle**, which a difference of
  two arctangents can.
- Phase modulation genuinely needs unwrapping, since the phase *is* the signal, but it is done by
  accumulating those same wrapped differences. An unwrap done that way cannot pick the wrong branch.

**The one real cost.** The angle across a sample interval is the *average* frequency over it, not
the instantaneous frequency at its end. That scales a sinusoidal deviation by `sinc(π·f_m/f_s)` —
four parts per million for a 1 kHz rate sampled at 200 kHz. It is a **linear scaling, not a
distortion**: it moves the deviation figure very slightly and leaves SINAD untouched. At audio rates
and sensible sample rates it is far below anything you would notice, but it is a real bias and it
grows if you sample close to the modulation rate.

## What is measured

Depth, deviation, rate, SINAD, distortion, residual FM and AM, and the carrier's own mean frequency
and amplitude.

**AM depth** is `(max − min)/(max + min)` of the envelope, in per cent. That is the textbook
definition and it is exact for a sinusoidally modulated carrier — an envelope of `A(1 + m·cos)` has a
maximum of `A(1 + m)` and a minimum of `A(1 − m)`, whose ratio is `m` **with the carrier amplitude
cancelled out**. That cancellation is why it is preferred to the peak of the recovered audio, which
would have to be divided by a separately estimated carrier level.

**A metric that does not apply reads `n/a`, not zero.** An AM measurement has no FM deviation, and
0 Hz would be a *result* rather than the absence of one.

**Carrier frequency and amplitude are always available**, whatever you demodulated. Both detectors
compute them on the way to any of the three, so withholding them would be hiding a trace already
produced — and seeing the envelope beside the frequency is how incidental AM on an FM signal is
found.

## SINAD, and the ceiling you will hit

SINAD is the whole recovered audio against everything in it that is not the fundamental: noise **and**
distortion together, which is what the S/(N+D) of the name means. Distortion is reported separately
and counts only the harmonics.

**On a clean simulated signal all three detectors return about 88 dB, and that number is the analysis
window, not the detector.** The spectrum is taken through a Blackman-Harris window whose sidelobes
are 92 dB down, and a measurement of how much power is *not* in the fundamental cannot see past its
own window's leakage. So 88 dB is a floor under the noise, not a statement about the signal.

> ### ⚠ An interpretation, not a specification
>
> **The window choice decides whether the requirement is meetable at all.** `REQ-DEM-010a` asks for
> SINAD above 60 dB on a clean signal. A Hann window — the usual general-purpose default, and the
> analyser's own default for spectrum work is Flat Top — has sidelobes 31.5 dB down and would put a
> ceiling near **40 dB** on any SINAD measurement. **No detector whatever could meet a 60 dB
> requirement through it.**
>
> Blackman-Harris was chosen for that reason. It is a defensible engineering choice and it is not
> one the specification made. If it is ever revisited, the consequence is not a slightly different
> number: it is whether the requirement can be met.

If you need to know a real signal's SINAD rather than the instrument's floor, the figure is valid
wherever it is comfortably below 88 dB — it tracks injected noise at 20 dB per decade of amplitude,
as it should.

## Residual FM and residual AM

The carrier's own noise and instability: **what is left when the modulation is taken out.** The
fundamental and its harmonics are removed from the audio spectrum and the rest is reported — in hertz
rms for FM, in per cent for AM. On an unmodulated carrier that is the whole of the deviation, which
is the case the figure is usually quoted for.

> ### ⚠ An interpretation, not a specification
>
> `REQ-DEM-010a` names residual FM/AM as a required result and does **not** define it. "Remove the
> fundamental and its first five harmonics" is this build's reading.
>
> The harmonic count is the same setting that bounds distortion (`HarmonicsForDistortion`,
> default 5), deliberately: the two metrics then cannot disagree about which bins are the modulation
> and which are the carrier's own. Five reaches the low-order distortion a detector or a transmitter
> actually produces, and stops well short of summing mostly noise.

## Post-detection filtering

De-emphasis first, then the high-pass, then the low-pass.

De-emphasis belongs to the signal's own definition — it undoes what the transmitter did — so it comes
before any choice the measurement makes. The high-pass then removes the carrier's slow wander before
the low-pass sets the audio bandwidth, so a large slow term is gone before anything else about the
band is decided.

**De-emphasis is named by time constant because that is how the standards state it**: 75 µs in the
Americas and Korea, 50 µs elsewhere. A time constant is a single pole, so 75 µs is a corner at
2122 Hz and a tone there comes out 3.01 dB down. It is a single pole and nothing sharper, because
anything sharper would not be the network the standards specify. It is applied **forwards only**,
keeping its phase response, which is also part of what it is — a zero-phase forward-and-back pass
would square the magnitude response and halve the time constant, giving a filter that is not 75 µs
de-emphasis under a name saying it is.

**Every filter changes the numbers.** A deviation measured through a de-emphasis network is a
deviation *of the filtered audio*, so the metrics carry a flag saying filtering was applied and the
summary says so in words. Comparing a filtered figure with an unfiltered one is comparing two
different measurements. That is also why all three are **off by default**.

A high-pass set above the low-pass passes nothing, and is refused rather than reported as a signal
with no power in it.

## Traces

Four: the demodulated waveform against time, its spectrum, the carrier's frequency against time, and
the carrier's amplitude against time. The requirement asks for three and calls the last "carrier
frequency/amplitude versus time"; that is two series, so it is offered as two traces.
