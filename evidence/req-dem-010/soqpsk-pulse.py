"""SOQPSK-TG from the standard, and what a linear receiver can and cannot do with it.

REQ-DEM-010's table has one row left: SOQPSK. This evaluates it from IRIG Standard 106-15
(Part 1), Chapter 2, subclause 2.4.3.2 -- equations 2-5 through 2-10 and Table 2-4 -- and then
measures whether it fits the shape of demodulator OpenVSA has.

WHAT IT SETTLES. Every other format in the catalogue is, in the end, a point list: a symbol is
a place, or a frequency, or an amplitude on one axis, and step 8 fits a model and step 9
decides. Two continuous-phase formats are already in it on that footing -- MSK and GMSK --
because Laurent's decomposition writes a BINARY CPM as a linear pulse driven by pseudo-symbols
that walk around a ring, exactly, and that pulse is what OpenVSA shapes and matches.

SOQPSK is ternary. Equation 2-10 produces impulses in {-1, 0, +1}, and the zero is what breaks
the construction: when the phase does not move, two adjacent pulse tails add IN PHASE rather
than in quadrature, and the linear model's envelope rises by exactly root two on a waveform
that is constant envelope by definition. The control below shows it costs nothing on the same
pulse with a binary alphabet -- 0.000000 %rms, exact to machine precision -- and 14.1 %rms with
the standard's ternary one. It is the alphabet, not the pulse and not the arithmetic.

Run: python soqpsk-pulse.py > soqpsk-pulse.txt
"""

import math

import numpy as np
from scipy.integrate import quad

# Table 2-4. The standard admits exactly one variant.
RHO = 0.70
B = 1.25
T1 = 1.5
T2 = 0.50

# The window sets the support: the pulse is zero beyond +/- (T1 + T2) bit periods.
REACH = T1 + T2

# Laurent's L: the frequency pulse spans this many bit periods.
L = int(round(2.0 * REACH))


def window(t):
    """w(t) of equation 2-7, with t in bit periods."""
    a = abs(t)

    if a <= T1:
        return 1.0

    if a <= T1 + T2:
        return 0.5 * (1.0 + math.cos(math.pi * (a - T1) / T2))

    return 0.0


def spectral_raised_cosine(t):
    """n(t) of equation 2-6 without its amplitude, with t in bit periods."""
    first = RHO * B * t
    second = B * t

    # cos(pi.x) / (1 - 4x^2) is 0/0 at x = +/- 1/2. L'Hopital: the derivatives are
    # -pi.sin(pi.x) and -8x, so the limit is pi/4 at both.
    if abs(abs(first) - 0.5) < 1e-12:
        shaped = math.pi / 4.0
    else:
        shaped = math.cos(math.pi * first) / (1.0 - (4.0 * first * first))

    # sin(pi.x)/(pi.x) is 0/0 at x = 0, where it is 1.
    if abs(second) < 1e-12:
        rolled = 1.0
    else:
        rolled = math.sin(math.pi * second) / (math.pi * second)

    return shaped * rolled


def unnormalised(t):
    return spectral_raised_cosine(t) * window(t)


# Equation 2-8: A is chosen so the pulse integrates to pi/2 -- one impulse of unit amplitude
# advances the phase by a right angle, which is a modulation index of one half.
AREA, _ = quad(unnormalised, -REACH, REACH, limit=400)
AMPLITUDE = (math.pi / 2.0) / AREA


def g(t):
    """The frequency pulse of equation 2-5, in radians per bit period."""
    return AMPLITUDE * unnormalised(t)


def phase(t):
    """Laurent's q(t), causal: the phase integral, normalised to run from 0 to 1/2.

    In the standard's own units the integral runs from 0 to pi/2. Laurent writes the
    decomposition with q(LT) = 1/2 and psi(t) = sin(2.pi.h.q(t)) / sin(pi.h), which for
    h = 1/2 is sin(pi.q(t)); the two differ by the constant 1/pi and nothing else.
    """
    if t <= 0.0:
        return 0.0

    if t >= L:
        return 0.5

    value, _ = quad(lambda x: g(x - REACH), 0.0, t, limit=400)

    return value / math.pi


