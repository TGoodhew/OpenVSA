"""The EDGE pulse c0(t), evaluated from 3GPP TS 45.004 subclause 3.5 and nothing else.

This exists so that the C# filter is checked against the SPECIFICATION rather than against
itself. It is written from the published definition, in a different language, with a different
numerical method (quadrature rather than a closed form), and it emits a table of coefficients the
test compares against. If the two agree to 1e-6 across the pulse, two independent readings of the
standard agree; if they do not, one of them is wrong and the disagreement says where.

    c0(t) = product over i = 0..3 of S(t + iT),   for 0 <= t <= 5T, and 0 otherwise

    S(t) = sin(pi * integral from 0 to t of g)              for 0    <= t <= 4T
         = sin(pi/2 - pi * integral from 0 to t-4T of g)    for 4T   <= t <= 8T
         = 0                                                otherwise

    g(t) = 1/(2T) * [ Q(2*pi*0.3*(t - 5T/2) / (T*sqrt(ln 2)))
                    - Q(2*pi*0.3*(t - 3T/2) / (T*sqrt(ln 2))) ]

    Q(t) = 1/sqrt(2*pi) * integral from t to infinity of exp(-tau^2/2) dtau

BT = 0.3 is the 0.3 in g. The pulse is causal over [0, 5T] in the standard's own time reference;
the filter this project applies is centred, so the table below is c0(t + 5T/2) over |t| <= 5T/2.
"""

import math

import numpy as np

T = 1.0
BT = 0.3


def q(x):
    """The upper-tail Gaussian, 1/sqrt(2pi) * integral_x^inf exp(-tau^2/2) dtau."""
    return 0.5 * math.erfc(x / math.sqrt(2.0))


def g(t):
    a = 2.0 * math.pi * BT / (T * math.sqrt(math.log(2.0)))
    return (q(a * (t - 2.5 * T)) - q(a * (t - 1.5 * T))) / (2.0 * T)


START = -6.0
"""Where the phase integral is started, in symbol periods.

The standard writes the lower limit as 0. Taken literally that makes the pulse asymmetric by
1.2e-4, because g(0) is 1.7e-4 rather than zero -- while the mathematics says c0 is exactly
symmetric about 5T/2 (g is symmetric about 2T, so S is symmetric about 4T, and the product of four
shifted copies reflects onto itself). Starting six symbols lower, where g has fallen to about 1e-30,
gives symmetry to 2.7e-14. The two readings differ by up to 6.1e-5 -- above the 1e-6 the acceptance
criterion is stated at -- so the choice is deliberate and both are printed below."""


def integral_g(upper, steps=4096, lower=None):
    """Integral of g from 0 to upper by composite Simpson, written out rather than imported.

    Simpson on a smooth integrand converges as h^4; at 4096 panels over at most 4T the residual is
    far below the 1e-6 the comparison is made at, and doubling the panel count changes nothing in
    the twelfth decimal -- checked."""
    lo = START if lower is None else lower

    if upper <= lo:
        return 0.0

    if steps % 2:
        steps += 1

    h = (upper - lo) / steps
    total = g(lo) + g(upper)

    for k in range(1, steps):
        total += g(lo + k * h) * (4.0 if k % 2 else 2.0)

    return total * h / 3.0


def s(t, lower=None):
    if 0.0 <= t <= 4.0 * T:
        return math.sin(math.pi * integral_g(t, lower=lower))
    if 4.0 * T < t <= 8.0 * T:
        return math.sin(math.pi / 2.0 - math.pi * integral_g(t - 4.0 * T, lower=lower))
    return 0.0


def c0(t, lower=None):
    """The pulse in the standard's own time reference: non-zero on [0, 5T]."""
    if t < 0.0 or t > 5.0 * T:
        return 0.0
    product = 1.0
    for i in range(4):
        product *= s(t + i * T, lower=lower)
    return product


def centred(tau, lower=None):
    """The same pulse centred on zero, which is how a filter is applied."""
    return c0(tau + 2.5 * T, lower=lower)


if __name__ == "__main__":
    # T/8 over the whole support: 41 points, fine enough that a test interpolates nothing and few
    # enough that the table can sit in the test itself, where a reader can see what is being
    # compared against rather than being told a file exists.
    steps = 8
    taus = np.arange(-2.5 * steps, 2.5 * steps + 1) / steps

    values = np.array([centred(t) for t in taus])
    literal = np.array([centred(t, lower=0.0) for t in taus])

    print("# EDGE pulse c0(t), 3GPP TS 45.004 subclause 3.5, BT = 0.3")
    print("# Evaluated from the published definition alone: composite Simpson on g, nothing from")
    print("# any library beyond erfc, and no OpenVSA code. Column 1 is t/T, column 2 is c0(t+5T/2).")
    print("#")
    print("# THE LOWER LIMIT OF THE PHASE INTEGRAL IS A READING OF THE STANDARD:")
    print("#   from %+.0fT (this table): peak %.12f, asymmetry %.3e"
          % (START, values.max(), np.max(np.abs(values - values[::-1]))))
    print("#   from 0, as written:       peak %.12f, asymmetry %.3e"
          % (literal.max(), np.max(np.abs(literal - literal[::-1]))))
    print("#   the two differ by up to %.3e, against a 1e-6 criterion"
          % np.max(np.abs(values - literal)))
    print("# g(0) = %.6e per T, which is why the limit matters at all" % g(0.0))
    print("# area %.12f" % np.trapezoid(values, taus))
    for t, v in zip(taus, values):
        print("%+.6f	%.12f" % (t, v))
