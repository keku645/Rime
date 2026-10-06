using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RimeUIEditor.Model;

namespace RimeUIEditor.View
{
    /// <summary>
    /// The 1280x720 stage. Draws every placement as its bounds (or a dashed default box when the symbol builds
    /// its content at runtime and has no static bounds), lets the user select and drag them, and — on the
    /// selected one — resize it by the handles on its corners and edges (Shift keeps the proportions) and turn
    /// it by the knob above it (Shift snaps to 15°). Coordinates are stage pixels; zoom is a render transform
    /// so hit testing stays in stage space; the handles keep a screen size through ViewScale.
    /// </summary>
    public class StageCanvas : Canvas
    {
        public const double StageWidth = 1280, StageHeight = 720;
        const double DefaultW = 160, DefaultH = 40;

        /// <summary>The handles of the selected placement: eight on its box, one knob above it for rotation.</summary>
        public enum HandleKind { None, NW, N, NE, E, SE, S, SW, W, Rotate }

        readonly List<PlacementInfo> m_Placements = new();
        string? m_Selected;                                  // the primary selection (properties, handles)
        readonly HashSet<string> m_SelectedSet = new();      // every selected placement (Ctrl+click adds, a band selects many)
        Point m_DragStart;
        (double X, double Y) m_DragOrigin;
        readonly Dictionary<string, (double X, double Y)> m_DragOrigins = new();   // every selected placement's origin when the drag began
        bool m_Dragging;
        bool m_Banding; Point m_BandStart; Rect m_Band;     // a left drag on empty stage: the rubber band

        public event Action<string?>? SelectionChanged;
        public event Action<string, double, double>? PlacementMoved;
        /// <summary>A drag of several selected placements ended: every one with its new local x/y (one undo step for the lot).</summary>
        public event Action<IReadOnlyList<(string Name, double X, double Y)>>? PlacementsMoved;

        /// <summary>Every selected placement (the primary one included).</summary>
        public IReadOnlyCollection<string> SelectedSet => m_SelectedSet;
        /// <summary>A handle drag ended: name, local x/y (the origin may move so the opposite edge stays), scale along the placement's own axes.</summary>
        public event Action<string, double, double, double, double>? PlacementResized;
        /// <summary>The rotation knob was released: name, degrees (positive = clockwise on screen).</summary>
        public event Action<string, double>? PlacementRotated;

        readonly ArtLayer m_Art = new();
        bool m_ShowArt = true, m_ShowOutlines = true;

        /// <summary>The viewport's zoom, so the handles and their strokes keep a screen size.</summary>
        public double ViewScale { get; set; } = 1.0;

        public string? Selected
        {
            get => m_Selected;
            set { m_Selected = value; m_SelectedSet.Clear(); if (value != null) m_SelectedSet.Add(value); Redraw(); }
        }

        /// <summary>The selection as a set (the graph's, mirrored): those placements, the primary the one named. Raises no event.</summary>
        public void SetSelection(IEnumerable<string> p_Names, string? p_Primary)
        {
            m_SelectedSet.Clear();
            foreach (var n in p_Names) if (m_Placements.Any(p => p.Name == n)) m_SelectedSet.Add(n);
            m_Selected = p_Primary != null && m_SelectedSet.Contains(p_Primary) ? p_Primary : m_SelectedSet.LastOrDefault();
            Redraw();
        }

        /// <summary>Ctrl+click: adds a placement to the selection or takes it out; the primary becomes the last one added.</summary>
        public void SelectToggle(string p_Name)
        {
            if (m_SelectedSet.Contains(p_Name)) { m_SelectedSet.Remove(p_Name); if (m_Selected == p_Name) m_Selected = m_SelectedSet.LastOrDefault(); }
            else { m_SelectedSet.Add(p_Name); m_Selected = p_Name; }
            SelectionChanged?.Invoke(m_Selected);
            Redraw();
        }

        /// <summary>The rubber band: every visible, unlocked placement whose box lies inside the rectangle (stage px) is selected.</summary>
        public void SelectInBand(Rect p_Band)
        {
            m_SelectedSet.Clear();
            foreach (var p in m_Placements.OrderBy(x => x.Depth))
                if (!Hidden.Contains(p.Name) && !Locked.Contains(p.Name) && p_Band.Contains(RectOf(p))) m_SelectedSet.Add(p.Name);
            m_Selected = m_SelectedSet.LastOrDefault();
            SelectionChanged?.Invoke(m_Selected);
            Redraw();
        }

