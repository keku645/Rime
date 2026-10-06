using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using RimeLib.Cmd.UiBuilder;
using RimeUIEditor.Model;

namespace RimeUIEditor.View
{
    /// <summary>
    /// The screen graph as boxes and wires: every shipped node (widgets and logic) with its ports, the document's
    /// nodes on top, the shipped connections and the document's. Drag a box to move it, click to select it,
    /// Ctrl+click to add to / take out of the selection, drag on empty graph to band-select, drag any selected box
    /// to move the lot; drag from an output port to an input port to wire (the document records the connection);
    /// Alt+click a wire to disconnect it (the window decides what that means for a shipped wire). Lives inside a
    /// StageViewport for zoom/pan. Shipped wires land on their port by the port's guid; document wires by name.
    /// A wire whose port cannot be found is drawn red to the box's corner and counted, never moved to another port.
    /// </summary>
    public class GraphView : Canvas
    {
        public const double GraphWidth = 2600, GraphHeight = 1800;
        public const double BoxWidth = 190;
        const double TitleHeight = 34, PortRow = 18, PortDot = 9;

        public class BoxPort
        {
            public string Field = "", Name = "", Key = "";   // Key: the port guid (shipped) or the name (document)
            public bool IsInput, Dangling;
            /// <summary>An event of the widget's contract with no port on the node yet: wiring it creates the port (drawn hollow).</summary>
            public bool Contract;
        }

        /// <summary>A wire's compatibility verdict for one target port while a wire is being dragged.</summary>
        public readonly struct WireVerdict
        {
            public readonly bool Ok; public readonly string Reason; public readonly int Evidence;
            public WireVerdict(bool p_Ok, string p_Reason, int p_Evidence) { Ok = p_Ok; Reason = p_Reason; Evidence = p_Evidence; }
        }

        public class Box
        {
            public string Key = "";                 // unique: the shipped node's guid, or "doc:<label>" for a document-only node
            public string Label = "", Type = "";
            public NodeInfo? Shipped;
            public NodeEntry? Doc;
            public double X, Y, Height;
            public List<BoxPort> Inputs = new(), Outputs = new();
            public List<string> Notes = new();      // extra lines under the type (port references)
            public bool IsDoc => Doc != null;
            public bool IsNew => Doc != null && Doc.New;
        }

        public class Wire
        {
            public string From = "", FromPort = "", To = "", ToPort = "";   // box keys and port keys
            public bool IsDoc, Dangling;
            public bool Resolved;                   // set by Redraw: both ports found
            /// <summary>The shipped UINodeConnection's instance guid (what the mod erases when the wire is disconnected); "" for a document wire.</summary>
            public string Guid = "";
            /// <summary>A document wire: the node entry it lives in and the connection itself.</summary>
            public NodeEntry? DocNode; public ConnectionEntry? DocConn;
            /// <summary>"From.port -> To.port" with the boxes' labels (set by Redraw).</summary>
            public string Label = "";
            internal Geometry? Geometry;            // the drawn curve, for the hit test of Alt+click
        }

        readonly List<Box> m_Boxes = new();
        readonly Dictionary<string, Point> m_Remembered = new();   // shipped node key -> position (this screen)
        readonly List<Wire> m_Wires = new();
        string m_Screen = "";
        Box? m_Selected;
        readonly HashSet<string> m_SelectedKeys = new();            // every selected box (the primary included), by key
        Box? m_Drag; Point m_DragStart; readonly Dictionary<Box, Point> m_DragOrigins = new();
        bool m_Banding; Point m_BandStart; Rect m_Band;
        Wire? m_HoverWire;                                          // the wire under the pointer while Alt is held
        (Box Box, string Field, string Name)? m_WireFrom; Line? m_WireLine;

        public event Action<Box?>? SelectionChanged;
        public event Action<Box, string, string, Box, string, string>? WireRequested;   // from, field, port, to, field, port
        /// <summary>A wire dropped on a port the rules refuse: from, to, the reason (the window logs it).</summary>
        public event Action<Box, BoxPort, Box, BoxPort, string>? WireRejected;
        /// <summary>Alt+click on a wire: the window takes it out of the document (a document wire) or has the mod erase it (a shipped one).</summary>
        public event Action<Wire>? WireRemoveRequested;
        public event Action<Box>? NodeMoving;     // a document node is about to take a new position (once per drag, however many boxes move)
        public event Action<Box>? NodeMoved;
        /// <summary>The events of a widget box's contract (query, fired-by-widget), so every event is a port to wire even before the node has one.</summary>
        public Func<Box, IEnumerable<(string Query, bool IsOutput)>>? ContractOf;
        /// <summary>Whether a wire may join two ports (from, fromPort, to, toPort): the window answers with the game's data.</summary>
        public Func<Box, BoxPort, Box, BoxPort, WireVerdict>? WireCheck;
        readonly Dictionary<BoxPort, WireVerdict> m_WireTargets = new();

        /// <summary>The viewport's zoom, so a wire's hit band keeps a screen size.</summary>
        public double ViewScale { get; set; } = 1.0;

