using System;

namespace RimeLib.Cmd.UiBuilder
{
    /// <summary>
    /// Whether a wire from one port to another is one the game's data allows, and how much of the shipped UI
    /// does the same. The rules are measured, not designed (every ui/flow partition of the game, 4387 connections
    /// with both ports on their nodes):
    ///   . a source is always an output slot (Out / True / False / Outputs[] / a widget event it fires) and a target
    ///     always an input slot (In / Show / Hide / Inputs[] / a widget event it receives) — no exception;
    ///   . no connection ever joins a node to itself;
    ///   . DataInputs (ActionNode) is never wired — parameters travel in Params;
    ///   . JumpNode.TargetPort is a reference field, not a slot a wire lands on;
    ///   . everything else is allowed; the catalogue says how often the game wires that pair of slots
    ///     (WidgetNode.Outputs → DataSetNode.In 309 times, DataSetNode.Out → WidgetNode.Inputs 67…), so a pair the
    ///     game never uses is allowed but flagged as untested.
    /// </summary>
    public static class PortCompat
    {
        public readonly struct Verdict
        {
            public readonly bool Ok;
            public readonly string Reason;
            /// <summary>How many shipped connections join this pair of slots; -1 when no catalogue was given.</summary>
            public readonly int Evidence;
            public Verdict(bool p_Ok, string p_Reason, int p_Evidence) { Ok = p_Ok; Reason = p_Reason; Evidence = p_Evidence; }
            public override string ToString() => (Ok ? "ok" : "no") + ": " + Reason;
        }

        /// <summary>The slot a port lives in, normalised: a widget's events are its Outputs / Inputs arrays.</summary>
        public static string Slot(string p_Type, string p_Field, bool p_IsInput)
        {
            if (string.Equals(p_Type, "WidgetNode", StringComparison.OrdinalIgnoreCase)) return p_IsInput ? "Inputs" : "Outputs";
            return p_Field;
        }

        public static Verdict Check(string p_FromType, string p_FromField, bool p_FromIsInput, string p_ToType, string p_ToField, bool p_ToIsInput, bool p_SameNode, WidgetCatalog? p_Catalog)
        {
            var s_Total = p_Catalog?.TotalWires ?? 0;
            var s_Corpus = s_Total > 0 ? $"0 of the game's {s_Total} connections" : "none of the game's connections";
            if (p_FromIsInput) return new Verdict(false, $"{p_FromField} receives: a wire starts at an output (Out / True / False / Outputs / an event the widget fires)", 0);
            if (!p_ToIsInput) return new Verdict(false, $"{p_ToField} fires: a wire ends at an input (In / Show / Hide / Inputs / an event the widget receives)", 0);
            if (p_SameNode) return new Verdict(false, $"a node never wires to itself ({s_Corpus} do)", 0);
            var s_FromSlot = Slot(p_FromType, p_FromField, false);
            var s_ToSlot = Slot(p_ToType, p_ToField, true);
            if (string.Equals(s_ToSlot, "DataInputs", StringComparison.OrdinalIgnoreCase))
                return new Verdict(false, $"DataInputs is never wired in the game's data ({s_Corpus}): an ActionNode's parameters go in Params", 0);
            if (string.Equals(p_ToType, "JumpNode", StringComparison.OrdinalIgnoreCase) && string.Equals(p_ToField, "TargetPort", StringComparison.OrdinalIgnoreCase))
                return new Verdict(false, "TargetPort is a reference to another node's port, not a slot: set it in the JumpNode's properties", 0);
            if (p_Catalog == null) return new Verdict(true, "direction ok (no catalogue to measure the pair against)", -1);
            var s_Count = p_Catalog.WireCount(p_FromType, s_FromSlot, p_ToType, s_ToSlot);
            return s_Count > 0
                ? new Verdict(true, $"the game wires {p_FromType}.{s_FromSlot} → {p_ToType}.{s_ToSlot} {s_Count}×", s_Count)
                : new Verdict(true, $"allowed by direction, but the game never wires {p_FromType}.{s_FromSlot} → {p_ToType}.{s_ToSlot} (untested)", 0);
        }
    }
}
