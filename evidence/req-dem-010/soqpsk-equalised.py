"""Can an equaliser rescue the linear model of SOQPSK? (REQ-DEM-010, issue #440)

soqpsk-pulse.py established that the principal Laurent pulse c0, driven by the ternary
impulses of IRIG 106 equation 2-10, models the true SOQPSK-TG waveform to 13.21 %rms --
and that a binary alphabet through the same arithmetic comes back at 0.000000 %rms. The
alphabet is the fault, not the pulse.

This asks the question that decides which route #440 takes, and which nobody had measured.
OpenVSA's chain HAS an equaliser, and its whole job is to fit out intersymbol interference a
pulse leaves behind. If the 13.21 % is ISI, the equaliser eats it, and SOQPSK can be
demodulated as offset QPSK through a pulse derived entirely from the standard -- no trellis,
no change to REQ-DEM-001's fourteen enforced steps, and no detection pulse taken from the
literature. That would settle #440 without needing either of the decisions it asks for.

THE EXPERIMENT IS AN UPPER BOUND, DELIBERATELY. What is fitted here is the least-squares FIR
of a given length, solved in closed form over the whole record with perfect knowledge of the
true waveform. No adaptive algorithm can beat it, and OpenVSA's cannot come close: its
equaliser is decision-directed, sees one block at a time, and converges TOWARDS this answer
at best. So a failure here is conclusive; a success would only be permission to try the real
thing in the chain.

Run from this directory:  python soqpsk-equalised.py
"""

import math

import numpy as np

# The module's file name has a hyphen and cannot be imported, so it is executed into a
# namespace of its own. That is deliberate beyond the mechanics: this measurement then runs
# against THAT script's definitions of the standard rather than a second implementation of
# them, which could be wrong in the same direction and agree with itself.
_ns = {"__name__": "soqpsk_pulse_loaded"}

with open("soqpsk-pulse.py", "r", encoding="utf-8") as handle:
    exec(compile(handle.read(), "soqpsk-pulse.py", "exec"), _ns)

phase = _ns["phase"]
c0_causal = _ns["c0_causal"]
precode = _ns["precode"]
sampled = _ns["sampled"]
waveforms = _ns["waveforms"]
compare = _ns["compare"]
L = _ns["L"]


def equalise(truth, model, per_bit, guard, taps):
    """The best linear equaliser of a given length, and what it leaves behind.

    Fitted from the MODEL to the TRUTH: it is asked to turn the linear reconstruction into
    the waveform that was actually transmitted, which is the direction OpenVSA's equaliser
    works in when it corrects a measurement towards its reference.

    Complex least squares over the whole record, solved in one step. See the module
    docstring for why that is the point rather than a shortcut.
    """
    edge = guard * per_bit
    a = truth[edge:-edge]
    b = model[edge:-edge]

    half = taps // 2
    design = np.stack(
        [np.roll(b, lag) for lag in range(-half, taps - half)], axis=1)

    gram = design.conj().T @ design
    cross = design.conj().T @ a

    weights = np.linalg.solve(gram + 1e-12 * np.eye(taps, dtype=complex), cross)
    residual = a - (design @ weights)

    evm = 100.0 * math.sqrt(np.vdot(residual, residual).real / np.vdot(a, a).real)

    return evm, weights


def rectangular(per_bit, alpha):
    """The MSK case: a rectangular frequency pulse of one bit period, where Laurent is exact.

    Copied from soqpsk-pulse.py's own control so the two measure the same thing.
    """
    q_grid = np.linspace(0.0, 1.0, 4001)
    q_value = q_grid * 0.5

    c_grid = np.linspace(0.0, 2.0, 8001)
    c_value = np.where(
        c_grid <= 1.0,
        np.sin(math.pi * c_grid * 0.5),
        np.sin((math.pi / 2.0) - (math.pi * (c_grid - 1.0) * 0.5)))

    return waveforms(alpha, per_bit, q_grid, q_value, c_grid, c_value, 1.0)