        public GraphView()
        {
            Width = GraphWidth; Height = GraphHeight;
            Background = new SolidColorBrush(Color.FromRgb(20, 22, 26));
            Focusable = true;
            MouseLeftButtonDown += OnDown; MouseMove += OnMove; MouseLeftButtonUp += OnUp;
        }

        public Box? Selected => m_Selected;
        /// <summary>Every selected box, by key (the primary included).</summary>
        public IReadOnlyCollection<string> SelectedKeys => m_SelectedKeys;
        /// <summary>The labels of the selected boxes (what the clipboard and Delete act on).</summary>
        public IReadOnlyList<string> SelectedLabels => m_Boxes.Where(b => m_SelectedKeys.Contains(b.Key)).Select(b => b.Label).Distinct().ToList();
        public IEnumerable<Box> Boxes => m_Boxes;
        public IEnumerable<Wire> Wires => m_Wires;
        /// <summary>Wires the last Redraw could not attach to a port on both ends (a model bug, never silent).</summary>
        public int UnresolvedWires { get; private set; }
        /// <summary>Shipped wires the document disconnected (not drawn: the mod erases them at load).</summary>
        public int RemovedWires { get; private set; }
        /// <summary>The wire lit under the pointer while Alt is held (a click disconnects it).</summary>
        public Wire? HoverWire => m_HoverWire;

        /// <summary>The area the boxes occupy (plus a margin): what Fit should frame. The canvas is at least GraphWidth × GraphHeight and grows to it (a flow graph has 270 boxes).</summary>
        public Size Extent()
        {
            if (m_Boxes.Count == 0) return new Size(800, 600);
            var w = m_Boxes.Max(b => b.X + BoxWidth) + 60; var h = m_Boxes.Max(b => b.Y + b.Height) + 60;
            return new Size(w, h);
        }

        void GrowToExtent()
        {
            var s_Extent = Extent();
            Width = System.Math.Max(GraphWidth, s_Extent.Width); Height = System.Math.Max(GraphHeight, s_Extent.Height);
        }

        public static Rect RectOf(Box b) => new(b.X, b.Y, BoxWidth, b.Height);

        /// <summary>Selects by label (what the stage and the layers know); a repeated label picks the first box. The selection becomes that one box.</summary>
        public void Select(string? p_Label)
        {
            m_Selected = m_Boxes.FirstOrDefault(b => b.Label == p_Label);
            SelectOnlyPrimary();
            Redraw();
        }

        public void SelectKey(string? p_Key)
        {
            m_Selected = m_Boxes.FirstOrDefault(b => b.Key == p_Key);
            SelectOnlyPrimary();
            Redraw();
        }

        void SelectOnlyPrimary()
        {
            m_SelectedKeys.Clear();
            if (m_Selected != null) m_SelectedKeys.Add(m_Selected.Key);
        }

        /// <summary>The selection as a set (the stage's, mirrored): the boxes with those labels; the primary the one named. Raises no event.</summary>
        public void SetSelection(IEnumerable<string> p_Labels, string? p_Primary)
        {
            var s_Set = new HashSet<string>(p_Labels);
            m_SelectedKeys.Clear();
            foreach (var b in m_Boxes.Where(b => s_Set.Contains(b.Label))) m_SelectedKeys.Add(b.Key);
            m_Selected = m_Boxes.FirstOrDefault(b => b.Label == p_Primary) ?? m_Boxes.LastOrDefault(b => m_SelectedKeys.Contains(b.Key));
            if (m_Selected != null) m_SelectedKeys.Add(m_Selected.Key);
            Redraw();
        }

        /// <summary>Ctrl+click: adds a box to the selection or takes it out; the primary becomes the last one added.</summary>
        public void SelectToggle(Box p_Box)
        {
            // the boxes are rebuilt on every graph refresh: a box handed in from before a refresh is matched by its key
            var b = m_Boxes.FirstOrDefault(x => x.Key == p_Box.Key) ?? p_Box;
            if (m_SelectedKeys.Contains(b.Key)) { m_SelectedKeys.Remove(b.Key); if (m_Selected == b) m_Selected = m_Boxes.LastOrDefault(x => m_SelectedKeys.Contains(x.Key)); }
            else { m_SelectedKeys.Add(b.Key); m_Selected = b; }
            SelectionChanged?.Invoke(m_Selected);
            Redraw();
        }

        /// <summary>The rubber band: every box that lies inside the rectangle (graph px) is selected.</summary>
        public void SelectInBand(Rect p_Band)
        {
            m_SelectedKeys.Clear();
            foreach (var b in m_Boxes) if (p_Band.Contains(RectOf(b))) m_SelectedKeys.Add(b.Key);
            m_Selected = m_Boxes.LastOrDefault(b => m_SelectedKeys.Contains(b.Key));
            SelectionChanged?.Invoke(m_Selected);
            Redraw();
        }

