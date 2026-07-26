using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using OpenVSA.Core;
using OpenVSA.Hal;
using OpenVSA.Hal.Visa;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Hal.Tests
{
    /// <summary>
    /// Every point count the UI will offer, taken through plan → configure → acquire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A settings control that offers a value must be able to deliver it. The point-count list is
    /// built from <c>AcquisitionPlanner.MaximumPointsFor</c>, which is built from the front end's
    /// declared capture depth — so if that declaration is optimistic, the UI offers counts that
    /// hang or fail when chosen, which is exactly the failure this sweeps for.
    /// </para>
    /// <para>
    /// The hardware sweep is gated on <c>OPENVSA_E4406A_LADDER</c> naming a resource, so the suite
    /// stays green with no bench attached. Run it with:
    /// <c>$env:OPENVSA_E4406A_LADDER = "GPIB0::17::INSTR"; dotnet test --filter Ladder</c>
    /// </para>
    /// </remarks>
    public class E4406ALadderTests
    {
        /// <summary>Environment variable naming the instrument for the hardware sweep.</summary>
        public const string HardwareVariable = "OPENVSA_E4406A_LADDER";

        private readonly ITestOutputHelper _output;

        public E4406ALadderTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task EveryOfferedPointCountCanBeAcquired()
        {
            // Against a scripted instrument, so this runs in CI: it proves the driver's own path
            // over the whole ladder, not the transfer time.
            var instrument = new LadderInstrument();
            using (var frontEnd = new E4406AFrontEnd("FAKE", resource => instrument))
            {
                await frontEnd.ConnectAsync(CancellationToken.None);

                int offered = 0;

                foreach (int points in OfferedPointCounts(frontEnd.Capabilities))
                {
                    int transform = AcquisitionLaw.TransformLengthFor(points, AnalysisPath.ComplexZoom);

                    AcquisitionPlan plan = frontEnd.Negotiate(
                        new AcquisitionRequest(1e9, 1e6, transform, -10.0));

                    await frontEnd.ConfigureAsync(plan, CancellationToken.None);
                    await frontEnd.ArmAsync(CancellationToken.None);

                    using (IqBlock block = await frontEnd.AcquireNextAsync(CancellationToken.None))
                    {
                        Assert.True(block.SampleCount > 0, points + " points produced an empty block.");
                        Assert.True(block.SampleRateHz > 0.0);
                    }

                    offered++;
                }

                Assert.True(offered > 0, "No point count was offered at all.");
                _output.WriteLine(offered + " point counts offered and acquired.");
            }
        }

        [Fact]
        public void TheOfferedCountsAreBoundedBySomethingAchievable()
        {
            // The bug this exists for: a front end whose declared depth comes straight from
            // "maximum sweep time x sample rate" declares over a billion samples, the UI then
            // offers the whole ladder, and choosing the top of it asks for a transfer that cannot
            // complete. A capture depth has to mean "can actually be delivered".
            var instrument = new LadderInstrument();
            using (var frontEnd = new E4406AFrontEnd("FAKE", resource => instrument))
            {
                frontEnd.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();

                IFrontEndCapabilities caps = frontEnd.Capabilities;

                Assert.True(
                    caps.MaxSamplesPerBlock <= E4406AFrontEnd.MaximumTransferSamples,
                    "Declared block size of " +
                    caps.MaxSamplesPerBlock.ToString(CultureInfo.InvariantCulture) +
                    " samples is more than this front end will transfer in one block.");
            }
        }

        [Fact]
        public async Task HardwareLadderSweep()
        {
            string resource = Environment.GetEnvironmentVariable(HardwareVariable);
            if (string.IsNullOrEmpty(resource))
            {
                // No bench attached. Reported rather than silently passing, so a green run does
                // not read as "the hardware sweep succeeded".
                _output.WriteLine(HardwareVariable + " is not set, so the hardware sweep did not run.");
                return;
            }

            using (var frontEnd = new E4406AFrontEnd(resource, null))
            {
                await frontEnd.ConnectAsync(CancellationToken.None);
                _output.WriteLine("Connected: " + frontEnd.DisplayName);
                _output.WriteLine(
                    "Declared block size: " +
                    frontEnd.Capabilities.MaxSamplesPerBlock.ToString(CultureInfo.InvariantCulture));

                var failures = new List<string>();

                foreach (int points in OfferedPointCounts(frontEnd.Capabilities))
                {
                    int transform = AcquisitionLaw.TransformLengthFor(points, AnalysisPath.ComplexZoom);
                    var clock = Stopwatch.StartNew();

                    try
                    {
                        AcquisitionPlan plan = frontEnd.Negotiate(
                            new AcquisitionRequest(1e9, 1e6, transform, -10.0));

                        await frontEnd.ConfigureAsync(plan, CancellationToken.None);
                        await frontEnd.ArmAsync(CancellationToken.None);

                        using (IqBlock block = await frontEnd.AcquireNextAsync(CancellationToken.None))
                        {
                            _output.WriteLine(
                                points.ToString(CultureInfo.InvariantCulture).PadLeft(7) + " points, " +
                                transform.ToString(CultureInfo.InvariantCulture).PadLeft(7) + " samples asked, " +
                                block.SampleCount.ToString(CultureInfo.InvariantCulture).PadLeft(7) + " returned, " +
                                clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture).PadLeft(6) + " ms");
                        }
                    }
                    catch (Exception failure)
                    {
                        string message =
                            points + " points (" + transform + " samples) failed after " +
                            clock.ElapsedMilliseconds + " ms: " + failure.Message.Split('\n')[0];

                        _output.WriteLine(message);
                        failures.Add(message);
                    }
                }

                Assert.True(
                    failures.Count == 0,
                    "Point counts the UI offers but the instrument cannot deliver:" +
                    Environment.NewLine + string.Join(Environment.NewLine, failures));
            }
        }

        /// <summary>The counts a settings control would offer for these capabilities.</summary>
        private static IEnumerable<int> OfferedPointCounts(IFrontEndCapabilities capabilities)
        {
            long ceiling = Math.Min(
                capabilities.MaxSamplesPerBlock,
                Math.Min(capabilities.MaxCaptureSamples, FrequencyPoints.MaximumTransformLength));

            foreach (int candidate in FrequencyPoints.Supported)
            {
                if (AcquisitionLaw.TransformLengthFor(candidate, AnalysisPath.ComplexZoom) > ceiling)
                {
                    yield break;
                }

                yield return candidate;
            }
        }

        /// <summary>A scripted instrument that returns exactly the sweep length it was set.</summary>
        private sealed class LadderInstrument : IInstrumentSession
        {
            private double _bandwidth = 1e6;
            private double _sweepSeconds = 1e-3;
            private string _pending;
            private bool _lastWasScalarFetch;

            public string ResourceName => "FAKE";

            public int TimeoutMilliseconds { get; set; } = 1000;

            public void Write(string command)
            {
                _lastWasScalarFetch = command.StartsWith(":FETCh:WAVeform1", StringComparison.Ordinal);

                if (command.StartsWith(":SENSe:WAVeform:BANDwidth:RESolution ", StringComparison.Ordinal))
                {
                    _bandwidth = Trailing(command);
                }
                else if (command.StartsWith(":SENSe:WAVeform:SWEep:TIME ", StringComparison.Ordinal))
                {
                    _sweepSeconds = Trailing(command);
                }

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
                var samples = (int)Math.Round(_sweepSeconds / Aperture());
                samples = Math.Max(2, samples);

                if (!_lastWasScalarFetch)
                {
                    return new byte[samples * 8];
                }

                // The seven scalars, binary like everything else under REAL,32.
                float[] scalars = { (float)Aperture(), -20f, -20f, samples, 3f, -17f, -40f };
                var payload = new byte[scalars.Length * 4];

                for (int i = 0; i < scalars.Length; i++)
                {
                    Buffer.BlockCopy(BitConverter.GetBytes(scalars[i]), 0, payload, i * 4, 4);
                }

                return payload;
            }

            public void Clear()
            {
            }

            public void Dispose()
            {
            }

            private double Aperture() => 1.0 / (_bandwidth * 1.5);

            private string Answer(string command)
            {
                if (command == "*IDN?")
                {
                    return "Hewlett-Packard,E4406A,US40062429,A.08.10";
                }

                if (command == ":SYSTem:ERRor?")
                {
                    return "+0,\"No error\"";
                }

                if (command.StartsWith(":SENSe:FREQuency:CENTer?", StringComparison.Ordinal))
                {
                    return Number(command.EndsWith("MAX", StringComparison.Ordinal) ? 4.3214e9 : 1e3);
                }

                if (command.StartsWith(":SENSe:WAVeform:BANDwidth:RESolution:ACTual?", StringComparison.Ordinal))
                {
                    return Number(_bandwidth);
                }

                if (command.StartsWith(":SENSe:WAVeform:BANDwidth:RESolution?", StringComparison.Ordinal))
                {
                    return Number(command.EndsWith("MAX", StringComparison.Ordinal) ? 10e6 : 10.0);
                }

                if (command.StartsWith(":SENSe:WAVeform:APERture?", StringComparison.Ordinal))
                {
                    return Number(Aperture());
                }

                if (command.StartsWith(":SENSe:WAVeform:SWEep:TIME?", StringComparison.Ordinal))
                {
                    // The real instrument reports a hundred seconds, which at its sample rate is
                    // one and a half billion samples.
                    return Number(100.0);
                }

                return "0";
            }

            private static double Trailing(string command)
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
