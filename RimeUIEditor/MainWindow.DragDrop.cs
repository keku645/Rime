using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Newtonsoft.Json.Linq;
using RimeLib.Cmd.UiBuilder;
using RimeUIEditor.View;

namespace RimeUIEditor
{
    /// <summary>
    /// Drag and drop, with the targets the game's own data allows (measured over every UI graph):
    ///   . a widget asset (ui/assets/*) onto the STAGE → placement + WidgetNode where it lands (screens only); onto
    ///     the GRAPH → the same widget, its box where it lands and its clip at the stage's default spot;
    ///   . a screen (ui/flow/screen/*) onto the GRAPH of a flow graph → a StateNode showing it (a DialogNode for a
    ///     popup — every shipped StateNode/DialogNode lives in a UIGraphAsset, never in a screen);
    ///   . a data component (ui/uicomponents/*) onto the graph → a DataSetNode bound to it;
    ///   . a node type from the toolbox onto the graph → that node where it lands; onto the stage → the node on the
    ///     graph (in a fresh column) and the Graph tab comes up, since a node has no place on the stage;
    ///   . any partition onto a reference row of the properties (the row checks the type).
    /// The gesture starts on the explorer list, the palette and the toolbox (a label follows the mouse, the target
    /// that will take the drop gets a frame); every drag event is logged as "[drop] …" so a drop that goes nowhere
    /// can be read off the log. The drop handlers are plain methods (HandleStageDrop / HandleGraphDrop) so the seams
    /// can exercise them without OLE; the selftest can also run the real OLE gesture through the mouse.
    /// Wiring on the graph is "compatible" by the same measure: PortCompat over the widget catalogue's counts.
    /// </summary>
    public partial class MainWindow
    {
        public const string PartitionFormat = PropertyGrid.PartitionFormat;
        public const string NodeTypeFormat = "rime/nodetype";

        Point m_DragStart;
        object? m_DragSource;
        DataObject? m_DragData;     // the row under the PRESS: a drag that leaves the list on its first move still carries it
        DragGhost? m_Ghost;

        void InitDragDrop()
        {
            Viewport.CanDrop = d => m_Current != null && (d.GetDataPresent(NodeTypeFormat) || (d.GetDataPresent(PartitionFormat) && PartitionType((string)d.GetData(PartitionFormat)!) == "UIWidgetAsset" && m_Current.HasStage));
            Viewport.Dropped += (d, p) => HandleStageDrop(d, p);
            Viewport.DragEvent += s => Log("[drop] stage: " + s);
            GraphViewport.CanDrop = d => m_Current != null && (d.GetDataPresent(NodeTypeFormat) || (d.GetDataPresent(PartitionFormat) && GraphDropKind((string)d.GetData(PartitionFormat)!) != null));
            GraphViewport.Dropped += (d, p) => HandleGraphDrop(d, p);
            GraphViewport.DragEvent += s => Log("[drop] graph: " + s);
            PropertiesGrid.PartitionTypeOf = PartitionType;
            PropertiesGrid.TypeMatches = IsA;
            foreach (var t in UiTypeCatalog.NodeTypes().Where(t => t != "WidgetNode")) NodeToolbox.Items.Add(t);
            // the label that follows the mouse: GiveFeedback bubbles from the drag source to the window while OLE owns the mouse
            GiveFeedback += OnGiveFeedback;
            // the graph's wiring: every event of a widget's contract is a port; a wire is checked against the game's data
            m_Graph.ContractOf = ContractPortsOf;
            m_Graph.WireCheck = (f, fp, t, tp) => { var v = CheckWire(f, fp, t, tp); return new GraphView.WireVerdict(v.Ok, v.Reason, v.Evidence); };
            m_Graph.WireRejected += (f, fp, t, tp, r) => Log($"wire {f.Label}.{fp.Name} -> {t.Label}.{tp.Name} refused: {r}");
        }

