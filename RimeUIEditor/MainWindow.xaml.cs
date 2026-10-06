using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimeLib.Cmd.Scaleform;
using RimeLib.Cmd.UiBuilder;
using RimeUIEditor.Model;
using RimeUIEditor.View;

namespace RimeUIEditor
{
    /// <summary>
    /// Composes BF3 UI screens: pick a screen (or a flow graph), see its stage and graph, add widgets from the
    /// palette, move them, set their properties, binding and connections, and build the mod. The editor never
    /// invents a mechanism: everything it emits goes through ui_build_mod, and everything it shows is read from
    /// the mounted game. Every change to the document goes through PushUndo first: Ctrl+Z / Ctrl+Y walk the
    /// document's history (JSON snapshots, like the shader editor).
    /// </summary>
    public partial class MainWindow : Window
    {
        const int c_UndoDepth = 100;

        readonly EditorSettings m_Settings = EditorSettings.Load();
        readonly RimeUiService m_Rime = new();
        readonly StageCanvas m_Stage = new();
        readonly GraphView m_Graph = new();
        string? m_SelectedNode;                 // a node picked on the graph (any type); placements select widgets
        string? m_SelectedKey;                  // the picked node's guid (labels can repeat on a screen)
        readonly Dictionary<string, GfxMovie?> m_WidgetMovies = new();
        readonly Dictionary<string, ScreenModel> m_Screens = new();

        ScreenDocument m_Doc = new() { Name = "MyUiMod", Superbundle = "myuimod/ui", Bundle = "myuimod/uibundle" };
        string m_DocPath = "";
        readonly List<string> m_Undo = new();
        readonly List<string> m_Redo = new();
        List<string> m_AllScreens = new();
        List<string> m_AllWidgets = new();
        ScreenModel? m_Current;
        string? m_SelectedPlacement;

        public MainWindow()
        {
            InitializeComponent();
            Viewport.Stage = m_Stage;
            Viewport.ScaleChanged += OnViewportScale;
            StageViewport.ZoomSpeed = m_Settings.CanvasZoomSpeed;
            GraphViewport.Stage = m_Graph;
            GraphViewport.ContentSize = new Size(GraphView.GraphWidth, GraphView.GraphHeight);
            m_Graph.SelectionChanged += OnGraphSelection;
            m_Graph.WireRequested += OnWireRequested;
            m_Graph.WireRemoveRequested += OnWireRemoveRequested;
            m_Graph.NodeMoving += _ => PushUndo();
            m_Graph.NodeMoved += _ => GraphViewport.ContentSize = m_Graph.Extent();
            GraphViewport.ScaleChanged += p_Scale => m_Graph.ViewScale = p_Scale;
            foreach (var t in UiTypeCatalog.NodeTypes().Where(t => t != "WidgetNode")) NodeTypeBox.Items.Add(t);
            NodeTypeBox.SelectedItem = "DataSetNode";
            InitDragDrop();
            UpdateUndoButtons();
            // the window comes back where it was closed — the monitor the user keeps it on, the place and size, maximised or not
            Monitors.Restore(this, m_Settings.Window);
            // the pictures' converter, as the settings name it
            RimeLib.Cmd.UiBuilder.ImageTextures.TexconvOverride = m_Settings.TexconvPath;

            // Asked before the first line about the game, never in a headless run (a modal there is a hang).
            if (m_Settings.IsFirstRun && !EditorSettings.Headless)
            {
                var s_FirstRun = new FirstRunWindow(m_Settings);
                s_FirstRun.ShowDialog();
                m_Settings.GamePath = s_FirstRun.GamePath;
                m_Settings.ModsPath = s_FirstRun.ModsPath;
                m_Settings.Save();
            }
            ShowPaths();
            UpdateSaveButtons();
            // the graph mirrors the stage's selection (the whole set, no event back)
            m_Stage.SelectionChanged += p_Name => { m_SelectedPlacement = p_Name; m_SelectedNode = p_Name; m_SelectedKey = null; m_Graph.SetSelection(m_Stage.SelectedSet, p_Name); SyncLayerSelection(); ShowProperties(); UpdateStatus(); };
            m_Stage.PlacementMoved += OnPlacementMoved;
            m_Stage.PlacementsMoved += OnPlacementsMoved;
            m_Stage.PlacementResized += OnPlacementResized;
            m_Stage.PlacementRotated += OnPlacementRotated;
            Log(EditorSettings.LooksLikeGame(m_Settings.GamePath)
                ? $"BF3 at {m_Settings.GamePath}."
                : "BF3 install not detected — set it in Settings… ▸ Game folder.");
            var s_Warm = OpenCache();
            if (!s_Warm)
                Log("No cache for this game yet: Mount game (minutes the first time; the UI is cached on the way, later runs need no mount).");
            if (!string.IsNullOrEmpty(m_Settings.LastDocument) && File.Exists(m_Settings.LastDocument))
                LoadDocument(m_Settings.LastDocument);
            ApplyDocumentToCurrent();   // nothing open yet: the stage says so
            // the screens of the last session come back (the tabs, and the one on the stage); saved again on every open / close and at exit
            Closing += (_, _) => PersistSession();
            if (s_Warm) _ = RestoreSession();
        }

        // ------------------------------------------------------------------------------------------ cache

