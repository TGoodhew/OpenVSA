using System;
using System.IO;
using System.Linq;
using System.Reflection;
using OpenVSA.Core;
using OpenVSA.Personality;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Personality.Tests
{
    /// <summary>
    /// <c>REQ-ARC-003</c>: a new personality assembly dropped into <c>Personalities\</c> is
    /// discovered on next launch, appears in the measurement-type selector, and runs — with no
    /// rebuild of the host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The assembly under test is <strong>not referenced by this one</strong>. It is built
    /// alongside with <c>ReferenceOutputAssembly=false</c> and copied into a plug-in directory at
    /// run time, because a test that referenced it would have the type loaded already and would
    /// prove only that reflection can find something the compiler put there.
    /// </para>
    /// <para>
    /// "On next launch" is the one clause a unit test cannot reproduce literally — nothing here
    /// restarts the application. What it can show is the property that makes a launch enough: a
    /// registry built from a directory finds what is in the directory, having been given no
    /// compile-time knowledge of it.
    /// </para>
    /// </remarks>
    public class PersonalityDiscoveryTests : IDisposable
    {
        private readonly string _plugins;
        private readonly ITestOutputHelper _output;

        /// <summary>Copies the example personality into a plug-in directory of its own.</summary>
        /// <param name="output">Where discovered names are written.</param>
        public PersonalityDiscoveryTests(ITestOutputHelper output)
        {
            _output = output;

            _plugins = Path.Combine(
                Path.GetTempPath(),
                "OpenVSA.Personalities." + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(_plugins);

            foreach (string file in ExampleFiles())
            {
                File.Copy(file, Path.Combine(_plugins, Path.GetFileName(file)), overwrite: true);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            try
            {
                Directory.Delete(_plugins, recursive: true);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // Assembly.LoadFrom holds the file open for the life of the AppDomain, so the
                // directory cannot be removed until the process exits — and Windows reports that
                // as UnauthorizedAccess, not IOException, which is why the first version of this
                // failed a passing test in its cleanup. A leftover temp directory is not worth
                // failing a test over.
            }
        }

        [Fact]
        public void APersonalityDroppedIntoTheDirectoryIsDiscovered()
        {
            // The host has no compile-time knowledge of this type: OpenVSA.Personality.Tests does
            // not reference OpenVSA.Personality.Example, which is what makes this a discovery test
            // rather than a reflection test.
            Assert.DoesNotContain(
                "OpenVSA.Personality.Example",
                typeof(PersonalityDiscoveryTests).Assembly
                    .GetReferencedAssemblies().Select(a => a.Name));

            var registry = new PersonalityRegistry();
            int added = registry.ProbeDirectory(_plugins);

            _output.WriteLine(
                "discovered " + added + ": " +
                string.Join(", ", registry.Personalities.Select(p => p.DisplayName)));

            foreach (PersonalityDiscoveryFailure failure in registry.Failures)
            {
                _output.WriteLine("failure: " + failure);
            }

            Assert.Equal(1, added);
            Assert.Empty(registry.Failures);

            IMeasurementPersonality personality = registry.Personalities.Single();

            Assert.Equal("Example mean power", personality.DisplayName);
            Assert.False(string.IsNullOrEmpty(personality.Standard));
            Assert.False(string.IsNullOrEmpty(personality.StandardRevision));
        }

        [Fact]
        public void ADiscoveredPersonalityRuns()
        {
            var registry = new PersonalityRegistry();
            registry.ProbeDirectory(_plugins);

            IMeasurementPersonality personality = registry.Personalities.Single();

            // A unit-amplitude carrier: mean power is 1.0 whatever the phase.
            var metadata = new IqBlockMetadata(
                1024, 2.0e6, 1.0e9, false, 1.0, 0.0, 1L,
                new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc), 0.0, false,
                new FrontEndId("test"), null);

            IqBlock block = IqBlock.Rent(metadata);
            Span<float> samples = block.GetSamples();

            for (int n = 0; n < 1024; n++)
            {
                samples[n * 2] = (float)Math.Cos(0.1 * n);
                samples[n * 2 + 1] = (float)Math.Sin(0.1 * n);
            }

            Assert.True(personality.CanMeasure(block));

            var readings = personality.Measure(block).ToArray();

            foreach (PersonalityReading reading in readings)
            {
                _output.WriteLine(reading.ToString());
            }

            Assert.Equal(2, readings.Length);
            Assert.Equal("Mean power", readings[0].Name);
            Assert.Equal(1.0, readings[0].Value, 5);
            Assert.Equal(1024.0, readings[1].Value, 6);
        }

        [Fact]
        public void AMissingDirectoryIsNotAFailure()
        {
            // REQ-NFR-032: the application runs usefully with nothing installed. A plain deployment
            // has no Personalities folder, and that is the normal case rather than a fault.
            var registry = new PersonalityRegistry();

            Assert.Equal(0, registry.ProbeDirectory(
                Path.Combine(_plugins, "no-such-directory")));

            Assert.Empty(registry.Failures);
            Assert.Empty(registry.Personalities);
        }

        [Fact]
        public void AnUnloadableFileIsRecordedRatherThanThrown()
        {
            // One bad plug-in must not stop the others being found, and must not stop the
            // application starting.
            string bad = Path.Combine(_plugins, "OpenVSA.Personality.Broken.dll");
            File.WriteAllBytes(bad, new byte[] { 0x4D, 0x5A, 0x00, 0x01, 0x02, 0x03 });

            var registry = new PersonalityRegistry();
            int added = registry.ProbeDirectory(_plugins);

            _output.WriteLine(string.Join(Environment.NewLine, registry.Failures.Select(f => f.ToString())));

            // The good one is still there, and the bad one is explained.
            Assert.Equal(1, added);
            Assert.Single(registry.Failures);
            Assert.Contains("Broken", registry.Failures[0].Candidate);
        }

        [Fact]
        public void TheSameAssemblyProbedTwiceRegistersOnce()
        {
            // The application probes its own folder and a plug-in folder, which can present the
            // same assembly twice. Registering it twice would show every personality doubled.
            var registry = new PersonalityRegistry();

            Assert.Equal(1, registry.ProbeDirectory(_plugins));
            Assert.Equal(0, registry.ProbeDirectory(_plugins));
            Assert.Single(registry.Personalities);
        }

        [Fact]
        public void FindLocatesAPersonalityByName()
        {
            var registry = new PersonalityRegistry();
            registry.ProbeDirectory(_plugins);

            Assert.NotNull(registry.Find("Example mean power"));
            Assert.NotNull(registry.Find("EXAMPLE MEAN POWER"));
            Assert.Null(registry.Find("Nothing of the sort"));
            Assert.Null(registry.Find(null));
        }

        /// <summary>The example personality's built output, from beside this assembly.</summary>
        private static string[] ExampleFiles()
        {
            string here = Path.GetDirectoryName(
                new Uri(typeof(PersonalityDiscoveryTests).Assembly.CodeBase).LocalPath);

            int bin = here.IndexOf("bin", StringComparison.OrdinalIgnoreCase);
            string tail = here.Substring(bin);

            string root = Path.GetFullPath(Path.Combine(here.Substring(0, bin), "..", ".."));
            string example = Path.Combine(root, "tests", "OpenVSA.Personality.Example", tail);

            Assert.True(
                Directory.Exists(example),
                "The example personality was not built at " + example);

            string[] files = Directory.GetFiles(example, "OpenVSA.Personality.Example.dll");

            Assert.NotEmpty(files);
            return files;
        }
    }
}