        /// <summary>Moves every selected placement by a stage offset and reports the lot (the mouse drag's end, and the test seam).</summary>
        public void MoveSelectedBy(double p_Dx, double p_Dy)
        {
            var s_Moved = new List<(string, double, double)>();
            foreach (var p in m_Placements.Where(x => m_SelectedSet.Contains(x.Name)))
            {
                ShiftPlacement(p, System.Math.Round(p.X + p_Dx), System.Math.Round(p.Y + p_Dy));
                s_Moved.Add((p.Name, p.LocalX, p.LocalY));
            }
            Redraw();
            if (s_Moved.Count > 0) PlacementsMoved?.Invoke(s_Moved);
        }

        /// <summary>Puts a placement's origin at a stage point: bounds, local translation and the live art follow.</summary>
        void ShiftPlacement(PlacementInfo p, double p_NewX, double p_NewY)
        {
            var bdx = (int)Math.Round((p_NewX - p.X) * 20); var bdy = (int)Math.Round((p_NewY - p.Y) * 20);
            p.Bounds = new RimeLib.Cmd.Scaleform.GfxMovie.Bounds { Left = p.Bounds.Left + bdx, Right = p.Bounds.Right + bdx, Top = p.Bounds.Top + bdy, Bottom = p.Bounds.Bottom + bdy };
            p.X = p_NewX; p.Y = p_NewY;
            var (lx, ly) = p.LocalFor(p_NewX, p_NewY);
            p.LocalX = Math.Round(lx); p.LocalY = Math.Round(ly);
            if (ArtGroups != null && ArtGroups.TryGetValue(p.Path, out var s_Group) && s_Group.Transform is MatrixTransform s_Mt && !s_Group.IsFrozen)
            {
                // the group's transform is the placement's local matrix in px: keep its linear part, move its translation
                var m = s_Mt.Matrix;
                s_Group.Transform = new MatrixTransform(m.M11, m.M12, m.M21, m.M22, p.LocalX, p.LocalY);
                m_Art.InvalidateVisual();
            }
        }

        /// <summary>The rendered movie (frame 1, stage px), drawn under the outlines.</summary>
        public Drawing? Art
        {
            get => m_Art.Drawing;
            set { m_Art.Drawing = value; m_Art.InvalidateVisual(); }
        }

        public bool ShowArt { get => m_ShowArt; set { m_ShowArt = value; Redraw(); } }
        public bool ShowOutlines { get => m_ShowOutlines; set { m_ShowOutlines = value; Redraw(); } }

        /// <summary>Layer toggles (the eye / the lock of the layers list): hidden placements are not drawn nor hit, locked ones are drawn but not hit.</summary>
        public HashSet<string> Hidden { get; } = new();
        public HashSet<string> Locked { get; } = new();

        /// <summary>Every named clip the renderer drew (the widgets' insides included), by path, in stage px — outlined when ShowInnerOutlines is on.</summary>
        public IReadOnlyDictionary<string, Rect>? InnerBounds { get; set; }
        bool m_ShowInner;
        public bool ShowInnerOutlines { get => m_ShowInner; set { m_ShowInner = value; Redraw(); } }
        bool m_ShowRotation;
        /// <summary>Draws each placement's local x axis from its origin (rotation / skew made visible).</summary>
        public bool ShowRotation { get => m_ShowRotation; set { m_ShowRotation = value; Redraw(); } }

        public StageCanvas()
        {
            Width = StageWidth;
            Height = StageHeight;
            Background = (Brush)Application.Current.Resources["StageBrush"];
            ClipToBounds = false;
            MouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
        }

        /// <summary>One element that paints a Drawing at 1:1 stage pixels (the canvas's zoom is the viewport's).</summary>
        class ArtLayer : FrameworkElement
        {
            public Drawing? Drawing;
            public ArtLayer() { IsHitTestVisible = false; Width = StageWidth; Height = StageHeight; }
            protected override void OnRender(DrawingContext p_Context)
            {
                if (Drawing == null) return;
                p_Context.PushClip(new RectangleGeometry(new Rect(0, 0, StageWidth, StageHeight)));
                p_Context.DrawDrawing(Drawing);
                p_Context.Pop();
            }
        }

        public void SetPlacements(IEnumerable<PlacementInfo> p_Placements)
        {
            m_Placements.Clear();
            m_Placements.AddRange(p_Placements);
            Redraw();
        }

