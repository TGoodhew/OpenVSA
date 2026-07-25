using System.Windows;

namespace OpenVSA.Ui
{
    /// <summary>
    /// Main application shell: a docking-window host, not an instrument emulator (REQ-UI-001).
    /// </summary>
    public partial class ShellWindow : Window
    {
        /// <summary>Creates the shell window.</summary>
        public ShellWindow()
        {
            InitializeComponent();
        }
    }
}
