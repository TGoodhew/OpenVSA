using OpenVSA.Core;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Core.Tests
{
    /// <summary>
    /// <c>REQ-NFR-030</c>: the platform floor, and the message that names what is unmet.
    /// </summary>
    public class PlatformRequirementsTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the detected build is written.</param>
        public PlatformRequirementsTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void ThisMachineMeetsTheFloor()
        {
            _output.WriteLine(
                "Windows build " + PlatformRequirements.WindowsBuild() +
                ", " + (System.IntPtr.Size * 8) + "-bit process");

            Assert.Null(PlatformRequirements.Unmet());
            Assert.True(PlatformRequirements.AreMet());
        }

        [Fact]
        public void TheBuildNumberIsReadNotGuessed()
        {
            // From RtlGetVersion rather than Environment.OSVersion, which the compatibility shim
            // pins at 6.2 without a matching manifest entry. A floor built on the managed property
            // would refuse to run on the very systems it is meant to allow.
            int build = PlatformRequirements.WindowsBuild();

            Assert.True(
                build >= PlatformRequirements.MinimumWindowsBuild,
                "Reported build " + build + " is below the 21H2 floor of " +
                PlatformRequirements.MinimumWindowsBuild +
                ", which would mean the version is being read through the compatibility shim.");
        }

        [Fact]
        public void TheFloorIsABuildNumberAndNotAVersion()
        {
            // Windows reports 10.0 for every release from Windows 10 through Windows 11, so a
            // major/minor comparison cannot distinguish 21H2 from anything else and would pass on
            // a build years too old. Recording the constant here so a later "simplification" to
            // Environment.OSVersion.Version has to argue with a test.
            Assert.Equal(19044, PlatformRequirements.MinimumWindowsBuild);
        }
    }
}