        /// <summary>Moves every selected box by an offset: document boxes write their position (one undo step for the lot), shipped ones remember it (the mouse drag's end, and the test seam).</summary>
        public void MoveSelectedBy(double p_Dx, double p_Dy)
        {
            var s_Set = m_Boxes.Where(b => m_SelectedKeys.Contains(b.Key)).ToList();
            if (s_Set.Count == 0) return;
            foreach (var b in s_Set) { b.X = System.Math.Round(b.X + p_Dx); b.Y = System.Math.Round(b.Y + p_Dy); }
            CommitPositions(s_Set, m_Selected ?? s_Set[0]);
            Redraw();
        }

        /// <summary>Writes the moved boxes' positions: the document's for document nodes (after one NodeMoving, so the window snapshots once), remembered for shipped ones.</summary>
        void CommitPositions(List<Box> p_Moved, Box p_Primary)
        {
            if (p_Moved.Any(b => b.Doc != null)) NodeMoving?.Invoke(p_Primary);   // the document is about to change: the window snapshots it for undo
            foreach (var b in p_Moved)
            {
                if (b.Doc != null) { b.Doc.GraphX = b.X; b.Doc.GraphY = b.Y; } else m_Remembered[b.Key] = new Point(b.X, b.Y);
            }
            GrowToExtent();   // a box dragged past the edge grows the canvas
            NodeMoved?.Invoke(p_Primary);
        }

