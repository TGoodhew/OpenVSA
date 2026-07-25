using OpenVSA.Core;
using Xunit;

namespace OpenVSA.Core.Tests
{
    public class FrontEndIdTests
    {
        [Fact]
        public void Unset_Identifier_Yields_Empty_Value()
        {
            Assert.Equal(string.Empty, FrontEndId.None.Value);
        }

        [Fact]
        public void Identifiers_Compare_By_Ordinal_Value()
        {
            Assert.Equal(new FrontEndId("GPIB0::17::INSTR"), new FrontEndId("GPIB0::17::INSTR"));
            Assert.NotEqual(new FrontEndId("sim"), new FrontEndId("Sim"));
        }
    }
}
