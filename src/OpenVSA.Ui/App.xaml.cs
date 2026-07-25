using System.Windows;

namespace OpenVSA.Ui
{
    /// <summary>
    /// Application entry point for the OpenVSA WPF shell.
    /// </summary>
    public partial class App : Application
    {
        /// <inheritdoc />
        protected override void OnStartup(StartupEventArgs e)
        {
            // Before any Syncfusion control is constructed, which is why this is here and not in
            // the shell window's constructor.
            SyncfusionLicense.Register();

            base.OnStartup(e);
        }
    }
}
