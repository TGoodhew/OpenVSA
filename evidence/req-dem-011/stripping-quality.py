"""How strong a line each constellation leaves when it is raised to its symmetry.

Step 3 of the chain strips the modulation by raising the signal to the power of the
constellation's rotational symmetry, which works because every symbol's contribution then lands at
the same angle. How well it works is a property of the POINT LIST and can be computed from it:

    quality = |sum of z^M| / sum of |z|^M

One when every point's M-th power has the same phase, and zero when they cancel. Run against every
format in the catalogue plus the 32-APSK of REQ-DEM-011's acceptance criterion.
"""

import cmath
import math


def normalise(points):
    power = sum(abs(z) ** 2 for z in points) / len(points)
    return [z / math.sqrt(power) for z in points]


def symmetry(points, order_limit=None):
    """The largest m for which turning the set by 2*pi/m leaves it looking the same."""
    limit = order_limit or len(points)
    for m in range(limit, 1, -1):
        turn = cmath.exp(1j * 2 * math.pi / m)
        if all(any(abs(z * turn - w) < 1e-9 for w in points) for z in points):
            return m
    return 1


def quality(points, m):
    top = abs(sum(z ** m for z in points))
    bottom = sum(abs(z) ** m for z in points)
    return top / bottom if bottom else 0.0


def psk(order):
    return normalise([cmath.exp(1j * 2 * math.pi * k / order) for k in range(order)])


def qpsk():
    u = 1 / math.sqrt(2)
    return normalise([complex(u, u), complex(-u, u), complex(-u, -u), complex(u, -u)])


def square_qam(bits):
    side = 1 << (bits // 2)
    return normalise([
        complex(2 * i - side + 1, 2 * q - side + 1)
        for i in range(side) for q in range(side)
    ])


def cross_qam(bits):
    order = 1 << bits
    side = 3 * (1 << ((bits - 3) // 2))
    surplus = side * side - order
    corner = round(math.sqrt(surplus / 4))
    pts = []
    for i in range(side):
        for q in range(side):
            in_corner = (i < corner or i >= side - corner) and (q < corner or q >= side - corner)
            if not in_corner:
                pts.append(complex(2 * i - side + 1, 2 * q - side + 1))
    return normalise(pts)


def star_qam(order, rings=2, ratio=2.0):
    per = order // rings
    pts = []
    radius = 1.0
    for _ in range(rings):
        pts += [radius * cmath.exp(1j * 2 * math.pi * k / per) for k in range(per)]
        radius *= ratio
    return normalise(pts)


def apsk(spec):
    pts = []
    for radius, count, phase in spec:
        pts += [radius * cmath.exp(1j * (phase + 2 * math.pi * k / count)) for k in range(count)]
    return normalise(pts)


CASES = [
    ("BPSK", psk(2)),
    ("QPSK", qpsk()),
    ("8PSK", psk(8)),
    ("16PSK", psk(16)),
    ("OOK", normalise([0j, 1 + 0j])),
    ("16QAM", square_qam(4)),
    ("64QAM", square_qam(6)),
    ("256QAM", square_qam(8)),
    ("1024QAM", square_qam(10)),
    ("4096QAM", square_qam(12)),
    ("32QAM (cross)", cross_qam(5)),
    ("128QAM (cross)", cross_qam(7)),
    ("2048QAM (cross)", cross_qam(11)),
    ("16STARQAM", star_qam(16)),
    ("32STARQAM", star_qam(32)),
    ("32APSK 4/12/16", apsk([
        (1.0, 4, math.pi / 4),
        (2.64, 12, math.pi / 12),
        (4.64, 16, math.pi / 16),
    ])),
    ("16APSK 4/12", apsk([(1.0, 4, math.pi / 4), (2.72, 12, 0.0)])),
]

print(f"{'format':>18}  {'M':>3}  {'quality':>10}")
for name, pts in CASES:
    m = symmetry(pts)
    print(f"{name:>18}  {m:>3}  {quality(pts, m):>10.6f}")
