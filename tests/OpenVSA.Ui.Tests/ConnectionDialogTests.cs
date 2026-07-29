using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Hal;
using OpenVSA.Ui;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// The part of <c>REQ-HAL-003</c>'s connection dialog that genuinely needs a window.
    /// </summary>
    /// <remarks>
    /// Which is not much, and deliberately so: what the listing contains is
    /// <see cref="ConnectionListingTests"/>'s. What is left is that Connect is enabled only on a row
    /// worth connecting to, that Refresh re-runs discovery, and that an enumeration which throws
    /// leaves a usable dialog rather than no dialog.
    /// </remarks>
    [Collection("Shell")]
    public class ConnectionDialogTests
    {
        private readonly ShellHost _host;
        private readonly ITestOutputHelper _output;

        /// <summary>Takes the shared STA host.</summary>
        /// <param name="host">The host whose thread every window here is built on.</param>
        /// <param name="output">Where the summaries are written.</param>
        public ConnectionDialogTests(ShellHost host, ITestOutputHelper output)
        {
            _host = host;
            _output = output;
        }

        [Fact]
        public void ConnectIsOffUntilARowWithADriverIsSelected()
        {
            // A row nothing drives is still listed and still readable — that is the point of
            // listing it — but connecting to it would fail, so the button says so first.
            _host.Run(() =>
            {
                var dialog = new ConnectionDialog(
                    () => ConnectionListingTests.Bench(), canEnumerate: true);

                Assert.False(dialog.ConnectButton.IsEnabled);

                Assert.True(dialog.Select("GPIB0::23::INSTR"));
                Assert.False(dialog.ConnectButton.IsEnabled);

                Assert.True(dialog.Select("GPIB0::17::INSTR"));
                Assert.True(dialog.ConnectButton.IsEnabled);

                dialog.Accept();

                Assert.Equal("GPIB0::17::INSTR", dialog.ChosenResource);

                dialog.Close();
            });
        }

        [Fact]
        public void AcceptingARowWithNoDriverDoesNothing()
        {
            // Belt and braces: the double-click path reaches Accept without going through the
            // button, so the button being disabled is not on its own enough.
            _host.Run(() =>
            {
                var dialog = new ConnectionDialog(
                    () => ConnectionListingTests.Bench(), canEnumerate: true);

                dialog.Select("GPIB0::9::INSTR");
                dialog.Accept();

                Assert.Null(dialog.ChosenResource);

                dialog.Close();
            });
        }

        [Fact]
        public void RefreshRunsDiscoveryAgain()
        {
            // The instrument that was switched off when the dialog opened is the reason this button
            // exists, so it has to actually ask again rather than re-render what it has.
            _host.Run(() =>
            {
                int calls = 0;

                var dialog = new ConnectionDialog(
                    () =>
                    {
                        calls++;
                        return calls == 1
                            ? new DiscoveredResource[0]
                            : (IReadOnlyList<DiscoveredResource>)ConnectionListingTests.Bench();
                    },
                    canEnumerate: true);

                Assert.Empty(dialog.Listing.Rows);

                dialog.Reload();

                _output.WriteLine(dialog.Listing.Summary);

                Assert.Equal(4, dialog.Listing.Rows.Count);
                Assert.Equal(2, calls);

                dialog.Close();
            });
        }

        [Fact]
        public void AnEnumerationThatThrowsLeavesAUsableDialog()
        {
            // Thrown out of a dialog the user opened on purpose, the exception would take the
            // window with it and leave them with no way to see what went wrong — which is the state
            // the dialog exists to get them out of.
            _host.Run(() =>
            {
                var dialog = new ConnectionDialog(
                    () => throw new InvalidOperationException("the resource manager is not there"),
                    canEnumerate: true);

                _output.WriteLine(dialog.Listing.Rows.Single().ToString());

                Assert.Single(dialog.Listing.Rows);
                Assert.Contains(
                    "the resource manager is not there", dialog.Listing.Rows[0].Status);
                Assert.False(dialog.ConnectButton.IsEnabled);

                dialog.Close();
            });
        }

        [Fact]
        public void SelectingAnAddressThatIsNotThereReportsItRatherThanSelectingSomethingElse()
        {
            _host.Run(() =>
            {
                var dialog = new ConnectionDialog(
                    () => ConnectionListingTests.Bench(), canEnumerate: true);

                Assert.False(dialog.Select("GPIB0::31::INSTR"));
                Assert.False(dialog.ConnectButton.IsEnabled);

                dialog.Close();
            });
        }
    }
}
