using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using RimeLib.Cmd.UiBuilder;

namespace RimeUIEditor.View
{
    /// <summary>
    /// One row of the property grid: a category header, or a named value of some kind with the editor that kind
    /// needs (check box, drop-down, text, reference picker, expandable struct, list with clear/add and one row per
    /// element). Values are in the document's spelling (JSON tokens); the row's callbacks write them back.
    /// </summary>
    public class PropRow
    {
        public string Path = "";                       // unique within the grid: keeps the expanded state across rebuilds
        public string Name = "";
        public string? Tooltip;
        public UiTypeCatalog.Kind Kind = UiTypeCatalog.Kind.String;
        public string TypeName = "";                   // enum / struct / instance / reference type
        public JToken? Value;                          // editable value; null when the row is display-only (Text)
        public string? Text;                           // display-only text
        public bool ReadOnly;
        public bool Modified;                          // the document overrides the shipped value
        public bool IsCategory;
        public List<PropRow> Children = new();
        public IReadOnlyList<string>? Choices;         // enum members, or a picker list for references
        public string? Summary;                        // struct / instance / list summary shown on the parent row
        public Action<JToken?>? OnChange;
        public Action? OnAdd;                          // list: +
        public Action? OnClear;                        // list: X
        public Action? OnRemove;                       // element: x
        public Action? OnAction;                       // a button of the row's own (a file to choose, a fit to apply), labelled ActionLabel
        public string ActionLabel = "…";
        public string? ActionTip;
        public bool Expandable => Children.Count > 0 || OnAdd != null;
    }

    /// <summary>
    /// A property grid in the style of the game's own editor: two columns, collapsible categories, bold names for
    /// values the document changed, and a footer naming the selected object's type. Rows come from PropRow trees;
    /// the grid only draws and routes edits.
    /// </summary>
    public class PropertyGrid : StackPanel
    {
        static readonly Brush s_CategoryBg = new SolidColorBrush(Color.FromRgb(58, 58, 63));
        static readonly Brush s_RowLine = new SolidColorBrush(Color.FromRgb(45, 45, 48));
        static readonly Brush s_NameFg = new SolidColorBrush(Color.FromRgb(210, 210, 210));
        static readonly Brush s_ReadOnlyFg = new SolidColorBrush(Color.FromRgb(150, 150, 150));
        static readonly Brush s_ModifiedFg = Brushes.Khaki;
        static readonly Brush s_EditorBg = new SolidColorBrush(Color.FromRgb(30, 30, 30));
        const double NameWidth = 150, Indent = 14;

        readonly HashSet<string> m_Collapsed = new();
        readonly List<PropRow> m_Rows = new();
        /// <summary>Sort each category's rows by name instead of the type's declaration order.</summary>
        public bool Alphabetical { get; set; }
        readonly TextBlock m_Footer = new() { Margin = new Thickness(6, 8, 6, 4), FontWeight = FontWeights.Bold, Foreground = Brushes.Silver, FontSize = 11 };
        public IEnumerable<PropRow> Rows => m_Rows;
        /// <summary>Row path -> the element that edits it (test seam: drive the grid the way the user does).</summary>
        public Dictionary<string, FrameworkElement> Editors { get; } = new();

        /// <summary>The drag format the explorer and the palette put on the clipboard: a partition name.</summary>
        public const string PartitionFormat = "rime/partition";
        /// <summary>Partition name -> its primary type (the window knows); a reference row only accepts a matching drop.</summary>
        public Func<string, string>? PartitionTypeOf { get; set; }
        /// <summary>(type, baseType) -> whether the type derives from the base (UICustomizationCompData is a UIComponentData); exact match when unset.</summary>
        public Func<string, string, bool>? TypeMatches { get; set; }

        /// <summary>Whether a partition may be dropped on a reference row: the row's type (or a derived one), or an untyped row.</summary>
        public bool AcceptsPartition(PropRow r, string p_Partition)
        {
            if (r.Kind != UiTypeCatalog.Kind.Ref || r.OnChange == null || r.ReadOnly) return false;
            var s_Type = PartitionTypeOf?.Invoke(p_Partition) ?? "";
            if (string.IsNullOrEmpty(r.TypeName) || string.IsNullOrEmpty(s_Type) || s_Type == "…") return true;
            if (string.Equals(s_Type, r.TypeName, StringComparison.OrdinalIgnoreCase)) return true;
            return TypeMatches?.Invoke(s_Type, r.TypeName) == true;
        }