        /// <summary>
        /// Points the service at this game's cache and, when it is warm, lists screens and widgets from it
        /// so the editor is usable at once. Returns whether the cache was warm.
        /// </summary>
        bool OpenCache()
        {
            if (!EditorSettings.LooksLikeGame(m_Settings.GamePath) || string.IsNullOrWhiteSpace(m_Settings.CachePath)) return false;
            try
            {
                var s_Dir = RimeUiService.CacheDirFor(m_Settings.CachePath, m_Settings.GamePath);
                if (!m_Rime.UseCache(s_Dir)) return false;
                RefreshLists();
                var s_Manifest = m_Rime.CacheManifest();
                var s_When = s_Manifest?["written"]?.ToString() ?? "?";
                Status($"cache ({s_When}): {m_AllScreens.Count} screens + graphs, {m_AllWidgets.Count} assets — not mounted");
                Log($"Reading the UI from the cache written {s_When} ({s_Dir}). Build mod mounts the game by itself when needed.");
                // a cache from an older build lacks what this one reads (the icons, the kits and weapons the preview's data comes from):
                // it looks warm and the preview comes up without pictures or profile — say so, and what fills it
                if (m_Rime.CachedVersion < RimeUiService.CacheVersion)
                {
                    Log($"THE CACHE PREDATES THIS EDITOR (version {m_Rime.CachedVersion} < {RimeUiService.CacheVersion}): the preview would show no icons and no kit/weapon profile. Press Mount once — the cache is completed with what is missing (minutes), nothing already there is fetched again.");
                    Status($"cache ({s_When}) is older than this editor: press Mount once to complete it");
                }
                EnsureWidgetCatalog();
                return true;
            }
            catch (Exception s_Ex)
            {
                Log("cache not usable: " + s_Ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Screens (…/flow/screen) and the flow graphs that chain them (…/flow/graph, UIGraphAsset only — the audio
        /// event assets there are not graphs), from the base game's ui/flow and the expansions' (ui/xp5/flow: End
        /// Game's CTF HUD, spawn ticket counter and graphs — a folder walk under ui/flow alone never listed them).
        /// </summary>
        void RefreshLists()
        {
            var s_All = m_Rime.Partitions("ui/").ToList();
            m_AllScreens = s_All.Where(p => p.Contains("/flow/screen/", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var s_Graph in s_All.Where(p => p.Contains("/flow/graph/", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var s_Json = m_Rime.PartitionJson(s_Graph);
                    var s_Type = (s_Json["Instances"] as JObject)?[(string?)s_Json["PrimaryInstanceGuid"] ?? ""]?["$type"]?.ToString();
                    if (ScreenModel.IsGraphAssetType(s_Type)) m_AllScreens.Add(s_Graph);
                }
                catch (Exception s_Ex) { Log($"{s_Graph}: {s_Ex.Message}"); }
            }
            m_AllWidgets = m_Rime.Partitions("ui/assets/").ToList();
            m_AllPartitions = m_Rime.Partitions("ui/").ToList();
            m_GamePartitions.Clear(); foreach (var p in m_AllPartitions) m_GamePartitions.Add(p);
            foreach (var s in m_Doc.Screens.Where(s => s.New)) { if (!m_AllScreens.Contains(s.Partition)) m_AllScreens.Add(s.Partition); if (!m_AllPartitions.Contains(s.Partition)) m_AllPartitions.Add(s.Partition); }
            BuildExplorerTree();
            FillTypeFilter();
            FillExplorerList();
            OnPaletteFilter(this, null!);
            UpdateStatus();
        }

        /// <summary>Mounts the game (once) and fills the cache. Used by the Mount button and by a build without a mount.</summary>
        async Task<bool> EnsureMounted()
        {
            if (m_Rime.IsMounted) return true;
            if (!EditorSettings.LooksLikeGame(m_Settings.GamePath)) { Log("The game folder is not set or is not the install root: Settings… ▸ Game folder."); return false; }
            MountButton.IsEnabled = false;
            MountButton.Content = "Mounting…";
            MountButton.ToolTip = "Mounting the game (minutes). The editor stays usable meanwhile.";
            Status("mounting…");
            Log($"Mounting BF3 from {m_Settings.GamePath} — this takes minutes; the editor stays usable (the screens come from the cache). A mount is only needed to Build mod" + (m_Rime.HasCache ? "" : " and to fill the cache the first time") + ".");
            var s_Log = new StringWriter();
            try
            {
                await Task.Run(() => m_Rime.Mount(m_Settings.GamePath, s_Log));
                Log(s_Log.ToString());
                RefreshLists();
                Status($"mounted: {m_AllScreens.Count} screens + graphs, {m_AllWidgets.Count} assets");
                Log($"mounted. {m_AllScreens.Count} screens and flow graphs under ui/flow, {m_AllWidgets.Count} partitions under ui/assets.");
                MountButton.Content = "Mounted";
                MountButton.ToolTip = "The game is mounted for this session (Build mod uses it).";
                if (!string.IsNullOrWhiteSpace(m_Settings.CachePath))
                {
                    var s_Dir = RimeUiService.CacheDirFor(m_Settings.CachePath, m_Settings.GamePath);
                    m_Rime.UseCache(s_Dir);
                    Status("caching the UI…");
                    var s_CacheLog = new StringWriter();
                    await Task.Run(() => m_Rime.WarmCache(s_CacheLog, (d, t, w) => Status($"caching {d}/{t}: {w}")));
                    Log(s_CacheLog.ToString());
                    Status($"mounted + cached: {m_AllScreens.Count} screens + graphs, {m_AllWidgets.Count} assets");
                }
                EnsureWidgetCatalog();
                return true;
            }
            catch (Exception s_Ex)
            {
                Log("MOUNT FAILED: " + s_Ex.Message + "\n" + s_Log);
                Status("mount failed");
                MountButton.IsEnabled = true;
                MountButton.Content = "Mount game";
                MountButton.ToolTip = "The mount failed — see the log; try again.";
                return false;
            }
        }

        // ------------------------------------------------------------------------------------------ helpers

        void Log(string p_Text)
        {
            Dispatcher.Invoke(() =>
            {
                LogBox.AppendText(p_Text.TrimEnd() + "\n");
                LogBox.ScrollToEnd();
            });
        }

        void Status(string p_Text) => Dispatcher.Invoke(() => StatusText.Text = p_Text);

        public void LogError(string p_Text) { Log(p_Text); Status("error — see the log"); }

        ScreenEntry? DocScreen(string p_Partition) =>
            m_Doc.Screens.FirstOrDefault(s => string.Equals(s.Partition, p_Partition, StringComparison.OrdinalIgnoreCase));

        ScreenEntry EnsureDocScreen(string p_Partition)
        {
            var s = DocScreen(p_Partition);
            if (s == null)
            {
                // a flow graph has no movie: its entry carries nodes and connections only
                var s_IsScreen = m_Current?.Partition == p_Partition ? m_Current.IsScreen : p_Partition.StartsWith("ui/flow/screen/", StringComparison.OrdinalIgnoreCase);
                s = new ScreenEntry { Partition = p_Partition, Movie = s_IsScreen ? "ui/assets/" + p_Partition.Split('/').Last() : null };
                m_Doc.Screens.Add(s);
            }
            return s;
        }

        GfxMovie? WidgetMovie(string p_Resource)
        {
            if (m_WidgetMovies.TryGetValue(p_Resource, out var m)) return m;
            try { m = GfxMovie.Load(m_Rime.ResourceBytes(p_Resource)); }
            catch { m = null; }
            m_WidgetMovies[p_Resource] = m;
            return m;
        }

        // ------------------------------------------------------------------------------------------ undo / redo

        string DocJson() => JsonConvert.SerializeObject(m_Doc, Formatting.None);

        public bool CanUndo => m_Undo.Count > 0;
        public bool CanRedo => m_Redo.Count > 0;

        /// <summary>Snapshots the document before a change. Every mutation of m_Doc calls this first.</summary>
        public void PushUndo()
        {
            m_Undo.Add(DocJson());
            if (m_Undo.Count > c_UndoDepth) m_Undo.RemoveAt(0);
            m_Redo.Clear();
            UpdateUndoButtons();
        }

        void ClearHistory() { m_Undo.Clear(); m_Redo.Clear(); UpdateUndoButtons(); }

        public bool Undo() => RestoreHistory(m_Undo, m_Redo);
        public bool Redo() => RestoreHistory(m_Redo, m_Undo);

        bool RestoreHistory(List<string> p_From, List<string> p_To)
        {
            if (p_From.Count == 0) return false;
            p_To.Add(DocJson());
            var s_Json = p_From[^1];
            p_From.RemoveAt(p_From.Count - 1);
            m_Doc = JsonConvert.DeserializeObject<ScreenDocument>(s_Json) ?? new ScreenDocument();
            UpdateUndoButtons();
            RegisterNewScreens();   // a new screen undone leaves the lists; one redone comes back
            ApplyDocumentToCurrent();
            return true;
        }

        void UpdateUndoButtons()
        {
            if (UndoButton == null) return;
            UndoButton.IsEnabled = CanUndo; RedoButton.IsEnabled = CanRedo;
            UndoButton.ToolTip = CanUndo ? $"Undo (Ctrl+Z) — {m_Undo.Count} step(s)" : "Nothing to undo";
            RedoButton.ToolTip = CanRedo ? $"Redo (Ctrl+Y) — {m_Redo.Count} step(s)" : "Nothing to redo";
        }

        void OnUndo(object p_Sender, ExecutedRoutedEventArgs e) => DoUndo();
        void OnRedo(object p_Sender, ExecutedRoutedEventArgs e) => DoRedo();
        void OnUndoClick(object p_Sender, RoutedEventArgs e) => DoUndo();
        void OnRedoClick(object p_Sender, RoutedEventArgs e) => DoRedo();

        void DoUndo() { if (Undo()) Log($"undo ({m_Undo.Count} left)"); else Log("Nothing left to undo."); }
        void DoRedo() { if (Redo()) Log($"redo ({m_Redo.Count} left)"); else Log("Nothing left to redo."); }

        // ------------------------------------------------------------------------------------------ art

        GfxRenderer? m_Renderer;
        TextDatabase? m_Texts;
        readonly Dictionary<string, System.Windows.Media.Imaging.BitmapSource?> m_Textures = new(StringComparer.OrdinalIgnoreCase);

        System.Windows.Media.Imaging.BitmapSource? LoadTexture(string p_Resource)
        {
            if (m_Textures.TryGetValue(p_Resource, out var s_Cached)) return s_Cached;
            System.Windows.Media.Imaging.BitmapSource? s_Bitmap = null;
            try { s_Bitmap = DdsImage.Load(m_Rime.TextureDds(p_Resource)); }
            catch (Exception s_Ex) { Log($"texture {p_Resource}: {s_Ex.Message}"); }
            m_Textures[p_Resource] = s_Bitmap;
            return s_Bitmap;
        }

        /// <summary>The renderer for the game's art, built once per language: font map + font library + text database.</summary>
        GfxRenderer Renderer()
        {
            if (m_Renderer != null) return m_Renderer;
            var s_Lang = m_Settings.Language;
            var s_FontMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var s_MapLang = s_Lang switch { "us" => "en", "ge" => "de", "jp" => "ja", _ => s_Lang };
            try
            {
                var s_Json = m_Rime.PartitionJson($"ui/static/fontcollection_{s_MapLang}_fontmapcollection");
                foreach (var s_Font in ((JObject)s_Json["Instances"]!).Properties().Select(p => p.Value).OfType<JObject>().Where(i => (string?)i["$type"] == "UIFontMappingCollection").SelectMany(i => (i["Fonts"] as JArray ?? new JArray()).OfType<JObject>()))
                    foreach (var s_Name in (s_Font["ScaleformFontName"] as JArray ?? new JArray()).Select(n => (string?)n).Where(n => n != null))
                        s_FontMap[s_Name!] = (string?)s_Font["FontLongName"] ?? "";
            }
            catch (Exception s_Ex) { Log("font map: " + s_Ex.Message); }
            try { m_Texts = TextDatabase.Load(m_Rime, s_Lang); Log($"texts: {m_Texts.Count} strings ({s_Lang})"); }
            catch (Exception s_Ex) { Log($"text database ({s_Lang}) not available: {s_Ex.Message}"); m_Texts = null; }
            m_Renderer = new GfxRenderer(WidgetMovie, LoadTexture, s_FontMap, $"ui/static/fontcollection_{s_MapLang}_fontlib", m_Texts)
            {
                // the class each widget movie registers: the renderer draws the text widgets the way their class builds them
                ClassOf = p_Resource => Settings()?.Get(p_Resource)?.Class,
            };
            return m_Renderer;
        }

        /// <summary>Draws the current screen's movie (with the document's stage ops applied) under the outlines.</summary>
        void RenderArt()
        {
            if (m_Current?.Movie == null) { m_Stage.Art = null; m_Stage.ArtGroups = null; return; }
            try
            {
                var r = Renderer();
                var s_Before = r.Warnings.Count;
                var s_Overrides = StaticTexts();
                r.TextFor = s_Overrides.Count == 0 ? null : p => TextOverride(s_Overrides, p);
                // the picture widgets: what each shows (its StaticUrl) and the picture itself — the document's PNG or the game's texture
                var s_Pictures = PictureUrls();
                r.PictureFor = s_Pictures.Count == 0 ? null : n => s_Pictures.TryGetValue(n, out var s_P) ? (PictureFor(s_P.Url), s_P.Url, s_P.Fit) : (null, "", false);
                m_Stage.Art = r.RenderMovie(m_Current.MovieName, m_Current.Movie);
                m_Stage.ArtGroups = r.PlacementGroups;
                m_Stage.InnerBounds = r.PlacementBounds;
                foreach (var w in r.Warnings.Skip(s_Before)) Log("art: " + w);
                Status($"{m_Current.Partition}: {r.ShapesDrawn} shapes, {r.BitmapsDrawn} bitmaps, {r.TextsDrawn} texts, {r.ImportsResolved} imports" + (r.ImportsMissing > 0 ? $", {r.ImportsMissing} missing" : ""));
            }
            catch (Exception s_Ex) { Log("art failed: " + s_Ex.Message); m_Stage.Art = null; }
        }

        RufflePreview.Server? m_RuffleServer;
        System.Diagnostics.Process? m_RuffleProcess;

        /// <summary>
        /// Preview in Ruffle: the open screen with the document's edits converted for a Flash player and played by Ruffle with the
        /// game's own ActionScript (see RufflePreview). Served from a local folder over 127.0.0.1; the previous player window is
        /// closed first. Without ruffle.exe the conversion still runs and the log says where the player would open.
        /// </summary>
        public string? RunRufflePreview()
        {
            if (m_Current == null || !m_Current.HasStage) { Log("Preview: open a screen with a stage first"); return null; }
            if (m_Current.IsNew) { Log("Preview: a screen made by the document is not previewed yet (its movie is the template's)"); return null; }
            try
            {
                var s_OutDir = Path.Combine(EditorSettings.DefaultPreviewRoot(), "live");
                Directory.CreateDirectory(s_OutDir);
                var s_Result = RufflePreview.Convert(m_Rime, m_Settings.Language, m_Current.MovieName, DocScreen(m_Current.Partition), s_OutDir, t => Log("[ruffle] " + t), m_Doc.Movies);
                if (s_Result.Failed > 0) { Log($"Preview: {s_Result.Failed} movie(s) failed to convert (see above)"); return null; }
                m_RuffleServer ??= new RufflePreview.Server(s_OutDir) { OnRequest = t => Dispatcher.BeginInvoke(() => Log("[http] " + t)), Resolve = r => ResolvePreviewImage(r, s_OutDir) };
                var s_Url = RufflePreview.HostUrl(m_RuffleServer);
                Log($"Preview: {s_Result.Written} movies written to {s_OutDir}; {s_Url}");
                // the player goes on the editor's monitor, wherever that is (the seams' windows stay on the secondary one)
                var s_Screen = Monitors.ForPreview(new System.Windows.Interop.WindowInteropHelper(this).Handle, Monitors.SeamsOnSecondary);
                if (m_RuffleHost == null || !m_RuffleHost.IsLoaded)
                {
                    m_RuffleHost = new RuffleHostWindow(s_Url, OnRuffleCall, Log);
                    if (IsVisible) m_RuffleHost.Owner = this;
                    m_RuffleHost.Closed += (_, _) => { m_RuffleHost = null; m_UiHost = null; };
                    // another resolution: the pictures the movie holds were served for one stage scale (1 in the fixed stage, the fit scale
                    // otherwise); when the player's scale is no longer that one the page reloads so they come back at the right size — coming
                    // back from Fit window to 720p left the icons small once
                    m_RuffleHost.OnResolutionChanged = () =>
                    {
                        if (m_RuffleHost == null) return;
                        if (Math.Abs(m_RuffleHost.StageScale - m_ServedImageScale) > 0.01) { m_TexturePngs.Clear(); m_RuffleHost.Navigate(m_RuffleHost.Url); }
                    };
                    Monitors.Place(m_RuffleHost, s_Screen);
                    m_RuffleHost.Show();
                    // the player opens at the game's design size (720p: stage scale 1, pictures at their pixel size); the toolbar changes it
                    m_RuffleHost.SetResolution(1280, 720);
                }
                else { m_RuffleHost.Navigate(s_Url); m_RuffleHost.Activate(); }
                // the engine's part for this screen: init of the widgets, the graph on every event, the data table — fed from the
                // screen's recording when one was imported (Record data… in the game, Import recording…)
                m_UiHost?.Detach();
                var s_Fixture = DataRecorder.FixturePath(EditorSettings.DefaultPreviewRoot(), m_Current.MovieName);
                var s_Recording = DataRecorder.Recording.Load(s_Fixture);
                if (s_Recording == null) Log($"Preview: no recording of this screen yet ({s_Fixture}) — widgets fed from game data stay empty; Record data… makes the mod that captures it");
                m_UiHost = new UiHost(m_Current, DocScreen(m_Current.Partition), m_WidgetCatalog, g => m_Rime.PartitionNameByGuid(g), m_RuffleHost, m_Texts, Log, s_Recording, HostServices(s_OutDir));
                var s_Host = m_UiHost;
                m_RuffleHost.OnBack = () => s_Host.FireBack();
                m_RuffleHost.OnInputConcept = (c, p) => s_Host.FireInputConcept(c, p);
                m_RuffleHost.OnSetData = (k, v) => s_Host.SetDataKey(k, v);
                m_RuffleHost.SetKeyChoices(s_Host.ReferencedKeys());
                // the profile in the toolbar: the kit and the primary weapon the generated data is built for
                m_RuffleHost.OnKitChanged = k => SetPreviewProfile(k, null);
                m_RuffleHost.OnWeaponChanged = w => SetPreviewProfile(DataGenerator.Current.Kit, w);
                m_RuffleHost.SetKitChoices(KitChoices(), DataGenerator.Current.Kit);
                m_RuffleHost.SetWeaponChoices(WeaponChoices(), DataGenerator.Current.Weapon);
                return s_Url;
            }
            catch (Exception s_Ex) { Log("Preview failed: " + s_Ex.Message); return null; }
        }

        RuffleHostWindow? m_RuffleHost;
        UiHost? m_UiHost;
        public UiHost? PreviewHost => m_UiHost;

        /// <summary>A screen or flow graph model by partition, loaded on first use (the explorer's cache is shared).</summary>
        ScreenModel? ModelFor(string p_Partition)
        {
            lock (m_Screens)
            {
                if (m_Screens.TryGetValue(p_Partition, out var s_Model)) return s_Model;
            }
            try
            {
                var s_Loaded = LoadModel(p_Partition);
                lock (m_Screens) { m_Screens[p_Partition] = s_Loaded; }
                return s_Loaded;
            }
            catch (Exception s_Ex) { Dispatcher.BeginInvoke(() => Log($"[host] {p_Partition} could not be loaded: {s_Ex.Message}")); return null; }
        }

        /// <summary>A screen's model from the game — or, for a screen the document makes, the template's partition under the new guids and the template's movie.</summary>
        ScreenModel LoadModel(string p_Partition)
        {
            var s_New = DocScreen(p_Partition)?.New == true ? DocScreen(p_Partition) : null;
            if (s_New == null) return ScreenModel.Load(m_Rime, p_Partition, WidgetMovie);
            var s_Short = s_New.Title ?? p_Partition.Split('/').Last();
            var s_Json = RimeLib.Cmd.UiBuilder.NewScreen.PartitionJson(p_Partition, s_Short, m_Rime.PartitionJson(s_New.Template));
            var s_Movie = m_Rime.ResourceBytes("ui/assets/" + s_New.Template.Split('/').Last());
            return ScreenModel.Load(m_Rime, p_Partition, WidgetMovie, s_Json, s_Movie);
        }

        /// <summary>Every flow graph of the game (UIGraphAsset partitions): the catalogue's type index when it exists, the flow folders otherwise.</summary>
        IEnumerable<string> FlowGraphPartitions()
        {
            var s_Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (m_WidgetCatalog != null)
                foreach (var kv in m_WidgetCatalog.PartitionTypes) if (kv.Value == "UIGraphAsset") s_Names.Add(kv.Key);
            if (s_Names.Count == 0)
                foreach (var s_Prefix in new[] { "ui/flow/graph/", "ui/xp5/flow/graph/" })
                    foreach (var s_Name in m_Rime.Partitions(s_Prefix)) s_Names.Add(s_Name);
            return s_Names;
        }

        /// <summary>What the preview's host needs from the editor to push other screens: models, document entries, recordings, conversion into the served folder, the flow graphs.</summary>
        UiHost.Services HostServices(string p_OutDir) => new(
            ModelFor,
            DocScreen,
            m => DataRecorder.Recording.Load(DataRecorder.FixturePath(EditorSettings.DefaultPreviewRoot(), m)),
            (m, d) => RufflePreview.Convert(m_Rime, m_Settings.Language, m, d, p_OutDir, t => Dispatcher.BeginInvoke(() => Log("[ruffle] " + t)), true, m_Doc.Movies).Failed == 0,
            p => { try { return m_Rime.PartitionJson(p); } catch { return null; } },
            FlowGraphPartitions,
            n => { try { return m_Doc.ImageByTexture(n) != null || m_Rime.HasTexture(n); } catch { return false; } },
            (c, s) => { try { var v = DataGenerator.Generate(c, s, out var h); return (v, h); } catch (Exception ex) { return (null, ex.Message); } },
            EngineAction,
            DataSetStandIn);

        /// <summary>
        /// The keys the engine's components act on when a screen's DataSetNode writes them: the KITS screen's SetKit (UIKitComp.SelectedKit =
        /// the row's index) moves the profile to that kit, the APPEARANCE screen's SetAppearance (CustSelectedAppearance = the camo's Index)
        /// picks the soldier camo, the vehicle screens' SetVehicleCategory (0 land / 1 air) tells the headers which tab is up. True when the
        /// profile changed: the host builds the generated values again.
        /// </summary>
        bool DataSetStandIn(string p_Name, object? p_Value, UiHost p_Host)
        {
            var s_Index = p_Value != null && int.TryParse(p_Value.ToString(), out var i) ? i : (int?)null;
            switch (p_Name)
            {
                case "UIKitComp.SelectedKit":
                    if (s_Index is { } s_Kit && DataGenerator.SelectKitIndex(s_Kit))
                    {
                        for (var n = 1; n <= 4; ++n) p_Host.Forget("UICustomizationComp.CustSelectedAccessory" + n);
                        m_RuffleHost?.SetKitChoices(KitChoices(), DataGenerator.Current.Kit);
                        m_RuffleHost?.SetWeaponChoices(WeaponChoices(), DataGenerator.Current.Weapon);
                        return true;
                    }
                    return false;
                case "UICustomizationComp.CustSelectedAppearance":
                    return s_Index is { } s_Camo && DataGenerator.SelectAppearance(s_Camo);
                case "UICustomizationComp.CustSelectedVehicleCategory":
                    if (DataGenerator.Current.VehicleCategory == s_Index) return false;
                    DataGenerator.Current.VehicleCategory = s_Index;
                    return true;
                case "UICustomizationComp.CustSelectedVehicle":
                    return s_Index is { } s_Vehicle && DataGenerator.SelectVehicle(s_Vehicle);
                default: return false;
            }
        }

        /// <summary>
        /// The engine's actions the preview stands in for — the kit customization ones, which turn the screen's selections (the keys its
        /// DataSetNodes wrote) into the player's profile. StorePrimaryWeaponAccessories (UpdateWeaponAccessory, StoreWeapon, RefreshWeapon and
        /// StoreWeaponAccessories in the shipped graphs all carry it) reads CustPrimaryWeapon (the primary's index in the kit's row) and
        /// CustSelectedAccessory1..4 (the item picked in each accessory row). True when the profile changed: the host then builds the
        /// generated values again and the widgets refresh.
        /// </summary>
        bool EngineAction(string p_Name, UiHost p_Host)
        {
            static int? Index(object? v) => v != null && int.TryParse(v.ToString(), out var i) ? i : null;
            switch (p_Name)
            {
                case "StorePrimaryWeaponAccessories":
                {
                    if (Index(p_Host.Value("UICustomizationComp.CustPrimaryWeapon")) is { } s_WeaponIndex && DataGenerator.SelectInRow("ID_M_SOLDIER_PRIMARY", s_WeaponIndex))
                    {
                        // a new primary: the accessory indices held so far were the old weapon's rows
                        for (var i = 1; i <= 4; ++i) p_Host.Forget("UICustomizationComp.CustSelectedAccessory" + i);
                        m_RuffleHost?.SetWeaponChoices(WeaponChoices(), DataGenerator.Current.Weapon);
                        return true;
                    }
                    var s_Changed = false;
                    for (var i = 1; i <= 4; ++i)
                        if (Index(p_Host.Value("UICustomizationComp.CustSelectedAccessory" + i)) is { } s_Index && DataGenerator.SelectAccessory(i - 1, s_Index)) s_Changed = true;
                    return s_Changed;
                }
                case "SetWeaponCustomization":
                {
                    // the loadout screen (UpdateSoldierLoadout): a pick in a row writes the row's category (CustSelectedWeaponCategory) and the
                    // item's index in the row's own key; a new primary weapon starts its accessories from "none"
                    var s_Changed = false;
                    foreach (var (s_Key, s_Category) in new[] { ("CustPrimaryWeapon", "ID_M_SOLDIER_PRIMARY"), ("CustSecondaryWeapon", "ID_M_SOLDIER_SECONDARY"), ("CustGadgetOne", "ID_M_SOLDIER_GADGET1"), ("CustGadgetTwo", "ID_M_SOLDIER_GADGET2"), ("CustSpecialization", "ID_M_SOLDIER_SPECIALIZATION") })
                        if (Index(p_Host.Value("UICustomizationComp." + s_Key)) is { } s_Index && DataGenerator.SelectInRow(s_Category, s_Index))
                        {
                            s_Changed = true;
                            if (s_Category == "ID_M_SOLDIER_PRIMARY")
                            {
                                for (var i = 1; i <= 4; ++i) p_Host.Forget("UICustomizationComp.CustSelectedAccessory" + i);
                                m_RuffleHost?.SetWeaponChoices(WeaponChoices(), DataGenerator.Current.Weapon);
                            }
                        }
                    return s_Changed;
                }
                default: return false;
            }
        }

        UiDataGenerator? m_Generator;
        /// <summary>The stand-in for the engine's data components: values built from the game's data for the preview's profile.</summary>
        public UiDataGenerator DataGenerator => m_Generator ??= new UiDataGenerator(m_Rime, t => Dispatcher.BeginInvoke(() => Log(t)));

        /// <summary>The player's profile changed in the toolbar: the generator builds for the new kit/weapon and the host builds every generated value again.</summary>
        public void SetPreviewProfile(string p_Kit, string? p_Weapon)
        {
            DataGenerator.Current = new UiDataGenerator.Profile { Kit = p_Kit, Weapon = p_Weapon };
            var s_Count = m_UiHost?.Regenerate($"kit {p_Kit.Split('/').Last()}{(p_Weapon != null ? ", weapon " + p_Weapon.Split('/').Last() : "")}") ?? 0;
            m_RuffleHost?.SetWeaponChoices(WeaponChoices(), p_Weapon);
            Log($"profile: {p_Kit.Split('/').Last()}{(p_Weapon != null ? " / " + p_Weapon.Split('/').Last() : "")} — {s_Count} generated value(s) built again");
        }

        /// <summary>The kits the player's toolbar offers: partition → "name — localised label".</summary>
        IEnumerable<(string Key, string Label)> KitChoices() =>
            DataGenerator.Kits().Select(k => (k.Partition, $"{k.Partition.Split('/').Last()} — {m_Texts?.Lookup(k.LabelSid) ?? k.LabelSid}"));

        /// <summary>The primary weapons of the profile's kit: unlock → localised name.</summary>
        IEnumerable<(string Key, string Label)> WeaponChoices() =>
            DataGenerator.PrimaryWeapons().Select(w => (w.Unlock, m_Texts?.Lookup(w.NameSid) ?? w.NameSid));

        readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]?> m_TexturePngs = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The stage scale the pictures were last served for: the movie holds them at that size until the page reloads.</summary>
        double m_ServedImageScale = 1.0;

        /// <summary>
        /// A texture the movies ask for by path ("./img/UI/Art/…" after the redirect), as a PNG: the game's DDS from the cache (or the mounted
        /// game), decoded and re-encoded once; kept under the preview folder so the next request is a plain file. Null when the texture is not to be had.
        /// </summary>
        (byte[] Bytes, string ContentType)? ResolvePreviewImage(string p_Relative, string p_OutDir)
        {
            const string s_Prefix = "ui/assets/img/";
            if (!p_Relative.StartsWith(s_Prefix, StringComparison.OrdinalIgnoreCase)) return null;
            var s_Name = p_Relative[s_Prefix.Length..].ToLowerInvariant().TrimEnd('/');
            if (s_Name.EndsWith(".png", StringComparison.Ordinal)) s_Name = s_Name[..^4];
            if (s_Name == "") return null;
            // the game keeps its whole 1280x720 UI at its pixel size on any resolution (the fixed stage: scale 1, pictures 1:1); in the
            // scaled 'fit' mode the picture is shrunk by the stage scale so it still spans its own pixels on screen
            var s_Scale = Math.Round(m_RuffleHost?.StageScale ?? 1.0, 2);
            if (s_Scale < 0.05) s_Scale = 1.0;
            m_ServedImageScale = s_Scale;
            var s_Png = m_TexturePngs.GetOrAdd(s_Name + "@" + s_Scale.ToString(System.Globalization.CultureInfo.InvariantCulture), n =>
            {
                try
                {
                    // a document picture is served from its PNG (the pixels the build ships); anything else is the game's texture
                    System.Windows.Media.Imaging.BitmapSource? s_Bitmap = m_Doc.ImageByTexture(s_Name) != null ? PictureFor(s_Name) : DdsImage.Load(m_Rime.TextureDds(s_Name));
                    if (s_Bitmap == null) return null;
                    if (Math.Abs(s_Scale - 1.0) > 0.01)
                    {
                        s_Bitmap = new System.Windows.Media.Imaging.TransformedBitmap(s_Bitmap, new System.Windows.Media.ScaleTransform(1.0 / s_Scale, 1.0 / s_Scale));
                        s_Bitmap.Freeze();
                    }
                    var s_Encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    s_Encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(s_Bitmap));
                    using var s_Stream = new MemoryStream();
                    s_Encoder.Save(s_Stream);
                    return s_Stream.ToArray();
                }
                catch { return null; }
            });
            return s_Png == null ? null : (s_Png, "image/png");
        }

        /// <summary>The same preview in the desktop player (ruffle.exe from Settings) — no bridge, for a machine without WebView2.</summary>
        public string? RunRufflePreviewDesktop()
        {
            if (m_Current == null || !m_Current.HasStage || m_Current.IsNew) { Log("Preview: open a shipped screen with a stage first"); return null; }
            try
            {
                var s_OutDir = Path.Combine(EditorSettings.DefaultPreviewRoot(), "live");
                var s_Result = RufflePreview.Convert(m_Rime, m_Settings.Language, m_Current.MovieName, DocScreen(m_Current.Partition), s_OutDir, t => Log("[ruffle] " + t), m_Doc.Movies);
                m_RuffleServer ??= new RufflePreview.Server(s_OutDir) { Resolve = r => ResolvePreviewImage(r, s_OutDir) };
                var s_Url = m_RuffleServer.Url + s_Result.ShellPath;
                try { if (m_RuffleProcess != null && !m_RuffleProcess.HasExited) m_RuffleProcess.Kill(); } catch { }
                var s_Screen = Monitors.ForPreview(new System.Windows.Interop.WindowInteropHelper(this).Handle, Monitors.SeamsOnSecondary);
                m_RuffleProcess = RufflePreview.Launch(m_Settings.RufflePath, s_Url, s_Screen, Log);
                return s_Url;
            }
            catch (Exception s_Ex) { Log("Preview in Ruffle (desktop) failed: " + s_Ex.Message); return null; }
        }

        /// <summary>
        /// A call the game's ActionScript made on an engine component, arriving from the preview: logged with what the editor
        /// knows about it — a fired widget event is named through the widget's contract (UIWidgetAsset.WidgetEvents).
        /// </summary>
        JToken? OnRuffleCall(string p_Comp, string p_Method, JArray p_Args)
        {
            if (m_UiHost != null) { m_UiHost.HandleCall(p_Comp, p_Method, p_Args); return null; }
            var s_Args = string.Join(", ", p_Args.Select(a => a.Type == JTokenType.String ? "\"" + a + "\"" : a.ToString(Newtonsoft.Json.Formatting.None)));
            if (p_Comp == "UIWidgetEventComp" && p_Method == "fireEvent" && p_Args.Count >= 2)
            {
                var s_Path = (string?)p_Args[0] ?? ""; var s_Instance = s_Path.Split('.').Last();
                // the widget: a shipped node of the screen or one the document added
                var s_Shipped = m_Current?.AllNodes.FirstOrDefault(n => n.IsWidget && n.Label == s_Instance);
                var s_DocNode = m_Current != null ? DocScreen(m_Current.Partition)?.Nodes.FirstOrDefault(n => n.InstanceName == s_Instance) : null;
                var s_Asset = s_Shipped != null ? m_Current!.Widgets.FirstOrDefault(w => w.InstanceName == s_Instance)?.WidgetPartition : s_DocNode?.Widget;
                var s_Info = s_Asset != null ? m_WidgetCatalog?.Get(s_Asset) : null;
                var s_Event = p_Args[1].Type == JTokenType.Integer || p_Args[1].Type == JTokenType.Float ? (int)p_Args[1] : -1;
                var s_Outputs = s_Info?.Events.Where(ev => ev.IsOutput).ToList();
                var s_Name = s_Outputs != null && s_Event >= 0 && s_Event < s_Outputs.Count ? s_Outputs[s_Event].Query : p_Args[1].ToString();
                Log($"[game] {s_Instance} fired {s_Name}" + (p_Args.Count > 2 && p_Args[2].Type != JTokenType.Null ? $" with {p_Args[2].ToString(Newtonsoft.Json.Formatting.None)}" : "") + (s_Shipped == null && s_DocNode == null ? $"  (no node named {s_Instance} on this screen)" : ""));
                return null;
            }
            if (p_Comp == "UIDataInterfaceComp" && p_Args.Count >= 1)
            {
                // a data key: named through the components' key tables (UI_<COMP>_<SOURCE> hashed)
                var s_Key = p_Args[0].ToString();
                var s_Named = m_WidgetCatalog?.Components.Values.SelectMany(c => c.Keys.Where(k => k.Value.ToString() == s_Key).Select(k => c.Name.Split('/').Last() + "." + k.Key)).FirstOrDefault();
                Log($"[game] {p_Comp}.{p_Method}({s_Args})" + (s_Named != null ? $"  = {s_Named}" : "  (key not in any component)"));
                return null;
            }
            Log($"[game] {p_Comp}.{p_Method}({s_Args})");
            return null;
        }

        void OnRufflePreview(object p_Sender, RoutedEventArgs e) => RunRufflePreview();

        /// <summary>
        /// Record data…: the RimeUiRecorder mod for the open screen and every other shipped screen of the document — their shipped
        /// movies with the recorder in front of their frame, the receiver, the superbundle; one mod records the whole walk through
        /// those screens. Installing it is the user's step (ModList line, server restart); the Output says so, and warns that any
        /// other mod shipping one of the screens must be off while recording (one movie per resource name reaches the game).
        /// </summary>
        async void OnRecordData(object p_Sender, RoutedEventArgs e)
        {
            if (m_Current == null || !m_Current.HasStage || m_Current.IsNew) { Log("Record data: open a shipped screen with a stage first"); return; }
            if (!m_Rime.IsMounted && !await EnsureMounted()) return;
            RecordButton.IsEnabled = false;
            Status("writing the recorder mod…");
            var s_Movies = RecorderScreens();
            var s_Log = new StringWriter();
            try
            {
                var s_Result = await Task.Run(() => BuildRecorderMod(s_Movies, s_Log, true));
                Log(s_Log.ToString());
                Log($"RECORDER MOD WRITTEN: {s_Result.ModDir} — for {string.Join(", ", s_Movies.Select(m => m.Split('/').Last()))}");
                Log($"  to record: a line '{DataRecorder.ModName}' (no '#') in the server's ModList.txt; put a '#' in front of any mod that ships one of these screens (a UI mod built from this document, for one) — one movie per screen reaches the game; restart the server");
                Log($"  in the game: open the screens; the client console prints [UIREC] armed…, then [AS2] #R… lines and '[UIREC] record N.i0 complete' per screen — hover and click what you want recorded too (each refresh is a record); press F5 when done (the records go to the mod's mod.db)");
                Log($"  then: Import recording… (the console copied, a text file, or the lines uirec_db.py makes from mod.db); each screen's data lands under {Path.Combine(EditorSettings.DefaultPreviewRoot(), "data")} and Preview in Ruffle uses it");
                Status("recorder mod written");
            }
            catch (Exception s_Ex) { Log(s_Log + "\nRECORDER MOD FAILED: " + s_Ex.Message); Status("recorder mod failed"); }
            RecordButton.IsEnabled = true;
        }

        /// <summary>The recorder as a passenger of the document's own build: every shipped screen of the document (its movie ships with the recorder in front).</summary>
        ModEmitter.Recorder RecorderParts() => DataRecorder.Parts(m_Doc.Name, m_Doc.Screens.Where(s => s.Movie != null || s.New).Select(s => (s.Movie ?? "ui/assets/" + s.Partition.Split('/').Last()).Split('/').Last()));   // the screens with a movie (a flow graph has none; a new screen's is the document's)

        /// <summary>The screens Record data… ships: the open one first, then the document's other shipped screens that have a movie.</summary>
        List<string> RecorderScreens()
        {
            var s_Movies = new List<string>();
            if (m_Current != null) s_Movies.Add(m_Current.MovieName);
            foreach (var s_Screen in m_Doc.Screens.Where(s => !s.New))
            {
                var s_Movie = !string.IsNullOrWhiteSpace(s_Screen.Movie) ? s_Screen.Movie : "ui/assets/" + s_Screen.Partition.Split('/').Last();
                if (!s_Movies.Contains(s_Movie, StringComparer.OrdinalIgnoreCase) && m_Rime.HasResource(s_Movie)) s_Movies.Add(s_Movie);
            }
            return s_Movies;
        }

        /// <summary>The recorder mod for a screen movie, into the Mods folder of the settings; p_Build needs the game mounted.</summary>
        public DataRecorder.ModResult BuildRecorderMod(string p_MovieResource, TextWriter p_Log, bool p_Build) => BuildRecorderMod(new[] { p_MovieResource }, p_Log, p_Build);

        /// <summary>The recorder mod for several screen movies at once (one walk, one database).</summary>
        public DataRecorder.ModResult BuildRecorderMod(IReadOnlyList<string> p_MovieResources, TextWriter p_Log, bool p_Build)
        {
            Directory.CreateDirectory(m_Settings.ModsPath);
            return DataRecorder.BuildMod(m_Rime, p_MovieResources, m_Settings.ModsPath, EditorSettings.DefaultBuildRoot(), p_Log, p_Build);
        }

        /// <summary>Import recording…: the clipboard when it holds the recorder's lines, else a text file the user picks.</summary>
        void OnImportRecording(object p_Sender, RoutedEventArgs e)
        {
            string s_Text = "";
            try { if (Clipboard.ContainsText()) s_Text = Clipboard.GetText(); } catch { }
            if (!s_Text.Contains("#R") && !s_Text.Contains("[UIREC]"))
            {
                if (EditorSettings.Headless) { Log("Import recording: nothing of the recorder's in the clipboard"); return; }
                var s_Dialog = new Microsoft.Win32.OpenFileDialog { Title = "Import recording: a copied client console", Filter = "Text (*.txt;*.log)|*.txt;*.log|All files (*.*)|*.*" };
                if (s_Dialog.ShowDialog(this) != true) return;
                try { s_Text = File.ReadAllText(s_Dialog.FileName); }
                catch (Exception s_Ex) { Log("Import recording: " + s_Ex.Message); return; }
            }
            ImportRecordingText(s_Text);
        }

        /// <summary>The recorder's lines in any text → one fixture per screen recorded, under the preview root; the next Preview in Ruffle uses them. Returns the screen recorded last.</summary>
        public DataRecorder.Recording? ImportRecordingText(string p_Text)
        {
            var s_Report = new DataRecorder.ImportReport();
            var s_All = DataRecorder.ImportAll(p_Text, EditorSettings.DefaultPreviewRoot(), s_Report);
            if (s_All.Count == 0) { Log($"Import recording: nothing complete found — {s_Report}"); return null; }
            foreach (var (s_Rec, s_Path) in s_All)
            {
                Log($"RECORDING IMPORTED: {s_Rec.Screen} → {s_Path}");
                Log($"  {s_Rec.Summary}");
            }
            Log($"  {s_Report}");
            Log(m_RuffleHost != null && m_RuffleHost.IsLoaded ? "  press Preview in Ruffle again to play the screen with this data" : "  Preview in Ruffle plays the screen with this data");
            return s_All[0].Recording;
        }


        /// <summary>
        /// Texts the screen's bindings fix statically, mapped to the clip that shows them (from the widget's
        /// ActionScript: PageHeader.updateHeaderData → heading, updateSubHeaderData → subHeading). Runtime-built
        /// widgets (TextField rows, button bars, lists) cannot be placed statically and stay as authored.
        /// </summary>
        Dictionary<string, string> StaticTexts() => m_Current == null ? new Dictionary<string, string>() : StaticTexts(m_Current, m_Texts, DocScreen(m_Current.Partition));

        /// <summary>
        /// The texts the bindings fix statically, shipped and from the document (an inline binding on a document node), keyed
        /// by the clip they land in: a page header's StaticHeader/StaticSubHeader → its heading/subHeading clips; a
        /// UITextDataBinding's StaticText → every text field inside the widget ("&lt;widget&gt;/*": TextField shows it in
        /// its txtDisplay, whatever row type it attaches at runtime).
        /// </summary>
        public static Dictionary<string, string> StaticTexts(ScreenModel p_Screen, TextDatabase? p_Texts, ScreenEntry? p_Doc = null)
        {
            var d = new Dictionary<string, string>();
            string Resolve(JToken? t) { var s = (string?)t ?? ""; return p_Texts?.Resolve(s) ?? s; }
            void Take(string p_Label, JObject b)
            {
                switch ((string?)b["$type"])
                {
                    case "UIPageHeaderBinding":
                        if (!string.IsNullOrEmpty((string?)b["StaticHeader"])) d[p_Label + "/heading"] = Resolve(b["StaticHeader"]);
                        if (!string.IsNullOrEmpty((string?)b["StaticSubHeader"])) d[p_Label + "/subHeading"] = Resolve(b["StaticSubHeader"]);
                        break;
                    case "UITextDataBinding":
                        if (!string.IsNullOrEmpty((string?)b["StaticText"])) d[p_Label + "/*"] = Resolve(b["StaticText"]);
                        break;
                }
            }
            foreach (var w in p_Screen.AllNodes.Where(n => n.IsWidget))
            {
                // a null DataBinding is a JValue, and indexing a JValue throws
                var s_BindingGuid = w.Json["DataBinding"] is JObject s_Ref ? (string?)s_Ref["InstanceGuid"] : null;
                if (s_BindingGuid == null || p_Screen.Instances[s_BindingGuid] is not JObject b) continue;
                Take(w.Label, b);
            }
            // the document's word wins: an inline binding on a new or edited node
            if (p_Doc != null)
                foreach (var n in p_Doc.Nodes)
                    if (n.Fields.TryGetValue("DataBinding", out var s_Inline) && s_Inline is JObject s_Obj && s_Obj["$type"] != null) Take(n.InstanceName, s_Obj);
            return d;
        }

        /// <summary>The override for a placement path: keys are "&lt;widget instance&gt;/&lt;clip&gt;" (or "&lt;widget instance&gt;/*" for any text inside it), paths carry the screen's root sprite in front.</summary>
        public static string? TextOverride(Dictionary<string, string> p_Overrides, string p_Path)
        {
            if (p_Overrides.TryGetValue(p_Path, out var t)) return t;
            foreach (var kv in p_Overrides)
            {
                if (kv.Key.EndsWith("/*", StringComparison.Ordinal)) { if (p_Path.Contains("/" + kv.Key[..^2] + "/", StringComparison.Ordinal)) return kv.Value; }
                else if (p_Path.EndsWith("/" + kv.Key, StringComparison.Ordinal)) return kv.Value;
            }
            return null;
        }

        void OnShowArt(object p_Sender, RoutedEventArgs e) { if (m_Stage != null) m_Stage.ShowArt = ShowArtBox.IsChecked == true; }
        void OnShowOutlines(object p_Sender, RoutedEventArgs e) { if (m_Stage != null) m_Stage.ShowOutlines = ShowOutlinesBox.IsChecked == true; }
        void OnShowInner(object p_Sender, RoutedEventArgs e) { if (m_Stage != null) m_Stage.ShowInnerOutlines = ShowInnerBox.IsChecked == true; }
        void OnShowRotation(object p_Sender, RoutedEventArgs e) { if (m_Stage != null) m_Stage.ShowRotation = ShowRotationBox.IsChecked == true; }
        void OnAlphabetical(object p_Sender, RoutedEventArgs e) { if (PropertiesGrid != null) { PropertiesGrid.Alphabetical = AlphabeticalBox.IsChecked == true; ShowProperties(); } }
        void OnSaveCommand(object p_Sender, ExecutedRoutedEventArgs e) => OnSaveDocument(p_Sender, e);

        // ------------------------------------------------------------------------------------------ mount

        void ShowPaths()
        {
            PathsText.Text = $"game: {(m_Settings.GamePath == "" ? "(not set)" : m_Settings.GamePath)}   mods: {m_Settings.ModsPath}";
        }

        void OnOpenSettings(object p_Sender, RoutedEventArgs e)
        {
            new SettingsWindow(this, m_Settings, ApplySettings).ShowDialog();
        }

        async void OnMount(object p_Sender, RoutedEventArgs e) => await EnsureMounted();

        void OnPaletteFilter(object p_Sender, TextChangedEventArgs e)
        {
            var f = PaletteFilter.Text.Trim();
            PaletteList.ItemsSource = m_AllWidgets.Where(s => f == "" || s.Contains(f, StringComparison.OrdinalIgnoreCase)).Select(s => s.Replace("ui/assets/", "")).ToList();
        }

        // ------------------------------------------------------------------------------------------ screen

        /// <summary>Rebuilds the current screen's movie from vanilla + the document's stage ops, then redraws.</summary>
        void ApplyDocumentToCurrent()
        {
            if (m_Current == null)
            {
                // no screen on the stage: say so where the user looks (the stage, the Layers) instead of leaving blanks
                EmptyHint.Visibility = Visibility.Visible;
                LayerList.ItemsSource = new List<object> { new TextBlock { Text = "(no screen open — click one in the Data Explorer)", Foreground = Brushes.Gray, FontStyle = FontStyles.Italic } };
                ScreenTitle.Text = ""; GraphTitle.Text = "";
                Status("no screen open");
                ShowProperties();
                return;
            }
            EmptyHint.Visibility = Visibility.Collapsed;
            var s_Entry = DocScreen(m_Current.Partition);
            // a screen the document made whose entry is gone (undone, or another document loaded) has nothing to show
            if (m_Current.IsNew && s_Entry == null) { CloseScreen(m_Current.Partition); return; }
            var s_Added = new HashSet<string>();
            if (m_Current.HasStage)
            {
                m_Current.Movie = GfxMovie.Load(m_Current.VanillaMovie);
                if (s_Entry != null)
                {
                    foreach (var s_Op in s_Entry.Stage)
                    {
                        try { ApplyOp(m_Current.Movie, s_Op); }
                        catch (Exception s_Ex) { Log($"stage op '{s_Op}' failed: {s_Ex.Message}"); }
                        if (s_Op.StartsWith("add:")) s_Added.Add(s_Op.Split(':')[2]);
                    }
                }
                m_Current.RefreshPlacements(WidgetMovie);
                foreach (var p in m_Current.Placements) p.Added = s_Added.Contains(p.Name);
            }
            RenderArt();
            m_Stage.SetPlacements(m_Current.Placements);
            ScreenTitle.Text = m_Current.Partition + (m_Current.HasStage ? "" : "   (flow graph — no stage)") + (s_Entry != null ? "   [in document]" : "");
            ApplyLayerToggles();
            LayerList.ItemsSource = m_Current.Placements.OrderByDescending(p => p.Depth).Select(LayerItem).ToList();
            SyncLayerSelection();
            RefreshGraph();
            ShowProperties();
            RefreshTabs();
        }

        static void ApplyOp(GfxMovie p_Movie, string p_Op)
        {
            var f = p_Op.Split(':');
            var inv = CultureInfo.InvariantCulture;
            switch (f[0])
            {
                case "move": p_Movie.Move(f[1], double.Parse(f[2], inv), double.Parse(f[3], inv)); break;
                case "scale": p_Movie.Scale(f[1], double.Parse(f[2], inv), double.Parse(f[3], inv)); break;
                case "rotate": p_Movie.Rotate(f[1], double.Parse(f[2], inv)); break;
                case "char": p_Movie.SetCharacter(f[1], ushort.Parse(f[2], inv)); break;
                case "depth": p_Movie.SetDepth(f[1], ushort.Parse(f[2], inv)); break;   // the drawing order: higher depth = in front
                case "remove": p_Movie.Remove(f[1]); break;
                case "vars": p_Movie.SetClipVars(f[1], GfxMovie.ParseClipVars(p_Op[(p_Op.IndexOf(':', p_Op.IndexOf(':') + 1) + 1)..])); break;
                case "import": p_Movie.AddImport(f[1], ushort.Parse(f[2], inv), f[3]); break;
                case "add":
                    p_Movie.AddPlacement(ushort.Parse(f[1], inv), f[2], ushort.Parse(f[3], inv), ushort.Parse(f[4], inv),
                        double.Parse(f[5], inv), double.Parse(f[6], inv), double.Parse(f[7], inv), double.Parse(f[8], inv));
                    break;
                case "clone": ModEmitter.ApplyOp(p_Movie, p_Op); break;    // the same widget placed again beside the original: the emitter's rule
                case "script": ModEmitter.ApplyOp(p_Movie, p_Op); break;   // a frame action on the screen's timeline: the emitter's rule, shared with the mod build
                case "textsize": ModEmitter.ApplyOp(p_Movie, p_Op); break; // a text widget's size in px: the emitter's rule (construct variable + frame action)
                default: throw new Exception("unknown op " + f[0]);
            }
        }

        // layer toggles, per screen and placement (view state, not part of the document)
        readonly HashSet<string> m_HiddenLayers = new(), m_LockedLayers = new();
        string LayerKey(string p_Name) => (m_Current?.Partition ?? "") + "|" + p_Name;

        void ApplyLayerToggles()
        {
            m_Stage.Hidden.Clear(); m_Stage.Locked.Clear();
            if (m_Current == null) return;
            foreach (var p in m_Current.Placements)
            {
                if (m_HiddenLayers.Contains(LayerKey(p.Name))) m_Stage.Hidden.Add(p.Name);
                if (m_LockedLayers.Contains(LayerKey(p.Name))) m_Stage.Locked.Add(p.Name);
            }
        }

        /// <summary>One layers-list row: the eye (drawn or not), the lock (selectable or not), the placement.</summary>
        FrameworkElement LayerItem(PlacementInfo p)
        {
            var s_Panel = new StackPanel { Orientation = Orientation.Horizontal, Tag = p.Name };
            var s_Eye = new CheckBox { IsChecked = !m_HiddenLayers.Contains(LayerKey(p.Name)), ToolTip = "drawn on the stage (view only, not part of the mod)", Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            s_Eye.Checked += (_, _) => { m_HiddenLayers.Remove(LayerKey(p.Name)); ApplyLayerToggles(); m_Stage.Redraw(); };
            s_Eye.Unchecked += (_, _) => { m_HiddenLayers.Add(LayerKey(p.Name)); ApplyLayerToggles(); m_Stage.Redraw(); };
            var s_Lock = new CheckBox { IsChecked = m_LockedLayers.Contains(LayerKey(p.Name)), ToolTip = "locked: cannot be picked or dragged on the stage", Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Content = "⚿", FontSize = 9 };
            s_Lock.Checked += (_, _) => { m_LockedLayers.Add(LayerKey(p.Name)); ApplyLayerToggles(); };
            s_Lock.Unchecked += (_, _) => { m_LockedLayers.Remove(LayerKey(p.Name)); ApplyLayerToggles(); };
            s_Panel.Children.Add(s_Eye);
            s_Panel.Children.Add(s_Lock);
            s_Panel.Children.Add(new TextBlock { Text = $"{p.Name}   ({p.ImportUrl ?? "local"} #{p.CharacterId}, depth {p.Depth})" + (p.Added ? "  +" : ""), VerticalAlignment = VerticalAlignment.Center, Foreground = p.Added ? Brushes.LightGreen : Brushes.Gainsboro });
            return s_Panel;
        }

        void OnLayerSelected(object p_Sender, SelectionChangedEventArgs e)
        {
            if (m_Current == null || LayerList.SelectedIndex < 0) return;
            var s_Name = m_Current.Placements.OrderByDescending(p => p.Depth).ElementAt(LayerList.SelectedIndex).Name;
            if (s_Name == m_SelectedPlacement) return;
            m_SelectedPlacement = s_Name;
            m_SelectedNode = s_Name; m_SelectedKey = null;
            m_Stage.Selected = s_Name;
            m_Graph.Select(s_Name);
            ShowProperties();
        }

        void SyncLayerSelection()
        {
            if (m_Current == null) return;
            var s_Index = m_Current.Placements.OrderByDescending(p => p.Depth).ToList().FindIndex(p => p.Name == m_SelectedPlacement);
            if (LayerList.SelectedIndex != s_Index) LayerList.SelectedIndex = s_Index;
        }

        /// <summary>A drag on the stage becomes a stage op in the document: the add op's own x/y, or a move op.</summary>
        void OnPlacementMoved(string p_Name, double p_X, double p_Y)
        {
            if (m_Current == null) return;
            PushUndo();
            WriteMove(EnsureDocScreen(m_Current.Partition), p_Name, p_X, p_Y);
            Log($"stage: {p_Name} -> local ({StageCanvas.F(p_X)},{StageCanvas.F(p_Y)})");
            try { ApplyDocumentToCurrent(); }
            catch (Exception s_Ex) { Log("apply failed: " + s_Ex.Message); }
        }

        /// <summary>Several selected placements dragged together: one undo step, one op each, the selection kept.</summary>
        void OnPlacementsMoved(IReadOnlyList<(string Name, double X, double Y)> p_Moves)
        {
            if (m_Current == null || p_Moves.Count == 0) return;
            PushUndo();
            var s_Entry = EnsureDocScreen(m_Current.Partition);
            foreach (var (s_Name, s_X, s_Y) in p_Moves) WriteMove(s_Entry, s_Name, s_X, s_Y);
            Log($"stage: {p_Moves.Count} placements moved together ({string.Join(", ", p_Moves.Select(m => m.Name))})");
            var s_Keep = m_Stage.SelectedSet.ToList(); var s_Primary = m_Stage.Selected;
            try { ApplyDocumentToCurrent(); }
            catch (Exception s_Ex) { Log("apply failed: " + s_Ex.Message); }
            m_Stage.Selected = s_Primary;
            foreach (var n in s_Keep.Where(n => n != s_Primary)) m_Stage.SelectToggle(n);
        }

        static void WriteMove(ScreenEntry p_Entry, string p_Name, double p_X, double p_Y)
        {
            var s_AddIndex = p_Entry.Stage.FindIndex(o => o.StartsWith("add:") && o.Split(':')[2] == p_Name);
            if (s_AddIndex >= 0)
            {
                var f = p_Entry.Stage[s_AddIndex].Split(':');
                f[5] = StageCanvas.F(p_X); f[6] = StageCanvas.F(p_Y);
                p_Entry.Stage[s_AddIndex] = string.Join(":", f);
            }
            else
            {
                p_Entry.Stage.RemoveAll(o => o.StartsWith("move:" + p_Name + ":"));
                p_Entry.Stage.Add($"move:{p_Name}:{StageCanvas.F(p_X)}:{StageCanvas.F(p_Y)}");
            }
        }

        /// <summary>A handle drag on the stage: the new scale and, since the opposite edge stayed put, the new origin — one undo step (the add op's own fields, or a move + a scale op).</summary>
        void OnPlacementResized(string p_Name, double p_X, double p_Y, double p_Sx, double p_Sy)
        {
            if (m_Current == null) return;
            PushUndo();
            var s_Entry = EnsureDocScreen(m_Current.Partition);
            var s_AddIndex = s_Entry.Stage.FindIndex(o => o.StartsWith("add:") && o.Split(':')[2] == p_Name);
            if (s_AddIndex >= 0)
            {
                var f = s_Entry.Stage[s_AddIndex].Split(':');
                f[5] = StageCanvas.F(p_X); f[6] = StageCanvas.F(p_Y); f[7] = StageCanvas.F(p_Sx); f[8] = StageCanvas.F(p_Sy);
                s_Entry.Stage[s_AddIndex] = string.Join(":", f);
            }
            else
            {
                s_Entry.Stage.RemoveAll(o => o.StartsWith("move:" + p_Name + ":") || o.StartsWith("scale:" + p_Name + ":"));
                s_Entry.Stage.Add($"move:{p_Name}:{StageCanvas.F(p_X)}:{StageCanvas.F(p_Y)}");
                s_Entry.Stage.Add($"scale:{p_Name}:{StageCanvas.F(p_Sx)}:{StageCanvas.F(p_Sy)}");
            }
            Log($"stage: {p_Name} resized to ×{StageCanvas.F(p_Sx)} / ×{StageCanvas.F(p_Sy)} at local ({StageCanvas.F(p_X)},{StageCanvas.F(p_Y)})");
            try { ApplyDocumentToCurrent(); }
            catch (Exception s_Ex) { Log("apply failed: " + s_Ex.Message); }
            m_Stage.Selected = p_Name;
        }

        /// <summary>The rotation knob or the Rotation row: a rotate op (kept even at 0° for a shipped placement, whose own angle it overrides; dropped at 0° for one the document added).</summary>
        void OnPlacementRotated(string p_Name, double p_Degrees)
        {
            if (m_Current == null) return;
            PushUndo();
            var s_Entry = EnsureDocScreen(m_Current.Partition);
            s_Entry.Stage.RemoveAll(o => o.StartsWith("rotate:" + p_Name + ":"));
            var s_Added = s_Entry.Stage.Any(o => o.StartsWith("add:") && o.Split(':')[2] == p_Name);
            if (!s_Added || System.Math.Abs(p_Degrees) > 0.005) s_Entry.Stage.Add($"rotate:{p_Name}:{StageCanvas.F(p_Degrees)}");
            Log($"stage: {p_Name} rotated to {StageCanvas.F(p_Degrees)}°");
            try { ApplyDocumentToCurrent(); }
            catch (Exception s_Ex) { Log("apply failed: " + s_Ex.Message); }
            m_Stage.Selected = p_Name;
        }

        // ------------------------------------------------------------------------------------------ properties

        IEnumerable<string> WiringOf(NodeInfo p_Node)
        {
            var s_Removed = new HashSet<string>(DocScreen(m_Current!.Partition)?.RemovedConnections.Select(r => r.Guid) ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            return m_Current!.Wires.Where(w => w.FromGuid == p_Node.Guid || w.ToGuid == p_Node.Guid)
                .Select(w => $"{w.From}.{w.FromPort} -> {w.To}.{w.ToPort}" + (w.Dangling ? " (dangling)" : "") + (w.Guid != "" && s_Removed.Contains(w.Guid) ? " (disconnected by the document)" : ""));
        }

        static string Compact(JToken v) => v.Type == JTokenType.String ? "\"" + (string)v! + "\"" : v.ToString(Formatting.None);

        /// <summary>A sensible default for a field of the game's types (the editor's "fill everything" helper).</summary>
        static JToken DefaultValue(UiTypeCatalog.FieldInfo f)
        {
            switch (f.Kind)
            {
                case UiTypeCatalog.Kind.Bool: return false;
                case UiTypeCatalog.Kind.Int: return 0;
                case UiTypeCatalog.Kind.Float: return 0.0;
                case UiTypeCatalog.Kind.String: return "";
                case UiTypeCatalog.Kind.Enum: return UiTypeCatalog.EnumMembers(f.TypeName).FirstOrDefault() ?? "";
                case UiTypeCatalog.Kind.Ref: return "";
                case UiTypeCatalog.Kind.List: return new JArray();
                case UiTypeCatalog.Kind.Struct:
                {
                    var o = new JObject();
                    var s = UiTypeCatalog.Describe(f.TypeName);
                    if (s != null) foreach (var sf in s.Fields.Where(x => x.Kind is not (UiTypeCatalog.Kind.Port or UiTypeCatalog.Kind.PortArray))) o[sf.Name] = DefaultValue(sf);
                    return o;
                }
                default: return "";
            }
        }

        /// <summary>Widget events by name, everything else by port: what the document's connection needs.</summary>
        ConnectionEntry MakeConnection(NodeEntry p_From, string p_FromPort, string p_ToNode, string p_ToPort)
        {
            var c = new ConnectionEntry { ToNode = p_ToNode };
            var s_Events = UiTypeCatalog.EnumMembers("UIWidgetEventID");
            if (p_From.Type == "WidgetNode" && s_Events.Contains("UIWidgetEventID_" + p_FromPort)) c.Event = p_FromPort; else c.FromPort = p_FromPort;
            var s_TargetIsWidget = m_Current?.AllNodes.Any(n => n.Label == p_ToNode && n.IsWidget) == true
                                   || DocScreen(m_Current?.Partition ?? "")?.Nodes.Any(n => n.InstanceName == p_ToNode && n.Type == "WidgetNode") == true;
            if (s_TargetIsWidget && s_Events.Contains("UIWidgetEventID_" + p_ToPort)) c.ToEvent = p_ToPort; else c.ToPort = p_ToPort;
            return c;
        }

        /// <summary>Puts a shipped node into the document as an edit (properties copied for widgets).</summary>
        NodeEntry EditShippedNode(string p_Label, bool p_PushUndo = true)
        {
            var s_Existing = DocScreen(m_Current!.Partition)?.Nodes.FirstOrDefault(n => n.InstanceName == p_Label);
            if (s_Existing != null) return s_Existing;
            if (p_PushUndo) PushUndo();
            var e = EnsureDocScreen(m_Current.Partition);
            var s_Shipped = m_Current.Widgets.FirstOrDefault(w => w.InstanceName == p_Label);
            var s_Any = m_Current.AllNodes.FirstOrDefault(n => n.Label == p_Label);
            var n = new NodeEntry { InstanceName = p_Label, New = s_Any == null && s_Shipped == null, Type = s_Any?.Type ?? "WidgetNode", Widget = s_Shipped?.WidgetPartition, ReplaceProperties = false };
            if (s_Shipped != null) foreach (var p in s_Shipped.Properties) n.Properties[p.Name] = p.Value;
            e.Nodes.Add(n);
            RefreshGraph();
            return n;
        }

        // ------------------------------------------------------------------------------------------ graph

        void RefreshGraph()
        {
            m_Graph.SetGraph(m_Current, m_Current == null ? null : DocScreen(m_Current.Partition));
            GraphViewport.ContentSize = m_Graph.Extent();
            GraphTitle.Text = m_Current == null ? "" : $"{m_Current.Partition}: {m_Current.AllNodes.Count} nodes, {m_Current.Wires.Count} connections" + (m_Graph.RemovedWires > 0 ? $" ({m_Graph.RemovedWires} disconnected by the document)" : "") + (m_Graph.UnresolvedWires > 0 ? $"  — {m_Graph.UnresolvedWires} WIRE(S) WITHOUT A PORT (red)" : "");
            if (m_Graph.UnresolvedWires > 0) Log($"graph: {m_Graph.UnresolvedWires} wire(s) could not be attached to a port — drawn red; please report the screen");
        }

        void OnGraphSelection(GraphView.Box? p_Box)
        {
            m_SelectedNode = p_Box?.Label;
            m_SelectedKey = p_Box?.Shipped?.Guid;
            m_SelectedPlacement = p_Box != null && m_Current != null && m_Current.Placements.Any(p => p.Name == p_Box.Label) ? p_Box.Label : null;
            // the stage mirrors the graph's selection: every selected widget box is a selected clip (the whole set, no event back)
            if (m_Current != null)
            {
                var s_Clips = m_Graph.SelectedLabels.Where(l => m_Current.Placements.Any(p => p.Name == l)).ToList();
                if (s_Clips.Count > 1) m_Stage.SetSelection(s_Clips, m_SelectedPlacement ?? s_Clips[^1]);
                else if (m_SelectedPlacement != null) m_Stage.Selected = m_SelectedPlacement;
                if (m_SelectedPlacement != null) SyncLayerSelection();
            }
            ShowProperties();
            UpdateStatus();
        }

        /// <summary>
        /// Alt+click on a wire: a document wire leaves its node's connections; a shipped wire goes on the screen's removed list —
        /// the mod erases that UINodeConnection (by its instance guid) from the screen at load ([UNWIRE] in the client log) — and
        /// is no longer drawn. One undo step; the Screen ▸ Removed connections row puts a shipped one back.
        /// </summary>
        void OnWireRemoveRequested(GraphView.Wire p_Wire)
        {
            if (m_Current == null) return;
            if (p_Wire.IsDoc)
            {
                if (p_Wire.DocNode == null || p_Wire.DocConn == null || !p_Wire.DocNode.Connections.Contains(p_Wire.DocConn)) { Log($"{p_Wire.Label}: not a connection of the document any more"); return; }
                PushUndo();
                p_Wire.DocNode.Connections.Remove(p_Wire.DocConn);
                Log($"disconnected {p_Wire.Label} (a wire of the document)");
            }
            else
            {
                if (p_Wire.Guid == "") { Log($"{p_Wire.Label}: this shipped wire has no instance guid, it cannot be removed"); return; }
                var s_Entry = DocScreen(m_Current.Partition);
                if (s_Entry != null && s_Entry.RemovedConnections.Any(r => string.Equals(r.Guid, p_Wire.Guid, StringComparison.OrdinalIgnoreCase))) return;
                PushUndo();
                var s_Ends = p_Wire.Label.Split(" -> ");
                EnsureDocScreen(m_Current.Partition).RemovedConnections.Add(new RemovedConnectionEntry { Guid = p_Wire.Guid, From = s_Ends[0], To = s_Ends.Length > 1 ? s_Ends[1] : "" });
                Log($"disconnected {p_Wire.Label}: a shipped wire — the mod erases it from the screen at load ([UNWIRE] in the client log); Ctrl+Z or the x in Screen ▸ Removed connections puts it back");
            }
            RefreshGraph();
            ShowProperties();
            UpdateStatus();
        }

        /// <summary>A wire dragged on the graph: recorded on the source node's document entry (created as an edit if the node is shipped). Refused, with the reason logged, when the game's data never wires such ports.</summary>
        void OnWireRequested(GraphView.Box p_From, string p_FromField, string p_FromPort, GraphView.Box p_To, string p_ToField, string p_ToPort)
        {
            if (m_Current == null) return;
            // the same check the drag shows on the dots, for wires that arrive by other paths (the seams, a typed row)
            var s_FromBoxPort = GraphView.FindBoxPort(p_From, p_FromField, p_FromPort, false) ?? GraphView.FindBoxPort(p_From, p_FromField, p_FromPort, true);
            var s_ToBoxPort = GraphView.FindBoxPort(p_To, p_ToField, p_ToPort, true) ?? GraphView.FindBoxPort(p_To, p_ToField, p_ToPort, false);
            if (s_FromBoxPort != null && s_ToBoxPort != null)
            {
                var s_Verdict = CheckWire(p_From, s_FromBoxPort, p_To, s_ToBoxPort);
                if (!s_Verdict.Ok) { Log($"wire {p_From.Label}.{p_FromPort} -> {p_To.Label}.{p_ToPort} refused: {s_Verdict.Reason}"); return; }
                Log($"wire {p_From.Label}.{p_FromPort} -> {p_To.Label}.{p_ToPort}: {s_Verdict.Reason}");
            }
            // the connection is worked out on the existing entry (or a probe with the box's type) so a duplicate costs no undo step
            var s_Existing = p_From.Doc ?? DocScreen(m_Current.Partition)?.Nodes.FirstOrDefault(n => n.InstanceName == p_From.Label);
            var s_Probe = s_Existing ?? new NodeEntry { InstanceName = p_From.Label, Type = p_From.Type };
            var s_FromSpec = p_FromField.Equals("Outputs", StringComparison.OrdinalIgnoreCase) && s_Probe.Type != "WidgetNode" ? "Outputs:" + p_FromPort : p_FromPort;
            var s_ToSpec = p_ToField.Equals("Inputs", StringComparison.OrdinalIgnoreCase) && !(p_To.Type == "WidgetNode") ? "Inputs:" + p_ToPort : p_ToPort;
            if (s_Probe.Type != "WidgetNode" && !p_FromField.Equals("Outputs", StringComparison.OrdinalIgnoreCase)) s_FromSpec = p_FromField; // single port: the field itself
            if (p_To.Type != "WidgetNode" && !p_ToField.Equals("Inputs", StringComparison.OrdinalIgnoreCase)) s_ToSpec = p_ToField;
            var c = MakeConnection(s_Probe, s_FromSpec, p_To.Label, s_ToSpec);
            if (s_Existing != null && s_Existing.Connections.Any(x => (x.Event ?? x.FromPort) == (c.Event ?? c.FromPort) && x.ToNode == c.ToNode && (x.ToEvent ?? x.ToPort) == (c.ToEvent ?? c.ToPort))) return;
            PushUndo();
            var s_Node = s_Existing ?? EditShippedNode(p_From.Label, false);
            s_Node.Connections.Add(c);
            Log($"wired {p_From.Label}.{c.Event ?? c.FromPort} -> {p_To.Label}.{c.ToEvent ?? c.ToPort}");
            m_SelectedNode = p_From.Label; m_SelectedKey = p_From.Shipped?.Guid; m_SelectedPlacement = m_Current.Placements.Any(p => p.Name == p_From.Label) ? p_From.Label : null;
            RefreshGraph();
            m_Graph.SelectKey(p_From.Key);
            ShowProperties();
        }

        void OnGraphFit(object p_Sender, RoutedEventArgs e) => GraphViewport.Fit();

        void OnCentreTab(object p_Sender, SelectionChangedEventArgs e)
        {
            if (CentreTabs?.SelectedIndex == 1) Dispatcher.BeginInvoke(new Action(() => { if (m_Graph.Boxes.Any()) GraphViewport.Fit(); }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>A node of any non-widget type, born with its type's ports; fields come from the properties panel. The button puts it in a fresh column to the right.</summary>
        void OnAddNode(object p_Sender, RoutedEventArgs e)
        {
            if (m_Current == null || NodeTypeBox.SelectedItem is not string s_Type) { Log("select a screen and a node type first"); return; }
            AddNodeAt(s_Type, System.Math.Round(m_Graph.Extent().Width), 40, null);
        }

        /// <summary>Adds a node of a type at a graph position (the drop point, or a fresh column), every non-port field at its default; p_Init customises it before it is shown.</summary>
        NodeEntry? AddNodeAt(string s_Type, double p_X, double p_Y, Action<NodeEntry>? p_Init)
        {
            if (m_Current == null) { Log("select a screen first"); return null; }
            if (UiTypeCatalog.Describe(s_Type)?.IsNode != true) { Log($"{s_Type} is not a node type"); return null; }
            PushUndo();
            var s_Entry = EnsureDocScreen(m_Current.Partition);
            var s_Names = m_Current.AllNodes.Select(n => n.Label).Concat(s_Entry.Nodes.Select(n => n.InstanceName)).ToHashSet();
            var s_N = 1;
            while (s_Names.Contains($"{s_Type}_{s_N:00}")) s_N++;
            var s_Node = new NodeEntry { InstanceName = $"{s_Type}_{s_N:00}", New = true, Type = s_Type, GraphX = System.Math.Max(0, System.Math.Round(p_X)), GraphY = System.Math.Max(0, System.Math.Round(p_Y)) };
            var s_Info = UiTypeCatalog.Describe(s_Type);
            if (s_Info != null)
                foreach (var f in s_Info.Fields.Where(f => f.DeclaringType != "UINodeData" && f.Kind is not (UiTypeCatalog.Kind.Port or UiTypeCatalog.Kind.PortArray)))
                    s_Node.Fields[f.Name] = DefaultValue(f);
            p_Init?.Invoke(s_Node);
            s_Entry.Nodes.Add(s_Node);
            m_SelectedNode = s_Node.InstanceName; m_SelectedKey = null; m_SelectedPlacement = null;
            RefreshGraph();
            m_Graph.Select(s_Node.InstanceName);
            ShowProperties();
            Log($"added {s_Node.InstanceName} ({s_Type}) -- fill its fields on the right, wire it on the graph");
            return s_Node;
        }

        // ------------------------------------------------------------------------------------------ palette

        void OnAddWidget(object p_Sender, RoutedEventArgs e)
        {
            if (PaletteList.SelectedItem is not string s_Short) { Log("select a screen and a widget first"); return; }
            AddWidget(s_Short);
        }

        /// <summary>Adds a widget asset (its ui/assets/ short name) to the open screen: import + placement on the stage (at p_Stage, or at (100,100)) + a new graph node (its box at p_Graph when given).</summary>
        NodeEntry? AddWidget(string s_Short, Point? p_Stage = null, Point? p_Graph = null, (double X, double Y)? p_Scale = null)
        {
            if (m_Current == null) { Log("select a screen first"); return null; }
            if (m_Current.Movie == null) { Log("a flow graph has no stage: widgets go on screens (ui/flow/screen/…)"); return null; }
            var s_Resource = "ui/assets/" + s_Short;
            var s_Movie = WidgetMovie(s_Resource);
            if (s_Movie == null) { Log($"{s_Resource} has no movie"); return null; }
            // the symbol the movie exports under the widget's own name (Grid -> "Grid"), else its last export
            var s_Exports = s_Movie.Exports().SelectMany(x => x.Characters).Select(c => c.Name).Where(n => !n.StartsWith("__")).ToList();
            var s_Symbol = s_Exports.FirstOrDefault(n => string.Equals(n, s_Short, StringComparison.OrdinalIgnoreCase)) ?? s_Exports.LastOrDefault();
            if (s_Symbol == null) { Log($"{s_Resource} exports no symbol"); return null; }
            var s_At = p_Stage ?? new Point(100, 100);

            PushUndo();
            var s_Entry = EnsureDocScreen(m_Current.Partition);
            var s_Base = char.ToUpperInvariant(s_Symbol[0]) + s_Symbol.Substring(1);
            var s_Names = m_Current.Placements.Select(p => p.Name).ToHashSet();
            var s_N = 1;
            while (s_Names.Contains($"{s_Base}_{s_N:00}")) s_N++;
            var s_Name = $"{s_Base}_{s_N:00}";

            var s_UsedChars = m_Current.Movie.Characters().Keys.Concat(m_Current.Movie.ImportedCharacters().Keys).Where(c => c < 20000).DefaultIfEmpty((ushort)1).Max();
            var s_Char = (ushort)(s_UsedChars + 1);
            var s_Sprite = m_Current.Placements.GroupBy(p => p.SpriteId).OrderByDescending(g => g.Count()).First().Key;
            var s_Depth = (ushort)(m_Current.Placements.Where(p => p.SpriteId == s_Sprite).Select(p => (int)p.Depth).DefaultIfEmpty(0).Max() + 1);

            var s_Url = s_Symbol + ".swf";
            if (!m_Current.Movie.Imports().Any(i => i.Url.Equals(s_Url, StringComparison.OrdinalIgnoreCase) && i.Characters.Any(c => c.Name == s_Symbol)))
                s_Entry.Stage.Add($"import:{s_Url}:{s_Char}:{s_Symbol}");
            else
                s_Char = m_Current.Movie.Imports().First(i => i.Url.Equals(s_Url, StringComparison.OrdinalIgnoreCase)).Characters.First(c => c.Name == s_Symbol).Id;
            // the add op is local to the sprite: put the widget at the stage point whatever the sprite's own placement
            var s_Sibling = m_Current.Placements.FirstOrDefault(p => p.SpriteId == s_Sprite);
            var (s_LocalX, s_LocalY) = s_Sibling != null ? s_Sibling.LocalFor(s_At.X, s_At.Y) : (s_At.X, s_At.Y);
            var s_Scale = p_Scale ?? (1.0, 1.0);
            s_Entry.Stage.Add($"add:{s_Sprite}:{s_Name}:{s_Char}:{s_Depth}:{StageCanvas.F(System.Math.Round(s_LocalX))}:{StageCanvas.F(System.Math.Round(s_LocalY))}:{StageCanvas.F(s_Scale.X)}:{StageCanvas.F(s_Scale.Y)}");
            // the construct-time variables the widget's shipped placements carry (m_rowType, buttonType…): its constructor reads them before
            // onClipLoad, so a placement without them runs blind (measured: a TextField without m_rowType attaches no text row)
            var s_Usual = m_WidgetCatalog != null && m_WidgetCatalog.Widgets.TryGetValue(s_Resource, out var s_Info) ? s_Info.UsualClipVars() : new List<(string, object?)>();
            if (s_Usual.Count > 0) s_Entry.Stage.Add($"vars:{s_Name}:{GfxMovie.FormatClipVars(s_Usual)}");
            var s_Node = new NodeEntry { InstanceName = s_Name, New = true, Widget = s_Resource, FocusIndex = -1 };
            if (p_Graph != null) { s_Node.GraphX = System.Math.Max(0, System.Math.Round(p_Graph.Value.X)); s_Node.GraphY = System.Math.Max(0, System.Math.Round(p_Graph.Value.Y)); }
            s_Entry.Nodes.Add(s_Node);
            Log($"added {s_Name} ({s_Resource}, symbol {s_Symbol} as #{s_Char}, depth {s_Depth}) at stage ({StageCanvas.F(s_At.X)},{StageCanvas.F(s_At.Y)})" +
                (s_Usual.Count > 0 ? $" with its usual clip variables ({GfxMovie.FormatClipVars(s_Usual)})" : m_WidgetCatalog == null ? " (no widget catalogue yet: no clip variables — add them in Properties ▸ Clip variables once it is up)" : "") +
                " -- set its scale in the stage ops, its properties and binding on the right");
            m_SelectedPlacement = s_Name; m_SelectedNode = s_Name; m_SelectedKey = null;
            ApplyDocumentToCurrent();
            m_Stage.Selected = s_Name;
            return s_Node;
        }

        // ------------------------------------------------------------------------------------------ document

        void OnNewDocument(object p_Sender, RoutedEventArgs e)
        {
            m_Doc = new ScreenDocument { Name = "MyUiMod", Superbundle = "myuimod/ui", Bundle = "myuimod/uibundle" };
            m_DocPath = "";
            ClearHistory();
            RegisterNewScreens();   // the screens the old document made go with it
            UpdateSaveButtons();
            ApplyDocumentToCurrent();
            ShowProperties();
            Log("new mod document (New screen makes a screen from nothing; the explorer opens the game's; Save as… chooses where it is kept)");
        }

        /// <summary>The New screen button: asks for a name (a headless run takes one), then makes the screen.</summary>
        async void OnNewScreen(object p_Sender, RoutedEventArgs e)
        {
            var s_Name = EditorSettings.Headless ? "NewScreen" : NameDialog.Ask(this, "New screen", "Name of the new screen (letters, digits, _):", "MyScreen");
            if (string.IsNullOrWhiteSpace(s_Name)) return;
            await NewScreen(s_Name);
        }

        /// <summary>
        /// A screen made from nothing: the game's smallest (ui/flow/screen/emptyscreen — its 1280x720 movie with the screen's
        /// ActionScript class, an input listener and the root sprite; its 3-instance partition) cloned under the new name and
        /// fresh guids, put in the document as new, listed in the explorer and opened. Build ships its movie and partition as
        /// new entries of the bundle; a StateNode in a flow graph shows it like any other screen.
        /// </summary>
        public async Task<ScreenEntry?> NewScreen(string p_Name)
        {
            var s_Partition = RimeLib.Cmd.UiBuilder.NewScreen.PartitionName(p_Name);
            if (m_AllScreens.Contains(s_Partition) || DocScreen(s_Partition) != null) { Log($"{s_Partition} exists already: pick another name"); return null; }
            if (!m_Rime.HasCache && !m_Rime.IsMounted) { Log("the template screen needs the cache or a mount"); return null; }
            PushUndo();
            var s_Entry = new ScreenEntry { Partition = s_Partition, Movie = RimeLib.Cmd.UiBuilder.NewScreen.MovieName(p_Name), New = true, Template = RimeLib.Cmd.UiBuilder.NewScreen.DefaultTemplate, Title = RimeLib.Cmd.UiBuilder.NewScreen.Clean(p_Name) };
            m_Doc.Screens.Add(s_Entry);
            RegisterNewScreens();
            Log($"new screen {s_Partition} (asset {RimeLib.Cmd.UiBuilder.NewScreen.AssetName(p_Name)}), cloned from {s_Entry.Template}: drop widgets on its stage, wire its graph; a StateNode in a flow graph shows it");
            await OpenScreen(s_Partition);
            CentreTabs.SelectedIndex = 0;
            return s_Entry;
        }

        readonly HashSet<string> m_GamePartitions = new(StringComparer.OrdinalIgnoreCase);   // what the game (cache / mount) lists: everything else in the lists is the document's

        /// <summary>
        /// The document's new screens join the lists (explorer, type filter, Screen pickers) as UIScreenAssets — and a new screen the
        /// document no longer has (undone, another document) leaves them, its tab and its model with it.
        /// </summary>
        void RegisterNewScreens()
        {
            var s_Changed = false;
            foreach (var s in m_Doc.Screens.Where(s => s.New))
            {
                if (!m_AllScreens.Contains(s.Partition)) { m_AllScreens.Add(s.Partition); s_Changed = true; }
                if (!m_AllPartitions.Contains(s.Partition)) m_AllPartitions.Add(s.Partition);
            }
            foreach (var s_Gone in m_AllScreens.Where(p => !m_GamePartitions.Contains(p) && DocScreen(p)?.New != true).ToList())
            {
                m_AllScreens.Remove(s_Gone); m_AllPartitions.Remove(s_Gone); m_Screens.Remove(s_Gone);
                if (m_OpenScreens.Contains(s_Gone)) CloseScreen(s_Gone);
                s_Changed = true;
            }
            if (s_Changed) FillExplorerList();
        }

        void OnOpenDocument(object p_Sender, RoutedEventArgs e)
        {
            var d = new OpenFileDialog { Filter = "screen document|*.json" };
            if (d.ShowDialog() == true) LoadDocument(d.FileName);
        }

        void LoadDocument(string p_Path)
        {
            try
            {
                m_Doc = JsonConvert.DeserializeObject<ScreenDocument>(File.ReadAllText(p_Path)) ?? new ScreenDocument();
                m_DocPath = p_Path;
                ClearHistory();
                m_Settings.LastDocument = p_Path; m_Settings.Save();
                RegisterNewScreens();
                UpdateSaveButtons();
                Log("document: " + p_Path);
                WarnClipVars();
                ApplyDocumentToCurrent();
                ShowProperties();
            }
            catch (Exception s_Ex) { Log("open failed: " + s_Ex.Message); }
        }

        /// <summary>Save: writes the document to its file without asking. Nothing happens until Save as… has chosen the file (the button is disabled meanwhile).</summary>
        void OnSaveDocument(object p_Sender, RoutedEventArgs e)
        {
            if (m_DocPath == "") { Log("the document has no file yet: use Save as… to choose where this session's changes are kept; Save then writes there without asking"); return; }
            WriteDocument();
            Log("saved " + m_DocPath);
        }

        /// <summary>Save as…: the user decides where the document (this session's changes: screens, nodes, wires, stage ops) lives; Save writes there from then on, Open… brings it back.</summary>
        void OnSaveAsDocument(object p_Sender, RoutedEventArgs e)
        {
            var d = new SaveFileDialog { Title = "Save the screen document as… (the editor's project file: this session's changes; the mod is built from it)", Filter = "screen document|*.json", FileName = m_DocPath != "" ? Path.GetFileName(m_DocPath) : m_Doc.Name + ".json" };
            if (m_DocPath != "" && Directory.Exists(Path.GetDirectoryName(m_DocPath))) d.InitialDirectory = Path.GetDirectoryName(m_DocPath);
            if (d.ShowDialog() != true) return;
            m_DocPath = d.FileName;
            WriteDocument();
            UpdateSaveButtons();
            Log("saved as " + m_DocPath + " — Save (Ctrl+S) writes there from now on; Open… brings it back");
        }

        void WriteDocument()
        {
            RelocateImages();
            File.WriteAllText(m_DocPath, JsonConvert.SerializeObject(m_Doc, Formatting.Indented));
            m_Settings.LastDocument = m_DocPath; m_Settings.Save();
        }

        /// <summary>Save works only once the document has a file (Save as…, Open…, or the last document reopened at start).</summary>
        void UpdateSaveButtons()
        {
            if (SaveButton == null) return;
            SaveButton.IsEnabled = m_DocPath != "";
            SaveButton.ToolTip = m_DocPath != "" ? $"Save to {m_DocPath} (Ctrl+S)" : "Save as… first: the document has no file yet.";
        }

        /// <summary>A screen the mod has something to say about.</summary>
        static bool HasWork(ScreenEntry s) => s.New || s.Nodes.Count > 0 || s.Stage.Count > 0 || s.Fields.Count > 0 || s.RemovedConnections.Count > 0;

        /// <summary>
        /// Placements the document adds without the construct-time variables their widget's shipped placements carry (documents from
        /// before the editor knew about them): said in the log, with the way to fix it — such a widget runs blind in the game.
        /// </summary>
        void WarnClipVars()
        {
            if (m_WidgetCatalog == null) return;
            foreach (var s in m_Doc.Screens)
                foreach (var s_Add in s.Stage.Where(o => o.StartsWith("add:")).ToList())
                {
                    var f = s_Add.Split(':');
                    if (f.Length < 3) continue;
                    var s_Clip = f[2];
                    if (s.Stage.Any(o => o.StartsWith("vars:" + s_Clip + ":"))) continue;
                    var s_Node = s.Nodes.FirstOrDefault(n => n.InstanceName == s_Clip);
                    if (s_Node?.Widget == null || !m_WidgetCatalog.Widgets.TryGetValue(s_Node.Widget, out var w)) continue;
                    var s_Usual = w.UsualClipVars();
                    if (s_Usual.Count == 0) continue;
                    Log($"⚠ {s.Partition.Split('/').Last()} / {s_Clip}: added without the clip variables its widget's placements carry ({string.Join(", ", s_Usual.Select(v => v.Name))}) — select it and press + on 'Usual variables' in Properties ▸ Clip variables, or it runs blind in the game");
                }
        }

        /// <summary>
        /// Build mod…: the questions first (name, version, authors, description, bundle names, where), then the minutes (a mount
        /// when there is none, the emit, the superbundle). What lands in the Mods folder is a complete VU mod: mod.json,
        /// ext/Shared + ext/Client, sb/Win32/&lt;superbundle&gt;.sb and src/ (the built movies, the new partitions, the document
        /// and the Rime recipe, with no path of this machine). Installing it (the line in ModList.txt, the client's cached copy)
        /// is the user's step: the Output says so, the tool never touches those files. A headless run asks nothing.
        /// </summary>
        async void OnBuild(object p_Sender, RoutedEventArgs e)
        {
            if (!m_Doc.Screens.Any(HasWork)) { Log("nothing to build: the document has no edits yet (drop a widget, wire a node, make a screen…)"); return; }
            BuildRequest? s_Request = null;
            if (!EditorSettings.Headless)
            {
                s_Request = BuildDialog.Ask(this, m_Doc, m_Settings);
                if (s_Request == null) return;
                if (s_Request.ModsFolder != m_Settings.ModsPath) { m_Settings.ModsPath = s_Request.ModsFolder; m_Settings.Save(); ShowPaths(); }
                ShowProperties();   // the Document rows follow what was typed
            }
            // the superbundle is built against the game's bundles: a build from the cache mounts first, by itself
            if (!m_Rime.IsMounted && !await EnsureMounted()) return;
            try { Directory.CreateDirectory(m_Settings.ModsPath); }
            catch { Log("Mods folder cannot be created: " + m_Settings.ModsPath + " (Settings… ▸ Mods folder)"); return; }
            // a build never asks where the document goes: a saved document is refreshed on its file, an unsaved one just builds (Save as… keeps it)
            if (m_DocPath != "") WriteDocument();
            BuildButton.IsEnabled = false;
            Status("building…");
            var s_Log = new StringWriter();
            try
            {
                var s_Work = Path.Combine(EditorSettings.DefaultBuildRoot(), m_Doc.Name);
                var s_DocDir = m_DocPath != "" ? Path.GetDirectoryName(m_DocPath)! : s_Work;
                var s_Record = s_Request?.Record == true ? RecorderParts() : null;
                var s_Dir = await Task.Run(() => m_Rime.BuildMod(m_Doc, s_DocDir, m_Settings.ModsPath, s_Log, s_Work, s_Record));
                Log(s_Log.ToString());
                Log($"MOD BUILT: {s_Dir}" + (s_Record != null ? " — WITH THE RECORDER inside (a diagnosis build)" : ""));
                if (s_Record != null) Log("  recording: install this mod alone (no other mod shipping these screens), open the screens in the game, press F5; then Import recording… (the mod's mod.db through uirec_db.py, or the console copied) — the records carry the edited screens' data, new nodes included");
                Log($"  mod.json (v{m_Doc.Version}, {m_Doc.Screens.Count(HasWork)} screen(s)) · ext/Shared + ext/Client · sb/Win32/{m_Doc.Superbundle}.sb — the built movies and the recipe are in {s_Work} (not part of the mod)");
                Log(m_Doc.IsStatic
                    ? "  graph delivery: STATIC — the edited screens ship as whole partitions in the bundle; ext/Client only probes ([PROBE] <screen>: N nodes … ours: X=true/false in the client log)"
                    : "  graph delivery: runtime — ext/Client edits each screen's live graph at load ([NODE]/[WIRE] in the client log)");
                Log(m_DocPath != "" ? $"  document saved: {m_DocPath}" : "  the document is not saved anywhere yet: Save as… keeps this session's changes for later");
                Log($"  to run it: a line '{m_Doc.Name}' (no '#') in the server's ModList.txt, then restart the server; if the game shows the old mod, delete the client's cached copy under %LOCALAPPDATA%\\VeniceUnleashed\\mods\\{m_Doc.Name.ToLowerInvariant()}");
                Status("built " + m_Doc.Name);
            }
            catch (Exception s_Ex)
            {
                Log(s_Log + "\nBUILD FAILED: " + s_Ex.Message);
                Status("build failed");
            }
            BuildButton.IsEnabled = true;
        }

        // ------------------------------------------------------------------------------------------ view

        bool m_SyncingZoom;

        /// <summary>The slider zooms around the view centre; the wheel (in the viewport) zooms toward the cursor.</summary>
        void OnZoom(object p_Sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (Viewport == null || m_SyncingZoom) return;
            Viewport.SetScale(ZoomSlider.Value);
        }

        void OnViewportScale(double p_Scale)
        {
            m_SyncingZoom = true;
            try
            {
                if (System.Math.Abs(ZoomSlider.Value - p_Scale) > 1e-4) ZoomSlider.Value = p_Scale;
                ZoomText.Text = (p_Scale * 100).ToString("0", CultureInfo.InvariantCulture) + " %";
                // the handles keep their screen size across the zoom
                m_Stage.ViewScale = p_Scale;
                if (m_Stage.Selected != null) m_Stage.Redraw();
                UpdateStatus();
            }
            finally { m_SyncingZoom = false; }
        }

        void OnFit(object p_Sender, RoutedEventArgs e) => Viewport.Fit();

        void OnZoomOne(object p_Sender, RoutedEventArgs e) => Viewport.SetScale(1.0);

        /// <summary>Settings changed: paths on the bar, zoom speed on the viewport.</summary>
        void ApplySettings()
        {
            ShowPaths();
            StageViewport.ZoomSpeed = m_Settings.CanvasZoomSpeed;
            if (m_Texts != null && m_Texts.Language != m_Settings.Language) { m_Renderer = null; m_Texts = null; RenderArt(); m_Stage.Redraw(); }
        }
    }
}
