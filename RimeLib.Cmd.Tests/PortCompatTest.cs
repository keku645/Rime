using RimeLib.Cmd.UiBuilder;
using Xunit;

namespace RimeLib.Cmd.Tests
{
    /// <summary>
    /// "Compatible" on the graph is what the shipped connections do: output → input of another node, never into
    /// DataInputs, never onto a JumpNode's TargetPort; the catalogue's counts are the evidence.
    /// </summary>
    public class PortCompatTest
    {
        static PortCompatTest() { _ = typeof(fb.DialogNode); }

        static WidgetCatalog Catalog()
        {
            var c = new WidgetCatalog { TotalWires = 4387 };
            c.Wires[WidgetCatalog.WireKey("WidgetNode", "Outputs", "DataSetNode", "In")] = 309;
            c.Wires[WidgetCatalog.WireKey("DataSetNode", "Out", "WidgetNode", "Inputs")] = 67;
            return c;
        }

        [Fact]
        public void OutputToInputOfAnotherNodeIsAllowedWithTheGamesEvidence()
        {
            var v = PortCompat.Check("WidgetNode", "Outputs", false, "DataSetNode", "In", true, false, Catalog());
            Assert.True(v.Ok);
            Assert.Equal(309, v.Evidence);
            Assert.Contains("309", v.Reason);
            // a widget target: its input events live in Inputs whatever the port row says
            var w = PortCompat.Check("DataSetNode", "Out", false, "WidgetNode", "OnShow", true, false, Catalog());
            Assert.True(w.Ok);
            Assert.Equal(67, w.Evidence);
            // a pair the game never wires is allowed but says so
            var u = PortCompat.Check("RefreshNode", "Out", false, "DataToggleNode", "In", true, false, Catalog());
            Assert.True(u.Ok);
            Assert.Equal(0, u.Evidence);
            Assert.Contains("never", u.Reason);
            // without a catalogue: direction only
            Assert.True(PortCompat.Check("ActionNode", "Out", false, "ActionNode", "In", true, false, null).Ok);
        }

        [Fact]
        public void WrongDirectionSelfWiresDataInputsAndTargetPortAreRefused()
        {
            Assert.False(PortCompat.Check("DataSetNode", "In", true, "ActionNode", "In", true, false, Catalog()).Ok);
            Assert.False(PortCompat.Check("DataSetNode", "Out", false, "ActionNode", "Out", false, false, Catalog()).Ok);
            var s = PortCompat.Check("DataSetNode", "Out", false, "DataSetNode", "In", true, true, Catalog());
            Assert.False(s.Ok);
            Assert.Contains("itself", s.Reason);
            var d = PortCompat.Check("DataGetNode", "Out", false, "ActionNode", "DataInputs", true, false, Catalog());
            Assert.False(d.Ok);
            Assert.Contains("DataInputs", d.Reason);
            Assert.Contains("4387", d.Reason);
            Assert.False(PortCompat.Check("StateNode", "Outputs", false, "JumpNode", "TargetPort", true, false, Catalog()).Ok);
        }
    }
}