        /// <summary>Drops a partition on a row (the seam the drag gesture ends in): true when the row took it.</summary>
        public bool DropOnRow(string p_Path, string p_Partition)
        {
            var r = Flatten(m_Rows).FirstOrDefault(x => x.Path == p_Path);
            if (r == null || !AcceptsPartition(r, p_Partition)) return false;
            r.OnChange!(p_Partition);
            return true;
        }

        static IEnumerable<PropRow> Flatten(IEnumerable<PropRow> p_Rows) => p_Rows.SelectMany(r => new[] { r }.Concat(Flatten(r.Children)));

        public PropertyGrid()
        {
            Background = (Brush)Application.Current.Resources["PanelBrush"];
        }

        public void SetRows(IEnumerable<PropRow> p_Rows, string p_Footer)
        {
            m_Rows.Clear(); m_Rows.AddRange(p_Rows);
            Children.Clear(); Editors.Clear();
            foreach (var r in m_Rows) AddRow(r, 0);
            m_Footer.Text = p_Footer;
            Children.Add(new Border { BorderBrush = s_RowLine, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 6, 0, 0) });
            Children.Add(m_Footer);
        }

        bool IsExpanded(PropRow r) => !m_Collapsed.Contains(r.Path);

        /// <summary>Collapses or expands a row by path ("cat:Document", "f.DataBinding") and redraws.</summary>
        public void SetExpanded(string p_Path, bool p_Expanded)
        {
            if (p_Expanded) m_Collapsed.Remove(p_Path); else m_Collapsed.Add(p_Path);
            SetRows(m_Rows.ToList(), m_Footer.Text);
        }

