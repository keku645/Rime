using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimeLib.Cmd.Scaleform;
using RimeLib.Cmd.UiBuilder;
using RimeUIEditor.View;

namespace RimeUIEditor
{
    /// <summary>
    /// Copy / paste / duplicate / delete of the selected widget or node (Ctrl+C, Ctrl+V, Ctrl+D, Delete — outside a
    /// text box). A copy is the node as the document would describe it: a document node as is; a shipped one turned
    /// into a new node with its widget, properties, binding (as an inline instance), fields and placement. A paste
    /// adds it next to the original (+20 px on the stage, +30 px on the graph) under a fresh name, one undo step.
    /// Delete takes a document node out with its stage ops; a shipped clip gets a remove op (the mod cannot delete a
    /// shipped node, only its clip); a shipped logic node stays.
    /// </summary>
    public partial class MainWindow
    {
        public static readonly RoutedCommand DuplicateCommand = new("Duplicate", typeof(MainWindow));

        class ClipItem
        {
            public string Json = "";           // the NodeEntry
            public bool IsWidget;
            public double X, Y, Sx = 1, Sy = 1, Rotation;   // the stage placement
            public double GraphX, GraphY;
            public string Label = "";
            public string? ClipVars;           // the placement's construct-time variables (vars op spelling), copied as they are
        }

        ClipItem? m_Clip;
        readonly List<ClipItem> m_Clips = new();   // a multi-selection copied: pasted together, same offset each

        /// <summary>Something copied and waiting to be pasted (test seam).</summary>
        public bool HasClip => m_Clip != null;

        /// <summary>The names to act on: the graph's whole selection when several boxes are selected there (the stage mirrors its widgets), else the stage's, else the single one.</summary>
        IReadOnlyList<string> SelectedLabels()
        {
            var s_Graph = m_Graph.SelectedLabels;
            if (s_Graph.Count > 1) return s_Graph;
            if (m_Stage.SelectedSet.Count > 1) return m_Stage.SelectedSet.ToList();
            var s_One = m_SelectedPlacement ?? m_SelectedNode;
            return s_One == null ? new List<string>() : new List<string> { s_One };
        }

        static bool InTextBox() => Keyboard.FocusedElement is TextBoxBase;

        void OnCopyCommand(object p_Sender, ExecutedRoutedEventArgs e) { if (!InTextBox()) CopySelected(); }
        void OnPasteCommand(object p_Sender, ExecutedRoutedEventArgs e) { if (!InTextBox()) PasteClip(); }
        void OnDuplicateCommand(object p_Sender, ExecutedRoutedEventArgs e) { if (!InTextBox()) DuplicateSelected(); }
        void OnDeleteCommand(object p_Sender, ExecutedRoutedEventArgs e) { if (!InTextBox()) DeleteSelected(); }

        /// <summary>The selected widget(s) or node as document nodes (a shipped one is described the way the document would add it).</summary>
        public bool CopySelected()
        {
            var s_Labels = SelectedLabels();
            if (m_Current == null || s_Labels.Count == 0) { Log("nothing selected to copy (pick a clip on the stage or a box on the graph)"); return false; }
            m_Clips.Clear();
            foreach (var s_Each in s_Labels) { if (CopyOne(s_Each, out var s_Item)) m_Clips.Add(s_Item!); }
            m_Clip = m_Clips.LastOrDefault();
            if (m_Clips.Count > 1) Log($"copied {m_Clips.Count} placements — Ctrl+V pastes them together");
            return m_Clips.Count > 0;
        }

