using System;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using OpenVSA.Core;
using OpenVSA.Personality;
using OpenVSA.Ui;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-ARC-003</c>: "A new personality assembly dropped into <c>Personalities\</c> is
    /// discovered on next launch, appears in the measurement-type selector, and runs — with no
    /// rebuild of the host."
    /// </summary>
    /// <remarks>
    /// <para>
    /// The middle clause is what these cover, and it is the one that was owed: discovery and
    /// running were built and tested with <see cref="PersonalityRegistry"/>, and nothing in the
    /// shell consulted it, so a dropped-in personality was found and had nowhere to appear.
    /// </para>
    /// <para>
    /// <strong>Nothing here references the example personality.</strong> The project builds it with
    /// <c>ReferenceOutputAssembly=false</c> and copies the file into <c>Personalities\</c> beside
    /// the test host — the folder <see cref="PersonalityRegistry.CreateDefault"/> probes — so a
    /// shell constructed afterwards finds it the way a launched application would. A reference
    /// would put the type in this assembly's output and prove only that reflection can find
    /// something the compiler already loaded. The absence of the reference is asserted below.
    /// </para>
    /// </remarks>
    [Collection("Shell")]
    public class PersonalitySelectorTests
    {
        private readonly ShellHost _host;
        private readonly ITestOutputHelper _output;

        /// <summary>Takes the shared STA host.</summary>
        /// <param name="host">The host whose thread the shell is built on.</param>
        /// <param name="output">Where the selector's contents are written.</param>
        public PersonalitySelectorTests(ShellHost host, ITestOutputHelper output)
        {
            _host = host;
            _output = output;
        }

        [Fact]
        public void TheHostWasNotBuiltAgainstTheExample()
        {
            // The claim the rest of this file rests on. If this assembly referenced the example,
            // every test below would pass without discovery doing anything at all.
            Assert.DoesNotContain(
                typeof(PersonalitySelectorTests).Assembly.GetReferencedAssemblies(),
                a => a.Name.IndexOf("Personality.Example", StringComparison.OrdinalIgnoreCase) >= 0);

            // And the shell itself, which is the host the criterion is about.
            Assert.DoesNotContain(
                typeof(ShellWindow).Assembly.GetReferencedAssemblies(),
                a => a.Name.IndexOf("Personality.Example", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void TheAssemblyIsWhereALaunchWouldFindIt()
        {
            // If the deployment step stopped working, the selector tests below would find nothing
            // and could be read as "no personality was offered" rather than "none was there".
            string plugins = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, PersonalityRegistry.PluginDirectoryName);

            _output.WriteLine(plugins);

            Assert.True(
                Directory.Exists(plugins),
                "The Personalities folder is missing, so nothing could be discovered and the " +
                "selector tests below would pass vacuously.");

            Assert.NotEmpty(Directory.GetFiles(plugins, PersonalityRegistry.PluginSearchPattern));
        }

        [Fact]
        public void ADiscoveredPersonalityAppearsUnderAnalysisType()
        {
            _host.Run(() =>
            {
                var shell = new ShellWindow();

                MenuItem type = TypeMenu(shell);

                string[] names = type.Items.OfType<MenuItem>()
                    .Select(m => ShellMenus.NameOf(m.Header as string))
                    .ToArray();

                _output.WriteLine(string.Join(", ", names));

                // The four built-in types keep their places, ahead of anything discovered.
                Assert.Equal("Spectrum", names[0]);

                Assert.True(
                    names.Length > 4,
                    "Only the four built-in measurement types are offered, so nothing discovered " +
                    "appeared in the selector.");

                shell.Close();
            });
        }

        [Fact]
        public void EveryDiscoveredPersonalityIsOfferedAndNoneIsInvented()
        {
            // Against the registry rather than a hard-coded name, so this says "the selector shows
            // what was discovered" rather than "the selector shows the example".
            _host.Run(() =>
            {
                var shell = new ShellWindow();

                string[] discovered = shell.Personalities.Personalities
                    .Select(p => p.DisplayName)
                    .ToArray();

                string[] offered = TypeMenu(shell).Items.OfType<MenuItem>()
                    .Select(m => ShellMenus.NameOf(m.Header as string))
                    .Skip(4)
                    .ToArray();

                _output.WriteLine(
                    "discovered [" + string.Join(", ", discovered) + "] offered [" +
                    string.Join(", ", offered) + "]");

                Assert.NotEmpty(discovered);
                Assert.Equal(discovered, offered);

                shell.Close();
            });
        }

        [Fact]
        public void ChoosingOneMakesItTheMeasurementType()
        {
            _host.Run(() =>
            {
                var shell = new ShellWindow();

                MenuItem chosen = TypeMenu(shell).Items.OfType<MenuItem>().Skip(4).First();

                chosen.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));

                _output.WriteLine(shell.Results.Summary);

                Assert.True(shell.Results.HasPersonality);
                Assert.Equal(
                    ShellMenus.NameOf(chosen.Header as string), shell.Results.PersonalityName);

                // The standard and its revision travel with the name, because REQ-PER-011 makes a
                // reading meaningless without knowing what it was measured against.
                Assert.NotEqual(string.Empty, shell.Results.Standard);

                // And Spectrum is no longer ticked, so the selector reads as one choice rather
                // than two.
                Assert.False(shell.SpectrumTypeIsChecked);

                shell.Close();
            });
        }

        [Fact]
        public void ChoosingSpectrumAgainPutsThePersonalityDown()
        {
            _host.Run(() =>
            {
                var shell = new ShellWindow();

                MenuItem type = TypeMenu(shell);

                type.Items.OfType<MenuItem>().Skip(4).First()
                    .RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));

                Assert.True(shell.Results.HasPersonality);

                type.Items.OfType<MenuItem>().First()
                    .RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));

                _output.WriteLine(shell.Results.Summary);

                Assert.False(shell.Results.HasPersonality);
                Assert.Contains("No measurement personality", shell.Results.Summary);

                shell.Close();
            });
        }

        [Fact]
        public void ADiscoveredPersonalityRunsAndProducesReadings()
        {
            // The criterion's third clause, reached through the shell's own selection rather than
            // by calling the personality directly — which is what makes this a test of the wiring
            // and not of the plug-in.
            _host.Run(() =>
            {
                var shell = new ShellWindow();

                TypeMenu(shell).Items.OfType<MenuItem>().Skip(4).First()
                    .RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));

                Assert.Equal(0, shell.Results.Count);

                using (IqBlock block = SampleBlock())
                {
                    shell.MeasureForTest(block);
                }

                foreach (string line in shell.Results.Lines)
                {
                    _output.WriteLine(line);
                }

                Assert.True(
                    shell.Results.Count > 0,
                    "The personality was selected and given a block it accepts, and produced no " +
                    "readings.");

                shell.Close();
            });
        }

        private static MenuItem TypeMenu(ShellWindow shell)
        {
            return shell.MenuBar.Items
                .OfType<MenuItem>()
                .Single(m => ShellMenus.NameOf(m.Header as string) == "Analysis")
                .Items.OfType<MenuItem>()
                .Single(m => ShellMenus.NameOf(m.Header as string) == "Type");
        }

        /// <summary>A unit-amplitude carrier, which any personality here will accept.</summary>
        private static IqBlock SampleBlock()
        {
            var metadata = new IqBlockMetadata(
                1024, 2.0e6, 1.0e9, false, 1.0, 0.0, 1L,
                new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc), 0.0, false,
                new FrontEndId("selector-test"), null);

            IqBlock block = IqBlock.Rent(metadata);
            Span<float> samples = block.GetSamples();

            for (int n = 0; n < 1024; n++)
            {
                samples[n * 2] = (float)Math.Cos(0.1 * n);
                samples[n * 2 + 1] = (float)Math.Sin(0.1 * n);
            }

            return block;
        }
    }
}
