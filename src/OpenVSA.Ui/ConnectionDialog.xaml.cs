using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using OpenVSA.Hal;

namespace OpenVSA.Ui
{
    /// <summary>
    /// Lists the resources a transport can reach, so a user can pick one (<c>REQ-HAL-003</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window is deliberately thin. Everything the criterion is about — which resources appear,
    /// what each is identified as, which are marked as having a driver — is
    /// <see cref="ConnectionListing"/>'s, and is tested without a window. What is left here is
    /// showing it, enabling Connect only on a row worth connecting to, and refreshing.
    /// </para>
    /// <para>
    /// Reached by choosing a front end that implements <see cref="IRequiresResource"/>, not from a
    /// menu entry of its own: <c>REQ-UI-061</c> fixes the menu contents as an exact list, so a
    /// dialog that a driver needs is opened by the act of choosing that driver.
    /// </para>
    /// </remarks>
    public partial class ConnectionDialog : Window
    {
        private readonly Func<IReadOnlyList<DiscoveredResource>> _discover;
        private readonly bool _canEnumerate;

        /// <summary>Creates the dialog over a supplied enumeration.</summary>
        /// <param name="discover">Lists resources; called again on Refresh.</param>
        /// <param name="canEnumerate">Whether any transport can enumerate at all.</param>
        /// <exception cref="ArgumentNullException"><paramref name="discover"/> is null.</exception>
        /// <remarks>
        /// Supplied rather than reached for, because <c>REQ-ARC-001</c> bars this assembly from
        /// referencing a transport. The shell passes
        /// <see cref="FrontEndRegistry.DiscoverResources"/> in.
        /// </remarks>
        public ConnectionDialog(
            Func<IReadOnlyList<DiscoveredResource>> discover, bool canEnumerate)
        {
            _discover = discover ?? throw new ArgumentNullException(nameof(discover));
            _canEnumerate = canEnumerate;

            InitializeComponent();

            Resources.SelectionChanged += (sender, e) => UpdateConnectButton();
            Resources.MouseDoubleClick += (sender, e) => Accepted(Accept());

            RefreshButton.Click += (sender, e) => Reload();
            ConnectButton.Click += (sender, e) => Accepted(Accept());

            Reload();
        }

        /// <summary>The resource the user chose, or null.</summary>
        public string ChosenResource { get; private set; }

        /// <summary>The listing currently shown.</summary>
        public ConnectionListing Listing { get; private set; }

        /// <summary>Re-runs discovery and repopulates the grid.</summary>
        /// <remarks>
        /// A failing enumeration is shown in the summary rather than thrown out of a dialog the
        /// user opened on purpose. Losing the window would leave them with no way to see what went
        /// wrong, which is the state the dialog exists to get them out of.
        /// </remarks>
        public void Reload()
        {
            IReadOnlyList<DiscoveredResource> found;

            try
            {
                found = _discover() ?? new DiscoveredResource[0];
            }
            catch (Exception e)
            {
                found = new[]
                {
                    new DiscoveredResource("(none)", null, "enumeration failed — " + e.Message, null),
                };
            }

            Listing = new ConnectionListing(found, _canEnumerate);

            SummaryText.Text = Listing.Summary;
            Resources.ItemsSource = Listing.Rows;

            UpdateConnectButton();
        }

        /// <summary>Selects a row by resource name, for tests and for restoring a choice.</summary>
        /// <param name="resourceName">The resource string to select.</param>
        /// <returns>Whether a row with that name was found.</returns>
        public bool Select(string resourceName)
        {
            foreach (ConnectionRow row in Listing.Rows)
            {
                if (string.Equals(row.Resource, resourceName, StringComparison.OrdinalIgnoreCase))
                {
                    Resources.SelectedItem = row;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Takes the selected row as the answer, if it is one that can be taken.</summary>
        /// <returns>Whether a choice was made.</returns>
        /// <remarks>
        /// Deliberately does not touch <see cref="Window.DialogResult"/>. That property throws
        /// unless the window was shown with <c>ShowDialog</c>, so setting it here would make the
        /// decision impossible to test without a modal window — and a modal window in an automated
        /// run blocks the dispatcher until something dismisses it, which nothing will.
        /// </remarks>
        internal bool Accept()
        {
            var row = Resources.SelectedItem as ConnectionRow;

            if (row == null || !row.CanConnect)
            {
                return false;
            }

            ChosenResource = row.Resource;
            return true;
        }

        /// <summary>Closes the window once a row has been taken.</summary>
        /// <param name="accepted">Whether a row was taken.</param>
        /// <remarks>
        /// Plain <see cref="Window.Close"/> rather than <c>DialogResult</c>, because this window is
        /// shown modelessly: <c>REQ-UI-070</c> bars the shell from putting anything in front of the
        /// measurement, and a bus scan is exactly the wrong thing to freeze it behind — thirty GPIB
        /// addresses at 700 ms each is twenty seconds. The answer is read from
        /// <see cref="ChosenResource"/> after the window closes, which is null when it was not
        /// taken.
        /// </remarks>
        private void Accepted(bool accepted)
        {
            if (accepted)
            {
                Close();
            }
        }

        private void UpdateConnectButton()
        {
            var row = Resources.SelectedItem as ConnectionRow;

            // Disabled on a row nothing drives, rather than enabled and then failing. The row is
            // still selectable and still readable, which is the point of listing it.
            ConnectButton.IsEnabled = row != null && row.CanConnect;
        }
    }
}
