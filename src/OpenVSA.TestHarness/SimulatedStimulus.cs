using System;

namespace OpenVSA.TestHarness
{
    /// <summary>
    /// A stimulus source with no instrument behind it, for exercising the harness itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #393 asks for this explicitly: the harness's own logic — scenario sequencing, where
    /// the expectation comes from, how a failure is reported — stays under test in CI even though
    /// the measurements it makes there are not real.
    /// </para>
    /// <para>
    /// <strong>It can be told to lie.</strong> <see cref="CoerceFrequencyTo"/> makes it report a
    /// different carrier from the one it was asked for, which is what a real generator does at the
    /// edge of its range. A harness that took its expectation from the request rather than from
    /// the read-back would pass that and should not.
    /// </para>
    /// </remarks>
    public sealed class SimulatedStimulus : IStimulusSource
    {
        /// <summary>Reports this frequency whatever it is asked for; <c>NaN</c> to obey.</summary>
        public double CoerceFrequencyTo { get; set; } = double.NaN;

        /// <summary>Reports this level whatever it is asked for; <c>NaN</c> to obey.</summary>
        public double CoerceLevelTo { get; set; } = double.NaN;

        /// <inheritdoc />
        public string DisplayName => "Simulated stimulus";

        /// <inheritdoc />
        public bool IsOutputEnabled { get; private set; }

        /// <inheritdoc />
        public double FrequencyHz { get; private set; }

        /// <inheritdoc />
        public double LevelDbm { get; private set; }

        /// <summary>Whether <see cref="Connect"/> has been called.</summary>
        public bool IsConnected { get; private set; }

        /// <inheritdoc />
        public void Connect() => IsConnected = true;

        /// <inheritdoc />
        public void SetContinuousWave(double frequencyHz, double levelDbm)
        {
            FrequencyHz = double.IsNaN(CoerceFrequencyTo) ? frequencyHz : CoerceFrequencyTo;
            LevelDbm = double.IsNaN(CoerceLevelTo) ? levelDbm : CoerceLevelTo;
        }

        /// <inheritdoc />
        public void SetOutput(bool enabled) => IsOutputEnabled = enabled;

        /// <inheritdoc />
        public void Refresh()
        {
        }

        /// <inheritdoc />
        public void Dispose() => IsOutputEnabled = false;
    }
}
