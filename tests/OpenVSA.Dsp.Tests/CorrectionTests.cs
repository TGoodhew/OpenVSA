using System;
using System.Globalization;
using System.IO;
using System.Linq;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-AMP-003</c> frequency-response correction and <c>REQ-AMP-004</c> de-embedding.
    /// </summary>
    public class CorrectionTests
    {
        private const double StartHz = 1.0e9;
        private const double BinWidthHz = 1.0e6;
        private const int Points = 101;

        private readonly ITestOutputHelper _output;

        public CorrectionTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void ACorrectionOfKnownShapeProducesExactlyThatShapeOnAFlatInput()
        {
            // REQ-AMP-003's criterion. A flat input corrected by a known slope must come out as
            // that slope and nothing else, so the correction is doing the whole of what it says
            // and none of anything else.
            SpectrumFrame flat = Flat(1.0);

            var table = new CorrectionTable("slope", new[]
            {
                new CorrectionPoint(StartHz, 0.0),
                new CorrectionPoint(StartHz + (Points - 1) * BinWidthHz, 10.0),
            });

            SpectrumFrame corrected = Corrections.Apply(flat, table);

            double worst = 0.0;

            for (int i = 0; i < Points; i++)
            {
                double expected = 10.0 * i / (Points - 1);
                double measured = corrected.LevelsDbm[i] - flat.LevelsDbm[i];

                worst = Math.Max(worst, Math.Abs(measured - expected));
            }

            _output.WriteLine("worst departure from the applied slope " + worst.ToString("G3") + " dB");

            Assert.True(worst < 1e-3, "The correction departed from its own shape by " + worst + " dB.");
        }

        [Fact]
        public void ACorrectionAndItsInverseCancel()
        {
            // Which is what makes de-embedding meaningful: removing a response has to be exactly
            // undoing applying it, in phase as well as magnitude.
            SpectrumFrame flat = Flat(1.0);
            CorrectionTable fixture = Fixture();

            SpectrumFrame there = Corrections.Apply(flat, fixture);
            SpectrumFrame andBack = Corrections.Remove(there, fixture);

            double worst = 0.0;

            for (int i = 0; i < flat.Complex.Length; i++)
            {
                worst = Math.Max(worst, Math.Abs(andBack.Complex[i] - flat.Complex[i]));
            }

            Assert.True(worst < 1e-5, "Applying and removing left a residue of " + worst + " V.");
        }

        [Fact]
        public void DeEmbeddingRecoversTheOriginalWithinTheStatedTolerances()
        {
            // REQ-AMP-004's criterion: 0.05 dB in magnitude and 0.5 degrees in phase, across the
            // band, after passing through a synthetic fixture of known complex response.
            SpectrumFrame original = Ramp();
            CorrectionTable fixture = Fixture();

            SpectrumFrame measured = Corrections.Apply(original, fixture);
            SpectrumFrame recovered = Corrections.Remove(measured, fixture);

            double worstDb = 0.0;
            double worstDegrees = 0.0;

            for (int i = 0; i < Points; i++)
            {
                worstDb = Math.Max(
                    worstDb, Math.Abs(recovered.LevelsDbm[i] - original.LevelsDbm[i]));

                worstDegrees = Math.Max(
                    worstDegrees, Math.Abs(PhaseAt(recovered, i) - PhaseAt(original, i)));
            }

            _output.WriteLine(
                "recovered within " + worstDb.ToString("G3") + " dB and " +
                worstDegrees.ToString("G3") + " degrees");

            Assert.True(worstDb <= 0.05, "Magnitude recovered to " + worstDb + " dB.");
            Assert.True(worstDegrees <= 0.5, "Phase recovered to " + worstDegrees + " degrees.");
        }

        [Fact]
        public void DeEmbeddingIsComplexRatherThanMagnitudeOnly()
        {
            // REQ-AMP-004 asks this to be proved by a fixture flat in magnitude and not in phase.
            // The requirement states the proof in terms of EVM, which arrives with Phase 2's
            // demodulation; the same thing is asserted directly here on the phase itself, which is
            // the quantity EVM would be measuring.
            SpectrumFrame original = Ramp();

            var phaseOnly = new CorrectionTable("phase-only fixture", new[]
            {
                new CorrectionPoint(StartHz, 0.0, 0.0),
                new CorrectionPoint(StartHz + (Points - 1) * BinWidthHz, 0.0, 120.0),
            });

            SpectrumFrame measured = Corrections.Apply(original, phaseOnly);

            // Magnitude-only de-embedding is the same operation with the phase discarded, which is
            // what a magnitude-only implementation would amount to.
            var magnitudeOnly = new CorrectionTable(
                "magnitude of the fixture",
                phaseOnly.Points.Select(p => new CorrectionPoint(p.FrequencyHz, p.MagnitudeDb)));

            SpectrumFrame partial = Corrections.Remove(measured, magnitudeOnly);
            SpectrumFrame full = Corrections.Remove(measured, phaseOnly);

            double worstPartial = 0.0;
            double worstFull = 0.0;

            for (int i = 0; i < Points; i++)
            {
                worstPartial = Math.Max(
                    worstPartial, Math.Abs(PhaseAt(partial, i) - PhaseAt(original, i)));

                worstFull = Math.Max(worstFull, Math.Abs(PhaseAt(full, i) - PhaseAt(original, i)));
            }

            _output.WriteLine(
                "magnitude-only leaves " + worstPartial.ToString("F1") +
                " degrees of error; complex leaves " + worstFull.ToString("G3"));

            // The fixture is flat in magnitude, so a magnitude-only correction changes nothing and
            // the whole 120 degrees remains.
            Assert.True(worstPartial > 100.0);
            Assert.True(worstFull <= 0.5);
        }

        [Fact]
        public void TablesCombineByAddingBothResponses()
        {
            // REQ-AMP-003's combinable: cable loss plus antenna factor. Exact rather than
            // approximate, because decibels and degrees both add.
            var cable = new CorrectionTable("cable", new[]
            {
                new CorrectionPoint(StartHz, -1.0, 10.0),
                new CorrectionPoint(StartHz + 100e6, -3.0, 30.0),
            });

            var antenna = new CorrectionTable("antenna", new[]
            {
                new CorrectionPoint(StartHz, 20.0, -5.0),
                new CorrectionPoint(StartHz + 100e6, 22.0, -5.0),
            });

            CorrectionTable both = cable.CombinedWith(antenna);

            Assert.Equal("cable + antenna", both.Name);
            Assert.Equal(19.0, both.At(StartHz).MagnitudeDb, 9);
            Assert.Equal(5.0, both.At(StartHz).PhaseDegrees, 9);
            Assert.Equal(19.0, both.At(StartHz + 100e6).MagnitudeDb, 9);
            Assert.Equal(25.0, both.At(StartHz + 100e6).PhaseDegrees, 9);
        }

        [Fact]
        public void CombiningKeepsTheFinerOfTheTwoGrids()
        {
            // A cable loss given every 100 MHz combined with an antenna factor given every 10 MHz
            // must keep the 10 MHz detail, or the combination throws away the measurement that
            // was more carefully made.
            var coarse = new CorrectionTable("coarse", new[]
            {
                new CorrectionPoint(StartHz, 0.0),
                new CorrectionPoint(StartHz + 100e6, 0.0),
            });

            CorrectionTable fine = new CorrectionTable(
                "fine",
                Enumerable.Range(0, 11).Select(i => new CorrectionPoint(StartHz + i * 10e6, i)));

            CorrectionTable both = coarse.CombinedWith(fine);

            Assert.Equal(11, both.Points.Count);
            Assert.Equal(5.0, both.At(StartHz + 50e6).MagnitudeDb, 9);
        }

        [Fact]
        public void TheResponseIsHeldFlatOutsideTheStatedRangeRatherThanExtrapolated()
        {
            // Extrapolating a cable loss beyond where it was measured invents a number that looks
            // like a measurement; holding the end value is visibly a limit of the table.
            var table = new CorrectionTable("short", new[]
            {
                new CorrectionPoint(StartHz, -2.0, 5.0),
                new CorrectionPoint(StartHz + 10e6, -4.0, 15.0),
            });

            Assert.Equal(-2.0, table.At(StartHz - 1e9).MagnitudeDb, 9);
            Assert.Equal(5.0, table.At(StartHz - 1e9).PhaseDegrees, 9);
            Assert.Equal(-4.0, table.At(StartHz + 1e9).MagnitudeDb, 9);
            Assert.Equal(-3.0, table.At(StartHz + 5e6).MagnitudeDb, 9);
        }

        [Fact]
        public void ATableIsReadFromTextWithCommentsAndOptionalPhase()
        {
            string text = string.Join(
                Environment.NewLine,
                "# Cable: RG-214, 3 m, measured 2026-07-26",
                "! a second comment style",
                string.Empty,
                "1.0e9, -1.5, 12.0",
                "1.1e9  -2.5  18.0",
                "1.2e9,-3.5");

            CorrectionTable table = CorrectionTable.Parse("cable", text);

            Assert.Equal(3, table.Points.Count);
            Assert.Equal(-1.5, table.At(1.0e9).MagnitudeDb, 9);
            Assert.Equal(18.0, table.At(1.1e9).PhaseDegrees, 9);

            // Phase is optional, because a magnitude-only table - an antenna factor, say - is a
            // legitimate and common thing to have.
            Assert.Equal(0.0, table.At(1.2e9).PhaseDegrees, 9);
        }

        [Fact]
        public void ATableIsReadFromAFile()
        {
            string path = Path.Combine(
                Path.GetTempPath(), "OpenVSA.correction." + Guid.NewGuid().ToString("N") + ".csv");

            try
            {
                File.WriteAllText(path, "1e9, -1.0" + Environment.NewLine + "2e9, -2.0");

                CorrectionTable table = CorrectionTable.Load(path);

                Assert.Equal(2, table.Points.Count);
                Assert.Equal(-1.5, table.At(1.5e9).MagnitudeDb, 9);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [Fact]
        public void TheCorrectionIsAvailableAsATraceOfItsOwn()
        {
            // REQ-DSP-040's Correction data type: a correction nobody can see is one nobody can
            // check.
            SpectrumFrame flat = Flat(1.0);
            CorrectionTable table = Fixture();

            SpectrumFrame response = Corrections.AsTrace(flat, table);

            Assert.Equal(flat.PointCount, response.PointCount);
            Assert.Equal(flat.StartFrequencyHz, response.StartFrequencyHz, 3);

            for (int i = 0; i < Points; i += 10)
            {
                CorrectionPoint point = table.At(flat.FrequencyAt(i));
                double magnitude = Math.Sqrt(
                    response.Complex[i * 2] * (double)response.Complex[i * 2] +
                    response.Complex[i * 2 + 1] * (double)response.Complex[i * 2 + 1]);

                Assert.Equal(point.MagnitudeDb, 20.0 * Math.Log10(magnitude), 4);
            }
        }

        [Fact]
        public void TheArgumentsAreChecked()
        {
            Assert.Throws<ArgumentNullException>(() => new CorrectionTable("x", null));
            Assert.Throws<ArgumentException>(() => new CorrectionTable("x", new CorrectionPoint[0]));

            // Two points at one frequency leave the correction there ambiguous.
            Assert.Throws<ArgumentException>(() => new CorrectionTable("x", new[]
            {
                new CorrectionPoint(1e9, 1.0),
                new CorrectionPoint(1e9, 2.0),
            }));

            Assert.Throws<ArgumentNullException>(() => CorrectionTable.Parse("x", null));
            Assert.Throws<FormatException>(() => CorrectionTable.Parse("x", "1e9"));
            Assert.Throws<FormatException>(() => CorrectionTable.Parse("x", "# nothing but a comment"));
            Assert.Throws<FormatException>(() => CorrectionTable.Parse("x", "one, two"));
            Assert.Throws<ArgumentNullException>(() => CorrectionTable.Load(null));

            Assert.Throws<ArgumentNullException>(() => Corrections.Apply(null, Fixture()));
            Assert.Throws<ArgumentNullException>(() => Corrections.Apply(Flat(1.0), null));
            Assert.Throws<ArgumentNullException>(() => Corrections.Remove(null, Fixture()));
            Assert.Throws<ArgumentNullException>(() => Corrections.AsTrace(Flat(1.0), null));
            Assert.Throws<ArgumentNullException>(() => Fixture().CombinedWith(null));
        }

        [Fact]
        public void InvertingNegatesBothMagnitudeAndPhase()
        {
            CorrectionTable inverted = Fixture().Inverted();

            Assert.Equal(-Fixture().At(StartHz).MagnitudeDb, inverted.At(StartHz).MagnitudeDb, 9);
            Assert.Equal(-Fixture().At(StartHz).PhaseDegrees, inverted.At(StartHz).PhaseDegrees, 9);
            Assert.Contains("inverse", inverted.Name);
        }

        // ---- Signals ---------------------------------------------------------------------------

        /// <summary>A fixture with slope in both magnitude and phase.</summary>
        private static CorrectionTable Fixture() =>
            new CorrectionTable("fixture", new[]
            {
                new CorrectionPoint(StartHz, -0.5, 0.0),
                new CorrectionPoint(StartHz + 50e6, -2.5, 45.0),
                new CorrectionPoint(StartHz + 100e6, -6.0, 130.0),
            });

        private static SpectrumFrame Flat(double voltsPeak)
        {
            var complex = new float[Points * 2];

            for (int i = 0; i < Points; i++)
            {
                complex[i * 2] = (float)voltsPeak;
            }

            return SpectrumFrame.FromComplex(
                complex, StartHz, BinWidthHz, WindowType.Uniform, 1.0);
        }

        /// <summary>A spectrum with both amplitude and phase varying, so both can be recovered.</summary>
        private static SpectrumFrame Ramp()
        {
            var complex = new float[Points * 2];

            for (int i = 0; i < Points; i++)
            {
                double amplitude = 0.2 + 0.6 * i / (Points - 1);
                double phase = 2.0 * Math.PI * 0.37 * i / (Points - 1);

                complex[i * 2] = (float)(amplitude * Math.Cos(phase));
                complex[i * 2 + 1] = (float)(amplitude * Math.Sin(phase));
            }

            return SpectrumFrame.FromComplex(
                complex, StartHz, BinWidthHz, WindowType.Uniform, 1.0);
        }

        private static double PhaseAt(SpectrumFrame frame, int index) =>
            Math.Atan2(frame.Complex[index * 2 + 1], frame.Complex[index * 2]) * 180.0 / Math.PI;
    }
}