        bool CopyOne(string s_Label, out ClipItem? p_Item)
        {
            p_Item = null;
            if (m_Current == null) return false;
            var s_Doc = DocScreen(m_Current.Partition)?.Nodes.FirstOrDefault(n => n.InstanceName == s_Label);
            NodeEntry s_Node;
            if (s_Doc != null) s_Node = JsonConvert.DeserializeObject<NodeEntry>(JsonConvert.SerializeObject(s_Doc))!;
            else
            {
                // the picked node's guid tells repeated labels apart, but only for the label it was picked under (a multi-selection copies by label)
                var s_Shipped = (m_SelectedKey != null ? m_Current.AllNodes.FirstOrDefault(n => n.Guid == m_SelectedKey && n.Label == s_Label) : null) ?? m_Current.AllNodes.FirstOrDefault(n => n.Label == s_Label);
                if (s_Shipped == null) { Log($"{s_Label}: not a node of this screen"); return false; }
                s_Node = new NodeEntry { InstanceName = s_Label, Type = s_Shipped.Type, New = true };
                var s_Info = UiTypeCatalog.Describe(s_Shipped.Type);
                if (s_Shipped.IsWidget)
                {
                    var w = m_Current.Widgets.FirstOrDefault(x => x.Guid == s_Shipped.Guid);
                    s_Node.Widget = w?.WidgetPartition; s_Node.FocusIndex = w?.FocusIndex ?? -1; s_Node.ZDepthLevel = w?.ZDepthLevel ?? 0;
                    foreach (var (k, v) in w?.Properties ?? new List<(string, string)>()) s_Node.Properties[k] = v;
                    var s_BindField = s_Info?.Field("DataBinding");
                    if (s_BindField != null && s_Shipped.Json["DataBinding"] is JObject s_Ref && ShippedToDoc(s_BindField, s_Ref) is JObject s_Inline && s_Inline["$type"] != null) s_Node.Fields["DataBinding"] = s_Inline;
                }
                else if (s_Info != null)
                    foreach (var f in s_Info.Fields)
                    {
                        if (f.Kind is UiTypeCatalog.Kind.Port or UiTypeCatalog.Kind.PortArray || f.Name is "Name" or "ParentGraph" or "IsRootNode" or "ParentIsScreen" || s_Shipped.Json[f.Name] == null) continue;
                        s_Node.Fields[f.Name] = ShippedToDoc(f, s_Shipped.Json[f.Name]!);
                    }
            }
            s_Node.Connections.Clear();   // wires name other nodes of this screen: a copy starts unwired
            var p = m_Current.Placements.FirstOrDefault(x => x.Name == s_Label);
            var s_Box = m_Graph.Boxes.FirstOrDefault(b => b.Label == s_Label);
            p_Item = new ClipItem
            {
                Json = JsonConvert.SerializeObject(s_Node), IsWidget = p != null, Label = s_Label,
                X = p?.X ?? 0, Y = p?.Y ?? 0, Sx = p?.SizeX ?? 1, Sy = p?.SizeY ?? 1, Rotation = p?.Rotation ?? 0,
                GraphX = s_Box?.X ?? 0, GraphY = s_Box?.Y ?? 0,
                ClipVars = p?.ClipVars is { Count: > 0 } s_Vars ? GfxMovie.FormatClipVars(s_Vars) : null,
            };
            Log($"copied {s_Label}" + (p != null ? $" ({s_Node.Widget}, {s_Node.Properties.Count} properties{(s_Node.Fields.ContainsKey("DataBinding") ? ", binding" : "")})" : $" ({s_Node.Type}, {s_Node.Fields.Count} fields)") + " — Ctrl+V pastes it next to the original");
            return true;
        }

        /// <summary>Adds the copied node(s) next to the original(s) under fresh names: a widget goes on the stage (+20,+20) with its scale and turn, a logic node on the graph (+30,+30). One undo step for the lot.</summary>
        public NodeEntry? PasteClip()
        {
            if (m_Clip == null || m_Clips.Count == 0) { Log("nothing to paste (Ctrl+C a clip or a box first)"); return null; }
            if (m_Current == null) { Log("open a screen to paste into"); return null; }
            NodeEntry? s_Last = null;
            var s_Names = new List<string>();
            var s_First = true;
            foreach (var c in m_Clips)
            {
                var n = PasteOne(c, s_First);
                s_First = false;
                if (n != null) { s_Last = n; s_Names.Add(n.InstanceName); }
            }
            if (s_Names.Count > 1) { m_Stage.Selected = s_Names[0]; foreach (var n in s_Names.Skip(1)) m_Stage.SelectToggle(n); Log($"pasted {s_Names.Count} placements: {string.Join(", ", s_Names)}"); }
            return s_Last;
        }

        NodeEntry? PasteOne(ClipItem c, bool p_UndoStep)
        {
            var s_Src = JsonConvert.DeserializeObject<NodeEntry>(c.Json)!;
            NodeEntry? s_New;
            if (c.IsWidget)
            {
                if (!m_Current!.HasStage || s_Src.Widget == null) { Log("a widget pastes on a screen (this is a flow graph)"); return null; }
                var s_Steps = m_Undo.Count;
                s_New = AddWidget(s_Src.Widget["ui/assets/".Length..], new Point(c.X + 20, c.Y + 20));   // one undo step: everything below rides on it
                if (s_New == null) return null;
                if (!p_UndoStep && m_Undo.Count > s_Steps) m_Undo.RemoveAt(m_Undo.Count - 1);   // a paste of several: one step for the lot
                s_New.FocusIndex = s_Src.FocusIndex; s_New.ZDepthLevel = s_Src.ZDepthLevel;
                s_New.Properties = new Dictionary<string, string>(s_Src.Properties);
                s_New.Fields = s_Src.Fields.ToDictionary(kv => kv.Key, kv => kv.Value.DeepClone());
                s_New.Binding = s_Src.Binding; s_New.Ports = s_Src.Ports;
                var s_Entry = DocScreen(m_Current.Partition)!;
                var i = s_Entry.Stage.FindIndex(o => o.StartsWith("add:") && o.Split(':')[2] == s_New.InstanceName);
                if (i >= 0) { var f = s_Entry.Stage[i].Split(':'); f[7] = StageCanvas.F(c.Sx); f[8] = StageCanvas.F(c.Sy); s_Entry.Stage[i] = string.Join(":", f); }
                if (System.Math.Abs(c.Rotation) > 0.005) s_Entry.Stage.Add($"rotate:{s_New.InstanceName}:{StageCanvas.F(c.Rotation)}");
                // the copied clip's own construct-time variables, in place of the widget's usual ones
                if (c.ClipVars != null)
                {
                    s_Entry.Stage.RemoveAll(o => o.StartsWith("vars:" + s_New.InstanceName + ":"));
                    s_Entry.Stage.Add($"vars:{s_New.InstanceName}:{c.ClipVars}");
                }
                m_SelectedPlacement = s_New.InstanceName; m_SelectedNode = s_New.InstanceName; m_SelectedKey = null;
                ApplyDocumentToCurrent();
                m_Stage.Selected = s_New.InstanceName;
            }
            else
            {
                var s_Steps = m_Undo.Count;
                s_New = AddNodeAt(s_Src.Type, c.GraphX + 30, c.GraphY + 30, n =>
                {
                    n.InstanceName = UniqueLabel(c.Label);
                    n.Fields = s_Src.Fields.ToDictionary(kv => kv.Key, kv => kv.Value.DeepClone());
                    n.Ports = s_Src.Ports;
                });
                if (s_New == null) return null;
                if (!p_UndoStep && m_Undo.Count > s_Steps) m_Undo.RemoveAt(m_Undo.Count - 1);
            }
            Log($"pasted {c.Label} as {s_New.InstanceName}");
            return s_New;
        }