        /// <summary>Rebuilds the boxes from the screen (shipped nodes) and the document's entry for it.</summary>
        public void SetGraph(ScreenModel? p_Model, ScreenEntry? p_Doc)
        {
            if (p_Model == null) { m_Boxes.Clear(); m_Wires.Clear(); Children.Clear(); UnresolvedWires = 0; RemovedWires = 0; m_SelectedKeys.Clear(); m_Selected = null; m_HoverWire = null; return; }
            if (m_Screen != p_Model.Partition) { m_Remembered.Clear(); m_Screen = p_Model.Partition; }
            var s_Selected = m_Selected?.Key;
            var s_SelectedKeys = m_SelectedKeys.ToList();
            m_Boxes.Clear(); m_Wires.Clear(); m_HoverWire = null;
            foreach (var n in p_Model.AllNodes)
            {
                // labels repeat (two logic nodes can share a name); the guid is what tells the boxes apart
                var b = new Box { Key = n.Guid, Label = n.Label, Type = n.Type, Shipped = n };
                foreach (var p in n.Ports)
                    (p.IsInput ? b.Inputs : b.Outputs).Add(new BoxPort { Field = p.Field, Name = p.Name, Key = p.Guid, IsInput = p.IsInput, Dangling = p.Dangling });
                foreach (var r in n.PortRefs) b.Notes.Add($"{r.Field} → {r.Node}.{r.Port}");
                m_Boxes.Add(b);
            }
            if (p_Doc != null)
                foreach (var e in p_Doc.Nodes)
                {
                    var s_Existing = m_Boxes.FirstOrDefault(b => b.Label == e.InstanceName);
                    if (s_Existing != null) { s_Existing.Doc = e; AddDocPorts(s_Existing, e); continue; }
                    var b = new Box { Key = "doc:" + e.InstanceName, Label = e.InstanceName, Type = e.Type, Doc = e };
                    var s_Info = UiTypeCatalog.Describe(e.Type);
                    if (s_Info != null)
                        foreach (var f in s_Info.OwnPorts)
                        {
                            var s_In = UiTypeCatalog.PortIsInput(f.Name);
                            (s_In ? b.Inputs : b.Outputs).Add(new BoxPort { Field = f.Name, Name = f.Name, Key = f.Name, IsInput = s_In });
                        }
                    AddDocPorts(b, e);
                    m_Boxes.Add(b);
                }
            // every event of a widget's contract is a port: the ones the node lacks are drawn hollow and made on first wire
            if (ContractOf != null)
                foreach (var b in m_Boxes.Where(b => b.Type == "WidgetNode"))
                    foreach (var (s_Query, s_IsOutput) in ContractOf(b))
                    {
                        var s_List = s_IsOutput ? b.Outputs : b.Inputs;
                        if (s_Query == "" || s_List.Any(p => SameName(p.Name, s_Query))) continue;
                        s_List.Add(new BoxPort { Field = s_IsOutput ? "Outputs" : "Inputs", Name = s_Query, Key = "contract:" + s_Query, IsInput = !s_IsOutput, Contract = true });
                    }
            string KeyOf(string p_Label) => m_Boxes.FirstOrDefault(b => b.Label == p_Label)?.Key ?? "";
            // shipped wires; the ones the document disconnected are not drawn (the mod erases them at load) but counted
            var s_Removed = new HashSet<string>(p_Doc?.RemovedConnections.Select(r => r.Guid) ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            RemovedWires = 0;
            foreach (var w in p_Model.Wires)
            {
                if (w.Guid != "" && s_Removed.Contains(w.Guid)) { RemovedWires++; continue; }
                m_Wires.Add(new Wire
                {
                    From = m_Boxes.Any(b => b.Key == w.FromGuid) ? w.FromGuid : KeyOf(w.From), FromPort = w.FromPortGuid,
                    To = m_Boxes.Any(b => b.Key == w.ToGuid) ? w.ToGuid : KeyOf(w.To), ToPort = w.ToPortGuid,
                    IsDoc = false, Dangling = w.Dangling, Guid = w.Guid,
                });
            }
            if (p_Doc != null)
                foreach (var e in p_Doc.Nodes)
                    foreach (var c in e.Connections)
                        m_Wires.Add(new Wire { From = KeyOf(e.InstanceName), FromPort = PortName(c.Event ?? c.FromPort ?? "Out"), To = KeyOf(c.ToNode), ToPort = PortName(c.ToEvent ?? c.ToPort ?? c.ToField ?? "In"), IsDoc = true, DocNode = e, DocConn = c });
            Layout();
            m_Selected = m_Boxes.FirstOrDefault(b => b.Key == s_Selected);
            // the selection survives a refresh for the boxes that still exist
            m_SelectedKeys.Clear();
            foreach (var k in s_SelectedKeys) if (m_Boxes.Any(b => b.Key == k)) m_SelectedKeys.Add(k);
            if (m_Selected != null) m_SelectedKeys.Add(m_Selected.Key);
            Redraw();
        }

        /// <summary>"Outputs:ID_M_YES" → ID_M_YES, "inValue" → In: the port name a document spec names.</summary>
        public static string PortName(string p_Spec)
        {
            var s = p_Spec.Contains(':') ? p_Spec[(p_Spec.IndexOf(':') + 1)..] : p_Spec;
            return s;
        }

        public static bool SameName(string p_A, string p_B)
        {
            if (string.Equals(p_A, p_B, StringComparison.OrdinalIgnoreCase)) return true;
            // a legacy Lua spelling (inValue, trueValue, out) against the field (In, True, Out)
            return string.Equals(UiTypeCatalog.LuaName(p_A), p_B, StringComparison.OrdinalIgnoreCase) || string.Equals(p_A, UiTypeCatalog.LuaName(p_B), StringComparison.OrdinalIgnoreCase);
        }

        static void AddDocPorts(Box b, NodeEntry e)
        {
            foreach (var p in e.Ports)
            {
                var s_IsInput = UiTypeCatalog.PortIsInput(p.Field);
                var s_List = s_IsInput ? b.Inputs : b.Outputs;
                if (!s_List.Any(x => SameName(x.Name, p.Name))) s_List.Add(new BoxPort { Field = p.Field, Name = p.Name, Key = p.Name, IsInput = s_IsInput });
            }
            foreach (var c in e.Connections)
            {
                // a widget event fired by the document creates an output port of that name; an array-port source (Outputs:NAME) too
                var s_From = c.Event ?? (c.FromPort != null && c.FromPort.Contains(':') ? PortName(c.FromPort) : null);
                if (s_From != null && !b.Outputs.Any(x => SameName(x.Name, s_From))) b.Outputs.Add(new BoxPort { Field = "Outputs", Name = s_From, Key = s_From, IsInput = false });
            }
        }

        /// <summary>Columns by wiring depth from the inputs/widgets; the document's positions win; remembered drags stay.</summary>
        void Layout()
        {
            var s_ByKey = new Dictionary<string, Box>();
            foreach (var b in m_Boxes) s_ByKey[b.Key] = b;
            var s_Depth = new Dictionary<string, int>();
            var s_Queue = new Queue<string>();
            foreach (var b in m_Boxes.Where(b => b.Type is "InstanceInputNode" or "WidgetNode")) { s_Depth[b.Key] = b.Type == "WidgetNode" ? 1 : 0; s_Queue.Enqueue(b.Key); }
            while (s_Queue.Count > 0)
            {
                var s_From = s_Queue.Dequeue();
                foreach (var w in m_Wires.Where(w => w.From == s_From))
                {
                    if (!s_ByKey.ContainsKey(w.To)) continue;
                    var d = s_Depth[s_From] + 1;
                    if (!s_Depth.TryGetValue(w.To, out var s_Old) || s_Old > d) { s_Depth[w.To] = System.Math.Min(d, 9); if (d < 9) s_Queue.Enqueue(w.To); }
                }
            }
            // one column per depth; a depth with more boxes than fit MaxColumnHeight breaks into side-by-side sub-columns
            // (frontendroot has 270 boxes in a handful of depths — one column each would be 9000 px tall)
            const double MaxColumnHeight = 2200;
            foreach (var b in m_Boxes) b.Height = TitleHeight + b.Notes.Count * 12 + System.Math.Max(b.Inputs.Count, b.Outputs.Count) * PortRow + 10;
            var x = 40.0;
            foreach (var s_Group in m_Boxes.GroupBy(b => s_Depth.TryGetValue(b.Key, out var d) ? d : 10).OrderBy(g => g.Key))
            {
                var s_ColX = x; var y = 40.0; var s_Right = x;
                foreach (var b in s_Group.OrderBy(b => b.Label))
                {
                    if (b.Doc != null && (b.Doc.GraphX != 0 || b.Doc.GraphY != 0)) { b.X = b.Doc.GraphX; b.Y = b.Doc.GraphY; continue; }
                    if (m_Remembered.TryGetValue(b.Key, out var s_Pos)) { b.X = s_Pos.X; b.Y = s_Pos.Y; continue; }
                    if (y > 40 && y + b.Height > MaxColumnHeight) { y = 40; s_ColX += BoxWidth + 40; }
                    b.X = s_ColX; b.Y = y; y += b.Height + 24; s_Right = s_ColX;
                    if (b.Doc != null) { b.Doc.GraphX = b.X; b.Doc.GraphY = b.Y; }
                }
                x = s_Right + BoxWidth + 70;
            }
            GrowToExtent();
        }

        double PortsTop(Box b) => b.Y + TitleHeight + b.Notes.Count * 12;

        /// <summary>The port a key names: by guid (shipped), else by name (document specs, legacy Lua spellings).</summary>
        static int FindPort(List<BoxPort> p_List, string p_Key)
        {
            var i = p_List.FindIndex(p => p.Key == p_Key);
            if (i < 0) i = p_List.FindIndex(p => SameName(p.Name, p_Key));
            return i;
        }

        Point? PortPoint(Box b, string p_Key, bool p_Input)
        {
            var s_List = p_Input ? b.Inputs : b.Outputs;
            var i = FindPort(s_List, p_Key);
            if (i < 0) return null;
            return new Point(p_Input ? b.X : b.X + BoxWidth, PortsTop(b) + i * PortRow + PortRow / 2);
        }

        static string PortLabel(Box b, string p_Key, bool p_Input)
        {
            var s_List = p_Input ? b.Inputs : b.Outputs;
            var i = FindPort(s_List, p_Key);
            return i < 0 ? p_Key : s_List[i].Name;
        }

        public void Redraw()
        {
            Children.Clear();
            UnresolvedWires = 0;
            // wires under boxes
            foreach (var w in m_Wires)
            {
                var s_From = m_Boxes.FirstOrDefault(b => b.Key == w.From); var s_To = m_Boxes.FirstOrDefault(b => b.Key == w.To);
                if (s_From == null || s_To == null) { UnresolvedWires++; w.Resolved = false; w.Geometry = null; continue; }
                var s_A = PortPoint(s_From, w.FromPort, false); var s_C = PortPoint(s_To, w.ToPort, true);
                w.Resolved = s_A != null && s_C != null;
                if (!w.Resolved) UnresolvedWires++;
                w.Label = $"{s_From.Label}.{PortLabel(s_From, w.FromPort, false)} -> {s_To.Label}.{PortLabel(s_To, w.ToPort, true)}";
                var a = s_A ?? new Point(s_From.X + BoxWidth, s_From.Y); var c = s_C ?? new Point(s_To.X, s_To.Y);
                var s_Hover = w == m_HoverWire;
                var s_Stroke = s_Hover ? Brushes.OrangeRed : !w.Resolved ? Brushes.Red : w.IsDoc ? Brushes.Orange : w.Dangling ? new SolidColorBrush(Color.FromRgb(120, 80, 80)) : new SolidColorBrush(Color.FromRgb(110, 150, 200));
                var s_Geometry = new PathGeometry(new[] { new PathFigure(a, new[] { new BezierSegment(new Point(a.X + 60, a.Y), new Point(c.X - 60, c.Y), c, true) }, false) });
                w.Geometry = s_Geometry;
                var s_Path = new Path
                {
                    Stroke = s_Stroke, StrokeThickness = s_Hover ? 3.5 : w.IsDoc || !w.Resolved ? 2 : 1.2, IsHitTestVisible = false,
                    StrokeDashArray = w.Dangling ? new DoubleCollection { 3, 3 } : null,
                    Data = s_Geometry,
                };
                Children.Add(s_Path);
            }
            foreach (var b in m_Boxes)
            {
                var s_Sel = b == m_Selected || m_SelectedKeys.Contains(b.Key);
                var s_Fill = b.IsNew ? Color.FromRgb(42, 62, 44) : b.IsDoc ? Color.FromRgb(58, 52, 34) : b.Type == "WidgetNode" ? Color.FromRgb(40, 48, 62) : Color.FromRgb(44, 44, 48);
                var s_Border = new Border
                {
                    Width = BoxWidth, Height = b.Height, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(s_Fill),
                    BorderBrush = s_Sel ? Brushes.Orange : b.IsDoc ? Brushes.Khaki : new SolidColorBrush(Color.FromRgb(90, 96, 104)), BorderThickness = new Thickness(s_Sel ? 2 : 1), Tag = b,
                };
                var s_Grid = new Grid();
                s_Grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleHeight + b.Notes.Count * 12) });
                s_Grid.RowDefinitions.Add(new RowDefinition());
                var s_Title = new StackPanel { Margin = new Thickness(6, 3, 6, 0) };
                s_Title.Children.Add(new TextBlock { Text = b.Label, FontWeight = FontWeights.Bold, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
                s_Title.Children.Add(new TextBlock { Text = b.Type + (b.IsNew ? "  (new)" : b.IsDoc ? "  (edited)" : "") + (b.Shipped?.UnknownType == true ? "  (unknown type)" : ""), FontSize = 9, Foreground = Brushes.Silver });
                foreach (var s_Note in b.Notes) s_Title.Children.Add(new TextBlock { Text = s_Note, FontSize = 9, Foreground = Brushes.Khaki, TextTrimming = TextTrimming.CharacterEllipsis });
                s_Grid.Children.Add(s_Title);
                var s_Ports = new Canvas(); Grid.SetRow(s_Ports, 1);
                for (var i = 0; i < b.Inputs.Count; ++i)
                {
                    var p = b.Inputs[i];
                    var t = new TextBlock { Text = p.Name, FontSize = 10, Foreground = PortText(p), FontStyle = p.Dangling || p.Contract ? FontStyles.Italic : FontStyles.Normal, ToolTip = PortTip(p) };
                    Canvas.SetLeft(t, 10); Canvas.SetTop(t, i * PortRow + 1); s_Ports.Children.Add(t);
                }
                for (var i = 0; i < b.Outputs.Count; ++i)
                {
                    var p = b.Outputs[i];
                    var t = new TextBlock { Text = p.Name, FontSize = 10, Foreground = PortText(p), FontStyle = p.Dangling || p.Contract ? FontStyles.Italic : FontStyles.Normal, Width = BoxWidth - 20, TextAlignment = TextAlignment.Right, ToolTip = PortTip(p) };
                    Canvas.SetLeft(t, 8); Canvas.SetTop(t, i * PortRow + 1); s_Ports.Children.Add(t);
                }
                s_Grid.Children.Add(s_Ports);
                s_Border.Child = s_Grid;
                SetLeft(s_Border, b.X); SetTop(s_Border, b.Y);
                Children.Add(s_Border);
                // port dots (hit targets for wiring)
                for (var i = 0; i < b.Inputs.Count; ++i) Children.Add(Dot(b, b.Inputs[i], new Point(b.X, PortsTop(b) + i * PortRow + PortRow / 2)));
                for (var i = 0; i < b.Outputs.Count; ++i) Children.Add(Dot(b, b.Outputs[i], new Point(b.X + BoxWidth, PortsTop(b) + i * PortRow + PortRow / 2)));
            }
            if (m_Banding && (m_Band.Width > 0 || m_Band.Height > 0))
            {
                var s_BandRect = new Rectangle
                {
                    Width = m_Band.Width, Height = m_Band.Height, Stroke = Brushes.LightSkyBlue, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 2 },
                    Fill = new SolidColorBrush(Color.FromArgb(40, 135, 206, 250)), IsHitTestVisible = false,
                };
                SetLeft(s_BandRect, m_Band.X); SetTop(s_BandRect, m_Band.Y);
                Children.Add(s_BandRect);
            }
            if (m_WireLine != null) Children.Add(m_WireLine);
        }

