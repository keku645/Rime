using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RimeShaderEditor.Graph;

namespace RimeShaderEditor.View;

public class GraphCanvas : GridCanvas
{
    private const double c_NodeWidth = 172;
    private const double c_RowHeight = 16;
    private const double c_TitleHeight = 18;
    private const double c_PortSize = 8;
    private const double c_TopPadding = 4;
    private const double c_PreviewSize = 136;
    private const int c_UndoDepth = 100;

    private sealed class PortHit
    {
        public required GraphNode Node { get; init; }
        public required string Port { get; init; }
        public required bool IsInput { get; init; }
        public required Point Anchor { get; init; }
    }

    private ShaderGraph m_Graph = new();
    private GraphNode? m_Selected;
    private GraphNode? m_Dragging;
    private Vector m_DragGrab;
    private PortHit? m_WireFrom;
    private Point m_WireCursor;
    private readonly Dictionary<string, FormattedText> m_TextCache = new();
    private readonly List<string> m_Undo = new();
    private readonly List<string> m_Redo = new();

    public event EventHandler? SelectionChanged;
    public event EventHandler? GraphChanged;

    /// <summary>Supplies the thumbnail for a texture node, keyed by its Register parameter.</summary>
    public Func<string, ImageSource?>? TexturePreviewProvider { get; set; }