        /// <summary>The box a placement shows: its measured bounds on the stage, or the default runtime box at its origin scaled like it.</summary>
        public static Rect RectOf(PlacementInfo p)
        {
            if (p.Bounds.Empty)
                return new Rect(p.X, p.Y, DefaultW * p.SizeX, DefaultH * p.SizeY);
            return new Rect(p.Bounds.Left / 20.0, p.Bounds.Top / 20.0, (p.Bounds.Right - p.Bounds.Left) / 20.0, (p.Bounds.Bottom - p.Bounds.Top) / 20.0);
        }

        double HandleSize => 8.0 / System.Math.Max(0.05, ViewScale);
        double KnobOffset => 26.0 / System.Math.Max(0.05, ViewScale);

        /// <summary>Where each handle sits for a box: corners, edge midpoints, and the knob above the top edge.</summary>
        IEnumerable<(HandleKind Kind, Point At)> Handles(Rect r)
        {
            var cx = r.Left + r.Width / 2; var cy = r.Top + r.Height / 2;
            yield return (HandleKind.NW, new Point(r.Left, r.Top));
            yield return (HandleKind.N, new Point(cx, r.Top));
            yield return (HandleKind.NE, new Point(r.Right, r.Top));
            yield return (HandleKind.E, new Point(r.Right, cy));
            yield return (HandleKind.SE, new Point(r.Right, r.Bottom));
            yield return (HandleKind.S, new Point(cx, r.Bottom));
            yield return (HandleKind.SW, new Point(r.Left, r.Bottom));
            yield return (HandleKind.W, new Point(r.Left, cy));
            yield return (HandleKind.Rotate, new Point(cx, r.Top - KnobOffset));
        }

        /// <summary>The one placement the handles work on: only when exactly one is selected.</summary>
        PlacementInfo? SelectedPlacement => m_Selected == null || m_SelectedSet.Count > 1 ? null : m_Placements.FirstOrDefault(p => p.Name == m_Selected && !Hidden.Contains(p.Name) && !Locked.Contains(p.Name));

        /// <summary>The handle of the selected placement under a stage point, or None.</summary>
        public HandleKind HandleAt(Point p_Point)
        {
            var p = SelectedPlacement;
            if (p == null) return HandleKind.None;
            var h = HandleSize;
            foreach (var (s_Kind, s_At) in Handles(RectOf(p)))
                if (System.Math.Abs(p_Point.X - s_At.X) <= h && System.Math.Abs(p_Point.Y - s_At.Y) <= h) return s_Kind;
            return HandleKind.None;
        }

        /// <summary>Where a handle of the selected placement sits now (test seam).</summary>
        public Point? HandlePosition(HandleKind p_Kind)
        {
            var p = SelectedPlacement;
            if (p == null) return null;
            return Handles(RectOf(p)).FirstOrDefault(h => h.Kind == p_Kind).At;
        }

