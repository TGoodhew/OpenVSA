using System;
using System.Linq;
using System.Windows.Controls;
using OpenVSA.Hal;
using OpenVSA.Ui;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-HAL-003</c>: choosing a front end that needs an address asks for one.
    /// </summary>
    /// <remarks>
    /// The reachability half of the criterion. <c>REQ-UI-061</c> fixes the menu contents as an exact
    /// list, so the connection dialog gets no entry of its own — it is opened by the act of choosing
    /// a driver that cannot work without an address, which is an entry that is already there.
    /// </remarks>
    [Collection("Shell")]
    public class ResourceChoiceTests
    {
        private readonly ShellHost _host;
        private readonly ITestOutputHelper _output;

        /// <summary>Takes the shared STA host.</summary>
        /// <param name="host">The host whose thread the shell is built on.</param>
        /// <param name="output">Where the menu contents are written.</param>
        public ResourceChoiceTests(ShellHost host, ITestOutputHelper output)
        {
            _host = host;
            _output = output;
        }

        [Fact]
        public void TheHardwareMenuHasNoEntryOfItsOwnForTheDialog()
        {
            // REQ-UI-061's exact-list criterion is asserted elsewhere as a set; this says out loud
            // why the dialog was not given an entry, so a later change that adds one has to argue
            // with something.
            _host.Run(() =>
            {
                var shell = new ShellWindow();

                MenuItem hardware = shell.MenuBar.Items
                    .OfType<MenuItem>()
                    .Single(m => ShellMenus.NameOf(m.Header as string) == "Hardware");

                string[] names = hardware.Items.OfType<MenuItem>()
                    .Select(m => ShellMenus.NameOf(m.Header as string))
                    .ToArray();

                _output.WriteLine(string.Join(", ", names));

                // Not a substring test: "Disconnect" contains "Connect", and the first version of
                // this matched it and failed. What must not appear is an entry OF ITS OWN for the
                // dialog.
                Assert.DoesNotContain("Connect…", names);
                Assert.DoesNotContain("Connection…", names);
                Assert.DoesNotContain("Connect", names);

                shell.Close();
            });
        }

        [Fact]
        public void CancellingTheChoiceLeavesTheShellExactlyAsItWas()
        {
            // The reason the dialog is opened before anything is torn down. Asking after
            // disconnecting would leave a user who pressed Cancel worse off than before they
            // touched the menu — disconnected from an instrument they had, with nothing selected.
            _host.Run(() =>
            {
                var shell = new ShellWindow();

                shell.ChooseResourceForTest = frontEnd => false;

                IFrontEnd before = shell.ActiveFrontEnd;

                MenuItem chosen = SelectAFrontEndNeedingAnAddress(shell);

                if (chosen == null)
                {
                    // No transport on this machine needs an address — the simulator and file
                    // playback do not. Said rather than passed silently.
                    _output.WriteLine(
                        "No discovered front end implements IRequiresResource on this machine; " +
                        "nothing to cancel.");
                    shell.Close();
                    return;
                }

                Assert.Same(before, shell.ActiveFrontEnd);
                Assert.False(chosen.IsChecked);

                shell.Close();
            });
        }

        [Fact]
        public void TheChosenAddressReachesTheFrontEnd()
        {
            // The other half: what the dialog returns is what the driver is pointed at. A dialog
            // whose answer went nowhere would still pass every test of the listing itself.
            var frontEnd = new AddressableFrontEnd();

            Assert.Equal(string.Empty, frontEnd.ResourceName);

            Func<IRequiresResource, bool> choose = f =>
            {
                f.UseResource("GPIB0::17::INSTR");
                return true;
            };

            Assert.True(choose(frontEnd));
            Assert.Equal("GPIB0::17::INSTR", frontEnd.ResourceName);
        }

        /// <summary>Clicks the first discovered front end that needs an address, if there is one.</summary>
        private static MenuItem SelectAFrontEndNeedingAnAddress(ShellWindow shell)
        {
            MenuItem instruments = shell.MenuBar.Items
                .OfType<MenuItem>()
                .Single(m => ShellMenus.NameOf(m.Header as string) == "Hardware")
                .Items.OfType<MenuItem>()
                .Single(m => ShellMenus.NameOf(m.Header as string) == "Instruments…");

            instruments.RaiseEvent(
                new System.Windows.RoutedEventArgs(MenuItem.SubmenuOpenedEvent));

            FrontEndRegistry registry = shell.Registry;

            foreach (MenuItem item in instruments.Items.OfType<MenuItem>())
            {
                string name = ShellMenus.NameOf(item.Header as string);

                FrontEndDescriptor descriptor = registry.Find(name);

                if (descriptor == null)
                {
                    continue;
                }

                bool needsAddress;

                try
                {
                    using (IFrontEnd created = descriptor.Create())
                    {
                        needsAddress = created is IRequiresResource;
                    }
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                if (needsAddress)
                {
                    item.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
                    return item;
                }
            }

            return null;
        }

        /// <summary>A front end that needs an address, standing in for a VISA driver.</summary>
        private sealed class AddressableFrontEnd : IRequiresResource
        {
            public string ResourceName { get; private set; } = string.Empty;

            public void UseResource(string resourceName)
            {
                ResourceName = resourceName;
            }
        }
    }
}