        /// <summary>Whether a type is the base type or derives from it (UICustomizationCompData → UIComponentData; UIScreenAsset → UIGraphAsset), per the game's type information.</summary>
        static bool IsA(string p_Type, string p_Base)
        {
            if (string.Equals(p_Type, p_Base, StringComparison.OrdinalIgnoreCase)) return true;
            for (var t = UiTypeCatalog.Describe(p_Type); t != null && t.BaseName != ""; t = UiTypeCatalog.Describe(t.BaseName))
                if (string.Equals(t.BaseName, p_Base, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ------------------------------------------------------------------------------------------ sources

        /// <summary>The press remembers the row under the mouse: the move that starts the drag may already be outside the list (a row at its edge, dragged outward).</summary>
        void OnDragSourceDown(object p_Sender, MouseButtonEventArgs e)
        {
            m_DragStart = e.GetPosition(null);
            m_DragSource = p_Sender;
            m_DragData = DragDataOf(p_Sender, e);
        }

        /// <summary>What every mouse move over a drag source decided, last 24 (test seam: the real-mouse gesture reads it when no drag starts).</summary>
        public List<string> DragSourceTrace { get; } = new();
        void Note(string p_Text) { if (DragSourceTrace.Count >= 24) DragSourceTrace.RemoveAt(0); DragSourceTrace.Add(p_Text); }

        /// <summary>Past the system's drag threshold, the item under the mouse goes on the clipboard as a partition name or a node type.</summary>
        void OnDragSourceMove(object p_Sender, MouseEventArgs e)
        {
            var s_Where = e.GetPosition((IInputElement)p_Sender);
            if (e.LeftButton != MouseButtonState.Pressed) { Note($"({s_Where.X:0},{s_Where.Y:0}) button up"); return; }
            if (m_DragSource != p_Sender) { Note($"({s_Where.X:0},{s_Where.Y:0}) not the pressed list"); return; }
            var s_Now = e.GetPosition(null);
            if (System.Math.Abs(s_Now.X - m_DragStart.X) < SystemParameters.MinimumHorizontalDragDistance && System.Math.Abs(s_Now.Y - m_DragStart.Y) < SystemParameters.MinimumVerticalDragDistance) { Note($"({s_Where.X:0},{s_Where.Y:0}) below threshold"); return; }
            var s_Data = m_DragData;
            if (s_Data == null) { Note($"({s_Where.X:0},{s_Where.Y:0}) no row was pressed"); return; }
            Note($"({s_Where.X:0},{s_Where.Y:0}) drag started");
            m_DragSource = null; m_DragData = null;
            // the press that started this drag selected a row; a screen selected that way must not open under the drag
            CancelPendingOpen();
            var s_What = s_Data.GetDataPresent(NodeTypeFormat) ? (string)s_Data.GetData(NodeTypeFormat)! : (string)s_Data.GetData(PartitionFormat)!;
            Log($"[drop] dragging {StageViewport.Formats(s_Data)}");
            ShowGhost(DragLabel(s_Data));
            DragDropEffects s_Effect;
            try { s_Effect = DragDrop.DoDragDrop((DependencyObject)p_Sender, s_Data, DragDropEffects.Copy); }
            finally { HideGhost(); }
            Log($"[drop] {s_What} -> {(s_Effect == DragDropEffects.None ? "dropped nowhere (no target took it)" : s_Effect.ToString().ToLowerInvariant())}");
        }

        /// <summary>What the label under the mouse says: the thing dragged and what it makes where.</summary>
        string DragLabel(IDataObject p_Data)
        {
            if (p_Data.GetDataPresent(NodeTypeFormat)) return $"{p_Data.GetData(NodeTypeFormat)}  → graph";
            var s_Partition = (string)p_Data.GetData(PartitionFormat)!;
            var s_Type = PartitionType(s_Partition);
            var s_Short = s_Partition.Split('/').Last();
            if (s_Type == "UIWidgetAsset") return $"{s_Short} (widget)  → stage / graph";
            if (IsA(s_Type, "UIScreenAsset")) return $"{s_Short} (screen)  → flow graph: {(s_Partition.Contains("/popups/", StringComparison.OrdinalIgnoreCase) ? "DialogNode" : "StateNode")}";
            if (IsA(s_Type, "UIComponentData")) return $"{s_Short} (component)  → graph: DataSetNode";
            return $"{s_Short} ({(s_Type == "" ? "partition" : s_Type)})  → a reference field";
        }

        DataObject? DragDataOf(object p_Sender, MouseEventArgs e)
        {
            switch (p_Sender)
            {
                case ListView when p_Sender == ScreenList:
                {
                    var s_Item = ItemUnder<ListViewItem>(ScreenList, e)?.Content as ExplorerItem;
                    return s_Item == null ? null : new DataObject(PartitionFormat, s_Item.Partition);
                }
                case ListBox when p_Sender == PaletteList:
                {
                    var s_Short = ItemUnder<ListBoxItem>(PaletteList, e)?.Content as string;
                    return s_Short == null ? null : new DataObject(PartitionFormat, "ui/assets/" + s_Short);
                }
                case ListBox when p_Sender == NodeToolbox:
                {
                    var s_Type = ItemUnder<ListBoxItem>(NodeToolbox, e)?.Content as string;
                    return s_Type == null ? null : new DataObject(NodeTypeFormat, s_Type);
                }
            }
            return null;
        }

        static T? ItemUnder<T>(ItemsControl p_List, MouseEventArgs e) where T : DependencyObject
        {
            var s_Hit = p_List.InputHitTest(e.GetPosition(p_List)) as DependencyObject;
            while (s_Hit != null && s_Hit is not T) s_Hit = System.Windows.Media.VisualTreeHelper.GetParent(s_Hit);
            return s_Hit as T;
        }

        // ------------------------------------------------------------------------------------------ the label under the mouse

        [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
        [DllImport("user32.dll")] static extern bool GetCursorPos(out NativePoint p_Point);

        void ShowGhost(string p_Text)
        {
            HideGhost();
            var s_Layer = AdornerLayer.GetAdornerLayer(RootGrid);
            if (s_Layer == null) return;
            m_Ghost = new DragGhost(RootGrid, p_Text);
            s_Layer.Add(m_Ghost);
        }

        void HideGhost()
        {
            if (m_Ghost == null) return;
            AdornerLayer.GetAdornerLayer(RootGrid)?.Remove(m_Ghost);
            m_Ghost = null;
        }

        /// <summary>While OLE owns the mouse the window gets no mouse moves: the label follows the cursor's screen position.</summary>
        void OnGiveFeedback(object p_Sender, GiveFeedbackEventArgs e)
        {
            if (m_Ghost == null) return;
            try
            {
                if (GetCursorPos(out var s_Pos)) m_Ghost.MoveTo(RootGrid.PointFromScreen(new Point(s_Pos.X, s_Pos.Y)));
            }
            catch { /* the window is not on screen (a headless run): no label */ }
        }

        // ------------------------------------------------------------------------------------------ targets

        /// <summary>A widget dropped on the stage: added where it landed (the point becomes the placement's origin). A node type dropped here goes to the graph, which comes up.</summary>
        public bool HandleStageDrop(IDataObject p_Data, Point p_Stage)
        {
            if (p_Data.GetDataPresent(NodeTypeFormat))
            {
                var s_Type = (string)p_Data.GetData(NodeTypeFormat)!;
                var s_Node = AddNodeAt(s_Type, System.Math.Round(m_Graph.Extent().Width), 40, null);
                if (s_Node == null) return false;
                CentreTabs.SelectedIndex = 1;
                Log($"[drop] a {s_Type} has no place on the stage: {s_Node.InstanceName} was added to the graph (fresh column) and the Graph tab is up");
                return true;
            }
            if (!p_Data.GetDataPresent(PartitionFormat)) return false;
            var s_Partition = (string)p_Data.GetData(PartitionFormat)!;
            if (PartitionType(s_Partition) != "UIWidgetAsset" || !s_Partition.StartsWith("ui/assets/", StringComparison.OrdinalIgnoreCase)) { Log($"{s_Partition} is not a widget asset: only widgets go on the stage"); return false; }
            if (m_Current?.HasStage != true) { Log("a flow graph has no stage: widgets go on screens"); return false; }
            return AddWidget(s_Partition["ui/assets/".Length..], p_Stage) != null;
        }

        /// <summary>"StateNode" / "DialogNode" / "DataSetNode" / "WidgetNode" for what a partition makes on the graph, or null when nothing.</summary>
        string? GraphDropKind(string p_Partition)
        {
            var s_Type = PartitionType(p_Partition);
            if (IsA(s_Type, "UIScreenAsset")) return m_Current?.IsScreen == true ? null : p_Partition.Contains("/popups/", StringComparison.OrdinalIgnoreCase) ? "DialogNode" : "StateNode";
            if (IsA(s_Type, "UIComponentData")) return "DataSetNode";   // the components are derived types (UICustomizationCompData…)
            if (IsA(s_Type, "UIWidgetAsset")) return m_Current?.HasStage == true ? "WidgetNode" : null;
            return null;
        }

        /// <summary>A node type or a partition dropped on the graph: the node the data allows, where it landed.</summary>
        public NodeEntry? HandleGraphDrop(IDataObject p_Data, Point p_Graph)
        {
            if (m_Current == null) return null;
            if (p_Data.GetDataPresent(NodeTypeFormat))
                return AddNodeAt((string)p_Data.GetData(NodeTypeFormat)!, p_Graph.X, p_Graph.Y, null);
            if (!p_Data.GetDataPresent(PartitionFormat)) return null;
            var s_Partition = (string)p_Data.GetData(PartitionFormat)!;
            var s_Kind = GraphDropKind(s_Partition);
            var s_Short = s_Partition.Split('/').Last();
            switch (s_Kind)
            {
                case "StateNode":
                case "DialogNode":
                {
                    // the shipped StateNodes are named after their screen (ComCenterScreen → "ComCenterScreen")
                    var s_Label = NiceName(s_Partition);
                    return AddNodeAt(s_Kind, p_Graph.X, p_Graph.Y, n =>
                    {
                        n.InstanceName = UniqueLabel(s_Label);
                        n.Fields["Screen"] = s_Partition;
                        if (s_Kind == "DialogNode") { n.Fields["DialogTitle"] = ""; n.Fields["DialogText"] = ""; }
                    });
                }
                case "DataSetNode":
                {
                    var s_Comp = m_WidgetCatalog?.Component(s_Partition);
                    var s_Label = "Set" + DataKeys.ComponentShortName(s_Comp?.Name ?? s_Short).Replace("Comp", "");
                    return AddNodeAt("DataSetNode", p_Graph.X, p_Graph.Y, n =>
                    {
                        n.InstanceName = UniqueLabel(s_Label);
                        n.Fields["DataSource"] = new JObject { ["DataName"] = "", ["DataCategory"] = s_Partition, ["DataKey"] = 0, ["UseDirectAccess"] = false, ["UpdateOnInitialize"] = true };
                    });
                }
                case "WidgetNode":
                {
                    // the box lands where it was dropped; the clip goes to the stage's default spot (drag it there on the Stage tab)
                    var s_Node = AddWidget(s_Partition["ui/assets/".Length..], null, p_Graph);
                    if (s_Node != null) Log($"[drop] {s_Node.InstanceName}'s clip sits at the stage's default spot (100,100): move it on the Stage tab");
                    return s_Node;
                }
                default:
                    if (IsA(PartitionType(s_Partition), "UIScreenAsset")) Log("a screen goes on the graph of a FLOW GRAPH (ui/flow/graph/*) as a StateNode — the game's screens never hold one");
                    else Log($"nothing on the graph takes {s_Partition} ({PartitionType(s_Partition)})");
                    return null;
            }
        }

        /// <summary>The asset's own Name has the casing (UI/Flow/Screen/ComCenterScreen); the partition name is lower-case.</summary>
        string NiceName(string p_Partition)
        {
            if (DocScreen(p_Partition) is { New: true } s_NewScreen) return RimeLib.Cmd.UiBuilder.NewScreen.Clean(s_NewScreen.Title ?? p_Partition.Split('/').Last());
            try
            {
                var s_Json = m_Rime.PartitionJson(p_Partition);
                var s_Name = (string?)(s_Json["Instances"] as JObject)?[(string?)s_Json["PrimaryInstanceGuid"] ?? ""]?["Name"];
                if (!string.IsNullOrEmpty(s_Name)) return s_Name.Split('/').Last();
            }
            catch { }
            return p_Partition.Split('/').Last();
        }

        string UniqueLabel(string p_Base)
        {
            var s_Entry = DocScreen(m_Current!.Partition);
            var s_Names = m_Current.AllNodes.Select(n => n.Label).Concat(s_Entry?.Nodes.Select(n => n.InstanceName) ?? Enumerable.Empty<string>()).ToHashSet();
            if (!s_Names.Contains(p_Base)) return p_Base;
            var i = 2;
            while (s_Names.Contains(p_Base + "_" + i.ToString("00"))) i++;
            return p_Base + "_" + i.ToString("00");
        }

        void OnAddNodeFromToolbox(object p_Sender, MouseButtonEventArgs e)
        {
            if (NodeToolbox.SelectedItem is string s_Type && m_Current != null) AddNodeAt(s_Type, System.Math.Round(m_Graph.Extent().Width), 40, null);
        }

        // ------------------------------------------------------------------------------------------ wiring: what is compatible

        /// <summary>The widget asset a graph box instantiates (document node first, then the shipped one).</summary>
        string? WidgetAssetOf(GraphView.Box p_Box)
        {
            if (p_Box.Type != "WidgetNode") return null;
            if (p_Box.Doc?.Widget != null) return p_Box.Doc.Widget;
            return p_Box.Shipped != null ? m_Current?.Widgets.FirstOrDefault(w => w.Guid == p_Box.Shipped.Guid)?.WidgetPartition : null;
        }

        /// <summary>The events of the box's widget contract (from the asset's WidgetEvents, measured into the catalogue).</summary>
        IEnumerable<(string Query, bool IsOutput)> ContractPortsOf(GraphView.Box p_Box)
        {
            var s_Asset = WidgetAssetOf(p_Box);
            var s_Info = s_Asset != null ? m_WidgetCatalog?.Get(s_Asset) : null;
            if (s_Info == null) return Enumerable.Empty<(string, bool)>();
            return s_Info.Events.Select(e => (e.Query, e.IsOutput));
        }

        /// <summary>Whether a wire from one box port to another is allowed, by the rules the shipped connections follow, with the game's evidence.</summary>
        PortCompat.Verdict CheckWire(GraphView.Box p_From, GraphView.BoxPort p_FromPort, GraphView.Box p_To, GraphView.BoxPort p_ToPort)
        {
            if (p_ToPort.Dangling) return new PortCompat.Verdict(false, "a dangling port (named by a shipped connection, in no slot of the node) takes no wire", 0);
            return PortCompat.Check(p_From.Type, p_FromPort.Field, p_FromPort.IsInput, p_To.Type, p_ToPort.Field, p_ToPort.IsInput, p_From.Key == p_To.Key, m_WidgetCatalog);
        }

        /// <summary>A port as a document connection spells it: a widget event by name, a single port by its field, an array port as Field:Name.</summary>
        static string PortSpecOf(GraphView.Box p_Box, GraphView.BoxPort p_Port)
        {
            if (p_Box.Type == "WidgetNode") return p_Port.Name;
            if (p_Port.Field is "Outputs" or "Inputs" or "DataInputs") return p_Port.Field + ":" + p_Port.Name;
            return p_Port.Field;
        }

        /// <summary>
        /// Every wire a node could fire, as "port -> Node.port" the way the Connections rows spell it: each output port
        /// (the widget's whole contract included) to each input port the rules allow, the pairs the game wires most first.
        /// </summary>
        public List<string> CompatibleTargets(string p_Label)
        {
            var s_From = m_Graph.Boxes.FirstOrDefault(b => b.Label == p_Label);
            if (s_From == null) return new List<string>();
            var s_List = new List<(string Text, int Evidence)>();
            foreach (var s_Out in s_From.Outputs.Where(p => !p.Dangling))
                foreach (var s_To in m_Graph.Boxes)
                    foreach (var s_In in s_To.Inputs)
                    {
                        var v = CheckWire(s_From, s_Out, s_To, s_In);
                        if (v.Ok) s_List.Add(($"{PortSpecOf(s_From, s_Out)} -> {s_To.Label}.{PortSpecOf(s_To, s_In)}", v.Evidence));
                    }
            return s_List.OrderByDescending(x => x.Evidence).ThenBy(x => x.Text, StringComparer.OrdinalIgnoreCase).Select(x => x.Text).Distinct().ToList();
        }
    }
}