        public void Redraw()
        {
            Children.Clear();
            if (m_ShowArt) { SetLeft(m_Art, 0); SetTop(m_Art, 0); Children.Add(m_Art); }
            // safe frame
            var s_Frame = new System.Windows.Shapes.Rectangle { Width = StageWidth, Height = StageHeight, Stroke = Brushes.DimGray, StrokeThickness = 1 };
            Children.Add(s_Frame);
            // the clips inside the widgets (imported movies' own placements): thin outlines with their names, the way the
            // game's editor shows every clip; they belong to the widget's movie, so they are not selectable here
            if (m_ShowInner && InnerBounds != null)
            {
                var s_Own = m_Placements.Select(p => p.Path).ToHashSet();
                var s_HiddenPaths = m_Placements.Where(p => Hidden.Contains(p.Name)).Select(p => p.Path + "/").ToList();
                foreach (var (s_Path, s_Rect) in InnerBounds)
                {
                    if (s_Own.Contains(s_Path) || s_Rect.Width < 1 || s_Rect.Height < 1) continue;
                    if (s_HiddenPaths.Any(h => s_Path.StartsWith(h, StringComparison.Ordinal))) continue;
                    var s_Box = new System.Windows.Shapes.Rectangle
                    {
                        Width = s_Rect.Width, Height = s_Rect.Height, Stroke = new SolidColorBrush(Color.FromArgb(150, 170, 190, 210)), StrokeThickness = 0.6,
                        Fill = null, IsHitTestVisible = false,
                    };
                    SetLeft(s_Box, s_Rect.X); SetTop(s_Box, s_Rect.Y);
                    Children.Add(s_Box);
                    if (s_Rect.Width > 24 && s_Rect.Height > 9)
                    {
                        var s_Name = new TextBlock { Text = s_Path[(s_Path.LastIndexOf('/') + 1)..], FontSize = 7, Foreground = new SolidColorBrush(Color.FromArgb(180, 190, 205, 220)), IsHitTestVisible = false };
                        SetLeft(s_Name, s_Rect.X + 1); SetTop(s_Name, s_Rect.Y); Children.Add(s_Name);
                    }
                }
            }
            foreach (var p in m_Placements.OrderBy(x => x.Depth))
            {
                // the eye of the layers list: a hidden placement's art goes transparent and it gets no box at all
                if (ArtGroups != null && ArtGroups.TryGetValue(p.Path, out var s_ArtGroup) && !s_ArtGroup.IsFrozen)
                {
                    var s_Opacity = Hidden.Contains(p.Name) ? 0.0 : 1.0;
                    if (System.Math.Abs(s_ArtGroup.Opacity - s_Opacity) > 0.001) { s_ArtGroup.Opacity = s_Opacity; m_Art.InvalidateVisual(); }
                }
                if (Hidden.Contains(p.Name)) continue;
                var r = RectOf(p);
                var s_Selected = p.Name == m_Selected || m_SelectedSet.Contains(p.Name);
                if (!m_ShowOutlines && !s_Selected)
                {
                    // still hit-testable: an invisible box keeps click/drag working with the outlines off
                    var s_Ghost = new System.Windows.Shapes.Rectangle { Width = System.Math.Max(2, r.Width), Height = System.Math.Max(2, r.Height), Fill = Brushes.Transparent, Tag = p.Name };
                    SetLeft(s_Ghost, r.X); SetTop(s_Ghost, r.Y);
                    Children.Add(s_Ghost);
                    continue;
                }
                var s_Box = new System.Windows.Shapes.Rectangle
                {
                    Width = Math.Max(2, r.Width), Height = Math.Max(2, r.Height),
                    Stroke = s_Selected ? Brushes.Orange : (p.Added ? Brushes.LightGreen : Brushes.SteelBlue),
                    StrokeThickness = s_Selected ? 2 : 1,
                    StrokeDashArray = p.Bounds.Empty ? new DoubleCollection { 4, 3 } : null,
                    Fill = new SolidColorBrush(Color.FromArgb(s_Selected ? (byte)60 : (byte)25, 120, 170, 255)),
                    Tag = p.Name,
                };
                SetLeft(s_Box, r.X); SetTop(s_Box, r.Y);
                Children.Add(s_Box);
                var s_Label = new TextBlock
                {
                    Text = p.Name + (p.Bounds.Empty ? " (runtime size)" : ""),
                    Foreground = s_Selected ? Brushes.Orange : Brushes.LightGray,
                    FontSize = 11, IsHitTestVisible = false,
                };
                SetLeft(s_Label, r.X + 3); SetTop(s_Label, r.Y + 2);
                Children.Add(s_Label);
                // origin marker
                var s_Dot = new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Fill = Brushes.Orange, IsHitTestVisible = false, Visibility = s_Selected ? Visibility.Visible : Visibility.Collapsed };
                SetLeft(s_Dot, p.X - 2.5); SetTop(s_Dot, p.Y - 2.5);
                Children.Add(s_Dot);
                // rotation: the placement's local x axis (world), 40 px long, from the origin
                if (m_ShowRotation)
                {
                    var (ax, ay) = p.World.ApplyVector(40 * 20, 0);
                    var s_Axis = new System.Windows.Shapes.Line { X1 = p.X, Y1 = p.Y, X2 = p.X + ax / 20.0, Y2 = p.Y + ay / 20.0, Stroke = Brushes.Yellow, StrokeThickness = 1, IsHitTestVisible = false };
                    Children.Add(s_Axis);
                    if (System.Math.Abs(p.Rotation) > 0.01)
                    {
                        var s_Deg = new TextBlock { Text = p.Rotation.ToString("0.#", CultureInfo.InvariantCulture) + "°", FontSize = 9, Foreground = Brushes.Yellow, IsHitTestVisible = false };
                        SetLeft(s_Deg, p.X + ax / 20.0 + 2); SetTop(s_Deg, p.Y + ay / 20.0 - 6); Children.Add(s_Deg);
                    }
                }
            }
            DrawHandles();
            if (m_Banding && m_Band.Width > 0 && m_Band.Height > 0)
            {
                var s_BandBox = new System.Windows.Shapes.Rectangle
                {
                    Width = m_Band.Width, Height = m_Band.Height, Stroke = Brushes.Orange, StrokeThickness = 1.0 / System.Math.Max(0.05, ViewScale),
                    StrokeDashArray = new DoubleCollection { 4, 3 }, Fill = new SolidColorBrush(Color.FromArgb(30, 255, 165, 0)), IsHitTestVisible = false,
                };
                SetLeft(s_BandBox, m_Band.X); SetTop(s_BandBox, m_Band.Y);
                Children.Add(s_BandBox);
            }
        }