        static Brush PortText(BoxPort p) => p.Dangling ? Brushes.IndianRed : p.Contract ? new SolidColorBrush(Color.FromRgb(140, 150, 165)) : Brushes.LightGray;

        static string? PortTip(BoxPort p) => p.Dangling ? "named by a shipped connection but not in the node's port list (dead in the game's data)"
            : p.Contract ? (p.IsInput ? "event the widget receives (its contract) — no port on the node yet: wiring it creates one" : "event the widget fires (its contract) — no port on the node yet: wiring it creates one")
            : null;

        Ellipse Dot(Box b, BoxPort p, Point p_At)
        {
            var s_Size = PortDot;
            Brush s_Fill = p.Dangling || p.Contract ? Brushes.Transparent : p.IsInput ? Brushes.LightSkyBlue : Brushes.Gold;
            Brush s_Stroke = p.Dangling ? Brushes.IndianRed : p.Contract ? (p.IsInput ? Brushes.LightSkyBlue : Brushes.Gold) : Brushes.Black;
            var s_Thick = p.Dangling || p.Contract ? 1.0 : 0.5;
            var s_Tip = (p.IsInput ? "input " : "output ") + p.Field + ":" + p.Name;
            // while a wire is being dragged, every input says whether it can take it: compatible ones grow bright, the rest go dark
            if (m_WireFrom != null && p.IsInput && m_WireTargets.TryGetValue(p, out var s_Verdict))
            {
                if (s_Verdict.Ok) { s_Size = PortDot + 4; s_Fill = Brushes.LightSkyBlue; s_Stroke = Brushes.White; s_Thick = 1.5; }
                else { s_Fill = new SolidColorBrush(Color.FromRgb(50, 54, 60)); s_Stroke = Brushes.DimGray; s_Thick = 1.0; }
                s_Tip += "\n" + (s_Verdict.Ok ? "✓ " : "✗ ") + s_Verdict.Reason;
            }
            var d = new Ellipse
            {
                Width = s_Size, Height = s_Size, Fill = s_Fill, Stroke = s_Stroke, StrokeThickness = s_Thick,
                Tag = (b, p.Field, p.Name, p.IsInput), ToolTip = s_Tip, Cursor = Cursors.Cross,
            };
            SetLeft(d, p_At.X - s_Size / 2); SetTop(d, p_At.Y - s_Size / 2);
            return d;
        }

