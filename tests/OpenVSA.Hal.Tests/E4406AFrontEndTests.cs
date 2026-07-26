using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenVSA.Core;
using OpenVSA.Hal;
using OpenVSA.Hal.Visa;
using Xunit;

namespace OpenVSA.Hal.Tests
{
    /// <summary>
    /// The E4406A driver, against a scripted instrument rather than hardware.
    /// </summary>
    /// <remarks>
    /// Everything the driver decides — command order, where its limits come from, how it forms the
    /// frequency axis, what it does when the instrument disagrees with it — is covered here with no
    /// VISA runtime and no bench. What is left untested until real hardware is present is the
    /// transport, which is the part that cannot be faked usefully.
    /// </remarks>
    public class E4406AFrontEndTests
    {
        [Fact]
        public async Task ItSelectsBasicModeAndTheWaveformMeasurementBeforeAnythingElse()
        {
            // The usual way an instrument driver goes wrong: every command is correct and one is
            // sent before the mode that makes it legal.
            var instrument = new FakeE4406A();
            using (E4406AFrontEnd frontEnd = Connected(instrument))
            {
                await Task.Yield();

                List<string> sent = frontEnd.Sent.ToList();
                int basic = sent.IndexOf(":INSTrument:SELect BASIC");
                int waveform = sent.IndexOf(":CONFigure:WAVeform");
                int bandwidth = sent.FindIndex(c => c.StartsWith(":SENSe:WAVeform:BANDwidth", StringComparison.Ordinal));

                Assert.True(basic >= 0 && waveform > basic);
                Assert.True(bandwidth > waveform, "Bandwidth was set before the measurement was selected.");
            }
        }

        [Fact]
        public async Task ItAsksForBinaryTransferWithSwappedByteOrder()
        {
            var instrument = new FakeE4406A();
            using (E4406AFrontEnd frontEnd = Connected(instrument))
            {
                await Task.Yield();

                Assert.Contains(":FORMat:DATA REAL,32", frontEnd.Sent);
                Assert.Contains(":FORMat:BORDer SWAP", frontEnd.Sent);
            }
        }

        [Fact]
        public async Task ItsLimitsComeFromTheInstrumentRatherThanADatasheet()
        {
            // The instrument here reports limits unlike any real E4406A. A driver that had learned
            // the datasheet's numbers would report those instead.
            var instrument = new FakeE4406A
            {
                MinCentreHz = 1e6,
                MaxCentreHz = 3e9,
                MinBandwidthHz = 10.0,
                MaxBandwidthHz = 4e6,
                MinLevelDbm = -40.0,
                MaxLevelDbm = 10.0,
            };

            using (E4406AFrontEnd frontEnd = Connected(instrument))
            {
                await Task.Yield();

                IFrontEndCapabilities caps = frontEnd.Capabilities;

                Assert.Equal(1e6, caps.CenterFrequencyRange.MinHz, 3);
                Assert.Equal(3e9, caps.CenterFrequencyRange.MaxHz, 3);
                Assert.Equal(10.0, caps.MinSpanHz, 3);
                Assert.Equal(4e6, caps.MaxSpanHz, 3);
            }
        }

        [Fact]
        public async Task TheReferenceLevelIsADisplayRangeBecauseBasicModeAutoRangesTheInput()
        {
            // Found on real hardware: [:SENSe]:POWer[:RF]:RANGe[:UPPer] is documented as needing
            // "the Service, cdmaOne, EDGE(w/GSM), GSM, NADC, PDC, cdma2000, or W-CDMA (3GPP)
            // mode", and Basic is not among them - sent in Basic mode the instrument does not
            // answer at all. So the attenuator is left on auto and no reference level is commanded.
            var instrument = new FakeE4406A();

            using (E4406AFrontEnd frontEnd = Connected(instrument))
            {
                await Task.Yield();

                Assert.Contains(":SENSe:WAVeform:ADC:RANGe AUTO", frontEnd.Sent);
                Assert.DoesNotContain(
                    frontEnd.Sent, c => c.StartsWith(":SENSe:POWer:RF:RANGe", StringComparison.Ordinal));

                // The instrument's own damage limit: "external attenuation required above 30 dBm".
                Assert.Equal(30.0, frontEnd.Capabilities.ReferenceLevelRange.MaxDbm, 3);
            }
        }