        void AddRow(PropRow r, int p_Level)
        {
            var s_Grid = new Grid { Margin = new Thickness(0, 0, 0, 0), MinHeight = 22 };
            s_Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NameWidth) });
            s_Grid.ColumnDefinitions.Add(new ColumnDefinition());
            var s_Border = new Border { Child = s_Grid, BorderBrush = s_RowLine, BorderThickness = new Thickness(0, 0, 0, 1), Background = r.IsCategory ? s_CategoryBg : null };

            // name cell: expander glyph + name, indented by nesting level
            var s_Name = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4 + p_Level * Indent, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            if (r.Expandable || r.IsCategory)
            {
                var s_Glyph = new TextBlock { Text = IsExpanded(r) ? "⊟" : "⊞", Width = 14, Foreground = Brushes.Silver, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
                s_Glyph.MouseLeftButtonDown += (_, e) => { if (IsExpanded(r)) m_Collapsed.Add(r.Path); else m_Collapsed.Remove(r.Path); SetRows(m_Rows.ToList(), m_Footer.Text); e.Handled = true; };
                s_Name.Children.Add(s_Glyph);
            }
            else s_Name.Children.Add(new TextBlock { Width = 14 });
            s_Name.Children.Add(new TextBlock
            {
                Text = r.Name, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = r.IsCategory ? Brushes.White : r.Modified ? s_ModifiedFg : r.ReadOnly ? s_ReadOnlyFg : s_NameFg,
                FontWeight = r.IsCategory || r.Modified ? FontWeights.Bold : FontWeights.Normal, ToolTip = r.Tooltip,
            });
            Grid.SetColumn(s_Name, 0); s_Grid.Children.Add(s_Name);

            if (!r.IsCategory)
            {
                var s_Editor = Editor(r);
                Grid.SetColumn(s_Editor, 1); s_Grid.Children.Add(s_Editor);
            }
            Children.Add(s_Border);
            if (IsExpanded(r))
                foreach (var c in Alphabetical && r.IsCategory ? r.Children.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList() : r.Children)
                    AddRow(c, r.IsCategory ? p_Level : p_Level + 1);
        }

        static string Fmt(JToken? v) => v == null ? "" : v.Type == JTokenType.String ? (string)v! : v.Type is JTokenType.Float or JTokenType.Integer ? v.ToString(Newtonsoft.Json.Formatting.None) : v.ToString(Newtonsoft.Json.Formatting.None);

        FrameworkElement Editor(PropRow r)
        {
            var s_Panel = new DockPanel { LastChildFill = true, Margin = new Thickness(2, 1, 4, 1) };
            // list / element buttons on the right
            if (r.OnRemove != null) DockPanel.SetDock(AddButton(s_Panel, "x", "remove this element", r.OnRemove), Dock.Right);
            if (r.OnAdd != null) DockPanel.SetDock(AddButton(s_Panel, "+", "add an element", r.OnAdd), Dock.Right);
            if (r.OnClear != null) DockPanel.SetDock(AddButton(s_Panel, "X", "remove every element", r.OnClear), Dock.Right);
            if (r.OnAction != null) DockPanel.SetDock(AddButton(s_Panel, r.ActionLabel, r.ActionTip ?? "", r.OnAction), Dock.Right);
            FrameworkElement s_Main;
            // a struct / list / inline instance row carries no value of its own: a summary, or a type drop-down for an instance
            var s_Composite = r.Value == null && (r.Children.Count > 0 || r.OnAdd != null || r.Kind is UiTypeCatalog.Kind.Struct or UiTypeCatalog.Kind.List || r.Summary != null);
            if (r.ReadOnly || (r.Value == null && r.OnChange == null && !s_Composite))
            {
                s_Main = new TextBlock { Text = r.Text ?? r.Summary ?? Fmt(r.Value), Foreground = s_ReadOnlyFg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = r.Text ?? r.Summary ?? Fmt(r.Value) };
            }
            else if (s_Composite)
            {
                if (r.Choices != null && r.OnChange != null)
                {
                    var s_Combo = new ComboBox { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, ToolTip = r.Tooltip };
                    foreach (var c in r.Choices) s_Combo.Items.Add(c);
                    s_Combo.SelectedItem = r.Choices.Contains(r.TypeName) ? r.TypeName : r.Choices.FirstOrDefault();
                    s_Combo.SelectionChanged += (_, _) => { if (s_Combo.SelectedItem is string s && s != r.TypeName) r.OnChange?.Invoke(s); };
                    s_Main = s_Combo;
                }
                else
                    s_Main = new TextBlock { Text = r.Summary ?? "", Foreground = s_ReadOnlyFg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = r.Summary };
            }
            else if (r.Kind == UiTypeCatalog.Kind.Bool)
            {
                var s_Box = new CheckBox { IsChecked = r.Value?.Type == JTokenType.Boolean ? (bool)r.Value : string.Equals((string?)r.Value, "true", StringComparison.OrdinalIgnoreCase), VerticalAlignment = VerticalAlignment.Center, Foreground = s_NameFg };
                s_Box.Checked += (_, _) => r.OnChange?.Invoke(true);
                s_Box.Unchecked += (_, _) => r.OnChange?.Invoke(false);
                s_Main = s_Box;
            }
            else if (r.Kind == UiTypeCatalog.Kind.Enum && r.Choices != null)
            {
                var s_Combo = new ComboBox { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, IsEditable = false };
                foreach (var c in r.Choices) s_Combo.Items.Add(c);
                var s_Current = (string?)r.Value ?? "";
                s_Combo.SelectedItem = r.Choices.FirstOrDefault(c => c == s_Current) ?? r.Choices.FirstOrDefault(c => c.EndsWith("_" + s_Current, StringComparison.Ordinal));
                s_Combo.SelectionChanged += (_, _) => { if (s_Combo.SelectedItem is string s && s != s_Current) { s_Current = s; r.OnChange?.Invoke(s); } };
                s_Main = s_Combo;
            }
            else if (r.Kind is UiTypeCatalog.Kind.String or UiTypeCatalog.Kind.Int && r.Choices != null)
            {
                // free text with suggestions (a binding's DataName, a widget property's known values): an editable combo
                var s_Combo = new ComboBox { IsEditable = true, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Text = Fmt(r.Value), ToolTip = r.Tooltip };
                foreach (var c in r.Choices) s_Combo.Items.Add(c);
                var s_Last = Fmt(r.Value);
                void Commit() { var t = s_Combo.Text ?? ""; if (t == s_Last) return; s_Last = t; r.OnChange?.Invoke(Parse(r, t)); }
                s_Combo.LostFocus += (_, _) => Commit();
                s_Combo.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
                s_Combo.SelectionChanged += (_, _) => { if (s_Combo.SelectedItem is string s) { s_Combo.Text = s; Commit(); } };
                s_Main = s_Combo;
            }
            else if (r.Kind == UiTypeCatalog.Kind.Ref)
            {
                var s_Text = TextEditor(r, Fmt(r.Value));
                var s_Pick = new Button { Content = "…", Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(2, 0, 0, 0), FontSize = 10, ToolTip = "pick " + (r.TypeName == "" ? "a reference" : "a " + r.TypeName) + " — or drop one here from the explorer" };
                s_Pick.Click += (_, _) => ShowPicker(s_Pick, r);
                DockPanel.SetDock(s_Pick, Dock.Right);
                s_Panel.Children.Add(s_Pick);
                // a partition dragged from the explorer / palette lands in the reference
                s_Text.AllowDrop = true;
                s_Text.PreviewDragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(PartitionFormat) && AcceptsPartition(r, (string)e.Data.GetData(PartitionFormat)!) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
                s_Text.PreviewDrop += (_, e) => { if (e.Data.GetDataPresent(PartitionFormat) && DropOnRow(r.Path, (string)e.Data.GetData(PartitionFormat)!)) e.Handled = true; };
                s_Main = s_Text;
            }
            else if (r.Value is JObject or JArray)
                s_Main = new TextBlock { Text = r.Summary ?? Fmt(r.Value), Foreground = s_ReadOnlyFg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = r.Summary };
            else
                s_Main = TextEditor(r, Fmt(r.Value));
            s_Panel.Children.Add(s_Main);
            Editors[r.Path] = s_Main;
            return s_Panel;
        }

        TextBox TextEditor(PropRow r, string p_Text)
        {
            var s_Box = new TextBox { Text = p_Text, FontSize = 11, FontFamily = new FontFamily("Consolas"), Background = s_EditorBg, Foreground = Brushes.Gainsboro, BorderBrush = s_RowLine, VerticalAlignment = VerticalAlignment.Center, ToolTip = r.Tooltip };
            var s_Last = p_Text;
            void Commit()
            {
                if (s_Box.Text == s_Last) return;
                s_Last = s_Box.Text;
                r.OnChange?.Invoke(Parse(r, s_Box.Text));
            }
            s_Box.LostFocus += (_, _) => Commit();
            s_Box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
            return s_Box;
        }

        /// <summary>Text typed into a row, as the token its kind wants (numbers stay numbers, everything else text).</summary>
        static JToken Parse(PropRow r, string p_Text)
        {
            var inv = CultureInfo.InvariantCulture;
            switch (r.Kind)
            {
                case UiTypeCatalog.Kind.Int: return long.TryParse(p_Text.Trim(), NumberStyles.Integer, inv, out var l) ? l : 0;
                case UiTypeCatalog.Kind.Float: return double.TryParse(p_Text.Trim(), NumberStyles.Float, inv, out var d) ? d : 0.0;
                default: return p_Text;
            }
        }

        static Button AddButton(Panel p_Parent, string p_Text, string p_Tip, Action p_Action)
        {
            var b = new Button { Content = p_Text, Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(2, 0, 0, 0), FontSize = 10, ToolTip = p_Tip, VerticalAlignment = VerticalAlignment.Center };
            b.Click += (_, _) => p_Action();
            p_Parent.Children.Add(b);
            return b;
        }

        void ShowPicker(FrameworkElement p_Anchor, PropRow r)
        {
            var s_All = r.Choices?.ToList() ?? new List<string>();
            var s_Popup = new Popup { PlacementTarget = p_Anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
            var s_Panel = new StackPanel { Width = 360, Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)) };
            var s_Filter = new TextBox { Margin = new Thickness(4), FontSize = 11, Background = s_EditorBg, Foreground = Brushes.Gainsboro };
            var s_List = new ListBox { Height = 260, Margin = new Thickness(4, 0, 4, 4), FontSize = 11, Background = s_EditorBg, Foreground = Brushes.Gainsboro };
            void Fill() { var f = s_Filter.Text.Trim(); s_List.ItemsSource = s_All.Where(x => f == "" || x.Contains(f, StringComparison.OrdinalIgnoreCase)).Take(400).ToList(); }
            s_Filter.TextChanged += (_, _) => Fill();
            s_List.MouseDoubleClick += (_, _) => { if (s_List.SelectedItem is string s) { s_Popup.IsOpen = false; r.OnChange?.Invoke(s); } };
            s_List.KeyDown += (_, e) => { if (e.Key == Key.Enter && s_List.SelectedItem is string s) { s_Popup.IsOpen = false; r.OnChange?.Invoke(s); } };
            Fill();
            s_Panel.Children.Add(new TextBlock { Text = $"{s_All.Count} candidates — double-click to pick", Margin = new Thickness(6, 4, 6, 0), Foreground = Brushes.Silver, FontSize = 10 });
            s_Panel.Children.Add(s_Filter); s_Panel.Children.Add(s_List);
            s_Popup.Child = new Border { Child = s_Panel, BorderBrush = Brushes.DimGray, BorderThickness = new Thickness(1) };
            s_Popup.IsOpen = true;
            s_Filter.Focus();
        }
    }
}
