"""Does windowed truncation break REQ-DEM-022a's cascade identity?

REQ-DEM-023 requires windowed truncation, "not abrupt", with stopband sidelobes below those of a
rectangularly truncated filter of the same span. REQ-DEM-022a requires a cascade of two RRC filters
to match the corresponding RC filter, and quotes floors it says are set by truncation alone:

    +/-8 sym -> 5.4e-4,  +/-16 -> 1.1e-4,  +/-32 -> 1.1e-5,  +/-64 -> 3.2e-6

Those floors are rectangular truncation. A window changes the taps, so it must change the cascade
error too, and the question is by how much — whether the two requirements can both be met, or
whether one of them has to give.

Run with no arguments. Nothing here imports the implementation under test.
"""

import math

import numpy as np

SPS = 16


def rrc(t, alpha):
    if abs(t) < 1e-12:
        return 1.0 + alpha * (4.0 / math.pi - 1.0)
    if alpha > 1e-12 and abs(abs(t) - 1.0 / (4.0 * alpha)) < 1e-12:
        a = math.pi / (4.0 * alpha)
        return (alpha / math.sqrt(2.0)) * (
            (1.0 + 2.0 / math.pi) * math.sin(a) + (1.0 - 2.0 / math.pi) * math.cos(a))
    num = math.sin(math.pi * t * (1.0 - alpha)) + 4.0 * alpha * t * math.cos(
        math.pi * t * (1.0 + alpha))
    return num / (math.pi * t * (1.0 - (4.0 * alpha * t) ** 2))


def rc(t, alpha):
    if abs(t) < 1e-12:
        sinc = 1.0
    else:
        sinc = math.sin(math.pi * t) / (math.pi * t)
    if alpha < 1e-12:
        return sinc
    if abs(abs(t) - 1.0 / (2.0 * alpha)) < 1e-12:
        # The analytic limit: (pi/4) * sinc(1/(2*alpha)).
        u = 1.0 / (2.0 * alpha)
        return (math.pi / 4.0) * (math.sin(math.pi * u) / (math.pi * u))
    return sinc * math.cos(math.pi * alpha * t) / (1.0 - (2.0 * alpha * t) ** 2)


def taps(fn, alpha, span, window):
    half = span * SPS
    t = (np.arange(2 * half + 1) - half) / SPS
    h = np.array([fn(x, alpha) for x in t])
    return h * window(len(h))


def rectangular(n):
    return np.ones(n)


def blackman(n):
    return np.blackman(n)


def hann(n):
    return np.hanning(n)


def tukey(fraction):
    def build(n):
        w = np.ones(n)
        taper = int(fraction * (n - 1) / 2.0)
        if taper < 1:
            return w
        edge = 0.5 * (1.0 - np.cos(np.pi * np.arange(taper) / taper))
        w[:taper] = edge
        w[n - taper:] = edge[::-1]
        return w
    return build


def cascade_error(alpha, span, window):
    """RMS difference between RRC (*) RRC and RC, over the RC's own span."""
    h = taps(rrc, alpha, span, window)
    composite = np.convolve(h, h) / SPS

    half = (len(composite) - 1) // 2
    t = (np.arange(len(composite)) - half) / SPS
    ideal = np.array([rc(x, alpha) for x in t])

    keep = np.abs(t) <= span
    return float(np.sqrt(np.mean((composite[keep] - ideal[keep]) ** 2)))


def worst_sidelobe(alpha, span, window):
    """Highest stopband sidelobe, in dB relative to the peak of the response."""
    h = taps(rrc, alpha, span, window)
    spectrum = np.abs(np.fft.rfft(h, 1 << 16))
    spectrum /= spectrum.max()

    freq = np.fft.rfftfreq(1 << 16, d=1.0 / SPS)
    stop = freq > (1.0 + alpha) / 2.0 * 1.6
    return float(20.0 * np.log10(spectrum[stop].max()))


if __name__ == "__main__":
    alpha = 0.35
    windows = [
        ("rectangular", rectangular),
        ("tukey 0.10", tukey(0.10)),
        ("tukey 0.25", tukey(0.25)),
        ("hann", hann),
        ("blackman", blackman),
    ]

    print(f"alpha {alpha}, {SPS} samples/symbol")
    print()
    print("cascade RRC*RRC against RC, RMS over +/- span:")
    print(f"{'window':>14}" + "".join(f"{'+/-' + str(s):>12}" for s in (8, 16, 32, 64)))
    for name, window in windows:
        row = "".join(f"{cascade_error(alpha, s, window):>12.2e}" for s in (8, 16, 32, 64))
        print(f"{name:>14}{row}")

    print()
    print("REQ-DEM-022a quotes, for truncation alone:")
    print(f"{'quoted':>14}{5.4e-4:>12.2e}{1.1e-4:>12.2e}{1.1e-5:>12.2e}{3.2e-6:>12.2e}")
    print(f"{'and demands':>14}{1.0e-3:>12.2e}{'':>12}{'':>12}{5.0e-6:>12.2e}")

    print()
    print("worst stopband sidelobe of the truncated RRC, dB below peak:")
    for name, window in windows:
        row = "".join(f"{worst_sidelobe(alpha, s, window):>12.1f}" for s in (8, 16, 32, 64))
        print(f"{name:>14}{row}")