        [Fact]
        public async Task TheSampleRateComesFromTheInstrumentsApertureNotFromTheSpan()
        {
            // The point the whole driver turns on: this instrument's rate-to-bandwidth
            // relationship is its own, so the aperture is asked for and never inferred. Here the
            // instrument reports a rate of 1.6x the bandwidth, which is not the product's 1.28 law.
            var instrument = new FakeE4406A { ApertureFor = bandwidth => 1.0 / (bandwidth * 1.6) };

            using (E4406AFrontEnd frontEnd = Connected(instrument))
            {
                AcquisitionPlan plan = frontEnd.Negotiate(
                    new AcquisitionRequest(1e9, 1e6, 4096, -10.0));

                await frontEnd.ConfigureAsync(plan, CancellationToken.None);

                Assert.Equal(1.6e6, frontEnd.SampleRateHz, 0);
            }
        }

        [Fact]
        public async Task EachBlockCarriesTheRateTheInstrumentReported()
        {
            var instrument = new FakeE4406A { ApertureFor = bandwidth => 1.0 / (bandwidth * 1.6) };

            using (E4406AFrontEnd frontEnd = Connected(instrument))
            {
                AcquisitionPlan plan = frontEnd.Negotiate(
                    new AcquisitionRequest(1e9, 1e6, 512, -10.0));

                await frontEnd.ConfigureAsync(plan, CancellationToken.None);
                await frontEnd.ArmAsync(CancellationToken.None);

                using (IqBlock block = await frontEnd.AcquireNextAsync(CancellationToken.None))
                {
                    Assert.Equal(1.6e6, block.SampleRateHz, 0);
                    Assert.Equal(1e9, block.CenterFrequencyHz, 3);
                    Assert.False(block.IsBaseband);

                    // Volts in, so the amplitude chain's full-scale term is one.
                    Assert.Equal(1.0, block.FullScaleVolts, 9);
                }
            }
        }

        [Fact]
        public async Task TheTraceIsReadAsInterleavedIqInVolts()
        {
            var instrument = new FakeE4406A();
            instrument.Trace = new float[] { 0.5f, 0.25f, -0.5f, -0.25f };

            using (E4406AFrontEnd frontEnd = Connected(instrument))
            {
                AcquisitionPlan plan = frontEnd.Negotiate(new AcquisitionRequest(1e9, 1e6, 2, -10.0));
                await frontEnd.ConfigureAsync(plan, CancellationToken.None);
                await frontEnd.ArmAsync(CancellationToken.None);

                using (IqBlock block = await frontEnd.AcquireNextAsync(CancellationToken.None))
                {
                    Assert.Equal(2, block.SampleCount);

                    // "The I values are listed first in each pair, using the 0 and even-indexed
                    // values. The Q values are the odd-indexed values."
                    Assert.Equal(0.5f, block.GetSample(0).I, 6);
                    Assert.Equal(0.25f, block.GetSample(0).Q, 6);
                    Assert.Equal(-0.5f, block.GetSample(1).I, 6);
                    Assert.Equal(-0.25f, block.GetSample(1).Q, 6);
                }
            }
        }

        [Fact]
        public async Task ABandwidthTheInstrumentDidNotHonourIsReported()
        {
            // REQ-HAL-001: "due to memory constraints the actual resolution bandwidth value may
            // vary from the value entered by the user", so the driver asks and says so.
            var instrument = new FakeE4406A { ActualBandwidthFor = wanted => wanted * 0.8 };
            var reported = new List<FrontEndEvent>();

            using (E4406AFrontEnd frontEnd = Connected(instrument))
            {
                frontEnd.Notification += (sender, e) => reported.Add(e);

                AcquisitionPlan plan = frontEnd.Negotiate(new AcquisitionRequest(1e9, 1e6, 512, -10.0));
                await frontEnd.ConfigureAsync(plan, CancellationToken.None);

                Assert.Contains(reported, e => e.Kind == FrontEndEventKind.ParameterCoerced);
                Assert.Equal(0.8e6, frontEnd.ActualBandwidthHz, 0);
            }
        }

        [Fact]
        public async Task AnInstrumentErrorFailsFastRatherThanMeasuringWithTheWrongSetting()
        {
            // A silently ignored command produces plausible data from a configuration nobody asked
            // for, which is worse than an exception.
            var instrument = new FakeE4406A { ErrorReply = "-222,\"Data out of range\"" };

            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new E4406AFrontEnd("FAKE", resource => instrument).ConnectAsync(CancellationToken.None));

            Assert.Contains("Data out of range", failure.Message);
        }