        /// <summary>The port a box shows under a field/name (the wiring gesture's endpoints).</summary>
        public static BoxPort? FindBoxPort(Box b, string p_Field, string p_Name, bool p_Input)
        {
            var s_List = p_Input ? b.Inputs : b.Outputs;
            return s_List.FirstOrDefault(p => SameName(p.Name, p_Name) && (p.Field == p_Field || p_Field == "")) ?? s_List.FirstOrDefault(p => SameName(p.Name, p_Name));
        }

        /// <summary>Starts a wire from an output port: every input port gets its verdict (highlight), the rubber line follows the mouse.</summary>
        public void BeginWire(Box p_From, BoxPort p_Port, Point? p_At = null)
        {
            // the boxes are rebuilt on every graph refresh: a box handed in from before a refresh is matched by its key
            var s_From = m_Boxes.FirstOrDefault(b => b.Key == p_From.Key) ?? p_From;
            var s_Port = FindBoxPort(s_From, p_Port.Field, p_Port.Name, false) ?? p_Port;
            m_WireFrom = (s_From, s_Port.Field, s_Port.Name);
            m_WireTargets.Clear();
            foreach (var b in m_Boxes)
                foreach (var p in b.Inputs)
                    m_WireTargets[p] = WireCheck?.Invoke(s_From, s_Port, b, p) ?? new WireVerdict(b.Key != s_From.Key, b.Key == s_From.Key ? "same node" : "ok", -1);
            var a = PortPoint(s_From, s_Port.Key, false) ?? p_At ?? new Point(s_From.X + BoxWidth, s_From.Y);
            var s_To = p_At ?? a;
            m_WireLine = new Line { X1 = a.X, Y1 = a.Y, X2 = s_To.X, Y2 = s_To.Y, Stroke = Brushes.Orange, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 3, 2 }, IsHitTestVisible = false };
            Redraw();
        }

        /// <summary>Ends the wire on a port (or nowhere): a compatible input raises WireRequested, an incompatible one WireRejected.</summary>
        public void EndWire(Box? p_To, BoxPort? p_ToPort)
        {
            if (m_WireFrom == null) return;
            var s_From = m_WireFrom.Value;
            var s_FromPort = FindBoxPort(s_From.Box, s_From.Field, s_From.Name, false);
            m_WireFrom = null; m_WireLine = null;
            // a target port handed in from before a refresh: the current box's port of that name
            if (p_To != null && p_ToPort != null && !m_WireTargets.ContainsKey(p_ToPort))
            {
                p_To = m_Boxes.FirstOrDefault(b => b.Key == p_To.Key) ?? p_To;
                p_ToPort = FindBoxPort(p_To, p_ToPort.Field, p_ToPort.Name, true) ?? p_ToPort;
            }
            var s_Verdict = p_ToPort != null && m_WireTargets.TryGetValue(p_ToPort, out var v) ? v : new WireVerdict(true, "", -1);
            m_WireTargets.Clear();
            Redraw();
            if (p_To == null || p_ToPort == null || s_FromPort == null) return;
            if (!s_Verdict.Ok) { WireRejected?.Invoke(s_From.Box, s_FromPort, p_To, p_ToPort, s_Verdict.Reason); return; }
            WireRequested?.Invoke(s_From.Box, s_From.Field, s_From.Name, p_To, p_ToPort.Field, p_ToPort.Name);
        }

        /// <summary>The verdicts computed for the wire being dragged (test seam).</summary>
        public IReadOnlyDictionary<BoxPort, WireVerdict> WireTargets => m_WireTargets;
        public bool WireInProgress => m_WireFrom != null;

        // ------------------------------------------------------------------------------------------ wires under the pointer

        /// <summary>The wire under a graph point (the topmost when several overlap), within a few screen pixels of its curve.</summary>
        public Wire? WireAt(Point p)
        {
            var s_Pen = new Pen(Brushes.Black, System.Math.Max(6.0, 12.0 / System.Math.Max(0.05, ViewScale)));
            for (var i = m_Wires.Count - 1; i >= 0; --i)
                if (m_Wires[i].Geometry != null && m_Wires[i].Geometry!.StrokeContains(s_Pen, p)) return m_Wires[i];
            return null;
        }

        /// <summary>A point on a wire's curve (0 = its source port, 1 = its target port): the seam that aims a click at a wire.</summary>
        public Point? PointOn(Wire w, double t)
        {
            if (w.Geometry is not PathGeometry g || g.Figures.Count == 0 || g.Figures[0].Segments.Count == 0 || g.Figures[0].Segments[0] is not BezierSegment s) return null;
            var a = g.Figures[0].StartPoint; var u = 1 - t;
            return new Point(
                u * u * u * a.X + 3 * u * u * t * s.Point1.X + 3 * u * t * t * s.Point2.X + t * t * t * s.Point3.X,
                u * u * u * a.Y + 3 * u * u * t * s.Point1.Y + 3 * u * t * t * s.Point2.Y + t * t * t * s.Point3.Y);
        }

        /// <summary>Alt+click at a point: the wire there (if any) is handed to the window to disconnect. Returns it (test seam).</summary>
        public Wire? DisconnectAt(Point p)
        {
            var w = WireAt(p);
            if (w == null) return null;
            m_HoverWire = null;
            WireRemoveRequested?.Invoke(w);
            return w;
        }

        // ------------------------------------------------------------------------------------------ mouse

        Box? BoxAt(Point p) => m_Boxes.LastOrDefault(b => p.X >= b.X && p.X <= b.X + BoxWidth && p.Y >= b.Y && p.Y <= b.Y + b.Height);

        (Box, string, string, bool)? DotAt(Point p)
        {
            foreach (var c in Children.OfType<Ellipse>().Reverse())
                if (c.Tag is ValueTuple<Box, string, string, bool> t)
                {
                    var l = GetLeft(c); var tp = GetTop(c);
                    if (p.X >= l - 3 && p.X <= l + PortDot + 3 && p.Y >= tp - 3 && p.Y <= tp + PortDot + 3) return t;
                }
            return null;
        }

        void OnDown(object p_Sender, MouseButtonEventArgs e)
        {
            Focus();
            var p = e.GetPosition(this);
            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
            {
                // Alt+click: disconnect the wire under the pointer; nothing else starts
                DisconnectAt(p);
                e.Handled = true;
                return;
            }
            var s_Dot = DotAt(p);
            if (s_Dot != null && !s_Dot.Value.Item4)
            {
                var s_Port = FindBoxPort(s_Dot.Value.Item1, s_Dot.Value.Item2, s_Dot.Value.Item3, false);
                if (s_Port != null) { BeginWire(s_Dot.Value.Item1, s_Port, p); CaptureMouse(); }
                return;
            }
            var b = BoxAt(p);
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                // Ctrl+click: add to / take out of the selection; nothing else starts
                if (b != null) SelectToggle(b);
                return;
            }
            if (b == null)
            {
                // empty graph: clear the selection and start the rubber band
                m_Selected = null; m_SelectedKeys.Clear();
                SelectionChanged?.Invoke(null);
                m_Banding = true; m_BandStart = p; m_Band = new Rect(p, p);
                CaptureMouse();
                Redraw();
                return;
            }
            // a click on a selected box keeps the whole selection (and drags it all); on another one it becomes the selection
            if (!m_SelectedKeys.Contains(b.Key)) { m_SelectedKeys.Clear(); m_SelectedKeys.Add(b.Key); }
            m_Selected = b;
            SelectionChanged?.Invoke(b);
            m_Drag = b; m_DragStart = p; m_DragOrigins.Clear();
            foreach (var x in m_Boxes.Where(x => m_SelectedKeys.Contains(x.Key))) m_DragOrigins[x] = new Point(x.X, x.Y);
            CaptureMouse();
            Redraw();
        }

        void OnMove(object p_Sender, MouseEventArgs e)
        {
            var p = e.GetPosition(this);
            if (m_WireLine != null) { m_WireLine.X2 = p.X; m_WireLine.Y2 = p.Y; return; }
            if (m_Banding)
            {
                m_Band = new Rect(new Point(System.Math.Min(m_BandStart.X, p.X), System.Math.Min(m_BandStart.Y, p.Y)), new Point(System.Math.Max(m_BandStart.X, p.X), System.Math.Max(m_BandStart.Y, p.Y)));
                Redraw();
                return;
            }
            if (m_Drag != null)
            {
                // every selected box moves by the same whole-pixel offset from where it was when the drag began
                var dx = p.X - m_DragStart.X; var dy = p.Y - m_DragStart.Y;
                foreach (var (b, o) in m_DragOrigins) { b.X = System.Math.Round(o.X + dx); b.Y = System.Math.Round(o.Y + dy); }
                Redraw();
                return;
            }
            // Alt held: the wire under the pointer lights up (a click disconnects it)
            var s_Hover = (Keyboard.Modifiers & ModifierKeys.Alt) != 0 ? WireAt(p) : null;
            if (s_Hover != m_HoverWire) { m_HoverWire = s_Hover; Redraw(); }
            Cursor = s_Hover != null ? Cursors.Hand : null;
        }

        void OnUp(object p_Sender, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(this);
            if (m_WireFrom != null)
            {
                var s_Dot = DotAt(p);
                ReleaseMouseCapture();
                var s_ToPort = s_Dot != null && s_Dot.Value.Item4 ? FindBoxPort(s_Dot.Value.Item1, s_Dot.Value.Item2, s_Dot.Value.Item3, true) : null;
                EndWire(s_ToPort != null ? s_Dot!.Value.Item1 : null, s_ToPort);
                return;
            }
            if (m_Banding)
            {
                m_Banding = false;
                ReleaseMouseCapture();
                if (m_Band.Width > 2 || m_Band.Height > 2) SelectInBand(m_Band); else Redraw();
                return;
            }
            if (m_Drag != null)
            {
                var b = m_Drag; m_Drag = null; ReleaseMouseCapture();
                var s_Moved = m_DragOrigins.Where(kv => System.Math.Abs(kv.Key.X - kv.Value.X) > 0.5 || System.Math.Abs(kv.Key.Y - kv.Value.Y) > 0.5).Select(kv => kv.Key).ToList();
                m_DragOrigins.Clear();
                if (s_Moved.Count > 0) CommitPositions(s_Moved, b);
            }
        }
    }
}
