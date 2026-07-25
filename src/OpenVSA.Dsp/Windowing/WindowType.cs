namespace OpenVSA.Dsp.Windowing
{
    /// <summary>
    /// The window functions of <c>REQ-DSP-010</c>.
    /// </summary>
    /// <remarks>
    /// The set and its order match the reference product. <see cref="FlatTop"/> is the default
    /// (<c>REQ-DSP-010a</c>) — deliberately, and it must stay that way: it surprises users who
    /// expect Hann, but amplitude accuracy over resolution is the documented behaviour being
    /// cloned.
    /// </remarks>
    public enum WindowType
    {
        /// <summary>Rectangular. ENBW 1.0000, peak sidelobe −13.3 dB. For self-windowing and transient signals.</summary>
        Uniform = 0,

        /// <summary>Hann ("Hanning"). ENBW 1.5000, peak sidelobe −31.5 dB. General purpose; unsuitable for bursts and chirps.</summary>
        Hann,

        /// <summary>Gaussian Top. ENBW 2.2153. High dynamic range.</summary>
        GaussianTop,

        /// <summary>Flat Top. ENBW 3.8194. <strong>The default.</strong> Amplitude accuracy over resolution.</summary>
        FlatTop,

        /// <summary>Blackman-Harris, 4-term minimum. ENBW 2.0044, peak sidelobe −92.0 dB.</summary>
        BlackmanHarris,

        /// <summary>Kaiser-Bessel, πα = 11.9. ENBW 2.0013, peak sidelobe −89.1 dB. Faster sidelobe roll-off than Blackman-Harris.</summary>
        KaiserBessel,

        /// <summary>Gaussian, α = 3.58 (σ = 0.1397). ENBW 2.0212, peak sidelobe −73.5 dB. Special purpose only.</summary>
        Gaussian,
    }
}