def s(t):
    """Laurent's psi, the factor c0 is a product of."""
    if t < 0.0 or t > 2 * L:
        return 0.0

    if t <= L:
        return math.sin(math.pi * phase(t))

    return math.sin((math.pi / 2.0) - (math.pi * phase(t - L)))


def c0_causal(t):
    """The principal Laurent pulse, non-zero on 0 <= t <= (L + 1)T."""
    if t < 0.0 or t > L + 1:
        return 0.0

    product = 1.0

    for i in range(L):
        product *= s(t + i)

    return product


def c0(t):
    """The same pulse about its own centre."""
    if abs(t) > (L + 1) / 2.0:
        return 0.0

    return c0_causal(t + ((L + 1) / 2.0))


def precode(bits):
    """Equation 2-10: the ternary impulse series, from antipodal bits."""
    alpha = np.zeros(len(bits))

    for i in range(2, len(bits)):
        alpha[i] = ((-1.0) ** (i + 1)) * bits[i - 1] * (bits[i] - bits[i - 2]) / 2.0

    return alpha


def sampled(fn, low, high, count):
    """A function on a fine grid, so the waveform build is not quadrature-bound."""
    grid = np.linspace(low, high, count)

    return grid, np.array([fn(x) for x in grid])


def waveforms(alpha, per_bit, q_grid, q_value, c_grid, c_value, reach):
    """The true CPM waveform and the linear model of it, on one time base.

    The true one is the standard's own definition: phi(t) = sum of alpha_i . pi . q(t - i),
    and the signal is exp(j.phi), which is constant envelope by construction. The model is
    Laurent's: pseudo-symbols b_k = exp(j.pi/2 . sum of alpha up to k) driving c0.
    """
    total = len(alpha) * per_bit
    grid = np.arange(total) / per_bit

    phi = np.zeros(total)

    for i, a in enumerate(alpha):
        if a == 0.0:
            continue

        phi += a * math.pi * np.interp(grid - i, q_grid, q_value, left=0.0, right=0.5)

    truth = np.exp(1j * phi)

    b = np.exp(1j * math.pi / 2.0 * np.cumsum(alpha))
    model = np.zeros(total, dtype=complex)

    for k in range(len(alpha)):
        model += b[k] * np.interp(
            grid - k, c_grid, c_value, left=0.0, right=0.0)

    return truth, model


def compare(truth, model, per_bit, guard):
    """How far the model is from the truth, after the best gain and phase.

    The complex scale is what a demodulator's gain and carrier-phase fit would find, so
    removing it is the difference between measuring the model and measuring an arbitrary
    reference level.
    """
    edge = guard * per_bit
    a = truth[edge:-edge]
    b = model[edge:-edge]

    scale = np.vdot(b, a) / np.vdot(b, b)
    residual = a - (scale * b)

    evm = 100.0 * math.sqrt(np.vdot(residual, residual).real / np.vdot(a, a).real)

    envelope = 20.0 * math.log10(
        np.max(np.abs(b)) / max(np.min(np.abs(b)), 1e-18))

    return evm, envelope


def control(per_bit, alpha, label):
    """The same construction on a rectangular pulse of one bit period, where it is EXACT.

    L = 1 and h = 1/2 is MSK, and Laurent's decomposition of it has a single term: the
    half cosine across two bit periods that OpenVSA already ships as PulseFilterType.Msk.
    A binary alphabet must therefore come back at zero, and anything it does not come back
    at is the alphabet.
    """
    q_grid = np.linspace(0.0, 1.0, 4001)
    q_value = q_grid * 0.5

    c_grid = np.linspace(0.0, 2.0, 8001)
    c_value = np.where(
        c_grid <= 1.0,
        np.sin(math.pi * c_grid * 0.5),
        np.sin((math.pi / 2.0) - (math.pi * (c_grid - 1.0) * 0.5)))

    truth, model = waveforms(
        alpha, per_bit, q_grid, q_value, c_grid, c_value, 1.0)

    evm, envelope = compare(truth, model, per_bit, 8)

    print("  %-38s EVM %9.6f %%rms   model envelope varies %6.3f dB" % (
        label, evm, envelope))