        [Fact]
        public async Task ARealBasebandRequestIsCoercedBecauseThisInstrumentDigitisesAtIf()
        {
            var instrument = new FakeE4406A();

            using (E4406AFrontEnd frontEnd = Connected(instrument))
            {
                await Task.Yield();

                AcquisitionPlan plan = frontEnd.Negotiate(
                    new AcquisitionRequest(1e9, 1e6, 512, -10.0, AnalysisPath.RealBaseband));

                Assert.Equal(AnalysisPath.ComplexZoom, plan.Path);
                Assert.NotNull(plan.CoercionFor("Path"));
                Assert.False(frontEnd.Capabilities.SupportsBasebandIq);
            }
        }

        [Fact]
        public async Task NegotiateSendsNothing()
        {
            // REQ-HAL-001 makes Negotiate pure, so it may be called freely to explore what is
            // achievable without touching the instrument.
            var instrument = new FakeE4406A();

            using (E4406AFrontEnd frontEnd = Connected(instrument))
            {
                await Task.Yield();

                int before = instrument.Received.Count;
                frontEnd.Negotiate(new AcquisitionRequest(2e9, 5e6, 8192, 0.0));

                Assert.Equal(before, instrument.Received.Count);
            }
        }

        [Fact]
        public void NegotiatingBeforeConnectingSaysWhy()
        {
            var frontEnd = new E4406AFrontEnd("FAKE", resource => new FakeE4406A());

            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
                () => frontEnd.Negotiate(new AcquisitionRequest(1e9, 1e6, 512, -10.0)));