    public ShaderGraph Graph
    {
        get => m_Graph;
        set
        {
            m_Graph = value;
            m_Selected = null;
            m_Dragging = null;
            m_WireFrom = null;
            m_Undo.Clear();
            m_Redo.Clear();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
    }

    public GraphNode? Selected => m_Selected;
    public bool CanUndo => m_Undo.Count > 0;
    public bool CanRedo => m_Redo.Count > 0;

    public GraphCanvas()
    {
        Focusable = true;
    }

    /// <summary>
    /// Snapshots the graph before a mutation. Whole-graph JSON rather than per-action deltas: graphs are tiny
    /// and a snapshot cannot desynchronise from the model the way an inverse-operation log can.
    /// </summary>
    public void PushUndo()
    {
        m_Undo.Add(m_Graph.ToJson());
        if (m_Undo.Count > c_UndoDepth)
            m_Undo.RemoveAt(0);

        m_Redo.Clear();
    }

    public bool Undo() => Restore(m_Undo, m_Redo);
    public bool Redo() => Restore(m_Redo, m_Undo);

    private bool Restore(List<string> p_From, List<string> p_To)
    {
        if (p_From.Count == 0)
            return false;

        p_To.Add(m_Graph.ToJson());
        var s_Json = p_From[^1];
        p_From.RemoveAt(p_From.Count - 1);

        var s_SelectedId = m_Selected?.Id;
        m_Graph = ShaderGraph.FromJson(s_Json);
        m_Selected = s_SelectedId == null ? null : m_Graph.FindNode(s_SelectedId);
        m_Dragging = null;
        m_WireFrom = null;

        SelectionChanged?.Invoke(this, EventArgs.Empty);
        GraphChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    public void AddNode(string p_Kind)
    {
        PushUndo();

        var s_Centre = TransformPoint(new Point(ActualWidth * 0.5, ActualHeight * 0.5));
        var s_Node = new GraphNode
        {
            Kind = p_Kind,
            X = Math.Round(s_Centre.X / 8) * 8,
            Y = Math.Round(s_Centre.Y / 8) * 8,
        };

        m_Graph.Nodes.Add(s_Node);
        m_Selected = s_Node;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        GraphChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void DeleteSelected()
    {
        if (m_Selected == null)
            return;

        PushUndo();
        m_Graph.RemoveNode(m_Selected.Id);
        m_Selected = null;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        GraphChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void Refresh() => InvalidateVisual();

    private static bool HasPreview(GraphNode p_Node) => p_Node.Def.HasTexturePreview;

    private static double NodeHeight(GraphNode p_Node)
    {
        var s_Rows = Math.Max(p_Node.Def.Inputs.Count, p_Node.Def.Outputs.Count);
        var s_Height = c_TopPadding + s_Rows * c_RowHeight + c_TitleHeight + c_TopPadding;
        return HasPreview(p_Node) ? s_Height + c_PreviewSize + c_TopPadding : s_Height;
    }

    private static Rect NodeRect(GraphNode p_Node) =>
        new(p_Node.X, p_Node.Y, c_NodeWidth, NodeHeight(p_Node));

    private static Rect PreviewRect(GraphNode p_Node)
    {
        var s_Rows = Math.Max(p_Node.Def.Inputs.Count, p_Node.Def.Outputs.Count);
        var s_Top = p_Node.Y + c_TopPadding + s_Rows * c_RowHeight;
        return new Rect(p_Node.X + (c_NodeWidth - c_PreviewSize) * 0.5, s_Top, c_PreviewSize, c_PreviewSize);
    }

    private static Point InputAnchor(GraphNode p_Node, int p_Index) =>
        new(p_Node.X, p_Node.Y + c_TopPadding + p_Index * c_RowHeight + c_RowHeight * 0.5);

    private static Point OutputAnchor(GraphNode p_Node, int p_Index) =>
        new(p_Node.X + c_NodeWidth, p_Node.Y + c_TopPadding + p_Index * c_RowHeight + c_RowHeight * 0.5);

    private static Brush CategoryBrush(string p_Category) => p_Category switch
    {
        "Inputs" => new SolidColorBrush(Color.FromRgb(64, 132, 72)),
        "Textures" => new SolidColorBrush(Color.FromRgb(40, 104, 56)),
        "Constants" => new SolidColorBrush(Color.FromRgb(104, 104, 108)),
        "Output" => new SolidColorBrush(Color.FromRgb(176, 152, 40)),
        _ => new SolidColorBrush(Color.FromRgb(52, 96, 148)),
    };

    private FormattedText Text(string p_Text, double p_Size, Brush p_Brush)
    {
        var s_Key = $"{p_Size}|{p_Brush}|{p_Text}";
        if (m_TextCache.TryGetValue(s_Key, out var s_Cached))
            return s_Cached;

        var s_Text = new FormattedText(p_Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Consolas"), p_Size, p_Brush, 1.0);

        m_TextCache[s_Key] = s_Text;
        return s_Text;
    }

    protected override void Render(DrawingContext p_Context)
    {
        p_Context.PushTransform(GetWorldMatrix());

        foreach (var s_Connection in m_Graph.Connections)
            RenderWire(p_Context, s_Connection);

        if (m_WireFrom != null)
            DrawCurve(p_Context, m_WireFrom.IsInput ? m_WireCursor : m_WireFrom.Anchor,
                m_WireFrom.IsInput ? m_WireFrom.Anchor : m_WireCursor,
                new Pen(new SolidColorBrush(Color.FromRgb(232, 200, 64)), 1.4));

        foreach (var s_Node in m_Graph.Nodes)
            RenderNode(p_Context, s_Node);

        p_Context.Pop();
    }

    private bool WireEndpoints(GraphConnection p_Connection, out Point p_From, out Point p_To)
    {
        p_From = default;
        p_To = default;

        var s_From = m_Graph.FindNode(p_Connection.FromNode);
        var s_To = m_Graph.FindNode(p_Connection.ToNode);
        if (s_From == null || s_To == null)
            return false;

        var s_FromIndex = s_From.Def.Outputs.FindIndex(p_P => p_P.Name == p_Connection.FromPort);
        var s_ToIndex = s_To.Def.Inputs.FindIndex(p_P => p_P.Name == p_Connection.ToPort);
        if (s_FromIndex < 0 || s_ToIndex < 0)
            return false;

        p_From = OutputAnchor(s_From, s_FromIndex);
        p_To = InputAnchor(s_To, s_ToIndex);
        return true;
    }

    private void RenderWire(DrawingContext p_Context, GraphConnection p_Connection)
    {
        if (!WireEndpoints(p_Connection, out var s_From, out var s_To))
            return;

        DrawCurve(p_Context, s_From, s_To, new Pen(new SolidColorBrush(Color.FromRgb(158, 158, 162)), 1.2));
    }

    private static double CurveOffset(Point p_A, Point p_B) => Math.Max(24.0, Math.Abs(p_B.X - p_A.X) * 0.45);

    private static void DrawCurve(DrawingContext p_Context, Point p_A, Point p_B, Pen p_Pen)
    {
        var s_Curve = CurveOffset(p_A, p_B);
        var s_Geometry = new StreamGeometry();
        using (var s_Ctx = s_Geometry.Open())
        {
            s_Ctx.BeginFigure(p_A, false, false);
            s_Ctx.BezierTo(new Point(p_A.X + s_Curve, p_A.Y), new Point(p_B.X - s_Curve, p_B.Y), p_B, true, false);
        }

        s_Geometry.Freeze();
        p_Context.DrawGeometry(null, p_Pen, s_Geometry);
    }

    private static Point CurveAt(Point p_A, Point p_B, double p_T)
    {
        var s_Curve = CurveOffset(p_A, p_B);
        var s_C1 = new Point(p_A.X + s_Curve, p_A.Y);
        var s_C2 = new Point(p_B.X - s_Curve, p_B.Y);

        var s_U = 1 - p_T;
        var s_W0 = s_U * s_U * s_U;
        var s_W1 = 3 * s_U * s_U * p_T;
        var s_W2 = 3 * s_U * p_T * p_T;
        var s_W3 = p_T * p_T * p_T;

        return new Point(
            s_W0 * p_A.X + s_W1 * s_C1.X + s_W2 * s_C2.X + s_W3 * p_B.X,
            s_W0 * p_A.Y + s_W1 * s_C1.Y + s_W2 * s_C2.Y + s_W3 * p_B.Y);
    }

    private void RenderNode(DrawingContext p_Context, GraphNode p_Node)
    {
        var s_Rect = NodeRect(p_Node);
        var s_Body = new SolidColorBrush(Color.FromRgb(210, 210, 212));
        var s_Border = new Pen(new SolidColorBrush(p_Node == m_Selected
            ? Color.FromRgb(255, 208, 64)
            : Color.FromRgb(24, 24, 26)), p_Node == m_Selected ? 2.0 : 1.0);

        p_Context.DrawRectangle(s_Body, s_Border, s_Rect);

        if (HasPreview(p_Node))
            RenderPreview(p_Context, p_Node);

        var s_TitleRect = new Rect(s_Rect.X, s_Rect.Bottom - c_TitleHeight, s_Rect.Width, c_TitleHeight);
        p_Context.DrawRectangle(CategoryBrush(p_Node.Def.Category), null, s_TitleRect);

        var s_Title = Text(p_Node.Def.Title, 10, Brushes.White);
        p_Context.DrawText(s_Title, new Point(s_TitleRect.X + 5, s_TitleRect.Y + 3));

        var s_Dark = new SolidColorBrush(Color.FromRgb(32, 32, 34));
        var s_Dim = new SolidColorBrush(Color.FromRgb(146, 146, 150));

        for (var i = 0; i < p_Node.Def.Inputs.Count; i++)
        {
            var s_Port = p_Node.Def.Inputs[i];
            var s_Anchor = InputAnchor(p_Node, i);
            var s_Unavailable = s_Port.WhyUnavailable(p_Node) != null;
            var s_Connected = m_Graph.ConnectionInto(p_Node.Id, s_Port.Name) != null;

            DrawPort(p_Context, s_Anchor, s_Connected, s_Unavailable);

            var s_Label = Text(s_Port.Name, 9, s_Unavailable ? s_Dim : s_Dark);
            p_Context.DrawText(s_Label, new Point(s_Anchor.X + c_PortSize, s_Anchor.Y - s_Label.Height * 0.5));
        }

        for (var i = 0; i < p_Node.Def.Outputs.Count; i++)
        {
            var s_Port = p_Node.Def.Outputs[i];
            var s_Anchor = OutputAnchor(p_Node, i);
            var s_HasWire = false;
            foreach (var _ in m_Graph.ConnectionsFrom(p_Node.Id, s_Port.Name))
            {
                s_HasWire = true;
                break;
            }

            DrawPort(p_Context, s_Anchor, s_HasWire, false);

            var s_Label = Text(s_Port.Name, 9, s_Dark);
            p_Context.DrawText(s_Label,
                new Point(s_Anchor.X - c_PortSize - s_Label.Width, s_Anchor.Y - s_Label.Height * 0.5));
        }
    }

    private void RenderPreview(DrawingContext p_Context, GraphNode p_Node)
    {
        var s_Rect = PreviewRect(p_Node);
        var s_Image = TexturePreviewProvider?.Invoke(p_Node.GetParam("Register"));

        if (s_Image != null)
        {
            p_Context.DrawImage(s_Image, s_Rect);
        }
        else
        {
            p_Context.DrawRectangle(new SolidColorBrush(Color.FromRgb(120, 120, 124)), null, s_Rect);

            var s_Pale = new SolidColorBrush(Color.FromRgb(240, 240, 240));
            var s_Faint = new SolidColorBrush(Color.FromRgb(200, 200, 204));
            var s_Register = Text($"t{p_Node.GetParam("Register")}", 14, s_Pale);
            var s_Hint = Text("press", 9, s_Faint);
            var s_Hint2 = Text("Load target textures", 9, s_Faint);

            var s_CentreY = s_Rect.Y + s_Rect.Height * 0.5;
            p_Context.DrawText(s_Register,
                new Point(s_Rect.X + (s_Rect.Width - s_Register.Width) * 0.5, s_CentreY - 26));
            p_Context.DrawText(s_Hint,
                new Point(s_Rect.X + (s_Rect.Width - s_Hint.Width) * 0.5, s_CentreY + 2));
            p_Context.DrawText(s_Hint2,
                new Point(s_Rect.X + (s_Rect.Width - s_Hint2.Width) * 0.5, s_CentreY + 14));
        }

        p_Context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(24, 24, 26)), 1.0), s_Rect);
    }

    private static void DrawPort(DrawingContext p_Context, Point p_Anchor, bool p_Connected, bool p_Unavailable)
    {
        var s_Rect = new Rect(p_Anchor.X - c_PortSize * 0.5, p_Anchor.Y - c_PortSize * 0.5, c_PortSize, c_PortSize);

        Brush s_Fill;
        if (p_Unavailable)
            s_Fill = new SolidColorBrush(Color.FromRgb(176, 176, 180));
        else if (p_Connected)
            s_Fill = new SolidColorBrush(Color.FromRgb(248, 208, 56));
        else
            s_Fill = new SolidColorBrush(Color.FromRgb(248, 248, 248));

        var s_Pen = new Pen(new SolidColorBrush(p_Unavailable
            ? Color.FromRgb(140, 140, 144)
            : Color.FromRgb(24, 24, 26)), 1.0);

        p_Context.DrawRectangle(s_Fill, s_Pen, s_Rect);
    }

    private PortHit? HitPort(Point p_World)
    {
        foreach (var s_Node in m_Graph.Nodes)
        {
            for (var i = 0; i < s_Node.Def.Inputs.Count; i++)
            {
                var s_Anchor = InputAnchor(s_Node, i);
                if (Near(p_World, s_Anchor))
                    return new PortHit
                    {
                        Node = s_Node, Port = s_Node.Def.Inputs[i].Name, IsInput = true, Anchor = s_Anchor,
                    };
            }

            for (var i = 0; i < s_Node.Def.Outputs.Count; i++)
            {
                var s_Anchor = OutputAnchor(s_Node, i);
                if (Near(p_World, s_Anchor))
                    return new PortHit
                    {
                        Node = s_Node, Port = s_Node.Def.Outputs[i].Name, IsInput = false, Anchor = s_Anchor,
                    };
            }
        }

        return null;
    }

    private static bool Near(Point p_A, Point p_B) =>
        Math.Abs(p_A.X - p_B.X) <= c_PortSize && Math.Abs(p_A.Y - p_B.Y) <= c_PortSize;

    private GraphNode? HitNode(Point p_World)
    {
        for (var i = m_Graph.Nodes.Count - 1; i >= 0; i--)
            if (NodeRect(m_Graph.Nodes[i]).Contains(p_World))
                return m_Graph.Nodes[i];

        return null;
    }

    /// <summary>Nearest wire within a tolerance of the click, found by sampling the same curve that is drawn.</summary>
    private GraphConnection? HitWire(Point p_World)
    {
        const int c_Samples = 24;
        var s_Tolerance = 6.0 / Math.Max(m_Scale, 0.0001);
        GraphConnection? s_Best = null;
        var s_BestDistance = double.MaxValue;

        foreach (var s_Connection in m_Graph.Connections)
        {
            if (!WireEndpoints(s_Connection, out var s_From, out var s_To))
                continue;

            for (var i = 0; i <= c_Samples; i++)
            {
                var s_Point = CurveAt(s_From, s_To, i / (double) c_Samples);
                var s_Distance = (s_Point - p_World).Length;
                if (s_Distance < s_BestDistance)
                {
                    s_BestDistance = s_Distance;
                    s_Best = s_Connection;
                }
            }
        }

        return s_BestDistance <= s_Tolerance ? s_Best : null;
    }

    protected override void OnMouseDown(MouseButtonEventArgs p_Args)
    {
        if (p_Args.ChangedButton == MouseButton.Left)
        {
            Focus();
            var s_World = TransformPoint(p_Args.GetPosition(this));

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                var s_Wire = HitWire(s_World);
                if (s_Wire != null)
                {
                    PushUndo();
                    m_Graph.Disconnect(s_Wire.ToNode, s_Wire.ToPort);
                    GraphChanged?.Invoke(this, EventArgs.Empty);
                    InvalidateVisual();
                }

                p_Args.Handled = true;
                return;
            }

            var s_Port = HitPort(s_World);
            if (s_Port != null)
            {
                PushUndo();

                // Dragging off a connected input detaches that wire and lets it be re-dropped elsewhere.
                if (s_Port.IsInput)
                    m_Graph.Disconnect(s_Port.Node.Id, s_Port.Port);

                m_WireFrom = s_Port;
                m_WireCursor = s_World;
                Mouse.Capture(this);
                p_Args.Handled = true;
                InvalidateVisual();
                return;
            }

            var s_Node = HitNode(s_World);
            m_Selected = s_Node;
            SelectionChanged?.Invoke(this, EventArgs.Empty);

            if (s_Node != null)
            {
                PushUndo();

                // Keep the node in front so overlapping nodes pick predictably.
                m_Graph.Nodes.Remove(s_Node);
                m_Graph.Nodes.Add(s_Node);

                m_Dragging = s_Node;
                m_DragGrab = new Vector(s_World.X - s_Node.X, s_World.Y - s_Node.Y);
                Mouse.Capture(this);
            }

            p_Args.Handled = true;
            InvalidateVisual();
            return;
        }

        base.OnMouseDown(p_Args);
    }

    protected override void OnMouseMove(MouseEventArgs p_Args)
    {
        if (m_Dragging != null && p_Args.LeftButton == MouseButtonState.Pressed)
        {
            var s_World = TransformPoint(p_Args.GetPosition(this));
            m_Dragging.X = Math.Round((s_World.X - m_DragGrab.X) / 4) * 4;
            m_Dragging.Y = Math.Round((s_World.Y - m_DragGrab.Y) / 4) * 4;
            p_Args.Handled = true;
            InvalidateVisual();
            return;
        }

        if (m_WireFrom != null && p_Args.LeftButton == MouseButtonState.Pressed)
        {
            m_WireCursor = TransformPoint(p_Args.GetPosition(this));
            p_Args.Handled = true;
            InvalidateVisual();
            return;
        }

        base.OnMouseMove(p_Args);
    }

    protected override void OnMouseUp(MouseButtonEventArgs p_Args)
    {
        if (p_Args.ChangedButton == MouseButton.Left)
        {
            if (m_Dragging != null)
            {
                m_Dragging = null;
                Mouse.Capture(null);
                GraphChanged?.Invoke(this, EventArgs.Empty);
                p_Args.Handled = true;
                InvalidateVisual();
                return;
            }

            if (m_WireFrom != null)
            {
                var s_Target = HitPort(TransformPoint(p_Args.GetPosition(this)));
                if (s_Target != null && s_Target.IsInput != m_WireFrom.IsInput)
                {
                    var s_Output = m_WireFrom.IsInput ? s_Target : m_WireFrom;
                    var s_Input = m_WireFrom.IsInput ? m_WireFrom : s_Target;
                    m_Graph.Connect(s_Output.Node.Id, s_Output.Port, s_Input.Node.Id, s_Input.Port);
                }

                m_WireFrom = null;
                Mouse.Capture(null);
                GraphChanged?.Invoke(this, EventArgs.Empty);
                p_Args.Handled = true;
                InvalidateVisual();
                return;
            }
        }

        base.OnMouseUp(p_Args);
    }

    protected override void OnKeyDown(KeyEventArgs p_Args)
    {
        if (p_Args.Key == Key.Delete)
        {
            DeleteSelected();
            p_Args.Handled = true;
            return;
        }

        base.OnKeyDown(p_Args);
    }
}
