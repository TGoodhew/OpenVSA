namespace OpenVSA.Demod.Signal
{
    /// <summary>
    /// The pulse-shaping filters of <c>REQ-DEM-021</c>'s catalogue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nine types, each selectable for both the measurement role and the reference role
    /// (<c>REQ-DEM-020</c>). What distinguishes them is the shape of one impulse response;
    /// everything else about applying a filter — the span, the window, the normalisation — is the
    /// same for all of them and lives in one place, which is what <c>REQ-DEM-022a</c> asks for.
    /// </para>
    /// <para>
    /// <strong><see cref="Edge"/> is not a Gaussian, and that is the one thing this enumeration
    /// exists to make impossible to get wrong.</strong> The requirement says so in a box of its own,
    /// because a Gaussian at some fitted BT looks near enough to pass a plot and is not the pulse
    /// any EDGE transmitter sends.
    /// </para>
    /// </remarks>
    public enum PulseFilterType
    {
        /// <summary>Root raised cosine, with roll-off α. The receiver's half of a Nyquist pair.</summary>
        RootRaisedCosine = 0,

        /// <summary>
        /// No shaping at all: the waveform passes through unchanged.
        /// </summary>
        /// <remarks>
        /// An entry in the catalogue rather than a step being skipped, which is why the chain still
        /// records step 5 as having run when this is chosen. Second in this enumeration because it
        /// was second when there were only two, and the numbers are stored in state files.
        /// </remarks>
        None = 1,

        /// <summary>Raised cosine, with roll-off α. The full Nyquist pulse a matched pair composes to.</summary>
        RaisedCosine = 2,

        /// <summary>Gaussian, with bandwidth–time product BT. The GSM/GMSK family's shaping.</summary>
        Gaussian = 3,

        /// <summary>
        /// The linearised-GMSK main pulse <em>c₀(t)</em> of 3GPP TS 45.004 — EDGE's transmit pulse.
        /// </summary>
        /// <remarks>
        /// The principal component of the Laurent decomposition of GMSK, and a distinct filter with
        /// no free parameter. <strong>Not a Gaussian at any BT</strong>: a test measures how far the
        /// nearest one is, and it is nowhere near.
        /// </remarks>
        Edge = 4,

        /// <summary>
        /// Half sine: one half-period of a cosine across a symbol.
        /// </summary>
        /// <remarks>
        /// The shaping of offset QPSK's staggered axes, and — read at half the bit rate, where a
        /// symbol is two bits — of MSK in its offset form. It is <strong>not</strong> the pulse to
        /// shape MSK with at the bit rate: that one spans two symbols and is <see cref="Msk"/>.
        /// Shaping π/2-BPSK with this instead produces a waveform whose envelope reaches zero at
        /// every symbol boundary, which demodulates perfectly and is not a constant-envelope
        /// modulation at all.
        /// </remarks>
        HalfSine = 5,

        /// <summary>Rectangular: unity across one symbol and zero outside it.</summary>
        Rectangular = 6,

        /// <summary>
        /// An ideal low-pass, with its cutoff given as a fraction of the symbol rate.
        /// </summary>
        /// <remarks>
        /// A brick wall in frequency is a sinc in time, so this is the sinc — and at the default
        /// cutoff of half the symbol rate it is exactly the Nyquist sinc, which is the raised cosine
        /// at α = 0. What makes it a useful entry rather than a duplicate is that the cutoff moves:
        /// a low-pass narrower or wider than the signal is how a filter's effect on a measurement is
        /// demonstrated at all.
        /// </remarks>
        LowPass = 7,

        /// <summary>Taps the user supplied, at a stated number of samples per symbol.</summary>
        UserDefined = 8,

        /// <summary>
        /// The linearised-MSK main pulse: half a cycle of a cosine across <em>two</em> symbols.
        /// </summary>
        /// <remarks>
        /// <para>
        /// What <see cref="Edge"/> is to GMSK, this is to MSK — the principal component of the same
        /// decomposition, for the case where there is no Gaussian filter in front of the modulator.
        /// Shaping π/2-BPSK with it produces a waveform whose envelope is constant to two tenths of
        /// a decibel, which is what makes it MSK rather than something that merely demodulates like
        /// it.
        /// </para>
        /// <para>
        /// It is zero at every symbol instant but its own, so it carries no intersymbol interference
        /// and needs no equaliser — unlike <see cref="Edge"/>, whose Gaussian origin spreads it over
        /// about three symbols and puts ISI in the signal by construction.
        /// </para>
        /// <para>
        /// Last in this enumeration because the numbers are stored in state files: a value appended
        /// is a value no older file contains, and a file written with it is refused by name rather
        /// than misread by an older build.
        /// </para>
        /// </remarks>
        Msk = 9,
    }

    /// <summary>
    /// Which of a demodulation's two filter positions a filter is being built for
    /// (<c>REQ-DEM-020</c>).
    /// </summary>
    /// <remarks>
    /// The two are independent in type and in parameter, and they are also normalised differently —
    /// which is the half of <c>REQ-DEM-022a</c> that has to be stated rather than derived. See
    /// <see cref="PulseFilter.Taps"/>.
    /// </remarks>
    public enum FilterRole
    {
        /// <summary>Applied to the acquired signal at step 5: the receiver's filter.</summary>
        Measurement = 0,

        /// <summary>Shapes the ideal waveform at step 10: what the measurement is compared against.</summary>
        Reference = 1,
    }
}