            Assert.Contains("Connect before negotiating", failure.Message);
        }

        [Fact]
        public async Task DisconnectingRestoresTheInstrumentsDisplay()
        {
            // Leaving a blank screen on a bench instrument is how a driver earns a reputation.
            var instrument = new FakeE4406A();
            E4406AFrontEnd frontEnd = Connected(instrument);

            await frontEnd.DisconnectAsync();

            Assert.Contains(":DISPlay:ENABle ON", instrument.Received);
        }

        [Fact]
        public void ItRequiresAResourceName()
        {
            Assert.Throws<ArgumentException>(() => new E4406AFrontEnd(string.Empty, null));
        }

        [Fact]
        public void TheResourceComesFromConfigurationOrTheEnvironment()
        {
            // Never a bus scan: on a bench with HP-IB extenders every address answers.
            string key = "OpenVSA.Visa.Test.Resource";
            string variable = VisaConfiguration.EnvironmentVariableFor(key);

            Assert.Equal("OPENVSA_VISA_TEST_RESOURCE", variable);
            Assert.Equal("GPIB0::7::INSTR", VisaConfiguration.ResourceFor(key, "GPIB0::7::INSTR"));

            try
            {
                Environment.SetEnvironmentVariable(variable, "TCPIP0::10.0.0.1::inst0::INSTR");
                Assert.Equal(
                    "TCPIP0::10.0.0.1::inst0::INSTR", VisaConfiguration.ResourceFor(key, "GPIB0::7::INSTR"));
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, null);
            }
        }

        private static E4406AFrontEnd Connected(FakeE4406A instrument)
        {
            var frontEnd = new E4406AFrontEnd("FAKE", resource => instrument);
            frontEnd.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
            return frontEnd;
        }

        /// <summary>
        /// A scripted E4406A: answers the queries the driver asks, records what it was sent.
        /// </summary>
        private sealed class FakeE4406A : IInstrumentSession
        {
            private readonly List<string> _received = new List<string>();
            private string _pending;
            private double _bandwidth = 1e6;
            private bool _lastWasScalarFetch;

            public double MinCentreHz { get; set; } = 7e6;
            public double MaxCentreHz { get; set; } = 4e9;
            public double MinBandwidthHz { get; set; } = 0.1;
            public double MaxBandwidthHz { get; set; } = 10e6;
            public double MaxSweepSeconds { get; set; } = 0.1;
            public double MinLevelDbm { get; set; } = -100.0;
            public double MaxLevelDbm { get; set; } = 30.0;
            public string ErrorReply { get; set; } = "+0,\"No error\"";
            public float[] Trace { get; set; }

            /// <summary>Sample period for a bandwidth; the instrument's own relationship.</summary>
            public Func<double, double> ApertureFor { get; set; } = bandwidth => 1.0 / (bandwidth * 1.25);

            /// <summary>The bandwidth actually adopted for one that was asked for.</summary>
            public Func<double, double> ActualBandwidthFor { get; set; } = wanted => wanted;

            public IReadOnlyList<string> Received => _received;

            public string ResourceName => "FAKE";

            public int TimeoutMilliseconds { get; set; } = 1000;

            public void Write(string command)
            {
                _received.Add(command);

                _lastWasScalarFetch = command.StartsWith(":FETCh:WAVeform1", StringComparison.Ordinal);

                if (command.StartsWith(":SENSe:WAVeform:BANDwidth:RESolution ", StringComparison.Ordinal))
                {
                    _bandwidth = ParseTrailingNumber(command);
                }

                // A query is any command containing '?', not only one ending in it: the limit
                // queries are of the form ":SENSe:FREQuency:CENTer? MAX".
                _pending = command.IndexOf('?') >= 0 ? Answer(command) : null;
            }

            public string ReadString()
            {
                string reply = _pending ?? string.Empty;
                _pending = null;
                return reply;
            }

            public string Query(string command)
            {
                Write(command);
                return ReadString();
            }

            public byte[] ReadBinaryBlock()
            {
                // :FORMat:DATA REAL,32 is global, so the scalar block comes back binary too - the
                // fake has to answer both queries in the same form the instrument does.
                float[] values = _lastWasScalarFetch
                    ? Scalars()
                    : (Trace ?? DefaultTrace());

                var payload = new byte[values.Length * 4];

                for (int i = 0; i < values.Length; i++)
                {
                    Buffer.BlockCopy(BitConverter.GetBytes(values[i]), 0, payload, i * 4, 4);
                }

                return payload;
            }

            /// <summary>The seven scalars, in the order REQ-E44-002 documents.</summary>
            private float[] Scalars()
            {
                float[] trace = Trace ?? DefaultTrace();

                return new[]
                {
                    (float)ApertureFor(ActualBandwidthFor(_bandwidth)),
                    -20.0f,
                    -20.0f,
                    trace.Length / 2.0f,
                    3.0f,
                    -17.0f,
                    -40.0f,
                };
            }

            public void Clear()
            {
            }

            public void Dispose()
            {
            }

            private static float[] DefaultTrace()
            {
                var trace = new float[1024];

                for (int n = 0; n < trace.Length / 2; n++)
                {
                    double phase = 2.0 * Math.PI * 64 * n / (trace.Length / 2);
                    trace[n * 2] = (float)(0.1 * Math.Cos(phase));
                    trace[n * 2 + 1] = (float)(0.1 * Math.Sin(phase));
                }

                return trace;
            }

            private string Answer(string command)
            {
                if (command == "*IDN?")
                {
                    return "Agilent Technologies,E4406A,MY00000000,A.11.00";
                }

                if (command == ":SYSTem:ERRor?")
                {
                    return ErrorReply;
                }

                if (command.StartsWith(":SENSe:FREQuency:CENTer?", StringComparison.Ordinal))
                {
                    return Number(command.EndsWith("MAX", StringComparison.Ordinal) ? MaxCentreHz : MinCentreHz);
                }

                if (command.StartsWith(":SENSe:WAVeform:BANDwidth:RESolution:ACTual?", StringComparison.Ordinal))
                {
                    return Number(ActualBandwidthFor(_bandwidth));
                }

                if (command.StartsWith(":SENSe:WAVeform:BANDwidth:RESolution?", StringComparison.Ordinal))
                {
                    return Number(command.EndsWith("MAX", StringComparison.Ordinal) ? MaxBandwidthHz : MinBandwidthHz);
                }

                if (command.StartsWith(":SENSe:WAVeform:APERture?", StringComparison.Ordinal))
                {
                    return Number(ApertureFor(ActualBandwidthFor(_bandwidth)));
                }

                if (command.StartsWith(":SENSe:WAVeform:SWEep:TIME?", StringComparison.Ordinal))
                {
                    return Number(MaxSweepSeconds);
                }

                if (command.StartsWith(":SENSe:POWer:RF:RANGe:UPPer?", StringComparison.Ordinal))
                {
                    return Number(command.EndsWith("MAX", StringComparison.Ordinal) ? MaxLevelDbm : MinLevelDbm);
                }

                return "0";
            }

            private static double ParseTrailingNumber(string command)
            {
                string[] parts = command.Split(' ');

                for (int i = parts.Length - 1; i >= 0; i--)
                {
                    double value;

                    if (double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    {
                        return value;
                    }
                }

                return 0.0;
            }

            private static string Number(double value) =>
                value.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
