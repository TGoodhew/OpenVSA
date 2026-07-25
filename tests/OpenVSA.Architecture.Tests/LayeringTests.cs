using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace OpenVSA.Architecture.Tests
{
    /// <summary>
    /// Automated enforcement of REQ-ARC-001 (strict acquisition/analysis separation).
    /// </summary>
    /// <remarks>
    /// The requirement states that layers L3 and above shall have no compile-time reference to
    /// any assembly in L0/L1 other than the HAL interface assembly, and that the DSP and
    /// measurement layers shall be incapable of distinguishing a live instrument from a file or
    /// simulator. That property is easy to state, easy to believe, and easy to lose to a single
    /// convenient using-directive — so it is asserted here rather than trusted.
    /// </remarks>
    public class LayeringTests
    {
        /// <summary>Concrete transport assemblies (L0). None may be referenced from L3 upward.</summary>
        private static readonly string[] ForbiddenTransportAssemblies =
        {
            "OpenVSA.Hal.Visa",
            "OpenVSA.Hal.File",
            "OpenVSA.Hal.Sim"
        };

        [Theory]
        [InlineData("OpenVSA.Dsp")]
        [InlineData("OpenVSA.Demod")]
        [InlineData("OpenVSA.Measurement")]
        [InlineData("OpenVSA.Personality")]
        [InlineData("OpenVSA.Api")]
        public void Analysis_Layers_Do_Not_Reference_Concrete_Transports(string assemblyName)
        {
            var assembly = Assembly.Load(assemblyName);

            var offenders = assembly
                .GetReferencedAssemblies()
                .Select(a => a.Name)
                .Where(n => ForbiddenTransportAssemblies.Contains(n, StringComparer.OrdinalIgnoreCase))
                .ToArray();

            Assert.True(
                offenders.Length == 0,
                $"REQ-ARC-001 violation: {assemblyName} references {string.Join(", ", offenders)}. " +
                "Layers L3 and above may reference only the OpenVSA.Hal interface assembly.");
        }

        [Fact]
        public void Dsp_Does_Not_Reference_The_Hal_At_All()
        {
            // Stricter than REQ-ARC-001 requires, and deliberately so: the DSP core consumes
            // IqBlock values from OpenVSA.Core and has no business knowing that front ends exist.
            var referenced = Assembly.Load("OpenVSA.Dsp")
                .GetReferencedAssemblies()
                .Select(a => a.Name)
                .ToArray();

            Assert.DoesNotContain("OpenVSA.Hal", referenced);
        }
    }
}
