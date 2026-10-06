using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace RimeUIEditor.View
{
    /// <summary>
    /// The label that follows the mouse during a drag (the name of the widget, screen, component or node type
    /// being dragged, and what it will make). An adorner on the window's root, so it never sits between the mouse
    /// and the drop target the way a popup window would.
    /// </summary>
    public class DragGhost : Adorner
    {
        readonly string m_Text;
        Point m_At = new(-1000, -1000);
        static readonly Typeface s_Face = new("Segoe UI");
        static readonly Brush s_Fill = new SolidColorBrush(Color.FromArgb(230, 61, 90, 128));
        static readonly Pen s_Edge = new(new SolidColorBrush(Color.FromArgb(255, 150, 180, 220)), 1);

        public DragGhost(UIElement p_Root, string p_Text) : base(p_Root)
        {
            m_Text = p_Text;
            IsHitTestVisible = false;
        }

        /// <summary>Puts the label next to a point in the root's coordinates (the mouse, during GiveFeedback).</summary>
        public void MoveTo(Point p_At) { m_At = p_At; InvalidateVisual(); }

        protected override void OnRender(DrawingContext p_Context)
        {
            if (m_At.X < -500) return;
            var s_Text = new FormattedText(m_Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, s_Face, 11, Brushes.White, 1.0);
            var s_Rect = new Rect(m_At.X + 16, m_At.Y + 12, s_Text.Width + 12, s_Text.Height + 6);
            p_Context.DrawRoundedRectangle(s_Fill, s_Edge, s_Rect, 3, 3);
            p_Context.DrawText(s_Text, new Point(s_Rect.X + 6, s_Rect.Y + 3));
        }
    }
}