def sweep(truth, model, per_bit, guard, plain, label):
    print("  %s" % label)
    print("    unequalised                 %9.4f %%rms" % plain)

    best = plain

    for taps in (3, 5, 9, 17, 33, 65, 129):
        evm, _ = equalise(truth, model, per_bit, guard, taps)
        best = min(best, evm)

        # The fraction is meaningless when there was no error to begin with -- the control
        # divides ~1e-14 by ~1e-16 and prints six figures of nothing. Said in words instead.
        if plain > 0.01:
            print("    %4d-tap least squares      %9.4f %%rms   (%5.1f %% of the error left)" % (
                taps, evm, 100.0 * evm / plain))
        else:
            print("    %4d-tap least squares      %9.4f %%rms   (nothing to remove)" % (
                taps, evm))

    print()

    return best


def main():
    per_bit = 32
    guard = 12

    rng = np.random.default_rng(20260907)
    bits = rng.choice([-1.0, 1.0], size=2000)
    alpha = precode(bits)

    print("SOQPSK-TG -- IRIG Standard 106-15 (Part 1), Chapter 2, subclause 2.4.3.2")
    print("%d bits at %d samples a bit; guard %d bits at each end." % (
        len(bits), per_bit, guard))
    print()

    # ------------------------------------------------------------------ the harness control
    #
    # BEFORE ASKING THE REAL QUESTION, CHECK THAT THE INSTRUMENT READS ZERO ON ZERO. A binary
    # alphabet through a rectangular pulse is MSK, where Laurent has one term and is exact, so
    # the model already IS the truth and a fitted equaliser must find a delta and improve
    # nothing. An equaliser harness that "improved" this would be fitting noise, and every
    # number below it would be worthless.
    print("CONTROL -- MSK, where the linear model is exact and there is nothing to fix:")
    print()

    truth_b, model_b = rectangular(per_bit, bits)
    plain_b, _ = compare(truth_b, model_b, per_bit, 8)

    sweep(truth_b, model_b, per_bit, 8, plain_b, "binary impulses, rectangular pulse")

    # ------------------------------------------------------------------ the real question
    q_grid, q_value = sampled(phase, 0.0, float(L), 4001)
    c_grid, c_value = sampled(c0_causal, 0.0, float(L + 1), 8001)

    truth, model = waveforms(
        alpha, per_bit, q_grid, q_value, c_grid, c_value, (L + 1) / 2.0)

    plain, envelope = compare(truth, model, per_bit, guard)

    print("THE QUESTION -- SOQPSK-TG's own pulse, ternary impulses from equation 2-10:")
    print()
    print("    the model's envelope varies %.3f dB on a waveform that is constant by" % envelope)
    print("    definition, which is the mismatch the equaliser is being asked to remove.")
    print()

    best = sweep(truth, model, per_bit, guard, plain, "ternary impulses, SOQPSK-TG pulse")

    # ------------------------------------------------------------------ what it means
    print("VERDICT")
    print()
    print("  Best any linear equaliser could do: %.4f %%rms." % best)
    print()

    if best < 1.0:
        print("  The mismatch IS linear intersymbol interference, and the chain's equaliser")
        print("  can be expected to remove it. SOQPSK can be demodulated through a pulse")
        print("  taken entirely from the standard.")
    else:
        print("  The mismatch is NOT linear intersymbol interference and no equaliser removes")
        print("  it. That follows from what it is: the ternary alphabet's zeros make adjacent")
        print("  pulse tails add in phase, and WHICH symbols do that depends on the data. A")
        print("  linear filter applies the same response to every symbol by definition, so it")
        print("  cannot undo an error that changes with the symbol sequence.")
        print()
        print("  So the standard-derived c0 does not give a demodulator, and #440's two routes")
        print("  are still the only two. This measurement removes the third.")


if __name__ == "__main__":
    main()