        /// <summary>Ctrl+D: copy and paste in one go.</summary>
        public NodeEntry? DuplicateSelected() => CopySelected() ? PasteClip() : null;

        /// <summary>
        /// Delete: a document node goes away with its stage ops (its import too when nothing else uses the symbol) and the
        /// wires other document nodes had to it; a shipped clip gets a remove op (the node stays: the mod adds and edits
        /// nodes, it cannot delete a shipped one); a shipped logic node cannot go.
        /// </summary>
        public bool DeleteSelected()
        {
            var s_Labels = SelectedLabels();
            if (m_Current == null || s_Labels.Count == 0) { Log("nothing selected to delete"); return false; }
            var s_Any = false; var s_First = true;
            foreach (var s_Each in s_Labels) { if (DeleteOne(s_Each, s_First)) { s_Any = true; s_First = false; } }
            if (s_Labels.Count > 1 && s_Any) Log($"removed {s_Labels.Count} placements together");
            return s_Any;
        }

        bool DeleteOne(string s_Label, bool p_UndoStep)
        {
            var s_Entry = DocScreen(m_Current!.Partition);
            var s_Doc = s_Entry?.Nodes.FirstOrDefault(n => n.InstanceName == s_Label);
            var s_Placement = m_Current.Placements.FirstOrDefault(p => p.Name == s_Label);
            if (s_Doc == null && s_Placement == null) { Log($"{s_Label} is a shipped node: a mod adds and edits nodes, it cannot delete one"); return false; }
            if (p_UndoStep) PushUndo();
            if (s_Doc != null)
            {
                s_Entry!.Nodes.Remove(s_Doc);
                var s_Add = s_Entry.Stage.FirstOrDefault(o => o.StartsWith("add:") && o.Split(':')[2] == s_Label);
                s_Entry.Stage.RemoveAll(o => (o.StartsWith("add:") && o.Split(':')[2] == s_Label) || o.StartsWith("move:" + s_Label + ":") || o.StartsWith("scale:" + s_Label + ":") || o.StartsWith("rotate:" + s_Label + ":") || o.StartsWith("vars:" + s_Label + ":") || o == "remove:" + s_Label);
                if (s_Add != null)
                {
                    var s_Char = s_Add.Split(':')[3];
                    if (!s_Entry.Stage.Any(o => o.StartsWith("add:") && o.Split(':')[3] == s_Char)) s_Entry.Stage.RemoveAll(o => o.StartsWith("import:") && o.Split(':')[2] == s_Char);
                }
                foreach (var n in s_Entry.Nodes) n.Connections.RemoveAll(x => x.ToNode == s_Label);
                if (!s_Doc.New && s_Placement != null) { s_Entry.Stage.Add($"remove:{s_Label}"); Log($"removed the edits of {s_Label} and its clip (remove op); the shipped node itself stays"); }
                else Log($"removed {s_Label} from the document");
            }
            else
            {
                s_Entry = EnsureDocScreen(m_Current.Partition);
                s_Entry.Stage.Add($"remove:{s_Label}");
                Log($"{s_Label}: the clip is taken off the stage (remove op); the shipped node stays — the mod cannot delete it");
            }
            m_SelectedPlacement = null; m_SelectedNode = null; m_SelectedKey = null;
            m_Stage.Selected = null;
            ApplyDocumentToCurrent();
            return true;
        }
    }
}
