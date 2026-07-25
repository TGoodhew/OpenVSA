using System.Windows;
using Syncfusion.Licensing;

namespace OpenVSA.Ui
{
    /// <summary>
    /// Application entry point for the OpenVSA WPF shell.
    /// </summary>
    public partial class App : Application
    {
        /// <summary>Creates the application and registers the Syncfusion licence.</summary>
        /// <remarks>
        /// <para>
        /// <strong>Registration belongs in the constructor, not <c>OnStartup</c>. Do not move
        /// it.</strong> The generated entry point runs <c>new App()</c> before
        /// <c>app.InitializeComponent()</c>, and <c>InitializeComponent</c> is what loads
        /// <c>App.xaml</c> and its merged resource dictionaries. <c>OnStartup</c> runs later
        /// still, during <c>Run()</c>. If a Syncfusion theme dictionary is ever merged into
        /// <c>App.xaml</c>, registering in <c>OnStartup</c> would happen after those controls
        /// had already been constructed — the banner would appear despite a valid key, and the
        /// cause would be some distance from the symptom.
        /// </para>
        /// <para>
        /// A missing key is skipped silently: the application launches in trial mode rather than
        /// failing to start over a cosmetic banner.
        /// </para>
        /// </remarks>
        public App()
        {
            string key = SyncfusionLicense.ResolveKey();

            if (!string.IsNullOrEmpty(key))
            {
                SyncfusionLicenseProvider.RegisterLicense(key);
                SyncfusionLicense.IsRegistered = true;
            }
        }
    }
}
