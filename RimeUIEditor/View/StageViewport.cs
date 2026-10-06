using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RimeUIEditor.View
{
    /// <summary>
    /// The window onto the stage: zoom with the wheel toward the cursor, pan with the right or middle button,
    /// F frames the whole stage, Ctrl+0 is 1:1. The zoom-toward-cursor and adaptive step maths are the ones the
    /// shader editor's GridCanvas took from Frosty's level editor (MIT), so both Rime editors feel the same.
    /// The stage itself keeps stage pixels: this control only carries the view matrix, so hit testing and
    /// dragging on the stage never see the zoom.
    /// </summary>
    public class StageViewport : Grid
    {
        /// <summary>User multiplier on the wheel-zoom step (Settings). 1 = the shader editor's speed.</summary>
        public static double ZoomSpeed { get; set; } = 1.0;

        public const double MinScale = 0.15, MaxScale = 4.0;

        readonly Canvas m_Layer = new();
        readonly MatrixTransform m_Transform = new();
        double m_Scale = 1.0;
        double m_ScaleLevel = 1.0;
        double m_ScaleRate = 0.075;
        Point m_Offset;            // where stage (0,0) sits, in viewport pixels
        Point m_PanStart;
        Point m_PanOrigin;
        bool m_Panning;
        bool m_Framed;

        readonly Pen m_MinorPen = new(new SolidColorBrush(Color.FromRgb(24, 28, 33)), 1.0);
        readonly Pen m_MajorPen = new(new SolidColorBrush(Color.FromRgb(34, 40, 47)), 1.0);

        public event Action<double>? ScaleChanged;

        /// <summary>Something dragged over / dropped on the stage: the data and the stage point (px, zoom undone). CanDrop answers the cursor.</summary>
        public Func<IDataObject, bool>? CanDrop;
        public event Action<IDataObject, Point>? Dropped;
        /// <summary>One line per drag event that matters (entered / left / dropped, with the formats and the stage point), for the window's log.</summary>
        public event Action<string>? DragEvent;
        bool m_DragOver;        // a drag the viewport accepts is over it: an accent frame says so
        bool m_DragInside;      // a drag is over the viewport (accepted or not)
        readonly System.Windows.Threading.DispatcherTimer m_LeaveTimer = new();

        public double Scale => m_Scale;

        /// <summary>What Fit frames: the 1280x720 stage by default; the graph view sets its own extent.</summary>
        public Size ContentSize { get; set; } = new(StageCanvas.StageWidth, StageCanvas.StageHeight);

        public StageViewport()
        {
            Focusable = true;
            ClipToBounds = true;
            Background = new SolidColorBrush(Color.FromRgb(10, 10, 10));
            m_MinorPen.Freeze(); m_MajorPen.Freeze();
            m_Layer.RenderTransform = m_Transform;
            Children.Add(m_Layer);
            SizeChanged += (_, _) => { if (!m_Framed && ActualWidth > 0 && ActualHeight > 0) Fit(); else Apply(); };
            AllowDrop = true;
            // DragEnter/DragLeave bubble up from every child the drag crosses (the stage's boxes), so a drag moving over the stage
            // raises leave/enter pairs all the way: "over" is decided on the first enter and released only when no DragOver follows a leave
            DragEnter += (_, e) =>
            {
                var s_Ok = CanDrop?.Invoke(e.Data) == true;
                if (!m_DragInside)
                {
                    m_DragInside = true; m_DragOver = s_Ok; InvalidateVisual();
                    DragEvent?.Invoke($"enter {Formats(e.Data)} at stage {Fmt(ToStage(e.GetPosition(this)))} -> {(s_Ok ? "accepted" : "refused")}");
                }
                e.Effects = s_Ok ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true;
            };
            DragOver += (_, e) => { e.Effects = CanDrop?.Invoke(e.Data) == true ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            DragOver += (_, _) => m_LeaveTimer.Stop();
            DragLeave += (_, e) =>
            {
                e.Handled = true;
                if (!m_DragInside) return;
                // a leave raised by a child boundary: the pointer is still over the viewport — and OLE keeps calling DragOver
                // while it is, so a leave with no DragOver in the next 150 ms is the real one
                if (PointerInside()) return;
                m_LeaveTimer.Stop(); m_LeaveTimer.Start();
            };
            m_LeaveTimer.Interval = TimeSpan.FromMilliseconds(150);
            m_LeaveTimer.Tick += (_, _) =>
            {
                m_LeaveTimer.Stop();
                if (!m_DragInside) return;
                m_DragInside = false; m_DragOver = false; InvalidateVisual();
                DragEvent?.Invoke("left" + PointerNote());
            };
            Drop += (_, e) =>
            {
                m_LeaveTimer.Stop();
                m_DragInside = false; m_DragOver = false; InvalidateVisual();
                var s_At = ToStage(e.GetPosition(this));
                var s_Ok = CanDrop?.Invoke(e.Data) == true;
                DragEvent?.Invoke($"drop {Formats(e.Data)} at stage {Fmt(s_At)} -> {(s_Ok ? "taken" : "refused")}");
                if (s_Ok) { Dropped?.Invoke(e.Data, s_At); e.Handled = true; }
            };
        }

        static string Fmt(Point p) => $"({System.Math.Round(p.X)},{System.Math.Round(p.Y)})";

        /// <summary>Whether the real pointer is over this viewport (OLE owns the mouse during a drag: the screen position is the only truth).</summary>
        bool PointerInside()
        {
            try
            {
                if (!NativeInput.GetCursorPos(out var s_Pos)) return false;
                var p = PointFromScreen(new Point(s_Pos.X, s_Pos.Y));
                return p.X >= 0 && p.Y >= 0 && p.X < ActualWidth && p.Y < ActualHeight;
            }
            catch { return false; }
        }

        string PointerNote()
        {
            try
            {
                if (!NativeInput.GetCursorPos(out var s_Pos)) return " (no pointer)";
                var p = PointFromScreen(new Point(s_Pos.X, s_Pos.Y));
                return $" (pointer at {p.X:0},{p.Y:0} of {ActualWidth:0}x{ActualHeight:0})";
            }
            catch (Exception s_Ex) { return " (" + s_Ex.Message + ")"; }
        }

        /// <summary>The clipboard formats a drag carries, with the value of ours ("rime/partition ui/assets/button").</summary>
        public static string Formats(IDataObject p_Data)
        {
            var s_Parts = new System.Collections.Generic.List<string>();
            foreach (var f in p_Data.GetFormats(false))
            {
                if (f.StartsWith("rime/", StringComparison.Ordinal)) { try { s_Parts.Add(f + " " + p_Data.GetData(f)); } catch { s_Parts.Add(f); } }
                else s_Parts.Add(f);
            }
            return s_Parts.Count == 0 ? "(no formats)" : string.Join(", ", s_Parts);
        }

        /// <summary>Whether a drag the viewport accepts is over it now (the accent frame).</summary>
        public bool DragHighlighted => m_DragOver;

        /// <summary>The stage element; one at a time, drawn in stage pixels.</summary>
        public UIElement? Stage
        {
            get => m_Layer.Children.Count > 0 ? m_Layer.Children[0] : null;
            set { m_Layer.Children.Clear(); if (value != null) m_Layer.Children.Add(value); Apply(); }
        }

        /// <summary>Whole stage on screen with a margin, centred. Also what F does.</summary>
        public void Fit()
        {
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            m_Framed = true;
            var s_Scale = System.Math.Min((ActualWidth - 40) / ContentSize.Width, (ActualHeight - 40) / ContentSize.Height);
            m_Scale = System.Math.Clamp(s_Scale, MinScale, MaxScale);
            ResetScaleLevel();
            m_Offset = new Point((ActualWidth - ContentSize.Width * m_Scale) * 0.5, (ActualHeight - ContentSize.Height * m_Scale) * 0.5);
            Apply();
        }

        /// <summary>Sets the zoom keeping the viewport centre (or the given viewport point) still — the slider's path.</summary>
        public void SetScale(double p_Scale, Point? p_Around = null)
        {
            p_Scale = System.Math.Clamp(p_Scale, MinScale, MaxScale);
            if (System.Math.Abs(p_Scale - m_Scale) < 1e-6) return;
            var s_Around = p_Around ?? new Point(ActualWidth * 0.5, ActualHeight * 0.5);
            var s_World = ToStage(s_Around);
            m_Scale = p_Scale;
            ResetScaleLevel();
            m_Offset = new Point(s_Around.X - s_World.X * m_Scale, s_Around.Y - s_World.Y * m_Scale);
            m_Framed = true;
            Apply();
        }

        public Point ToStage(Point p_Viewport) => new((p_Viewport.X - m_Offset.X) / m_Scale, (p_Viewport.Y - m_Offset.Y) / m_Scale);

        /// <summary>Puts a stage point at the centre of the view at the given zoom (a close look at a detail: the seams' zoomed photos).</summary>
        public void LookAt(Point p_Stage, double p_Scale)
        {
            m_Scale = System.Math.Clamp(p_Scale, MinScale, MaxScale);
            ResetScaleLevel();
            m_Offset = new Point(ActualWidth * 0.5 - p_Stage.X * m_Scale, ActualHeight * 0.5 - p_Stage.Y * m_Scale);
            m_Framed = true;
            Apply();
        }

        void ResetScaleLevel()
        {
            // the adaptive step follows the current zoom: bigger steps when zoomed in, finer when zoomed out
            m_ScaleLevel = 1.0; m_ScaleRate = 0.075;
            while (m_Scale > m_ScaleLevel * 2) { m_ScaleLevel *= 2; m_ScaleRate *= 2; }
            while (m_Scale < m_ScaleLevel && m_ScaleLevel > 0.01) { m_ScaleLevel *= 0.5; m_ScaleRate /= 2; }
        }

        void Apply()
        {
            m_Transform.Matrix = new Matrix(m_Scale, 0, 0, m_Scale, m_Offset.X, m_Offset.Y);
            InvalidateVisual();
            ScaleChanged?.Invoke(m_Scale);
        }

        protected override void OnMouseWheel(MouseWheelEventArgs p_Args)
        {
            var s_Cursor = p_Args.GetPosition(this);
            var s_World = ToStage(s_Cursor);

            // Frosty's step: proportional to where the zoom sits inside its current octave, times the user speed
            var s_Percent = (m_Scale - m_ScaleLevel * 0.5) / (m_ScaleLevel * 2 - m_ScaleLevel * 0.5);
            var s_Rate = ((1 - s_Percent) * (m_ScaleRate * 0.5) + s_Percent * (m_ScaleRate * 2)) * ZoomSpeed;
            if (p_Args.Delta < 0)
            {
                if (m_Scale <= MinScale) { p_Args.Handled = true; return; }
                m_Scale = System.Math.Max(MinScale, m_Scale - s_Rate);
            }
            else
            {
                if (m_Scale >= MaxScale) { p_Args.Handled = true; return; }
                m_Scale = System.Math.Min(MaxScale, m_Scale + s_Rate);
            }
            if (m_Scale > m_ScaleLevel * 2) { m_ScaleRate *= 2; m_ScaleLevel *= 2; }
            else if (m_Scale < m_ScaleLevel) { m_ScaleRate /= 2; m_ScaleLevel *= 0.5; }

            m_Offset = new Point(s_Cursor.X - s_World.X * m_Scale, s_Cursor.Y - s_World.Y * m_Scale);
            m_Framed = true;
            Apply();
            p_Args.Handled = true;
        }

        protected override void OnMouseDown(MouseButtonEventArgs p_Args)
        {
            Focus();
            if (p_Args.Handled || (p_Args.ChangedButton != MouseButton.Right && p_Args.ChangedButton != MouseButton.Middle))
                return;
            m_Panning = true;
            m_PanStart = p_Args.GetPosition(this);
            m_PanOrigin = m_Offset;
            CaptureMouse();
            Cursor = Cursors.SizeAll;
            p_Args.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs p_Args)
        {
            if (!m_Panning) return;
            var s_Now = p_Args.GetPosition(this);
            m_Offset = new Point(m_PanOrigin.X + (s_Now.X - m_PanStart.X), m_PanOrigin.Y + (s_Now.Y - m_PanStart.Y));
            Apply();
            p_Args.Handled = true;
        }

        protected override void OnMouseUp(MouseButtonEventArgs p_Args)
        {
            if (!m_Panning || (p_Args.ChangedButton != MouseButton.Right && p_Args.ChangedButton != MouseButton.Middle))
                return;
            m_Panning = false;
            ReleaseMouseCapture();
            Cursor = null;
            p_Args.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs p_Args)
        {
            if (p_Args.Key == Key.F) { Fit(); p_Args.Handled = true; }
            else if (p_Args.Key == Key.D0 && Keyboard.Modifiers == ModifierKeys.Control) { SetScale(1.0); p_Args.Handled = true; }
            else base.OnKeyDown(p_Args);
        }

        protected override void OnRender(DrawingContext p_Context)
        {
            base.OnRender(p_Context);
            // a stage-pixel grid behind the stage: 10 px minor while it is readable, 100 px major always
            if (m_Scale * 10 >= 6) DrawLines(p_Context, 10, m_MinorPen);
            DrawLines(p_Context, 100, m_MajorPen);
            // a drag this viewport will take: a 3 px frame in the editor's accent, drawn over the grid and under the stage
            if (m_DragOver && ActualWidth > 6 && ActualHeight > 6)
                p_Context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(120, 170, 255)), 3), new Rect(1.5, 1.5, ActualWidth - 3, ActualHeight - 3));
        }

        void DrawLines(DrawingContext p_Context, double p_Units, Pen p_Pen)
        {
            var s_Step = p_Units * m_Scale;
            if (s_Step < 2) return;
            var s_X0 = m_Offset.X % s_Step; if (s_X0 < 0) s_X0 += s_Step;
            var s_Y0 = m_Offset.Y % s_Step; if (s_Y0 < 0) s_Y0 += s_Step;
            for (var x = s_X0; x < ActualWidth; x += s_Step)
                p_Context.DrawLine(p_Pen, new Point(System.Math.Round(x) + 0.5, 0), new Point(System.Math.Round(x) + 0.5, ActualHeight));
            for (var y = s_Y0; y < ActualHeight; y += s_Step)
                p_Context.DrawLine(p_Pen, new Point(0, System.Math.Round(y) + 0.5), new Point(ActualWidth, System.Math.Round(y) + 0.5));
        }
    }
}
