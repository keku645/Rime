using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using RimeUIEditor.Model;

namespace RimeUIEditor
{
    /// <summary>
    /// The data explorer (a folder tree of the UI database with a Name/Type list, like the game's own editor),
    /// the open-screen tabs above the stage, and the status bar. Opening a screen or flow graph goes through
    /// OpenScreen, which the headless seams call too.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>One row of the explorer list.</summary>
        public class ExplorerItem
        {
            public string Name { get; set; } = "";
            public string Type { get; set; } = "";
            public string Partition { get; set; } = "";
            public override string ToString() => Partition;
        }

        List<string> m_AllPartitions = new();           // every ui/ partition the cache or mount lists
        string m_ExplorerFolder = "ui/flow/screen";     // the tree's selected folder
        readonly List<string> m_OpenScreens = new();    // tabs, in opening order

        /// <summary>
        /// The type of a partition, from the widget catalogue's index (built in the background). Until that index
        /// exists the folder says what the partition is (ui/assets = widget, ui/uicomponents = data component, the
        /// listed screens and flow graphs): a drop or a reference row must work right after the first start too.
        /// </summary>
        string PartitionType(string p_Partition)
        {
            if (DocScreen(p_Partition)?.New == true) return "UIScreenAsset";
            if (m_WidgetCatalog != null && m_WidgetCatalog.PartitionTypes.TryGetValue(p_Partition, out var t)) return t;
            if (m_AllScreens.Contains(p_Partition)) return p_Partition.StartsWith("ui/flow/graph/", StringComparison.OrdinalIgnoreCase) ? "UIGraphAsset" : "UIScreenAsset";
            if (m_WidgetCatalog == null)
            {
                if (p_Partition.StartsWith("ui/assets/", StringComparison.OrdinalIgnoreCase) && m_AllWidgets.Contains(p_Partition)) return "UIWidgetAsset";
                if (p_Partition.StartsWith("ui/uicomponents/", StringComparison.OrdinalIgnoreCase)) return "UIComponentData";
            }
            return m_CatalogBuilding ? "…" : "";
        }