        /// <summary>The resize handles and the rotation knob of the selected placement (screen-sized whatever the zoom).</summary>
        void DrawHandles()
        {
            var p = SelectedPlacement;
            if (p == null) return;
            var r = RectOf(p);
            var h = HandleSize; var s_Stroke = 1.0 / System.Math.Max(0.05, ViewScale);
            var s_Knob = Handles(r).First(x => x.Kind == HandleKind.Rotate).At;
            var s_Stem = new System.Windows.Shapes.Line { X1 = r.Left + r.Width / 2, Y1 = r.Top, X2 = s_Knob.X, Y2 = s_Knob.Y, Stroke = Brushes.Orange, StrokeThickness = s_Stroke, IsHitTestVisible = false };
            Children.Add(s_Stem);
            foreach (var (s_Kind, s_At) in Handles(r))
            {
                System.Windows.Shapes.Shape s_Shape = s_Kind == HandleKind.Rotate
                    ? new System.Windows.Shapes.Ellipse { Width = h * 1.4, Height = h * 1.4, Fill = new SolidColorBrush(Color.FromRgb(120, 220, 120)), Stroke = Brushes.Black, StrokeThickness = s_Stroke, IsHitTestVisible = false, ToolTip = "turn (Shift snaps to 15°)" }
                    : new System.Windows.Shapes.Rectangle { Width = h, Height = h, Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = s_Stroke, IsHitTestVisible = false };
                SetLeft(s_Shape, s_At.X - s_Shape.Width / 2); SetTop(s_Shape, s_At.Y - s_Shape.Height / 2);
                Children.Add(s_Shape);
            }
            // while a handle is dragged, the numbers next to the box say what the release will write
            if (m_Handle != HandleKind.None)
            {
                var s_Text = m_Handle == HandleKind.Rotate
                    ? $"{m_LastAngle.ToString("0.#", CultureInfo.InvariantCulture)}°"
                    : $"×{(m_Sx0 * m_LastK.X).ToString("0.###", CultureInfo.InvariantCulture)} / ×{(m_Sy0 * m_LastK.Y).ToString("0.###", CultureInfo.InvariantCulture)}  ({r.Width:0}×{r.Height:0} px)";
                var s_Note = new TextBlock { Text = s_Text, FontSize = 11 / System.Math.Max(0.3, ViewScale), Foreground = Brushes.Orange, Background = new SolidColorBrush(Color.FromArgb(180, 20, 20, 20)), IsHitTestVisible = false };
                SetLeft(s_Note, r.Right + h); SetTop(s_Note, r.Bottom + h);
                Children.Add(s_Note);
            }
        }

        PlacementInfo? HitTest(Point p_Point)
        {
            // topmost (highest depth) first; hidden and locked layers are not hit
            foreach (var p in m_Placements.OrderByDescending(x => x.Depth))
                if (!Hidden.Contains(p.Name) && !Locked.Contains(p.Name) && RectOf(p).Contains(p_Point))
                    return p;
            return null;
        }

        void OnDown(object p_Sender, MouseButtonEventArgs e)
        {
            var s_At = e.GetPosition(this);
            // the handles of the selected placement come before the boxes (a corner handle sits on a neighbour's box too)
            var s_Handle = HandleAt(s_At);
            if (s_Handle != HandleKind.None)
            {
                BeginHandle(s_Handle, s_At);
                CaptureMouse();
                Cursor = s_Handle == HandleKind.Rotate ? Cursors.Hand : Cursors.SizeAll;
                return;
            }
            var s_Hit = HitTest(s_At);
            var s_Ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (s_Ctrl)
            {
                // Ctrl+click: add to / take out of the selection; nothing else starts
                if (s_Hit != null) SelectToggle(s_Hit.Name);
                return;
            }
            if (s_Hit == null)
            {
                // empty stage: clear the selection and start the rubber band
                m_Selected = null; m_SelectedSet.Clear();
                SelectionChanged?.Invoke(null);
                m_Banding = true; m_BandStart = s_At; m_Band = new Rect(s_At, s_At);
                CaptureMouse();
                Redraw();
                return;
            }
            // a click on a selected placement keeps the whole selection (and drags it all); on another one it becomes the selection
            if (!m_SelectedSet.Contains(s_Hit.Name)) { m_SelectedSet.Clear(); m_SelectedSet.Add(s_Hit.Name); }
            m_Selected = s_Hit.Name;
            SelectionChanged?.Invoke(m_Selected);
            Redraw();
            m_Dragging = true;
            m_DragStart = s_At;
            m_DragOrigin = (s_Hit.X, s_Hit.Y);
            m_DragOrigins.Clear();
            foreach (var p in m_Placements.Where(x => m_SelectedSet.Contains(x.Name))) m_DragOrigins[p.Name] = (p.X, p.Y);
            CaptureMouse();
        }

