using System;
using System.IO;
using System.Runtime;
using System.Windows;
using OpenVSA.Core;
using OpenVSA.Core.Threading;
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
            // REQ-NFR-030: refuse below the platform floor with a message naming the unmet
            // requirement, rather than failing obscurely somewhere in the load. In the constructor
            // because it must run before InitializeComponent touches WPF: a 32-bit process that
            // reached a XAML parse would report a BadImageFormatException from a resource
            // dictionary, which tells the person in front of it nothing.
            string unmet = PlatformRequirements.Unmet();

            if (unmet != null)
            {
                MessageBox.Show(
                    unmet,
                    "OpenVSA cannot run on this system",
                    MessageBoxButton.OK,
                    MessageBoxImage.Stop);

                Environment.Exit(2);
            }

            // REQ-NFR-010: the thread this runs on is the dispatcher thread, and marking it here is
            // what lets every layer below assert that it is not doing DSP or I/O on it. Debug
            // builds only - the call compiles away in Release.
            ThreadAffinity.MarkUiThread();

            // The policy itself lives in SyncfusionLicense.Register so that the paths which do not
            // go through App -- the test host builds a ShellWindow on its own STA thread -- can
            // reach it too. This call stays here, and stays in the constructor, for the reason the
            // remarks above give: it must run before InitializeComponent loads App.xaml.
            SyncfusionLicense.Register();

            StartProfileGuidedJit();
        }

        /// <summary>
        /// Lets the runtime jit the start-up path on the other cores (<c>REQ-NFR-025</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Measured before it was chosen.</strong> The cold-start harness now reports each
        /// launch in four phases, and it says the whole of the overrun is before a window exists:
        /// 6.80 s to the window against 0.87 s warm, while the menu, the connection and the first
        /// frame together take 0.60 s and are the same figure cold or warm. So the cost is loading
        /// and jitting assemblies, and nothing in the shell's own start-up work is worth touching.
        /// </para>
        /// <para>
        /// Multicore JIT is the runtime's answer to precisely that: the first run records which
        /// methods the start-up path jits, and every run after it compiles them on a background core
        /// in parallel with the main thread's work rather than one at a time on demand.
        /// </para>
        /// <para>
        /// <strong>It does nothing on the very first launch</strong> — there is no profile yet — and
        /// that is worth being clear about rather than claiming a figure it will not deliver. It is
        /// the second launch onward that gains, which is every launch a user makes after the first.
        /// </para>
        /// <para>
        /// In the constructor, before <c>InitializeComponent</c>, because a profile started after the
        /// resource dictionaries have loaded has missed the part of the run it exists to help. And it
        /// throws nothing worth propagating: a profile that cannot be written is a start-up that is
        /// merely no faster, so a failure here must not stop the application launching.
        /// </para>
        /// </remarks>
        private static void StartProfileGuidedJit()
        {
            try
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OpenVSA");

                Directory.CreateDirectory(root);

                ProfileOptimization.SetProfileRoot(root);
                ProfileOptimization.StartProfile("startup.profile");
            }
            catch (Exception)
            {
                // Deliberately swallowed, and the only place in this file that is. Every failure
                // here -- no write access, a full disk, a policy that forbids the folder -- costs
                // nothing but the speed-up, and refusing to start over it would be absurd.
            }
        }
    }
}