        /// <summary>Rebuilds the folder tree from the partition names (ui/flow/screen/frontend → nested items), keeping the selected folder.</summary>
        void BuildExplorerTree()
        {
            ExplorerTree.Items.Clear();
            var s_Root = new TreeViewItem { Header = "ui", Tag = "ui", IsExpanded = true, Foreground = Brushes.Gainsboro };
            var s_ByPath = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase) { ["ui"] = s_Root };
            foreach (var s_Part in m_AllPartitions)
            {
                var s_Segments = s_Part.Split('/');
                var s_Path = s_Segments[0];
                for (var i = 1; i < s_Segments.Length - 1; ++i)
                {
                    var s_Child = s_Path + "/" + s_Segments[i];
                    if (!s_ByPath.TryGetValue(s_Child, out var s_Item))
                    {
                        s_Item = new TreeViewItem { Header = s_Segments[i], Tag = s_Child, Foreground = Brushes.Gainsboro, IsExpanded = s_Child.Count(c => c == '/') < 2 };
                        s_ByPath[s_Path].Items.Add(s_Item);
                        s_ByPath[s_Child] = s_Item;
                    }
                    s_Path = s_Child;
                }
            }
            ExplorerTree.Items.Add(s_Root);
            if (s_ByPath.TryGetValue(m_ExplorerFolder, out var s_Sel)) s_Sel.IsSelected = true;
        }

        void OnExplorerFolder(object p_Sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (ExplorerTree.SelectedItem is TreeViewItem s_Item && s_Item.Tag is string s_Folder)
            {
                m_ExplorerFolder = s_Folder;
                // a folder click leaves a type filter (the folder view is the "(all types)" one)
                if (TypeFilterValue != null) { TypeFilter.SelectionChanged -= OnTypeFilter; TypeFilter.SelectedItem = c_AllTypes; TypeFilter.SelectionChanged += OnTypeFilter; }
                if (string.IsNullOrWhiteSpace(ScreenFilter.Text)) FillExplorerList();
                UpdateStatus();
            }
        }

        bool m_FillingExplorer;   // the list is being rebuilt: its selection is restored, not chosen — nothing opens

        /// <summary>
        /// The list: the selected folder's partitions, or — with a search text — every ui/ partition matching it.
        /// Rebuilding the list restores the selected row without opening it: a refresh (the mount, the catalogue,
        /// a folder click) is not the user picking a screen — it used to re-open the selected row over whatever
        /// screen was on the stage (a flow graph's row emptied the Layers).
        /// </summary>
        const string c_AllTypes = "(all types)";

        /// <summary>The type drop-down: every type the UI database holds, most common first ("(all types)" = browse by folder).</summary>
        void FillTypeFilter()
        {
            var s_Was = TypeFilter.SelectedItem as string ?? c_AllTypes;
            var s_Types = m_WidgetCatalog != null && m_WidgetCatalog.PartitionTypes.Count > 0
                ? m_WidgetCatalog.PartitionTypes.Values.GroupBy(t => t).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).ToList()
                : new List<string> { "UIScreenAsset", "UIGraphAsset", "UIWidgetAsset", "UIComponentData" };
            TypeFilter.SelectionChanged -= OnTypeFilter;
            TypeFilter.Items.Clear();
            TypeFilter.Items.Add(c_AllTypes);
            foreach (var t in s_Types) TypeFilter.Items.Add(t);
            TypeFilter.SelectedItem = TypeFilter.Items.Contains(s_Was) ? s_Was : c_AllTypes;
            TypeFilter.SelectionChanged += OnTypeFilter;
        }

        void OnTypeFilter(object p_Sender, SelectionChangedEventArgs e)
        {
            if (ScreenList == null) return;
            FillExplorerList();
            UpdateStatus();
        }

        /// <summary>The type the list is restricted to, or null for the folder / search view.</summary>
        string? TypeFilterValue => TypeFilter?.SelectedItem is string t && t != c_AllTypes ? t : null;

        void FillExplorerList()
        {
            var f = ScreenFilter.Text.Trim();
            var s_Type = TypeFilterValue;
            // a type restricts the list to every partition of that type, whatever its folder (search still applies); else the search or the folder
            IEnumerable<string> s_Source = s_Type != null
                ? m_AllPartitions.Where(p => string.Equals(PartitionType(p), s_Type, StringComparison.OrdinalIgnoreCase) && (f == "" || p.Contains(f, StringComparison.OrdinalIgnoreCase)))
                : f != ""
                ? m_AllPartitions.Where(p => p.Contains(f, StringComparison.OrdinalIgnoreCase))
                : m_AllPartitions.Where(p => p.StartsWith(m_ExplorerFolder + "/", StringComparison.OrdinalIgnoreCase) && p.IndexOf('/', m_ExplorerFolder.Length + 1) < 0);
            var s_Selected = (ScreenList.SelectedItem as ExplorerItem)?.Partition;
            var s_FullNames = f != "" || s_Type != null;
            m_FillingExplorer = true;
            try
            {
                ScreenList.ItemsSource = s_Source.Select(p => new ExplorerItem { Partition = p, Name = (s_FullNames ? p : p.Split('/').Last()) + (DocScreen(p)?.New == true ? "  (new)" : ""), Type = PartitionType(p) }).ToList();
                if (s_Selected != null) ScreenList.SelectedItem = (ScreenList.ItemsSource as List<ExplorerItem>)?.FirstOrDefault(i => i.Partition == s_Selected);
            }
            finally { m_FillingExplorer = false; }
        }

        void OnScreenFilter(object p_Sender, TextChangedEventArgs e)
        {
            if (ScreenList == null) return;
            FillExplorerList();
        }

        string? m_PendingOpen;                          // a screen selected by a mouse press: opened on release, unless the press became a drag
        /// <summary>Test seam: treat every selection as a mouse press (opened by CommitPendingOpen, dropped by CancelPendingOpen).</summary>
        public bool DeferExplorerOpens;

        /// <summary>
        /// A click on a screen or flow graph opens it; other partition types just show their type. The selection
        /// happens on the mouse PRESS, and a press may be the start of a drag (a screen dragged onto a flow graph):
        /// so with the button down the open waits for the release, and a drag cancels it — otherwise the dragged
        /// screen would replace the flow graph it was meant to land on.
        /// </summary>
        async void OnScreenSelected(object p_Sender, SelectionChangedEventArgs e)
        {
            if (m_FillingExplorer) return;   // a rebuilt list restoring its row: not a pick
            if (ScreenList.SelectedItem is not ExplorerItem s_Item) return;
            var s_Type = PartitionType(s_Item.Partition);
            if (!(ScreenModel.IsGraphAssetType(s_Type) || m_AllScreens.Contains(s_Item.Partition))) { UpdateStatus(); return; }
            if (DeferExplorerOpens || Mouse.LeftButton == MouseButtonState.Pressed) { PendOpen(s_Item.Partition); UpdateStatus(); return; }
            await OpenScreen(s_Item.Partition);
        }

        void OnExplorerMouseUp(object p_Sender, MouseButtonEventArgs e) => _ = CommitPendingOpen();

        /// <summary>The release that ends a click on a screen: opens what the press selected (nothing when the press became a drag).</summary>
        public async Task<bool> CommitPendingOpen()
        {
            var s_Partition = m_PendingOpen;
            m_PendingOpen = null;
            m_OpenTimer?.Stop();
            if (s_Partition == null) return false;
            await OpenScreen(s_Partition);
            return true;
        }

        public void CancelPendingOpen() { m_PendingOpen = null; m_OpenTimer?.Stop(); }

        // a press that never reports its release (the up event went elsewhere): the pending screen still opens once the
        // button is seen up — a click must never be swallowed
        System.Windows.Threading.DispatcherTimer? m_OpenTimer;

        void PendOpen(string p_Partition)
        {
            m_PendingOpen = p_Partition;
            if (m_OpenTimer == null)
            {
                m_OpenTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                m_OpenTimer.Tick += (_, _) =>
                {
                    if (m_PendingOpen == null) { m_OpenTimer.Stop(); return; }
                    if (DeferExplorerOpens) return;                       // the seam holds the button down on purpose
                    if (Mouse.LeftButton == MouseButtonState.Released) { m_OpenTimer.Stop(); _ = CommitPendingOpen(); }
                };
            }
            m_OpenTimer.Stop(); m_OpenTimer.Start();
        }

        /// <summary>Test seam: a screen selected by a press whose release the list never saw.</summary>
        public void PendOpenForTest(string p_Partition) => PendOpen(p_Partition);

        public string? CurrentScreen => m_Current?.Partition;

        /// <summary>The open tabs and the screen on the stage go to the settings: the next start brings them back.</summary>
        void PersistSession()
        {
            if (m_Restoring) return;
            m_Settings.OpenScreens = m_OpenScreens.ToList();
            m_Settings.CurrentScreen = m_Current?.Partition ?? "";
            // where the window is (monitor, place, size, maximised) rides with the session; a window without a handle yet keeps what was saved
            if (View.Monitors.PlacementOf(this) is { } s_Placement) m_Settings.Window = s_Placement;
            m_Settings.Save();
        }

        bool m_Restoring;

        /// <summary>Reopens the last session's screens (those the cache still lists), the one that was on the stage last.</summary>
        async Task RestoreSession()
        {
            var s_Wanted = m_Settings.OpenScreens.Where(p => m_AllScreens.Contains(p)).ToList();
            if (s_Wanted.Count == 0) return;
            m_Restoring = true;
            try
            {
                var s_Last = m_AllScreens.Contains(m_Settings.CurrentScreen) ? m_Settings.CurrentScreen : s_Wanted[^1];
                // the tabs in their old order, then the one that was on the stage comes to the front
                foreach (var p in s_Wanted) await OpenScreen(p);
                if (m_Current?.Partition != s_Last) await OpenScreen(s_Last);
                Log($"restored {s_Wanted.Count} screen(s) from the last session; {s_Last.Split('/').Last()} on the stage");
            }
            finally { m_Restoring = false; }
        }

        /// <summary>Double-click on a widget asset = add it to the open screen (the palette's button does the same).</summary>
        void OnExplorerDoubleClick(object p_Sender, MouseButtonEventArgs e)
        {
            if (ScreenList.SelectedItem is not ExplorerItem s_Item) return;
            if (PartitionType(s_Item.Partition) == "UIWidgetAsset" && s_Item.Partition.StartsWith("ui/assets/", StringComparison.OrdinalIgnoreCase))
                AddWidget(s_Item.Partition["ui/assets/".Length..]);
        }

        /// <summary>Opens a screen or flow graph (loads it once, then switches), adds its tab and shows it.</summary>
        public async Task<bool> OpenScreen(string p_Partition)
        {
            if (m_Current?.Partition == p_Partition) { RefreshTabs(); return true; }
            Status("loading " + p_Partition);
            try
            {
                if (!m_Screens.TryGetValue(p_Partition, out var s_Model))
                {
                    // a screen the document makes loads from its template (LoadModel: the template's partition under the new guids, its movie)
                    s_Model = await Task.Run(() => LoadModel(p_Partition));
                    m_Screens[p_Partition] = s_Model;
                }
                m_Current = s_Model;
                m_SelectedPlacement = null; m_SelectedNode = null; m_SelectedKey = null;
                if (!m_OpenScreens.Contains(p_Partition)) m_OpenScreens.Add(p_Partition);
                ApplyDocumentToCurrent();
                // a flow graph has nothing on the stage: open it on the graph
                if (!s_Model.HasStage && CentreTabs.SelectedIndex == 0) CentreTabs.SelectedIndex = 1;
                RefreshTabs();
                Status(p_Partition);
                Log($"opened {p_Partition}: {s_Model.Placements.Count} clips in Layers, {s_Model.AllNodes.Count} nodes on the graph" + (s_Model.HasStage ? "" : " (a flow graph: no stage)"));
                PersistSession();
                return true;
            }
            catch (Exception s_Ex)
            {
                Log("load failed: " + s_Ex.Message);
                Status("load failed");
                return false;
            }
        }

        void CloseScreen(string p_Partition)
        {
            m_OpenScreens.Remove(p_Partition);
            if (m_Current?.Partition == p_Partition)
            {
                m_Current = null; m_SelectedPlacement = null; m_SelectedNode = null; m_SelectedKey = null;
                if (m_OpenScreens.Count > 0) _ = OpenScreen(m_OpenScreens[^1]);
                else { m_Stage.Art = null; m_Stage.SetPlacements(Array.Empty<PlacementInfo>()); RefreshGraph(); ApplyDocumentToCurrent(); }
            }
            RefreshTabs();
            PersistSession();
        }

        /// <summary>Closes every tab at once (nothing on the stage afterwards).</summary>
        public void CloseAllScreens()
        {
            m_OpenScreens.Clear();
            m_Current = null; m_SelectedPlacement = null; m_SelectedNode = null; m_SelectedKey = null;
            m_Stage.Art = null; m_Stage.SetPlacements(Array.Empty<PlacementInfo>());
            RefreshGraph();
            ApplyDocumentToCurrent();
            RefreshTabs();
            PersistSession();
        }

        /// <summary>One tab per open screen: click to switch, × to close. The current one is highlighted.</summary>
        void RefreshTabs()
        {
            OpenTabs.Children.Clear();
            foreach (var s_Part in m_OpenScreens)
            {
                var s_Partition = s_Part;
                var s_Current = m_Current?.Partition == s_Partition;
                var s_Tab = new Border
                {
                    Background = new SolidColorBrush(s_Current ? Color.FromRgb(61, 90, 128) : Color.FromRgb(43, 43, 43)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)), BorderThickness = new Thickness(1, 1, 1, 0), Margin = new Thickness(0, 0, 2, 0), Padding = new Thickness(8, 3, 4, 3), Tag = s_Partition,
                };
                var s_Panel = new StackPanel { Orientation = Orientation.Horizontal };
                var s_InDoc = DocScreen(s_Partition) != null;
                s_Panel.Children.Add(new TextBlock { Text = s_Partition.Split('/').Last() + (s_InDoc ? " •" : ""), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, ToolTip = s_Partition + (s_InDoc ? "  (in the document)" : ""), Foreground = Brushes.Gainsboro });
                var s_Close = new TextBlock { Text = "×", FontSize = 12, Margin = new Thickness(8, -1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Silver, Cursor = Cursors.Hand, ToolTip = "close" };
                s_Close.MouseLeftButtonDown += (_, e2) => { CloseScreen(s_Partition); e2.Handled = true; };
                s_Panel.Children.Add(s_Close);
                s_Tab.Child = s_Panel;
                s_Tab.ToolTip = s_Partition + (s_InDoc ? "  (in the document)" : "") + "\nclick: show · middle click: close";
                s_Tab.MouseLeftButtonDown += async (_, _) => { if (m_Current?.Partition != s_Partition) await OpenScreen(s_Partition); };
                // the wheel button closes a tab, as browsers do
                s_Tab.MouseDown += (_, e2) => { if (e2.ChangedButton == MouseButton.Middle) { CloseScreen(s_Partition); e2.Handled = true; } };
                OpenTabs.Children.Add(s_Tab);
            }
            UpdateStatus();
        }

        public IReadOnlyList<string> OpenScreens => m_OpenScreens;

        /// <summary>The status bar: the current screen as a path, the selection, and the counts on the right.</summary>
        void UpdateStatus()
        {
            if (StatusLeft == null) return;
            var s_Left = m_Current == null ? m_ExplorerFolder.Replace("/", " / ") : m_Current.Partition.Replace("/", " / ");
            var s_Sel = m_SelectedPlacement ?? m_SelectedNode;
            if (s_Sel != null) s_Left += "   ›   " + s_Sel;
            StatusLeft.Text = s_Left;
            StatusRight.Text = m_Current == null
                ? $"{m_AllPartitions.Count} partitions"
                : $"{m_Current.AllNodes.Count} nodes · {m_Current.Wires.Count} connections · {m_Current.Placements.Count} placements · {m_OpenScreens.Count} open · zoom {(Viewport?.Scale ?? 1) * 100:0}%";
        }
    }
}