def main():
    print("SOQPSK-TG -- IRIG Standard 106-15 (Part 1), Chapter 2, subclause 2.4.3.2")
    print("Table 2-4:  rho = %.2f   B = %.2f   T1 = %.1f   T2 = %.2f" % (RHO, B, T1, T2))
    print("The frequency pulse spans +/- %.1f bit periods, so Laurent's L = %d" % (REACH, L))
    print("Amplitude A from equation 2-8 (the integral is pi/2): %.15f" % AMPLITUDE)
    print()

    # The mathematics says c0 is symmetric about its centre: g is symmetric, so the phase
    # integral is antisymmetric about L/2, so psi is symmetric about L, and a product of L of
    # them reflects onto itself. Anything else would mean the phase integral was wrong.
    worst = max(abs(c0(t) - c0(-t)) for t in np.linspace(0.0, 3.0, 601))

    print("Symmetry of c0 about its centre:   %.3e" % worst)
    print("Peak of c0, at its centre:         %.15f" % c0(0.0))
    print("Energy of c0:                      %.15f" % quad(
        lambda t: c0(t) ** 2, -3.0, 3.0, limit=400)[0])
    print()

    print("c0 at 8 samples per bit period, over +/- 2.5:")
    print()

    for step in range(-20, 21):
        t = step / 8.0

        print("  %+7.4f  %.15f" % (t, c0(t)))

    print()
    print("THE CONTROL: a rectangular frequency pulse of one bit period, where Laurent's")
    print("decomposition has a single term and is exact.")
    print()

    rng = np.random.default_rng(20260831)
    per_bit = 32

    control(
        per_bit,
        rng.choice([-1.0, 1.0], size=400),
        "binary impulses in {-1, +1}, which is MSK")

    bits = rng.choice([-1.0, 1.0], size=400)
    alpha = precode(bits)

    control(
        per_bit,
        alpha,
        "ternary impulses from equation 2-10")

    zeros = int(np.sum(alpha == 0.0))

    print()
    print("  %d of the %d impulses are zero -- the phase holds for that bit, and the two" % (
        zeros, len(alpha)))
    print("  neighbouring pulse tails add in phase rather than in quadrature. Root two is")
    print("  3.010 dB, which is what the model's envelope does on a waveform whose envelope")
    print("  is constant by definition.")
    print()
    print("AND THE SAME THING ON SOQPSK-TG'S OWN PULSE:")
    print()

    q_grid, q_value = sampled(phase, 0.0, float(L), 4001)
    c_grid, c_value = sampled(c0_causal, 0.0, float(L + 1), 8001)

    truth, model = waveforms(
        alpha, per_bit, q_grid, q_value, c_grid, c_value, (L + 1) / 2.0)

    evm, envelope = compare(truth, model, per_bit, 12)

    print("  %-38s EVM %9.6f %%rms   model envelope varies %6.3f dB" % (
        "SOQPSK-TG, ternary, L = 4", evm, envelope))

    print()
    print("  True waveform's envelope varies by %.3e dB, as a CPM's must." % (
        20.0 * math.log10(np.max(np.abs(truth)) / np.min(np.abs(truth)))))
    print()
    print("CONCLUSION. Laurent's decomposition is a statement about BINARY continuous-phase")
    print("modulation. SOQPSK's excitation is ternary by definition, so the pulse OpenVSA")
    print("would shape and match does not describe the signal, and no choice of pulse fixes")
    print("it -- the control shows the same construction exact on a binary alphabet and")
    print("wrong by 14 %rms on the ternary one, on the same pulse. Demodulating SOQPSK needs")
    print("either a detector over the modulation's phase states, which the 14-step chain of")
    print("REQ-DEM-001 has no place for, or the ternary PAM decomposition, whose pulse comes")
    print("from the literature rather than from this standard. Both are decisions rather than")
    print("implementations, so the catalogue refuses the name and says which.")


if __name__ == "__main__":
    main()