        /// <summary>The art's per-placement groups (by placement path) so a drag moves the drawing live, before the document is rewritten.</summary>
        public IReadOnlyDictionary<string, DrawingGroup>? ArtGroups { get; set; }

        void OnMove(object p_Sender, MouseEventArgs e)
        {
            var s_Now = e.GetPosition(this);
            if (m_Handle != HandleKind.None)
            {
                DragHandle(s_Now, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                return;
            }
            if (m_Banding)
            {
                m_Band = new Rect(new Point(System.Math.Min(m_BandStart.X, s_Now.X), System.Math.Min(m_BandStart.Y, s_Now.Y)), new Point(System.Math.Max(m_BandStart.X, s_Now.X), System.Math.Max(m_BandStart.Y, s_Now.Y)));
                Redraw();
                return;
            }
            if (!m_Dragging || m_Selected == null)
            {
                // the cursor says what a press would do
                var s_Over = HandleAt(s_Now);
                Cursor = s_Over switch
                {
                    HandleKind.Rotate => Cursors.Hand,
                    HandleKind.N or HandleKind.S => Cursors.SizeNS,
                    HandleKind.E or HandleKind.W => Cursors.SizeWE,
                    HandleKind.NW or HandleKind.SE => Cursors.SizeNWSE,
                    HandleKind.NE or HandleKind.SW => Cursors.SizeNESW,
                    _ => null,
                };
                return;
            }
            var dx = s_Now.X - m_DragStart.X; var dy = s_Now.Y - m_DragStart.Y;
            // every selected placement moves by the same whole-pixel offset from where it was when the drag began
            foreach (var p in m_Placements.Where(x => m_DragOrigins.ContainsKey(x.Name)))
            {
                var o = m_DragOrigins[p.Name];
                ShiftPlacement(p, Math.Round(o.X + dx), Math.Round(o.Y + dy));
            }
            Redraw();
        }

        void OnUp(object p_Sender, MouseButtonEventArgs e)
        {
            if (m_Handle != HandleKind.None)
            {
                ReleaseMouseCapture();
                Cursor = null;
                EndHandle();
                return;
            }
            if (m_Banding)
            {
                m_Banding = false;
                ReleaseMouseCapture();
                if (m_Band.Width > 2 || m_Band.Height > 2) SelectInBand(m_Band); else Redraw();
                return;
            }
            if (!m_Dragging) return;
            m_Dragging = false;
            ReleaseMouseCapture();
            var s_Moved = new List<(string, double, double)>();
            foreach (var p in m_Placements.Where(x => m_DragOrigins.ContainsKey(x.Name)))
            {
                var o = m_DragOrigins[p.Name];
                if (Math.Abs(p.X - o.X) > 0.5 || Math.Abs(p.Y - o.Y) > 0.5) s_Moved.Add((p.Name, p.LocalX, p.LocalY));
            }
            m_DragOrigins.Clear();
            if (s_Moved.Count == 1) PlacementMoved?.Invoke(s_Moved[0].Item1, s_Moved[0].Item2, s_Moved[0].Item3);
            else if (s_Moved.Count > 1) PlacementsMoved?.Invoke(s_Moved);
        }

        // ------------------------------------------------------------------------------------------ handles: resize and rotate

        HandleKind m_Handle;
        Rect m_Rect0;                       // the box when the drag began
        Point m_Origin0;                    // the placement's origin (stage px) when the drag began
        Point m_HandleStart;                // where the dragged handle sat
        Point m_Anchor;                     // the point that must not move (opposite corner / edge), or the origin for a turn
        double m_Sx0, m_Sy0, m_Angle0, m_Phi0;
        bool m_Bounds0Empty;
        (double X, double Y) m_LastK = (1, 1);
        double m_LastAngle;

        /// <summary>Starts a handle drag on the selected placement (the mouse path and the test seam share it).</summary>
        public void BeginHandle(HandleKind p_Kind, Point p_At)
        {
            var p = SelectedPlacement;
            if (p == null || p_Kind == HandleKind.None) return;
            m_Handle = p_Kind;
            m_Rect0 = RectOf(p);
            m_Origin0 = new Point(p.X, p.Y);
            m_Sx0 = p.SizeX; m_Sy0 = p.SizeY;
            m_Angle0 = System.Math.Atan2(p.RotateSkew0, p.ScaleX);
            m_Bounds0Empty = p.Bounds.Empty;
            m_HandleStart = Handles(m_Rect0).First(h => h.Kind == p_Kind).At;
            m_Phi0 = System.Math.Atan2(m_HandleStart.Y - m_Origin0.Y, m_HandleStart.X - m_Origin0.X);
            var r = m_Rect0; var cx = r.Left + r.Width / 2; var cy = r.Top + r.Height / 2;
            m_Anchor = p_Kind switch
            {
                HandleKind.NW => new Point(r.Right, r.Bottom), HandleKind.N => new Point(cx, r.Bottom), HandleKind.NE => new Point(r.Left, r.Bottom),
                HandleKind.E => new Point(r.Left, cy), HandleKind.SE => new Point(r.Left, r.Top), HandleKind.S => new Point(cx, r.Top),
                HandleKind.SW => new Point(r.Right, r.Top), HandleKind.W => new Point(r.Right, cy),
                _ => m_Origin0,
            };
            m_LastK = (1, 1); m_LastAngle = m_Angle0 * 180 / System.Math.PI;
            Redraw();
        }

        /// <summary>
        /// Moves the dragged handle to a stage point. A corner or edge scales the placement about the opposite
        /// corner / edge (that point stays put: the origin moves along) — p_Uniform (Shift) keeps the proportions;
        /// the knob turns it about its origin — p_Uniform snaps to 15°. The box, the art and the numbers follow live.
        /// </summary>
        public void DragHandle(Point p_At, bool p_Uniform)
        {
            var p = SelectedPlacement;
            if (p == null || m_Handle == HandleKind.None) return;
            if (m_Handle == HandleKind.Rotate)
            {
                var s_Phi = System.Math.Atan2(p_At.Y - m_Origin0.Y, p_At.X - m_Origin0.X);
                var s_Theta = m_Angle0 + (s_Phi - m_Phi0);
                if (p_Uniform) s_Theta = System.Math.Round(s_Theta / (System.Math.PI / 12)) * (System.Math.PI / 12);
                m_LastAngle = NormalizeDegrees(s_Theta * 180 / System.Math.PI);
                // the box: the initial corners turned about the origin by the change, then their bounds
                var s_Delta = s_Theta - m_Angle0;
                var s_Corners = new[] { m_Rect0.TopLeft, m_Rect0.TopRight, m_Rect0.BottomLeft, m_Rect0.BottomRight }
                    .Select(c => Turn(c, m_Origin0, s_Delta)).ToList();
                var s_Rect = new Rect(new Point(s_Corners.Min(c => c.X), s_Corners.Min(c => c.Y)), new Point(s_Corners.Max(c => c.X), s_Corners.Max(c => c.Y)));
                ApplyPreview(p, m_Origin0, m_Sx0, m_Sy0, s_Theta, s_Rect);
                Redraw();
                return;
            }
            var s_MovesX = m_Handle is HandleKind.NW or HandleKind.NE or HandleKind.E or HandleKind.SE or HandleKind.SW or HandleKind.W;
            var s_MovesY = m_Handle is HandleKind.NW or HandleKind.N or HandleKind.NE or HandleKind.SE or HandleKind.S or HandleKind.SW;
            double kx = 1, ky = 1;
            var dx0 = m_HandleStart.X - m_Anchor.X; var dy0 = m_HandleStart.Y - m_Anchor.Y;
            if (s_MovesX && System.Math.Abs(dx0) > 0.5) kx = (p_At.X - m_Anchor.X) / dx0;
            if (s_MovesY && System.Math.Abs(dy0) > 0.5) ky = (p_At.Y - m_Anchor.Y) / dy0;
            if (p_Uniform)
            {
                var k = s_MovesX && s_MovesY ? (System.Math.Abs(kx - 1) >= System.Math.Abs(ky - 1) ? kx : ky) : s_MovesX ? kx : ky;
                kx = ky = k;
            }
            kx = System.Math.Clamp(kx, 0.02, 50); ky = System.Math.Clamp(ky, 0.02, 50);
            m_LastK = (kx, ky);
            // the anchor stays where it is: the origin scales about it along with the box
            var s_Origin = new Point(m_Anchor.X + (m_Origin0.X - m_Anchor.X) * kx, m_Anchor.Y + (m_Origin0.Y - m_Anchor.Y) * ky);
            var l = m_Anchor.X + (m_Rect0.Left - m_Anchor.X) * kx; var rg = m_Anchor.X + (m_Rect0.Right - m_Anchor.X) * kx;
            var t = m_Anchor.Y + (m_Rect0.Top - m_Anchor.Y) * ky; var b = m_Anchor.Y + (m_Rect0.Bottom - m_Anchor.Y) * ky;
            var s_Box = new Rect(new Point(System.Math.Min(l, rg), System.Math.Min(t, b)), new Point(System.Math.Max(l, rg), System.Math.Max(t, b)));
            ApplyPreview(p, s_Origin, m_Sx0 * kx, m_Sy0 * ky, m_Angle0, s_Box);
            Redraw();
        }

        static Point Turn(Point p, Point p_About, double p_Theta)
        {
            var dx = p.X - p_About.X; var dy = p.Y - p_About.Y;
            var c = System.Math.Cos(p_Theta); var s = System.Math.Sin(p_Theta);
            return new Point(p_About.X + dx * c - dy * s, p_About.Y + dx * s + dy * c);
        }

        static double NormalizeDegrees(double d)
        {
            while (d > 180) d -= 360;
            while (d <= -180) d += 360;
            return d;
        }

        /// <summary>Writes a transform into the placement (matrix terms, origin, local translation, box) and moves the live art accordingly.</summary>
        void ApplyPreview(PlacementInfo p, Point p_Origin, double p_Sx, double p_Sy, double p_Theta, Rect p_Box)
        {
            var c = System.Math.Cos(p_Theta); var s = System.Math.Sin(p_Theta);
            p.ScaleX = p_Sx * c; p.RotateSkew0 = p_Sx * s; p.RotateSkew1 = -p_Sy * s; p.ScaleY = p_Sy * c;
            p.X = System.Math.Round(p_Origin.X * 100) / 100; p.Y = System.Math.Round(p_Origin.Y * 100) / 100;
            var (lx, ly) = p.LocalFor(p.X, p.Y);
            p.LocalX = System.Math.Round(lx * 100) / 100; p.LocalY = System.Math.Round(ly * 100) / 100;
            if (!m_Bounds0Empty)
                p.Bounds = new RimeLib.Cmd.Scaleform.GfxMovie.Bounds
                {
                    Left = (int)System.Math.Round(p_Box.Left * 20), Top = (int)System.Math.Round(p_Box.Top * 20),
                    Right = (int)System.Math.Round(p_Box.Right * 20), Bottom = (int)System.Math.Round(p_Box.Bottom * 20),
                };
            if (ArtGroups != null && ArtGroups.TryGetValue(p.Path, out var s_Group) && !s_Group.IsFrozen)
            {
                // WPF's (M11, M12, M21, M22) is the SWF's (ScaleX, RotateSkew0, RotateSkew1, ScaleY): x' = M11 x + M21 y, y' = M12 x + M22 y
                s_Group.Transform = new MatrixTransform(p.ScaleX, p.RotateSkew0, p.RotateSkew1, p.ScaleY, p.LocalX, p.LocalY);
                m_Art.InvalidateVisual();
            }
        }

        /// <summary>Ends the handle drag: a changed size or angle is reported (the window writes the stage ops); nothing changed = just a redraw.</summary>
        public void EndHandle()
        {
            var p = SelectedPlacement;
            var s_Kind = m_Handle;
            m_Handle = HandleKind.None;
            if (p == null || s_Kind == HandleKind.None) { Redraw(); return; }
            if (s_Kind == HandleKind.Rotate)
            {
                if (System.Math.Abs(NormalizeDegrees(m_LastAngle - m_Angle0 * 180 / System.Math.PI)) > 0.01) PlacementRotated?.Invoke(p.Name, System.Math.Round(m_LastAngle, 2));
                else Redraw();
                return;
            }
            if (System.Math.Abs(m_LastK.X - 1) > 1e-4 || System.Math.Abs(m_LastK.Y - 1) > 1e-4)
                PlacementResized?.Invoke(p.Name, p.LocalX, p.LocalY, System.Math.Round(m_Sx0 * m_LastK.X, 4), System.Math.Round(m_Sy0 * m_LastK.Y, 4));
            else Redraw();
        }

        public bool HandleInProgress => m_Handle != HandleKind.None;

        public static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
