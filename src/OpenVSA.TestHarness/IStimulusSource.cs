using System;

namespace OpenVSA.TestHarness
{
    /// <summary>
    /// A signal generator driven to a known state, so that a measurement can be checked against it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Deliberately not <c>IFrontEnd</c>.</strong> That interface is the measurement path —
    /// the thing under test. A generator behind it could feed synthesised data straight into the
    /// DSP layer without ever crossing an instrument, which is exactly the shortcut this harness
    /// exists to prevent. The two sides of the bench have separate contracts because they are
    /// separate things.
    /// </para>
    /// <para>
    /// Small on purpose. A verification scenario needs a carrier at a known frequency and level and
    /// the ability to turn it off; anything more belongs to the scenario that wants it.
    /// </para>
    /// </remarks>
    public interface IStimulusSource : IDisposable
    {
        /// <summary>How the source identifies itself.</summary>
        string DisplayName { get; }

        /// <summary>Whether the output is currently enabled.</summary>
        bool IsOutputEnabled { get; }

        /// <summary>Carrier frequency, in hertz.</summary>
        double FrequencyHz { get; }

        /// <summary>Output level, in dBm.</summary>
        double LevelDbm { get; }

        /// <summary>Connects to the source and reads its identity.</summary>
        void Connect();

        /// <summary>
        /// Sets an unmodulated carrier.
        /// </summary>
        /// <param name="frequencyHz">Carrier frequency, in hertz.</param>
        /// <param name="levelDbm">Output level, in dBm.</param>
        /// <remarks>
        /// Modulation is switched off explicitly rather than assumed off. A generator left in a
        /// modulated state by whoever used it last produces a spread spectrum where the scenario
        /// expects a tone, and the failure looks like a defect in the analyser.
        /// </remarks>
        void SetContinuousWave(double frequencyHz, double levelDbm);

        /// <summary>Enables or disables the output.</summary>
        /// <param name="enabled">Whether RF should be on.</param>
        void SetOutput(bool enabled);

        /// <summary>Reads back what the source says its state is now.</summary>
        /// <remarks>
        /// Read back rather than remembered. A scenario that records what it asked for and reports
        /// that as the reference has verified the analyser against its own intentions.
        /// </remarks>
        void Refresh();
    }
}
