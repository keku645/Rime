using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json.Linq;
using RimeLib.Cmd.Scaleform;
using RimeLib.Cmd.UiBuilder;
using RimeUIEditor.View;

namespace RimeUIEditor
{
    /// <summary>
    /// Headless seam: drives the real window through the user's path and compares the artefact against the
    /// mod that is known to work in game. Usage:
    ///   RimeUIEditor.exe --selftest &lt;bf3 path&gt; &lt;mods root&gt; &lt;reference screen .gfx&gt; &lt;out dir&gt;
    /// Writes &lt;out&gt;/selftest.png (the window after the edit) and exits 0 on success, 1 otherwise.
    /// Uses scratch settings when RUE_SETTINGS points at one, so the machine's own settings are untouched.
    /// </summary>
    public partial class MainWindow
    {
        void Photograph(string p_Path)
        {
            // children added since the last layout pass have no size yet and would not be drawn: lay the window out first
            UpdateLayout();
            var s_Bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            s_Bitmap.Render(this);
            var s_Encoder = new PngBitmapEncoder();
            s_Encoder.Frames.Add(BitmapFrame.Create(s_Bitmap));
            using var s_Png = File.Create(p_Path);
            s_Encoder.Save(s_Png);
        }

        /// <summary>A hash of the stage's art rendered at 1:1 (1280x720): two renders that differ in pixels differ here.</summary>
        string ArtHash()
        {
            var s_Visual = new DrawingVisual();
            using (var s_Dc = s_Visual.RenderOpen())
            {
                s_Dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 1280, 720));
                if (m_Stage.Art != null) s_Dc.DrawDrawing(m_Stage.Art);
            }
            var s_Bitmap = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32);
            s_Bitmap.Render(s_Visual);
            var s_Pixels = new byte[1280 * 720 * 4];
            s_Bitmap.CopyPixels(s_Pixels, 1280 * 4, 0);
            return Convert.ToHexString(System.Security.Cryptography.MD5.HashData(s_Pixels));
        }

        /// <summary>
        /// Reads a value out of the running Ruffle preview: the bridge's rueDispatch resolves p_Path (a clip, a property —
        /// "_level0.sc1.instance1.KitSelector_01.mcContainer.Slot_0.headerIcon.txt.text") and calls toString on it, the page
        /// echoes the result into the log as JSON ("\"0\"", "true", "321"; "undefined" when nothing is there). A TEXT read
        /// out of the movie where a photo can only show pixels.
        /// </summary>
        async Task<string> ProbeClip(string p_Path)
        {
            if (m_RuffleHost == null) return "(no player)";
            var s_At = LogBox.Text.Length;
            var s_Mark = "dispatch " + p_Path + ".toString -> ";
            m_RuffleHost.Dispatch(p_Path, "toString", null, null, null, true);
            for (var i = 0; i < 80 && !LogBox.Text[s_At..].Contains(s_Mark); ++i) await Task.Delay(25);
            var s_Log = LogBox.Text[s_At..]; var s_I = s_Log.IndexOf(s_Mark);
            return s_I < 0 ? "(no echo)" : s_Log[(s_I + s_Mark.Length)..].Split('\n')[0].Trim();
        }

        /// <summary>A recorder echo (one init/refresh record) as the console lines the game's receiver prints — what Import recording… parses.</summary>
        static string ConsoleOf(JObject p_Record)
        {
            var s_Text = p_Record.ToString(Newtonsoft.Json.Formatting.None);
            var s_Total = (s_Text.Length + 179) / 180; var s_Console = new System.Text.StringBuilder();
            for (var i = 0; i < s_Total; ++i) { var s_Piece = s_Text.Substring(i * 180, System.Math.Min(180, s_Text.Length - i * 180)); s_Console.Append($"[02:22:{i % 60:00}] [info] [VeniceEXT] [rimeuirecorder] [AS2] #R{p_Record["run"]}.i0|{i + 1}|{s_Total}|{s_Piece.Length}|{s_Piece}\n"); }
            return s_Console.ToString();
        }

        /// <summary>The length of a list as the movie echoes it (an array, or an array-like object with a length).</summary>
        static int CountOfList(JToken? p_List) => p_List is JArray s_Array ? s_Array.Count : (int?)(p_List as JObject)?["length"] ?? 0;

        public async Task<int> SelfTest(string p_GamePath, string p_ModsRoot, string p_ReferenceGfx, string p_OutDir)
        {
            Directory.CreateDirectory(p_OutDir);
            var s_Report = new StringWriter();
            void R(string t) { s_Report.WriteLine(t); Log("[selftest] " + t); }
            try
            {
                m_Settings.GamePath = p_GamePath; m_Settings.ModsPath = p_ModsRoot; ShowPaths();
                {
                    // where the seam's window sits: on the secondary monitor when asked for (RUE_WINDOW_SCREEN=secondary), never on the user's
                    var s_On = Monitors.Of(new System.Windows.Interop.WindowInteropHelper(this).Handle);
                    R($"window on monitor {(s_On == null ? "(unknown)" : $"({s_On.Left},{s_On.Top}) {s_On.Width}x{s_On.Height}{(s_On.Primary ? " primary" : " secondary")}")}; RUE_WINDOW_SCREEN={Environment.GetEnvironmentVariable("RUE_WINDOW_SCREEN") ?? "(unset)"}");
                    if (Monitors.SeamsOnSecondary && Monitors.Secondary() != null && (s_On == null || s_On.Primary)) throw new Exception("the seam's window is on the primary monitor although RUE_WINDOW_SCREEN=secondary");
                }
                // the user's path: Mount game (which also fills the cache next to the scratch settings). RUE_SELFTEST_QUICK=1 skips the
                // mount (minutes) and the build when the cache is warm — the editing steps then run in seconds
                var s_Quick = Environment.GetEnvironmentVariable("RUE_SELFTEST_QUICK") == "1" && m_Rime.HasCache;
                // a warm cache is the user's path: the editor comes up from it, a screen is opened, and the mount happens later through the
                // Mount button (with everything open) — only a cold cache mounts first
                var s_Warm = m_Rime.HasCache;
                if (!s_Quick && !s_Warm && !await EnsureMounted()) throw new Exception("mount failed");
                R($"{(s_Quick ? "cache (quick run, no mount)" : s_Warm ? "cache (warm; the mount comes later, through the button)" : "mounted")}: {m_AllScreens.Count} screens, {m_AllWidgets.Count} assets");
                // the toolbar's disabled buttons (Undo/Redo with an empty history) keep a dark face and grey text: the system's disabled
                // template painted them light-on-light. Probe the rendered pixels, not the style.
                {
                    UpdateLayout();
                    if (UndoButton.IsEnabled) throw new Exception("Undo is enabled with an empty history");
                    var s_Origin = UndoButton.TransformToAncestor(this).Transform(new Point(0, 0));
                    var s_Shot = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    s_Shot.Render(this);
                    var s_Y = (int)(s_Origin.Y + UndoButton.ActualHeight / 2); var s_W = (int)UndoButton.ActualWidth;
                    var s_Row = new byte[s_W * 4];
                    s_Shot.CopyPixels(new Int32Rect((int)s_Origin.X, s_Y, s_W, 1), s_Row, s_W * 4, 0);
                    var s_Face = (R: s_Row[4 * 3 + 2], G: s_Row[4 * 3 + 1], B: s_Row[4 * 3]);
                    var s_Brightest = Enumerable.Range(0, s_W).Max(x => (s_Row[x * 4] + s_Row[x * 4 + 1] + s_Row[x * 4 + 2]) / 3);
                    if (System.Math.Abs(s_Face.R - 46) > 14 || System.Math.Abs(s_Face.G - 49) > 14 || System.Math.Abs(s_Face.B - 54) > 14) throw new Exception($"disabled Undo face = ({s_Face.R},{s_Face.G},{s_Face.B}), expected the dark disabled face (46,49,54)");
                    if (s_Brightest < 80 || s_Brightest > 200) throw new Exception($"disabled Undo's brightest pixel = {s_Brightest}: its text should be a readable grey (about 140), neither invisible nor the enabled white");
                    R($"disabled buttons: dark face ({s_Face.R},{s_Face.G},{s_Face.B}), text grey (brightest {s_Brightest})");
                }
                if (m_AllScreens.Count < 100) throw new Exception("too few screens listed");
                // only the named step and what it strictly needs
                var s_Focus = Environment.GetEnvironmentVariable("RUE_SELFTEST_FOCUS");
                if (!string.IsNullOrWhiteSpace(s_Focus))
                {
                    var s_FocusCode = await SelfTestFocused(s_Focus, R, p_OutDir);
                    File.WriteAllText(Path.Combine(p_OutDir, "selftest.txt"), s_Report.ToString());
                    File.WriteAllText(Path.Combine(p_OutDir, "selftest_log.txt"), LogBox.Text);
                    return s_FocusCode;
                }

                // the cache stands on its own: a second service with no mount lists the same screens and serves the same bytes
                var s_CacheDir = RimeUiService.CacheDirFor(m_Settings.CachePath, p_GamePath);
                var s_Cold = new RimeUiService();
                if (!s_Cold.UseCache(s_CacheDir)) throw new Exception("cache not warm after mount: " + s_CacheDir);
                var s_ColdScreens = s_Cold.Partitions("ui/flow/screen/").ToList();
                var s_MountScreens = m_Rime.Partitions("ui/flow/screen/").Count();
                if (s_ColdScreens.Count != s_MountScreens) throw new Exception($"cache lists {s_ColdScreens.Count} screens, mount {s_MountScreens}");
                if (s_Cold.Partitions("ui/flow/graph/").Count() != m_Rime.Partitions("ui/flow/graph/").Count()) throw new Exception("cache lists a different number of flow graphs than the mount");
                if (m_AllScreens.Count(p => p.StartsWith("ui/flow/graph/")) < 70) throw new Exception($"the list carries {m_AllScreens.Count(p => p.StartsWith("ui/flow/graph/"))} flow graphs, expected the game's 80");
                var s_RefScreen = "ui/flow/screen/customizeaccessoriesscreen";
                if (!s_Cold.ResourceBytes("ui/assets/customizeaccessoriesscreen").SequenceEqual(m_Rime.ResourceBytes("ui/assets/customizeaccessoriesscreen")))
                    throw new Exception("cached movie differs from the mounted one");
                if (s_Cold.PartitionJson(s_RefScreen)["PartitionGuid"]?.ToString() != m_Rime.PartitionJson(s_RefScreen)["PartitionGuid"]?.ToString())
                    throw new Exception("cached partition differs from the mounted one");
                if (s_Cold.PartitionNameByGuid(m_Rime.PartitionJson("ui/assets/grid")["PartitionGuid"]!.ToString()) != "ui/assets/grid")
                    throw new Exception("cache does not resolve a partition guid without a mount");
                var s_Atlas = s_Cold.TextureDds("ui/assets/button/button_i7");
                if (s_Atlas.Length < 128 || s_Atlas[0] != (byte)'D') throw new Exception("cached atlas texture is not a DDS");
                var s_Loc = s_Cold.PartitionJson("localization/us_loc");
                var s_LocDb = s_Loc.Descendants().OfType<JProperty>().First(p => p.Name == "BinaryChunk").Value.ToString();
                var s_LocBytes = s_Cold.ChunkBytes(s_LocDb);
                if (s_LocBytes.Length < 100000) throw new Exception($"us_loc text database chunk not cached ({s_LocBytes.Length} bytes)");
                R($"cache: english text database chunk {s_LocDb} = {s_LocBytes.Length} bytes");
                var s_Texts = TextDatabase.Load(s_Cold, "us");
                if (s_Texts.Lookup("ID_M_YES") != "YES" || s_Texts.Lookup("ID_P_ANAME_NOCAMO") != "No Camo")
                    throw new Exception($"text database: ID_M_YES={s_Texts.Lookup("ID_M_YES")} ID_P_ANAME_NOCAMO={s_Texts.Lookup("ID_P_ANAME_NOCAMO")}");
                R($"texts: {s_Texts.Count} english strings; ID_M_YES={s_Texts.Lookup("ID_M_YES")}, ID_P_ANAME_NOCAMO={s_Texts.Lookup("ID_P_ANAME_NOCAMO")}");
                R($"cache: {s_ColdScreens.Count} screens without a mount; movie, partition, guid map and atlas served from {s_CacheDir}");

                // nothing open yet: the stage and the Layers say so (a blank frame read as "broken")
                for (var i = 0; i < 300 && m_Restoring; ++i) await Task.Delay(100);   // a previous run's session may be coming back
                if (OpenScreens.Count > 0) R($"start: {OpenScreens.Count} screen(s) restored from the last run's session ({m_Current?.Partition.Split('/').Last()} on the stage) — closed for the test");
                CloseAllScreens();
                if (m_Current != null || EmptyHint.Visibility != Visibility.Visible || LayerList.Items.Count != 1) throw new Exception($"empty state: current {m_Current?.Partition}, hint {EmptyHint.Visibility}, layers rows {LayerList.Items.Count}");
                Photograph(Path.Combine(p_OutDir, "selftest_empty.png"));
                R("empty stage: the hint says to open a screen from the Data Explorer; Layers says no screen is open");
                // a screen whose root sprite sits at the stage centre (children at negative local coordinates): the boxes must
                // land where the art lands, a drag must write LOCAL coordinates, and the graph must survive duplicate node names
                m_Doc = new ScreenDocument { Name = "UiEditorSelfTest", Superbundle = "uieditorselftest/ui", Bundle = "uieditorselftest/uibundle" };
                // Save is dead until Save as… (or Open…) has given the document a file; with one, Save writes silently
                m_DocPath = ""; UpdateSaveButtons();
                if (SaveButton.IsEnabled) throw new Exception("Save is enabled with no document file");
                var s_LogBefore = LogBox.Text.Length;
                OnSaveDocument(this, null!);
                if (!LogBox.Text[s_LogBefore..].Contains("Save as…")) throw new Exception("Save without a file did not point at Save as…");
                m_DocPath = Path.Combine(p_OutDir, "UiEditorSelfTest.json"); UpdateSaveButtons();
                if (!SaveButton.IsEnabled) throw new Exception("Save is disabled with a document file");
                OnSaveDocument(this, null!);
                if (!File.Exists(m_DocPath)) throw new Exception("Save did not write the document to its file");
                R("Save: disabled without a file (the log points at Save as…), writes silently once the document has one; Build never asks");
                // the user's path: the explorer — folder ui/flow/screen selected, the screen picked in the list
                if (!(ScreenList.ItemsSource as System.Collections.Generic.List<ExplorerItem>)?.Any(i => i.Partition == "ui/flow/screen/customizesoldierscreen") == true)
                    throw new Exception("the explorer list does not show ui/flow/screen/customizesoldierscreen for the folder " + m_ExplorerFolder);
                ScreenList.SelectedItem = (ScreenList.ItemsSource as System.Collections.Generic.List<ExplorerItem>)!.First(i => i.Partition == "ui/flow/screen/customizesoldierscreen");
                for (var i = 0; i < 1200 && m_Current?.Partition != "ui/flow/screen/customizesoldierscreen" && !LogBox.Text.Contains("load failed"); ++i) await Task.Delay(50);
                if (m_Current?.Partition != "ui/flow/screen/customizesoldierscreen") throw new Exception("customizesoldierscreen did not load through the explorer list");
                if (!OpenScreens.Contains("ui/flow/screen/customizesoldierscreen") || OpenTabs.Children.Count != 1) throw new Exception("the opened screen has no tab");
                if (EmptyHint.Visibility == Visibility.Visible) throw new Exception("the empty-stage hint is still up with a screen open");
                if (!LogBox.Text.Contains("opened ui/flow/screen/customizesoldierscreen: 5 clips in Layers")) throw new Exception("the log does not say what opening the screen did");
                var s_Header = m_Current.Placements.First(p => p.Name == "PageHeader_01");
                if (System.Math.Abs(s_Header.X - 0) > 0.5 || System.Math.Abs(s_Header.Y - 0) > 0.5 || System.Math.Abs(s_Header.LocalX + 640) > 0.5)
                    throw new Exception($"PageHeader_01: stage ({s_Header.X},{s_Header.Y}) local ({s_Header.LocalX},{s_Header.LocalY}) — expected stage (0,0) from local (-640,-360) under a root at (640,360)");
                if (s_Header.Bounds.Left / 20.0 < 80 || s_Header.Bounds.Left / 20.0 > 100) throw new Exception($"PageHeader_01 bounds not on the stage: {s_Header.Bounds}");
                var s_Art = Renderer().PlacementGroups;
                if (!s_Art.ContainsKey(s_Header.Path)) throw new Exception($"no live art group for {s_Header.Path} (have: {string.Join(", ", s_Art.Keys.Take(6))})");
                var (s_Lx, s_Ly) = s_Header.LocalFor(20, 10);
                if (System.Math.Abs(s_Lx + 620) > 0.5 || System.Math.Abs(s_Ly + 350) > 0.5) throw new Exception($"local for stage (20,10) = ({s_Lx},{s_Ly}), expected (-620,-350)");
                if (m_Current.AllNodes.GroupBy(n => n.Label).Any(g => g.Count() > 1) == false) throw new Exception("expected duplicate node labels on this screen (the graph crash case)");
                OnPlacementMoved("PageHeader_01", -620, -350);   // what a drag of (+20,+10) on the stage records
                var s_Op = DocScreen("ui/flow/screen/customizesoldierscreen")!.Stage.Single();
                if (s_Op != "move:PageHeader_01:-620:-350") throw new Exception("drag wrote " + s_Op);
                s_Header = m_Current.Placements.First(p => p.Name == "PageHeader_01");
                if (System.Math.Abs(s_Header.X - 20) > 0.5 || System.Math.Abs(s_Header.Y - 10) > 0.5) throw new Exception($"after the move PageHeader_01 sits at stage ({s_Header.X},{s_Header.Y}), expected (20,10)");
                m_Stage.Selected = "PageHeader_01"; Viewport.Fit(); await Task.Delay(100);
                Photograph(Path.Combine(p_OutDir, "selftest_soldier_stage.png"));
                CentreTabs.SelectedIndex = 1; await Task.Delay(100);
                if (m_Graph.Boxes.Count() != m_Current.AllNodes.Count) throw new Exception("graph boxes != nodes on the duplicate-label screen");
                // every shipped wire lands on ITS port: a ComparisonLogicNode fires from a named array port ("0"/"1"), never from port #0 by accident
                if (m_Graph.UnresolvedWires != 0) throw new Exception($"{m_Graph.UnresolvedWires} wires without a port on customizesoldierscreen");
                if (m_Graph.Wires.Count() != m_Current.Wires.Count) throw new Exception("graph wires != connections");
                var s_Cmp = m_Graph.Boxes.First(b => b.Type == "ComparisonLogicNode" && m_Graph.Wires.Any(w => w.From == b.Key));
                var s_CmpWire = m_Graph.Wires.First(w => w.From == s_Cmp.Key);
                var s_CmpPort = s_Cmp.Outputs.FirstOrDefault(p => p.Key == s_CmpWire.FromPort) ?? throw new Exception("comparison wire not keyed by its port guid");
                if (s_CmpPort.Field != "Outputs" || s_CmpPort.Name == "Outputs" || s_CmpPort.Dangling) throw new Exception($"comparison wire leaves port '{s_CmpPort.Field}:{s_CmpPort.Name}', expected one of the node's named Outputs (0/1/ID0…)");
                if (s_Cmp.Outputs.Count < 2 || s_Cmp.Outputs.Select(p => p.Name).Distinct().Count() != s_Cmp.Outputs.Count) throw new Exception($"{s_Cmp.Label} outputs are not distinct named ports: {string.Join(",", s_Cmp.Outputs.Select(p => p.Name))}");
                var s_Undo1 = m_Undo.Count;
                Photograph(Path.Combine(p_OutDir, "selftest_soldier.png"));
                CentreTabs.SelectedIndex = 0;
                R($"customizesoldierscreen: root at (640,360); PageHeader_01 stage (0,0) = local (-640,-360); drag wrote {s_Op}; graph {m_Graph.Boxes.Count()} boxes with duplicate labels, {m_Graph.Wires.Count()} wires all on their ports ({s_Cmp.Label}.{s_CmpPort.Name} -> …)");
                // the drag was one undo step: Ctrl+Z (the command binding) takes the move op back, the placement returns to (0,0); redo brings it back
                if (s_Undo1 < 1) throw new Exception("the stage drag pushed no undo step");
                ApplicationCommands.Undo.Execute(null, this);
                if (DocScreen("ui/flow/screen/customizesoldierscreen")?.Stage.Count is > 0) throw new Exception("undo did not remove the move op");
                s_Header = m_Current.Placements.First(p => p.Name == "PageHeader_01");
                if (System.Math.Abs(s_Header.X) > 0.5) throw new Exception($"after undo PageHeader_01 sits at stage x={s_Header.X}, expected 0");
                ApplicationCommands.Redo.Execute(null, this);
                if (DocScreen("ui/flow/screen/customizesoldierscreen")?.Stage.SingleOrDefault() != s_Op) throw new Exception("redo did not restore the move op");
                if (System.Math.Abs(m_Current.Placements.First(p => p.Name == "PageHeader_01").X - 20) > 0.5) throw new Exception("after redo the placement did not move back");
                R("undo/redo: Ctrl+Z took the drag back (placement at 0,0 again), Ctrl+Y restored it");
                // the handles on the selected box (the code the mouse drives): a corner scales about the opposite corner, Shift keeps the
                // proportions, an edge scales one axis (the origin follows so the far edge stays), the knob turns it; each is one undo step
                {
                    CentreTabs.SelectedIndex = 0; await Task.Delay(50);
                    // selected the way the user does it (the Layers row): the stage, the graph and the properties follow
                    LayerList.SelectedIndex = m_Current.Placements.OrderByDescending(p => p.Depth).ToList().FindIndex(p => p.Name == "PageHeader_01");
                    await Task.Delay(50);
                    if (m_Stage.Selected != "PageHeader_01") throw new Exception("the Layers row did not select the placement on the stage");
                    var p0 = m_Current.Placements.First(p => p.Name == "PageHeader_01");
                    var r0 = StageCanvas.RectOf(p0); var sx0 = p0.SizeX; var sy0 = p0.SizeY; var s_Rot0 = p0.Rotation; var s_StepsH = m_Undo.Count;
                    RimeUIEditor.Model.PlacementInfo P() => m_Current!.Placements.First(p => p.Name == "PageHeader_01");
                    if (m_Stage.HandleAt(new Point(r0.Right, r0.Bottom)) != StageCanvas.HandleKind.SE) throw new Exception("no SE handle on the selected box's corner");
                    if (m_Stage.HandleAt(new Point(r0.Left, r0.Top + r0.Height / 2)) != StageCanvas.HandleKind.W) throw new Exception("no W handle on the selected box's edge");
                    if (m_Stage.HandleAt(new Point(r0.Left + r0.Width / 2, r0.Top + r0.Height / 2)) != StageCanvas.HandleKind.None) throw new Exception("the box's middle counts as a handle");
                    // SE corner, free: +50 % wide, the same height; the NW corner stays
                    m_Stage.BeginHandle(StageCanvas.HandleKind.SE, new Point(r0.Right, r0.Bottom));
                    m_Stage.DragHandle(new Point(r0.Right + r0.Width / 2, r0.Bottom), false);
                    m_Stage.EndHandle();
                    var p1 = P(); var r1 = StageCanvas.RectOf(p1);
                    if (System.Math.Abs(p1.SizeX - sx0 * 1.5) > 0.01 || System.Math.Abs(p1.SizeY - sy0) > 0.01) throw new Exception($"SE drag: scale ×{p1.SizeX / sx0:0.###}/×{p1.SizeY / sy0:0.###}, expected ×1.5/×1");
                    if (System.Math.Abs(r1.Left - r0.Left) > 1 || System.Math.Abs(r1.Top - r0.Top) > 1 || System.Math.Abs(r1.Width - r0.Width * 1.5) > 2) throw new Exception($"SE drag: box {r1} from {r0}: the opposite corner moved or the width is off");
                    var s_Ops1 = DocScreen("ui/flow/screen/customizesoldierscreen")!.Stage;
                    if (!s_Ops1.Any(o => o.StartsWith("scale:PageHeader_01:")) || m_Undo.Count != s_StepsH + 1) throw new Exception("SE drag: no scale op / not one undo step: " + string.Join(" ; ", s_Ops1));
                    Undo();
                    // SE corner with Shift: uniform, the larger factor wins
                    m_Stage.Selected = "PageHeader_01";
                    m_Stage.BeginHandle(StageCanvas.HandleKind.SE, new Point(r0.Right, r0.Bottom));
                    m_Stage.DragHandle(new Point(r0.Right + r0.Width / 2, r0.Bottom), true);
                    m_Stage.EndHandle();
                    var p2 = P();
                    if (System.Math.Abs(p2.SizeX - sx0 * 1.5) > 0.01 || System.Math.Abs(p2.SizeY - sy0 * 1.5) > 0.01) throw new Exception($"SE drag with Shift: scale ×{p2.SizeX / sx0:0.###}/×{p2.SizeY / sy0:0.###}, expected ×1.5/×1.5");
                    Undo();
                    // W edge: the right edge stays, the width grows by a quarter, the origin moves along (a move op next to the scale op)
                    m_Stage.Selected = "PageHeader_01";
                    m_Stage.BeginHandle(StageCanvas.HandleKind.W, new Point(r0.Left, r0.Top + r0.Height / 2));
                    m_Stage.DragHandle(new Point(r0.Left - r0.Width / 4, r0.Top + r0.Height / 2), false);
                    m_Stage.EndHandle();
                    var p3 = P(); var r3 = StageCanvas.RectOf(p3);
                    if (System.Math.Abs(r3.Right - r0.Right) > 1 || System.Math.Abs(r3.Width - r0.Width * 1.25) > 2 || System.Math.Abs(p3.SizeX - sx0 * 1.25) > 0.01 || System.Math.Abs(p3.SizeY - sy0) > 0.01) throw new Exception($"W drag: box {r3} from {r0}, scale ×{p3.SizeX / sx0:0.###}/×{p3.SizeY / sy0:0.###}");
                    var s_Ops3 = DocScreen("ui/flow/screen/customizesoldierscreen")!.Stage;
                    if (!s_Ops3.Any(o => o.StartsWith("move:PageHeader_01:")) || !s_Ops3.Any(o => o.StartsWith("scale:PageHeader_01:"))) throw new Exception("W drag: expected a move and a scale op: " + string.Join(" ; ", s_Ops3));
                    Undo();
                    // the knob: a quarter turn clockwise about the origin → a rotate op, the placement reads 90° back from the movie
                    m_Stage.Selected = "PageHeader_01";
                    var s_Knob = m_Stage.HandlePosition(StageCanvas.HandleKind.Rotate) ?? throw new Exception("no rotation knob");
                    // the placement objects are rebuilt after every op (and a drag mutates the live one): take the origin from the current one
                    var pk = P();
                    var vx = s_Knob.X - pk.X; var vy = s_Knob.Y - pk.Y;
                    m_Stage.BeginHandle(StageCanvas.HandleKind.Rotate, s_Knob);
                    m_Stage.DragHandle(new Point(pk.X - vy, pk.Y + vx), false);
                    if (!m_Stage.HandleInProgress) throw new Exception("the knob drag did not start");
                    m_Stage.EndHandle();
                    var p4 = P();
                    // a quarter turn on top of whatever the shipped placement had (BF3's menus slant some clips)
                    var s_Expected = s_Rot0 + 90; while (s_Expected > 180) s_Expected -= 360;
                    if (System.Math.Abs(p4.Rotation - s_Expected) > 0.5) throw new Exception($"knob drag: rotation {p4.Rotation:0.##}°, expected {s_Expected:0.##}° (shipped {s_Rot0:0.##}° + 90°)");
                    if (System.Math.Abs(p4.SizeX - sx0) > 0.01 || System.Math.Abs(p4.SizeY - sy0) > 0.01) throw new Exception("the turn changed the scale");
                    var s_Ops4 = DocScreen("ui/flow/screen/customizesoldierscreen")!.Stage;
                    if (!s_Ops4.Any(o => o.StartsWith("rotate:PageHeader_01:"))) throw new Exception("knob drag: no rotate op: " + string.Join(" ; ", s_Ops4));
                    if (Row("pl.rot").Value?.Type is not JTokenType.Float || System.Math.Abs((double)Row("pl.rot").Value! - s_Expected) > 0.5 || !Row("pl.rot").Modified) throw new Exception($"the Rotation row shows '{Row("pl.rot").Value}' (modified {Row("pl.rot").Modified}), expected {s_Expected:0.##} in bold");
                    Viewport.Fit(); await Task.Delay(100);
                    Photograph(Path.Combine(p_OutDir, "selftest_handles.png"));
                    Undo();
                    if (System.Math.Abs(P().Rotation - s_Rot0) > 0.01) throw new Exception("undo did not take the turn back");
                    R($"handles: SE corner ×1.5 wide (NW corner stayed), Shift → ×1.5/×1.5, W edge ×1.25 (right edge stayed, move+scale ops), knob → a quarter turn from the shipped {s_Rot0:0.##}° read back as {p4.Rotation:0.##}°; each one undo step");
                }
                m_Doc = new ScreenDocument { Name = "UiEditorSelfTest", Superbundle = "uieditorselftest/ui", Bundle = "uieditorselftest/uibundle" };
                ClearHistory();

                // the user's path: pick the screen in the list
                var s_Screen = "ui/flow/screen/customizeaccessoriesscreen";
                // the search box finds it whatever folder is selected; then it opens as a second tab
                ScreenFilter.Text = "customizeaccessories";
                var s_Found = (ScreenList.ItemsSource as System.Collections.Generic.List<ExplorerItem>)?.FirstOrDefault(i => i.Partition == s_Screen) ?? throw new Exception("the explorer search did not list " + s_Screen);
                if (s_Found.Type != "UIScreenAsset") throw new Exception($"explorer type column for {s_Screen}: '{s_Found.Type}'");
                ScreenList.SelectedItem = s_Found;
                for (var i = 0; i < 1200 && m_Current?.Partition != s_Screen && !LogBox.Text.Contains("load failed"); ++i) await Task.Delay(50);
                if (m_Current?.Partition != s_Screen) throw new Exception("screen did not load");
                ScreenFilter.Text = "";
                if (OpenScreens.Count != 2 || OpenTabs.Children.Count != 2) throw new Exception($"expected 2 open tabs, have {OpenScreens.Count}");
                // the selected explorer row is painted with OUR selection colour (the system's light grey made the text invisible):
                // probe the rendered pixel of the selected row, not the property
                {
                    var s_Sel = (ScreenList.ItemsSource as System.Collections.Generic.List<ExplorerItem>)!.First(i => i.Partition == s_Screen);
                    ScreenList.SelectedItem = s_Sel; UpdateLayout();
                    ScreenList.ScrollIntoView(s_Sel); UpdateLayout();
                    if (ScreenList.ItemContainerGenerator.ContainerFromItem(s_Sel) is not System.Windows.Controls.ListViewItem s_Container) throw new Exception("no container for the selected explorer row");
                    // RenderTargetBitmap.Render(control) paints the control shifted by its offset inside its parent (a WPF trap):
                    // photograph the whole window and probe the row's point transformed to the window instead
                    var s_Origin = s_Container.TransformToAncestor(this).Transform(new Point(0, 0));
                    var s_Shot = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    s_Shot.Render(this);
                    var s_Px = new byte[4];
                    s_Shot.CopyPixels(new Int32Rect((int)s_Origin.X + 3, (int)(s_Origin.Y + s_Container.ActualHeight / 2), 1, 1), s_Px, 4, 0);
                    if (System.Math.Abs(s_Px[2] - 61) > 14 || System.Math.Abs(s_Px[1] - 90) > 14 || System.Math.Abs(s_Px[0] - 128) > 14)
                        throw new Exception($"selected explorer row pixel = ({s_Px[2]},{s_Px[1]},{s_Px[0]}), expected our selection blue (61,90,128)");
                    R($"explorer: selected row painted ({s_Px[2]},{s_Px[1]},{s_Px[0]}) = the editor's selection colour, text stays light");
                }
                // drag & drop, through the drop handlers (the OLE gesture needs a real mouse): a widget dropped on the stage lands where it
                // was dropped; a node type dropped on the graph lands there; a component makes a DataSetNode bound to it; a popup screen
                // dropped on a FLOW GRAPH makes a DialogNode showing it (a screen refuses it, as the game's data does); a partition dropped
                // on a reference row fills it. Then every drop is undone and the document is byte-identical to before.
                {
                    var s_BeforeDrops = DocJson();
                    var s_StepsDrops = m_Undo.Count;
                    if (!HandleStageDrop(new DataObject(PartitionFormat, "ui/assets/button"), new Point(300, 200))) throw new Exception("stage drop of a widget refused");
                    var s_Dropped = m_Current!.Placements.FirstOrDefault(p => p.Name.StartsWith("Button_") && p.Added) ?? throw new Exception("dropped widget not on the stage");
                    if (System.Math.Abs(s_Dropped.X - 300) > 0.5 || System.Math.Abs(s_Dropped.Y - 200) > 0.5) throw new Exception($"dropped widget sits at stage ({s_Dropped.X},{s_Dropped.Y}), expected (300,200)");
                    if (HandleStageDrop(new DataObject(PartitionFormat, "ui/uicomponents/uicustomizationcomp"), new Point(10, 10))) throw new Exception("the stage took a component");
                    var s_Typed = HandleGraphDrop(new DataObject(NodeTypeFormat, "RefreshNode"), new Point(900, 700)) ?? throw new Exception("graph drop of a node type refused");
                    if (s_Typed.Type != "RefreshNode" || System.Math.Abs(s_Typed.GraphX - 900) > 0.5 || System.Math.Abs(s_Typed.GraphY - 700) > 0.5) throw new Exception($"dropped node {s_Typed.Type} at ({s_Typed.GraphX},{s_Typed.GraphY})");
                    var s_Comp = HandleGraphDrop(new DataObject(PartitionFormat, "ui/uicomponents/uicustomizationcomp"), new Point(950, 300)) ?? throw new Exception("graph drop of a component refused");
                    if (s_Comp.Type != "DataSetNode" || (string?)s_Comp.Fields["DataSource"]?["DataCategory"] != "ui/uicomponents/uicustomizationcomp") throw new Exception("component drop did not make a DataSetNode bound to it: " + s_Comp.Type);
                    if (HandleGraphDrop(new DataObject(PartitionFormat, "ui/flow/screen/popups/popupgeneric"), new Point(10, 10)) != null) throw new Exception("a screen took a StateNode (only flow graphs hold them)");
                    if (!PropertiesGrid.DropOnRow("f.DataSource.DataCategory", "ui/uicomponents/uisettingscomp")) throw new Exception("a component dropped on the DataCategory row was refused (rows: " + string.Join(", ", PropertiesGrid.Rows.SelectMany(r => r.Children).Select(r => r.Path).Where(p => p.StartsWith("f.")).Take(12)) + ")");
                    if ((string?)s_Comp.Fields["DataSource"]?["DataCategory"] != "ui/uicomponents/uisettingscomp") throw new Exception("the row drop did not change the DataCategory");
                    if (PropertiesGrid.DropOnRow("f.DataSource.DataCategory", "ui/assets/button")) throw new Exception("a widget asset was accepted on a UIComponentData row");
                    // a flow graph takes a screen as a StateNode / a popup as a DialogNode
                    if (!await OpenScreen("ui/flow/graph/spawn/customizationgraph")) throw new Exception("customizationgraph did not open");
                    var s_Dialog = HandleGraphDrop(new DataObject(PartitionFormat, "ui/flow/screen/popups/popupgeneric"), new Point(400, 400)) ?? throw new Exception("popup drop on a flow graph refused");
                    if (s_Dialog.Type != "DialogNode" || (string?)s_Dialog.Fields["Screen"] != "ui/flow/screen/popups/popupgeneric") throw new Exception($"popup drop made {s_Dialog.Type} / {s_Dialog.Fields["Screen"]}");
                    var s_State = HandleGraphDrop(new DataObject(PartitionFormat, "ui/flow/screen/customizesoldierscreen"), new Point(400, 600)) ?? throw new Exception("screen drop on a flow graph refused");
                    // the flow graph already holds the game's own StateNode for that screen (named "CustomizeSoldierScreen"), so ours gets the next free label
                    if (s_State.Type != "StateNode" || !s_State.InstanceName.StartsWith("CustomizeSoldierScreen") || (string?)s_State.Fields["Screen"] != "ui/flow/screen/customizesoldierscreen")
                        throw new Exception($"screen drop made {s_State.Type} '{s_State.InstanceName}' → {s_State.Fields["Screen"]}, expected a StateNode named after the asset (CustomizeSoldierScreen…) showing it");
                    if (!m_Current!.AllNodes.Any(n => n.Label == "CustomizeSoldierScreen" && n.Type == "StateNode")) throw new Exception("customizationgraph lost its shipped CustomizeSoldierScreen StateNode");
                    if (!await OpenScreen(s_Screen)) throw new Exception("could not return to the accessories screen");
                    var s_Drops = m_Undo.Count - s_StepsDrops;
                    for (var i = 0; i < s_Drops; ++i) if (!Undo()) throw new Exception("undo of a drop refused");
                    if (DocJson() != s_BeforeDrops) throw new Exception("undoing every drop did not restore the document");
                    if (m_Current!.Placements.Any(p => p.Name.StartsWith("Button_") && p.Added)) throw new Exception("the dropped widget survived the undo");
                    R($"drag & drop: widget → stage (300,200) ✓, node type → graph (900,700) ✓, component → DataSetNode ✓, component → DataCategory row ✓ (widget refused), popup → DialogNode and screen → StateNode on a flow graph ✓ (refused on a screen); {s_Drops} drops undone to the same document");
                }
                // drag & drop, end to end: what a drop must leave behind — the widget on the stage where it landed, its row in Layers
                // selected, its properties on the right and editable, every event of its contract as a port on the graph, a wire to a
                // node only where the game's data allows it (the rest refused with a reason), the panel offering the compatible wires;
                // a node type dropped on the stage goes to the graph (tab up); a widget dropped on the graph lands there as a box; a press
                // on a screen in the explorer opens it on release, not when it becomes a drag. Then the real OLE gesture with the mouse
                // when RUE_SELFTEST_MOUSE=1 (it moves the pointer).
                {
                    if (m_WidgetCatalog == null && !m_CatalogBuilding) EnsureWidgetCatalog();
                    for (var i = 0; i < 1200 && m_CatalogBuilding; ++i) await Task.Delay(100);
                    if (m_WidgetCatalog == null) throw new Exception("the widget catalogue was not built");
                    if (m_WidgetCatalog.TotalWires < 4000 || m_WidgetCatalog.WireCount("WidgetNode", "Outputs", "DataSetNode", "In") < 300)
                        throw new Exception($"the catalogue's wiring counts are off: total {m_WidgetCatalog.TotalWires}, WidgetNode.Outputs→DataSetNode.In {m_WidgetCatalog.WireCount("WidgetNode", "Outputs", "DataSetNode", "In")} (measured 4387 / 309)");
                    var s_Before2 = DocJson(); var s_Steps2 = m_Undo.Count;
                    System.Collections.Generic.IEnumerable<PropRow> Flat(System.Collections.Generic.IEnumerable<PropRow> p_Rows) => p_Rows.SelectMany(r => new[] { r }.Concat(Flat(r.Children)));
                    PropRow RowAt(string p_Path) => Flat(PropertiesGrid.Rows).FirstOrDefault(r => r.Path == p_Path) ?? throw new Exception("no property row " + p_Path + " (have: " + string.Join(", ", Flat(PropertiesGrid.Rows).Select(r => r.Path).Take(40)) + ")");
                    // a dropped widget takes the construct-time variables its shipped placements carry: that needs the widget catalogue (a fresh version rebuilds in the background)
                    for (var i = 0; i < 1800 && m_WidgetCatalog == null; ++i) await Task.Delay(100);
                    if (m_WidgetCatalog == null) throw new Exception("the widget catalogue did not come up in 180 s");
                    if (!HandleStageDrop(new DataObject(PartitionFormat, "ui/assets/button"), new Point(300, 200))) throw new Exception("stage drop refused");
                    var s_Landed = m_Current!.Placements.First(p => p.Name.StartsWith("Button_") && p.Added);
                    await Task.Delay(50);
                    var s_ButtonVars = DocScreen(s_Screen)!.Stage.FirstOrDefault(o => o.StartsWith("vars:" + s_Landed.Name + ":")) ?? throw new Exception("the dropped button got no construct-time variables (vars op): " + string.Join(" ; ", DocScreen(s_Screen)!.Stage));
                    if (!s_ButtonVars.Contains("buttonType=")) throw new Exception("the button's vars op lacks buttonType: " + s_ButtonVars);
                    if (s_Landed.ClipVars == null || s_Landed.ClipVars.All(v => v.Name != "buttonType")) throw new Exception("the rebuilt movie does not carry the dropped button's construct-time variables");
                    // Layers: the new placement is the selected row; the stage and the properties show it as the document's new node
                    var s_Rows2 = m_Current.Placements.OrderByDescending(p => p.Depth).ToList();
                    if (LayerList.SelectedIndex != s_Rows2.FindIndex(p => p.Name == s_Landed.Name)) throw new Exception($"Layers: row {LayerList.SelectedIndex} selected after the drop, expected {s_Landed.Name}'s ({s_Rows2.FindIndex(p => p.Name == s_Landed.Name)})");
                    if (m_Stage.Selected != s_Landed.Name || m_SelectedPlacement != s_Landed.Name) throw new Exception("the dropped widget is not the selection");
                    if (RowAt("f.WidgetAsset").Value?.ToString() != "ui/assets/button" || RowAt("doc.remove") == null) throw new Exception("the properties do not show the dropped widget as the document's node");
                    if (System.Math.Abs((double)RowAt("pl.x").Value! - s_Landed.LocalX) > 0.01) throw new Exception("Placement X row != the placement");
                    // values editable right after the drop: focus, scale (an op on the add), a setting the widget's code reads (+)
                    RowAt("f.FocusIndex").OnChange!(3L);
                    var s_DocButton = DocScreen(s_Screen)!.Nodes.First(n => n.InstanceName == s_Landed.Name);
                    if (s_DocButton.FocusIndex != 3) throw new Exception("FocusIndex edit after the drop did not reach the node");
                    RowAt("pl.sx").OnChange!(2.0);
                    if (System.Math.Abs(m_Current.Placements.First(p => p.Name == s_Landed.Name).ScaleX - 2.0) > 1e-6) throw new Exception("scale edit after the drop did not change the placement");
                    // a clip variable edited in the grid: the clip's vars op and the rebuilt movie follow
                    RowAt("cv.buttonType").OnChange!("largeArrowButtonFlat");
                    if (!DocScreen(s_Screen)!.Stage.Any(o => o.StartsWith("vars:" + s_Landed.Name + ":") && o.Contains("buttonType=largeArrowButtonFlat")) || m_Current.Placements.First(p => p.Name == s_Landed.Name).ClipVars?.First(v => v.Name == "buttonType").Value?.ToString() != "largeArrowButtonFlat")
                        throw new Exception("editing a clip variable in the grid did not reach the vars op and the movie");
                    var s_VarsCat = PropertiesGrid.Rows.FirstOrDefault(r => r.IsCategory && r.Name.StartsWith("Clip variables")) ?? throw new Exception("no Clip variables category for the dropped button");
                    if (!s_VarsCat.Children.Any(r => r.Path == "cv.buttonType" && r.Choices != null && r.Choices.Contains("smallTextButton"))) throw new Exception("the buttonType row does not offer the shipped values");
                    var s_SettingCat = PropertiesGrid.Rows.FirstOrDefault(r => r.IsCategory && r.Name.StartsWith("Widget settings (")) ?? throw new Exception("no widget settings for the dropped button");
                    var s_Setting = s_SettingCat.Children.FirstOrDefault(r => r.OnAdd != null) ?? throw new Exception("no addable setting for the dropped button");
                    s_Setting.OnAdd!();
                    if (!s_DocButton.Properties.ContainsKey(s_Setting.Name)) throw new Exception($"+ on the setting {s_Setting.Name} did not add it to the node");
                    // the graph: the box shows the whole contract (hollow ports), a wire to a dropped RefreshNode is allowed with the game's evidence
                    CentreTabs.SelectedIndex = 1; await Task.Delay(100);
                    var s_Box = m_Graph.Boxes.First(b => b.Label == s_Landed.Name);
                    if (!s_Box.Outputs.Any(p => p.Name == "OnItemReleased" && p.Contract) || !s_Box.Inputs.Any(p => p.Name == "Show" && p.Contract))
                        throw new Exception($"the dropped widget's box lacks its contract ports: out {string.Join(",", s_Box.Outputs.Select(p => p.Name))} in {string.Join(",", s_Box.Inputs.Select(p => p.Name))}");
                    var s_Refresh = HandleGraphDrop(new DataObject(NodeTypeFormat, "RefreshNode"), new Point(900, 700)) ?? throw new Exception("RefreshNode drop refused");
                    // every graph refresh rebuilds the boxes: take them again after the drop
                    s_Box = m_Graph.Boxes.First(b => b.Label == s_Landed.Name);
                    var s_RefreshBox = m_Graph.Boxes.First(b => b.Label == s_Refresh.InstanceName);
                    var s_OutPort = s_Box.Outputs.First(p => p.Name == "OnItemReleased");
                    m_Graph.BeginWire(s_Box, s_OutPort);
                    var s_InPort = s_RefreshBox.Inputs.First(p => p.Name == "In");
                    var s_OwnIn = s_Box.Inputs.First(p => p.Name == "Show");
                    if (!m_Graph.WireTargets.TryGetValue(s_InPort, out var s_V1) || !s_V1.Ok || s_V1.Evidence < 1) throw new Exception($"RefreshNode.In not offered as compatible for the widget event: {(m_Graph.WireTargets.TryGetValue(s_InPort, out var x) ? x.Reason : "no verdict")}");
                    if (!m_Graph.WireTargets.TryGetValue(s_OwnIn, out var s_V2) || s_V2.Ok) throw new Exception("the widget's own input counted as compatible");
                    var s_Lit = m_Graph.WireTargets.Count(kv => kv.Value.Ok); var s_Dark = m_Graph.WireTargets.Count(kv => !kv.Value.Ok);
                    GraphViewport.Fit(); await Task.Delay(100);
                    Photograph(Path.Combine(p_OutDir, "selftest_wire.png"));
                    // the dots are a few pixels at Fit: a 1:1 look at the two boxes (the lit target, the dark own inputs) — a probe needs its own render
                    GraphViewport.LookAt(new Point((s_Box.X + s_RefreshBox.X) / 2 + 95, (s_Box.Y + s_RefreshBox.Y) / 2 + 60), 1.0); await Task.Delay(100);
                    Photograph(Path.Combine(p_OutDir, "selftest_wire_zoom.png"));
                    m_Graph.EndWire(s_RefreshBox, s_InPort);
                    if (m_Graph.WireInProgress) throw new Exception("the wire did not end");
                    var s_Wire = s_DocButton.Connections.FirstOrDefault(c => c.Event == "OnItemReleased" && c.ToNode == s_Refresh.InstanceName && c.ToPort == "In") ?? throw new Exception("the wire from the contract event was not recorded: " + string.Join("; ", s_DocButton.Connections.Select(c => $"{c.Event ?? c.FromPort} -> {c.ToNode}.{c.ToEvent ?? c.ToPort}")));
                    if (!m_Graph.Boxes.First(b => b.Label == s_Landed.Name).Outputs.Any(p => p.Name == "OnItemReleased" && !p.Contract)) throw new Exception("the wired contract event did not become a solid port");
                    // refused: onto the node's own input; from an input port
                    var s_StepsRefused = m_Undo.Count;
                    m_Graph.BeginWire(m_Graph.Boxes.First(b => b.Label == s_Landed.Name), m_Graph.Boxes.First(b => b.Label == s_Landed.Name).Outputs.First(p => p.Name == "OnItemReleased"));
                    m_Graph.EndWire(m_Graph.Boxes.First(b => b.Label == s_Landed.Name), m_Graph.Boxes.First(b => b.Label == s_Landed.Name).Inputs.First(p => p.Name == "Show"));
                    if (m_Undo.Count != s_StepsRefused || s_DocButton.Connections.Count != 1) throw new Exception("a wire onto the node's own input was recorded");
                    if (!LogBox.Text.Contains("refused: a node never wires to itself")) throw new Exception("the refused self-wire was not logged with its reason");
                    s_RefreshBox = m_Graph.Boxes.First(b => b.Label == s_Refresh.InstanceName);
                    OnWireRequested(s_RefreshBox, "In", "In", m_Graph.Boxes.First(b => b.Label == s_Landed.Name), "Inputs", "Show");
                    if (s_Refresh.Connections.Count != 0 || !LogBox.Text.Contains("refused: In receives")) throw new Exception("a wire out of an input port was not refused");
                    // the panel offers the compatible wires (the game's most-wired pairs first), never one to the node itself
                    var s_Offers = CompatibleTargets(s_Landed.Name);
                    if (!s_Offers.Contains($"OnItemReleased -> {s_Refresh.InstanceName}.In")) throw new Exception("the compatible targets do not include the RefreshNode: " + string.Join(" | ", s_Offers.Take(8)));
                    if (s_Offers.Any(o => o.Contains("-> " + s_Landed.Name + "."))) throw new Exception("the compatible targets offer a wire to the node itself");
                    if (!s_Offers[0].Contains("->") || s_Offers[0].Contains(s_Landed.Name + ".")) throw new Exception("first offer: " + s_Offers[0]);
                    m_SelectedNode = s_Landed.Name; m_SelectedPlacement = s_Landed.Name; m_SelectedKey = null; ShowProperties();
                    var s_ConnRow = RowAt("doc.conn");
                    if (s_ConnRow.OnAdd == null || s_ConnRow.Children.Count != 1 || s_ConnRow.Children[0].Choices == null || !s_ConnRow.Children[0].Choices!.Contains($"OnItemReleased -> {s_Refresh.InstanceName}.In")) throw new Exception("the Connections rows do not offer the compatible targets");
                    s_ConnRow.OnAdd();
                    if (s_DocButton.Connections.Count != 2) throw new Exception("+ on Connections did not add the first compatible wire");
                    // Alt+click on a wire disconnects it: a document wire leaves its node's connections; a shipped wire is listed as removed on
                    // the screen (the mod erases it at load) and disappears from the graph; undo brings each back, the Screen row's x too
                    Point? AimAt(GraphView.Wire w) { foreach (var t in new[] { 0.5, 0.35, 0.65, 0.2, 0.8 }) { var q = m_Graph.PointOn(w, t); if (q != null && m_Graph.WireAt(q.Value) == w) return q; } return null; }
                    var s_DocWire = m_Graph.Wires.FirstOrDefault(w => w.IsDoc && w.DocConn == s_Wire) ?? throw new Exception("the dragged wire is not on the graph as a document wire");
                    var s_AimDoc = AimAt(s_DocWire) ?? throw new Exception("no point on the document wire hits it");
                    var s_StepsAlt = m_Undo.Count;
                    if (m_Graph.DisconnectAt(s_AimDoc) != s_DocWire) throw new Exception("Alt+click did not hit the document wire");
                    if (s_DocButton.Connections.Contains(s_Wire) || m_Undo.Count != s_StepsAlt + 1 || m_Graph.Wires.Any(w => w.IsDoc && w.DocConn == s_Wire)) throw new Exception("Alt+click did not take the document wire out in one step");
                    Undo();
                    s_DocButton = DocScreen(s_Screen)!.Nodes.First(n => n.InstanceName == s_Landed.Name);
                    if (s_DocButton.Connections.Count != 2) throw new Exception("undo did not bring the document wire back");
                    var s_ShippedPick = m_Graph.Wires.Where(w => !w.IsDoc && w.Resolved && w.Guid != "").OrderByDescending(w => m_Graph.Boxes.First(b => b.Key == w.To).Label == "Confirm").Select(w => (Wire: w, At: AimAt(w))).FirstOrDefault(x => x.At != null);
                    var s_ShippedWire = s_ShippedPick.Wire ?? throw new Exception("no shipped wire can be aimed at");
                    var s_ShippedCount = m_Graph.Wires.Count(w => !w.IsDoc);
                    var s_StepsShipped = m_Undo.Count;
                    if (m_Graph.DisconnectAt(s_ShippedPick.At!.Value) != s_ShippedWire) throw new Exception("Alt+click did not hit the shipped wire");
                    var s_RemovedList = DocScreen(s_Screen)!.RemovedConnections;
                    if (s_RemovedList.Count != 1 || !string.Equals(s_RemovedList[0].Guid, s_ShippedWire.Guid, StringComparison.OrdinalIgnoreCase) || !s_RemovedList[0].From.Contains('.') || m_Undo.Count != s_StepsShipped + 1) throw new Exception($"the shipped wire was not listed as removed in one step: {s_RemovedList.Count} entries");
                    if (m_Graph.Wires.Count(w => !w.IsDoc) != s_ShippedCount - 1 || m_Graph.RemovedWires != 1 || m_Graph.Wires.Any(w => w.Guid == s_ShippedWire.Guid)) throw new Exception("the removed shipped wire is still drawn");
                    if (!GraphTitle.Text.Contains("1 disconnected by the document")) throw new Exception("the graph title does not count the disconnected wire: " + GraphTitle.Text);
                    ShowProperties();
                    var s_RemovedRow = RowAt("scr.removed");
                    if (s_RemovedRow.Children.Count != 1 || s_RemovedRow.Children[0].OnRemove == null || s_RemovedRow.Children[0].Name != s_RemovedList[0].From) throw new Exception("the Screen ▸ Removed connections row does not list the wire with an x");
                    s_RemovedRow.Children[0].OnRemove!();
                    if (DocScreen(s_Screen)!.RemovedConnections.Count != 0 || m_Graph.Wires.Count(w => !w.IsDoc) != s_ShippedCount) throw new Exception("x on the removed connection did not put the wire back");
                    Undo();
                    if (DocScreen(s_Screen)!.RemovedConnections.Count != 1 || m_Graph.RemovedWires != 1) throw new Exception("undo did not re-remove the wire");
                    Undo();
                    if (DocScreen(s_Screen)!.RemovedConnections.Count != 0 || m_Graph.Wires.Count(w => !w.IsDoc) != s_ShippedCount) throw new Exception("undo did not bring the shipped wire back");
                    R($"Alt+click: document wire {s_DocWire.Label} disconnected (1 step) and undone; shipped wire {s_ShippedWire.Label} → removedConnections[{s_ShippedWire.Guid}] (1 step), gone from the graph ({s_ShippedCount - 1} of {s_ShippedCount} shipped wires drawn), x in Screen ▸ Removed connections put it back, 2 undos consistent");
                    // multi-selection on the graph: Ctrl+click adds boxes (the stage mirrors the widgets among them), a band selects the boxes inside
                    // it, a move takes the lot in one step, Delete takes the document nodes of the set in one step
                    var s_Step = HandleGraphDrop(new DataObject(NodeTypeFormat, "DataStepNode"), new Point(900, 1000)) ?? throw new Exception("DataStepNode drop refused");
                    m_Graph.Select(s_Refresh.InstanceName);
                    m_Graph.SelectToggle(m_Graph.Boxes.First(b => b.Label == s_Step.InstanceName));
                    m_Graph.SelectToggle(m_Graph.Boxes.First(b => b.Label == s_Landed.Name));
                    if (m_Graph.SelectedLabels.Count != 3 || m_Stage.SelectedSet.Count != 1 || !m_Stage.SelectedSet.Contains(s_Landed.Name)) throw new Exception($"Ctrl+click on the graph: {m_Graph.SelectedLabels.Count} boxes selected (expected 3), stage mirrors {string.Join(",", m_Stage.SelectedSet)} (expected {s_Landed.Name})");
                    // the three selected boxes (orange borders) photographed at 1:1 around them
                    GraphViewport.LookAt(new Point((m_Graph.Boxes.First(b => b.Label == s_Refresh.InstanceName).X + m_Graph.Boxes.First(b => b.Label == s_Step.InstanceName).X) / 2 + 95, (m_Graph.Boxes.First(b => b.Label == s_Refresh.InstanceName).Y + m_Graph.Boxes.First(b => b.Label == s_Step.InstanceName).Y) / 2 + 60), 0.8); await Task.Delay(100);
                    Photograph(Path.Combine(p_OutDir, "selftest_multigraph.png"));
                    var s_RefreshDoc = DocScreen(s_Screen)!.Nodes.First(n => n.InstanceName == s_Refresh.InstanceName); var s_StepDoc = DocScreen(s_Screen)!.Nodes.First(n => n.InstanceName == s_Step.InstanceName);
                    var s_Rx0 = s_RefreshDoc.GraphX; var s_Sx0 = s_StepDoc.GraphX; var s_Bx0 = m_Graph.Boxes.First(b => b.Label == s_Landed.Name).X;
                    var s_StepsGraphMove = m_Undo.Count;
                    m_Graph.MoveSelectedBy(25, 15);
                    if (m_Undo.Count != s_StepsGraphMove + 1) throw new Exception("moving three boxes together is not one undo step");
                    if (System.Math.Abs(s_RefreshDoc.GraphX - s_Rx0 - 25) > 0.5 || System.Math.Abs(s_StepDoc.GraphX - s_Sx0 - 25) > 0.5 || System.Math.Abs(m_Graph.Boxes.First(b => b.Label == s_Landed.Name).X - s_Bx0 - 25) > 0.5) throw new Exception("the three boxes did not move together");
                    if (m_Graph.SelectedLabels.Count != 3) throw new Exception("the graph selection did not survive the move");
                    Undo();
                    if (System.Math.Abs(DocScreen(s_Screen)!.Nodes.First(n => n.InstanceName == s_Refresh.InstanceName).GraphX - s_Rx0) > 0.5) throw new Exception("undo did not put the boxes back");
                    var s_BandG = Rect.Union(GraphView.RectOf(m_Graph.Boxes.First(b => b.Label == s_Refresh.InstanceName)), GraphView.RectOf(m_Graph.Boxes.First(b => b.Label == s_Step.InstanceName)));
                    s_BandG.Inflate(8, 8);
                    m_Graph.SelectInBand(s_BandG);
                    if (!m_Graph.SelectedLabels.Contains(s_Refresh.InstanceName) || !m_Graph.SelectedLabels.Contains(s_Step.InstanceName)) throw new Exception("the band did not select the boxes inside it: " + string.Join(",", m_Graph.SelectedLabels));
                    if (m_Graph.Boxes.Any(b => m_Graph.SelectedKeys.Contains(b.Key) && !s_BandG.Contains(GraphView.RectOf(b)))) throw new Exception("the band selected a box outside it");
                    m_Graph.Select(s_Refresh.InstanceName); m_Graph.SelectToggle(m_Graph.Boxes.First(b => b.Label == s_Step.InstanceName));
                    var s_StepsGraphDel = m_Undo.Count;
                    if (!DeleteSelected() || DocScreen(s_Screen)!.Nodes.Any(n => n.InstanceName == s_Refresh.InstanceName || n.InstanceName == s_Step.InstanceName) || m_Undo.Count != s_StepsGraphDel + 1) throw new Exception("Delete on two selected boxes did not take both in one step");
                    Undo();
                    if (!DocScreen(s_Screen)!.Nodes.Any(n => n.InstanceName == s_Refresh.InstanceName) || !DocScreen(s_Screen)!.Nodes.Any(n => n.InstanceName == s_Step.InstanceName)) throw new Exception("undo did not bring the two boxes back");
                    R($"graph multi-selection: Ctrl+click → 3 boxes (stage mirrors {s_Landed.Name}), moved together (+25,+15) in 1 step and undone, band around {s_Refresh.InstanceName}+{s_Step.InstanceName} selects them, Delete took both in 1 step, undo brought them back");
                    // the static text: typed in the Static text row it becomes a UITextDataBinding on the node and shows on the stage
                    CentreTabs.SelectedIndex = 0;
                    var s_TextField = HandleStageDrop(new DataObject(PartitionFormat, "ui/assets/textfield"), new Point(500, 400)) ? m_Current.Placements.First(p => p.Name.StartsWith("TextField_") && p.Added) : throw new Exception("textfield drop refused");
                    // the one that cost an in-game round: a TextField attaches "mc_" + m_rowType in its constructor — the dropped one carries it
                    if (s_TextField.ClipVars == null || !s_TextField.ClipVars.Any(v => v.Name == "m_rowType" && (string?)v.Value == "bold1") || !s_TextField.ClipVars.Any(v => v.Name == "m_align"))
                        throw new Exception("the dropped text field lacks its construct-time variables (m_rowType=bold1, m_align…): " + (s_TextField.ClipVars == null ? "(none)" : GfxMovie.FormatClipVars(s_TextField.ClipVars)));
                    RowAt("f.StaticText").OnChange!("I hate burgers");
                    var s_TfDoc = DocScreen(s_Screen)!.Nodes.First(n => n.InstanceName == s_TextField.Name);
                    if (s_TfDoc.Fields.TryGetValue("DataBinding", out var s_TfBind) && s_TfBind is JObject s_TfObj && (string?)s_TfObj["$type"] == "UITextDataBinding" && (string?)s_TfObj["StaticText"] == "I hate burgers" && (bool?)s_TfObj["OverrideDirectAccess"] == true) { }
                    else throw new Exception("the static text did not make a UITextDataBinding on the node: " + (s_TfDoc.Fields.TryGetValue("DataBinding", out var x2) ? x2.ToString(Newtonsoft.Json.Formatting.None) : "(none)"));
                    if (!Renderer().TextPaths.Any(t => t.Contains("/" + s_TextField.Name + "/"))) throw new Exception($"the text field's text clip was not drawn; {Renderer().TextPaths.Count} text paths: {string.Join(" | ", Renderer().TextPaths.Where(t => t.Contains("TextField") || t.Contains("preview")).Take(8))}; imports {Renderer().ImportsResolved} resolved / {Renderer().ImportsMissing} missing; warnings: {string.Join(" | ", Renderer().Warnings.Take(6))}; placement bounds empty={s_TextField.Bounds.Empty}");
                    if (TextOverride(StaticTexts(), "instance1/" + s_TextField.Name + "/bold1/txtDisplay") != "I hate burgers") throw new Exception("the static text does not reach the text field's clip on the stage");
                    // what the game draws for it (TextField.as): the movie's mc_<m_rowType> row — bold1 = $Menu_bold 20 px — with its text field
                    // sized to the widget's placed box (422x42 at scale 1). Not the movie's previewInstance (the game's own editor preview,
                    // $Menu_bold 32 px) the stage drew before: "big in the editor, small in the game"
                    GfxRenderer.DrawnText TfRow() => Renderer().DrawnTexts.FirstOrDefault(d => d.Path.Contains("/" + s_TextField.Name + "/bold1/")) ?? throw new Exception("the text field's bold1 row was not drawn; drawn: " + string.Join(" | ", Renderer().DrawnTexts.Select(d => $"{d.Path}=\"{d.Text}\"@{d.SizePx}")));
                    var s_TfRow = TfRow();
                    if (s_TfRow.Text != "I hate burgers" || System.Math.Abs(s_TfRow.SizePx - 20) > 0.01 || System.Math.Abs(s_TfRow.Box.Width - 422) > 0.5 || System.Math.Abs(s_TfRow.Box.Height - 42) > 0.5 || !s_TfRow.Font.Contains("Purista", StringComparison.OrdinalIgnoreCase))
                        throw new Exception($"the bold1 row is drawn as \"{s_TfRow.Text}\" {s_TfRow.SizePx}px {s_TfRow.Font} in {s_TfRow.Box.Width}x{s_TfRow.Box.Height}; the game shows $Menu_bold (Purista EA Semibold) 20 px in the widget's 422x42 box");
                    if (Renderer().DrawnTexts.Any(d => d.Path.Contains("/" + s_TextField.Name + "/previewInstance/"))) throw new Exception("the 32 px previewInstance placeholder is still drawn for the text field");
                    // the text typed through the DataBinding instance (StaticText inside it) must redraw the stage too — it used to change the
                    // document and leave the art as it was; the art is compared by pixels, not by the model
                    var s_ArtA = ArtHash();
                    RowAt("f.DataBinding.StaticText").OnChange!("I HATE PEOPLE");
                    var s_TfRow2 = TfRow();
                    if (s_TfRow2.Text != "I HATE PEOPLE") throw new Exception($"StaticText typed in the DataBinding instance did not redraw the stage: the row still says \"{s_TfRow2.Text}\"");
                    var s_ArtB = ArtHash();
                    if (s_ArtA == s_ArtB) throw new Exception("the stage art did not change after the text changed (same pixels)");
                    if ((string?)DocScreen(s_Screen)!.Nodes.First(n => n.InstanceName == s_TextField.Name).Fields["DataBinding"]?["StaticText"] != "I HATE PEOPLE") throw new Exception("the DataBinding instance edit did not reach the document");
                    // scaling the placement ×2: the game doubles the text box, not the font (it resets the widget's scale and resizes the field)
                    RowAt("pl.sx").OnChange!(2.0);
                    RowAt("pl.sy").OnChange!(2.0);
                    var s_TfRow3 = TfRow();
                    if (System.Math.Abs(s_TfRow3.SizePx - 20) > 0.01 || System.Math.Abs(s_TfRow3.Box.Width - 844) > 1 || System.Math.Abs(s_TfRow3.Box.Height - 84) > 1) throw new Exception($"scaled ×2 the row is {s_TfRow3.SizePx}px in {s_TfRow3.Box.Width}x{s_TfRow3.Box.Height}; the game keeps 20 px and gives the field an 844x84 box");
                    // … so a left-aligned text that fits does not move a pixel (the old preview doubled it); centred, it moves inside the wider box
                    var s_ArtC = ArtHash();
                    if (s_ArtC != s_ArtB) throw new Exception("scaling the text field ×2 changed its left-aligned text: the game only grows the box");
                    RowAt("cv.m_align").OnChange!("center");
                    if (ArtHash() == s_ArtC) throw new Exception("m_align=center did not move the text inside the 844 px box");
                    Undo(); Undo(); Undo();
                    if (System.Math.Abs(TfRow().Box.Width - 422) > 0.5 || ArtHash() != s_ArtB) throw new Exception("3 undos did not bring the text box back to 422 px, left-aligned");
                    RowAt("f.StaticText").OnChange!("I hate burgers");
                    R($"in-game text: {s_TextField.Name} draws its bold1 row ($Menu_bold 20 px in a 422x42 box, ×2 → 844x84 and still 20 px), the StaticText typed inside the DataBinding instance redraws the stage (pixels changed)");
                    // the live preview: the screen with the document's edits converted for a Flash player — self-contained widgets (no imports
                    // left, fonts inlined), the screen with its prologue (wait, registerClass) and epilogue (initialize with the static texts),
                    // the font library under the movies' aliases, the shell — and served over 127.0.0.1
                    {
                        var s_RuffleDir = Path.Combine(p_OutDir, "ruffle");
                        var s_Conv = RufflePreview.Convert(m_Rime, m_Settings.Language, m_Current.MovieName, DocScreen(s_Screen), s_RuffleDir, t => Log("[ruffle] " + t), m_Doc.Movies);
                        if (s_Conv.Failed != 0) throw new Exception($"ruffle conversion: {s_Conv.Failed} movie(s) failed");
                        var s_ScreenSwf = GfxMovie.Load(Path.Combine(s_RuffleDir, "ui", "assets", "customizeaccessoriesscreen.swf"));
                        if (s_ScreenSwf.FrameCount != 3) throw new Exception($"the converted screen has {s_ScreenSwf.FrameCount} frames, expected wait + registrations + its own (the epilogue closes it)");
                        var s_Scripts = string.Join("\n", s_ScreenSwf.Tags.Where(t => t.Code == 12).Select(t => System.Text.Encoding.Latin1.GetString(t.Body)));
                        if (!s_Scripts.Contains("registerClass") || !s_Scripts.Contains($"{s_TextField.Name}.initialize(Text=I hate burgers)") || !s_Scripts.Contains("PageHeader_01.initialize(Header=CUSTOMIZE"))
                            throw new Exception("the converted screen lacks the class registrations or the initialize calls with the static texts");
                        var s_Kit = GfxMovie.Load(Path.Combine(s_RuffleDir, "ui", "assets", "kitselector.swf"));
                        if (s_Kit.Imports().Any()) throw new Exception("kitselector.swf still imports: " + string.Join(", ", s_Kit.Imports().Select(i => i.Url)));
                        if (!s_Kit.Characters().Values.Any(t => t.Code == 75)) throw new Exception("kitselector.swf has no inlined glyph font");
                        if (!s_Kit.Characters().Values.Any(t => t.Code == 36)) throw new Exception("kitselector.swf has no atlas bitmap");
                        var s_FontLib = GfxMovie.Load(Path.Combine(s_RuffleDir, "ui", "static", "gfxfontlib.swf"));
                        if (!s_FontLib.Exports().SelectMany(e => e.Characters).Any(c => string.Equals(c.Name, "$Menu_bold", StringComparison.OrdinalIgnoreCase))) throw new Exception("the font library does not export $Menu_bold");
                        // the recorder rides in the converted screen too (its echo is how the preview's init is verified below)
                        if (!DataRecorder.Available) throw new Exception("Data/UiRecorder.avm1 is not next to the editor");
                        if (!DataRecorder.IsInjected(s_ScreenSwf)) throw new Exception("the converted screen does not carry the recorder action");
                        // Record data…: the recorder mod for this screen — the shipped movie with the recorder in front of its frame, receiver, delivery, mod.json
                        {
                            var s_RecLog = new StringWriter();
                            // the mod folder now (its superbundle needs the mount, which comes later by the user's own button: built and checked there)
                            var s_Mod = BuildRecorderMod(m_Current.MovieName, s_RecLog, m_Rime.IsMounted);
                            Log(s_RecLog.ToString());
                            var s_Recorded = GfxMovie.Load(s_Mod.MoviePath);
                            if (!DataRecorder.IsInjected(s_Recorded)) throw new Exception("the recorder mod's movie has no recorder action");
                            var s_Vanilla = GfxMovie.Load(m_Rime.ResourceBytes(m_Current.MovieName));
                            if (s_Recorded.Tags.Count != s_Vanilla.Tags.Count + 1) throw new Exception($"the recorder mod's movie has {s_Recorded.Tags.Count} root tags, the shipped one {s_Vanilla.Tags.Count}: only one DoAction may be added");
                            var s_Inserted = s_Recorded.Tags.FindIndex(t => t.Code == 12 && System.Text.Encoding.Latin1.GetString(t.Body).Contains(DataRecorder.Marker));
                            if (s_Inserted < 0 || s_Recorded.Tags[s_Inserted + 1].Code != GfxMovie.TagShowFrame) throw new Exception("the recorder action is not right in front of the screen's ShowFrame");
                            for (int i = 0, j = 0; i < s_Vanilla.Tags.Count; ++i, ++j) { if (j == s_Inserted) ++j; if (!s_Vanilla.Tags[i].Emit().SequenceEqual(s_Recorded.Tags[j].Emit())) throw new Exception($"root tag {i} of the recorder mod's movie differs from the shipped one"); }
                            foreach (var s_File in new[] { "mod.json", Path.Combine("ext", "Shared", "__init__.lua"), Path.Combine("ext", "Client", "__init__.lua"), Path.Combine("ext", "Server", "__init__.lua") })
                                if (!File.Exists(Path.Combine(s_Mod.ModDir, s_File))) throw new Exception($"the recorder mod lacks {s_File}");
                            var s_Receiver = File.ReadAllText(Path.Combine(s_Mod.ModDir, "ext", "Client", "__init__.lua"));
                            if (!s_Receiver.Contains("UI:EnableTypingMode") || !s_Receiver.Contains("[UIREC]") || !s_Receiver.Contains(m_Current.MovieName.Split('/').Last())) throw new Exception("the receiver does not decode the channel / name the screen");
                            // F5 (or uirecdump) sends the decoded records to the server, which keeps them in the mod's database: the console's limit no longer matters
                            if (!s_Receiver.Contains("InputDeviceKeys.IDK_F5") || !s_Receiver.Contains("NetEvents:Send('RimeUiRecorder:Record'") || !s_Receiver.Contains("NetEvents:Send('RimeUiRecorder:Flush'") || !s_Receiver.Contains("Console:Register('uirecdump'")) throw new Exception("the receiver lacks the F5 / uirecdump dump to the server");
                            var s_ServerSide = File.ReadAllText(Path.Combine(s_Mod.ModDir, "ext", "Server", "__init__.lua"));
                            if (!s_ServerSide.Contains("NetEvents:Subscribe('RimeUiRecorder:Record'") || !s_ServerSide.Contains("SQL:Open()") || !s_ServerSide.Contains("INSERT OR REPLACE INTO uirec") || !s_ServerSide.Contains("NetEvents:SendTo('RimeUiRecorder:Saved'")) throw new Exception("the server side does not keep the records in the mod's database");
                            if (!File.ReadAllText(Path.Combine(s_Mod.ModDir, "mod.json")).Contains("mod.db")) throw new Exception("mod.json does not tell where the records end up");
                            if (!File.ReadAllText(Path.Combine(s_Mod.ModDir, "ext", "Shared", "__init__.lua")).Contains(DataRecorder.Superbundle)) throw new Exception("the delivery does not mount the recorder's superbundle");
                            R($"Record data…: {s_Mod.ModDir} — shipped movie + one DoAction ({DataRecorder.Action().Length} B of recorder) before its ShowFrame, every other root tag byte-identical; receiver + delivery + mod.json{(s_Mod.Built ? "; superbundle built" : "")}");
                        }
                        // Import recording…: the console's "[AS2] #R…" lines (some joined without a newline, as a paste can be) → the screen's fixture
                        {
                            var s_Record = new JObject
                            {
                                ["screen"] = "selftestscreen", ["root"] = "instance1", ["run"] = 3, ["kind"] = "init", ["seq"] = 0, ["captured"] = true,
                                ["widgets"] = new JObject { ["0"] = new JObject { ["InstanceName"] = "KitSelector_05", ["HasFocus"] = false, ["NumEvents"] = 2, ["Event_0"] = 22, ["Event_1"] = 23, ["Align"] = new JObject { ["0"] = 0, ["1"] = 0, ["length"] = 2 }, ["ZDepthLevel"] = 0, ["data"] = new JObject { ["Setup"] = new JObject { ["Items"] = new JObject { ["0"] = new JObject { ["Label"] = "ID_P_ANAME_NOCAMO", ["ItemImage"] = "x" }, ["length"] = 1 } } } }, ["1"] = new JObject { ["InstanceName"] = "TextField_01", ["NumEvents"] = 0, ["data"] = new JObject { ["Text"] = "Ünïcödé \"quoted\" #R|fake" } }, ["length"] = 2 },
                                ["getData"] = new JObject { ["-1791838705"] = new JObject { ["value"] = 1 } },
                                ["texts"] = new JObject { ["ID_M_BACK"] = "BACK" },
                            };
                            var s_RefreshRec = new JObject { ["screen"] = "selftestscreen", ["root"] = "instance1", ["run"] = 3, ["kind"] = "refresh", ["seq"] = 1, ["captured"] = true, ["list"] = new JArray(new JObject { ["widgetName"] = "KitSelector_05", ["widgetData"] = new JObject { ["Setup"] = new JObject { ["Items"] = new JObject { ["length"] = 0 } } } }), ["getData"] = new JObject(), ["texts"] = new JObject() };
                            string Console(string p_Rid, JObject p_Json, bool p_Newlines)
                            {
                                var s_Text = p_Json.ToString(Newtonsoft.Json.Formatting.None).Replace("Ü", "\\u00dc").Replace("ï", "\\u00ef").Replace("ö", "\\u00f6").Replace("é", "\\u00e9");
                                var s_Total = (s_Text.Length + 179) / 180; var s_Out = new System.Text.StringBuilder();
                                for (var i = 0; i < s_Total; ++i) { var s_Piece = s_Text.Substring(i * 180, System.Math.Min(180, s_Text.Length - i * 180)); s_Out.Append($"[02:14:{i % 60:00}] [info] [VeniceEXT] [rimeuirecorder] [AS2] #R{p_Rid}|{i + 1}|{s_Total}|{s_Piece.Length}|{s_Piece}{(p_Newlines ? "\n" : "")}"); }
                                return s_Out.ToString();
                            }
                            var s_PastedConsole = "Connecting to frostbite server at 192.168.0.10:25200.[02:14:00] [info] [VeniceEXT] [rimeuirecorder] [UIREC] armed for selftestscreen\n" + Console("3.i0", s_Record, true) + Console("3.r1", s_RefreshRec, false) + "\n[02:14:09] [info] [VeniceEXT] [rimeuirecorder] [AS2] #R3.r2|1|2|180|" + new string('x', 100) + "\n";
                            var s_ImportReport = new DataRecorder.ImportReport();
                            var s_Parsed = DataRecorder.Parse(s_PastedConsole, s_ImportReport) ?? throw new Exception("the pasted console did not parse: " + s_ImportReport);
                            if (s_Parsed.Screen != "selftestscreen" || s_Parsed.Run != 3 || s_Parsed.Widgets.Count != 2 || s_Parsed.Refreshes.Count != 1 || !s_Parsed.Captured) throw new Exception($"parsed {s_Parsed.Summary}, expected 2 widgets and 1 refresh of run 3 ({s_ImportReport})");
                            if ((string?)s_Parsed.InitRecord("TextField_01")?["data"]?["Text"] != "Ünïcödé \"quoted\" #R|fake") throw new Exception("the escaped text did not come back: " + s_Parsed.InitRecord("TextField_01")?["data"]?["Text"]);
                            if (s_Parsed.DataValue("-1791838705")?.ToString() != "1" || (string?)s_Parsed.Texts["ID_M_BACK"] != "BACK") throw new Exception("the captured answers did not come back");
                            if (s_Parsed.RefreshPayload("KitSelector_05", "Setup") == null || s_ImportReport.Truncated != 1 || s_ImportReport.Incomplete.Count != 1) throw new Exception($"refresh payload / the cut frame were not accounted for: {s_ImportReport}");
                            var s_Imported = ImportRecordingText(s_PastedConsole) ?? throw new Exception("Import recording refused the pasted console");
                            var s_FixturePath = DataRecorder.FixturePath(EditorSettings.DefaultPreviewRoot(), "selftestscreen");
                            var s_Reloaded = DataRecorder.Recording.Load(s_FixturePath) ?? throw new Exception("the fixture was not written: " + s_FixturePath);
                            if (s_Reloaded.ToJson().ToString() != s_Imported.ToJson().ToString()) throw new Exception("the fixture does not reload byte-identical");
                            // a walk through two screens in one database (a recorder mod that ships both): each screen gets its own fixture, the one recorded last comes first
                            var s_OtherRec = new JObject { ["screen"] = "selftestother", ["root"] = "instance1", ["run"] = 2, ["kind"] = "init", ["seq"] = 0, ["captured"] = false, ["widgets"] = new JObject { ["0"] = new JObject { ["InstanceName"] = "Button_01", ["NumEvents"] = 0, ["data"] = new JObject() }, ["length"] = 1 }, ["getData"] = new JObject(), ["texts"] = new JObject() };
                            var s_WalkReport = new DataRecorder.ImportReport();
                            var s_Walk = DataRecorder.ImportAll(Console("2.i0", s_OtherRec, true) + s_PastedConsole, EditorSettings.DefaultPreviewRoot(), s_WalkReport);
                            if (s_Walk.Count != 2 || s_Walk[0].Recording.Screen != "selftestscreen" || s_Walk[1].Recording.Screen != "selftestother" || s_Walk[1].Recording.Widgets.Count != 1 || s_Walk[1].Recording.Run != 2) throw new Exception($"a two-screen walk did not import as two recordings: {string.Join(", ", s_Walk.Select(w => w.Recording.Screen + " run " + w.Recording.Run))} ({s_WalkReport})");
                            if (DataRecorder.Recording.Load(DataRecorder.FixturePath(EditorSettings.DefaultPreviewRoot(), "selftestother"))?.Screen != "selftestother") throw new Exception("the second screen's fixture was not written");
                            File.Delete(DataRecorder.FixturePath(EditorSettings.DefaultPreviewRoot(), "selftestother"));
                            R($"Import recording…: {s_ImportReport} → {s_FixturePath} ({s_Reloaded.Summary}); escapes, a pasted line join, a cut frame and an incomplete record handled; a two-screen walk lands in two fixtures");
                        }
                        using var s_Server = new RufflePreview.Server(s_RuffleDir);
                        using var s_Http = new System.Net.Http.HttpClient();
                        var s_Shell = await s_Http.GetByteArrayAsync(s_Server.Url + s_Conv.ShellPath);
                        if (!s_Shell.SequenceEqual(File.ReadAllBytes(Path.Combine(s_RuffleDir, "ui", "assets", "preview.swf")))) throw new Exception("the preview server did not serve the shell as written");
                        var s_Missing = await s_Http.GetAsync(s_Server.Url + "ui/assets/nothere.swf");
                        if (s_Missing.StatusCode != System.Net.HttpStatusCode.NotFound) throw new Exception("the preview server answered a missing file with " + s_Missing.StatusCode);
                        R($"Ruffle preview: {s_Conv.Written} movies converted ({s_Conv.ShellPath}: 4-frame screen with registerClass + initialize of the static texts; kitselector self-contained with font and atlas; $Menu_bold exported), served at {s_Server.Url}");
                        if (!File.Exists(Path.Combine(s_RuffleDir, "ui", "assets", "bridge.swf")) || !File.Exists(Path.Combine(s_RuffleDir, "host.html")) || !File.Exists(Path.Combine(s_RuffleDir, "ruffle", "ruffle.js")))
                            throw new Exception("the bridge movie, the host page or the Ruffle web player are missing from the preview folder");
                        // the widgets' own classes (Data/widget_settings.json): a movie that registers helper classes after its own keeps its own —
                        // kitinfobox binds "label" to Util.ScrollText last, and the generator used to keep that when the widget's class had no settings
                        var s_WidgetSettings = RimeLib.Cmd.UiBuilder.WidgetSettings.Load() ?? throw new Exception("Data/widget_settings.json not found");
                        if (s_WidgetSettings.Widgets["kitinfobox"].Class != "Widget.CustomizeKit.KitInfoBox" || s_WidgetSettings.Widgets["list"].Class != "Widget.List.List")
                            throw new Exception($"widget_settings.json names the wrong classes: kitinfobox → {s_WidgetSettings.Widgets["kitinfobox"].Class}, list → {s_WidgetSettings.Widgets["list"].Class}");
                        R("widget settings: kitinfobox → Widget.CustomizeKit.KitInfoBox, list → Widget.List.List (the widget's own class, not the helper registered after it)");
                        if (Environment.GetEnvironmentVariable("RUE_SELFTEST_RUFFLE") == "1")
                        {
                            // opt-in: the real button path — the in-editor player (WebView2) on the secondary monitor; the bridge must announce
                            // itself (the game's components are proxied) and a call dispatched into the movie must come back through it
                            // the first preview runs on generated data alone: a recording of this screen left by an earlier run (the second
                            // preview below writes one) would feed the widgets instead — the strings of an earlier player stood in for the
                            // generated ones for three runs before this line
                            var s_StaleFixture = DataRecorder.FixturePath(EditorSettings.DefaultPreviewRoot(), m_Current.MovieName);
                            var s_Url = await StartPreviewForSeam(p_OutDir);
                            if (m_UiHost == null || m_RuffleHost == null) throw new Exception("no host after the preview");
                            if (m_UiHost.WidgetsInitialised < 10) throw new Exception($"the host initialised {m_UiHost.WidgetsInitialised} widgets, expected the screen's 10 plus the document's");
                            if (!m_UiHost.ParentGraph.EndsWith("customizationgraph", StringComparison.OrdinalIgnoreCase)) throw new Exception($"the host resolved the flow graph '{m_UiHost.ParentGraph}', expected ui/flow/graph/spawn/customizationgraph");
                            if (!LogBox.Text.Contains("entering customizeaccessoriesscreen: EnterScreen")) throw new Exception("the host did not enter the screen through the StateNode's EnterScreen port");
                            // the focus: the screen's entry highlights the first row (OnItemHighlighted → activate → requestFocus) and the host, as the
                            // engine's focus manager, hands it onFocused(false)
                            for (var i = 0; i < 100 && !LogBox.Text.Contains("focus on customizeaccessoriesscreen: KitSelector_01 "); ++i) await Task.Delay(50);
                            // the first row asks for the focus while its group (0) holds it already: the engine calls nothing (the screen's init gave the group onFocused(true)) — the host the same
                            if (!LogBox.Text.Contains("focus on customizeaccessoriesscreen: KitSelector_01 asked, group 0 held already")) throw new Exception("the first row's requestFocus did not reach the host's focus manager as 'group 0 held already'");
                            // the engine facts the widgets ask at initialise are answered before the first widget initialises: an unanswered
                            // platform reads as console (the BACK button hides itself, the kit rows get no mouse handlers, no arrows to click)
                            if (LogBox.Text.Contains("UISettingsComp.Platform  (no value held")) throw new Exception("the widgets asked the platform before the host answered it (a console layout: no BACK button, no mouse on the rows)");
                            if (!LogBox.Text.Contains("generated UISettingsComp.Platform ←") || !LogBox.Text.Contains("generated FrontEndComp.InFrontend ←")) throw new Exception("the platform / frontend facts were not generated for the profile");
                            // the data the components would compute, generated from the game's own assets for the profile (kit, weapon, its customization
                            // table, the UI metadata): the five rows come up with items and ask for their icons
                            var s_DataCached = m_Rime.HasPartition("gameplay/kits/usassault");
                            var s_DataShot = Path.Combine(p_OutDir, "selftest_ruffle_data.png");
                            if (s_DataCached)
                            {
                                if (m_UiHost.Generated < 5) throw new Exception($"the generator built {m_UiHost.Generated} values for the accessories screen, expected the five rows and the flags");
                                foreach (var s_Source in new[] { "UICustomizationComp.WeaponAccessoryMain", "UICustomizationComp.WeaponAccessory1", "UICustomizationComp.WeaponAccessory4", "UICustomizationComp.CustomizationFrontend" })
                                    if (!LogBox.Text.Contains("generated " + s_Source + " ←")) throw new Exception("the generator did not build " + s_Source);
                                // the slot's shape as the game hands it: one object {ShowStepPlus, Items, ItemCategory}, the weapon row's category the primary's sid
                                var s_MainComp = m_WidgetCatalog!.Components.Values.First(c => c.Name.EndsWith("UICustomizationComp", StringComparison.OrdinalIgnoreCase));
                                var s_MainRow = m_UiHost.Data[s_MainComp.Keys["WeaponAccessoryMain"].ToString()] as JObject;
                                if (s_MainRow == null || (string?)s_MainRow["ItemCategory"] != "ID_M_SOLDIER_PRIMARY" || (s_MainRow["Items"] as JArray)?.Count is not > 1) throw new Exception("the generated weapon row is not one slot object with the primary's category and the kit's weapon list: " + (s_MainRow?.ToString(Newtonsoft.Json.Formatting.None) ?? "null")[..System.Math.Min(200, (s_MainRow?.ToString(Newtonsoft.Json.Formatting.None) ?? "null").Length)]);
                                for (var i = 0; i < 200 && m_UiHost.TexturesAsked < 3; ++i) await Task.Delay(50);
                                if (m_UiHost.TexturesAsked < 3) throw new Exception($"the generated rows asked for {m_UiHost.TexturesAsked} icons; expected the weapon's and the accessories' images");
                                await Task.Delay(1200);
                                await m_RuffleHost.CaptureAsync(s_DataShot);
                                var s_Dec = new System.Windows.Media.Imaging.PngBitmapDecoder(new Uri(s_DataShot), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                                var s_Fr = new System.Windows.Media.Imaging.FormatConvertedBitmap(s_Dec.Frames[0], PixelFormats.Bgra32, null, 0);
                                var s_Pix = new byte[s_Fr.PixelWidth * s_Fr.PixelHeight * 4]; s_Fr.CopyPixels(s_Pix, s_Fr.PixelWidth * 4, 0);
                                var s_Sc = System.Math.Min(s_Fr.PixelWidth / 1280.0, s_Fr.PixelHeight / 720.0);
                                var s_Ox = (s_Fr.PixelWidth - 1280 * s_Sc) / 2; var s_Oy = (s_Fr.PixelHeight - 720 * s_Sc) / 2;
                                var s_RowsLit = 0;
                                for (var y = (int)(s_Oy + 120 * s_Sc); y < (int)(s_Oy + 660 * s_Sc) && y < s_Fr.PixelHeight; ++y)
                                    for (var x = (int)(s_Ox + 90 * s_Sc); x < (int)(s_Ox + 730 * s_Sc) && x < s_Fr.PixelWidth; ++x)
                                        if (x >= 0 && y >= 0 && (s_Pix[(y * s_Fr.PixelWidth + x) * 4] + s_Pix[(y * s_Fr.PixelWidth + x) * 4 + 1] + s_Pix[(y * s_Fr.PixelWidth + x) * 4 + 2]) / 3 > 60) ++s_RowsLit;
                                if (s_RowsLit < 5000) throw new Exception($"the rows area shows {s_RowsLit} lit pixels: the generated rows did not render (selftest_ruffle_data.png)");
                                // the BACK button (Button_02, HideOnConsole) shows on PC: pixels lit at its placement
                                var s_Back = m_Current.Placements.First(p => p.Name == "Button_02");
                                var s_BackLit = 0;
                                for (var y = (int)(s_Oy + s_Back.Y * s_Sc); y < (int)(s_Oy + (s_Back.Y + 40) * s_Sc) && y < s_Fr.PixelHeight; ++y)
                                    for (var x = (int)(s_Ox + s_Back.X * s_Sc); x < (int)(s_Ox + (s_Back.X + 200) * s_Sc) && x < s_Fr.PixelWidth; ++x)
                                        if (x >= 0 && y >= 0 && (s_Pix[(y * s_Fr.PixelWidth + x) * 4] + s_Pix[(y * s_Fr.PixelWidth + x) * 4 + 1] + s_Pix[(y * s_Fr.PixelWidth + x) * 4 + 2]) / 3 > 60) ++s_BackLit;
                                if (s_BackLit < 150) throw new Exception($"the BACK button (Button_02 at {s_Back.X},{s_Back.Y}) shows {s_BackLit} lit pixels: hidden as on a console (selftest_ruffle_data.png)");
                                // the weapon row's service-star badge reads the item's ServiceStars (0 for the profile), as Shared.ServiceStars.setStars
                                // writes it — a TEXT read out of the movie, not pixels: the badge kept its authored "50" for two runs whose photos
                                // "looked right" while the export name mc_iconsServiceStars pointed at a copy of the symbol (the class registered on
                                // the copy, the placed instance had no setStars)
                                var s_BadgeText = await ProbeClip("_level0.sc1.instance1.KitSelector_01.mcContainer.Slot_0.headerIcon.txt.text");
                                if (s_BadgeText != "\"0\"") throw new Exception($"the weapon row's service-star badge reads {s_BadgeText}, expected \"0\" (the generated ServiceStars): the badge symbol's class did not reach the placed instance (export name on a copy of mc_iconsServiceStars?)");
                                // the weapon row's children as the movie holds them (what shows, where): read out of the player, one line in the log
                                // to compare against the game's row when a photo shows something the placements do not explain
                                var s_RowChildren = new System.Text.StringBuilder();
                                foreach (var s_Child in new[] { "mouseArea", "buttonLeft", "buttonRight", "bg_normal", "bg_selected", "bar", "slot1", "slot2", "slot3", "mainImage", "mainLock", "slotLock1", "slotLock2", "slotLock3", "header", "headerIcon", "category", "button1", "plus",
                                                                 "slot1.spinnerMc", "slot2.spinnerMc", "slot3.spinnerMc", "mainImage.spinnerMc", "slot3.imgHolder", "slot3.sizeClip" })
                                {
                                    var s_ChildPath = "_level0.sc1.instance1.KitSelector_01.mcContainer.Slot_0." + s_Child;
                                    s_RowChildren.Append($"{s_Child}: visible {await ProbeClip(s_ChildPath + "._visible")} alpha {await ProbeClip(s_ChildPath + "._alpha")} at {await ProbeClip(s_ChildPath + "._x")},{await ProbeClip(s_ChildPath + "._y")} size {await ProbeClip(s_ChildPath + "._width")}x{await ProbeClip(s_ChildPath + "._height")} frame {await ProbeClip(s_ChildPath + "._currentframe")}; ");
                                }
                                foreach (var s_Slot in new[] { "slot1", "slot3" })
                                {
                                    var s_SlotPath = "_level0.sc1.instance1.KitSelector_01.mcContainer.Slot_0." + s_Slot;
                                    s_RowChildren.Append($"{s_Slot} image: url {await ProbeClip(s_SlotPath + ".url")} loaded {await ProbeClip(s_SlotPath + ".m_isLoaded")} loading {await ProbeClip(s_SlotPath + ".m_currentlyLoading")} contains {await ProbeClip(s_SlotPath + ".m_containsImage")} queued {await ProbeClip(s_SlotPath + ".m_queuedObject")}; ");
                                }
                                Log("[selftest] weapon row children: " + s_RowChildren);
                                R($"data: {m_UiHost.Generated} values generated for the accessories screen (weapon row + 4 accessory rows + flags) from the kit, the weapon's customization table and the UI metadata; {m_UiHost.TexturesAsked} icons asked ({m_UiHost.TexturesFound} served); {s_RowsLit} lit pixels in the rows area; the BACK button shows ({s_BackLit} lit pixels at its placement); the weapon row's service-star badge reads {s_BadgeText} (selftest_ruffle_data.png)");
                                // the profile in the player's toolbar: another kit (and its first primary) → every generated value is built again and
                                // the widgets bound to them refresh — the header says the new kit, the rows show its weapon's table; then another
                                // weapon of that kit → the weapon row is built for it. Driven through the toolbar's own handlers.
                                var s_Kits = DataGenerator.Kits();
                                if (s_Kits.Count < 8 || !s_Kits.Any(k => k.Partition == "gameplay/kits/ruengineer")) throw new Exception($"the generator lists {s_Kits.Count} kit(s); expected the game's soldier kits with gameplay/kits/ruengineer among them");
                                var s_LogProfile = LogBox.Text.Length;
                                m_RuffleHost.OnKitChanged!("gameplay/kits/ruengineer");
                                var s_ProfileLine = LogBox.Text[s_LogProfile..];
                                if (!s_ProfileLine.Contains("regenerated UICustomizationComp.WeaponAccessoryMain ←") || !s_ProfileLine.Contains("regenerated UIKitComp.SelectedKitName ←")) throw new Exception("the kit change did not build the weapon row and the kit name again: " + s_ProfileLine.Trim()[..System.Math.Min(300, s_ProfileLine.Trim().Length)]);
                                if (!s_ProfileLine.Contains("→ updateTextData(\"ENGINEER\")") || !s_ProfileLine.Contains("KitSelector_01.refresh({Setup}) → updateSetupData(")) throw new Exception("the widgets bound to the regenerated keys did not refresh (the header's text, localised, and the weapon row): " + s_ProfileLine.Trim()[..System.Math.Min(300, s_ProfileLine.Trim().Length)]);
                                var s_Weapons = DataGenerator.PrimaryWeapons();
                                if (s_Weapons.Count < 2) throw new Exception($"the engineer kit lists {s_Weapons.Count} primary weapon(s) for the toolbar");
                                s_LogProfile = LogBox.Text.Length;
                                m_RuffleHost.OnWeaponChanged!(s_Weapons[1].Unlock);
                                s_ProfileLine = LogBox.Text[s_LogProfile..];
                                if (!s_ProfileLine.Contains("regenerated UICustomizationComp.WeaponAccessoryMain ←") || !s_ProfileLine.Contains(s_Weapons[1].Unlock + " selected with its slot icons")) throw new Exception($"the weapon change did not build the weapon row with {s_Weapons[1].Unlock} selected: " + s_ProfileLine.Trim()[..System.Math.Min(300, s_ProfileLine.Trim().Length)]);
                                await Task.Delay(1500);
                                var s_ProfileShot = Path.Combine(p_OutDir, "selftest_ruffle_profile.png");
                                await m_RuffleHost.CaptureAsync(s_ProfileShot);
                                if (File.ReadAllBytes(s_ProfileShot).SequenceEqual(File.ReadAllBytes(s_DataShot))) throw new Exception("selftest_ruffle_profile.png is byte-identical to selftest_ruffle_data.png: the kit/weapon change did not reach the player");
                                R($"profile: kit ruengineer ({s_Kits.Count} kits in the toolbar) → header ENGINEER, rows built again; weapon {s_Weapons[1].Unlock.Split('/').Last()} (of {s_Weapons.Count}) → weapon row built for it; the photo differs from the assault one (selftest_ruffle_profile.png)");
                                // back to the default profile: the rest of the run (Confirm → loadout) reads the assault kit
                                m_RuffleHost.OnKitChanged!("gameplay/kits/usassault");
                                await Task.Delay(800);
                                // a pick in an accessory row, as the row's arrows do: KitSelector_02 (optics) fires SetIndex with the item's Index →
                                // the screen's SetAccessory1 writes CustSelectedAccessory1 → AccessoryChanged → the flow graph's UpdateWeaponAccessory
                                // (StorePrimaryWeaponAccessories) → the stand-in takes it into the profile → the weapon row's optics slot shows the optic
                                var s_Cust = m_WidgetCatalog!.Components.Values.First(c => c.Name.EndsWith("UICustomizationComp", StringComparison.OrdinalIgnoreCase));
                                var s_MainKey = s_Cust.Keys["WeaponAccessoryMain"].ToString();
                                // the weapon row is one slot object holding the kit's primary list; the equipped weapon is the DefaultSelected item
                                static string SlotImage(object? p_Main, int p_Slot)
                                {
                                    var s_Slot = p_Main is JArray s_A ? (s_A.Count > 0 ? s_A[0] : null) : p_Main as JToken;
                                    var s_Item = (s_Slot?["Items"] as JArray)?.FirstOrDefault(i => (bool?)i["DefaultSelected"] == true);
                                    return (s_Item?["SlotsData"] as JArray)?[p_Slot]?["ItemImage"]?.ToString() ?? "";
                                }
                                var s_SlotBefore = SlotImage(m_UiHost.Data[s_MainKey], 0);
                                if (!s_SlotBefore.Contains("NoSelection", StringComparison.OrdinalIgnoreCase)) throw new Exception("the optics slot of the weapon row does not start at the \"none\" icon: " + s_SlotBefore);
                                var s_LogPick = LogBox.Text.Length;
                                // the row's own right arrow (KitSelectorSlot.stepRight: what the arrow button's press handler calls): the slot fires
                                // SetIndex with the next item's Index — the unlock's identifier, as the game's rows do
                                m_RuffleHost.Dispatch("_level0.sc1.instance1.KitSelector_02.mcContainer.Slot_0", "stepRight");
                                for (var i = 0; i < 100 && !LogBox.Text[s_LogPick..].Contains("regenerated UICustomizationComp.WeaponAccessoryMain"); ++i) await Task.Delay(50);
                                var s_PickLine = LogBox.Text[s_LogPick..];
                                var s_PickValue = System.Text.RegularExpressions.Regex.Match(s_PickLine, "CustSelectedAccessory1 = \"(-?\\d+)\"").Groups[1].Value;
                                if (!s_PickLine.Contains("KitSelector_02 fired SetIndex") || s_PickValue == "" || !s_PickLine.Contains("AccessoryChanged: screen output") || !s_PickLine.Contains("engine action StorePrimaryWeaponAccessories") || !s_PickLine.Contains("stood in for"))
                                    throw new Exception("the arrow did not run row → SetAccessory1 → AccessoryChanged → UpdateWeaponAccessory → stand-in: " + s_PickLine.Trim()[..System.Math.Min(400, s_PickLine.Trim().Length)]);
                                if (!long.TryParse(s_PickValue, out var s_PickId) || System.Math.Abs(s_PickId) < 1000) throw new Exception("the row's arrow sent a position, not the unlock's identifier: " + s_PickValue);
                                var s_SlotAfter = SlotImage(m_UiHost.Data[s_MainKey], 0);
                                if (s_SlotAfter == s_SlotBefore || s_SlotAfter.Contains("NoSelection", StringComparison.OrdinalIgnoreCase)) throw new Exception($"the weapon row's optics slot did not take the pick: {s_SlotBefore} → {s_SlotAfter}");
                                await Task.Delay(1500);
                                var s_PickShot = Path.Combine(p_OutDir, "selftest_ruffle_pick.png");
                                await m_RuffleHost.CaptureAsync(s_PickShot);
                                if (File.ReadAllBytes(s_PickShot).SequenceEqual(File.ReadAllBytes(s_DataShot))) throw new Exception("selftest_ruffle_pick.png is byte-identical to selftest_ruffle_data.png: the pick did not reach the player");
                                R($"pick: the optics row's right arrow (stepRight) → SetIndex({s_PickValue}, the unlock's identifier) → CustSelectedAccessory1 → AccessoryChanged → StorePrimaryWeaponAccessories stood in → the weapon row's optics slot {s_SlotBefore.Split('/').Last()} → {s_SlotAfter.Split('/').Last()} (selftest_ruffle_pick.png)");
                                // the keyboard as the game reads it on PC, through the player window's own key handler: Down = NavigateDown handed to
                                // the focusable widgets — the focused weapon row (one slot) fires its NAVIGATE_DOWN, the screen wires it to the next
                                // row, which activates and asks the focus (the host's focus manager moves it); Right = the focused row's stepRight
                                // (SetIndex); Up = back to the weapon row
                                // the focus sits where the last gesture left it (the pick's stepRight activated the optics row): the walk is relative to it
                                var s_FocusRow = m_UiHost.FocusedWidget ?? throw new Exception("no widget holds the focus on the accessories screen (no requestFocus reached the host)");
                                if (!s_FocusRow.StartsWith("KitSelector_0")) throw new Exception("the focus is on " + s_FocusRow + ", expected a kit row");
                                var s_RowNo = int.Parse(s_FocusRow[^1..]);
                                var s_NextRow = "KitSelector_0" + (s_RowNo % 5 + 1);   // the rows wrap: KitSelector_05.NavigateDown → KitSelector_01.Activate
                                var s_LogKeys = LogBox.Text.Length;
                                // the press, then the release once the press has done its work (a real key's release comes a frame later: the focus has moved by then)
                                m_RuffleHost.OnInputConcept!(1, true);
                                for (var i = 0; i < 100 && !LogBox.Text[s_LogKeys..].Contains($"focus on customizeaccessoriesscreen: {s_NextRow} onFocused(false)"); ++i) await Task.Delay(50);
                                m_RuffleHost.OnInputConcept!(1, false);
                                var s_KeyLine = LogBox.Text[s_LogKeys..];
                                // handed the game's way (measured on its traffic): the widgets of the focus group + the AlwaysInFocus ones in the init order; the
                                // press stops at the row that fires NAVIGATE_DOWN; the BACK button (FocusIndex -1) and the rows of other groups never see it
                                for (var i = 0; i < 100 && !s_KeyLine.Contains("input concept NavigateDown released on customizeaccessoriesscreen:"); ++i) { await Task.Delay(50); s_KeyLine = LogBox.Text[s_LogKeys..]; }
                                var s_DownLine = System.Text.RegularExpressions.Regex.Match(s_KeyLine, @"input concept NavigateDown pressed on customizeaccessoriesscreen: ([^
]*)").Groups[1].Value;
                                var s_DownHanded = s_DownLine.Split(", ", StringSplitOptions.RemoveEmptyEntries).Select(h => h.Trim()).ToList();
                                if (s_DownHanded.Count == 0 || s_DownHanded.Last() != s_FocusRow) throw new Exception($"Down was handed to [{s_DownLine}], expected it to stop at the focused row {s_FocusRow}: " + s_KeyLine.Trim()[..System.Math.Min(300, s_KeyLine.Trim().Length)]);
                                // the init order: rows 1..4 sit before the button bar (position 6), the camo row after it — only there the bar is offered the key first
                                var s_ExpectedDown = s_RowNo == 5 ? new[] { "ConsoleButtonBar_01", "KitSelector_05" } : new[] { s_FocusRow };
                                if (!s_DownHanded.SequenceEqual(s_ExpectedDown)) throw new Exception($"Down on {s_FocusRow} was handed to [{s_DownLine}], expected [{string.Join(", ", s_ExpectedDown)}] (the init order, stopping at the row that fires NAVIGATE_DOWN)");
                                if (s_DownHanded.Contains("Button_02") || s_DownHanded.Any(h => h.StartsWith("KitSelector_0") && h != s_FocusRow)) throw new Exception($"a widget outside the focus group was handed the key: [{s_DownLine}] (the BACK button has FocusIndex -1; the other rows other groups)");
                                var s_ReleaseLine = System.Text.RegularExpressions.Regex.Match(s_KeyLine, @"input concept NavigateDown released on customizeaccessoriesscreen: ([^
]*)").Groups[1].Value;
                                if (!s_ReleaseLine.Contains("ConsoleButtonBar_01") || !s_ReleaseLine.Contains(s_NextRow)) throw new Exception($"the release should reach every eligible widget of the new group ({s_NextRow} + the button bar): [{s_ReleaseLine}]");
                                if (!s_KeyLine.Contains($"{s_NextRow} onFocused(false)") || !s_KeyLine.Contains($"{s_FocusRow} onUnfocused()")) throw new Exception($"Down did not move the focus from {s_FocusRow} to {s_NextRow} (NAVIGATE_DOWN → the next row's Activate → requestFocus): " + s_KeyLine.Trim()[..System.Math.Min(600, s_KeyLine.Trim().Length)]);
                                if (m_UiHost.FocusedWidget != s_NextRow) throw new Exception("the host's focus is on " + m_UiHost.FocusedWidget + " after Down, expected " + s_NextRow);
                                s_LogKeys = LogBox.Text.Length;
                                m_RuffleHost.OnInputConcept!(3, true); m_RuffleHost.OnInputConcept!(3, false);
                                for (var i = 0; i < 100 && !LogBox.Text[s_LogKeys..].Contains($"{s_NextRow} fired SetIndex"); ++i) await Task.Delay(50);
                                s_KeyLine = LogBox.Text[s_LogKeys..];
                                var s_KeyPick = System.Text.RegularExpressions.Regex.Match(s_KeyLine, s_NextRow + " fired SetIndex \\(\"?(-?\\d+)").Groups[1].Value;
                                if (s_KeyPick == "") throw new Exception($"Right on the focused row {s_NextRow} did not step it (stepRight → SetIndex): " + s_KeyLine.Trim()[..System.Math.Min(400, s_KeyLine.Trim().Length)]);
                                for (var i = 0; i < 100 && !LogBox.Text[s_LogKeys..].Contains("regenerated UICustomizationComp.WeaponAccessoryMain"); ++i) await Task.Delay(50);
                                s_LogKeys = LogBox.Text.Length;
                                m_RuffleHost.OnInputConcept!(0, true); m_RuffleHost.OnInputConcept!(0, false);
                                for (var i = 0; i < 100 && !LogBox.Text[s_LogKeys..].Contains($"focus on customizeaccessoriesscreen: {s_FocusRow} onFocused(false)"); ++i) await Task.Delay(50);
                                if (!LogBox.Text[s_LogKeys..].Contains($"{s_FocusRow} onFocused(false)") || !LogBox.Text[s_LogKeys..].Contains($"{s_NextRow} onUnfocused()")) throw new Exception($"Up did not bring the focus back to {s_FocusRow}: " + LogBox.Text[s_LogKeys..].Trim()[..System.Math.Min(400, LogBox.Text[s_LogKeys..].Trim().Length)]);
                                await Task.Delay(300);
                                R($"keyboard: Down → NavigateDown to the focused row {s_FocusRow} (+ AlwaysInFocus widgets, never the BACK button) → its NAVIGATE_DOWN → {s_NextRow} activated and focused; Right → its stepRight → SetIndex({s_KeyPick}); Up → {s_FocusRow} focused again");
                                // the recorder inside the movie noted that traffic on the widget instances and flushed it (half-second batches): the concept
                                // handed to the row, the focus moves, the row's fireEvent with the event id of its table — what a game recording carries
                                for (var i = 0; i < 60 && !m_UiHost.EventsEchoed.Any(e => (string?)e["m"] == "onInputConceptPressed" && (string?)e["w"] == s_FocusRow && (int?)e["a"]?[0] == 0); ++i) await Task.Delay(50);
                                var s_Traffic = m_UiHost.EventsEchoed;
                                if (!s_Traffic.Any(e => (string?)e["m"] == "onInputConceptPressed" && (string?)e["w"] == s_FocusRow && (int?)e["a"]?[0] == 1)) throw new Exception($"the recorder did not note NavigateDown (concept 1) pressed on {s_FocusRow}: {s_Traffic.Count} events — " + string.Join(", ", s_Traffic.Take(12).Select(e => $"{e["w"]}.{e["m"]}({e["a"]})")));
                                if (!s_Traffic.Any(e => (string?)e["m"] == "onFocused" && (string?)e["w"] == s_NextRow) || !s_Traffic.Any(e => (string?)e["m"] == "onUnfocused" && (string?)e["w"] == s_FocusRow)) throw new Exception("the recorder did not note the focus moving " + s_FocusRow + " → " + s_NextRow);
                                if (!s_Traffic.Any(e => (string?)e["m"] == "fireEvent" && (string?)e["w"] == s_NextRow && (int?)e["a"]?[0] == m_UiHost.EventValue("SetIndex") && e["a"]?[1]?.ToString() == s_KeyPick)) throw new Exception($"the recorder did not note {s_NextRow}.fireEvent(SetIndex = {m_UiHost.EventValue("SetIndex")}, {s_KeyPick}): " + string.Join(", ", s_Traffic.Where(e => (string?)e["m"] == "fireEvent").Take(12).Select(e => $"{e["w"]}.fireEvent({e["a"]})")));
                                if (!s_Traffic.Any(e => (string?)e["m"] == "requestFocus" && (string?)e["w"] == s_NextRow)) throw new Exception("the recorder did not note " + s_NextRow + ".requestFocus");
                                R($"recorder traffic: {s_Traffic.Count} event(s) noted on the widgets and flushed in batches — onInputConceptPressed(1) on {s_FocusRow}, {s_NextRow}.requestFocus → onFocused/onUnfocused, {s_NextRow}.fireEvent(SetIndex, {s_KeyPick}); a game recording carries the same (which widgets the engine hands the keys, what they fire back)");
                                if (Environment.GetEnvironmentVariable("RUE_SELFTEST_MOUSE") == "1")
                                {
                                    // the real pointer and keyboard on the player window (opt-in: it moves the pointer): a hover on the barrel row raises
                                    // its mouseOver → OnItemOver → KitInfoBox_01.OnShow; a click on the rail row's right arrow is the arrow button's
                                    // press → stepRight → SetIndex; a real Down key reaches the window's key handler through the WebView2
                                    var s_Row4 = m_Current.Placements.First(p => p.Name == "KitSelector_04");
                                    var s_Row3 = m_Current.Placements.First(p => p.Name == "KitSelector_03");
                                    NativeInput.GetCursorPos(out var s_OldCursor);
                                    m_RuffleHost.Activate(); m_RuffleHost.Topmost = true; await Task.Delay(400);
                                    var s_Trace = new System.Text.StringBuilder();
                                    try
                                    {
                                        var s_LogMouse = LogBox.Text.Length;
                                        // the row's body (KitView_3: 580 wide, 78 tall; the mouse area starts at its origin)
                                        var s_Hover = m_RuffleHost.StageToScreen(new Point(s_Row4.X + 290, s_Row4.Y + 39));
                                        NativeInput.MoveTo(s_Hover); await Task.Delay(120); NativeInput.MoveTo(new Point(s_Hover.X + 2, s_Hover.Y + 1)); await Task.Delay(500);
                                        NativeInput.GetCursorPos(out var s_At);
                                        s_Trace.Append($"hover asked ({s_Hover.X:0},{s_Hover.Y:0}) for stage ({s_Row4.X + 290},{s_Row4.Y + 39}), pointer at ({s_At.X},{s_At.Y}); ");
                                        var s_MouseLine = LogBox.Text[s_LogMouse..];
                                        if (!s_MouseLine.Contains("KitSelector_04 fired OnItemOver")) throw new Exception("the pointer over the barrel row did not raise its mouseOver (OnItemOver); " + s_Trace + "log: " + s_MouseLine.Trim()[..System.Math.Min(300, s_MouseLine.Trim().Length)]);
                                        if (!s_MouseLine.Contains("KitInfoBox_01.OnShow")) throw new Exception("the hover's OnItemOver did not reach KitInfoBox_01.OnShow; " + s_Trace);
                                        s_LogMouse = LogBox.Text.Length;
                                        // the right arrow: a Button at the row's local (547,0), 67 px wide, as tall as the row
                                        var s_Arrow = m_RuffleHost.StageToScreen(new Point(s_Row3.X + 547 + 33, s_Row3.Y + 39));
                                        NativeInput.MoveTo(s_Arrow); await Task.Delay(200); NativeInput.LeftDown(); await Task.Delay(80); NativeInput.LeftUp();
                                        for (var i = 0; i < 100 && !LogBox.Text[s_LogMouse..].Contains("KitSelector_03 fired SetIndex"); ++i) await Task.Delay(50);
                                        s_MouseLine = LogBox.Text[s_LogMouse..];
                                        s_Trace.Append($"arrow clicked at ({s_Arrow.X:0},{s_Arrow.Y:0}) for stage ({s_Row3.X + 580},{s_Row3.Y + 39}); ");
                                        if (!s_MouseLine.Contains("KitSelector_03 fired SetIndex")) throw new Exception("the click on the rail row's right arrow did not step the row (the arrow's press → stepRight → SetIndex); " + s_Trace + "log: " + s_MouseLine.Trim()[..System.Math.Min(300, s_MouseLine.Trim().Length)]);
                                        for (var i = 0; i < 100 && !LogBox.Text[s_LogMouse..].Contains("regenerated UICustomizationComp.WeaponAccessoryMain"); ++i) await Task.Delay(50);
                                        // a real key on the active player window
                                        s_LogMouse = LogBox.Text.Length;
                                        NativeInput.KeyPress(NativeInput.VkDown);
                                        for (var i = 0; i < 60 && !LogBox.Text[s_LogMouse..].Contains("input concept NavigateDown pressed"); ++i) await Task.Delay(50);
                                        if (!LogBox.Text[s_LogMouse..].Contains("input concept NavigateDown pressed")) throw new Exception("a real Down key on the player window did not reach the host (the WebView2 did not raise the window's key event); " + s_Trace);
                                        await Task.Delay(300);
                                    }
                                    finally { m_RuffleHost.Topmost = false; NativeInput.SetCursorPos(s_OldCursor.X, s_OldCursor.Y); }
                                    await Task.Delay(300);
                                    R("mouse on the player (real pointer): hover on the barrel row → OnItemOver → KitInfoBox_01.OnShow; click on the rail row's right arrow → SetIndex; a real Down key → NavigateDown through the window; " + s_Trace);
                                }
                                else R("mouse on the player: skipped (RUE_SELFTEST_MOUSE=1 moves the real pointer over the player: a hover, an arrow click, a real key)");
                            }
                            else R("data: the kit and customization partitions are not in the cache (a mount fetches them), so the generated rows are not checked in this quick run");
                            // the recorder inside the movie echoed initializeScreen: the record the game's log channel would carry, chunked into
                            // console lines here exactly as the receiver prints them, imported, and played back by a second preview later
                            for (var i = 0; i < 200 && (m_UiHost.LastRecord == null || (string?)m_UiHost.LastRecord["kind"] != "init"); ++i) await Task.Delay(50);
                            var s_Echo = m_UiHost.LastRecord ?? throw new Exception("the recorder did not echo initializeScreen from inside the movie");
                            if ((string?)s_Echo["kind"] != "init" || (string?)s_Echo["screen"] != "customizeaccessoriesscreen") throw new Exception("the recorder's echo is not the init of the accessories screen: " + s_Echo.ToString(Newtonsoft.Json.Formatting.None)[..200]);
                            if ((bool?)s_Echo["captured"] != true) throw new Exception("the recorder could not capture the components' calls (apply on the proxies failed)");
                            var s_EchoText = s_Echo.ToString(Newtonsoft.Json.Formatting.None);
                            // the strings the movie saw are the strings the host sent: the player turns an empty string that comes in through
                            // ExternalInterface into the text "null" when the ROOT movie of the stage (the shell, whose clip the callback runs
                            // under — not the bridge) is below version 9; the accessory rows' empty ItemCategory showed as a small "null"
                            // above every label until the shell was packaged as version 10 — measured here on the echo, not on pixels
                            if (s_DataCached && (s_EchoText.Contains("\"ItemCategory\":\"null\"") || !s_EchoText.Contains("\"ItemCategory\":\"\"")))
                                throw new Exception("the movie did not see the empty strings the host sent (an empty ItemCategory arrived as " + (s_EchoText.Contains("\"ItemCategory\":\"null\"") ? "\"null\"" : "something else") + "): the shell must be a version 9+ movie for ExternalInterface to keep empty strings");
                            // …and the way back: a value the page returns to a call the movie makes (getData…) converts under the movie of
                            // the calling proxy — the bridge — so the bridge is a version 9+ movie too; probed with a key set to "" and the
                            // bridge's getData proxy called from the page with its result echoed
                            m_UiHost.SetDataKey("1234567890", "");
                            var s_LogProbe = LogBox.Text.Length;
                            m_RuffleHost.Dispatch("_global.Data.UIDataInterfaceComp", "getData", 1234567890, null, null, true);
                            for (var i = 0; i < 100 && !LogBox.Text[s_LogProbe..].Contains("UIDataInterfaceComp.getData -> "); ++i) await Task.Delay(50);
                            var s_ProbeLog = LogBox.Text[s_LogProbe..]; var s_ProbeAt = s_ProbeLog.IndexOf("UIDataInterfaceComp.getData -> ");
                            var s_Returned = s_ProbeAt < 0 ? "(no echo)" : s_ProbeLog[(s_ProbeAt + "UIDataInterfaceComp.getData -> ".Length)..].Split('\n')[0].Trim();
                            if (s_Returned != "{\"value\":\"\"}") throw new Exception("an empty string the page returned to the movie's getData came back as " + s_Returned + ": the bridge must be a version 9+ movie (a return value converts under the calling movie)");
                            R("strings: an empty string pushed into the movie (initializeScreen) and one returned to it (getData) both stay empty — the shell and the bridge are version-10 movies (below 9 the player turns them into the text \"null\")");
                            var s_EchoTotal = (s_EchoText.Length + 179) / 180; var s_Console = new System.Text.StringBuilder();
                            for (var i = 0; i < s_EchoTotal; ++i) { var s_Piece = s_EchoText.Substring(i * 180, System.Math.Min(180, s_EchoText.Length - i * 180)); s_Console.Append($"[02:20:{i % 60:00}] [info] [VeniceEXT] [rimeuirecorder] [AS2] #R{s_Echo["run"]}.i0|{i + 1}|{s_EchoTotal}|{s_Piece.Length}|{s_Piece}\n"); }
                            var s_Live = ImportRecordingText(s_Console.ToString()) ?? throw new Exception("the echoed init did not import as a recording");
                            if (s_Live.Widgets.Count < 10 || !s_Live.InstanceNames.Contains("KitSelector_05") || !s_Live.InstanceNames.Contains(s_TextField.Name)) throw new Exception($"the echoed init names {string.Join(", ", s_Live.InstanceNames)}: expected the screen's 10 widgets and {s_TextField.Name}");
                            var s_KitRecord = s_Live.InitRecord("KitSelector_05")!;
                            if ((int?)s_KitRecord["NumEvents"] is not > 0 || (int?)s_KitRecord["Event_0"] != m_UiHost.EventValue(m_WidgetCatalog!.Get("ui/assets/kitselector")!.Events[0].Query)) throw new Exception("the echoed record of KitSelector_05 lacks the events the host sent");
                            // the record carries the node's alignment pair (centre, centre for the kit rows); the widget's base class applied it
                            // against the stage rectangles the shell defines, and the rows stayed where they were authored (the lit-pixel checks)
                            var s_AlignCount = s_KitRecord["Align"] is JArray s_AlignArray ? s_AlignArray.Count : (int?)(s_KitRecord["Align"] as JObject)?["length"] ?? 0;
                            if (s_AlignCount != 2) throw new Exception("the echoed record of KitSelector_05 lacks its Align pair: " + (s_KitRecord["Align"]?.ToString(Newtonsoft.Json.Formatting.None) ?? "none"));
                            // HasFocus as the game hands it: FocusIndex 0 (the first row) true, the other rows false; the item's Index is the unlock's Identifier
                            if ((bool?)s_Live.InitRecord("KitSelector_01")?["HasFocus"] != true || (bool?)s_KitRecord["HasFocus"] != false) throw new Exception("HasFocus is not the node's FocusIndex == 0 (KitSelector_01 true, KitSelector_05 false)");
                            var s_FirstIndex = (s_KitRecord["data"]?["Setup"]?["Items"] as JArray)?.FirstOrDefault()?["Index"]?.ToString() ?? "";
                            if (!long.TryParse(s_FirstIndex, out var s_IndexValue) || s_IndexValue == 0 || (s_IndexValue >= 0 && s_IndexValue < 100)) throw new Exception("the row's items do not carry the unlock's Identifier as Index: " + s_FirstIndex);
                            if ((string?)s_Live.InitRecord(s_TextField.Name)?["data"]?["Text"] != "I hate burgers") throw new Exception("the echoed record of the document's text field lacks its static text");
                            var s_LiveFixture = DataRecorder.FixturePath(EditorSettings.DefaultPreviewRoot(), m_Current.MovieName);
                            if (!File.Exists(s_LiveFixture)) throw new Exception("the recording of the accessories screen was not saved at " + s_LiveFixture);
                            R($"recording: the recorder echoed {s_Live.Widgets.Count} init records from inside the movie ({s_EchoText.Length} chars, {s_EchoTotal} console chunks) → Import recording… → {s_LiveFixture}");
                            // a row with an icon, as the game's data carries it (KitView.updateSetupData: an array of slots; the accessories rows are
                            // KitSelectorSlot: {ItemCategory, Items: [{Label, ItemImage under UI/Art/…, Index, DefaultSelected}]}): the second preview must
                            // ask the editor for the texture and show it
                            // camo2, not camo1: the game's own "No Camo" entry carries camo1, so the generated camo row already showed it in the
                            // first preview and a fixture with the same icon could not be told from the generated rows (the two photos came out
                            // byte-identical); the control below refuses an icon the first preview already fetched
                            const string s_IconPath = "UI/Art/Persistence/Camo/camo2";
                            var s_IconCached = m_Rime.HasTexture(s_IconPath.ToLowerInvariant());
                            if (LogBox.Text.Contains("/ui/assets/img/" + s_IconPath + " -> 200")) throw new Exception("the icon test's texture " + s_IconPath + " was already fetched by the first preview: the test could not tell the fixture's icon from the generated rows");
                            s_KitRecord["data"] = new JObject
                            {
                                ["Setup"] = new JArray(new JObject
                                {
                                    ["ItemCategory"] = "",
                                    ["Items"] = new JArray(new JObject { ["Label"] = "ID_P_ANAME_NOCAMO", ["ItemImage"] = s_IconPath, ["Index"] = 0, ["Locked"] = false, ["DefaultSelected"] = true }),
                                }),
                                ["Visibility"] = true,
                            };
                            s_Live.Save(s_LiveFixture);
                            R($"fixture: KitSelector_05 given one slot with the icon {s_IconPath} ({(s_IconCached ? "in the cache" : "NOT cached: the icon test needs a mount")})");
                            var s_Calls = m_RuffleHost.Calls;
                            var s_LogBefore2 = LogBox.Text.Length;
                            // round trip: editor → movie (rueDispatch) → the proxy → the page → editor → the graph: KitSelector_05's OnItemOver is wired
                            // to KitInfoBox_01's OnShow in the shipped screen
                            m_RuffleHost.Dispatch("_global.Data.UIWidgetEventComp", "fireEvent", "_level0.sc1.instance1.KitSelector_05", m_UiHost.EventValue("OnItemOver"), 3);
                            for (var i = 0; i < 100 && m_RuffleHost.Calls < s_Calls + 1; ++i) await Task.Delay(50);
                            if (m_RuffleHost.Calls < s_Calls + 1) throw new Exception("the fireEvent dispatched into the movie did not come back through the bridge");
                            await Task.Delay(300);
                            var s_Line = LogBox.Text[s_LogBefore2..];
                            if (!s_Line.Contains("KitSelector_05 fired OnItemOver") || !s_Line.Contains("KitInfoBox_01.OnShow: handleInEvent(")) throw new Exception("the host did not follow KitSelector_05.OnItemOver → KitInfoBox_01.OnShow: " + s_Line.Trim());
                            // the flow graph: ConsoleButtonBar_01's OnItemOut is wired to the screen's Confirm output, which the parent graph takes to a
                            // comparison (SaveAccessoriesEnabled) the game's component answers: generated for the profile when the game's data is at hand,
                            // else the walk stops and says so, and the key is set by hand here (the player's toolbar does the same)
                            var s_FlowModel = ModelFor("ui/flow/graph/spawn/customizationgraph") ?? throw new Exception("the flow graph model did not load");
                            var s_SaveKey = s_FlowModel.AllNodes.First(n => n.Label == "SaveAccessoriesEnabled").Json["DataSourceInfo"]?["DataKey"]?.ToString() ?? throw new Exception("SaveAccessoriesEnabled has no data key");
                            if (!m_UiHost.ReferencedKeys().Any(k => k.Key == s_SaveKey)) throw new Exception("the toolbar's key list lacks the comparison's key");
                            s_LogBefore2 = LogBox.Text.Length;
                            // the flag is a constant of the profile (generated without any cached partition): True → the store action (pop 1: the
                            // accessories screen) → the sub screen pushed
                            // the BACK button itself (Button_02, shown on PC): its release is the screen's Confirm output, as the mouse would do it
                            m_RuffleHost.Dispatch("_level0.sc1.instance1.Button_02", "buttonReleased");
                            for (var i = 0; i < 600 && !(m_UiHost.Stack.Count == 1 && m_UiHost.Stack[0].StartsWith("sc2:") && m_UiHost.LastRecord != null && (string?)m_UiHost.LastRecord["screen"] == "customizesoldiersubscreen"); ++i) await Task.Delay(50);
                            s_Line = LogBox.Text[s_LogBefore2..];
                            if (!s_Line.Contains("Button_02 fired OnItemReleased")) throw new Exception("the BACK button's release did not reach the host (the button is not initialised, or hidden as on a console): " + s_Line.Trim()[..System.Math.Min(300, s_Line.Trim().Length)]);
                            if (!s_Line.Contains("SaveAccessoriesEnabled: compares UICustomizationComp.SaveAccessoriesEnabled = \"")) throw new Exception("the comparison did not read the key (generated, or set by hand): " + s_Line.Trim());
                            if (!s_Line.Contains("pop sc1 customizeaccessoriesscreen")) throw new Exception("the wire's NumScreensToPop did not pop the accessories screen: " + s_Line.Trim());
                            if (!s_Line.Contains("engine action ")) throw new Exception("the store action was not logged under its UIAction name: " + s_Line.Trim());
                            if (!s_Line.Contains("push ui/flow/screen/customizesoldiersubscreen as sc2")) throw new Exception("the flow graph did not push the sub screen: " + s_Line.Trim());
                            if (m_UiHost.Stack.Count != 1 || !m_UiHost.Stack[0].StartsWith("sc2:customizesoldiersubscreen")) throw new Exception("the stack after Confirm is " + string.Join(", ", m_UiHost.Stack) + ", expected the sub screen alone as sc2");
                            if (!s_Line.Contains("sc2 customizesoldiersubscreen loaded: initializeScreen with") || !s_Line.Contains("entering customizesoldiersubscreen: EnterScreen")) throw new Exception("the pushed screen was not initialised and entered: " + s_Line.Trim());
                            if (m_UiHost.LastRecord == null || (string?)m_UiHost.LastRecord["screen"] != "customizesoldiersubscreen") throw new Exception("the pushed screen's recorder did not echo its init (the movie did not run)");
                            // the loadout's rows come from the kit's tables: the specialization row from its own table (KitSpecialization), and
                            // the primary weapon item with its small slot icons (the row's ActionScript shows lock placeholders when SlotsData
                            // is missing); read from the sub screen's own echo — the data as the movie saw it
                            if (s_DataCached)
                            {
                                if (!s_Line.Contains("generated UICustomizationComp.KitSpecialization ←")) throw new Exception("the generator did not build the loadout's specialization row (UICustomizationComp.KitSpecialization)");
                                // the first echo of a screen is the recorder's hello (no widgets); the init follows
                                for (var i = 0; i < 200 && (string?)m_UiHost.LastRecord?["kind"] != "init"; ++i) await Task.Delay(50);
                                if ((string?)m_UiHost.LastRecord?["kind"] != "init") throw new Exception("the sub screen's recorder did not echo its init (last record: " + (string?)m_UiHost.LastRecord?["kind"] + ")");
                                var s_SubText = m_UiHost.LastRecord!.ToString(Newtonsoft.Json.Formatting.None);
                                var s_SubTotal = (s_SubText.Length + 179) / 180; var s_SubConsole = new System.Text.StringBuilder();
                                for (var i = 0; i < s_SubTotal; ++i) { var s_Piece = s_SubText.Substring(i * 180, System.Math.Min(180, s_SubText.Length - i * 180)); s_SubConsole.Append($"[02:21:{i % 60:00}] [info] [VeniceEXT] [rimeuirecorder] [AS2] #R{m_UiHost.LastRecord["run"]}.i0|{i + 1}|{s_SubTotal}|{s_Piece.Length}|{s_Piece}\n"); }
                                var s_SubParsed = DataRecorder.Parse(s_SubConsole.ToString(), new DataRecorder.ImportReport()) ?? throw new Exception("the sub screen's echo did not parse as a recording");
                                // a list as the movie echoes it (array, or array-like object), or one slot object as the game hands a row's Setup
                                static JToken? Nth(JToken? p_List, int p_Index) => p_List is JArray s_Array ? (p_Index < s_Array.Count ? s_Array[p_Index] : null)
                                    : p_List is JObject s_Object && (s_Object.ContainsKey("length") || s_Object.ContainsKey(p_Index.ToString())) ? s_Object[p_Index.ToString()]
                                    : p_Index == 0 ? p_List as JObject : null;
                                static int CountOf(JToken? p_List) => p_List is JArray s_Array ? s_Array.Count : (int?)(p_List as JObject)?["length"] ?? 0;
                                var s_Rows = s_SubParsed.Widgets.Where(w => ((string?)w["InstanceName"] ?? "").StartsWith("KitSelector_")).Select(w => Nth(w["data"]?["Setup"], 0)).Where(r => r is JObject).Cast<JObject>().ToList();
                                var s_PrimaryRow = s_Rows.FirstOrDefault(r => (string?)r["ItemCategory"] == "PRIMARY WEAPON");
                                var s_PrimaryItem = Nth(s_PrimaryRow?["Items"], 0);
                                if (s_PrimaryItem == null || CountOf(s_PrimaryItem["SlotsData"]) < 1) throw new Exception("the loadout's primary weapon item carries no SlotsData (its row would show lock placeholders): " + (s_PrimaryItem?.ToString(Newtonsoft.Json.Formatting.None) ?? "no primary row among " + s_Rows.Count));
                                var s_SpecRow = s_Rows.FirstOrDefault(r => (string?)r["ItemCategory"] == "SPECIALIZATION");
                                if (s_SpecRow == null || CountOf(s_SpecRow["Items"]) < 1) throw new Exception("the loadout's specialization row is missing or empty (rows seen: " + string.Join(", ", s_Rows.Select(r => (string?)r["ItemCategory"])) + ")");
                                R($"loadout: {s_Rows.Count} rows from the kit's weapon and specialization tables ({string.Join(", ", s_Rows.Select(r => (string?)r["ItemCategory"]))}); the primary weapon carries {CountOf(s_PrimaryItem["SlotsData"])} slot icons");
                                // a pick in a loadout row: KitSelector_02 (SIDEARM) fires SetIndex(1) → the row's SetWeaponCategory (Param 1; six nodes share
                                // that label — the wire's end is resolved by guid) → SetSecondaryWeapon writes CustSecondaryWeapon → SetWeapon (screen output)
                                // → UpdateSoldierLoadout (SetWeaponCustomization) → the stand-in → the sidearm row is built again with the pick selected
                                var s_CustComp = m_WidgetCatalog!.Components.Values.First(c => c.Name.EndsWith("UICustomizationComp", StringComparison.OrdinalIgnoreCase));
                                var s_LogLoadoutPick = LogBox.Text.Length;
                                m_RuffleHost.Dispatch("_global.Data.UIWidgetEventComp", "fireEvent", "_level0.sc2.instance1.KitSelector_02", m_UiHost.EventValue("SetIndex"), 1);
                                for (var i = 0; i < 100 && !LogBox.Text[s_LogLoadoutPick..].Contains("regenerated UICustomizationComp.KitSecondaryWeapon"); ++i) await Task.Delay(50);
                                var s_LoadoutPick = LogBox.Text[s_LogLoadoutPick..];
                                if (!s_LoadoutPick.Contains("SetWeaponCategory: set UICustomizationComp.CustSelectedWeaponCategory = \"1\"") || !s_LoadoutPick.Contains("SetSecondaryWeapon: set UICustomizationComp.CustSecondaryWeapon = \"1\"") || !s_LoadoutPick.Contains("engine action SetWeaponCustomization") || !s_LoadoutPick.Contains("stood in for") || !s_LoadoutPick.Contains("regenerated UICustomizationComp.KitSecondaryWeapon"))
                                    throw new Exception("the loadout pick did not run row → SetWeaponCategory(1) → SetSecondaryWeapon → SetWeapon → UpdateSoldierLoadout → stand-in → KitSecondaryWeapon built again: " + s_LoadoutPick.Trim()[..System.Math.Min(500, s_LoadoutPick.Trim().Length)]);
                                var s_SecondaryItems = ((m_UiHost.Data[s_CustComp.Keys["KitSecondaryWeapon"].ToString()] as JObject)?["Items"] as JArray) ?? new JArray();
                                if (s_SecondaryItems.Count < 2 || (bool?)s_SecondaryItems[1]?["DefaultSelected"] != true) throw new Exception("the sidearm row was not built again with its second item selected");
                                R($"loadout pick: SIDEARM SetIndex(1) → CustSelectedWeaponCategory = 1, CustSecondaryWeapon = 1 → SetWeapon → SetWeaponCustomization stood in → the sidearm row built again with {s_SecondaryItems[1]?["Label"]} selected (selftest_ruffle_subscreen.png)");
                            }
                            await Task.Delay(700);
                            await m_RuffleHost.CaptureAsync(Path.Combine(p_OutDir, "selftest_ruffle_subscreen.png"));
                            // the ACCESSORIES button of the primary weapon row (button1, its BtnLabel1 shown on PC): its release is the row's
                            // BTN_1_RELEASED → OnItemReleased → the screen's AccessoriesButton output → the flow graph pops the loadout and pushes the
                            // accessories screen; BACK there (Confirm) brings the loadout back — as the user goes to and fro
                            var s_LogAcc = LogBox.Text.Length;
                            m_RuffleHost.Dispatch("_level0.sc2.instance1.KitSelector_01.mcContainer.Slot_0", "onButtonReleased", 1);
                            for (var i = 0; i < 600 && !LogBox.Text[s_LogAcc..].Contains("entering customizeaccessoriesscreen: EnterScreen"); ++i) await Task.Delay(50);
                            var s_AccLine = LogBox.Text[s_LogAcc..];
                            if (!s_AccLine.Contains("KitSelector_01 fired OnItemReleased")) throw new Exception("the ACCESSORIES button's release did not reach the host (button1 hidden, or the row not initialised): " + s_AccLine.Trim()[..System.Math.Min(300, s_AccLine.Trim().Length)]);
                            if (!s_AccLine.Contains("AccessoriesButton: screen output")) throw new Exception("OnItemReleased of the primary row is not wired to the loadout's AccessoriesButton output: " + s_AccLine.Trim()[..System.Math.Min(500, s_AccLine.Trim().Length)]);
                            if (!s_AccLine.Contains("pop sc2 customizesoldiersubscreen") || !s_AccLine.Contains("push ui/flow/screen/customizeaccessoriesscreen as sc3")) throw new Exception("AccessoriesButton did not pop the loadout and push the accessories screen: " + s_AccLine.Trim()[..System.Math.Min(500, s_AccLine.Trim().Length)]);
                            if (m_UiHost.Stack.Count != 1 || !m_UiHost.Stack[0].StartsWith("sc3:customizeaccessoriesscreen")) throw new Exception("the stack after ACCESSORIES is " + string.Join(", ", m_UiHost.Stack) + ", expected the accessories screen alone as sc3");
                            s_LogAcc = LogBox.Text.Length;
                            m_RuffleHost.Dispatch("_level0.sc3.instance1.Button_02", "buttonReleased");
                            for (var i = 0; i < 600 && !LogBox.Text[s_LogAcc..].Contains("entering customizesoldiersubscreen: EnterScreen"); ++i) await Task.Delay(50);
                            if (m_UiHost.Stack.Count != 1 || !m_UiHost.Stack[0].StartsWith("sc4:customizesoldiersubscreen")) throw new Exception("BACK on the accessories screen did not bring the loadout back: the stack is " + string.Join(", ", m_UiHost.Stack) + "; " + LogBox.Text[s_LogAcc..].Trim()[..System.Math.Min(400, LogBox.Text[s_LogAcc..].Trim().Length)]);
                            for (var i = 0; i < 200 && (string?)m_UiHost.LastRecord?["screen"] != "customizesoldiersubscreen"; ++i) await Task.Delay(50);
                            await Task.Delay(500);
                            R("loadout ACCESSORIES: the primary row's button1 released → OnItemReleased → AccessoriesButton → the loadout popped, the accessories screen pushed as sc3 and entered; BACK there → Confirm → the loadout again as sc4");
                            // Back (Esc) on the sub screen: its StateNode's Deactivate input-event output, into a comparison of its own
                            s_LogBefore2 = LogBox.Text.Length;
                            if (!m_UiHost.FireBack()) throw new Exception("Back found no Back/Deactivate input on the sub screen's StateNode");
                            await Task.Delay(200);
                            s_Line = LogBox.Text[s_LogBefore2..];
                            if (!s_Line.Contains("input Deactivate on customizesoldiersubscreen") || !s_Line.Contains("SaveWeaponsEnabled: compares")) throw new Exception("Back did not reach the sub screen's SaveWeaponsEnabled comparison: " + s_Line.Trim());
                            var s_SubRecords = (m_UiHost.LastRecord["widgets"] as JArray)?.Count ?? (int?)m_UiHost.LastRecord["widgets"]?["length"] ?? 0;
                            R($"flow graph: {m_UiHost.ParentGraph.Split('/').Last()} — OnItemOver → KitInfoBox_01.OnShow; Confirm → screen output → SaveAccessoriesEnabled generated for the profile → True → pop accessories → store action logged → customizesoldiersubscreen pushed as sc2, loaded, initialised and entered (its recorder echoed {s_SubRecords} records; selftest_ruffle_subscreen.png); Back → Deactivate → SaveWeaponsEnabled");
                            // the KITS screen (customizesoldierscreen): the loadout's Back saves and the flow graph pushes it — one KitViewSlot row per kit of
                            // the team (KitsData), the tab bars, the header; LOADOUT on a row (button1) writes SetKit → the profile moves to that kit
                            // → GotoCustomize → the loadout for it; Back → the KITS screen again; APPEARANCE (button2) → the appearance screen with the
                            // kit's soldier camos; Back
                            if (s_DataCached)
                            {
                                for (var i = 0; i < 400 && !(m_UiHost.Stack.Count == 1 && m_UiHost.Stack[0].EndsWith(":customizesoldierscreen") && (string?)m_UiHost.LastRecord?["screen"] == "customizesoldierscreen" && (string?)m_UiHost.LastRecord?["kind"] == "init"); ++i) await Task.Delay(50);
                                if (m_UiHost.Stack.Count != 1 || !m_UiHost.Stack[0].EndsWith(":customizesoldierscreen")) throw new Exception("the loadout's Back did not bring the KITS screen (customizesoldierscreen) up: the stack is " + string.Join(", ", m_UiHost.Stack));
                                var s_KitsClip = m_UiHost.Stack[0].Split(':')[0];
                                if (!LogBox.Text.Contains("generated UICustomizationComp.KitsData ←")) throw new Exception("the generator did not build the KITS screen's rows (UICustomizationComp.KitsData)");
                                for (var i = 0; i < 100 && !LogBox.Text.Contains($"focus on customizesoldierscreen: KitView_01 "); ++i) await Task.Delay(50);
                                await Task.Delay(800);
                                var s_KitRows = new System.Collections.Generic.List<string>();
                                for (var i = 0; i < 4; ++i) s_KitRows.Add(await ProbeClip($"_level0.{s_KitsClip}.instance1.KitView_01.mcContainer.Slot_{i}.header.text"));
                                if (s_KitRows[0] != "\"ASSAULT\"" || s_KitRows[1] != "\"ENGINEER\"" || s_KitRows[2] != "\"SUPPORT\"" || s_KitRows[3] != "\"RECON\"") throw new Exception("the KITS screen's rows read " + string.Join(", ", s_KitRows) + ", expected the team's four kits ASSAULT, ENGINEER, SUPPORT, RECON");
                                var s_LoadoutBtn = await ProbeClip($"_level0.{s_KitsClip}.instance1.KitView_01.mcContainer.Slot_0.button1.currentLabel");
                                var s_AppearanceBtn = await ProbeClip($"_level0.{s_KitsClip}.instance1.KitView_01.mcContainer.Slot_0.button2.currentLabel");
                                if (s_LoadoutBtn != "\"LOADOUT\"" || s_AppearanceBtn != "\"APPEARANCE\"") throw new Exception($"the kit row's buttons read {s_LoadoutBtn} / {s_AppearanceBtn}, expected \"LOADOUT\" / \"APPEARANCE\" (BtnLabel1/2 of the node)");
                                await m_RuffleHost.CaptureAsync(Path.Combine(p_OutDir, "selftest_ruffle_kits.png"));
                                // LOADOUT on the ENGINEER row: the row activates (Selected → SetKit = 1 → the profile's kit) and its button1 → GotoCustomize → the loadout
                                var s_LogKits = LogBox.Text.Length;
                                m_RuffleHost.Dispatch($"_level0.{s_KitsClip}.instance1.KitView_01.mcContainer.Slot_1", "onButtonReleased", 1);
                                for (var i = 0; i < 600 && !(m_UiHost.Stack.Count == 1 && m_UiHost.Stack[0].EndsWith(":customizesoldiersubscreen") && (string?)m_UiHost.LastRecord?["screen"] == "customizesoldiersubscreen" && (string?)m_UiHost.LastRecord?["kind"] == "init"); ++i) await Task.Delay(50);
                                var s_KitsLine = LogBox.Text[s_LogKits..];
                                if (!s_KitsLine.Contains("SetKit: set UIKitComp.SelectedKit = \"1\"") || !s_KitsLine.Contains("the profile took UIKitComp.SelectedKit = \"1\"")) throw new Exception("LOADOUT on the second kit row did not move the profile to that kit (Selected → SetKit → the stand-in): " + s_KitsLine.Trim()[..System.Math.Min(600, s_KitsLine.Trim().Length)]);
                                if (m_UiHost.Stack.Count != 1 || !m_UiHost.Stack[0].EndsWith(":customizesoldiersubscreen")) throw new Exception("GotoCustomize did not push the loadout: the stack is " + string.Join(", ", m_UiHost.Stack) + "; " + s_KitsLine.Trim()[..System.Math.Min(600, s_KitsLine.Trim().Length)]);
                                if (!string.Equals(DataGenerator.Current.Kit, "gameplay/kits/usengineer", StringComparison.OrdinalIgnoreCase)) throw new Exception("the profile's kit after LOADOUT on the engineer row is " + DataGenerator.Current.Kit);
                                var s_EngineerName = (string?)DataRecorder.Parse(ConsoleOf(m_UiHost.LastRecord!), new DataRecorder.ImportReport())?.InitRecord("TextField_01")?["data"]?["Text"];
                                if (s_EngineerName != "ENGINEER") throw new Exception("the loadout pushed for the engineer kit names " + (s_EngineerName ?? "nothing") + " in its header field, expected ENGINEER");
                                await Task.Delay(500);
                                await m_RuffleHost.CaptureAsync(Path.Combine(p_OutDir, "selftest_ruffle_engineer.png"));
                                // Back → the KITS screen again (the engineer row selected), APPEARANCE on it → the appearance screen: the kit's camos in its row
                                s_LogKits = LogBox.Text.Length;
                                if (!m_UiHost.FireBack()) throw new Exception("Back found no Deactivate input on the loadout's StateNode");
                                for (var i = 0; i < 600 && !(m_UiHost.Stack.Count == 1 && m_UiHost.Stack[0].EndsWith(":customizesoldierscreen") && (string?)m_UiHost.LastRecord?["screen"] == "customizesoldierscreen" && (string?)m_UiHost.LastRecord?["kind"] == "init"); ++i) await Task.Delay(50);
                                if (m_UiHost.Stack.Count != 1 || !m_UiHost.Stack[0].EndsWith(":customizesoldierscreen")) throw new Exception("Back on the engineer loadout did not bring the KITS screen back: " + string.Join(", ", m_UiHost.Stack));
                                s_KitsClip = m_UiHost.Stack[0].Split(':')[0];
                                for (var i = 0; i < 100 && !LogBox.Text[s_LogKits..].Contains("focus on customizesoldierscreen: KitView_01 "); ++i) await Task.Delay(50);
                                await Task.Delay(500);
                                s_LogKits = LogBox.Text.Length;
                                m_RuffleHost.Dispatch($"_level0.{s_KitsClip}.instance1.KitView_01.mcContainer.Slot_1", "onButtonReleased", 2);
                                for (var i = 0; i < 600 && !(m_UiHost.Stack.Count == 1 && m_UiHost.Stack[0].EndsWith(":customizeappearancescreen") && (string?)m_UiHost.LastRecord?["screen"] == "customizeappearancescreen" && (string?)m_UiHost.LastRecord?["kind"] == "init"); ++i) await Task.Delay(50);
                                s_KitsLine = LogBox.Text[s_LogKits..];
                                if (m_UiHost.Stack.Count != 1 || !m_UiHost.Stack[0].EndsWith(":customizeappearancescreen")) throw new Exception("APPEARANCE on the engineer row did not push the appearance screen: the stack is " + string.Join(", ", m_UiHost.Stack) + "; " + s_KitsLine.Trim()[..System.Math.Min(600, s_KitsLine.Trim().Length)]);
                                if (!s_KitsLine.Contains("generated UICustomizationComp.KitAppearance ←")) throw new Exception("the generator did not build the appearance row (UICustomizationComp.KitAppearance)");
                                var s_AppearanceClip = m_UiHost.Stack[0].Split(':')[0];
                                var s_AppearanceRow = DataRecorder.Parse(ConsoleOf(m_UiHost.LastRecord!), new DataRecorder.ImportReport())?.InitRecord("KitSelector_01")?["data"]?["Setup"];
                                var s_CamoCount = CountOfList(s_AppearanceRow is JArray a ? a[0]?["Items"] : s_AppearanceRow?["Items"]);
                                if (s_CamoCount < 2) throw new Exception("the appearance row carries " + s_CamoCount + " camo(s); expected the kit's visual table");
                                for (var i = 0; i < 100 && !LogBox.Text[s_LogKits..].Contains("focus on customizeappearancescreen: KitSelector_01 "); ++i) await Task.Delay(50);
                                await Task.Delay(800);
                                var s_CamoLabel = await ProbeClip($"_level0.{s_AppearanceClip}.instance1.KitSelector_01.mcContainer.Slot_0.header.text");
                                if (s_CamoLabel == "(no echo)" || s_CamoLabel == "\"\"" || s_CamoLabel.Contains("ID_")) throw new Exception("the appearance row's header reads " + s_CamoLabel + ": the camo's name did not reach the row (or was not localised)");
                                await m_RuffleHost.CaptureAsync(Path.Combine(p_OutDir, "selftest_ruffle_appearance.png"));
                                s_LogKits = LogBox.Text.Length;
                                if (!m_UiHost.FireBack()) throw new Exception("Back found no Deactivate input on the appearance screen's StateNode");
                                for (var i = 0; i < 600 && !(m_UiHost.Stack.Count == 1 && m_UiHost.Stack[0].EndsWith(":customizesoldierscreen") && (string?)m_UiHost.LastRecord?["screen"] == "customizesoldierscreen" && (string?)m_UiHost.LastRecord?["kind"] == "init"); ++i) await Task.Delay(50);
                                if (m_UiHost.Stack.Count != 1 || !m_UiHost.Stack[0].EndsWith(":customizesoldierscreen")) throw new Exception("Back on the appearance screen did not bring the KITS screen back: " + string.Join(", ", m_UiHost.Stack) + "; " + LogBox.Text[s_LogKits..].Trim()[..System.Math.Min(600, LogBox.Text[s_LogKits..].Trim().Length)]);
                                R($"kits screen: the loadout's Back saved and pushed customizesoldierscreen — rows {string.Join(", ", s_KitRows)} with buttons {s_LoadoutBtn} / {s_AppearanceBtn} (selftest_ruffle_kits.png); LOADOUT on the engineer row → SetKit(1) → the profile's kit → GotoCustomize → the engineer's loadout (header ENGINEER, selftest_ruffle_engineer.png); Back → the KITS screen; APPEARANCE → customizeappearancescreen with the kit's {s_CamoCount} camos (first: {s_CamoLabel}, selftest_ruffle_appearance.png); Back → the KITS screen");
                                // the vehicle screens: the KITS screen's sub tab LAND (TabBar_02's second button released) → SubTabsLogic → the screen's
                                // LandTab output → the flow graph unspawns the soldier, sets the vehicle category, spawns a vehicle and pushes
                                // customizelandscreen (one row per land vehicle class, the team's vehicle pictured); button1 on the first row → SetVehicle →
                                // GotoCustomize → customizelandsubscreen (the class's parts); Back → the land screen; the AIR tab → customizeairscreen →
                                // the first class → customizeairsubscreen (pilot rows + gunner rows when the vehicle has a gunner seat); Back; KITS tab → the KITS screen
                                // the KITS rows as the game's record shapes them (measured 2026-09-16): four items, the support's icon frame "medic", five slots each,
                                // the support's gadget-1 slot = the fixed ammo bag (its selectable part is empty), ItemImage/Index = the row's number
                                var s_KitsSetup = DataRecorder.Parse(ConsoleOf(m_UiHost.LastRecord!), new DataRecorder.ImportReport())?.InitRecord("KitView_01")?["data"]?["Setup"] as JArray ?? new JArray();
                                if (s_KitsSetup.Count != 4 || (string?)s_KitsSetup[2]?["HeaderIcon"] != "medic" || (s_KitsSetup[2]?["SlotsData"] as JArray)?.Count != 5 || s_KitsSetup[2]?["ItemImage"]?.Type != JTokenType.Integer || s_KitsSetup[2]?["Index"]?.Type != JTokenType.Integer || s_KitsSetup[2]?["DefaultSelected"] != null)
                                    throw new Exception("the KITS rows are not shaped as the game's record: " + s_KitsSetup.ToString(Newtonsoft.Json.Formatting.None)[..System.Math.Min(600, s_KitsSetup.ToString(Newtonsoft.Json.Formatting.None).Length)]);
                                var s_SupportGadget = (string?)(s_KitsSetup[2]?["SlotsData"] as JArray)?[2]?["Label"] ?? "";
                                if (!s_SupportGadget.Contains("AMMO", StringComparison.OrdinalIgnoreCase)) throw new Exception("the support's gadget-1 slot reads \"" + s_SupportGadget + "\", expected the fixed ammo bag (the unlabelled part after GADGET1)");
                                if (m_Rime.HasPartition("gameplay/vehicles/mbtcustomization") && m_Rime.HasPartition("gameplay/vehicles/jetcustomization"))
                                {
                                    s_KitsClip = m_UiHost.Stack[0].Split(':')[0];
                                    async Task<string> TabTo(string p_Screen, int p_Tab, string p_From)
                                    {
                                        var s_Mark = LogBox.Text.Length;
                                        m_RuffleHost!.Dispatch($"_level0.{p_From}.instance1.TabBar_02", "buttonReleased", "button_" + p_Tab);
                                        for (var i = 0; i < 800 && !(m_UiHost!.Stack.Count == 1 && m_UiHost.Stack[0].EndsWith(":" + p_Screen) && (string?)m_UiHost.LastRecord?["screen"] == p_Screen && (string?)m_UiHost.LastRecord?["kind"] == "init"); ++i) await Task.Delay(50);
                                        if (m_UiHost!.Stack.Count != 1 || !m_UiHost.Stack[0].EndsWith(":" + p_Screen)) throw new Exception($"the sub tab {p_Tab} did not bring {p_Screen} up: the stack is {string.Join(", ", m_UiHost.Stack)}; " + LogBox.Text[s_Mark..].Trim()[..System.Math.Min(700, LogBox.Text[s_Mark..].Trim().Length)]);
                                        for (var i = 0; i < 100 && !LogBox.Text[s_Mark..].Contains($"focus on {p_Screen}: KitView_01 "); ++i) await Task.Delay(50);
                                        await Task.Delay(800);
                                        return m_UiHost.Stack[0].Split(':')[0];
                                    }
                                    async Task<string> RowButton(string p_Clip, string p_Widget, int p_Row, int p_Button, string p_Screen)
                                    {
                                        var s_Mark = LogBox.Text.Length;
                                        m_RuffleHost!.Dispatch($"_level0.{p_Clip}.instance1.{p_Widget}.mcContainer.Slot_{p_Row}", "onButtonReleased", p_Button);
                                        for (var i = 0; i < 800 && !(m_UiHost!.Stack.Count == 1 && m_UiHost.Stack[0].EndsWith(":" + p_Screen) && (string?)m_UiHost.LastRecord?["screen"] == p_Screen && (string?)m_UiHost.LastRecord?["kind"] == "init"); ++i) await Task.Delay(50);
                                        if (m_UiHost!.Stack.Count != 1 || !m_UiHost.Stack[0].EndsWith(":" + p_Screen)) throw new Exception($"button {p_Button} on row {p_Row} did not bring {p_Screen} up: the stack is {string.Join(", ", m_UiHost.Stack)}; " + LogBox.Text[s_Mark..].Trim()[..System.Math.Min(700, LogBox.Text[s_Mark..].Trim().Length)]);
                                        for (var i = 0; i < 100 && !LogBox.Text[s_Mark..].Contains($"focus on {p_Screen}: KitSelector_01 "); ++i) await Task.Delay(50);
                                        await Task.Delay(800);
                                        return m_UiHost.Stack[0].Split(':')[0];
                                    }
                                    async Task<string> BackTo(string p_Screen)
                                    {
                                        var s_Mark = LogBox.Text.Length;
                                        if (!m_UiHost!.FireBack()) throw new Exception("Back found no Deactivate input on the screen on top");
                                        for (var i = 0; i < 800 && !(m_UiHost.Stack.Count == 1 && m_UiHost.Stack[0].EndsWith(":" + p_Screen) && (string?)m_UiHost.LastRecord?["screen"] == p_Screen && (string?)m_UiHost.LastRecord?["kind"] == "init"); ++i) await Task.Delay(50);
                                        if (m_UiHost.Stack.Count != 1 || !m_UiHost.Stack[0].EndsWith(":" + p_Screen)) throw new Exception($"Back did not bring {p_Screen} back: the stack is {string.Join(", ", m_UiHost.Stack)}; " + LogBox.Text[s_Mark..].Trim()[..System.Math.Min(700, LogBox.Text[s_Mark..].Trim().Length)]);
                                        await Task.Delay(600);
                                        return m_UiHost.Stack[0].Split(':')[0];
                                    }
                                    var s_LogVeh = LogBox.Text.Length;
                                    var s_LandClip = await TabTo("customizelandscreen", 1, s_KitsClip);
                                    if (!LogBox.Text[s_LogVeh..].Contains("generated UICustomizationComp.LandVehiclesData ←")) throw new Exception("the generator did not build the land vehicle rows (UICustomizationComp.LandVehiclesData)");
                                    // the game's LAND list (measured 2026-09-16): MBT then IFV, each row with the class's icon frame, no picture, six part slots
                                    var s_LandSetup = DataRecorder.Parse(ConsoleOf(m_UiHost.LastRecord!), new DataRecorder.ImportReport())?.InitRecord("KitView_01")?["data"]?["Setup"] as JArray ?? new JArray();
                                    var s_LandRows = s_LandSetup.Count;
                                    // (the echoed record carries the labels as the host localised them on the way to the widget: "MBT" for ID_EOR_SCORINGBUCKET_VEHICLEMBT)
                                    if (s_LandRows != 2 || (string?)s_LandSetup[0]?["HeaderIcon"] != "mbt" || (string?)s_LandSetup[1]?["HeaderIcon"] != "ifv" || (string?)s_LandSetup[0]?["ItemImage"] != "" || (s_LandSetup[0]?["SlotsData"] as JArray)?.Count != 6 || string.IsNullOrEmpty((string?)s_LandSetup[0]?["Label"]) || ((string?)s_LandSetup[0]?["Label"])!.StartsWith("ID_"))
                                        throw new Exception($"the LAND screen's rows are not the game's (MBT, IFV with icon frames mbt/ifv, no picture, six slots): " + s_LandSetup.ToString(Newtonsoft.Json.Formatting.None)[..System.Math.Min(600, s_LandSetup.ToString(Newtonsoft.Json.Formatting.None).Length)]);
                                    var s_LandFirst = await ProbeClip($"_level0.{s_LandClip}.instance1.KitView_01.mcContainer.Slot_0.header.text");
                                    var s_LandTabs = await ProbeClip($"_level0.{s_LandClip}.instance1.TabBar_02.tabBarHolder.button_1.currentLabel");
                                    if (s_LandFirst == "(no echo)" || s_LandFirst == "\"\"" || s_LandFirst.Contains("ID_")) throw new Exception("the first land vehicle row reads " + s_LandFirst);
                                    if (s_LandTabs != "\"LAND\"") throw new Exception("the LAND screen's sub tab reads " + s_LandTabs + ", expected \"LAND\" (the tab names live in the placement's clip variables: localised by the converter)");
                                    await m_RuffleHost.CaptureAsync(Path.Combine(p_OutDir, "selftest_ruffle_land.png"));
                                    s_LogVeh = LogBox.Text.Length;
                                    var s_LandSubClip = await RowButton(s_LandClip, "KitView_01", 0, 1, "customizelandsubscreen");
                                    var s_LandSub = LogBox.Text[s_LogVeh..];
                                    if (!s_LandSub.Contains("SetVehicle: set UICustomizationComp.CustSelectedVehicle = \"0\"") || !s_LandSub.Contains("generated UICustomizationComp.LandVehicleProperty1 ←")) throw new Exception("the vehicle row's button did not write CustSelectedVehicle and build the sub screen's part rows: " + s_LandSub.Trim()[..System.Math.Min(600, s_LandSub.Trim().Length)]);
                                    var s_LandPart = await ProbeClip($"_level0.{s_LandSubClip}.instance1.KitSelector_01.mcContainer.Slot_0.header.text");
                                    var s_LandName = (string?)DataRecorder.Parse(ConsoleOf(m_UiHost.LastRecord!), new DataRecorder.ImportReport())?.InitRecord("TextField_01")?["data"]?["Text"] ?? "(no name)";
                                    if (s_LandPart == "(no echo)" || s_LandPart == "\"\"" || s_LandPart.Contains("ID_")) throw new Exception("the land sub screen's first part row reads " + s_LandPart);
                                    await m_RuffleHost.CaptureAsync(Path.Combine(p_OutDir, "selftest_ruffle_landsub.png"));
                                    s_LandClip = await BackTo("customizelandscreen");
                                    var s_AirClip = await TabTo("customizeairscreen", 2, s_LandClip);
                                    // the game's AIR list (measured 2026-09-16): JET (three pilot slots, three empty) then ATTACK HELI (six slots filled)
                                    var s_AirSetup = DataRecorder.Parse(ConsoleOf(m_UiHost.LastRecord!), new DataRecorder.ImportReport())?.InitRecord("KitView_01")?["data"]?["Setup"] as JArray ?? new JArray();
                                    var s_AirRows = s_AirSetup.Count;
                                    if (s_AirRows != 2 || (string?)s_AirSetup[0]?["HeaderIcon"] != "jets" || (string?)s_AirSetup[1]?["HeaderIcon"] != "aheli" || (string?)(s_AirSetup[0]?["SlotsData"] as JArray)?[3]?["Label"] != "" || string.IsNullOrEmpty((string?)(s_AirSetup[1]?["SlotsData"] as JArray)?[3]?["Label"]))
                                        throw new Exception("the AIR screen's rows are not the game's (JET with empty gunner slots, ATTACK HELI with six): " + s_AirSetup.ToString(Newtonsoft.Json.Formatting.None)[..System.Math.Min(600, s_AirSetup.ToString(Newtonsoft.Json.Formatting.None).Length)]);
                                    s_LogVeh = LogBox.Text.Length;
                                    var s_AirSubClip = await RowButton(s_AirClip, "KitView_01", 0, 1, "customizeairsubscreen");
                                    var s_AirSub = LogBox.Text[s_LogVeh..];
                                    if (!s_AirSub.Contains("generated UICustomizationComp.AirVehiclePilotProperty1 ←") || !s_AirSub.Contains("generated UICustomizationComp.HasGunnerSeat ←")) throw new Exception("the air sub screen's pilot rows / gunner flag were not generated: " + s_AirSub.Trim()[..System.Math.Min(600, s_AirSub.Trim().Length)]);
                                    var s_AirPart = await ProbeClip($"_level0.{s_AirSubClip}.instance1.KitSelector_01.mcContainer.Slot_0.header.text");
                                    var s_Gunner = await ProbeClip($"_level0.{s_AirSubClip}.instance1.KitSelector_04._visible");
                                    if (s_AirPart == "(no echo)" || s_AirPart == "\"\"" || s_AirPart.Contains("ID_")) throw new Exception("the air sub screen's first part row reads " + s_AirPart);
                                    // the jet has no gunner: its gunner rows come hidden with no data, as in the game's record (Visibility false, Setup null)
                                    if (s_Gunner.Trim('"') != "false") throw new Exception("the jet's gunner rows are visible (" + s_Gunner + "); the game hides them (HasGunnerSeat false)");
                                    var s_JetPilot = DataRecorder.Parse(ConsoleOf(m_UiHost.LastRecord!), new DataRecorder.ImportReport())?.InitRecord("KitSelector_01")?["data"]?["Setup"];
                                    var s_JetItem = ((s_JetPilot is JArray ja ? ja[0]?["Items"] : s_JetPilot?["Items"]) as JArray)?.FirstOrDefault() as JObject;
                                    if (s_JetItem == null || s_JetItem["HeaderIcon"] != null || s_JetItem["ServiceStars"] != null || s_JetItem["ShowPlus"] == null) throw new Exception("a vehicle part item is not shaped as the game's (ShowPlus, no HeaderIcon, no ServiceStars): " + (s_JetItem?.ToString(Newtonsoft.Json.Formatting.None) ?? "none"));
                                    await m_RuffleHost.CaptureAsync(Path.Combine(p_OutDir, "selftest_ruffle_airsub.png"));
                                    s_AirClip = await BackTo("customizeairscreen");
                                    s_KitsClip = await TabTo("customizesoldierscreen", 0, s_AirClip);
                                    R($"vehicles: LAND tab → customizelandscreen ({s_LandRows} classes, first {s_LandFirst}, sub tab {s_LandTabs}; selftest_ruffle_land.png) → button1 on the first class → SetVehicle(0) → customizelandsubscreen (vehicle {s_LandName}, first part row {s_LandPart}; selftest_ruffle_landsub.png) → Back; AIR tab → customizeairscreen ({s_AirRows} classes) → the first class → customizeairsubscreen (first part row {s_AirPart}, gunner rows visible {s_Gunner}; selftest_ruffle_airsub.png) → Back; KITS tab → the KITS screen");
                                }
                                else R("vehicles: the vehicle customization assets are not in the cache (a mount fetches them), so the LAND / AIR screens are not walked in this quick run");
                                // the rest of the run reads the assault kit again
                                DataGenerator.SelectKitIndex(0);
                            }
                            await StepResolution(R, p_OutDir);
                            // the second preview: the accessories screen again, fed from its recording
                            var s_LogBefore3 = LogBox.Text.Length;
                            var s_Url2 = RunRufflePreview() ?? throw new Exception("the second Preview in Ruffle did not run");
                            var s_Host2 = m_UiHost ?? throw new Exception("no host after the second preview");
                            if (s_Host2.Recording == null) throw new Exception("the second preview did not load the recording");
                            for (var i = 0; i < 400 && s_Host2.WidgetsInitialised == 0; ++i) await Task.Delay(50);
                            if (s_Host2.WidgetsInitialised == 0) throw new Exception("the second preview did not initialise the screen");
                            if (s_Host2.RecordedWidgetsUsed < 10) throw new Exception($"the second preview used {s_Host2.RecordedWidgetsUsed} recorded records, expected the screen's widgets");
                            if (s_Host2.TextsPushed < 1000) throw new Exception($"the host pushed {s_Host2.TextsPushed} texts to the page, expected the whole database");
                            for (var i = 0; i < 200 && (s_Host2.LastRecord == null || (string?)s_Host2.LastRecord["kind"] != "init"); ++i) await Task.Delay(50);
                            if (s_Host2.LastRecord == null) throw new Exception("the second preview's recorder echo did not arrive");
                            R($"recording round trip: second preview: initializeScreen with {s_Host2.RecordedWidgetsUsed}/{s_Host2.WidgetsInitialised} records from the recording, {s_Host2.TextsPushed} texts on the page");
                            // the icon: the kit row's image manager asks loadTextureAsync, the host answers loadTextureAsyncDone(true) when the texture is to be had,
                            // the movie loads ./img/<path> and the server converts the game's texture to a PNG on the spot — pixels appear in the row
                            for (var i = 0; i < 200 && s_Host2.TexturesAsked == 0; ++i) await Task.Delay(50);
                            if (s_Host2.TexturesAsked == 0) throw new Exception("the kit row with an icon never asked for its texture (loadTextureAsync)");
                            if (s_IconCached)
                            {
                                if (s_Host2.TexturesFound == 0) throw new Exception("the texture is in the cache but the host answered loadTextureAsyncDone(false)");
                                for (var i = 0; i < 200 && !LogBox.Text[s_LogBefore3..].Contains("/ui/assets/img/" + s_IconPath + " -> 200"); ++i) await Task.Delay(50);
                                if (!LogBox.Text[s_LogBefore3..].Contains("/ui/assets/img/" + s_IconPath + " -> 200")) throw new Exception("the second preview did not fetch the icon as ./img/<path> from the preview server (or the server could not convert it)");
                                await Task.Delay(700);
                                var s_IconShot = Path.Combine(p_OutDir, "selftest_ruffle_icon.png");
                                await m_RuffleHost.CaptureAsync(s_IconShot);
                                var s_Row = m_Current.Placements.First(p => p.Name == "KitSelector_05");
                                var s_Decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(new Uri(s_IconShot), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                                var s_Frame = new System.Windows.Media.Imaging.FormatConvertedBitmap(s_Decoder.Frames[0], PixelFormats.Bgra32, null, 0);
                                var s_Px = new byte[s_Frame.PixelWidth * s_Frame.PixelHeight * 4]; s_Frame.CopyPixels(s_Px, s_Frame.PixelWidth * 4, 0);
                                // the player letterboxes 1280x720 into its window: the row's stage rectangle scaled to the shot
                                var s_Scale = System.Math.Min(s_Frame.PixelWidth / 1280.0, s_Frame.PixelHeight / 720.0);
                                var s_OffX = (s_Frame.PixelWidth - 1280 * s_Scale) / 2; var s_OffY = (s_Frame.PixelHeight - 720 * s_Scale) / 2;
                                var s_RowLit = 0;
                                for (var y = (int)(s_OffY + s_Row.Y * s_Scale); y < (int)(s_OffY + (s_Row.Y + 135) * s_Scale) && y < s_Frame.PixelHeight; ++y)
                                    for (var x = (int)(s_OffX + s_Row.X * s_Scale); x < (int)(s_OffX + (s_Row.X + 625) * s_Scale) && x < s_Frame.PixelWidth; ++x)
                                        if (x >= 0 && y >= 0 && (s_Px[(y * s_Frame.PixelWidth + x) * 4] + s_Px[(y * s_Frame.PixelWidth + x) * 4 + 1] + s_Px[(y * s_Frame.PixelWidth + x) * 4 + 2]) / 3 > 60) ++s_RowLit;
                                if (s_RowLit < 800) throw new Exception($"the kit row shows {s_RowLit} lit pixels in its rectangle: the icon (and label) did not render (selftest_ruffle_icon.png)");
                                // two shots, compared: the fixture's row must make this photo differ from the generated-data one
                                if (s_DataCached && File.ReadAllBytes(s_IconShot).SequenceEqual(File.ReadAllBytes(s_DataShot))) throw new Exception("selftest_ruffle_icon.png is byte-identical to selftest_ruffle_data.png: the second preview did not show the fixture's row");
                                R($"icons: loadTextureAsync({s_IconPath}) → found → loadTextureAsyncDone(true) → the second preview fetched ./img/{s_IconPath} (served as PNG from the game's texture) → {s_RowLit} lit pixels in the KitSelector_05 row; the photo differs from the generated-data one (selftest_ruffle_icon.png vs selftest_ruffle_data.png)");
                            }
                            else R($"icons: loadTextureAsync asked {s_Host2.TexturesAsked} texture(s); {s_IconPath} is not in the cache (a mount fetches ui/art), so the render check is skipped in this quick run");
                            // a dialog with its data: Confirm with SaveAccessoriesEnabled held as False → the flow graph's DialogNode pushes the generic
                            // popup with the dialog's title, text and buttons on the keys the popup's widgets bind (over the accessories screen);
                            // OK → PopupButtonReleased(0) → the wire pops the popup → RevertAccessories reads the pressed button's label → ID_M_POPUP_OK
                            // → pop the accessories screen, push the loadout
                            var s_HostShot = Path.Combine(p_OutDir, "selftest_ruffle_host.png");
                            await Task.Delay(500);
                            await m_RuffleHost.CaptureAsync(s_HostShot);
                            var s_BeforeDialog = File.ReadAllBytes(s_HostShot);
                            s_Host2.SetDataKey(s_SaveKey, "False");
                            var s_LogDialog = LogBox.Text.Length;
                            m_RuffleHost.Dispatch("_global.Data.UIWidgetEventComp", "fireEvent", "_level0.sc1.instance1.ConsoleButtonBar_01", s_Host2.EventValue("OnItemOut"), 0);
                            for (var i = 0; i < 600 && !(s_Host2.Stack.Count == 2 && s_Host2.Stack[1].EndsWith(":popupgeneric") && s_Host2.LastRecord != null && (string?)s_Host2.LastRecord["screen"] == "popupgeneric" && (string?)s_Host2.LastRecord["kind"] == "init"); ++i) await Task.Delay(50);
                            var s_DialogLine = LogBox.Text[s_LogDialog..];
                            if (!s_DialogLine.Contains("SaveAccessoriesEnabled: compares UICustomizationComp.SaveAccessoriesEnabled = \"False\"") || !s_DialogLine.Contains(": dialog \"") || !s_DialogLine.Contains("data keys set") || !s_DialogLine.Contains("push ui/flow/screen/popups/popupgeneric as "))
                                throw new Exception("Confirm with the flag False did not open the locked-accessories dialog: " + s_DialogLine.Trim()[..System.Math.Min(600, s_DialogLine.Trim().Length)]);
                            if (s_Host2.Stack.Count != 2 || !s_Host2.Stack[1].EndsWith(":popupgeneric")) throw new Exception("the stack after the dialog is " + string.Join(", ", s_Host2.Stack) + ", expected the popup over the accessories screen");
                            var s_PopupClip = s_Host2.Stack[1].Split(':')[0];
                            if (!s_DialogLine.Contains(s_PopupClip + " popupgeneric loaded: initializeScreen with")) throw new Exception("the popup was not initialised: " + s_DialogLine.Trim()[..System.Math.Min(600, s_DialogLine.Trim().Length)]);
                            await Task.Delay(1500);
                            var s_DialogShot = Path.Combine(p_OutDir, "selftest_ruffle_dialog.png");
                            await m_RuffleHost.CaptureAsync(s_DialogShot);
                            if (File.ReadAllBytes(s_DialogShot).SequenceEqual(s_BeforeDialog)) throw new Exception("selftest_ruffle_dialog.png is byte-identical to the screen before the dialog: the popup did not render");
                            // the popup's buttons as the movie holds them: PopupButtonsManager attaches a Button per label on PC (attachMovie("Button")
                            // → Button.initialize → attachMovie("smallTextButton") for its art, both by name out of the inlined Button.swf) — read as
                            // TEXT: the label the button carries and the width of its art; a popup without OK/CANCEL passed on pixels for a session
                            var s_Btn0 = $"_level0.{s_PopupClip}.instance1.PopupButtonsManager_01.holder.btn0";
                            var s_OkLabel = await ProbeClip(s_Btn0 + ".currentLabel"); var s_OkArt = await ProbeClip(s_Btn0 + ".buttonMc._width");
                            var s_CancelLabel = await ProbeClip($"_level0.{s_PopupClip}.instance1.PopupButtonsManager_01.holder.btn1.currentLabel");
                            if (s_OkLabel != "\"OK\"" || s_CancelLabel != "\"CANCEL\"") throw new Exception($"the popup's buttons read {s_OkLabel} / {s_CancelLabel}, expected \"OK\" / \"CANCEL\" (PopupButtonsManager attached no Button per label, or the labels were not localised)");
                            if (!double.TryParse(s_OkArt.Trim('"'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s_OkWidth) || s_OkWidth < 10) throw new Exception($"the popup's OK button has no art (buttonMc._width = {s_OkArt}): attachMovie(\"smallTextButton\") found nothing in the inlined Button.swf — a stale converted copy in the preview folder?");
                            s_LogDialog = LogBox.Text.Length;
                            // OK pressed as the mouse does it: the Button's release → its releaseFunction → the manager's RELEASED with the label
                            m_RuffleHost.Dispatch(s_Btn0, "buttonReleased");
                            for (var i = 0; i < 600 && !(s_Host2.Stack.Count == 1 && s_Host2.Stack[0].EndsWith(":customizesoldiersubscreen")); ++i) await Task.Delay(50);
                            s_DialogLine = LogBox.Text[s_LogDialog..];
                            // the button fires the label it was given (the localised text here, the id in the game): the comparison takes it back to the id
                            if (!s_DialogLine.Contains("PopupButtonsManager_01 fired OnItemReleased (\"OK\")") || !s_DialogLine.Contains("PopupButtonReleased: screen output") || !s_DialogLine.Contains("RevertAccessories: compares the event's value \"OK\" = the popup's button = \"ID_M_POPUP_OK\"") || !s_DialogLine.Contains("pop " + s_PopupClip + " popupgeneric") || !s_DialogLine.Contains("pop sc1 customizeaccessoriesscreen") || !s_DialogLine.Contains("push ui/flow/screen/customizesoldiersubscreen as "))
                                throw new Exception("OK on the dialog did not run PopupButtonReleased → RevertAccessories → pop popup → pop accessories → push loadout: " + s_DialogLine.Trim()[..System.Math.Min(700, s_DialogLine.Trim().Length)]);
                            if (s_Host2.Stack.Count != 1 || !s_Host2.Stack[0].EndsWith(":customizesoldiersubscreen")) throw new Exception("the stack after OK is " + string.Join(", ", s_Host2.Stack) + ", expected the loadout alone");
                            R($"dialog: Confirm with SaveAccessoriesEnabled = False → the DialogNode pushed popupgeneric as {s_PopupClip} with its title, text and [OK | CANCEL] on the popup's keys, over the accessories screen (selftest_ruffle_dialog.png); the buttons read {s_OkLabel} / {s_CancelLabel} with art {s_OkWidth:0} px wide; OK's button released → PopupButtonReleased(0) → the popup popped → RevertAccessories read the button's label ID_M_POPUP_OK → accessories popped, loadout pushed");
                            await Task.Delay(500);
                            await m_RuffleHost.CaptureAsync(s_HostShot);
                            // the camo screen: the document Data/CamoMenu.seam.json (the shipping one plus a test table entry) turns the camo row of the
                            // accessories screen into the door to a camo screen of its own (family buttons, a grid of thumbnails, the info box), wired
                            // through the customization flow graph; previewed as a third preview with the engineer's SCAR-H — see StepCamoMenu
                            var s_CamoDocSource = Path.Combine(AppContext.BaseDirectory, "Data", "CamoMenu.seam.json");
                            if (File.Exists(s_CamoDocSource) && s_DataCached)
                            {
                                // the run's own document steps aside (its object and its undo history untouched: the editing steps after the previews
                                // undo their way back to a snapshot taken before them) and the camo menu document takes the stage
                                var s_KeepDoc = m_Doc; var s_KeepPath = m_DocPath;
                                var s_CamoDoc = Path.Combine(p_OutDir, "CamoMenu.json");
                                File.Copy(s_CamoDocSource, s_CamoDoc, true);
                                m_Doc = Newtonsoft.Json.JsonConvert.DeserializeObject<ScreenDocument>(File.ReadAllText(s_CamoDoc)) ?? throw new Exception("the camo menu document did not parse");
                                m_DocPath = s_CamoDoc; RegisterNewScreens(); UpdateSaveButtons(); ApplyDocumentToCurrent(); ShowProperties();
                                if (m_Doc.Name != "CustomizeCamoMenu") throw new Exception("the camo menu document did not load: " + m_Doc.Name);
                                var s_CamoEntry = DocScreen("ui/flow/screen/customizeaccessoriesscreen") ?? throw new Exception("the camo menu document has no entry for the accessories screen");
                                if (!s_CamoEntry.Stage.Any(o => o.StartsWith("script:camorow:"))) throw new Exception("the camo menu document carries no camorow script op");
                                await StepCamoMenu(R, p_OutDir);
                                // the run's own document (the same object, its history intact) and profile again
                                m_Doc = s_KeepDoc; m_DocPath = s_KeepPath; RegisterNewScreens(); UpdateSaveButtons(); ApplyDocumentToCurrent(); ShowProperties();
                                await OpenScreen("ui/flow/screen/customizeaccessoriesscreen");
                                if (m_Current == null || m_Current.Movie!.FrameScripts().Contains("camorow")) throw new Exception("the camo menu's stage ops are still on the run's own document after the swap back");
                                SetPreviewProfile("gameplay/kits/usassault", null);
                            }
                            else R("camo menu: skipped (Data/CamoMenu.seam.json missing next to the editor, or the kit data not cached)");
                        }
                    }
                    // copy / paste / duplicate / delete
                    m_SelectedPlacement = s_TextField.Name; m_SelectedNode = s_TextField.Name; m_SelectedKey = null; m_Stage.Selected = s_TextField.Name;
                    var s_StepsClip = m_Undo.Count;
                    if (!CopySelected() || !HasClip) throw new Exception("copy refused");
                    var s_Pasted = PasteClip() ?? throw new Exception("paste refused");
                    var s_PastedPl = m_Current.Placements.First(p => p.Name == s_Pasted.InstanceName);
                    if (s_Pasted.InstanceName == s_TextField.Name || System.Math.Abs(s_PastedPl.X - s_TextField.X - 20) > 0.5 || System.Math.Abs(s_PastedPl.Y - s_TextField.Y - 20) > 0.5) throw new Exception($"pasted {s_Pasted.InstanceName} at ({s_PastedPl.X},{s_PastedPl.Y}), expected +20,+20 from {s_TextField.Name}");
                    if ((string?)s_Pasted.Fields["DataBinding"]?["StaticText"] != "I hate burgers" || m_Undo.Count != s_StepsClip + 1) throw new Exception("the paste did not carry the binding / was not one undo step");
                    if (!DocScreen(s_Screen)!.Stage.Any(o => o.StartsWith("vars:" + s_Pasted.InstanceName + ":") && o.Contains("m_rowType=bold1"))) throw new Exception("the paste did not carry the clip's construct-time variables");
                    if (m_SelectedPlacement != s_Pasted.InstanceName) throw new Exception("the paste is not the selection");
                    var s_Dup = DuplicateSelected() ?? throw new Exception("duplicate refused");
                    if (m_Current.Placements.Count(p => p.Name.StartsWith("TextField_") && p.Added) != 3) throw new Exception("Ctrl+D did not add a third text field");
                    if (!DeleteSelected() || m_Current.Placements.Any(p => p.Name == s_Dup.InstanceName) || DocScreen(s_Screen)!.Nodes.Any(n => n.InstanceName == s_Dup.InstanceName)) throw new Exception("Delete did not take the duplicate away");
                    if (DocScreen(s_Screen)!.Stage.Any(o => o.StartsWith("vars:" + s_Dup.InstanceName + ":"))) throw new Exception("Delete left the clip's vars op behind");
                    // a shipped clip: Delete writes a remove op and the clip leaves the stage (the node stays)
                    m_SelectedPlacement = "Button_02"; m_SelectedNode = "Button_02"; m_SelectedKey = null;
                    if (!DeleteSelected() || m_Current.Placements.Any(p => p.Name == "Button_02") || !DocScreen(s_Screen)!.Stage.Contains("remove:Button_02")) throw new Exception("Delete on a shipped clip did not write a remove op");
                    Undo();
                    if (!m_Current.Placements.Any(p => p.Name == "Button_02")) throw new Exception("undo did not bring the shipped clip back");
                    // a shipped logic node cannot go
                    m_SelectedPlacement = null; m_SelectedNode = "Confirm"; m_SelectedKey = m_Current.AllNodes.First(n => n.Label == "Confirm").Guid;
                    if (DeleteSelected()) throw new Exception("a shipped logic node was deleted");
                    R($"static text: \"I hate burgers\" → UITextDataBinding on {s_TextField.Name}, drawn in its text clip; copy/paste → {s_Pasted.InstanceName} at +20,+20 with the binding (1 step); Ctrl+D → {s_Dup.InstanceName}; Delete took it away; Delete on Button_02 = remove op (undone); Confirm refused");
                    // multi-selection: Ctrl+click adds, a band selects what lies inside it, a drag moves the lot in one step, Delete takes the lot in one step
                    m_Stage.Selected = s_Landed.Name; m_Stage.SelectToggle(s_TextField.Name);
                    if (m_Stage.SelectedSet.Count != 2 || m_Stage.HandleAt(new Point(StageCanvas.RectOf(m_Current.Placements.First(p => p.Name == s_TextField.Name)).Right, StageCanvas.RectOf(m_Current.Placements.First(p => p.Name == s_TextField.Name)).Bottom)) != StageCanvas.HandleKind.None)
                        throw new Exception($"Ctrl+click: {m_Stage.SelectedSet.Count} selected (expected 2, and no handles while several are selected)");
                    var s_LandedX0 = m_Current.Placements.First(p => p.Name == s_Landed.Name).X; var s_TextX0 = m_Current.Placements.First(p => p.Name == s_TextField.Name).X;
                    var s_StepsMulti = m_Undo.Count;
                    m_Stage.MoveSelectedBy(15, 10);
                    if (m_Undo.Count != s_StepsMulti + 1) throw new Exception("moving two placements together is not one undo step");
                    if (System.Math.Abs(m_Current.Placements.First(p => p.Name == s_Landed.Name).X - s_LandedX0 - 15) > 0.5 || System.Math.Abs(m_Current.Placements.First(p => p.Name == s_TextField.Name).X - s_TextX0 - 15) > 0.5) throw new Exception("the two placements did not move together");
                    if (m_Stage.SelectedSet.Count != 2) throw new Exception("the selection did not survive the move");
                    Undo();
                    var s_Band = Rect.Union(StageCanvas.RectOf(m_Current.Placements.First(p => p.Name == s_Landed.Name)), StageCanvas.RectOf(m_Current.Placements.First(p => p.Name == s_TextField.Name)));
                    s_Band.Inflate(6, 6);
                    m_Stage.SelectInBand(s_Band);
                    if (!m_Stage.SelectedSet.Contains(s_Landed.Name) || !m_Stage.SelectedSet.Contains(s_TextField.Name)) throw new Exception("the band did not select what lies inside it: " + string.Join(",", m_Stage.SelectedSet));
                    if (m_Stage.SelectedSet.Any(n => !s_Band.Contains(StageCanvas.RectOf(m_Current.Placements.First(p => p.Name == n))))) throw new Exception("the band selected something outside it");
                    m_Stage.Selected = s_Landed.Name; m_Stage.SelectToggle(s_TextField.Name);
                    var s_StepsDel = m_Undo.Count;
                    if (!DeleteSelected() || m_Current.Placements.Any(p => p.Name == s_Landed.Name || p.Name == s_TextField.Name) || m_Undo.Count != s_StepsDel + 1) throw new Exception("Delete on two selected placements did not take both in one step");
                    Undo();
                    if (!m_Current.Placements.Any(p => p.Name == s_Landed.Name) || !m_Current.Placements.Any(p => p.Name == s_TextField.Name)) throw new Exception("undo did not bring the two placements back");
                    R($"multi-selection: Ctrl+click → 2 selected (no handles), moved together (+15,+10) in 1 step, band around both selects them, Delete took both in 1 step, undo brought them back");
                    // the stage after the drop, the layer selected, the node wired: photographed
                    m_Stage.Selected = s_Landed.Name; m_SelectedPlacement = s_Landed.Name; m_SelectedNode = s_Landed.Name; Viewport.Fit(); await Task.Delay(100);
                    Photograph(Path.Combine(p_OutDir, "selftest_drop.png"));
                    // a node type dropped on the STAGE lands on the graph and the Graph tab comes up; a widget dropped on the GRAPH is a box there
                    if (!HandleStageDrop(new DataObject(NodeTypeFormat, "DataToggleNode"), new Point(50, 50))) throw new Exception("a node type dropped on the stage was refused");
                    if (CentreTabs.SelectedIndex != 1 || !DocScreen(s_Screen)!.Nodes.Any(n => n.Type == "DataToggleNode")) throw new Exception("the node dropped on the stage did not reach the graph with the Graph tab up");
                    var s_OnGraph = HandleGraphDrop(new DataObject(PartitionFormat, "ui/assets/button"), new Point(600, 900)) ?? throw new Exception("a widget dropped on the graph was refused");
                    if (System.Math.Abs(s_OnGraph.GraphX - 600) > 0.5 || System.Math.Abs(s_OnGraph.GraphY - 900) > 0.5 || !m_Current.Placements.Any(p => p.Name == s_OnGraph.InstanceName && p.Added)) throw new Exception($"widget dropped on the graph: box at ({s_OnGraph.GraphX},{s_OnGraph.GraphY}), on stage {m_Current.Placements.Any(p => p.Name == s_OnGraph.InstanceName)}");
                    CentreTabs.SelectedIndex = 0;
                    var s_Steps3 = m_Undo.Count - s_Steps2;
                    for (var i = 0; i < s_Steps3; ++i) if (!Undo()) throw new Exception("undo refused");
                    if (DocJson() != s_Before2) throw new Exception("undoing the drops and edits did not restore the document");
                    R($"drop, end to end: Button on the stage at (300,200) → Layers row selected, properties editable (FocusIndex, scale, +{s_Setting.Name}); graph box with the contract ({s_Box.Outputs.Count} out / {s_Box.Inputs.Count} in); wire OnItemReleased → {s_Refresh.InstanceName}.In taken ({s_V1.Reason}); {s_Lit} inputs lit / {s_Dark} dark while dragging; self-wire and input-as-source refused; {s_Offers.Count} compatible targets offered, + added one; node on stage → graph tab; widget on graph → box at (600,900); {s_Steps3} steps undone");
                    // a screen from nothing: the empty screen's movie and partition cloned under the new name — opened, edited, shown by a
                    // StateNode. It stays in the document for the build (a new screen undone leaves the lists: checked on the way)
                    var s_NewEntry = await NewScreen("UiTestScreen") ?? throw new Exception("New screen refused");
                    Undo();
                    if (m_AllScreens.Contains("ui/flow/screen/uitestscreen") || OpenScreens.Contains("ui/flow/screen/uitestscreen")) throw new Exception("an undone new screen stayed in the lists / tabs");
                    Redo();
                    if (!m_AllScreens.Contains("ui/flow/screen/uitestscreen")) throw new Exception("a redone new screen did not come back to the lists");
                    await OpenScreen("ui/flow/screen/uitestscreen");
                    if (m_Current?.Partition != "ui/flow/screen/uitestscreen" || !m_Current.IsNew || EmptyHint.Visibility == Visibility.Visible) throw new Exception($"the new screen did not open on the stage (current {m_Current?.Partition}, new {m_Current?.IsNew})");
                    if (m_Current.Placements.Count != 1 || m_Current.Placements[0].Name != "InputListener_01" || m_Current.AllNodes.Count != 1) throw new Exception($"the new screen should carry the template's input listener only: {m_Current.Placements.Count} clips, {m_Current.AllNodes.Count} nodes");
                    if (!(ScreenList.ItemsSource as System.Collections.Generic.List<ExplorerItem>)!.Any(i => i.Partition == "ui/flow/screen/uitestscreen")) throw new Exception("the explorer does not list the new screen");
                    if (!HandleStageDrop(new DataObject(PartitionFormat, "ui/assets/button"), new Point(200, 150)) || !m_Current.Placements.Any(p => p.Name == "Button_01" && p.Added)) throw new Exception("a widget could not be dropped on the new screen");
                    // a picture of the user's on the new screen (the picture widget + its Custom texture): the build then ships its texture for real
                    // (the recipe's texture lines run against the game)
                    if (!HandleStageDrop(new DataObject(PartitionFormat, c_ImageWidget), new Point(100, 100))) throw new Exception("the picture widget could not be dropped on the new screen");
                    var s_PictureClip = m_Current.Placements.First(p => p.Added && p.Name.StartsWith("ImageManager_")).Name;
                    var s_PictureEntry = SetCustomTextureFile(s_PictureClip, WriteTestPng(p_OutDir)) ?? throw new Exception("Custom texture refused the test PNG on the new screen");
                    FitBoxToPicture(s_PictureClip, 98, 58);
                    if (!m_Current.Placements.Any(p => p.Name == s_PictureClip && p.Added) || m_Doc.Images.Count != 1 || s_PictureEntry.Name != "test_image") throw new Exception($"the picture did not land on the new screen ({s_PictureClip}, {m_Doc.Images.Count} pictures)");
                    await OpenScreen("ui/flow/graph/spawn/customizationgraph");
                    var s_Show = HandleGraphDrop(new DataObject(PartitionFormat, "ui/flow/screen/uitestscreen"), new Point(500, 500)) ?? throw new Exception("the new screen was refused on a flow graph");
                    if (s_Show.Type != "StateNode" || (string?)s_Show.Fields["Screen"] != "ui/flow/screen/uitestscreen" || s_Show.InstanceName != "UiTestScreen") throw new Exception($"the new screen made {s_Show.Type} '{s_Show.InstanceName}' → {s_Show.Fields["Screen"]}");
                    if (!RefCandidates("UIScreenAsset").Contains("ui/flow/screen/uitestscreen")) throw new Exception("the Screen picker does not offer the new screen");
                    Undo();   // the StateNode; the new screen and its button stay for the build
                    await OpenScreen(s_Screen);
                    R($"new screen: ui/flow/screen/uitestscreen (asset UI/Flow/Screen/UiTestScreen) from {s_NewEntry.Template}: 1 template clip, Button_01 dropped on it, listed in the explorer and the Screen picker, a StateNode 'UiTestScreen' showed it on customizationgraph; undone it left the lists, redone it came back");
                    // the explorer: a press selects, the release opens, a drag in between cancels the open
                    DeferExplorerOpens = true;
                    var s_Soldier = (ScreenList.ItemsSource as System.Collections.Generic.List<ExplorerItem>)!.First(i => i.Partition == "ui/flow/screen/customizesoldierscreen");
                    ScreenList.SelectedItem = s_Soldier; await Task.Delay(50);
                    if (m_Current.Partition != s_Screen) throw new Exception("a press on a screen opened it before the release");
                    CancelPendingOpen();
                    if (await CommitPendingOpen() || m_Current.Partition != s_Screen) throw new Exception("a press that became a drag still opened the screen");
                    ScreenList.SelectedItem = null; ScreenList.SelectedItem = s_Soldier; await Task.Delay(50);
                    if (!await CommitPendingOpen() || m_Current.Partition != "ui/flow/screen/customizesoldierscreen") throw new Exception("the release did not open the pressed screen");
                    DeferExplorerOpens = false;
                    await OpenScreen(s_Screen);
                    // a press whose release the list never sees (the up event went elsewhere) still opens: the pending screen is
                    // committed as soon as the button is seen up
                    PendOpenForTest("ui/flow/screen/customizesoldierscreen");
                    for (var i = 0; i < 40 && m_Current.Partition != "ui/flow/screen/customizesoldierscreen"; ++i) await Task.Delay(50);
                    if (m_Current.Partition != "ui/flow/screen/customizesoldierscreen") throw new Exception("a pending open with the button up was never committed");
                    await OpenScreen(s_Screen);
                    R("explorer: a press selects and the release opens; a press that turns into a drag opens nothing; a press with no release seen opens anyway");
                    // the type filter: every screen of the database at once, whatever its folder (the HUD, the frontend, the popups…); back to folders after
                    TypeFilter.SelectedItem = "UIScreenAsset"; await Task.Delay(50);
                    var s_Screens = ScreenList.ItemsSource as System.Collections.Generic.List<ExplorerItem> ?? throw new Exception("no list with the type filter");
                    // 241 under ui/flow/screen + End Game's 2 under ui/xp5/flow/screen (the CTF HUD and its ticket counter), which a folder walk of ui/flow never showed
                    var s_ExpectedScreens = 243 + m_Doc.Screens.Count(s => s.New);   // the game's, plus the ones this document made
                    if (s_Screens.Count != s_ExpectedScreens || !s_Screens.Any(i => i.Partition == "ui/flow/screen/hudmpscreen") || !s_Screens.Any(i => i.Partition == "ui/flow/screen/frontend/mainmenuscreenpc") || !s_Screens.Any(i => i.Partition == "ui/xp5/flow/screen/hudctfscreen") || s_Screens.Any(i => i.Type != "UIScreenAsset"))
                        throw new Exception($"type filter UIScreenAsset lists {s_Screens.Count} rows (expected {s_ExpectedScreens}: the game's 243 = 241 + End Game's 2, plus the document's new ones)");
                    ScreenFilter.Text = "hud";
                    var s_Hud = ScreenList.ItemsSource as System.Collections.Generic.List<ExplorerItem>;
                    if (s_Hud == null || s_Hud.Count < 10 || s_Hud.Any(i => !i.Partition.Contains("hud"))) throw new Exception($"type filter + search 'hud': {s_Hud?.Count} rows");
                    ScreenFilter.Text = ""; TypeFilter.SelectedItem = "(all types)"; await Task.Delay(50);
                    if ((ScreenList.ItemsSource as System.Collections.Generic.List<ExplorerItem>)?.Any(i => i.Partition == "ui/flow/screen/frontend/mainmenuscreenpc") != false) throw new Exception("the folder view did not come back after the type filter");
                    // the wheel button closes a tab
                    var s_TabCount = OpenScreens.Count;
                    var s_Victim = OpenScreens.First(p => p != m_Current.Partition);
                    var s_TabBorder = OpenTabs.Children.OfType<System.Windows.Controls.Border>().First(b => (string)b.Tag == s_Victim);
                    s_TabBorder.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Middle) { RoutedEvent = UIElement.MouseDownEvent });
                    if (OpenScreens.Count != s_TabCount - 1 || OpenScreens.Contains(s_Victim) || m_Current.Partition != s_Screen) throw new Exception($"middle click did not close the tab {s_Victim}");
                    await OpenScreen(s_Victim); await OpenScreen(s_Screen);
                    R($"explorer: type filter UIScreenAsset = {s_Screens.Count} screens ({s_Hud.Count} with 'hud'); middle click closed the {s_Victim.Split('/').Last()} tab");
                    // the real thing: the OLE gesture through the mouse (opt-in, it moves the pointer)
                    if (Environment.GetEnvironmentVariable("RUE_SELFTEST_MOUSE") == "1")
                    {
                        var s_Before3 = DocJson(); var s_Steps4 = m_Undo.Count;
                        ToolboxTabs.SelectedIndex = 0; PaletteFilter.Text = "button"; UpdateLayout();
                        PaletteList.SelectedItem = "button"; PaletteList.ScrollIntoView("button"); UpdateLayout();
                        // the drop lands on the Stage tab: the steps before leave the Graph tab showing, and a tab's content that is not
                        // showing is out of the visual tree (PointToScreen: "not connected to a PresentationSource")
                        CentreTabs.SelectedIndex = 0; UpdateLayout();
                        // the player's window is owned by the editor and stays above it: over the palette it would take the pointer
                        // ("over nothing", no drag) — out of the way for the gesture, back afterwards
                        var s_PreviewShown = m_RuffleHost?.IsVisible == true;
                        if (s_PreviewShown) m_RuffleHost!.Hide();
                        Activate(); Topmost = true; await Task.Delay(300);
                        PaletteList.SelectedItem = "button"; PaletteList.ScrollIntoView("button"); UpdateLayout();
                        var s_Row = PaletteList.ItemContainerGenerator.ContainerFromItem("button") as System.Windows.Controls.ListBoxItem ?? throw new Exception("no palette row for 'button'");
                        if (System.Windows.PresentationSource.FromVisual(s_Row) == null) throw new Exception("the palette row for 'button' is not in the window's visual tree (the list was rebuilt after the filter, or the Widgets tab is not showing)");
                        if (System.Windows.PresentationSource.FromVisual(Viewport) == null) throw new Exception("the stage is not in the window's visual tree (the Stage tab is not showing)");
                        var s_FromPt = s_Row.PointToScreen(new Point(s_Row.ActualWidth / 2, s_Row.ActualHeight / 2));
                        var s_ViewPt = new Point(Viewport.ActualWidth * 0.5, Viewport.ActualHeight * 0.45);
                        var s_ToPt = Viewport.PointToScreen(s_ViewPt);
                        var s_Expected = Viewport.ToStage(s_ViewPt);   // measured again from the real pointer just before the release
                        NativeInput.GetCursorPos(out var s_OldCursor);
                        var s_LogMark = LogBox.Text.Length;
                        var s_Trace = new System.Text.StringBuilder();
                        DragSourceTrace.Clear();
                        var s_Moves = new System.Collections.Generic.List<string>();
                        MouseEventHandler s_MoveSpy = (_, e2) => { if (s_Moves.Count < 40) s_Moves.Add($"({e2.GetPosition(RootGrid).X:0},{e2.GetPosition(RootGrid).Y:0}) over {(e2.OriginalSource as FrameworkElement)?.GetType().Name}"); };
                        PreviewMouseMove += s_MoveSpy;
                        // where the window's Drop event says the pointer was released (stage px): the truth the placement must match
                        Point? s_DropAt = null;
                        Action<IDataObject, Point> s_DropSpy = (_, p) => s_DropAt = p;
                        Viewport.Dropped += s_DropSpy;
                        try
                        {
                            // the pointer must sit on the 'button' row before the press: the palette list can still re-layout (the filter's
                            // deferred rebuild, a scroll) after the row was measured — measured again and moved again until the row under
                            // the pointer is the one asked for (a press on the row below started no drag)
                            for (var s_Try = 0; s_Try < 4; ++s_Try)
                            {
                                s_FromPt = s_Row.PointToScreen(new Point(s_Row.ActualWidth / 2, s_Row.ActualHeight / 2));
                                NativeInput.MoveTo(s_FromPt); await Task.Delay(150);
                                DependencyObject? s_Under = Mouse.DirectlyOver as DependencyObject;
                                while (s_Under != null && s_Under is not System.Windows.Controls.ListBoxItem) s_Under = VisualTreeHelper.GetParent(s_Under);
                                if (ReferenceEquals(s_Under, s_Row)) break;
                                s_Trace.Append($"try {s_Try}: the pointer at ({s_FromPt.X:0},{s_FromPt.Y:0}) is over {(s_Under as System.Windows.Controls.ListBoxItem)?.Content ?? Mouse.DirectlyOver?.GetType().Name ?? "nothing"}, not the 'button' row; ");
                                PaletteList.ScrollIntoView("button"); UpdateLayout(); await Task.Delay(250);
                            }
                            NativeInput.GetCursorPos(out var s_At1);
                            s_Trace.Append($"pointer asked ({s_FromPt.X:0},{s_FromPt.Y:0}) is at ({s_At1.X},{s_At1.Y}); over {Mouse.DirectlyOver?.GetType().Name ?? "nothing"}; window active {IsActive}; ");
                            NativeInput.LeftDown(); await Task.Delay(150);
                            s_Trace.Append($"after press: button {Mouse.LeftButton}, selected '{PaletteList.SelectedItem}', drag source {(m_DragSource != null ? "armed" : "none")}; ");
                            for (var i = 1; i <= 16; ++i) { NativeInput.MoveTo(new Point(s_FromPt.X + (s_ToPt.X - s_FromPt.X) * i / 16.0, s_FromPt.Y + (s_ToPt.Y - s_FromPt.Y) * i / 16.0)); await Task.Delay(40); }
                            await Task.Delay(200);
                            NativeInput.GetCursorPos(out var s_At2);
                            // where the pointer really is, in stage px, through the window's own screen mapping (the window may sit anywhere)
                            s_Expected = Viewport.ToStage(Viewport.PointFromScreen(new Point(s_At2.X, s_At2.Y)));
                            s_Trace.Append($"released at ({s_At2.X},{s_At2.Y}) asked ({s_ToPt.X:0},{s_ToPt.Y:0}) = stage ({s_Expected.X:0},{s_Expected.Y:0}) at zoom {Viewport.Scale:0.00}; source saw: {string.Join(" / ", DragSourceTrace)}; window saw {s_Moves.Count} moves: {string.Join(" / ", s_Moves.Take(12))}");
                            NativeInput.LeftUp();
                            for (var i = 0; i < 60 && !LogBox.Text[s_LogMark..].Contains("[drop] ui/assets/button ->"); ++i) await Task.Delay(50);
                        }
                        finally { Topmost = false; NativeInput.SetCursorPos(s_OldCursor.X, s_OldCursor.Y); PaletteFilter.Text = ""; PreviewMouseMove -= s_MoveSpy; Viewport.Dropped -= s_DropSpy; if (s_PreviewShown) m_RuffleHost?.Show(); }
                        var s_Tail = LogBox.Text[s_LogMark..];
                        var s_DropLines = string.Join(" | ", s_Tail.Split('\n').Where(l => l.Contains("[drop]")).Select(l => l.Trim()));
                        if (!s_Tail.Contains("[drop] dragging rime/partition ui/assets/button")) throw new Exception("the press+move on the palette did not start a drag; " + s_Trace + "; log: " + s_DropLines);
                        if (!s_Tail.Contains("[drop] stage: drop rime/partition ui/assets/button")) throw new Exception("the stage got no drop; " + s_Trace + "; log: " + s_DropLines);
                        var s_ByMouse = m_Current!.Placements.FirstOrDefault(p => p.Name.StartsWith("Button_") && p.Added) ?? throw new Exception("no Button placed by the mouse drop; " + s_Trace + "; log: " + s_DropLines);
                        if (s_DropAt == null) throw new Exception("the stage's Drop event did not fire; " + s_Trace);
                        // the placement sits where the Drop event put the pointer (the pointer itself may drift a few px between the last move and the release)
                        if (System.Math.Abs(s_ByMouse.X - s_DropAt.Value.X) > 1 || System.Math.Abs(s_ByMouse.Y - s_DropAt.Value.Y) > 1) throw new Exception($"the mouse drop landed at ({s_ByMouse.X},{s_ByMouse.Y}), the pointer was released at stage ({s_DropAt.Value.X:0},{s_DropAt.Value.Y:0}); {s_Trace}; log: {s_DropLines}");
                        if (System.Math.Abs(s_DropAt.Value.X - s_Expected.X) > 40 || System.Math.Abs(s_DropAt.Value.Y - s_Expected.Y) > 40) throw new Exception($"the release point ({s_DropAt.Value.X:0},{s_DropAt.Value.Y:0}) is far from where the pointer was sent ({s_Expected.X:0},{s_Expected.Y:0}); {s_Trace}");
                        if (LayerList.SelectedIndex != m_Current.Placements.OrderByDescending(p => p.Depth).ToList().FindIndex(p => p.Name == s_ByMouse.Name)) throw new Exception("Layers did not select the widget dropped by the mouse");
                        R($"mouse gesture (real OLE drag, pointer moved): palette 'button' → stage ({s_ByMouse.X},{s_ByMouse.Y}) = the release point ({s_DropAt.Value.X:0},{s_DropAt.Value.Y:0}); pointer sent to ({s_Expected.X:0},{s_Expected.Y:0}); Layers selected it; log: {s_DropLines}");
                        for (var i = m_Undo.Count; i > s_Steps4; --i) Undo();
                        if (DocJson() != s_Before3) throw new Exception("undoing the mouse drop did not restore the document");
                    }
                    else R("mouse gesture: skipped (RUE_SELFTEST_MOUSE=1 runs the real OLE drag with the pointer)");
                }
                // the user's path to the mount: the editor came up from the cache, screens are open — now the Mount button. Layers, the
                // explorer and the open screen must survive it, the button must say what it is doing, and nothing may error.
                if (!s_Quick && !m_Rime.IsMounted)
                {
                    var s_LayersBefore = LayerList.Items.Count; var s_Was = m_Current!.Partition; var s_LogMark = LogBox.Text.Length;
                    if (s_LayersBefore == 0) throw new Exception("no layers before the mount");
                    MountButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    if (MountButton.IsEnabled || MountButton.Content?.ToString() != "Mounting…") throw new Exception($"the Mount button gave no sign of mounting (enabled {MountButton.IsEnabled}, '{MountButton.Content}')");
                    for (var i = 0; i < 6000 && !(StatusText.Text.StartsWith("mounted + cached") || StatusText.Text.StartsWith("mount failed")); ++i) await Task.Delay(100);
                    if (!StatusText.Text.StartsWith("mounted + cached")) throw new Exception("the mount did not finish: status '" + StatusText.Text + "'\n" + LogBox.Text[s_LogMark..]);
                    if (!m_Rime.IsMounted) throw new Exception("not mounted after the button");
                    if (m_Current?.Partition != s_Was) throw new Exception("the mount changed the open screen");
                    if (LayerList.Items.Count != s_LayersBefore) throw new Exception($"Layers: {LayerList.Items.Count} rows after the mount, {s_LayersBefore} before");
                    if ((ScreenList.ItemsSource as System.Collections.Generic.List<ExplorerItem>)?.Count is not > 0) throw new Exception("the explorer list is empty after the mount");
                    if (LogBox.Text[s_LogMark..].Contains("ERROR")) throw new Exception("an error was logged during the mount:\n" + LogBox.Text[s_LogMark..]);
                    if (MountButton.Content?.ToString() != "Mounted") throw new Exception($"the Mount button reads '{MountButton.Content}' after the mount");
                    Photograph(Path.Combine(p_OutDir, "selftest_mounted.png"));
                    R($"mount through the button with {s_Was.Split('/').Last()} open: {LayerList.Items.Count} layers kept, explorer {(ScreenList.ItemsSource as System.Collections.Generic.List<ExplorerItem>)!.Count} rows, button 'Mounted', status '{StatusText.Text}'");
                }
                R($"screen loaded: {m_Current.Widgets.Count} widget nodes, {m_Current.Placements.Count} placements, {m_Current.Connections.Count} connections");
                var s_Known = m_Current.Placements.Count(p => !p.Bounds.Empty);
                R($"placements with static bounds: {s_Known}/{m_Current.Placements.Count}");

                // pick Grid in the palette and add it
                PaletteList.SelectedItem = "grid";
                OnAddWidget(this, null!);
                var s_Entry = DocScreen(s_Screen) ?? throw new Exception("no document entry after add");
                var s_Node = s_Entry.Nodes.Single();
                R($"added node {s_Node.InstanceName}; stage ops: {string.Join(" ; ", s_Entry.Stage)}");
                if (!m_Current.Placements.Any(p => p.Name == s_Node.InstanceName && p.Added)) throw new Exception("added placement not on the stage");
                // the Grid came with its usual construct-time variables (i_cellType…); the reference movie that ran in game predates them and the
                // Grid reads its settings from initData anyway: taken out so the byte comparison below still measures the stage edit
                var s_GridVars = s_Entry.Stage.FirstOrDefault(o => o.StartsWith("vars:" + s_Node.InstanceName + ":")) ?? throw new Exception("the added Grid got no construct-time variables");
                if (!s_GridVars.Contains("i_cellType=")) throw new Exception("the Grid's vars op lacks i_cellType: " + s_GridVars);
                s_Entry.Stage.Remove(s_GridVars);
                ApplyDocumentToCurrent();

                // the same configuration the hand-built mod used, entered as the user would (document fields)
                s_Entry.Stage.RemoveAll(o => o.StartsWith("add:"));
                s_Entry.Stage.Add($"add:10:CamoGrid_01:11:11:740:260:4.1463:4.8387");
                s_Node.InstanceName = "CamoGrid_01"; s_Node.FocusIndex = 5;
                foreach (var (k, v) in new[] { ("p_cellType", "KitCell"), ("p_selectedIndex", "0"), ("p_highlightInactive", "0"), ("p_keyboardNavigation", "0"), ("p_dynamicLoading", "0"),
                                               ("p_itemPicker", "0"), ("p_animTime", "0.5"), ("p_itemPickerAlphaMin", "100"), ("p_itemPickerAlphaMax", "100"), ("p_fbTag", "CAMO") })
                    s_Node.Properties[k] = v;
                s_Node.Binding = new BindingEntry { DataName = "Setup", DataKey = -1858312129, CategoryFromNode = "KitSelector_05" };
                s_Node.Connections.Add(new ConnectionEntry { Event = "OnItemOver", ToNode = "KitInfoBox_01", ToEvent = "OnShow" });
                s_Node.Connections.Add(new ConnectionEntry { Event = "OnItemReleased", ToNode = "Confirm", ToField = "inValue" });
                m_SelectedPlacement = "CamoGrid_01";
                ApplyDocumentToCurrent();
                m_Stage.Selected = "CamoGrid_01";
                await Task.Delay(100);

                // the view: Fit shows the whole stage, zooming around a point keeps that point still, the slider follows
                Viewport.Fit();
                var s_TopLeft = Viewport.ToStage(new Point(0, 0)); var s_BottomRight = Viewport.ToStage(new Point(Viewport.ActualWidth, Viewport.ActualHeight));
                if (s_TopLeft.X > 0 || s_TopLeft.Y > 0 || s_BottomRight.X < StageCanvas.StageWidth || s_BottomRight.Y < StageCanvas.StageHeight)
                    throw new Exception("Fit does not show the whole stage");
                var s_Anchor = new Point(Viewport.ActualWidth * 0.3, Viewport.ActualHeight * 0.6);
                var s_Before = Viewport.ToStage(s_Anchor);
                Viewport.SetScale(Viewport.Scale * 1.8, s_Anchor);
                var s_After = Viewport.ToStage(s_Anchor);
                if (System.Math.Abs(s_Before.X - s_After.X) > 0.01 || System.Math.Abs(s_Before.Y - s_After.Y) > 0.01)
                    throw new Exception($"zoom moved the anchored point: {s_Before} -> {s_After}");
                if (System.Math.Abs(ZoomSlider.Value - Viewport.Scale) > 1e-3) throw new Exception("slider did not follow the viewport zoom");
                R($"view: fit then zoom ×1.8 around a point keeps it still; slider = {ZoomSlider.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
                Viewport.Fit();

                // photograph the window as the user sees it
                Photograph(Path.Combine(p_OutDir, "selftest.png"));
                R("window photographed");

                // the properties panel: a shipped widget of THIS screen shows every field of its type with the values the screen ships,
                // grouped by declaring type, its placement, its binding as a whole instance, its properties, its event contract and wiring
                // the widget catalogue builds in the background after the cache opens: wait for it like the user would
                if (m_WidgetCatalog == null && !m_CatalogBuilding) EnsureWidgetCatalog();
                for (var i = 0; i < 1200 && m_CatalogBuilding; ++i) await Task.Delay(100);
                if (m_WidgetCatalog == null) throw new Exception("the widget catalogue was not built");
                var s_Order = m_Current.Placements.OrderByDescending(p => p.Depth).ToList();
                LayerList.SelectedIndex = s_Order.FindIndex(p => p.Name == "KitSelector_05");
                await Task.Delay(50);
                if (m_SelectedPlacement != "KitSelector_05") throw new Exception("picking a layer did not select the placement");
                System.Collections.Generic.IEnumerable<PropRow> Flatten(System.Collections.Generic.IEnumerable<PropRow> p_Rows) => p_Rows.SelectMany(r => new[] { r }.Concat(Flatten(r.Children)));
                PropRow Row(string p_Path) => Flatten(PropertiesGrid.Rows).FirstOrDefault(r => r.Path == p_Path) ?? throw new Exception("no property row " + p_Path + " (have: " + string.Join(", ", Flatten(PropertiesGrid.Rows).Select(r => r.Path).Take(40)) + ")");
                var s_Cats = PropertiesGrid.Rows.Where(r => r.IsCategory).Select(r => r.Name).ToList();
                foreach (var c in new[] { "Document", "Screen", "Placement", "UINodeData", "WidgetNode", "Ports", "Connections", "Widget events (contract)" })
                    if (!s_Cats.Contains(c)) throw new Exception($"properties: category '{c}' missing (have {string.Join(", ", s_Cats)})");
                if ((long)Row("f.FocusIndex").Value! != 4) throw new Exception("KitSelector_05 FocusIndex should read 4 from the shipped screen");
                if (Row("f.WidgetAsset").Value?.ToString() != "ui/assets/kitselector") throw new Exception("WidgetAsset row: " + Row("f.WidgetAsset").Value);
                if (Row("pl.x").Modified || Row("f.FocusIndex").Modified) throw new Exception("nothing is modified before an edit");
                var s_Bind = Row("f.DataBinding");
                if (s_Bind.TypeName != "UIDynamicDataBinding" || Row("f.DataBinding.Bindings").Children.Count != 1) throw new Exception($"binding row: {s_Bind.TypeName} with {Row("f.DataBinding.Bindings").Children.Count} sources");
                // the DataKey is shown as the component's SOURCE by name (the number is the fb hash of UI_<COMP>_<SOURCE>):
                // -1858312129 = UICustomizationComp.WeaponAccessory4 — the camo row of the accessories screen reads accessory slot 4
                var s_KeyRow = Row("f.DataBinding.Bindings[0].DataKey");
                if (Row("f.DataBinding.Bindings[0].DataCategory").Value?.ToString() != "ui/uicomponents/uicustomizationcomp") throw new Exception("binding source rows do not carry the shipped DataCategory");
                if (s_KeyRow.Name != "DataKey (source)" || s_KeyRow.Value?.ToString() != "WeaponAccessory4") throw new Exception($"DataKey row: '{s_KeyRow.Name}' = '{s_KeyRow.Value}', expected the source WeaponAccessory4 (key -1858312129)");
                if (s_KeyRow.Choices == null || !s_KeyRow.Choices.Contains("ButtonLayout")) throw new Exception("the source drop-down does not list the component's sources");
                if (DataKeys.Compute("UI/UIComponents/UICustomizationComp", "WeaponAccessory4") != -1858312129) throw new Exception("DataKeys.Compute disagrees with the shipped key");
                var s_NameRow = Row("f.DataBinding.Bindings[0].DataName");
                if (s_NameRow.Choices == null || !s_NameRow.Choices.Contains("Setup")) throw new Exception("DataName does not suggest the widget's data channels (KitView: Setup, Visibility)");
                // a free-text row with suggestions is an editable combo, and it must SHOW its text (the first template showed a blank box)
                PropertiesGrid.SetExpanded("cat:WidgetNode", true); PropertiesGrid.SetExpanded("f.DataBinding", true); PropertiesGrid.SetExpanded("f.DataBinding.Bindings", true); PropertiesGrid.SetExpanded("f.DataBinding.Bindings[0]", true);
                UpdateLayout();
                if (PropertiesGrid.Editors.TryGetValue("f.DataBinding.Bindings[0].DataName", out var s_NameEditor) && s_NameEditor is System.Windows.Controls.ComboBox s_NameCombo)
                {
                    if (!s_NameCombo.IsEditable || s_NameCombo.Text != "Setup") throw new Exception($"DataName editor: editable={s_NameCombo.IsEditable} text='{s_NameCombo.Text}'");
                    var s_TextBox = s_NameCombo.Template?.FindName("PART_EditableTextBox", s_NameCombo) as System.Windows.Controls.TextBox;
                    if (s_TextBox == null || s_TextBox.Visibility != Visibility.Visible || s_TextBox.Text != "Setup") throw new Exception($"the editable combo's text box is not showing 'Setup' (visible={s_TextBox?.Visibility}, text='{s_TextBox?.Text}')");
                }
                else throw new Exception("DataName has no combo editor: " + string.Join(", ", PropertiesGrid.Editors.Keys.Where(k => k.Contains("DataName"))));
                // the widget's own settings, from its ActionScript: KitView reads BtnLabel1/2 (already set), the catalogue adds SendIndexInArray…
                var s_SettingsCat = PropertiesGrid.Rows.FirstOrDefault(r => r.IsCategory && r.Name.StartsWith("Widget settings (Widget.CustomizeKit.KitView")) ?? throw new Exception("no 'Widget settings' category from the widget's code (have: " + string.Join(", ", PropertiesGrid.Rows.Where(r => r.IsCategory).Select(r => r.Name)) + ")");
                if (!s_SettingsCat.Children.Any(r => r.Name == "SendIndexInArray")) throw new Exception("the settings category lacks SendIndexInArray (set on 19 nodes)");
                var s_ChanCat = PropertiesGrid.Rows.FirstOrDefault(r => r.IsCategory && r.Name.StartsWith("Data channels")) ?? throw new Exception("no data channels category");
                if (!s_ChanCat.Children.Any(r => r.Name == "Setup")) throw new Exception("data channels lack Setup");
                // the screen's own fields, with the shipped values, and the movie's size
                if (Row("scr.f.Modal").Value?.Type != JTokenType.Boolean || (bool)Row("scr.f.Modal").Value! != false) throw new Exception("screen field Modal not read from the asset: " + Row("scr.f.Modal").Value);
                if (Row("scr.size").Text != "1280/720") throw new Exception("screen size row: " + Row("scr.size").Text);
                // every clip inside the widgets is outlined with 'All clips': the renderer measured their bounds
                var s_Inner = Renderer().PlacementBounds;
                if (s_Inner.Count < m_Current.Placements.Count + 10) throw new Exception($"inner clip bounds: {s_Inner.Count} (placements {m_Current.Placements.Count})");
                if (!s_Inner.Keys.Any(k => k.StartsWith("instance1/KitInfoBox_01/"))) throw new Exception("no inner clip of KitInfoBox_01 measured: " + string.Join(", ", s_Inner.Keys.Take(12)));
                ShowInnerBox.IsChecked = true; await Task.Delay(50);
                var s_InnerBoxes = m_Stage.Children.OfType<System.Windows.Shapes.Rectangle>().Count(x => x.Tag == null && x.StrokeThickness < 1);
                if (s_InnerBoxes < 10) throw new Exception($"'All clips' drew {s_InnerBoxes} inner outlines");
                ShowInnerBox.IsChecked = false;
                R($"properties: DataKey -1858312129 shown as UICustomizationComp.WeaponAccessory4, DataName suggests {string.Join("/", s_NameRow.Choices!)}, {s_SettingsCat.Children.Count} widget settings from KitView's code, {s_ChanCat.Children.Count} data channels; screen Modal=false, size 1280/720; {s_Inner.Count} clips measured, {s_InnerBoxes} inner outlines drawn");
                // a screen field edited in the grid lands on the document's screen entry (one undo step) and travels to the Lua
                var s_StepsScreen = m_Undo.Count;
                Row("scr.f.Modal").OnChange!(true);
                if (DocScreen(s_Screen)?.Fields.TryGetValue("Modal", out var s_ModalTok) != true || s_ModalTok?.Type != JTokenType.Boolean || !(bool)s_ModalTok || m_Undo.Count != s_StepsScreen + 1)
                    throw new Exception("the screen field edit did not reach the document (Modal)");
                if (!Row("scr.f.Modal").Modified) throw new Exception("edited screen field not marked modified");
                R("properties: Modal edited on the screen itself → document screen fields (1 undo step)");
                if (Row("f.WidgetProperties").Children.Count != 2 || Row("f.WidgetProperties[0]").Summary != "BtnLabel1 = ") throw new Exception("WidgetProperties rows: " + Row("f.WidgetProperties").Summary);
                var s_KnownCat = PropertiesGrid.Rows.FirstOrDefault(r => r.IsCategory && r.Name.StartsWith("Widget settings ("));
                if (s_KnownCat == null || !s_KnownCat.Children.Any(r => r.Name == "SendIndexInArray")) throw new Exception("the widget catalogue did not offer kitselector's other properties (SendIndexInArray…): " + string.Join(", ", s_Cats));
                if (Row("f.DataBinding").Choices?.Contains("UITextDataBinding") != true) throw new Exception("the binding type is not switchable among the binding types");
                R($"properties: {s_Cats.Count} categories for KitSelector_05 ({string.Join(", ", s_Cats)}); FocusIndex 4, binding UIDynamicDataBinding[Setup/-1858312129 @ uicustomizationcomp], {Row("f.WidgetProperties").Children.Count} properties, {s_KnownCat.Children.Count} more known for kitselector, {PropertiesGrid.Rows.First(r => r.Name == "Widget events (contract)").Children.Count} events");
                // editing in the grid: the first edit puts the shipped node into the document as an edit (one undo step), a binding leaf edit
                // turns the whole binding into an inline instance on the document; two undos take both back
                var s_Steps0 = m_Undo.Count;
                Row("f.FocusIndex").OnChange!(7L);
                var s_Edit = DocScreen(s_Screen)!.Nodes.FirstOrDefault(n => n.InstanceName == "KitSelector_05") ?? throw new Exception("the grid edit did not create the document entry");
                if (s_Edit.New || s_Edit.FocusIndex != 7 || m_Undo.Count != s_Steps0 + 1 || !Row("f.FocusIndex").Modified) throw new Exception("FocusIndex edit: new=" + s_Edit.New + " focus=" + s_Edit.FocusIndex + " steps=" + (m_Undo.Count - s_Steps0));
                Row("f.DataBinding.Bindings[0].UseDirectAccess").OnChange!(true);
                if (s_Edit.Fields.TryGetValue("DataBinding", out var s_InstTok) && s_InstTok is JObject s_Inst && (string?)s_Inst["$type"] == "UIDynamicDataBinding" && (bool?)s_Inst["Bindings"]?[0]?["UseDirectAccess"] == true && (long?)s_Inst["Bindings"]?[0]?["DataKey"] == -1858312129) { }
                else throw new Exception("binding edit did not produce the inline instance: " + (s_Edit.Fields.TryGetValue("DataBinding", out var t) ? t.ToString(Newtonsoft.Json.Formatting.None) : "(none)"));
                if (!Row("f.DataBinding").Modified) throw new Exception("binding row not marked modified");
                // the photo shows the node's own categories: fold the document/screen/placement ones the way a user would
                foreach (var c in new[] { "cat:Document", "cat:Screen", "cat:Placement", "cat:UINodeData" }) PropertiesGrid.SetExpanded(c, false);
                await Task.Delay(100);
                Photograph(Path.Combine(p_OutDir, "selftest_props.png"));
                foreach (var c in new[] { "cat:Document", "cat:Screen", "cat:Placement", "cat:UINodeData" }) PropertiesGrid.SetExpanded(c, true);
                Undo(); Undo();
                if (DocScreen(s_Screen)!.Nodes.Any(n => n.InstanceName == "KitSelector_05")) throw new Exception("two undos did not take the grid edits back");
                R("properties: FocusIndex 4→7 made the document edit (1 step); UseDirectAccess made the binding an inline UIDynamicDataBinding instance; 2 undos took both back");
                // an undo replaces the document object: re-take the entry and the node
                s_Entry = DocScreen(s_Screen) ?? throw new Exception("screen entry gone after the undos");
                s_Node = s_Entry.Nodes.Single(n => n.InstanceName == "CamoGrid_01");
                // the new widget's binding, edited through the grid, travels as an inline instance (the build checks the Lua)
                LayerList.SelectedIndex = s_Order.FindIndex(p => p.Name == "CamoGrid_01");
                await Task.Delay(50);
                if (Row("f.DataBinding").TypeName != "UIDynamicDataBinding" || Row("f.DataBinding.Bindings[0].DataCategory").Value?.ToString() != "ui/uicomponents/uicustomizationcomp")
                    throw new Exception("CamoGrid_01's shorthand binding is not shown as the instance it makes: " + Row("f.DataBinding").TypeName + " / " + Row("f.DataBinding.Bindings[0].DataCategory").Value);
                Row("f.DataBinding.Bindings[0].UseDirectAccess").OnChange!(false);
                if (s_Node.Binding != null || !s_Node.Fields.ContainsKey("DataBinding")) throw new Exception("CamoGrid_01's binding did not move to Fields[DataBinding]");
                R("properties: CamoGrid_01's binding now travels as an inline UIDynamicDataBinding instance");
                m_SelectedPlacement = "CamoGrid_01"; m_Stage.Selected = "CamoGrid_01";

                // the graph: every shipped node and the added one are boxes; a logic node added from the type list; wires dragged
                CentreTabs.SelectedIndex = 1;
                await Task.Delay(150);
                var s_Boxes = m_Graph.Boxes.ToList();
                if (s_Boxes.Count != m_Current.AllNodes.Count + 1) throw new Exception($"graph shows {s_Boxes.Count} boxes for {m_Current.AllNodes.Count} shipped nodes + 1 added");
                if (!s_Boxes.Any(b => b.Label == "CamoGrid_01" && b.IsNew)) throw new Exception("added widget not on the graph");
                NodeTypeBox.SelectedItem = "DataSetNode";
                OnAddNode(this, null!);
                var s_Set = s_Entry.Nodes.Single(n => n.Type == "DataSetNode");
                s_Set.InstanceName = "SetButtons";
                s_Set.Fields["Param"] = "ID_M_BACK,IDB_Rright,false,0;ID_M_YES,IDB_Rleft,false,0;";
                s_Set.Fields["DataSource"] = Newtonsoft.Json.Linq.JObject.Parse("{\"DataName\":\"\",\"DataKey\":1631675365,\"DataCategory\":\"ui/uicomponents/uicustomizationcomp\",\"UseDirectAccess\":false,\"UpdateOnInitialize\":true}");
                RefreshGraph();
                // a shipped wire disconnected with Alt+click travels to the Lua as a removed connection (the built mod is checked below)
                var s_Confirm0 = m_Graph.Boxes.First(b => b.Label == "Confirm");
                var s_ConfirmWire = m_Graph.Wires.FirstOrDefault(w => !w.IsDoc && w.Resolved && w.Guid != "" && w.To == s_Confirm0.Key) ?? throw new Exception("no shipped wire into Confirm to disconnect");
                OnWireRemoveRequested(s_ConfirmWire);
                if (s_Entry.RemovedConnections.Count != 1 || s_Entry.RemovedConnections[0].Guid != s_ConfirmWire.Guid) throw new Exception("the shipped wire into Confirm was not recorded as removed");
                var s_FromBox = m_Graph.Boxes.First(b => b.Label == "CamoGrid_01");
                var s_ToBox = m_Graph.Boxes.First(b => b.Label == "SetButtons");
                var s_Confirm = m_Graph.Boxes.First(b => b.Label == "Confirm");
                OnWireRequested(s_FromBox, "Outputs", "OnItemPressed", s_ToBox, "In", "In");
                OnWireRequested(s_ToBox, "Out", "Out", s_Confirm, "In", "In");
                var s_Wire1 = s_Node.Connections.FirstOrDefault(c => c.Event == "OnItemPressed" && c.ToNode == "SetButtons" && c.ToPort == "In");
                var s_Wire2 = s_Set.Connections.FirstOrDefault(c => c.FromPort == "Out" && c.ToNode == "Confirm" && c.ToPort == "In");
                if (s_Wire1 == null || s_Wire2 == null) throw new Exception("dragged wires not recorded on the document");
                m_Graph.Select("SetButtons");
                m_SelectedNode = "SetButtons"; m_SelectedPlacement = null; ShowProperties();
                await Task.Delay(100);
                GraphViewport.Fit();
                await Task.Delay(100);
                Photograph(Path.Combine(p_OutDir, "selftest_graph.png"));
                R($"graph: {m_Graph.Boxes.Count()} boxes; SetButtons (DataSetNode) added with {s_Set.Fields.Count} typed fields; shipped wire {s_ConfirmWire.Label} disconnected; wires CamoGrid_01.OnItemPressed -> SetButtons.In, SetButtons.Out -> Confirm.In");
                CentreTabs.SelectedIndex = 0;

                // the whole edit is a history: two undos take the two wires back (the document changes each time), two redos put
                // them back byte for byte; undoing everything leaves the fresh document, redoing everything returns to this state
                var s_Final = DocJson();
                var s_Steps = m_Undo.Count;
                if (s_Steps < 4) throw new Exception($"only {s_Steps} undo steps for add widget + add node + 2 wires");
                if (!Undo()) throw new Exception("undo refused");
                var s_Mid1 = DocJson();
                if (s_Mid1 == s_Final) throw new Exception("undo changed nothing");
                if (!Undo()) throw new Exception("second undo refused");
                var s_Doc2 = DocScreen(s_Screen) ?? throw new Exception("screen entry gone after two undos");
                if (s_Doc2.Nodes.Single(n => n.Type == "DataSetNode").Connections.Count != 0 || s_Doc2.Nodes.Single(n => n.Type == "WidgetNode").Connections.Any(c => c.Event == "OnItemPressed"))
                    throw new Exception("two undos did not take the two dragged wires back");
                if (m_Graph.Wires.Count(w => w.IsDoc) != 2) throw new Exception($"graph shows {m_Graph.Wires.Count(w => w.IsDoc)} document wires after the undos, expected the 2 typed connections");
                if (!Redo() || !Redo()) throw new Exception("redo refused");
                if (DocJson() != s_Final)
                {
                    File.WriteAllText(Path.Combine(p_OutDir, "undo_expected.json"), s_Final);
                    File.WriteAllText(Path.Combine(p_OutDir, "undo_actual.json"), DocJson());
                    throw new Exception("two redos did not restore the document byte for byte (undo_expected.json / undo_actual.json)");
                }
                while (Undo()) { }
                if (DocScreen(s_Screen) != null || CanUndo) throw new Exception("undoing everything did not leave the fresh document");
                while (Redo()) { }
                if (DocJson() != s_Final || CanRedo) throw new Exception("redoing everything did not return to the final document");
                if (m_Graph.Wires.Count(w => w.IsDoc) != 4) throw new Exception($"graph shows {m_Graph.Wires.Count(w => w.IsDoc)} document wires after the redos, expected 4");
                R($"undo/redo: {s_Steps} steps; 2 undos removed the dragged wires, 2 redos restored the document byte-identically; all the way back and forth too");

                // the Build dialog's derived names and the default description
                {
                    if (BuildDialog.SuperbundleFor("My Mod-2") != "mymod2/ui" || BuildDialog.BundleFor("mymod2/ui") != "mymod2/uibundle" || BuildDialog.SanitizeName("My Mod-2") != "MyMod2") throw new Exception("the derived mod/bundle names are wrong");
                    var s_Describe = ModEmitter.DescribeDocument(m_Doc);
                    if (!s_Describe.Contains("customizeaccessoriesscreen") || !s_Describe.Contains("Win32/" + m_Doc.Superbundle)) throw new Exception("the default description does not say what the mod does: " + s_Describe);
                    R("shipping: names My Mod-2 → MyMod2 / mymod2/ui / mymod2/uibundle; default description names the screens and the superbundle");
                }

                // the session comes back on the next start: a second editor built from the same (scratch) settings reopens the same tabs,
                // puts the same screen on the stage and shows its Layers — the way the user finds it after a restart
                {
                    PersistSession();
                    // the fresh editor has no document loaded (this test never saved one through the editor): the shipped clips are what it shows
                    // (a screen the document makes needs the document: the fresh editor has none loaded, so it is not expected back)
                    var s_Tabs = OpenScreens.Where(p => DocScreen(p)?.New != true).ToList(); var s_Front = m_Current!.Partition; var s_Layers = m_Current.Placements.Count(p => !p.Added);
                    var s_Again = new MainWindow();
                    for (var i = 0; i < 900 && !(s_Again.OpenScreens.Count >= s_Tabs.Count && s_Again.CurrentScreen == s_Front && !s_Again.m_Restoring); ++i) await Task.Delay(100);
                    var s_Restart = $"tabs {string.Join(",", s_Again.OpenScreens.Select(p => p.Split('/').Last()))} (expected {string.Join(",", s_Tabs.Select(p => p.Split('/').Last()))} in that order), stage {s_Again.CurrentScreen?.Split('/').Last()} (expected {s_Front.Split('/').Last()}), layers {s_Again.LayerList.Items.Count} (expected {s_Layers})";
                    var s_Ok = s_Again.OpenScreens.SequenceEqual(s_Tabs) && s_Again.CurrentScreen == s_Front && s_Again.LayerList.Items.Count == s_Layers && s_Again.EmptyHint.Visibility != Visibility.Visible;
                    var s_Placement = PlacementComesBack(s_Again);
                    s_Again.Close();
                    if (!s_Ok) throw new Exception("the session did not come back on a fresh start: " + s_Restart);
                    R("restart: " + s_Restart + "; window " + s_Placement);
                }
                // build and compare the produced screen movie with the reference that ran in game
                File.WriteAllText(m_DocPath, Newtonsoft.Json.JsonConvert.SerializeObject(m_Doc, Newtonsoft.Json.Formatting.Indented));
                if (s_Quick)
                {
                    await StepTextSizeAtTheEnd(R, p_OutDir);
                    R("QUICK PASS (editing steps only; the build needs a mount)");
                    File.WriteAllText(Path.Combine(p_OutDir, "selftest.txt"), s_Report.ToString());
                File.WriteAllText(Path.Combine(p_OutDir, "selftest_log.txt"), LogBox.Text);
                    return 0;
                }
                var s_BuildLog = new StringWriter();
                var s_Work = Path.Combine(p_OutDir, "build", m_Doc.Name);
                var s_ModDir = await Task.Run(() => m_Rime.BuildMod(m_Doc, p_OutDir, p_ModsRoot, s_BuildLog, s_Work));
                Log(s_BuildLog.ToString());
                // the mod folder is only what the game needs (mod.json, ext/, sb/): the build's intermediates live in the work folder
                var s_ModEntries = Directory.GetFileSystemEntries(s_ModDir).Select(Path.GetFileName).OrderBy(n => n).ToList();
                if (!s_ModEntries.SequenceEqual(new[] { "ext", "mod.json", "sb" })) throw new Exception("the mod folder is not mod.json + ext + sb: " + string.Join(", ", s_ModEntries));
                if (!File.Exists(Path.Combine(s_Work, "rime_build.txt"))) throw new Exception("the recipe was not written to the work folder " + s_Work);
                var s_ModJson = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(s_ModDir, "mod.json")));
                if ((string?)s_ModJson["Version"] != m_Doc.Version || (string?)s_ModJson["Name"] != m_Doc.Name || !((string?)s_ModJson["Description"] ?? "").Contains("Win32/" + m_Doc.Superbundle)) throw new Exception("mod.json does not carry the document's name/version/description: " + s_ModJson.ToString(Newtonsoft.Json.Formatting.None));
                if (File.ReadAllBytes(Path.Combine(s_ModDir, "mod.json")).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF })) throw new Exception("mod.json starts with a BOM (VU would not see the mod)");
                R($"mod folder: mod.json v{m_Doc.Version} without BOM + ext + sb, nothing else; build files in {s_Work}");
                // a generated partition written with the C# layouts instead of the game's crashed the client once (value types read as pointers):
                // the fidelity map must be next to the exe and loaded — the writer says so on the console
                var s_Fidelity = Type.GetType("RimeLib.Serialization.Frostbite2_0.Ebx.EbxFidelity, RimeLib.Serialization.Frostbite2_0")?.GetProperty("Loaded")?.GetValue(null) as bool?;
                if (s_Fidelity != true) throw new Exception($"the EBX fidelity map (fidelity_fb2.json next to the exe) was not loaded (Loaded = {s_Fidelity?.ToString() ?? "type not found"}): the new screen's partition would carry reflection-derived layouts");
                R("EBX fidelity map loaded: the new screen's partition is written with the game's field layouts");
                var s_Built = GfxMovie.Load(Path.Combine(s_Work, "customizeaccessoriesscreen.gfx")).ToPayload();
                var s_Ref = GfxMovie.Load(p_ReferenceGfx).ToPayload();
                var s_Same = s_Built.SequenceEqual(s_Ref);
                R($"built movie == reference that ran in game: {s_Same} ({s_Built.Length} vs {s_Ref.Length} bytes)");
                if (!s_Same) throw new Exception("movie differs from the reference");
                if (!File.Exists(Path.Combine(s_ModDir, "sb", "Win32", "uieditorselftest", "ui.sb"))) throw new Exception("no .sb built");
                var s_ClientLua = File.ReadAllText(Path.Combine(s_ModDir, "ext", "Client", "__init__.lua"));
                if (!s_ClientLua.Contains("type = \"DataSetNode\"") || !s_ClientLua.Contains("kind = \"struct\", type = \"UIDataSourceInfo\"") || !s_ClientLua.Contains("[\"ui/uicomponents/uicustomizationcomp\"]"))
                    throw new Exception("generated Lua lacks the generic node, its struct field or its asset");
                if (!s_ClientLua.Contains("kind = \"instance\", type = \"UIDynamicDataBinding\"") || !s_ClientLua.Contains("{ name = \"dataKey\", kind = \"number\", value = -1858312129 }"))
                    throw new Exception("generated Lua lacks the inline binding instance edited in the grid");
                if (!s_ClientLua.Contains("{ name = \"modal\", kind = \"bool\", value = true }")) throw new Exception("generated Lua lacks the screen field Modal = true");
                if (!s_ClientLua.Contains("removedConnections = {") || !s_ClientLua.Contains($"guid = \"{s_ConfirmWire.Guid.ToUpperInvariant()}\", from = \"{s_ConfirmWire.Label.Split(" -> ")[0]}\"") || !s_ClientLua.Contains("[UNWIRE]"))
                    throw new Exception("generated Lua lacks the disconnected shipped wire (removedConnections / [UNWIRE])");
                R($"generated ext/Client erases the disconnected shipped wire {s_ConfirmWire.Label} by its guid ([UNWIRE])");
                // static delivery: the same document builds again (no second mount) with every edited screen as a whole partition in the bundle —
                // the game's dump with the document applied, under the screen's own name — and a client that only probes
                m_Doc.GraphDelivery = "static";
                var s_StaticLog = new StringWriter();
                var s_StaticWork = Path.Combine(p_OutDir, "build_static", m_Doc.Name);
                var s_StaticDir = await Task.Run(() => m_Rime.BuildMod(m_Doc, p_OutDir, Path.Combine(p_ModsRoot, "static"), s_StaticLog, s_StaticWork));
                m_Doc.GraphDelivery = "lua";
                Log(s_StaticLog.ToString());
                var s_StaticJson = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(s_StaticWork, "customizeaccessoriesscreen.partition.json")));
                var s_StaticInst = (Newtonsoft.Json.Linq.JObject)s_StaticJson["Instances"]!;
                var s_Labels = s_StaticInst.Properties().Select(p => (string?)p.Value["InstanceName"] ?? (string?)p.Value["Name"]).ToList();
                if (!s_Labels.Contains("CamoGrid_01") || !s_Labels.Contains("SetButtons")) throw new Exception("the static partition lacks the document's nodes: " + string.Join(",", s_Labels.Where(l => l != null).Take(40)));
                if (s_StaticInst.Properties().Any(p => string.Equals(p.Name, s_ConfirmWire.Guid, StringComparison.OrdinalIgnoreCase))) throw new Exception("the static partition still holds the disconnected shipped wire");
                if (!string.Equals((string?)s_StaticJson["PartitionGuid"], m_Screens["ui/flow/screen/customizeaccessoriesscreen"].PartitionGuid, StringComparison.OrdinalIgnoreCase)) throw new Exception("the static partition does not keep the screen's own guid");
                var s_StaticRecipe = File.ReadAllText(Path.Combine(s_StaticWork, "rime_build.txt"));
                if (!s_StaticRecipe.Contains("add_json_partition ui/flow/screen/customizeaccessoriesscreen ")) throw new Exception("the static recipe does not add the edited partition under the screen's name:\n" + s_StaticRecipe);
                // what the screen imports rides in front of it (measured in game: a partition links its imports against what is loaded at that moment)
                var s_RecipeLines = s_StaticRecipe.Split('\n').Select(l => l.Trim()).ToList();
                var s_ScreenLine = s_RecipeLines.FindIndex(l => l.StartsWith("add_json_partition ui/flow/screen/customizeaccessoriesscreen "));
                foreach (var s_Dep in new[] { "ui/assets/kitselector", "ui/assets/kitinfobox", "ui/assets/textfield", "ui/assets/grid", "ui/uicomponents/uicustomizationcomp", "ui/uicomponents/uikitcomp", "ui/flow/graph/audiomapping/defaultuigraphaudiomapping" })
                {
                    var s_DepLine = s_RecipeLines.FindIndex(l => l.StartsWith("add_json_partition " + s_Dep + " "));
                    if (s_DepLine < 0 || s_DepLine > s_ScreenLine) throw new Exception($"the static recipe does not carry the import {s_Dep} in front of the screen (line {s_DepLine} vs {s_ScreenLine}):\n" + s_StaticRecipe);
                }
                if (!File.Exists(Path.Combine(s_StaticWork, "dep_ui_assets_kitselector.json"))) throw new Exception("the dependency copy of ui/assets/kitselector was not written");
                var s_StaticLua0 = File.ReadAllText(Path.Combine(s_StaticDir, "ext", "Client", "__init__.lua"));
                if (!s_StaticLua0.Contains("name = \"ui/assets/kitselector\", partition = Guid(") || !s_StaticLua0.Contains("[PROBE] imports loaded")) throw new Exception("the static client Lua does not probe the screen's imports");
                var s_StaticLua = File.ReadAllText(Path.Combine(s_StaticDir, "ext", "Client", "__init__.lua"));
                if (!s_StaticLua.Contains("static = true, probe = { \"CamoGrid_01\"") || !s_StaticLua.Contains("[PROBE]")) throw new Exception("the static client Lua does not probe instead of editing");
                if (!File.Exists(Path.Combine(s_StaticDir, "sb", "Win32", "uieditorselftest", "ui.sb"))) throw new Exception("no static .sb built");
                R($"static delivery: {s_StaticInst.Count} instances in the shipped customizeaccessoriesscreen partition (CamoGrid_01 + SetButtons in, {s_ConfirmWire.Label} out), add_json_partition under its own name behind {s_RecipeLines.Count(l => l.StartsWith("add_json_partition ui/assets/") || l.StartsWith("add_json_partition ui/uicomponents/") || l.StartsWith("add_json_partition ui/flow/graph/audiomapping/"))} dependency copies, client Lua = probe only (nodes, imports, widget assets)");
                // Record data… with the game mounted: the recorder mod's superbundle is built against the game's bundles (the movie replaces the screen's)
                {
                    var s_RecLog = new StringWriter();
                    var s_RecMod = await Task.Run(() => BuildRecorderMod("ui/assets/customizeaccessoriesscreen", s_RecLog, true));
                    Log(s_RecLog.ToString());
                    if (!s_RecMod.Built || !File.Exists(s_RecMod.SbPath)) throw new Exception("the recorder mod's superbundle was not built: " + s_RecMod.SbPath);
                    if (new FileInfo(s_RecMod.SbPath).Length < new FileInfo(s_RecMod.MoviePath).Length) throw new Exception("the recorder mod's superbundle is smaller than the movie it must carry");
                    if (!s_RecLog.ToString().Contains("replace_resource ui/assets/customizeaccessoriesscreen 1")) throw new Exception("the recorder recipe did not replace the screen's movie");
                    R($"Record data… (mounted): {s_RecMod.SbPath} built ({new FileInfo(s_RecMod.SbPath).Length} bytes) with the recorder movie replacing ui/assets/customizeaccessoriesscreen");
                }
                // the screen made from nothing: its movie (with the dropped button) and its partition are new entries of the bundle, and the
                // build ran the add_resource / add_json_partition lines without complaint (a partition JSON the converter rejects fails the build)
                var s_NewGfx = Path.Combine(s_Work, "uitestscreen.gfx");
                if (!File.Exists(s_NewGfx) || !GfxMovie.Load(s_NewGfx).StagePlacements().Any(p => p.Place.Name == "Button_01")) throw new Exception("the new screen's movie was not written with its button");
                var s_NewJson = Path.Combine(s_Work, "uitestscreen.partition.json");
                if (!File.Exists(s_NewJson) || !File.ReadAllText(s_NewJson).Contains("\"UI/Flow/Screen/UiTestScreen\"")) throw new Exception("the new screen's partition JSON was not written");
                var s_Recipe = File.ReadAllText(Path.Combine(s_Work, "rime_build.txt"));
                if (!s_Recipe.Contains("add_resource ui/assets/uitestscreen SwfMovie") || !s_Recipe.Contains("add_json_partition ui/flow/screen/uitestscreen")) throw new Exception("the recipe does not add the new screen's movie and partition:\n" + s_Recipe);
                if (!s_ClientLua.Contains("name = \"ui/flow/screen/uitestscreen\"")) throw new Exception("the Lua does not edit the new screen");
                R("new screen shipped: uitestscreen.gfx (Button_01 in it) + uitestscreen.partition.json built in the work folder, added to the bundle by the recipe, edited by the Lua");
                // the picture on the new screen went through the real build: its widget in the movie, its texture lines run against the game
                // without complaint (RunRecipe throws on a failing line), the chunk store superbundle beside the UI one, both declared
                if (!GfxMovie.Load(s_NewGfx).StagePlacements().Any(p => p.Place.Name.StartsWith("ImageManager_"))) throw new Exception("the new screen's movie lacks the picture widget");
                var s_ChunksSb = Path.Combine(s_ModDir, "sb", "Win32", m_Doc.ChunksSuperbundle.Replace('/', Path.DirectorySeparatorChar) + ".sb");
                if (!File.Exists(s_ChunksSb) || new FileInfo(s_ChunksSb).Length < 6000) throw new Exception($"the picture's chunk store was not built: {s_ChunksSb} ({(File.Exists(s_ChunksSb) ? new FileInfo(s_ChunksSb).Length : 0)} bytes)");
                var s_BuildText = s_BuildLog.ToString();
                if (!s_BuildText.Contains("picture test_image:") || !s_BuildText.Contains("> add_dds_texture \"UI/Art/UiEditorSelfTest/test_image\"") || !s_BuildText.Contains("> add_chunk ")) throw new Exception("the build log does not show the picture's conversion and texture lines");
                if (!((s_ModJson["Superbundles"] as JArray)?.Any(t => (string?)t == "Win32/" + m_Doc.ChunksSuperbundle) ?? false)) throw new Exception("mod.json does not declare the chunk store: " + s_ModJson["Superbundles"]);
                R($"picture shipped: UI/Art/UiEditorSelfTest/test_image (DXT5 100x60) — header resource + TextureAsset in the UI bundle, pixels in {Path.GetFileName(s_ChunksSb)} ({new FileInfo(s_ChunksSb).Length} bytes), both superbundles in mod.json, the widget in uitestscreen.gfx");
                R("generated ext/Client carries the DataSetNode with typed fields, the component asset to load and the binding as an inline instance");
                await StepTextSizeAtTheEnd(R, p_OutDir);
                R("PASS");
                File.WriteAllText(Path.Combine(p_OutDir, "selftest.txt"), s_Report.ToString());
                File.WriteAllText(Path.Combine(p_OutDir, "selftest_log.txt"), LogBox.Text);
                return 0;
            }
            catch (Exception s_Ex)
            {
                R("FAIL: " + s_Ex.Message + "\n" + s_Ex.StackTrace);
                R("--- window log ---");
                R(LogBox.Text);
                File.WriteAllText(Path.Combine(p_OutDir, "selftest.txt"), s_Report.ToString());
                File.WriteAllText(Path.Combine(p_OutDir, "selftest_log.txt"), LogBox.Text);
                return 1;
            }
        }

        /// <summary>
        /// The resolution the preview stands in for: the game keeps a picture at its PIXEL size whatever the resolution (keku's 720p and
        /// 1440p captures: the weapon's picture stays ~230 px while the row doubles), so the server shrinks the pictures by the player's
        /// stage scale - measured on the served PNG at 720p and at 1440p (the stage scale reached is what the monitor allows) and on the
        /// movie's own picture clip after the reload. Needs the preview up on the accessories screen.
        /// </summary>
        async Task StepResolution(Action<string> R, string p_OutDir)
        {
            // the game's rule (keku's 720p / 1440p captures of the same screen): rows, texts, icons — the whole 1280x720 UI — keep their pixel size
            // and sit centred; only the 3D behind grows. The player draws the stage at one device pixel per stage unit, centred: at 1440p the
            // page's player element is 1280x720 device px with room around it, the weapon's picture is served at its own size and the row's
            // picture clip spans it 1:1; 'Fit window' scales the stage instead (and the pictures with it)
            // the player opens on the editor's monitor (keku 21:00: it opened on the other one after he had moved the editor): the user's
            // placement answers the editor's own monitor, and the window this seam opened sits on the editor's
            var s_EditorHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var s_EditorOn = Monitors.Of(s_EditorHandle);
            var s_UserPlacement = Monitors.ForPreview(s_EditorHandle, false);
            if (s_EditorOn != null && s_UserPlacement != s_EditorOn) throw new Exception($"the user's preview would open on the monitor at ({s_UserPlacement?.Left},{s_UserPlacement?.Top}); the editor sits on the one at ({s_EditorOn.Left},{s_EditorOn.Top})");
            var s_PlayerOn = Monitors.Of(new System.Windows.Interop.WindowInteropHelper(m_RuffleHost!).Handle);
            if (s_EditorOn != null && s_PlayerOn != s_EditorOn) throw new Exception($"the player window sits on the monitor at ({s_PlayerOn?.Left},{s_PlayerOn?.Top}), the editor on the one at ({s_EditorOn.Left},{s_EditorOn.Top})");
            using var s_Http2 = new System.Net.Http.HttpClient();
            static (int W, int H) PngSize(byte[] p_Png) => (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(p_Png.AsSpan(16, 4)), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(p_Png.AsSpan(20, 4)));
            async Task<(string Mode, int W, int H, int Left, int Top, double Dpr)> Layout()
            {
                var s_Json = await m_RuffleHost!.EvalPage("(function(){var p=document.querySelector('ruffle-player'),r=p.getBoundingClientRect(),d=window.devicePixelRatio||1;return JSON.stringify({m:document.body.classList.contains('fixed')?'fixed':'fit',w:Math.round(r.width*d),h:Math.round(r.height*d),l:Math.round(r.left*d),t:Math.round(r.top*d),d:d});})()");
                var o = JObject.Parse(Newtonsoft.Json.JsonConvert.DeserializeObject<string>(s_Json) ?? "{}");
                return ((string?)o["m"] ?? "", (int?)o["w"] ?? 0, (int?)o["h"] ?? 0, (int?)o["l"] ?? 0, (int?)o["t"] ?? 0, (double?)o["d"] ?? 1);
            }
            // the step measures the accessories screen's weapon row (the first screen of the page): start the page over so that screen is up
            // whatever the walk left showing (the full walk arrives here on the KITS screen, whose rows carry no picture), and read the
            // row's picture request from this point of the log — an earlier screen's request must not stand in for it
            var s_LogStart = LogBox.Text.Length;
            m_RuffleHost!.SelectResolution(720);
            m_RuffleHost.Navigate(m_RuffleHost.Url);
            for (var i = 0; i < 400 && !(m_UiHost!.Stack.Count == 1 && m_UiHost.WidgetsInitialised > 0 && System.Text.RegularExpressions.Regex.IsMatch(LogBox.Text[s_LogStart..], @"texture (UI/Art/Persistence/Weapons/[^ ]+) for mainImage")); ++i) await Task.Delay(50);
            var s_Match = System.Text.RegularExpressions.Regex.Match(LogBox.Text[s_LogStart..], @"texture (UI/Art/Persistence/Weapons/[^ ]+) for mainImage");
            if (!s_Match.Success) throw new Exception("the accessories screen's weapon row asked for no picture within 20 s of the page starting over");
            var s_Picture = s_Match.Groups[1].Value;
            var s_ImageUrl = m_RuffleServer!.Url + "ui/assets/img/" + s_Picture.ToLowerInvariant();
            await Task.Delay(1000);
            var s_At720 = await Layout();
            var s_Png720 = PngSize(await s_Http2.GetByteArrayAsync(s_ImageUrl));
            if (s_At720.Mode != "fixed" || System.Math.Abs(s_At720.W - 1280) > 2 || System.Math.Abs(s_At720.H - 720) > 2) throw new Exception($"at 720p the player is {s_At720.Mode} {s_At720.W}x{s_At720.H} device px, expected the fixed 1280x720 stage");
            var s_Holder720 = await ProbeClip("_level0.sc1.instance1.KitSelector_01.mcContainer.Slot_0.mainImage.m_holder._width");
            // the clip must be there to be measured: 'undefined' on both sides would compare equal and prove nothing
            if (!double.TryParse(s_Holder720.Trim('"'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s_HolderUnits) || s_HolderUnits < 32) throw new Exception($"at 720p the weapon row's picture clip reads {s_Holder720}: expected its width in stage units (is the accessories screen up?)");
            m_RuffleHost.SelectResolution(1440);
            await Task.Delay(600);
            var s_At1440 = await Layout();
            var s_Dpi = System.Windows.Media.VisualTreeHelper.GetDpi(m_RuffleHost).DpiScaleX;
            var s_Player = (W: (int)System.Math.Round(m_RuffleHost.PlayerWidthDip * s_Dpi), H: (int)System.Math.Round(m_RuffleHost.PlayerHeightDip * s_Dpi));
            if (s_At1440.Mode != "fixed" || System.Math.Abs(s_At1440.W - 1280) > 2 || System.Math.Abs(s_At1440.H - 720) > 2) throw new Exception($"at 1440p the player is {s_At1440.Mode} {s_At1440.W}x{s_At1440.H} device px, expected the fixed 1280x720 stage inside a {s_Player.W}x{s_Player.H} player");
            if (s_Player.W < 1400 || s_At1440.Left < 40 || System.Math.Abs(s_At1440.Left - (s_Player.W - 1280) / 2) > 4) throw new Exception($"at 1440p the stage sits at x={s_At1440.Left} in a {s_Player.W} px wide player: expected centred with room around (the window did not grow past 720p? working area {Monitors.WorkingAreaOf(m_RuffleHost)})");
            var s_Png1440 = PngSize(await s_Http2.GetByteArrayAsync(s_ImageUrl));
            if (s_Png1440.W != s_Png720.W || s_Png720.W < 64) throw new Exception($"the weapon picture is served {s_Png720.W} px at 720p and {s_Png1440.W} at 1440p: in the fixed stage it keeps its pixel size");
            var s_ClipWidth = await ProbeClip("_level0.sc1.instance1.KitSelector_01.mcContainer.Slot_0.mainImage.m_holder._width");
            if (s_ClipWidth.Trim('"') != s_Holder720.Trim('"')) throw new Exception($"the row's picture clip spans {s_ClipWidth} stage units at 1440p vs {s_Holder720} at 720p: it must not change");
            await m_RuffleHost.CaptureAsync(Path.Combine(p_OutDir, "selftest_ruffle_1440.png"));
            // fit: the stage scaled to the window, the pictures shrunk by that scale so they span their own pixels
            var s_LogFit = LogBox.Text.Length;
            m_RuffleHost.SelectResolution(0);
            for (var i = 0; i < 400 && !(m_UiHost!.Stack.Count == 1 && m_UiHost.WidgetsInitialised > 0 && LogBox.Text[s_LogFit..].Contains("for mainImage")); ++i) await Task.Delay(50);
            await Task.Delay(800);
            var s_Fit = await Layout();
            var s_Scale = m_RuffleHost.StageScale;
            var s_PngFit = PngSize(await s_Http2.GetByteArrayAsync(s_ImageUrl));
            if (s_Fit.Mode != "fit" || s_Scale < 1.2 || System.Math.Abs(s_PngFit.W - System.Math.Round(s_Png720.W / System.Math.Round(s_Scale, 2))) > 2) throw new Exception($"fit: mode {s_Fit.Mode}, stage scale {s_Scale:0.00}, picture served {s_PngFit.W} px (expected ~{s_Png720.W / s_Scale:0})");
            // back to 720p from the scaled mode: the page reloads (the pictures the movie held were the shrunk ones) and the clip is 1:1 again — the icons stayed small here once
            var s_LogBack = LogBox.Text.Length;
            m_RuffleHost.SelectResolution(720);
            for (var i = 0; i < 400 && !(m_UiHost!.Stack.Count == 1 && m_UiHost.WidgetsInitialised > 0 && LogBox.Text[s_LogBack..].Contains("for mainImage")); ++i) await Task.Delay(50);
            await Task.Delay(1000);
            var s_ClipBack = await ProbeClip("_level0.sc1.instance1.KitSelector_01.mcContainer.Slot_0.mainImage.m_holder._width");
            if (s_ClipBack.Trim('"') != s_Holder720.Trim('"')) throw new Exception($"back at 720p after Fit window the row's picture clip spans {s_ClipBack} stage units, expected {s_Holder720} (the page must reload so the pictures come back 1:1)");
            R($"resolution: 720p → the fixed 1280x720 stage fills the player ({s_Picture.Split('/').Last()} served {s_Png720.W}x{s_Png720.H}, clip {s_Holder720} units); 1440p → the player is {s_Player.W}x{s_Player.H} px and the stage stays 1280x720 at ({s_At1440.Left},{s_At1440.Top}), picture and clip unchanged — the whole UI keeps its 720p pixel size, centred, as the game; Fit window → scaled {s_Scale:0.00}, picture served {s_PngFit.W} px; back to 720p → reloaded, clip {s_ClipBack} again; the player window on the editor's monitor at ({s_EditorOn?.Left},{s_EditorOn?.Top})");
        }

        /// <summary>
        /// Preview in Ruffle for a seam, from a clean slate: a recording of this screen left by an earlier run and the converted movies of
        /// earlier previews are removed first (a fixture that fed the widgets, a widget converted by an older converter: both stood in for
        /// the product before), then the player, the bridge and the host's init of every widget are waited for.
        /// </summary>
        async Task<string> StartPreviewForSeam(string p_OutDir)
        {
            var s_StaleFixture = DataRecorder.FixturePath(EditorSettings.DefaultPreviewRoot(), m_Current!.MovieName);
            if (File.Exists(s_StaleFixture)) File.Delete(s_StaleFixture);
            var s_LiveDir = Path.Combine(EditorSettings.DefaultPreviewRoot(), "live");
            if (Directory.Exists(s_LiveDir)) Directory.Delete(s_LiveDir, true);
            var s_Url = RunRufflePreview();
            if (s_Url == null || m_RuffleHost == null) throw new Exception("Preview in Ruffle did not open its window");
            if (m_UiHost?.Recording != null) throw new Exception("the first preview loaded a recording (" + s_StaleFixture + "): it must run on generated data");
            var s_Ready = new TaskCompletionSource<bool>();
            m_RuffleHost.BridgeReady += () => s_Ready.TrySetResult(true);
            var s_Announced = await Task.WhenAny(s_Ready.Task, Task.Delay(30000)) == s_Ready.Task;
            await m_RuffleHost.CaptureAsync(Path.Combine(p_OutDir, "selftest_ruffle_host.png"));
            if (!s_Announced) throw new Exception("the bridge did not announce itself within 30 s (Ruffle web did not run the shell, or ExternalInterface does not reach the page)");
            for (var i = 0; i < 200 && (m_UiHost == null || m_UiHost.WidgetsInitialised == 0); ++i) await Task.Delay(50);
            if (m_UiHost == null || m_UiHost.WidgetsInitialised == 0) throw new Exception("the host did not initialise the widgets after the screen loaded");
            return s_Url;
        }

        /// <summary>
        /// The window comes back where it was (keku: the editor must open on the monitor he last had it on): this window's placement
        /// was saved with the session; a fresh editor built from the same settings, given a handle without being shown, sits at the
        /// same place, size and state. Returns the placement for the report. The fresh window is the caller's to close.
        /// </summary>
        string PlacementComesBack(MainWindow p_Fresh)
        {
            var s_Saved = m_Settings.Window ?? throw new Exception("the session did not save the window's placement");
            var s_Now = Monitors.PlacementOf(this) ?? throw new Exception("this window's placement cannot be read");
            if (s_Saved.ToString() != s_Now.ToString()) throw new Exception($"the saved placement {s_Saved} is not this window's {s_Now}");
            new System.Windows.Interop.WindowInteropHelper(p_Fresh).EnsureHandle();
            var s_Back = Monitors.PlacementOf(p_Fresh) ?? throw new Exception("the fresh window's placement cannot be read");
            if (s_Back.ToString() != s_Saved.ToString()) throw new Exception($"the fresh editor sits at {s_Back}, the saved placement is {s_Saved}");
            var s_On = Monitors.Of(new System.Windows.Interop.WindowInteropHelper(p_Fresh).Handle);
            var s_Own = Monitors.Of(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            if (s_Own != null && s_On != s_Own) throw new Exception($"the fresh editor is on the monitor at ({s_On?.Left},{s_On?.Top}), this one on ({s_Own.Left},{s_Own.Top})");
            return $"{s_Back} on the monitor at ({s_On?.Left},{s_On?.Top}) again";
        }

        /// <summary>
        /// The drawing order (keku, 2026-09-16: "cómo hago para que algo esté por encima o por debajo de X"): a clip dropped on the
        /// accessories screen comes in front (the highest depth); ▼ sends it one step back (it swaps depths with PageHeader_01), a depth
        /// typed in Properties puts it at KitInfoBox_01's depth (they swap); the Layers list, the movie's placements, the document's ops
        /// and the art's drawing order agree at every step; undo brings the order back.
        /// </summary>
        Task StepDepth(Action<string> R)
        {
            CentreTabs.SelectedIndex = 0;
            var s_Before = m_Current!.Placements.Select(p => p.Name).ToHashSet();
            if (!HandleStageDrop(new DataObject(PartitionFormat, "ui/assets/button"), new Point(400, 300))) throw new Exception("the clip could not be dropped");
            var s_Name = m_Current.Placements.First(p => p.Added && !s_Before.Contains(p.Name)).Name;
            Model.PlacementInfo P(string n) => m_Current!.Placements.First(p => p.Name == n);
            string TopOfLayers() => ((System.Collections.IList)LayerList.ItemsSource!).Count > 0 ? m_Current!.Placements.OrderByDescending(p => p.Depth).First().Name : "";
            var s_Sprite = P(s_Name).SpriteId;
            var s_Header = P("PageHeader_01"); var s_Info = P("KitInfoBox_01");
            if (s_Header.SpriteId != s_Sprite || s_Info.SpriteId != s_Sprite) throw new Exception("the dropped clip is not in the sprite of PageHeader_01 / KitInfoBox_01");
            if (P(s_Name).Depth <= s_Header.Depth || TopOfLayers() != s_Name) throw new Exception($"a dropped clip must come in front: depth {P(s_Name).Depth} vs PageHeader_01 {s_Header.Depth}, Layers top {TopOfLayers()}");
            var s_Top = P(s_Name).Depth; var s_HeaderDepth = s_Header.Depth; var s_InfoDepth = s_Info.Depth;
            // the art draws in depth order: the last child of the sprite's group is the clip in front
            string Frontmost()
            {
                var g = Renderer().PlacementGroups;
                var s_Inner = g["instance1"].Children.OfType<DrawingGroup>().FirstOrDefault() ?? throw new Exception("the root sprite's group has no children");
                var s_Last = s_Inner.Children.OfType<DrawingGroup>().LastOrDefault(c => g.ContainsValue(c)) ?? throw new Exception("no placement group inside the root sprite");
                return g.First(kv => kv.Value == s_Last).Key;
            }
            if (!Frontmost().EndsWith(s_Name)) throw new Exception("the art does not draw the dropped clip last: " + Frontmost());
            // ▼: one step back — it swaps with PageHeader_01
            m_SelectedPlacement = s_Name; m_SelectedNode = s_Name; m_Stage.Selected = s_Name;
            MoveLayer(-1);
            if (P(s_Name).Depth != s_HeaderDepth || P("PageHeader_01").Depth != s_Top) throw new Exception($"after ▼ the clip is at {P(s_Name).Depth} and PageHeader_01 at {P("PageHeader_01").Depth}, expected {s_HeaderDepth} / {s_Top}");
            if (TopOfLayers() != "PageHeader_01") throw new Exception("after ▼ the Layers list does not show PageHeader_01 topmost: " + TopOfLayers());
            var s_Ops = DocScreen(m_Current.Partition)!.Stage;
            if (!s_Ops.Contains($"depth:{s_Name}:{s_HeaderDepth}")) throw new Exception("no depth op for ▼: " + string.Join(" | ", s_Ops));
            // a depth typed in Properties: KitInfoBox_01's — the two swap
            ShowProperties();
            System.Collections.Generic.IEnumerable<PropRow> Flat(System.Collections.Generic.IEnumerable<PropRow> p_Rows) => p_Rows.SelectMany(r => new[] { r }.Concat(Flat(r.Children)));
            var s_DepthRow = Flat(PropertiesGrid.Rows).FirstOrDefault(r => r.Path == "pl.depth") ?? throw new Exception("no Depth row");
            if ((long)s_DepthRow.Value! != s_HeaderDepth || !s_DepthRow.Modified) throw new Exception($"the Depth row reads {s_DepthRow.Value} (modified {s_DepthRow.Modified}), expected {s_HeaderDepth} modified");
            s_DepthRow.OnChange!((long)s_InfoDepth);
            if (P(s_Name).Depth != s_InfoDepth || P("KitInfoBox_01").Depth != s_HeaderDepth) throw new Exception($"after typing {s_InfoDepth} the clip is at {P(s_Name).Depth} and KitInfoBox_01 at {P("KitInfoBox_01").Depth}");
            if (Frontmost().EndsWith(s_Name)) throw new Exception("the art still draws the clip last after it was sent back");
            var s_Order = string.Join(",", m_Current.Placements.Where(p => p.SpriteId == s_Sprite).OrderByDescending(p => p.Depth).Select(p => p.Name));
            // the movie the build ships carries the same depths (the same op, the same SetDepth)
            var s_Check = GfxMovie.Load(m_Current.VanillaMovie!);
            foreach (var s_Op in s_Ops) ModEmitter.ApplyOp(s_Check, s_Op);
            var s_Shipped = s_Check.StagePlacements().Where(p => p.Place.Name is not null).ToDictionary(p => p.Place.Name!, p => p.Place.Depth);
            if (s_Shipped[s_Name] != s_InfoDepth || s_Shipped["KitInfoBox_01"] != s_HeaderDepth || s_Shipped["PageHeader_01"] != s_Top) throw new Exception($"the built movie's depths differ: {s_Name} {s_Shipped[s_Name]}, KitInfoBox_01 {s_Shipped["KitInfoBox_01"]}, PageHeader_01 {s_Shipped["PageHeader_01"]}");
            // undo, twice: the order as dropped
            Undo(); Undo();
            if (P(s_Name).Depth != s_Top || P("PageHeader_01").Depth != s_HeaderDepth || P("KitInfoBox_01").Depth != s_InfoDepth || TopOfLayers() != s_Name) throw new Exception("undo did not bring the drawing order back");
            R($"depth: {s_Name} dropped at depth {s_Top} (in front, Layers topmost, drawn last); ▼ → {s_HeaderDepth}, PageHeader_01 up to {s_Top} (Layers topmost PageHeader_01, depth op); Depth row typed {s_InfoDepth} → swapped with KitInfoBox_01 (order {s_Order}); the built movie carries the same depths; two undos → as dropped");
            return Task.CompletedTask;
        }

        /// <summary>
        /// The camo screen (Data/CamoMenu.seam.json loaded as the document: the accessories screen, a new camo screen and the
        /// customization flow graph edited; the engineer's SCAR-H whose camo row lists three camos, one of them renamed by the
        /// document's table): the camo row's arrows are gone, its name is the game's; Activate on the row leaves the accessories
        /// screen through its new CamoButton output and the flow graph replaces it with the camo screen (popped first); the camo
        /// screen shows the header path + ACCESSORIES, the camo title, the kit, BACK, the family buttons and the grid of the family
        /// worn with the keyboard cursor on its button; the keys walk buttons and cells, the table's camo shows its name and text in
        /// the info box; Activate on it fires the grid's Released with the camo's IDENTIFIER → SetAccessory4 → AccessoryChanged →
        /// the flow graph's store action (the profile takes it); Back pops the camo screen and re-enters the accessories screen,
        /// whose camo row now reads the table's name and shows no arrows; a click on the row opens the camo screen again, BACK
        /// (the button → Confirm) returns. Photos of the camo screen, of the pick and of the row after.
        /// </summary>
        async Task StepCamoMenu(Action<string> R, string p_OutDir)
        {
            if (m_Doc.Name != "CustomizeCamoMenu") throw new Exception("the camo menu document is not loaded: " + m_Doc.Name);
            var s_AccEntry = DocScreen("ui/flow/screen/customizeaccessoriesscreen") ?? throw new Exception("the camo menu document has no entry for the accessories screen");
            if (!s_AccEntry.Stage.Any(o => o.StartsWith("script:camorow:"))) throw new Exception("the accessories screen entry carries no camorow script op");
            var s_CamoEntry = DocScreen("ui/flow/screen/customizecamoscreen") ?? throw new Exception("the camo menu document has no entry for the camo screen");
            if (!s_CamoEntry.New || !s_CamoEntry.Stage.Any(o => o.StartsWith("script:camoscreen:"))) throw new Exception("the camo screen entry is not a new screen with the camoscreen script op");
            var s_FlowEntry = DocScreen("ui/flow/graph/spawn/customizationgraph") ?? throw new Exception("the camo menu document does not edit the customization flow graph");
            if (!s_FlowEntry.Nodes.Any(n => n.Type == "StateNode" && n.New && n.Fields.TryGetValue("Screen", out var s_ScreenField) && (string?)s_ScreenField == "ui/flow/screen/customizecamoscreen")) throw new Exception("the flow graph entry has no StateNode for the camo screen");
            await OpenScreen("ui/flow/screen/customizeaccessoriesscreen");
            if (m_Current == null || !m_Current.Movie!.FrameScripts().Contains("camorow")) throw new Exception("the accessories movie shown does not carry the frame script camorow (stage op script: not applied)");
            // the engineer's SCAR-H: its camo row lists three camos (the desert stripe among them, renamed by the document's table)
            DataGenerator.Current = new UiDataGenerator.Profile { Kit = "gameplay/kits/ruengineer" };
            var s_ScarH = DataGenerator.PrimaryWeapons().FirstOrDefault(w => w.Unlock.Contains("scar-h", StringComparison.OrdinalIgnoreCase));
            if (s_ScarH.Unlock == null) throw new Exception("the engineer kit lists no SCAR-H");
            SetPreviewProfile("gameplay/kits/ruengineer", s_ScarH.Unlock);
            // an unlock the client cannot name rides in the camo row (keku 2026-09-29: an empty box in the M416's MISC) — the generator adds it
            const int c_SeamBlankId = 987654321;
            Environment.SetEnvironmentVariable("RUE_SEAM_BLANK_CAMO", c_SeamBlankId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var s_LogCamo = LogBox.Text.Length;
            await StartPreviewForSeam(p_OutDir);
            var s_Host = m_UiHost ?? throw new Exception("no host for the camo screen preview");
            for (var i = 0; i < 600 && !LogBox.Text[s_LogCamo..].Contains("focus on customizeaccessoriesscreen: KitSelector_01 "); ++i) await Task.Delay(50);
            if (!LogBox.Text[s_LogCamo..].Contains("generated UICustomizationComp.WeaponAccessory4 ←")) throw new Exception("the camo row's data was not generated for the preview");
            await Task.Delay(1000);
            // the screen on top of the player's stack, by its clip ("sc1:customizeaccessoriesscreen")
            string Top() => s_Host.Stack.Count == 0 ? "_level0.none.instance1." : "_level0." + s_Host.Stack[^1].Split(':')[0] + ".instance1.";
            string TopScreen() => s_Host.Stack.Count == 0 ? "" : s_Host.Stack[^1].Split(':')[1];   // "" between a pop and the push that follows it
            async Task<string> Probe(string p) => (await ProbeClip(Top() + p)).Trim('"');
            async Task Key(int p_Concept) { m_RuffleHost!.OnInputConcept!(p_Concept, true); m_RuffleHost.OnInputConcept!(p_Concept, false); await Task.Delay(500); }
            async Task CursorIs(string p_Zone, string? p_Button, string p_When)
            {
                var s_Zone = await Probe("CamoGrid._camoZone"); var s_Cur = await Probe("CamoGrid._camoCursor");
                if (s_Zone != p_Zone || (p_Button != null && s_Cur != p_Button)) throw new Exception($"{p_When}: the keyboard cursor is on zone \"{s_Zone}\" button {s_Cur}; expected \"{p_Zone}\" {p_Button ?? "(any)"}");
            }
            async Task WaitTop(string p_Screen, string p_When)
            {
                for (var i = 0; i < 200 && (TopScreen() != p_Screen || !LogBox.Text.Contains($"{s_Host.Stack[^1].Split(':')[0]} {p_Screen} loaded: initializeScreen")); ++i) await Task.Delay(50);   // TopScreen() is "" while the stack is empty, so the index is safe here
                if (TopScreen() != p_Screen) throw new Exception($"{p_When}: the screen on top is {TopScreen()}, expected {p_Screen} (stack: {string.Join(" < ", s_Host.Stack)})");
                await Task.Delay(1200);
            }
            if (TopScreen() != "customizeaccessoriesscreen") throw new Exception("the preview did not start on the accessories screen: " + string.Join(" < ", s_Host.Stack));
            // the camo row: no arrows (the weapon row keeps its), the game's name for the camo worn (no camo: not in the table)
            var s_CamoLeft = await Probe("KitSelector_05.mcContainer.Slot_0.m_mcButtonLeft._visible");
            var s_WeaponLeft = await Probe("KitSelector_01.mcContainer.Slot_0.m_mcButtonLeft._visible");
            if (s_CamoLeft != "false" || s_WeaponLeft != "true") throw new Exception($"arrows: camo row {s_CamoLeft} (expected hidden), weapon row {s_WeaponLeft} (expected shown)");
            var s_RowBefore = await Probe("KitSelector_05.mcContainer.Slot_0.header.text");
            var s_WornId = await Probe("KitSelector_05.mcContainer.Slot_0.m_id"); var s_WeaponId = await Probe("KitSelector_01.mcContainer.Slot_0.m_id");   // what the door reports to Lua
            var s_TableCount = await Probe("KitSelector_05._camoTableCount");
            if (s_TableCount != "1") throw new Exception("the row's script did not read the document's table (1 entry expected): " + s_TableCount);
            // the camo row focused, Activate: the row's own Button1Released → the CamoButton output → the flow graph pops the accessories
            // screen and pushes the camo screen (the document's StateNode)
            s_LogCamo = LogBox.Text.Length;
            m_RuffleHost!.Dispatch(Top() + "KitSelector_05", "activate", "0");
            for (var i = 0; i < 100 && !LogBox.Text[s_LogCamo..].Contains("focus on customizeaccessoriesscreen: KitSelector_05 onFocused(false)"); ++i) await Task.Delay(50);
            await Task.Delay(600);
            s_LogCamo = LogBox.Text.Length;
            await Key(6);
            await WaitTop("customizecamoscreen", "after Activate on the camo row");
            var s_Open = LogBox.Text[s_LogCamo..];
            if (!s_Open.Contains("KitSelector_05 fired OnItemReleased") || !s_Open.Contains("CamoButton: screen output") || !s_Open.Contains("pop sc1 customizeaccessoriesscreen") || !s_Open.Contains("push ui/flow/screen/customizecamoscreen"))
                throw new Exception("Activate on the camo row did not open the camo screen through the graph (Button1Released → CamoButton → the flow graph's CamoButton wire, pop 1, push): " + s_Open.Trim()[..System.Math.Min(700, s_Open.Trim().Length)]);
            if (s_Host.Stack.Count != 1) throw new Exception("the camo screen did not replace the accessories screen: " + string.Join(" < ", s_Host.Stack));
            // the screen up: its StateNode's Initialized output (the document's port) fires the post-process action every customization
            // screen fires on entering (the glitch between screens)
            for (var i = 0; i < 100 && !LogBox.Text[s_LogCamo..].Contains("CamoPostProcess: engine action"); ++i) await Task.Delay(50);
            var s_Up = LogBox.Text[s_LogCamo..];
            if (!s_Up.Contains("CustomizeCamoScreen.Initialized [Screen] (flow graph, document)") || !s_Up.Contains("CamoPostProcess: engine action")) throw new Exception("the camo screen coming up did not fire its Initialized output into the post-process action (the glitch): " + s_Up.Trim()[..System.Math.Min(600, s_Up.Trim().Length)]);
            // the blank unlock (on the family worn, MISC: the SCAR-H wears no camo): it REACHED the screen (the control: the last of its
            // items), no cell is it, and its id went to Lua once (GBL) -- measured before anything that depends on the language
            var s_ItemsAt = Top()[..^"instance1.".Length];
            var s_ItemCount = int.TryParse((await ProbeClip(s_ItemsAt + "camoItems.length")).Trim('"'), out var s_Ic) ? s_Ic : -1;
            var s_LastItem = s_ItemCount > 0 ? (await ProbeClip(s_ItemsAt + $"camoItems.{s_ItemCount - 1}.Index")).Trim('"') : "";
            var s_BlankFamilyCells = await Probe("CamoGrid.gridData.length");
            var s_BlankCells = new System.Collections.Generic.List<int>();
            for (var k = 0; k < (int.TryParse(s_BlankFamilyCells, out var s_Bc) ? s_Bc : 0); ++k)
                if ((await Probe($"CamoGrid.gridData.{k}.data.Index")) == c_SeamBlankId.ToString(System.Globalization.CultureInfo.InvariantCulture)) s_BlankCells.Add(k);
            var s_BlankTrail = (await ProbeClip("_global._camoSignalTrail")).Trim('"');
            Environment.SetEnvironmentVariable("RUE_SEAM_BLANK_CAMO", null);
            if (s_LastItem != c_SeamBlankId.ToString(System.Globalization.CultureInfo.InvariantCulture)) throw new Exception($"the blank unlock never reached the camo screen (its last item is \"{s_LastItem}\" of {s_ItemCount}): the check below would prove nothing");
            if (s_BlankCells.Count > 0) throw new Exception($"MISC draws the blank unlock as cell(s) {string.Join(",", s_BlankCells)} — the empty box");
            if (!s_BlankTrail.Split(" | ").Contains($"GBL0,{c_SeamBlankId}")) throw new Exception($"the blank unlock was not named to Lua: the last signals are \"{s_BlankTrail}\", none GBL0,{c_SeamBlankId}");
            R($"camo screen: a blank unlock ({c_SeamBlankId}) reached the screen as item {s_ItemCount - 1} of {s_ItemCount}, MISC left it out ({s_BlankFamilyCells} cell(s)) and said GBL0,{c_SeamBlankId}");
            // the camo screen: header path + ACCESSORIES and the camo title through the localiser, the kit's name, BACK, the buttons, the grid on the
            // family worn (MISC: the SCAR-H wears no camo) with the cursor on its button
            var s_Heading = await Probe("PageHeader_01.heading.text"); var s_SubHeading = await Probe("PageHeader_01.subHeading.text");
            var s_Kit = await Probe("TextField_01.m_textField.txtDisplay.text"); var s_Back = await Probe("Button_02.currentLabel");
            if (s_Kit != "ENGINEER") throw new Exception($"the camo screen's kit name reads \"{s_Kit}\", expected ENGINEER (UIKitComp.SelectedKitName through the document's binding)");
            if (!s_Heading.Contains("ACCESSORIES", StringComparison.OrdinalIgnoreCase) || !s_Heading.Contains("LOADOUT", StringComparison.OrdinalIgnoreCase)) throw new Exception($"the camo screen's header path reads \"{s_Heading}\", expected the accessories path plus ACCESSORIES");
            var s_Lang = await Probe("PageHeader_01._camoLang");
            if (s_SubHeading != "CAMOUFLAGE" || s_Lang != "en") throw new Exception($"the camo screen's title reads \"{s_SubHeading}\" for language \"{s_Lang}\", expected our word CAMOUFLAGE for en (the player's language through the localiser)");
            // the wheel: the grid carries the mouse listener (a row per notch on its scrollbar; MISC's two cells need none, so only its presence is measured here)
            var s_Wheel = await Probe("CamoGrid.m_mouseListener.onMouseWheel");
            if (s_Wheel == "undefined" || s_Wheel == "") throw new Exception("the grid has no mouse-wheel listener");
            // the weapon view's drag: the grid carries the press/release listener that tells the client's Lua (the mannequin turns there; the
            // preview has no mannequin, so only the listener and its threshold are measured — a press left of it is the panel's)
            var s_DragDown = await Probe("CamoGrid._camoDragListener.onMouseDown"); var s_DragUp = await Probe("CamoGrid._camoDragListener.onMouseUp");
            if (s_DragDown == "undefined" || s_DragDown == "" || s_DragUp == "undefined" || s_DragUp == "") throw new Exception("the grid has no drag listener for the weapon view");
            var s_DragLeft = (await ProbeClip(Top()[..^"instance1.".Length] + "_camoDragLeft")).Trim('"');   // a variable of the screen's timeline
            if (s_DragLeft != "480") throw new Exception($"the drag threshold reads \"{s_DragLeft}\", expected 480 (the panel's right edge plus a margin)");
            var s_SignalsBefore = (await ProbeClip("_global._camoSignals")).Trim('"');   // the door's loadout frame is already counted here
            m_RuffleHost.Dispatch(Top() + "CamoGrid._camoDragListener", "onMouseUp");   // a release with no press: no signal
            await Task.Delay(100);
            var s_Signals = (await ProbeClip("_global._camoSignals")).Trim('"');
            if (s_Signals != s_SignalsBefore) throw new Exception($"a release with no press sent a signal to Lua ({s_SignalsBefore} -> {s_Signals}), expected none");
            // the weapon view's loadout: opening the door sent the equipped identifiers of the five rows to Lua (the row's fireEvent wrapper)
            // (among the last signals, not THE last: the camo screen's mailbox receipt "MGR…" comes after it -- 2026-09-28)
            var s_Trail = (await ProbeClip("_global._camoSignalTrail")).Trim('"');
            var s_LastSignal = s_Trail.Split(" | ").LastOrDefault(t => t.StartsWith("WVL")) ?? "";
            if (s_LastSignal == "") throw new Exception($"the door did not report the loadout to Lua: the last signals are \"{s_Trail}\", none a WVL<weapon,acc1,acc2,acc3,camo>");
            var s_LoadoutIds = s_LastSignal[3..].Split(',');
            // the row sends each id unsigned (CamoRow.as camoIdKey, since 2026-09-21; the Lua reads them with Unsigned) and a slot's m_id
            // reads signed: the same 32 bits either way
            static string U32(string p_Id) => long.TryParse(p_Id, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? (v < 0 ? v + 4294967296L : v).ToString(System.Globalization.CultureInfo.InvariantCulture) : p_Id;
            if (s_LoadoutIds.Length != 5 || U32(s_LoadoutIds[0]) != U32(s_WeaponId) || U32(s_LoadoutIds[4]) != U32(s_WornId)) throw new Exception($"the loadout reads \"{s_LastSignal}\", expected five fields: the weapon {s_WeaponId} first and the worn camo {s_WornId} last");
            if (s_Back != "BACK") throw new Exception($"the camo screen's BACK button reads \"{s_Back}\"");
            var s_Btn0 = await Probe("CamoCat_01.currentLabel"); var s_Btn3 = await Probe("CamoCat_04.currentLabel"); var s_Btn7 = await Probe("CamoCat_08.currentLabel");
            if (s_Btn0 != "MISC" || s_Btn3 != "DESERT" || s_Btn7 != "WOODLAND") throw new Exception($"the family buttons read {s_Btn0} / {s_Btn3} / {s_Btn7}, expected MISC / DESERT / WOODLAND");
            var s_MiscCells = await Probe("CamoGrid.gridData.length"); var s_MiscFirst = await Probe("CamoGrid.gridData.0.data.Label");
            var s_Columns = await Probe("CamoGrid.columns"); var s_CellScale = await Probe("CamoGrid.gridClips.cell-0-s-0._xscale");
            if (s_Columns != "2" || !s_CellScale.StartsWith("198.2")) throw new Exception($"the grid lays its cells in {s_Columns} columns at scale {s_CellScale}; expected 2 columns at 198.2 % (a button's width)");
            // the mask a gap past the cells on the right and below: the highlight frame's corner ticks of the second column and the last row are not cut
            var s_MaskW = await Probe("CamoGrid.mask._width"); var s_MaskH = await Probe("CamoGrid.mask._height"); var s_GridH = await Probe("CamoGrid.gridHeight");
            if (!double.TryParse(s_MaskW, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s_Mw) || System.Math.Abs(s_Mw - 460) > 1 || !double.TryParse(s_MaskH, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s_Mh) || !double.TryParse(s_GridH, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s_Gh) || System.Math.Abs(s_Mh - (s_Gh + 6)) > 1)
                throw new Exception($"the grid's mask is {s_MaskW} x {s_MaskH} for a grid {s_GridH} high; expected 460 (two cells + two gaps) x gridHeight + 6");
            await CursorIs("buttons", "0", "on the camo screen (the family worn)");
            var s_InfoX = await Probe("KitInfoBox_01._camoArtX"); var s_InfoY = await Probe("KitInfoBox_01._camoArtY");
            if (!double.TryParse(s_InfoX, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s_Ix) || !double.TryParse(s_InfoY, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s_Iy) || System.Math.Abs(s_Ix - 96) > 2 || System.Math.Abs(s_Iy - 515) > 2)
                throw new Exception($"the info box's art is at ({s_InfoX},{s_InfoY}), expected (96,515)");
            var s_ShotScreen = Path.Combine(p_OutDir, "selftest_ruffle_camoscreen.png");
            await m_RuffleHost.CaptureAsync(s_ShotScreen);
            // the cursor is SEEN, not only probed: the MISC button's pixels with the cursor on it differ from its pixels once the cursor left (Down → AUTUMN)
            static string ButtonPixels(string p_Photo, int p_Button)
            {
                var s_Dec = new System.Windows.Media.Imaging.PngBitmapDecoder(new Uri(p_Photo), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                var s_Fr = new System.Windows.Media.Imaging.FormatConvertedBitmap(s_Dec.Frames[0], PixelFormats.Bgra32, null, 0);
                var s_Scale = System.Math.Min(s_Fr.PixelWidth / 1280.0, s_Fr.PixelHeight / 720.0);
                var s_Ox = (s_Fr.PixelWidth - 1280 * s_Scale) / 2; var s_Oy = (s_Fr.PixelHeight - 720 * s_Scale) / 2;
                var s_X0 = (int)(s_Ox + (96 + (p_Button % 2) * 230) * s_Scale); var s_Y0 = (int)(s_Oy + (140 + (p_Button / 2) * 40) * s_Scale);
                var s_W = (int)(224 * s_Scale); var s_H = (int)(34 * s_Scale);
                var s_Buf = new byte[s_W * s_H * 4]; s_Fr.CopyPixels(new Int32Rect(s_X0, s_Y0, s_W, s_H), s_Buf, s_W * 4, 0);
                long s_Sum = 0; var s_Hash = 17L;
                foreach (var b in s_Buf) { s_Sum += b; s_Hash = unchecked(s_Hash * 31 + b); }
                return $"{s_Hash:x}/{s_Sum}";
            }
            var s_MiscWithCursor = ButtonPixels(s_ShotScreen, 0);
            await Key(1);
            await CursorIs("buttons", "2", "after Down over the buttons");
            var s_ShotCursorOff = Path.Combine(p_OutDir, "selftest_ruffle_camocursoroff.png");
            await m_RuffleHost.CaptureAsync(s_ShotCursorOff);
            var s_MiscWithout = ButtonPixels(s_ShotCursorOff, 0); var s_AutumnWith = ButtonPixels(s_ShotCursorOff, 2); var s_AutumnWithout = ButtonPixels(s_ShotScreen, 2);
            if (s_MiscWithCursor == s_MiscWithout) throw new Exception($"MISC draws the same pixels with the cursor on it and without ({s_MiscWithCursor}): the keyboard cursor is invisible on the camo screen (selftest_ruffle_camoscreen.png vs selftest_ruffle_camocursoroff.png)");
            if (s_AutumnWith == s_AutumnWithout) throw new Exception($"AUTUMN draws the same pixels with the cursor on it and without ({s_AutumnWith}): the cursor did not move visibly");
            // the keys: Right → DESERT; Activate → the DESERT family: the SCAR-H's desert stripe, renamed by the table (a name wider than a cell)
            const string s_TableName = "Dune Test of a Long Desert Stripe Name";
            await Key(3);
            await CursorIs("buttons", "3", "after Down, Right over the buttons");
            await Key(6);
            await Task.Delay(400);
            var s_DesertCells = await Probe("CamoGrid.gridData.length"); var s_DesertFirst = await Probe("CamoGrid.gridData.0.data.Label");
            var s_DesertSelected = await Probe("CamoCat_04.selected");
            if (s_DesertCells != "1" || s_DesertFirst != s_TableName || s_DesertSelected != "true") throw new Exception($"the DESERT family lists {s_DesertCells} camo(s), first \"{s_DesertFirst}\", button selected {s_DesertSelected}; expected the one desert camo under the table's name \"Dune Test\"");
            // Down ×3 → into the grid: the cell under the cursor, its name and the table's text in the info box
            await Key(1); await Key(1); await Key(1);
            await CursorIs("grid", null, "after Down out of the buttons");
            var s_KeyCell = await Probe("CamoGrid.selectedIndex"); var s_InfoShown = await Probe("KitInfoBox_01._visible");
            var s_InfoLabel = await Probe("KitInfoBox_01.m_mcLabel.text"); var s_InfoText = await Probe("KitInfoBox_01.m_mcDescription.text");
            if (s_KeyCell != "0" || s_InfoShown != "true" || s_InfoLabel != s_TableName || !s_InfoText.Contains("A camo of the table")) throw new Exception($"the cursor in the grid: cell {s_KeyCell}, info box shown {s_InfoShown} \"{s_InfoLabel}\" / \"{s_InfoText}\"; expected cell 0 with the table's name and text");
            await m_RuffleHost.CaptureAsync(Path.Combine(p_OutDir, "selftest_ruffle_camocell.png"));
            // a name wider than the cell's label scrolls: the field's own horizontal scroll moves (keku: "los títulos largos no se mueven")
            var s_MaxScroll = await Probe("CamoGrid.gridClips.cell-0-s-0.m_label.txt.maxhscroll"); var s_Scrolls = await Probe("CamoGrid.gridClips.cell-0-s-0._camoScrolls");
            // the picture box of the cell widened to the document's scale (the picture fits into it when it loads): 150 % of the ImageManager's own
            var s_ImgScaled = await Probe("CamoGrid.gridClips.cell-0-s-0.m_image._camoScaled"); var s_ImgW = await Probe("CamoGrid.gridClips.cell-0-s-0.m_image.startW"); var s_ImgH = await Probe("CamoGrid.gridClips.cell-0-s-0.m_image.startH");
            if (s_ImgScaled != "true" || !double.TryParse(s_ImgW.Trim('"'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s_ImgWidth) || s_ImgWidth <= 0) throw new Exception($"the cell's picture box was not widened (scaled {s_ImgScaled}, startW {s_ImgW}, startH {s_ImgH})");
            // the name moved down by the document's shift (the game's label sits on the cell's top edge)
            var s_LabelShifted = await Probe("CamoGrid.gridClips.cell-0-s-0.m_label._camoShifted"); var s_LabelY = await Probe("CamoGrid.gridClips.cell-0-s-0.m_label._y");
            if (s_LabelShifted != "true") throw new Exception($"the cell's name was not moved down (shifted {s_LabelShifted}, _y {s_LabelY})");
            if (!int.TryParse(s_MaxScroll, out var s_Max) || s_Max <= 0 || s_Scrolls != "true") throw new Exception($"the long name does not overflow its cell's label as expected (maxhscroll {s_MaxScroll}, scrolling {s_Scrolls}): the marquee has nothing to do");
            // the game's scroll timer drives it (2.5 s still, 2 s forward, 1 s still, 0.5 s back): within one cycle the scroll moves
            var s_Scroll1 = await Probe("CamoGrid.gridClips.cell-0-s-0.m_label.txt.hscroll");
            var s_Scroll2 = s_Scroll1;
            for (var i = 0; i < 28 && s_Scroll2 == s_Scroll1; ++i) { await Task.Delay(250); s_Scroll2 = await Probe("CamoGrid.gridClips.cell-0-s-0.m_label.txt.hscroll"); }
            if (s_Scroll1 == s_Scroll2) throw new Exception($"the long name's label did not scroll in 7 s (hscroll {s_Scroll1} → {s_Scroll2}, max {s_MaxScroll})");
            await m_RuffleHost.CaptureAsync(Path.Combine(p_OutDir, "selftest_ruffle_camoscroll.png"));
            // Activate on the cell: the grid's Released with the camo's IDENTIFIER → SetAccessory4 → AccessoryChanged → the flow graph's store
            // action (the profile takes the pick, the row's data is built again); the screen stays up
            s_LogCamo = LogBox.Text.Length;
            await Key(6);
            for (var i = 0; i < 100 && !LogBox.Text[s_LogCamo..].Contains("regenerated UICustomizationComp.WeaponAccessory4"); ++i) await Task.Delay(50);
            var s_Pick = LogBox.Text[s_LogCamo..];
            if (!(s_Pick.Contains("CamoGrid fired OnItemReleased (566124738)") || s_Pick.Contains("CamoGrid fired OnItemReleased (\"566124738\")")) || !s_Pick.Contains("SetAccessory4: set UICustomizationComp.CustSelectedAccessory4 = \"566124738\"") || !s_Pick.Contains("AccessoryChanged: screen output") || !s_Pick.Contains("UpdateWeaponAccessory: engine action") || !s_Pick.Contains("regenerated UICustomizationComp.WeaponAccessory4"))
                throw new Exception("Activate on the cell did not pick the camo through the screen's graph (Released with the identifier → SetAccessory4 → AccessoryChanged → the flow graph's store action): " + s_Pick.Trim()[..System.Math.Min(700, s_Pick.Trim().Length)]);
            // the pick flashes too: AccessoryChanged chains through a post-process action of its own into the store (the engine follows one wire
            // per output port, so a second wire from the same port would never run); both must show, the glitch before the store
            var s_GlitchAt = s_Pick.IndexOf("CamoPickPostProcess: engine action", StringComparison.Ordinal);
            var s_StoreAt = s_Pick.IndexOf("UpdateWeaponAccessory: engine action", StringComparison.Ordinal);
            if (s_GlitchAt < 0 || s_StoreAt < 0 || s_GlitchAt > s_StoreAt) throw new Exception($"the pick did not chain glitch → store (glitch at {s_GlitchAt}, store at {s_StoreAt}): " + s_Pick.Trim()[..System.Math.Min(700, s_Pick.Trim().Length)]);
            await Task.Delay(1000);
            if (TopScreen() != "customizecamoscreen" || s_Host.Stack.Count != 1) throw new Exception("the pick changed the screen: " + string.Join(" < ", s_Host.Stack));
            var s_Picked = await Probe("CamoGrid._camoPicked"); var s_SelAfter = await Probe("CamoGrid.gridData.0.data.Label");
            if (s_Picked != "566124738" || s_SelAfter != s_TableName) throw new Exception($"after the pick the grid holds picked {s_Picked}, first cell \"{s_SelAfter}\"");
            var s_PickSignal = (await ProbeClip("_global._camoLastSignal")).Trim('"');   // the weapon view repaints the weapon it shows from this
            if (s_PickSignal != "WVC566124738") throw new Exception($"the pick did not reach Lua: last signal \"{s_PickSignal}\", expected WVC566124738");
            // (no photo of the pick: the screen stays as it was — the weapon shows the pick in the game, not here)
            // Back (Esc): the document StateNode's Deactivate → the accessories screen entered again, the camo screen popped; its camo row wears the pick
            // under the table's name, still without arrows
            s_LogCamo = LogBox.Text.Length;
            if (!s_Host.FireBack()) throw new Exception("Back found no Deactivate output on the camo screen's StateNode (document)");
            await WaitTop("customizeaccessoriesscreen", "after Back on the camo screen");
            var s_BackLog = LogBox.Text[s_LogCamo..];
            if (!s_BackLog.Contains("input Deactivate on customizecamoscreen") || !s_BackLog.Contains("pop sc2 customizecamoscreen")) throw new Exception("Back did not pop the camo screen through the document's Deactivate wire: " + s_BackLog.Trim()[..System.Math.Min(500, s_BackLog.Trim().Length)]);
            if (s_Host.Stack.Count != 1) throw new Exception("after Back the stack is not the accessories screen alone: " + string.Join(" < ", s_Host.Stack));
            var s_RowAfter = await Probe("KitSelector_05.mcContainer.Slot_0.header.text");
            var s_ArrowsAfter = await Probe("KitSelector_05.mcContainer.Slot_0.m_mcButtonLeft._visible");
            if (s_RowAfter != s_TableName || s_ArrowsAfter != "false") throw new Exception($"back on the accessories screen the camo row reads \"{s_RowAfter}\" with arrows {s_ArrowsAfter}; expected the table's name \"Dune Test\" and no arrows (before: \"{s_RowBefore}\")");
            await m_RuffleHost.CaptureAsync(Path.Combine(p_OutDir, "selftest_ruffle_camorow.png"));
            // the mouse: a press anywhere on the row opens the camo screen (the script fires the row's Button1Released); BACK on it (the button → Confirm) returns
            s_LogCamo = LogBox.Text.Length;
            m_RuffleHost.Dispatch(Top() + "KitSelector_05.mcContainer.Slot_0.m_mcMouseArea", "onPress");
            await WaitTop("customizecamoscreen", "after a press on the camo row");
            if (!LogBox.Text[s_LogCamo..].Contains("KitSelector_05 fired OnItemReleased")) throw new Exception("the press on the row did not fire its Button1Released");
            var s_CursorOnOpen = await Probe("CamoGrid._camoFamily");
            if (s_CursorOnOpen != "3") throw new Exception("the camo screen did not open on the family of the camo worn now (DESERT = 3): " + s_CursorOnOpen);
            s_LogCamo = LogBox.Text.Length;
            m_RuffleHost.Dispatch(Top() + "Button_02", "buttonReleased");
            await WaitTop("customizeaccessoriesscreen", "after BACK on the camo screen");
            if (!LogBox.Text[s_LogCamo..].Contains("Confirm: screen output")) throw new Exception("BACK did not leave the camo screen through its Confirm output");
            R($"camo screen: Data/CamoMenu.seam.json (the accessories screen, the new camo screen, the customization flow graph) on the SCAR-H — the camo row without arrows reads \"{s_RowBefore}\" (table: {s_TableCount} entry); Activate on it → Button1Released → CamoButton → the flow graph pops the accessories screen and pushes the camo screen (Initialized → the post-process action); header \"{s_Heading}\" / \"{s_SubHeading}\" (our word for {s_Lang}), wheel listener on the grid, kit {s_Kit}, {s_Back}, buttons {s_Btn0}…{s_Btn7}, grid {s_Columns} columns at {s_CellScale} % (MISC: {s_MiscCells} camo(s), first {s_MiscFirst}), cursor on MISC (seen: its pixels differ once the cursor leaves; selftest_ruffle_camoscreen.png / selftest_ruffle_camocursoroff.png), kit {s_Kit}, info box art at ({s_InfoX},{s_InfoY}); Down, Right, Activate → DESERT: {s_DesertCells} camo \"{s_DesertFirst}\" (the table's name); Down ×3 → cell 0, info box \"{s_InfoLabel}\" / \"{s_InfoText[..System.Math.Min(40, s_InfoText.Length)]}…\" (selftest_ruffle_camocell.png), the long name scrolling ({s_Scroll1} → {s_Scroll2} of {s_MaxScroll}; selftest_ruffle_camoscroll.png), its picture box widened to {s_ImgW}x{s_ImgH} (150 %), its name at y {s_LabelY} (shifted); Activate → Released(566124738) → SetAccessory4 → AccessoryChanged → CamoPickPostProcess (the glitch) → UpdateWeaponAccessory → regenerated, screen stays; Back → Deactivate → pop, accessories again: the row reads \"{s_RowAfter}\", no arrows (selftest_ruffle_camorow.png); a press on the row → camo screen on family {s_CursorOnOpen}; BACK → Confirm → accessories");
        }

        /// <summary>
        /// The vehicle camo windows (keku 2026-09-23: a CAMUFLAJE button beside each PERSONAL. of TIERRA / AIRE, opening the weapon's camo
        /// window adapted to vehicles, "Sin camuflaje" only for now): the document's kit movie edit gives the vehicle row a second button
        /// (button1 at 276, button2 at 426, the EQUIPOS layout) that reads our word for the language; the button → the row's own
        /// Button2Released → our SetVehicle → the screen's new output → the flow graph (pop 1) → SpawnVehicle → the window; the window lists
        /// its own "Sin camuflaje" cell; Back → StoreVehicleAccessories → TIERRA again. The same for AIRE.
        /// </summary>
        async Task StepVehicleCamo(Action<string> R, string p_OutDir)
        {
            if (m_Doc.Name != "CustomizeCamoMenu") throw new Exception("the camo menu document is not loaded: " + m_Doc.Name);
            if (!m_Doc.Movies.Any(m => m.Resource == "ui/assets/kitview" && m.Stage.Any(o => o.StartsWith("clone:KitView_2/button1:button2:"))))
                throw new Exception("the document does not give the kit movie's vehicle row its second button");
            if (!m_Rime.HasPartition("gameplay/vehicles/mbtcustomization") || !m_Rime.HasPartition("gameplay/vehicles/jetcustomization"))
                throw new Exception("the vehicle customization assets are not in the cache (a mount fetches them)");
            var s_Summary = new System.Collections.Generic.List<string>();
            // our word for the preview's language (the document's list: the rows' button and the window's title say it); the game's own
            // texts (the no-camo cell, BACK) are only required to be localised, whatever the language
            var s_Word = m_Settings.Language switch { "us" => "CAMOUFLAGE", "es" => "CAMUFLAJE", "fr" => "CAMOUFLAGE", "ge" => "TARNUNG", "it" => "MIMETICA", _ => null };
            foreach (var (s_Kind, s_Rows, s_Window, s_Door, s_Tab) in new[]
                     {
                         ("land", "customizelandscreen", "customizelandcamoscreen", "LandCamoButton", "LAND"),
                         ("air", "customizeairscreen", "customizeaircamoscreen", "AirCamoButton", "AIR"),
                     })
            {
                if (!await OpenScreen("ui/flow/screen/" + s_Rows)) throw new Exception($"the {s_Rows} screen did not open");
                if (m_Current == null || !m_Current.Movie!.FrameScripts().Contains("camovehiclerow")) throw new Exception($"the {s_Rows} movie shown does not carry the frame script camovehiclerow");
                var s_Mark = LogBox.Text.Length;
                await StartPreviewForSeam(p_OutDir);
                var s_Host = m_UiHost ?? throw new Exception("no host for the vehicle preview");
                for (var i = 0; i < 600 && !LogBox.Text[s_Mark..].Contains($"focus on {s_Rows}: KitView_01 "); ++i) await Task.Delay(50);
                await Task.Delay(1000);
                string Top() => s_Host.Stack.Count == 0 ? "_level0.none.instance1." : "_level0." + s_Host.Stack[^1].Split(':')[0] + ".instance1.";
                string TopScreen() => s_Host.Stack.Count == 0 ? "" : s_Host.Stack[^1].Split(':')[1];
                async Task<string> Probe(string p) => (await ProbeClip(Top() + p)).Trim('"');
                async Task WaitTop(string p_Screen, string p_When)
                {
                    for (var i = 0; i < 200 && (TopScreen() != p_Screen || !LogBox.Text.Contains($"{s_Host.Stack[^1].Split(':')[0]} {p_Screen} loaded: initializeScreen")); ++i) await Task.Delay(50);
                    if (TopScreen() != p_Screen) throw new Exception($"{p_When}: the screen on top is {TopScreen()}, expected {p_Screen} (stack: {string.Join(" < ", s_Host.Stack)})");
                    await Task.Delay(1200);
                }
                if (TopScreen() != s_Rows) throw new Exception($"the preview did not start on {s_Rows}: " + string.Join(" < ", s_Host.Stack));
                // the rows: the game's own classes of the map (the generator's), each with two buttons -- PERSONAL. moved left, ours where it was
                var s_RowCount = await Probe("KitView_01.m_slots.length");
                var s_B1 = await Probe("KitView_01.mcContainer.Slot_0.button1.currentLabel"); var s_B2 = await Probe("KitView_01.mcContainer.Slot_0.button2.currentLabel");
                var s_B1x = await Probe("KitView_01.mcContainer.Slot_0.button1._x"); var s_B2x = await Probe("KitView_01.mcContainer.Slot_0.button2._x");
                var s_B2Seen = await Probe("KitView_01.mcContainer.Slot_0.button2._visible");
                var s_Header = await Probe("KitView_01.mcContainer.Slot_0.header.text");
                if (s_B2Seen != "true" || (s_Word != null ? s_B2 != s_Word : (s_B2 == "" || s_B2.StartsWith("ID_")))) throw new Exception($"{s_Rows}: the row's second button is visible {s_B2Seen} reading \"{s_B2}\"; expected our word {s_Word ?? "(any localised)"} for {m_Settings.Language}");
                // keku: PERSONAL. on the right, CAMUFLAJE on its left (118 px each, flush right)
                if (s_B1x != "456" || s_B2x != "336") throw new Exception($"{s_Rows}: PERSONAL. sits at x {s_B1x}, CAMUFLAJE at {s_B2x}; expected 456 / 336");
                var s_B1w = await Probe("KitView_01.mcContainer.Slot_0.button1._width"); var s_B2w = await Probe("KitView_01.mcContainer.Slot_0.button2._width");
                // every class name's field ends before the buttons at the game's size; a name longer than its room scrolls (its hscroll moves
                // within the game's scroll cycle), a short one does not
                var s_Fits = new System.Collections.Generic.List<string>();
                for (var r = 0; r < (int.TryParse(s_RowCount, out var s_N) ? s_N : 0); ++r)
                {
                    var s_Slot = $"KitView_01.mcContainer.Slot_{r}.header";
                    var s_RowName = await Probe(s_Slot + ".text");
                    var s_Right = await Probe(s_Slot + "._camoRight"); var s_Scrolls = await Probe(s_Slot + "._camoScrolls");
                    var s_Size = await Probe(s_Slot + "._camoSize"); var s_TextW = await Probe(s_Slot + ".txt.textWidth"); var s_Room = await Probe(s_Slot + "._camoRoom");
                    if (!double.TryParse(s_Right, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s_R) || s_R > 336 - 4)
                        throw new Exception($"{s_Rows}: row {r} \"{s_RowName}\" has its field end at x {s_Right}, under CAMUFLAJE at 336");
                    if (s_Size != "20") throw new Exception($"{s_Rows}: row {r} \"{s_RowName}\" is drawn at {s_Size} px; the name keeps the game's 20");
                    var s_Long = double.Parse(s_TextW, System.Globalization.CultureInfo.InvariantCulture) > double.Parse(s_Room, System.Globalization.CultureInfo.InvariantCulture);
                    if (s_Long != (s_Scrolls == "true")) throw new Exception($"{s_Rows}: row {r} \"{s_RowName}\" ({s_TextW} px of text in {s_Room}) scrolls {s_Scrolls}");
                    var s_Moved = "";
                    if (s_Long)
                    {
                        // the game's timer: 2.5 s still, 2 s forward, 1 s still, 0.5 s back -- within 7 s the scroll moves
                        var s_H1 = await Probe(s_Slot + ".txt.hscroll"); var s_H2 = s_H1; var s_Max = await Probe(s_Slot + ".txt.maxhscroll");
                        for (var i = 0; i < 28 && s_H2 == s_H1; ++i) { await Task.Delay(250); s_H2 = await Probe(s_Slot + ".txt.hscroll"); }
                        if (s_H1 == s_H2) throw new Exception($"{s_Rows}: row {r} \"{s_RowName}\" did not scroll in 7 s (hscroll {s_H1}, max {s_Max})");
                        s_Moved = $", scrolling {s_H1}→{s_H2} of {s_Max}";
                    }
                    s_Fits.Add($"\"{s_RowName}\" {s_TextW}px in {s_Room}{s_Moved}");
                }
                if (string.IsNullOrEmpty(s_B1) || s_B1 == s_B2 || s_B1.StartsWith("ID_")) throw new Exception($"{s_Rows}: the row's first button reads \"{s_B1}\"");
                var s_RowsShot = Path.Combine(p_OutDir, $"selftest_ruffle_veh{s_Kind}rows.png");
                await m_RuffleHost!.CaptureAsync(s_RowsShot);
                // the second button of the first row: the row's own code (selectSlot + Button2Released) → our SetVehicle → the new output →
                // the flow graph (pop 1) → our SpawnVehicle → the vehicle window
                s_Mark = LogBox.Text.Length;
                m_RuffleHost.Dispatch(Top() + "KitView_01.mcContainer.Slot_0", "onButtonReleased", 2);
                await WaitTop(s_Window, $"after CAMOUFLAGE on the first {s_Tab} row");
                var s_Open = LogBox.Text[s_Mark..];
                foreach (var s_Want in new[] { "KitView_01 fired OnChanged", $"{s_Door}SetVehicle: set UICustomizationComp.CustSelectedVehicle", $"{s_Door}: screen output", $"{s_Door}Spawn: engine action", $"push ui/flow/screen/{s_Window}" })
                    if (!s_Open.Contains(s_Want)) throw new Exception($"CAMOUFLAGE on the {s_Tab} row did not open the window through the graph (missing \"{s_Want}\"): " + s_Open.Trim()[..System.Math.Min(900, s_Open.Trim().Length)]);
                if (s_Host.Stack.Count != 1) throw new Exception($"the {s_Tab} window did not replace {s_Rows}: " + string.Join(" < ", s_Host.Stack));
                // the window: the vehicle's name, the land/air path + our title, BACK, the families, ONE cell -- "Sin camuflaje" (No Camo in en)
                var s_Heading = await Probe("PageHeader_01.heading.text"); var s_SubHeading = await Probe("PageHeader_01.subHeading.text");
                var s_Name = await Probe("TextField_01.m_textField.txtDisplay.text"); var s_Back = await Probe("Button_02.currentLabel");
                // the seam's document carries a vehicle table in the window's mailbox (one camo, worn): 2 cells once it has arrived
                // ⭐ AT ONCE (keku 2026-09-23: "debe ser todo instantáneo"): the table is written before the window opens and the class comes
                // with the window's own name binding -- half a second is the ceiling, not a wait for a vehicle
                for (var i = 0; i < 5 && await Probe("CamoGrid.gridData.length") != "2"; ++i) await Task.Delay(100);
                var s_Cells = await Probe("CamoGrid.gridData.length"); var s_Cell0 = await Probe("CamoGrid.gridData.0.data.Label");
                var s_Cell1 = await Probe("CamoGrid.gridData.1.data.Label");
                // what is WORN lives in the script's items (DefaultSelected), not in the grid's cells; the cursor opens on it
                var s_Timeline = Top()[..^"instance1.".Length];
                for (var i = 0; i < 5 && (await ProbeClip(s_Timeline + "camoItems.1.DefaultSelected")).Trim('"') != "true"; ++i) await Task.Delay(100);
                var s_Sel0 = (await ProbeClip(s_Timeline + "camoItems.0.DefaultSelected")).Trim('"'); var s_Sel1 = (await ProbeClip(s_Timeline + "camoItems.1.DefaultSelected")).Trim('"');
                var s_Cursor = await Probe("CamoGrid.selectedIndex");
                var s_Btn0 = await Probe("CamoCat_01.currentLabel"); var s_Btn7 = await Probe("CamoCat_08.currentLabel");
                var s_Signal = (await ProbeClip("_global._camoLastSignal")).Trim('"');
                if (s_Word != null ? s_SubHeading != s_Word : s_SubHeading == "") throw new Exception($"the {s_Tab} window's title reads \"{s_SubHeading}\", expected {s_Word}");
                if (s_Cells != "2" || s_Cell0 == "" || s_Cell0.StartsWith("ID_") || (m_Settings.Language == "us" && s_Cell0 != "No Camo"))
                    throw new Exception($"the {s_Tab} window lists {s_Cells} cell(s), first \"{s_Cell0}\"; expected its no-camo cell (localised) and the table's vehicle camo");
                if (s_Cell1 != "Berkut" || s_Sel1 != "true" || s_Sel0 == "true" || s_Cursor != "1")
                    throw new Exception($"the {s_Tab} window's vehicle camo cell reads \"{s_Cell1}\" (worn {s_Sel1}; no-camo cell worn {s_Sel0}; cursor on cell {s_Cursor}); expected Berkut, worn by the table's W record, the cursor on it");
                // the FAMILY chosen at the bake travels (keku 2026-09-24): the table files Berkut under DESERT (4th button), a family its
                // name would never guess -- the window opens on the worn camo's family with that button marked
                var s_Family = (await ProbeClip(s_Timeline + "camoFamily")).Trim('"'); var s_Desert = await Probe("CamoCat_04.selected");
                var s_DesertLabel = await Probe("CamoCat_04.currentLabel");
                if (s_Family != "3" || s_Desert != "true" || s_DesertLabel != "DESERT")
                    throw new Exception($"the {s_Tab} window opened on family {s_Family} (button 4 \"{s_DesertLabel}\" selected {s_Desert}); expected 3 = DESERT, the family the table files the worn camo under");
                if (s_Back == "" || s_Back.StartsWith("ID_") || s_Btn0 != "MISC" || s_Btn7 != "WOODLAND") throw new Exception($"the {s_Tab} window's buttons read {s_Back} / {s_Btn0} / {s_Btn7}");
                // the list's word, or what follows it since the window has a table of its own: its receipt ("VTB<camos>,<worn>,<vehicle>") and
                // its asks for it again ("VTQ<n>")
                if (!s_Signal.StartsWith("VGR1,") && s_Signal != "VHD" && !s_Signal.StartsWith("VTB") && !s_Signal.StartsWith("VCL"))
                    throw new Exception($"the {s_Tab} window did not report its list to Lua: last signal \"{s_Signal}\", expected VGR1,<delivered> (or VHD / VTB / VCL after it)");
                // the game's vehicle taken away once the view has read it: the grid's TooltipActive → HideVehicle → the flow graph's
                // UnspawnVehicle, _camoHideMs after the window is set up; the view is told ("VHD"). With _camoHideMs -1 (the copy off)
                // it must NOT happen: the game's vehicle stays -- measured over the same 4 s
                var s_HideMs = (await ProbeClip(Top()[..^"instance1.".Length] + "_camoHideMs")).Trim('"');
                var s_HideOn = double.TryParse(s_HideMs, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s_Hm) && s_Hm >= 0;
                for (var i = 0; i < 80 && !LogBox.Text[s_Mark..].Contains("Hide: engine action"); ++i) await Task.Delay(50);
                var s_Hide = LogBox.Text[s_Mark..];
                var s_Hidden = s_Hide.Contains("CamoGrid fired ToggleOn") && s_Hide.Contains("HideVehicle: screen output") && s_Hide.Contains("Hide: engine action");
                if (s_HideOn != s_Hidden)
                    throw new Exception($"the {s_Tab} window {(s_Hidden ? "took" : "did not take")} the game's vehicle away with _camoHideMs = {s_HideMs}: " + s_Hide.Trim()[^System.Math.Min(900, s_Hide.Trim().Length)..]);
                var s_HideSignal = (await ProbeClip("_global._camoLastSignal")).Trim('"');
                if (s_HideOn && s_HideSignal != "VHD") throw new Exception($"the {s_Tab} window did not tell the view the vehicle went away: last signal \"{s_HideSignal}\", expected VHD");
                // nothing asks for the table any more: it is there the instant the window opens (checked above, within half a second)
                // a PICK, in one go and in the game's own order (unspawn, select, spawn -- CustomizationGraph's tab switch): the grid's
                // Released → PickVehicleCamo → PickGlitch → PickUnspawn, and right behind it its TooltipActive(row) →
                // RespawnSetVehicle (the unspawn leaves the selection at -1: run 114) → RespawnVehicle → PickSpawn. The window stays;
                // the pick tells the Lua its CLASS ("VVC<id>,<class SID>") and the respawn its row ("VRS<row>,<row now>")
                s_Mark = LogBox.Text.Length;
                m_RuffleHost.Dispatch(Top()[..^"instance1.".Length].TrimEnd('.'), "camoPick", 0);
                for (var i = 0; i < 60 && !LogBox.Text[s_Mark..].Contains("PickSpawn: engine action"); ++i) await Task.Delay(50);
                var s_PickLog = LogBox.Text[s_Mark..];
                var s_Order = new[] { "CamoGrid fired OnItemReleased", "PickVehicleCamo: screen output", "PickGlitch: engine action",
                                      "PickUnspawn: engine action", "CamoGrid fired ToggleOn", "RespawnSetVehicle: set",
                                      "RespawnVehicle: screen output", "PickSpawn: engine action" };
                var s_At = -1;
                foreach (var s_Step in s_Order)
                {
                    var s_Next = s_PickLog.IndexOf(s_Step, System.Math.Max(0, s_At), StringComparison.Ordinal);
                    if (s_Next < 0)
                        throw new Exception($"a pick in the {s_Tab} window did not go {string.Join(" → ", s_Order)} in that order (missing or out of order: \"{s_Step}\"): " + s_PickLog.Trim()[..System.Math.Min(900, s_PickLog.Trim().Length)]);
                    s_At = s_Next;
                }
                var s_PickSignal = (await ProbeClip("_global._camoLastPickSignal")).Trim('"');
                if (!s_PickSignal.StartsWith("VVC") || !s_PickSignal.EndsWith(",ID_EOR_SCORINGBUCKET_VEHICLEMBT"))
                    throw new Exception($"a pick in the {s_Tab} window did not tell the Lua its class: pick signal \"{s_PickSignal}\", expected VVC<id>,ID_EOR_SCORINGBUCKET_VEHICLEMBT");
                // and the ask REPEATED at each of _camoRespawnMs (a spawn while the old vehicle is still there does nothing: run 115), the
                // last one saying so ("VRS<row>,<row now>,<n>")
                static int Occurrences(string p_Text, string p_What)
                {
                    var s_N = 0;
                    for (var s_I = p_Text.IndexOf(p_What, StringComparison.Ordinal); s_I >= 0; s_I = p_Text.IndexOf(p_What, s_I + p_What.Length, StringComparison.Ordinal))
                        ++s_N;
                    return s_N;
                }
                var s_Asks = ((await ProbeClip(Top()[..^"instance1.".Length] + "_camoRespawnMs")).Trim('"')).Split(',').Length;
                for (var i = 0; i < 60 && Occurrences(LogBox.Text[s_Mark..], "PickSpawn: engine action") < s_Asks; ++i) await Task.Delay(50);
                var s_Spawns = Occurrences(LogBox.Text[s_Mark..], "PickSpawn: engine action");
                var s_RespawnSignal = (await ProbeClip("_global._camoLastSignal")).Trim('"');
                if (s_Asks < 2 || s_Spawns != s_Asks || !s_RespawnSignal.StartsWith("VRS") || !s_RespawnSignal.EndsWith("," + s_Asks))
                    throw new Exception($"the {s_Tab} window did not repeat its ask for the vehicle: {s_Spawns} spawn(s) for {s_Asks} ask(s), last signal \"{s_RespawnSignal}\", expected VRS<row>,<row now>,{s_Asks}");
                if (s_Host.Stack.Count != 1 || !s_Host.Stack[^1].Contains(s_Window.Split('/').Last())) throw new Exception($"a pick closed the {s_Tab} window: " + string.Join(" < ", s_Host.Stack));
                // and the pick is marked at once (cell 0 = "Sin camuflaje" now worn), before the table is asked for again
                var s_After0 = (await ProbeClip(s_Timeline + "camoItems.0.DefaultSelected")).Trim('"'); var s_After1 = (await ProbeClip(s_Timeline + "camoItems.1.DefaultSelected")).Trim('"');
                if (s_After0 != "true" || s_After1 == "true") throw new Exception($"the pick of \"Sin camuflaje\" was not marked in the {s_Tab} window (cell 0 worn {s_After0}, cell 1 worn {s_After1})");
                var s_WindowShot = Path.Combine(p_OutDir, $"selftest_ruffle_veh{s_Kind}camo.png");
                await m_RuffleHost.CaptureAsync(s_WindowShot);
                // Back (Esc): the window's Deactivate → our StoreVehicleAccessories (pop 1) → the rows screen again
                s_Mark = LogBox.Text.Length;
                if (!s_Host.FireBack()) throw new Exception($"Back found no Deactivate output on the {s_Tab} window's StateNode");
                await WaitTop(s_Rows, $"after Back on the {s_Tab} window");
                var s_BackLog = LogBox.Text[s_Mark..];
                if (!s_BackLog.Contains($"pop") || !s_BackLog.Contains("BackStore: engine action") || !s_BackLog.Contains("BackSpawn: engine action")) throw new Exception($"Back on the {s_Tab} window did not store, spawn the game's vehicle again and pop through the document's wires: " + s_BackLog.Trim()[..System.Math.Min(600, s_BackLog.Trim().Length)]);
                s_Summary.Add($"{s_Tab}: {s_RowCount} row(s), first \"{s_Header}\" with buttons \"{s_B1}\" at {s_B1x} ({s_B1w} wide) / \"{s_B2}\" at {s_B2x} ({s_B2w} wide), names {string.Join(", ", s_Fits)} ({Path.GetFileName(s_RowsShot)}); CAMOUFLAGE → OnChanged → {s_Door}SetVehicle → {s_Door} → {s_Door}Spawn → {s_Window}: \"{s_Heading}\" / \"{s_SubHeading}\", vehicle \"{s_Name}\", {s_Cells} cell \"{s_Cell0}\", signal {s_Signal} ({Path.GetFileName(s_WindowShot)}); Back → store → {s_Rows}");
            }
            R("vehicle camo windows: " + string.Join(" | ", s_Summary));
        }

        /// <summary>
        /// The soldier's window IN PLACE OF APARIENCIA (keku 2026-09-28: tabs CONJUNTO · CABEZA · TORSO · PIERNAS, "todo con todo"). The
        /// KITS screen's APPEARANCE button (a row's button2 → OnChanged, and the console bar's Edit) goes, shipped static, into an output
        /// of ours, SkinButton, instead of the game's ChangeAppearance; the flow graph takes it into a loadout update of ours and our
        /// window -- never the game's appearance screen.
        /// The window: the appearance path and title, the kit's name, BACK, four tabs reading our words for the language, the eight
        /// families four to a row under them, a grid of the kit's own looks (the game's appearance rows) plus the seam table's skin of the
        /// US assault (torso and legs), the soldier known from the KITS screen's data (US_Assault). CONJUNTO marks nothing (the torso
        /// wears the skin, the rest the game's look); TORSO lists the skin and marks it (the window opens that tab on its family); CABEZA
        /// does not list it; a pick on PIERNAS says "SKNlegs,<id>,US_Assault" to the Lua, fires the grid's Released → our row into the game's selected appearance → SkinPicked → the game's store → the
        /// glitch, stays, and marks the cell; a pick on CONJUNTO sets the three parts (TORSO then marks the stock look, not the skin);
        /// Back → the glitch → pop → the KITS screen.
        /// </summary>
        async Task StepSoldierSkin(Action<string> R, string p_OutDir)
        {
            if (m_Doc.Name != "CustomizeCamoMenu") throw new Exception("the camo menu document is not loaded: " + m_Doc.Name);
            var s_Flow = m_Doc.Screens.FirstOrDefault(s => s.Partition == "ui/flow/graph/spawn/customizationgraph") ?? throw new Exception("the document does not edit the customization flow graph");
            // ⛔ keku's boot (2026-09-28, "aun carga el ui vanilla"): a LIVE removal of a shipped wire in the flow graph did not take in the
            // game -- the door is moved in the KITS screen instead (shipped static: its two wires into ChangeAppearance left out, into
            // our SkinButton), and the flow graph only ADDS (a removal there would be the unproven road again)
            if (s_Flow.RemovedConnections.Count != 0) throw new Exception("the document removes shipped wires of the flow graph LIVE -- a road that did not take in the game");
            var s_Kits = m_Doc.Screens.FirstOrDefault(s => s.Partition == "ui/flow/screen/customizesoldierscreen") ?? throw new Exception("the document does not edit the KITS screen");
            foreach (var s_Wire in new[] { "03727261-DF82-4D5C-8DDF-FCB2E8C69E5A", "CCC8878F-E833-4C4C-8724-5A2F1D97F7DE" })
                if (!s_Kits.RemovedConnections.Any(r => string.Equals(r.Guid, s_Wire, StringComparison.OrdinalIgnoreCase)))
                    throw new Exception($"the KITS screen keeps the game's wire {s_Wire} into ChangeAppearance");
            if (!(s_Kits.Delivery != null ? string.Equals(s_Kits.Delivery, "static", StringComparison.OrdinalIgnoreCase) : m_Doc.IsStatic)) throw new Exception("the KITS screen's edits are not shipped static");
            if (!m_Doc.Screens.Any(s => s.Partition == "ui/flow/screen/customizeskinscreen" && s.New)) throw new Exception("the document has no soldier window");
            var s_Words = m_Settings.Language switch
            {
                "us" => new[] { "OUTFIT", "HEAD", "TORSO", "LEGS" },
                "es" => new[] { "CONJUNTO", "CABEZA", "TORSO", "PIERNAS" },
                _ => null,
            };
            var s_KitName = DataGenerator.Current.Kit.Split('/').Last();
            var s_Want = (s_KitName.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? "RU" : "US") + "_" +
                         (s_KitName.Contains("engineer", StringComparison.OrdinalIgnoreCase) ? "Engineer" : s_KitName.Contains("support", StringComparison.OrdinalIgnoreCase) ? "Support" :
                          s_KitName.Contains("recon", StringComparison.OrdinalIgnoreCase) ? "Recon" : "Assault");
            if (!await OpenScreen("ui/flow/screen/customizesoldierscreen")) throw new Exception("the KITS screen did not open");
            var s_Mark = LogBox.Text.Length;
            await StartPreviewForSeam(p_OutDir);
            var s_Host = m_UiHost ?? throw new Exception("no host for the preview");
            for (var i = 0; i < 600 && !LogBox.Text[s_Mark..].Contains("focus on customizesoldierscreen: KitView_01 "); ++i) await Task.Delay(50);
            await Task.Delay(1000);
            string Top() => s_Host.Stack.Count == 0 ? "_level0.none.instance1." : "_level0." + s_Host.Stack[^1].Split(':')[0] + ".instance1.";
            string TopScreen() => s_Host.Stack.Count == 0 ? "" : s_Host.Stack[^1].Split(':')[1];
            async Task<string> Probe(string p) => (await ProbeClip(Top() + p)).Trim('"');
            string Timeline() => Top()[..^"instance1.".Length];
            async Task<string> Var(string p) => (await ProbeClip(Timeline() + p)).Trim('"');
            async Task WaitTop(string p_Screen, string p_When)
            {
                for (var i = 0; i < 200 && (TopScreen() != p_Screen || !LogBox.Text.Contains($"{s_Host.Stack[^1].Split(':')[0]} {p_Screen} loaded: initializeScreen")); ++i) await Task.Delay(50);
                if (TopScreen() != p_Screen) throw new Exception($"{p_When}: the screen on top is {TopScreen()}, expected {p_Screen} (stack: {string.Join(" < ", s_Host.Stack)})");
                await Task.Delay(1200);
            }
            static string Unsigned(string p_Id) => long.TryParse(p_Id, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? (v < 0 ? v + 4294967296L : v).ToString(System.Globalization.CultureInfo.InvariantCulture) : p_Id;
            if (TopScreen() != "customizesoldierscreen") throw new Exception("the preview did not start on the KITS screen: " + string.Join(" < ", s_Host.Stack));
            // APPEARANCE on the first row: the row's own code (button2 → OnChanged = Button2Released) → ChangeAppearance → the flow graph's door
            s_Mark = LogBox.Text.Length;
            m_RuffleHost!.Dispatch(Top() + "KitView_01.mcContainer.Slot_0", "onButtonReleased", 2);
            await WaitTop("customizeskinscreen", "after APPEARANCE on the first kit row");
            var s_Open = LogBox.Text[s_Mark..];
            foreach (var s_Step in new[] { "SkinButton: screen output", "SkinEnterLoadout: engine action", "push ui/flow/screen/customizeskinscreen" })
                if (!s_Open.Contains(s_Step)) throw new Exception($"APPEARANCE did not open the soldier window through the moved door (missing \"{s_Step}\"): " + s_Open.Trim()[..System.Math.Min(900, s_Open.Trim().Length)]);
            if (s_Open.Contains("customizeappearancescreen") || s_Open.Contains("ChangeAppearance: screen output")) throw new Exception("APPEARANCE still fired the game's door or reached its appearance screen: " + s_Open.Trim()[..System.Math.Min(900, s_Open.Trim().Length)]);
            if (s_Host.Stack.Count != 1) throw new Exception("the soldier window did not replace the KITS screen: " + string.Join(" < ", s_Host.Stack));
            // the window: header, kit, BACK, the tabs (our words), the families, the soldier, the looks and the table's skin
            var s_Heading = await Probe("PageHeader_01.heading.text"); var s_SubHeading = await Probe("PageHeader_01.subHeading.text");
            var s_Kit = await Probe("TextField_01.m_textField.txtDisplay.text"); var s_Back = await Probe("Button_02.currentLabel");
            var s_Tabs = new System.Collections.Generic.List<string>();
            for (var t = 1; t <= 4; ++t) s_Tabs.Add(await Probe($"SkinTab_0{t}.currentLabel"));
            var s_Fam0 = await Probe("CamoCat_01.currentLabel"); var s_Fam7 = await Probe("CamoCat_08.currentLabel");
            var s_TabY = await Probe("SkinTab_01._y"); var s_Fam1Y = await Probe("CamoCat_01._y"); var s_FamY = await Probe("CamoCat_05._y"); var s_Fam4X = await Probe("CamoCat_04._x");
            if (s_SubHeading == "" || s_SubHeading.StartsWith("ID_") || s_Heading == "" || s_Heading.StartsWith("ID_")) throw new Exception($"the soldier window's header reads \"{s_Heading}\" / \"{s_SubHeading}\"");
            if (s_Kit == "" || s_Back == "" || s_Back.StartsWith("ID_")) throw new Exception($"the soldier window's kit reads \"{s_Kit}\", BACK \"{s_Back}\"");
            if (s_Words != null ? !s_Tabs.SequenceEqual(s_Words) : s_Tabs.Any(w => w == "" || w.StartsWith("ID_"))) throw new Exception($"the tabs read {string.Join(" · ", s_Tabs)}; expected {(s_Words == null ? "our words" : string.Join(" · ", s_Words))} for {m_Settings.Language}");
            if (s_Fam0 != "MISC" || s_Fam7 != "WOODLAND") throw new Exception($"the families read {s_Fam0} … {s_Fam7}");
            // the tabs one row at y 140, the families two rows of four under them (the 4th family in the tabs' 4th column) -- set apart from
            // the tabs by 26 px (keku 2026-09-28: "separa algo más los botones nuevos de las categorías camo"; 10 px read as one block)
            if (s_TabY != "140" || s_Fam1Y != "200" || s_FamY != "240" || s_Fam4X != "441") throw new Exception($"the tabs sit at y {s_TabY}, the 1st family at y {s_Fam1Y}, the 5th at y {s_FamY}, the 4th family at x {s_Fam4X}; expected 140 / 200 / 240 / 441");
            // the drag turns the mannequin (keku: "rotar el personaje, pero sólo en 360º"): the grid carries the press/release listener and
            // its threshold is the weapon windows' (the turn itself is CamoSoldierView.lua's, measured by soldiers/turn_harness.py)
            var s_SkinDragDown = await Probe("CamoGrid._camoDragListener.onMouseDown");
            var s_SkinDragLeft = (await ProbeClip(Timeline() + "_camoDragLeft")).Trim('"');
            if (s_SkinDragDown == "undefined" || s_SkinDragDown == "" || s_SkinDragLeft != "480") throw new Exception($"the soldier window's drag: listener \"{s_SkinDragDown}\", threshold \"{s_SkinDragLeft}\"; expected a listener and 480");
            for (var i = 0; i < 10 && await Var("camoSoldierKey") != s_Want; ++i) await Task.Delay(100);
            var s_Key = await Var("camoSoldierKey");
            if (s_Key != s_Want) throw new Exception($"the window is about \"{s_Key}\"; the preview's profile is {DataGenerator.Current.Kit} ({s_Want})");
            for (var i = 0; i < 10 && await Var("camoStock.length") == "0"; ++i) await Task.Delay(100);
            var s_Stock = int.TryParse(await Var("camoStock.length"), out var s_S) ? s_S : 0;
            var s_Items = int.TryParse(await Var("camoItems.length"), out var s_I) ? s_I : 0;
            if (s_Stock < 10 || s_Items != s_Stock + 1) throw new Exception($"CONJUNTO lists {s_Items} look(s) of {s_Stock} of the game's; expected the kit's looks and the table's skin (+1)");
            var s_SkinAt = s_Stock;   // the skins come after the game's looks
            var s_SkinLabel = await Var($"camoItems.{s_SkinAt}.Label"); var s_SkinIsSkin = await Var($"camoItems.{s_SkinAt}._camoSkin");
            if (s_SkinLabel != "Snow Test Skin" || s_SkinIsSkin != "true") throw new Exception($"the item after the game's looks reads \"{s_SkinLabel}\" (skin {s_SkinIsSkin}); expected the table's \"Snow Test Skin\"");
            // CONJUNTO shows the SKIN's picture and family, whatever its parts' own cells say (the table gives its legs another)
            const string c_SkinThumb = "UI/Art/Persistence/Camo/default", c_LegsThumb = "UI/Art/Persistence/Specializations/Camo/PremiumCamo_Berkut";
            var s_AllImage = await Var($"camoItems.{s_SkinAt}.ItemImage"); var s_AllFam = await Var($"camoItems.{s_SkinAt}._camoFamily");
            if (s_AllImage != c_SkinThumb || s_AllFam != "SNOW") throw new Exception($"CONJUNTO: the skin's cell shows \"{s_AllImage}\" filed under \"{s_AllFam}\"; expected the skin's own ({c_SkinThumb}, SNOW)");
            // CONJUNTO: the parts differ (the torso wears the skin) -- nothing marked
            var s_MarkedOnAll = new System.Collections.Generic.List<int>();
            for (var k = 0; k < s_Items; ++k) if (await Var($"camoItems.{k}.DefaultSelected") == "true") s_MarkedOnAll.Add(k);
            if (s_MarkedOnAll.Count != 0) throw new Exception($"CONJUNTO marks item(s) {string.Join(",", s_MarkedOnAll)}; the parts differ (the table's torso wears the skin) -- nothing should be");
            // the keyboard cursor rests on the tab picked (it sat on MISC while the window showed another family: the first photo)
            var s_CursorZone = await Probe("CamoGrid._camoZone"); var s_CursorOn = await Probe("CamoGrid._camoCursor");
            if (s_CursorZone != "buttons" || s_CursorOn != "0") throw new Exception($"the cursor opens in zone \"{s_CursorZone}\" on button {s_CursorOn}; expected the CONJUNTO tab (buttons, 0)");
            var s_ShotAll = Path.Combine(p_OutDir, "selftest_ruffle_skin_conjunto.png");
            await m_RuffleHost.CaptureAsync(s_ShotAll);
            // a stock look is filed by its NAME, the same in every language (the table's R record: the NWU row, "XP2_NWU navy"): NAVAL
            // lists it whatever its label reads
            var s_Nwu = Unsigned(unchecked((int)(uint)RimeLib.Cmd.UiBuilder.DataKeys.FbHash("UI/Art/Persistence/Camo/U_CAMO_XP2_NWU")).ToString());
            var s_NwuAt = -1;
            for (var k = 0; k < s_Stock && s_NwuAt < 0; ++k) if (Unsigned(await Var($"camoItems.{k}.Index")) == s_Nwu) s_NwuAt = k;
            if (s_NwuAt < 0) throw new Exception($"the kit's looks carry no NWU row (id {s_Nwu}): the seam's R record names nothing");
            m_RuffleHost.Dispatch(Top() + "CamoCat_05", "buttonReleased");
            for (var i = 0; i < 10 && await Var("camoFamily") != "4"; ++i) await Task.Delay(100);
            var s_NavalCells = int.TryParse(await Var("camoShown.length"), out var s_Nc) ? s_Nc : 0;
            var s_NavalHas = false;
            for (var c = 0; c < s_NavalCells; ++c) if (await Var($"camoShown.{c}") == s_NwuAt.ToString()) s_NavalHas = true;
            var s_NwuLabel = await Var($"camoItems.{s_NwuAt}.Label");
            if (!s_NavalHas) throw new Exception($"NAVAL ({s_NavalCells} cell(s)) does not list the NWU look (item {s_NwuAt}, \"{s_NwuLabel}\"): the look's name did not file it");
            // ⛔ THE SCROLL THAT OUTLIVED ITS FAMILY (keku 2026-09-28, capture: "cuando mueves la barra scroll vertical lateral y te mueves a
            // otra pestaña de categoría camo, a veces aparecen vacías"): the game's Grid.removeScrollbar takes the bar away without putting its
            // cells back. A family with a bar scrolled to the bottom, then one that fits: its cells must sit at the grid's start (the
            // stimulus checked first -- the scroll must really have moved the cells, or the test could not fail)
            var s_Counts = new int[8];
            for (var f = 0; f < 8; ++f)
            {
                m_RuffleHost.Dispatch(Top() + $"CamoCat_0{f + 1}", "buttonReleased");
                for (var i = 0; i < 10 && await Var("camoFamily") != f.ToString(); ++i) await Task.Delay(100);
                s_Counts[f] = int.TryParse(await Var("camoShown.length"), out var s_Cf) ? s_Cf : 0;
            }
            var s_BarFam = System.Array.FindIndex(s_Counts, n => n > 4);
            var s_FitFam = System.Array.FindIndex(s_Counts, n => n >= 1 && n <= 4);
            if (s_BarFam < 0 || s_FitFam < 0) throw new Exception($"no family with a scrollbar and one that fits on CONJUNTO (cells per family {string.Join(",", s_Counts)})");
            m_RuffleHost.Dispatch(Top() + $"CamoCat_0{s_BarFam + 1}", "buttonReleased");
            for (var i = 0; i < 10 && await Var("camoFamily") != s_BarFam.ToString(); ++i) await Task.Delay(100);
            await Task.Delay(300);
            if (await Probe("CamoGrid.m_usingScrollbar") != "true") throw new Exception($"family {s_BarFam} ({s_Counts[s_BarFam]} cells) shows no scrollbar");
            m_RuffleHost.Dispatch(Top() + "CamoGrid.ScrollBar", "setPercentage", 100);
            await Task.Delay(300);
            var s_Scrolled = double.TryParse(await Probe("CamoGrid.gridClips._y"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s_Sy) ? s_Sy : 0;
            if (s_Scrolled >= 0) throw new Exception($"the scrollbar at 100% did not move the cells (gridClips._y {s_Scrolled}): the test could not fail");
            m_RuffleHost.Dispatch(Top() + $"CamoCat_0{s_FitFam + 1}", "buttonReleased");
            for (var i = 0; i < 10 && await Var("camoFamily") != s_FitFam.ToString(); ++i) await Task.Delay(100);
            await Task.Delay(300);
            var s_FitY = await Probe("CamoGrid.gridClips._y"); var s_FitBar = await Probe("CamoGrid.m_usingScrollbar");
            var s_ShotScroll = Path.Combine(p_OutDir, "selftest_ruffle_skin_scroll.png");
            await m_RuffleHost.CaptureAsync(s_ShotScroll);
            if (s_FitY != "0" || s_FitBar == "true") throw new Exception($"after scrolling family {s_BarFam} to the bottom, family {s_FitFam} ({s_Counts[s_FitFam]} cells, no bar) sits at gridClips._y {s_FitY} (bar {s_FitBar}); expected 0 -- its cells are above the mask, the grid reads empty");
            m_RuffleHost.Dispatch(Top() + $"CamoCat_0{s_BarFam + 1}", "buttonReleased");
            for (var i = 0; i < 10 && await Var("camoFamily") != s_BarFam.ToString(); ++i) await Task.Delay(100);
            await Task.Delay(300);
            var s_BackY = await Probe("CamoGrid.gridClips._y");
            if (s_BackY != "0") throw new Exception($"back on family {s_BarFam} it opens scrolled (gridClips._y {s_BackY}); expected the top");
            var s_ScrollSaid = $"scroll: family {s_BarFam} ({s_Counts[s_BarFam]} cells) at 100% (cells at {s_Scrolled}) → family {s_FitFam} ({s_Counts[s_FitFam]}) at 0, no bar ({Path.GetFileName(s_ShotScroll)}) → back at the top";
            m_RuffleHost.Dispatch(Top() + "CamoCat_01", "buttonReleased");
            for (var i = 0; i < 10 && await Var("camoFamily") != "0"; ++i) await Task.Delay(100);
            // TORSO: the skin listed and marked, the window on its family (SNOW, the 6th)
            m_RuffleHost.Dispatch(Top() + "SkinTab_03", "buttonReleased");
            for (var i = 0; i < 10 && await Var("camoTab") != "2"; ++i) await Task.Delay(100);
            var s_TorsoItems = await Var("camoItems.length"); var s_TorsoSkin = await Var($"camoItems.{s_SkinAt}.DefaultSelected");
            var s_TorsoFamily = await Var("camoFamily"); var s_TorsoSel = await Probe("SkinTab_03.selected"); var s_AllSel = await Probe("SkinTab_01.selected");
            var s_TorsoCursor = await Probe("CamoGrid._camoCursor");
            if (s_TorsoCursor != "2") throw new Exception($"after the TORSO tab the cursor is on button {s_TorsoCursor}; expected the tab (2)");
            if (await Var("camoTab") != "2" || s_TorsoItems != (s_Stock + 1).ToString() || s_TorsoSkin != "true" || s_TorsoFamily != "5" || s_TorsoSel != "true" || s_AllSel == "true")
                throw new Exception($"TORSO: tab {await Var("camoTab")}, {s_TorsoItems} item(s), the skin marked {s_TorsoSkin}, family {s_TorsoFamily} (tab buttons: TORSO {s_TorsoSel}, CONJUNTO {s_AllSel}); expected tab 2, {s_Stock + 1}, true, 5 (SNOW), true, false");
            var s_TorsoImage = await Var($"camoItems.{s_SkinAt}.ItemImage"); var s_TorsoLabel = await Var($"camoItems.{s_SkinAt}.Label");
            var s_TorsoText = await Var($"camoItems.{s_SkinAt}.Description.Description");
            if (s_TorsoImage != c_SkinThumb || s_TorsoLabel != "Snow Test Skin" || s_TorsoText != "A skin of the seam.")
                throw new Exception($"TORSO: the skin's cell shows \"{s_TorsoImage}\", reads \"{s_TorsoLabel}\" (text \"{s_TorsoText}\"); its torso has no cell of its own -- expected the skin's ({c_SkinThumb}, \"Snow Test Skin\", \"A skin of the seam.\")");
            var s_ShotTorso = Path.Combine(p_OutDir, "selftest_ruffle_skin_torso.png");
            await m_RuffleHost.CaptureAsync(s_ShotTorso);
            // CABEZA: the skin has no head -- not listed
            m_RuffleHost.Dispatch(Top() + "SkinTab_02", "buttonReleased");
            for (var i = 0; i < 10 && await Var("camoTab") != "1"; ++i) await Task.Delay(100);
            var s_HeadItems = await Var("camoItems.length");
            if (s_HeadItems != s_Stock.ToString()) throw new Exception($"CABEZA lists {s_HeadItems} item(s); the skin has no head: expected the {s_Stock} of the game's");
            // PIERNAS: a pick of the first cell -- "SKNlegs,<its id>,<soldier>", Released → SkinPicked → the glitch, the window stays, marked
            m_RuffleHost.Dispatch(Top() + "SkinTab_04", "buttonReleased");
            for (var i = 0; i < 10 && await Var("camoTab") != "3"; ++i) await Task.Delay(100);
            // ⭐ the legs are another thing than the torso (keku 2026-09-28): PIERNAS shows the legs' OWN cell -- its picture, filed under
            // URBAN (listed there, not under the skin's SNOW) -- and the window's family is put back for the pick below
            var s_LegsImage = await Var($"camoItems.{s_SkinAt}.ItemImage"); var s_LegsFam = await Var($"camoItems.{s_SkinAt}._camoFamily");
            var s_LegsLabel = await Var($"camoItems.{s_SkinAt}.Label"); var s_LegsInfo = await Var($"camoItems.{s_SkinAt}.Description.Label");
            var s_LegsText = await Var($"camoItems.{s_SkinAt}.Description.Description");
            if (s_LegsImage != c_LegsThumb || s_LegsFam != "URBAN" || s_LegsLabel != "Pantalón Rayas" || s_LegsInfo != "Pantalón Rayas" || s_LegsText != "Rayas en las piernas.")
                throw new Exception($"PIERNAS: the skin's cell shows \"{s_LegsImage}\" filed under \"{s_LegsFam}\", reads \"{s_LegsLabel}\" (INFO \"{s_LegsInfo}\": \"{s_LegsText}\"); expected the legs' own cell ({c_LegsThumb}, URBAN, \"Pantalón Rayas\", \"Rayas en las piernas.\")");            var s_LegsFamilyWas = await Var("camoFamily");
            async Task<bool> ListsSkin(int p_Family)
            {
                m_RuffleHost.Dispatch(Top() + $"CamoCat_0{p_Family + 1}", "buttonReleased");
                for (var i = 0; i < 10 && await Var("camoFamily") != p_Family.ToString(); ++i) await Task.Delay(100);
                var s_N = int.TryParse(await Var("camoShown.length"), out var s_Ln) ? s_Ln : 0;
                for (var c = 0; c < s_N; ++c) if (await Var($"camoShown.{c}") == s_SkinAt.ToString()) return true;
                return false;
            }
            var s_InUrban = await ListsSkin(6);
            var s_ShotLegs = Path.Combine(p_OutDir, "selftest_ruffle_skin_legs_urban.png");
            await m_RuffleHost.CaptureAsync(s_ShotLegs);
            var s_InSnow = await ListsSkin(5);
            if (!s_InUrban || s_InSnow) throw new Exception($"PIERNAS: the skin's legs cell listed under URBAN {s_InUrban}, under SNOW {s_InSnow}; expected URBAN only (its own family)");
            if (int.TryParse(s_LegsFamilyWas, out var s_Was)) await ListsSkin(s_Was);
            var s_Cell0Item = await Var("camoShown.0");
            var s_Cell0Id = Unsigned(await Var($"camoItems.{s_Cell0Item}.Index"));
            s_Mark = LogBox.Text.Length;
            m_RuffleHost.Dispatch(Timeline().TrimEnd('.'), "camoPick", 0);
            for (var i = 0; i < 40 && !LogBox.Text[s_Mark..].Contains("SkinPickGlitch: engine action"); ++i) await Task.Delay(50);
            var s_PickLog = LogBox.Text[s_Mark..];
            var s_PickSignal = (await ProbeClip("_global._camoLastPickSignal")).Trim('"');
            if (s_PickSignal != $"SKNlegs,{s_Cell0Id},{s_Want}") throw new Exception($"a pick on PIERNAS said \"{s_PickSignal}\"; expected SKNlegs,{s_Cell0Id},{s_Want}");
            // ⛔ keku's boot (2026-09-28, "en el maniquí funciona pero una vez ingame tengo el camo default"): the deploy sends the
            // appearance the client's PROFILE holds for the kit -- so a pick stores OUR row there the game's way, as the weapon windows
            // do: our row into the game's selected appearance (SkinRowSet), then the game's StoreSoldierAppearance (SkinRowStore)
            var s_OurRow = unchecked((int)RimeLib.Cmd.UiBuilder.DataKeys.FbHash("UI/Art/Persistence/Camo/U_CAMO_SKINS")).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var s_At = -1;
            foreach (var s_Step in new[] { "CamoGrid fired OnItemReleased", "SkinRowSet: set", "SkinPicked: screen output", "SkinRowStore: engine action", "SkinPickGlitch: engine action" })
            {
                var s_Next = s_PickLog.IndexOf(s_Step, System.Math.Max(0, s_At), StringComparison.Ordinal);
                if (s_Next < 0) throw new Exception($"a pick did not go Released → SkinRowSet → SkinPicked → SkinRowStore → SkinPickGlitch (missing \"{s_Step}\"): " + s_PickLog.Trim()[..System.Math.Min(900, s_PickLog.Trim().Length)]);
                s_At = s_Next;
            }
            var s_RowSetAt = s_PickLog.IndexOf("SkinRowSet: set", StringComparison.Ordinal);
            var s_RowSetLine = s_PickLog[s_RowSetAt..].Split('\n')[0].Trim();
            if (!s_RowSetLine.Contains("745180783") && !s_RowSetLine.Contains("CustSelectedAppearance")) throw new Exception("SkinRowSet did not write the game's selected appearance: " + s_RowSetLine);
            if (!s_RowSetLine.Contains($"\"{s_OurRow}\"")) throw new Exception($"SkinRowSet did not write our row ({s_OurRow}): " + s_RowSetLine);
            if (TopScreen() != "customizeskinscreen") throw new Exception("a pick closed the soldier window: " + string.Join(" < ", s_Host.Stack));
            var s_LegsMarked = await Var($"camoItems.{s_Cell0Item}.DefaultSelected");
            if (s_LegsMarked != "true") throw new Exception($"the pick on PIERNAS was not marked (item {s_Cell0Item} DefaultSelected {s_LegsMarked})");
            // CONJUNTO: a pick of the first cell sets the three parts -- TORSO then marks that look, not the skin
            m_RuffleHost.Dispatch(Top() + "SkinTab_01", "buttonReleased");
            for (var i = 0; i < 10 && await Var("camoTab") != "0"; ++i) await Task.Delay(100);
            var s_AllItem = await Var("camoShown.0");
            var s_AllId = Unsigned(await Var($"camoItems.{s_AllItem}.Index"));
            var s_AllBase = await Var($"camoItems.{s_AllItem}._camoBase") == "true";
            m_RuffleHost.Dispatch(Timeline().TrimEnd('.'), "camoPick", 0);
            await Task.Delay(300);
            var s_AllSignal = (await ProbeClip("_global._camoLastPickSignal")).Trim('"');
            var s_AllWant = $"SKNall,{(s_AllBase ? "0" : s_AllId)},{s_Want}";
            if (s_AllSignal != s_AllWant) throw new Exception($"a pick on CONJUNTO said \"{s_AllSignal}\"; expected {s_AllWant} (the cell {(s_AllBase ? "is" : "is not")} the game's own look)");
            m_RuffleHost.Dispatch(Top() + "SkinTab_03", "buttonReleased");
            for (var i = 0; i < 10 && await Var("camoTab") != "2"; ++i) await Task.Delay(100);
            var s_TorsoSkinAfter = await Var($"camoItems.{s_SkinAt}.DefaultSelected");
            var s_TorsoLookAfter = await Var($"camoItems.{s_AllItem}.DefaultSelected");
            if (s_TorsoSkinAfter == "true" || s_TorsoLookAfter != "true")
                throw new Exception($"after CONJUNTO the torso marks the skin {s_TorsoSkinAfter} and the picked look {s_TorsoLookAfter}; expected false / true");
            // Back (Esc): the window's Deactivate → the glitch (pop 1) → the KITS screen
            s_Mark = LogBox.Text.Length;
            if (!s_Host.FireBack()) throw new Exception("Back found no Deactivate output on the soldier window's StateNode");
            await WaitTop("customizesoldierscreen", "after Back on the soldier window");
            var s_BackLog = LogBox.Text[s_Mark..];
            if (!s_BackLog.Contains("pop") || !s_BackLog.Contains("SkinBackGlitch: engine action")) throw new Exception("Back on the soldier window did not pop through the glitch: " + s_BackLog.Trim()[..System.Math.Min(600, s_BackLog.Trim().Length)]);
            // every photo a different picture
            var s_Hashes = new[] { s_ShotAll, s_ShotTorso, s_ShotLegs }.Select(f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))).ToList();
            if (s_Hashes.Distinct().Count() != s_Hashes.Count) throw new Exception("two of the CONJUNTO, TORSO and PIERNAS (URBAN) photos are the same picture");
            var s_Signals = (await ProbeClip("_global._camoSignals")).Trim('"');
            R($"soldier window: APPEARANCE on the first kit row → OnChanged → SkinButton (the KITS screen shipped static, the game's ChangeAppearance never fired) → SkinEnterLoadout → customizeskinscreen; header \"{s_Heading}\" / \"{s_SubHeading}\", kit \"{s_Kit}\", {s_Back}, tabs {string.Join(" · ", s_Tabs)} (y {s_TabY}), families {s_Fam0}…{s_Fam7} (from y {s_Fam1Y}, the 5th at y {s_FamY}); drag threshold {s_SkinDragLeft}; soldier {s_Key}; CONJUNTO {s_Items} looks ({s_Stock} of the game's + the table's skin), none marked ({Path.GetFileName(s_ShotAll)}); TORSO: the skin marked, family SNOW, the skin's picture ({Path.GetFileName(s_ShotTorso)}); CABEZA {s_HeadItems} (no skin); PIERNAS: the legs' own cell \"{s_LegsLabel}\" ({s_LegsImage.Split('/')[^1]}) under URBAN, not SNOW ({Path.GetFileName(s_ShotLegs)}); {s_ScrollSaid}; PIERNAS pick → {s_PickSignal} → Released → SkinRowSet (our row {s_OurRow} as the game's selected appearance) → SkinPicked → SkinRowStore (the game's store) → SkinPickGlitch, marked; CONJUNTO pick → {s_AllSignal} → TORSO marks that look; Back → SkinBackGlitch → KITS ({s_Signals} signals)");
        }

        /// <summary>
        /// The pistol's camo window (keku 2026-10-06: "lo que queremos y es como lo hemos hecho con todos los camos es que se abra una
        /// ventana nueva", the pistols "con el mismo método de los accesorios"). The LOADOUT screen, shipped static, keeps its SIDEARM
        /// row's arrows and makes a click on the row's box a door: on the RELEASE (the accessory rows' law) the rows' script re-selects
        /// what the row shows (the game's own SelectorChanged chain stores it) and fires the row's Button1Released → our SidearmCategory
        /// ('1') → SidearmButton → the flow graph (pop 1) → the window. The window: the loadout's path and our CAMOUFLAGE word, the kit,
        /// BACK, the eight families, a grid on the SIDEARM row's own list that shows ONE cell -- the pistol worn as "Sin camuflaje" (the
        /// kit's other pistols are in the list and not shown) -- and tells the view "WVAS,<the pistol>". A pick runs the loadout's own
        /// chain (SetWeaponCategory '1' → SetSecondaryWeapon with the pistol's identifier → SetWeapon → the game's
        /// UpdateWeaponCustomization) and the glitch, the window stays; Back → the glitch → pop → the loadout, its SIDEARM row on the pick.
        /// </summary>
        async Task StepSidearmCamo(Action<string> R, string p_OutDir)
        {
            if (m_Doc.Name != "CustomizeCamoMenu") throw new Exception("the camo menu document is not loaded: " + m_Doc.Name);
            var s_Flow = m_Doc.Screens.FirstOrDefault(s => s.Partition == "ui/flow/graph/spawn/customizationgraph") ?? throw new Exception("the document does not edit the customization flow graph");
            if (s_Flow.RemovedConnections.Count != 0) throw new Exception("the document removes shipped wires of the flow graph LIVE -- a road that did not take in the game");
            var s_Loadout = m_Doc.Screens.FirstOrDefault(s => s.Partition == "ui/flow/screen/customizesoldiersubscreen") ?? throw new Exception("the document does not edit the LOADOUT screen");
            if (!(s_Loadout.Delivery != null ? string.Equals(s_Loadout.Delivery, "static", StringComparison.OrdinalIgnoreCase) : m_Doc.IsStatic)) throw new Exception("the LOADOUT screen's edits are not shipped static");
            if (s_Loadout.RemovedConnections.Count != 0) throw new Exception("the LOADOUT screen removes a wire of the game's: the SIDEARM row's Button1Released was free");
            if (!m_Doc.Screens.Any(s => s.Partition == "ui/flow/screen/customizesidearmscreen" && s.New)) throw new Exception("the document has no pistol window");
            var s_Word = m_Settings.Language switch { "us" => "CAMOUFLAGE", "es" => "CAMUFLAJE", _ => null };
            if (!await OpenScreen("ui/flow/screen/customizesoldiersubscreen")) throw new Exception("the LOADOUT screen did not open");
            if (m_Current == null || !m_Current.Movie!.FrameScripts().Contains("camorow")) throw new Exception("the LOADOUT movie shown does not carry the rows' frame script camorow");
            var s_Mark = LogBox.Text.Length;
            await StartPreviewForSeam(p_OutDir);
            var s_Host = m_UiHost ?? throw new Exception("no host for the preview");
            for (var i = 0; i < 600 && !LogBox.Text[s_Mark..].Contains("focus on customizesoldiersubscreen"); ++i) await Task.Delay(50);
            await Task.Delay(1000);
            string Top() => s_Host.Stack.Count == 0 ? "_level0.none.instance1." : "_level0." + s_Host.Stack[^1].Split(':')[0] + ".instance1.";
            string TopScreen() => s_Host.Stack.Count == 0 ? "" : s_Host.Stack[^1].Split(':')[1];
            async Task<string> Probe(string p) => (await ProbeClip(Top() + p)).Trim('"');
            string Timeline() => Top()[..^"instance1.".Length];
            async Task<string> Var(string p) => (await ProbeClip(Timeline() + p)).Trim('"');
            async Task WaitTop(string p_Screen, string p_When)
            {
                for (var i = 0; i < 200 && (TopScreen() != p_Screen || !LogBox.Text.Contains($"{s_Host.Stack[^1].Split(':')[0]} {p_Screen} loaded: initializeScreen")); ++i) await Task.Delay(50);
                if (TopScreen() != p_Screen) throw new Exception($"{p_When}: the screen on top is {TopScreen()}, expected {p_Screen} (stack: {string.Join(" < ", s_Host.Stack)})");
                await Task.Delay(1200);
            }
            static string Unsigned(string p_Id) => long.TryParse(p_Id, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? (v < 0 ? v + 4294967296L : v).ToString(System.Globalization.CultureInfo.InvariantCulture) : p_Id;
            static string Signed(string p_Id) => long.TryParse(p_Id, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? (v >= 2147483648L ? v - 4294967296L : v).ToString(System.Globalization.CultureInfo.InvariantCulture) : p_Id;
            async Task<System.Collections.Generic.List<string>> Signals()
            {
                var s_Out = new System.Collections.Generic.List<string>();
                var s_N = int.TryParse((await ProbeClip("_global._camoSignalArr.length")).Trim('"'), out var s_L) ? s_L : 0;
                for (var k = 0; k < s_N; ++k) s_Out.Add((await ProbeClip($"_global._camoSignalArr.{k}")).Trim('"'));
                return s_Out;
            }
            if (TopScreen() != "customizesoldiersubscreen") throw new Exception("the preview did not start on the LOADOUT screen: " + string.Join(" < ", s_Host.Stack));
            // the SIDEARM row: the pistol worn, its arrows kept (the row still steps through the kit's pistols), its box's release ours
            const string c_Slot = "KitSelector_02.mcContainer.Slot_0";
            for (var i = 0; i < 40 && (await Probe(c_Slot + ".m_id") is "" or "undefined"); ++i) await Task.Delay(100);
            var s_Worn = Unsigned(await Probe(c_Slot + ".m_id"));
            var s_RowName = await Probe(c_Slot + ".header.text");
            var s_RowCount = await Probe(c_Slot + ".m_data.length");
            var s_Arrows = await Probe(c_Slot + ".m_mcButtonLeft._visible");
            var s_Ours = await Probe(c_Slot + ".m_mcMouseArea.onRelease._camoWrap");
            if (s_Worn is "" or "undefined" or "0") throw new Exception($"the SIDEARM row holds no pistol (m_id \"{s_Worn}\")");
            if (!int.TryParse(s_RowCount, out var s_Pistols) || s_Pistols < 2) throw new Exception($"the SIDEARM row lists {s_RowCount} item(s); the kit offers several pistols -- the window's filter could not be told from a list of one");
            if (s_Arrows != "true") throw new Exception($"the SIDEARM row lost its arrows (left arrow visible {s_Arrows}): it keeps them, as the accessory rows do");
            if (s_Ours != "true") throw new Exception($"the SIDEARM row's box release is not the door (onRelease._camoWrap {s_Ours})");
            // the LOADOUT's own mailbox (2026-10-06): the camo table reaches the rows' script here too, or a pistol twin could never
            // be folded into its pistol -- its receipt is MRW<entries>,<new>,<delivered>,<camo row redone>,<rows folded again>
            var s_Mailed = (await Signals()).FirstOrDefault(x => x.StartsWith("MRW", StringComparison.Ordinal));
            if (s_Mailed == null) throw new Exception("the LOADOUT's mailbox did not answer (no MRW receipt from the rows' script): the table never reaches the SIDEARM row; last signals: " + string.Join(" | ", await Signals()));
            // the seam document's table (make_camomenu_doc SEAM_SIDEARM_TABLE): the bench twin U_SEC_TEST_U_M9 (319457744) as a camo OF the M9
            var s_TwinBase = await Var("camoTable.319457744.base"); var s_TwinName = await Var("camoTable.319457744.name");
            if (s_TwinBase != "287899934" || s_TwinName != "PRUEBA C") throw new Exception($"the LOADOUT's table does not know the M9's twin: base \"{s_TwinBase}\", name \"{s_TwinName}\" (receipt {s_Mailed})");
            R("the LOADOUT's mailbox answered: " + s_Mailed + ", twin 319457744 = \"" + s_TwinName + "\" of " + s_TwinBase);
            // ⛔ and the row ASKS for the table when its fold found none (keku's runs 349/350: the first entry's writer never fired in
            // the game): the store read comes back empty here too (the host does not answer that key), so the row must fire its
            // mailbox's OnItemReleased, wired into CamoTableSet — "MPF1" and the host following that wire
            var s_Asked = (await Signals()).Contains("MPF1");
            var s_Log0 = LogBox.Text;
            var s_FiredAt = s_Log0.IndexOf("CamoMail fired OnItemReleased", StringComparison.Ordinal);
            var s_SetAfter = s_FiredAt >= 0 ? s_Log0.IndexOf("CamoTableSet: set key", s_FiredAt, StringComparison.Ordinal) : -1;
            if (!s_Asked || s_FiredAt < 0 || s_SetAfter < 0)
                throw new Exception($"the LOADOUT's rows did not ask their mailbox for the table (MPF1 {s_Asked}, CamoMail fired OnItemReleased {s_FiredAt >= 0}, " +
                                    $"then CamoTableSet {s_SetAfter >= 0}); last signals: " + string.Join(" | ", await Signals()));
            R("the rows asked their mailbox: MPF1 -> CamoMail.OnItemReleased -> CamoTableSet");
            var s_ShotLoadout = Path.Combine(p_OutDir, "selftest_ruffle_sidearm_loadout.png");
            await m_RuffleHost!.CaptureAsync(s_ShotLoadout);
            // the door: the release on the row's box (the PRESS only focuses the row: KitViewSlot.mousePress)
            s_Mark = LogBox.Text.Length;
            m_RuffleHost.Dispatch(Top() + c_Slot + ".m_mcMouseArea", "onRelease");
            await WaitTop("customizesidearmscreen", "after a release on the SIDEARM row's box");
            var s_Open = LogBox.Text[s_Mark..];
            var s_At = -1;
            // (the row's re-select fires the widget's SELECTOR_CHANGED, Query 14, which the host logs by its event name SetIndex)
            foreach (var s_Step in new[] { "KitSelector_02 fired SetIndex", "SetSecondaryWeapon: set", "KitSelector_02 fired OnItemReleased",
                                           "SidearmCategory: set", "SidearmButton: screen output", "push ui/flow/screen/customizesidearmscreen" })
            {
                var s_Next = s_Open.IndexOf(s_Step, System.Math.Max(0, s_At), StringComparison.Ordinal);
                if (s_Next < 0) throw new Exception($"the SIDEARM row's door did not go re-select (the game's store chain) → Button1Released → SidearmCategory → SidearmButton → the window (missing or out of order: \"{s_Step}\"): " + s_Open.Trim()[..System.Math.Min(1200, s_Open.Trim().Length)]);
                s_At = s_Next;
            }
            var s_CategoryLine = s_Open[s_Open.IndexOf("SidearmCategory: set", StringComparison.Ordinal)..].Split('\n')[0].Trim();
            if (!s_CategoryLine.EndsWith("= \"1\"")) throw new Exception("the door's SidearmCategory did not write the SIDEARM category 1: " + s_CategoryLine);
            if (s_Open.Contains("customizeaccessoriesscreen")) throw new Exception("the SIDEARM row's door reached the accessories screen: " + s_Open.Trim()[..System.Math.Min(900, s_Open.Trim().Length)]);
            if (s_Host.Stack.Count != 1) throw new Exception("the pistol window did not replace the LOADOUT screen: " + string.Join(" < ", s_Host.Stack));
            var s_OpenSlot = (await ProbeClip("_global._camoOpenSlot")).Trim('"'); var s_OpenBase = (await ProbeClip("_global._camoOpenBase")).Trim('"');
            if (s_OpenSlot != "S" || s_OpenBase != s_Worn) throw new Exception($"the row told the window slot \"{s_OpenSlot}\", pistol \"{s_OpenBase}\"; expected S and {s_Worn}");
            // the window: header, kit, BACK, families; the grid on the row's list, ONE cell (the pistol worn, as "Sin camuflaje")
            var s_Heading = await Probe("PageHeader_01.heading.text"); var s_SubHeading = await Probe("PageHeader_01.subHeading.text");
            var s_Kit = await Probe("TextField_01.m_textField.txtDisplay.text"); var s_Back = await Probe("Button_02.currentLabel");
            var s_Fam0 = await Probe("CamoCat_01.currentLabel"); var s_Fam7 = await Probe("CamoCat_08.currentLabel");
            if (s_Heading == "" || s_Heading.StartsWith("ID_")) throw new Exception($"the pistol window's path reads \"{s_Heading}\"");
            if (s_Word != null ? s_SubHeading != s_Word : (s_SubHeading == "" || s_SubHeading.StartsWith("ID_"))) throw new Exception($"the pistol window's title reads \"{s_SubHeading}\"; expected {s_Word ?? "our word"} for {m_Settings.Language}");
            if (s_Kit == "" || s_Back == "" || s_Back.StartsWith("ID_")) throw new Exception($"the pistol window's kit reads \"{s_Kit}\", BACK \"{s_Back}\"");
            if (s_Fam0 != "MISC" || s_Fam7 != "WOODLAND") throw new Exception($"the families read {s_Fam0} … {s_Fam7}");
            for (var i = 0; i < 20 && await Var("camoItems.length") is "" or "0" or "undefined"; ++i) await Task.Delay(100);
            var s_Items = int.TryParse(await Var("camoItems.length"), out var s_It) ? s_It : 0;
            var s_Cells = int.TryParse(await Var("camoShown.length"), out var s_Ce) ? s_Ce : 0;
            var s_Base = Unsigned(await Var("camoBaseWanted"));
            var s_Cell0 = await Probe("CamoGrid.gridData.0.data.Label"); var s_Cell0Image = await Probe("CamoGrid.gridData.0.data.ImagePath");
            var s_Cell0Item = await Var("camoShown.0"); var s_Cell0Id = Unsigned(await Var($"camoItems.{s_Cell0Item}.Index"));
            if (s_Items != s_Pistols) throw new Exception($"the window's grid got {s_Items} item(s); the SIDEARM row lists {s_Pistols} -- it must bind the row's own list");
            if (s_Cells != 1 || s_Base != s_Worn || s_Cell0Id != s_Worn) throw new Exception($"the window shows {s_Cells} cell(s) about pistol {s_Base} (cell 0 = {s_Cell0Id}); expected ONE, the pistol worn {s_Worn} -- never the kit's other pistols");
            if (s_Cell0 == "" || s_Cell0.StartsWith("ID_") || !s_Cell0Image.EndsWith("WeaponAccessory/NoSelection")) throw new Exception($"the pistol's own cell reads \"{s_Cell0}\" with \"{s_Cell0Image}\"; expected the localised \"Sin camuflaje\" and the crossed-out picture");
            var s_Said = await Signals();
            if (!s_Said.Contains("WVAS," + s_Worn)) throw new Exception($"the window did not tell the view its pistol (WVAS,{s_Worn}); last signals: {string.Join(" | ", s_Said)}");
            var s_ShotWindow = Path.Combine(p_OutDir, "selftest_ruffle_sidearm_window.png");
            await m_RuffleHost.CaptureAsync(s_ShotWindow);
            // a pick (cell 0): the loadout's own chain for its SIDEARM row, then the game's update and the glitch; the window stays
            s_Mark = LogBox.Text.Length;
            m_RuffleHost.Dispatch(Timeline().TrimEnd('.'), "camoPick", 0);
            for (var i = 0; i < 60 && !LogBox.Text[s_Mark..].Contains("SidearmPickGlitch: engine action"); ++i) await Task.Delay(50);
            var s_PickLog = LogBox.Text[s_Mark..];
            s_At = -1;
            foreach (var s_Step in new[] { "CamoGrid fired OnItemReleased", "SetWeaponCategory: set", "SetSecondaryWeapon: set", "SetWeapon: screen output",
                                           "SidearmPickUpdate: engine action", "SidearmPickGlitch: engine action" })
            {
                var s_Next = s_PickLog.IndexOf(s_Step, System.Math.Max(0, s_At), StringComparison.Ordinal);
                if (s_Next < 0) throw new Exception($"a pick did not go Released → SetWeaponCategory → SetSecondaryWeapon → SetWeapon → SidearmPickUpdate → SidearmPickGlitch (missing or out of order: \"{s_Step}\"): " + s_PickLog.Trim()[..System.Math.Min(900, s_PickLog.Trim().Length)]);
                s_At = s_Next;
            }
            var s_SecondaryLine = s_PickLog[s_PickLog.IndexOf("SetSecondaryWeapon: set", StringComparison.Ordinal)..].Split('\n')[0].Trim();
            if (!s_SecondaryLine.EndsWith($"= \"{s_Worn}\"") && !s_SecondaryLine.EndsWith($"= \"{Signed(s_Worn)}\""))
                throw new Exception($"SetSecondaryWeapon did not write the pistol picked ({s_Worn}): " + s_SecondaryLine);
            var s_PickCategory = s_PickLog[s_PickLog.IndexOf("SetWeaponCategory: set", StringComparison.Ordinal)..].Split('\n')[0].Trim();
            if (!s_PickCategory.EndsWith("= \"1\"")) throw new Exception("the pick's SetWeaponCategory did not write the SIDEARM category 1: " + s_PickCategory);
            if (TopScreen() != "customizesidearmscreen" || s_Host.Stack.Count != 1) throw new Exception("a pick closed the pistol window: " + string.Join(" < ", s_Host.Stack));
            var s_PickSignal = (await ProbeClip("_global._camoLastPickSignal")).Trim('"');
            var s_Picked = (await ProbeClip("_global._camoPicked")).Trim('"'); var s_PickedSlot = (await ProbeClip("_global._camoPickedSlot")).Trim('"');
            if (s_PickSignal != "WVC" + s_Worn || s_Picked != s_Worn || s_PickedSlot != "S") throw new Exception($"the pick said \"{s_PickSignal}\" and left picked {s_Picked} on slot {s_PickedSlot}; expected WVC{s_Worn}, {s_Worn}, S");
            // Back (Esc): the window's Deactivate → the glitch (pop 1) → the LOADOUT screen, its SIDEARM row on the pick (applied once)
            s_Mark = LogBox.Text.Length;
            if (!s_Host.FireBack()) throw new Exception("Back found no Deactivate output on the pistol window's StateNode");
            await WaitTop("customizesoldiersubscreen", "after Back on the pistol window");
            var s_BackLog = LogBox.Text[s_Mark..];
            if (!s_BackLog.Contains("pop") || !s_BackLog.Contains("SidearmBackGlitch: engine action")) throw new Exception("Back on the pistol window did not pop through the glitch: " + s_BackLog.Trim()[..System.Math.Min(600, s_BackLog.Trim().Length)]);
            for (var i = 0; i < 30 && (await ProbeClip("_global._camoPicked")).Trim('"') == s_Worn; ++i) await Task.Delay(100);
            var s_RowAfter = Unsigned(await Probe(c_Slot + ".m_id")); var s_PickedAfter = (await ProbeClip("_global._camoPicked")).Trim('"');
            var s_ArrowsAfter = await Probe(c_Slot + ".m_mcButtonLeft._visible");
            if (s_RowAfter != s_Worn || s_PickedAfter == s_Worn || s_ArrowsAfter != "true")
                throw new Exception($"back on the LOADOUT screen the SIDEARM row holds {s_RowAfter} (the pick {s_Worn}), the pick mark is \"{s_PickedAfter}\" (consumed = not the pick), arrows {s_ArrowsAfter}; " +
                                    $"row patched {await Probe(c_Slot + "._camoPatched")}, armed {await Probe("KitSelector_02._camoArmed")}, slot data {await Probe(c_Slot + ".m_data.length")}; last signals: {string.Join(" | ", await Signals())}");
            var s_ShotBack = Path.Combine(p_OutDir, "selftest_ruffle_sidearm_back.png");
            await m_RuffleHost.CaptureAsync(s_ShotBack);
            var s_Hashes = new[] { s_ShotLoadout, s_ShotWindow }.Select(f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))).ToList();
            if (s_Hashes.Distinct().Count() != s_Hashes.Count) throw new Exception("the LOADOUT and pistol window photos are the same picture");
            R($"pistol window: the LOADOUT screen (static, camorow) -- SIDEARM row \"{s_RowName}\" ({s_Pistols} pistols, arrows kept, worn {s_Worn}; {Path.GetFileName(s_ShotLoadout)}); release on its box → re-select (SelectorChanged → SetSecondaryWeapon) → Button1Released → {s_CategoryLine} → SidearmButton → customizesidearmscreen; header \"{s_Heading}\" / \"{s_SubHeading}\", kit \"{s_Kit}\", {s_Back}, families {s_Fam0}…{s_Fam7}; grid {s_Items} item(s) of the row, {s_Cells} cell \"{s_Cell0}\" (the pistol worn), WVAS,{s_Worn} ({Path.GetFileName(s_ShotWindow)}); pick → {s_PickCategory} → {s_SecondaryLine} → SetWeapon → SidearmPickUpdate (2001688152 ['0','1']) → SidearmPickGlitch, {s_PickSignal}, window stays; Back → SidearmBackGlitch → LOADOUT, row on {s_RowAfter}, pick mark consumed ({Path.GetFileName(s_ShotBack)})");
        }

        /// <summary>
        /// The crossbow's camo window (keku 2026-10-06: *"la ballesta es detectado como gadget, el usuario solo puede abrir la ventana de
        /// camo en el gadget cuando SOLO aparezca la ballesta, o la ballesta con mira"*). The GADGET1 row of the LOADOUT screen gets the
        /// SIDEARM row's door, gated to the crossbow's two unlocks: on the preview's first gadget (the assault's medkit) a release on its
        /// box opens NOTHING and says "WVOG1,<id>" (the control: the same gesture that opens the window below); stepped with the row's
        /// own selection to the crossbow, the release opens customizegadget1screen -- the gadget row's list, ONE cell (the crossbow worn
        /// as "Sin camuflaje"), "WVAG1,<crossbow>" -- and a pick runs the loadout's chain for that row (SetWeaponCategory '2' →
        /// SetGadgetOne with the crossbow → SetWeapon → Gadget1PickUpdate → Gadget1PickGlitch); Back → Gadget1BackGlitch → the loadout.
        /// </summary>
        async Task StepGadgetCamo(Action<string> R, string p_OutDir)
        {
            if (m_Doc.Name != "CustomizeCamoMenu") throw new Exception("the camo menu document is not loaded: " + m_Doc.Name);
            if (!m_Doc.Screens.Any(s => s.Partition == "ui/flow/screen/customizegadget1screen" && s.New) || !m_Doc.Screens.Any(s => s.Partition == "ui/flow/screen/customizegadget2screen" && s.New))
                throw new Exception("the document has no crossbow windows (gadget 1 and 2)");
            var s_Crossbow = new[] { "120742943", "3181383740" };   // U_Crossbow_Scoped_Cobra / _RifleScope (EBX)
            if (!await OpenScreen("ui/flow/screen/customizesoldiersubscreen")) throw new Exception("the LOADOUT screen did not open");
            var s_Mark = LogBox.Text.Length;
            await StartPreviewForSeam(p_OutDir);
            var s_Host = m_UiHost ?? throw new Exception("no host for the preview");
            for (var i = 0; i < 600 && !LogBox.Text[s_Mark..].Contains("focus on customizesoldiersubscreen"); ++i) await Task.Delay(50);
            await Task.Delay(1000);
            string Top() => s_Host.Stack.Count == 0 ? "_level0.none.instance1." : "_level0." + s_Host.Stack[^1].Split(':')[0] + ".instance1.";
            string TopScreen() => s_Host.Stack.Count == 0 ? "" : s_Host.Stack[^1].Split(':')[1];
            async Task<string> Probe(string p) => (await ProbeClip(Top() + p)).Trim('"');
            string Timeline() => Top()[..^"instance1.".Length];
            async Task<string> Var(string p) => (await ProbeClip(Timeline() + p)).Trim('"');
            async Task WaitTop(string p_Screen, string p_When)
            {
                for (var i = 0; i < 200 && (TopScreen() != p_Screen || !LogBox.Text.Contains($"{s_Host.Stack[^1].Split(':')[0]} {p_Screen} loaded: initializeScreen")); ++i) await Task.Delay(50);
                if (TopScreen() != p_Screen) throw new Exception($"{p_When}: the screen on top is {TopScreen()}, expected {p_Screen} (stack: {string.Join(" < ", s_Host.Stack)})");
                await Task.Delay(1200);
            }
            static string Unsigned(string p_Id) => long.TryParse(p_Id, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? (v < 0 ? v + 4294967296L : v).ToString(System.Globalization.CultureInfo.InvariantCulture) : p_Id;
            async Task<System.Collections.Generic.List<string>> Signals()
            {
                var s_Out = new System.Collections.Generic.List<string>();
                var s_N = int.TryParse((await ProbeClip("_global._camoSignalArr.length")).Trim('"'), out var s_L) ? s_L : 0;
                for (var k = 0; k < s_N; ++k) s_Out.Add((await ProbeClip($"_global._camoSignalArr.{k}")).Trim('"'));
                return s_Out;
            }
            if (TopScreen() != "customizesoldiersubscreen") throw new Exception("the preview did not start on the LOADOUT screen: " + string.Join(" < ", s_Host.Stack));
            const string c_Slot = "KitSelector_03.mcContainer.Slot_0";
            for (var i = 0; i < 40 && (await Probe(c_Slot + ".m_id") is "" or "undefined"); ++i) await Task.Delay(100);
            var s_First = Unsigned(await Probe(c_Slot + ".m_id")); var s_FirstName = await Probe(c_Slot + ".header.text");
            var s_Count = int.TryParse(await Probe(c_Slot + ".m_data.length"), out var s_C) ? s_C : 0;
            if (s_Crossbow.Contains(s_First)) throw new Exception($"the preview's GADGET1 row already shows the crossbow ({s_First}): the control below could not fail");
            var s_At = -1;
            for (var k = 0; k < s_Count && s_At < 0; ++k) if (s_Crossbow.Contains(Unsigned(await Probe($"{c_Slot}.m_data.{k}.Index")))) s_At = k;
            if (s_At < 0) throw new Exception($"the GADGET1 row lists no crossbow among its {s_Count} item(s)");
            // the CONTROL: a release on the medkit's box opens nothing (the same gesture that opens the window below)
            s_Mark = LogBox.Text.Length;
            m_RuffleHost!.Dispatch(Top() + c_Slot + ".m_mcMouseArea", "onRelease");
            await Task.Delay(2500);
            var s_Denied = LogBox.Text[s_Mark..];
            var s_Said = await Signals();
            if (TopScreen() != "customizesoldiersubscreen" || s_Denied.Contains("Gadget1Button") || s_Denied.Contains("Gadget1Category"))
                throw new Exception($"a release on \"{s_FirstName}\" ({s_First}) on the GADGET1 row opened a door: " + s_Denied.Trim()[..System.Math.Min(700, s_Denied.Trim().Length)]);
            if (!s_Said.Contains("WVOG1," + s_First)) throw new Exception($"the GADGET1 row's refusal did not say WVOG1,{s_First}; last signals: {string.Join(" | ", s_Said)}");
            var s_ShotDenied = Path.Combine(p_OutDir, "selftest_ruffle_gadget_denied.png");
            await m_RuffleHost.CaptureAsync(s_ShotDenied);
            // the row's own selection to the crossbow (what an arrow does), then the same release
            m_RuffleHost.Dispatch(Top() + c_Slot, "select", s_At, true);
            for (var i = 0; i < 30 && !s_Crossbow.Contains(Unsigned(await Probe(c_Slot + ".m_id"))); ++i) await Task.Delay(100);
            var s_Worn = Unsigned(await Probe(c_Slot + ".m_id")); var s_WornName = await Probe(c_Slot + ".header.text");
            if (!s_Crossbow.Contains(s_Worn)) throw new Exception($"the GADGET1 row did not step to the crossbow (item {s_At}): it shows {s_Worn}");
            await Task.Delay(800);
            s_Mark = LogBox.Text.Length;
            m_RuffleHost.Dispatch(Top() + c_Slot + ".m_mcMouseArea", "onRelease");
            await WaitTop("customizegadget1screen", "after a release on the crossbow's box");
            var s_Open = LogBox.Text[s_Mark..];
            foreach (var s_Step in new[] { "KitSelector_03 fired OnItemReleased", "Gadget1Category: set", "Gadget1Button: screen output", "push ui/flow/screen/customizegadget1screen" })
                if (!s_Open.Contains(s_Step)) throw new Exception($"the crossbow's door did not open its window (missing \"{s_Step}\"): " + s_Open.Trim()[..System.Math.Min(900, s_Open.Trim().Length)]);
            var s_Category = s_Open[s_Open.IndexOf("Gadget1Category: set", StringComparison.Ordinal)..].Split('\n')[0].Trim();
            if (!s_Category.EndsWith("= \"2\"")) throw new Exception("the door did not write the GADGET1 category 2: " + s_Category);
            for (var i = 0; i < 20 && await Var("camoItems.length") is "" or "0" or "undefined"; ++i) await Task.Delay(100);
            var s_Items = int.TryParse(await Var("camoItems.length"), out var s_It) ? s_It : 0;
            var s_Cells = int.TryParse(await Var("camoShown.length"), out var s_Ce) ? s_Ce : 0;
            var s_Cell0 = await Probe("CamoGrid.gridData.0.data.Label"); var s_SubHeading = await Probe("PageHeader_01.subHeading.text");
            var s_Cell0Id = Unsigned(await Var($"camoItems.{await Var("camoShown.0")}.Index"));
            if (s_Items != s_Count || s_Cells != 1 || s_Cell0Id != s_Worn) throw new Exception($"the crossbow window: {s_Items} item(s) of the row's {s_Count}, {s_Cells} cell(s), cell 0 = {s_Cell0Id}; expected the row's list and ONE cell, the crossbow {s_Worn}");
            s_Said = await Signals();
            if (!s_Said.Contains("WVAG1," + s_Worn)) throw new Exception($"the crossbow window did not tell the view its weapon (WVAG1,{s_Worn}); last signals: {string.Join(" | ", s_Said)}");
            var s_ShotWindow = Path.Combine(p_OutDir, "selftest_ruffle_gadget_window.png");
            await m_RuffleHost.CaptureAsync(s_ShotWindow);
            // a pick: the loadout's chain for the GADGET1 row
            s_Mark = LogBox.Text.Length;
            m_RuffleHost.Dispatch(Timeline().TrimEnd('.'), "camoPick", 0);
            for (var i = 0; i < 60 && !LogBox.Text[s_Mark..].Contains("Gadget1PickGlitch: engine action"); ++i) await Task.Delay(50);
            var s_PickLog = LogBox.Text[s_Mark..];
            var s_Pos = -1;
            foreach (var s_Step in new[] { "CamoGrid fired OnItemReleased", "SetWeaponCategory: set", "SetGadgetOne: set", "SetWeapon: screen output", "Gadget1PickUpdate: engine action", "Gadget1PickGlitch: engine action" })
            {
                var s_Next = s_PickLog.IndexOf(s_Step, System.Math.Max(0, s_Pos), StringComparison.Ordinal);
                if (s_Next < 0) throw new Exception($"a pick in the crossbow window did not go Released → SetWeaponCategory → SetGadgetOne → SetWeapon → Gadget1PickUpdate → Gadget1PickGlitch (missing or out of order: \"{s_Step}\"): " + s_PickLog.Trim()[..System.Math.Min(900, s_PickLog.Trim().Length)]);
                s_Pos = s_Next;
            }
            var s_GadgetLine = s_PickLog[s_PickLog.IndexOf("SetGadgetOne: set", StringComparison.Ordinal)..].Split('\n')[0].Trim();
            var s_PickCategory = s_PickLog[s_PickLog.IndexOf("SetWeaponCategory: set", StringComparison.Ordinal)..].Split('\n')[0].Trim();
            if (!s_PickCategory.EndsWith("= \"2\"") || !(s_GadgetLine.Contains(s_Worn) || s_GadgetLine.Contains(unchecked((int)uint.Parse(s_Worn)).ToString())))
                throw new Exception($"the pick wrote {s_PickCategory} / {s_GadgetLine}; expected category 2 and the crossbow {s_Worn}");
            if (TopScreen() != "customizegadget1screen") throw new Exception("a pick closed the crossbow window: " + string.Join(" < ", s_Host.Stack));
            // Back: the glitch (pop 1) → the loadout, the GADGET1 row still on the crossbow
            s_Mark = LogBox.Text.Length;
            if (!s_Host.FireBack()) throw new Exception("Back found no Deactivate output on the crossbow window's StateNode");
            await WaitTop("customizesoldiersubscreen", "after Back on the crossbow window");
            var s_BackLog = LogBox.Text[s_Mark..];
            if (!s_BackLog.Contains("Gadget1BackGlitch: engine action")) throw new Exception("Back on the crossbow window did not go through its glitch: " + s_BackLog.Trim()[..System.Math.Min(600, s_BackLog.Trim().Length)]);
            var s_ShotBack = Path.Combine(p_OutDir, "selftest_ruffle_gadget_back.png");
            await m_RuffleHost.CaptureAsync(s_ShotBack);
            var s_Hashes = new[] { s_ShotDenied, s_ShotWindow, s_ShotBack }.Select(f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))).ToList();
            if (s_Hashes[0] == s_Hashes[1] || s_Hashes[1] == s_Hashes[2]) throw new Exception("the crossbow window's photo is the same picture as a LOADOUT one");
            R($"crossbow window: GADGET1 \"{s_FirstName}\" ({s_First}) → release → nothing opens, WVOG1 ({Path.GetFileName(s_ShotDenied)}); the row's own select → \"{s_WornName}\" ({s_Worn}) → release → {s_Category} → Gadget1Button → customizegadget1screen \"{s_SubHeading}\": {s_Items} item(s) of the row, ONE cell \"{s_Cell0}\", WVAG1,{s_Worn} ({Path.GetFileName(s_ShotWindow)}); pick → {s_PickCategory} → {s_GadgetLine} → SetWeapon → Gadget1PickUpdate → Gadget1PickGlitch; Back → Gadget1BackGlitch → LOADOUT ({Path.GetFileName(s_ShotBack)})");
        }

        /// <summary>
        /// The rows of our own on TIERRA (keku 2026-09-26: the vehicles without a customization of their own get a row -- their name,
        /// their MINIMAP icon, only CAMUFLAJE, on the right). The framework adds an asset of its own per vehicle at the end of the side's
        /// list; the game builds its row like any other (label = the asset's name SID, no class icon, six empty slots) and the rows'
        /// script dresses it: the class icon with its star, the bar, the slots and PERSONAL. go, CAMUFLAJE takes PERSONAL.'s place, the
        /// minimap icon (the kit movie's added clip) sits where the class icon was, on the vehicle's frame; the game's rows keep their
        /// look; and Enter on our row fires CAMUFLAJE (on PC it fires PERSONAL. of the focused row).
        /// ⭐ After his first test in the game (same day, "funciona todo correctamente"): the icon as big as the class icon, WHITE and
        /// still (it cycled its colours), and our row NARROW -- its header strip. The step runs the screen TWICE: first with the rows
        /// script's three looks put back as he tested them (the CONTROL: every new check must fail there -- rows at index x 111, no
        /// mask, the icon at 100 % cycling, the bar over 111 px rows), then with the document's (rows one under the other at their
        /// real heights, ours cut to the header strip, the icon white and still and bigger, the list scrolled by those heights). The
        /// generator lists the table's fifteen land rows after the game's classes: they overflow the list, and every kind of minimap
        /// icon (Jeep, Car, Boat, ATV, DirtBike, none) is in the photo.
        /// </summary>
        async Task StepVehicleOwnRow(Action<string> R, string p_OutDir)
        {
            if (m_Doc.Name != "CustomizeCamoMenu") throw new Exception("the camo menu document is not loaded: " + m_Doc.Name);
            var s_Kit = m_Doc.Movies.FirstOrDefault(m => m.Resource == "ui/assets/kitview") ?? throw new Exception("the document does not edit the kit movie");
            if (!s_Kit.Stage.Any(o => o.StartsWith("import:iconsIngame.swf:")) || !s_Kit.Stage.Any(o => o.StartsWith("add:28:vehMapIcon:")))
                throw new Exception("the kit movie's vehicle row does not get the minimap icon clip: " + string.Join(" | ", s_Kit.Stage));
            // the framework table's land rows (VehicleRowsData.lua: its order, one per name SID) and their minimap frames -- mc_iconsIngameBig
            // has one label per frame: Boat 50, Car 51, Jeep 52, Transport 134 (the AAV's TankLC), ATV 156, DirtBike 167 (measured offline)
            var c_Rows = new (string Sid, string Icon)[]
            {
                ("ID_P_VNAME_HUMVEE", "Jeep"), ("ID_P_VNAME_VODNIK", "Jeep"), ("ID_P_VNAME_GROWLER", "Car"), ("ID_P_VNAME_VDV", "Car"),
                ("ID_P_VNAME_AAV", "Transport"), ("ID_P_VNAME_RIB", "Boat"), ("ID_P_XP1_VNAME_DPV", "Car"), ("ID_P_XP1_VNAME_SKIDLOADER", "Car"),
                ("ID_P_VNAME_QUADBIKE", "ATV"), ("ID_P_VNAME_PHOENIX", "Jeep"), ("ID_P_VNAME_RHINO", "Jeep"), ("ID_P_VNAME_BARSUK", "Jeep"),
                ("ID_P_VNAME_ASRAD", "Jeep"), ("ID_P_VNAME_DIRTBIKE", "DirtBike"), ("ID_P_VNAME_VODAA", "Jeep"),
            };
            var c_Frames = new System.Collections.Generic.Dictionary<string, int> { ["Boat"] = 50, ["Car"] = 51, ["Jeep"] = 52, ["Transport"] = 134, ["ATV"] = 156, ["DirtBike"] = 167 };
            const int c_RowH = 111;   // the game's row: KitView_2 is 580 x 109 (measured offline) and the list adds 2 px under it
            // the land screen's rows script and the three looks the document gives it
            var s_Land = m_Doc.Screens.FirstOrDefault(s => s.Partition == "ui/flow/screen/customizelandscreen") ?? throw new Exception("the document does not edit customizelandscreen");
            var s_OpAt = s_Land.Stage.FindIndex(o => o.StartsWith("script:camovehiclerow:"));
            if (s_OpAt < 0) throw new Exception("customizelandscreen carries no camovehiclerow script op");
            var s_Op = s_Land.Stage[s_OpAt];
            string Var(string p_Name) { var s_M = System.Text.RegularExpressions.Regex.Match(s_Op, "[:|]" + p_Name + "=([^|:]*)"); return s_M.Success ? s_M.Groups[1].Value : ""; }
            double NumOf(string p_Text) => double.TryParse(p_Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : -1;
            var s_CutText = Var("_vehOwnHeight"); var s_HighText = Var("_vehIconHeight"); var s_WhiteText = Var("_vehIconWhite");
            var s_Cut = (int)NumOf(s_CutText); var s_High = NumOf(s_HighText); var s_White = (int)NumOf(s_WhiteText);
            if (s_Cut <= 0 || s_Cut >= c_RowH || s_High <= 0 || s_White <= 0)
                throw new Exception($"the document's rows script does not cut our rows, size or whiten their icon: _vehOwnHeight {s_CutText}, _vehIconHeight {s_HighText}, _vehIconWhite {s_WhiteText}");
            // each row's white glyph height, as the document hands it to the script ("sid~frame~glyph"): the scale each icon must get
            var s_Glyph = Var("_vehOwnRows").Split(';').Select(e => e.Split('~')).Where(b => b.Length >= 3).ToDictionary(b => b[0], b => NumOf(b[2]));
            foreach (var (s_Sid, s_Icon) in c_Rows)
                if (!s_Glyph.TryGetValue(s_Sid, out var s_G) || s_G <= 0) throw new Exception($"the document hands the rows script no glyph height for {s_Sid} ({s_Icon})");
            // the control: the same op with the looks keku tested in the game
            var s_Control = s_Op.Replace($"_vehOwnHeight={s_CutText}", "_vehOwnHeight=0").Replace($"_vehIconHeight={s_HighText}", "_vehIconHeight=0").Replace($"_vehIconWhite={s_WhiteText}", "_vehIconWhite=0");
            if (s_Control == s_Op) throw new Exception("the control's op is the document's");
            var s_Shots = new System.Collections.Generic.List<string>();
            DataGenerator.ExtraVehicleRows.Clear();
            foreach (var (s_Sid, _) in c_Rows) DataGenerator.ExtraVehicleRows.Add((s_Sid, false));
            try
            {
                s_Land.Stage[s_OpAt] = s_Control;
                var s_A = await OwnRowPass(true);
                s_Land.Stage[s_OpAt] = s_Op;
                var s_B = await OwnRowPass(false);
                // every photo a different picture (a seam that photographs the same thing twice proves nothing)
                var s_Hashes = s_Shots.Select(f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))).ToList();
                if (s_Hashes.Distinct().Count() != s_Hashes.Count) throw new Exception("two of the step's photos are the same picture: " + string.Join(", ", s_Shots.Select(Path.GetFileName)));
                R($"vehicle own row: CONTROL (the looks tested in the game, 0/0/0) {s_A} || DOCUMENT ({s_Cut} px rows, icon glyphs {s_HighText} px tall, inner frame {s_White}) {s_B}");
            }
            finally
            {
                s_Land.Stage[s_OpAt] = s_Op;
                DataGenerator.ExtraVehicleRows.Clear();
            }

            async Task<string> OwnRowPass(bool p_Control)
            {
                var s_Pass = p_Control ? "control" : "document";
                if (!await OpenScreen("ui/flow/screen/customizelandscreen")) throw new Exception("the customizelandscreen screen did not open");
                ApplyDocumentToCurrent();   // the stage ops again: OpenScreen keeps a screen already open as it is
                if (m_Current == null || !m_Current.Movie!.FrameScripts().Contains("camovehiclerow")) throw new Exception("the customizelandscreen movie shown does not carry the frame script camovehiclerow");
                var s_Mark = LogBox.Text.Length;
                await StartPreviewForSeam(p_OutDir);
                var s_Host = m_UiHost ?? throw new Exception("no host for the vehicle preview");
                for (var i = 0; i < 600 && !LogBox.Text[s_Mark..].Contains("focus on customizelandscreen: KitView_01 "); ++i) await Task.Delay(50);
                await Task.Delay(1000);
                string Top() => s_Host.Stack.Count == 0 ? "_level0.none.instance1." : "_level0." + s_Host.Stack[^1].Split(':')[0] + ".instance1.";
                string TopScreen() => s_Host.Stack.Count == 0 ? "" : s_Host.Stack[^1].Split(':')[1];
                async Task<string> Probe(string p) => (await ProbeClip(Top() + p)).Trim('"');
                async Task<double> Num(string p) => double.TryParse(await Probe(p), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
                async Task WaitTop(string p_Screen, string p_When)
                {
                    for (var i = 0; i < 200 && (TopScreen() != p_Screen || !LogBox.Text.Contains($"{s_Host.Stack[^1].Split(':')[0]} {p_Screen} loaded: initializeScreen")); ++i) await Task.Delay(50);
                    if (TopScreen() != p_Screen) throw new Exception($"{p_When}: the screen on top is {TopScreen()}, expected {p_Screen} (stack: {string.Join(" < ", s_Host.Stack)})");
                    await Task.Delay(1200);
                }
                if (TopScreen() != "customizelandscreen") throw new Exception($"{s_Pass}: the preview did not start on customizelandscreen: " + string.Join(" < ", s_Host.Stack));
                if (!LogBox.Text[s_Mark..].Contains("generated UICustomizationComp.LandVehiclesData")) throw new Exception($"{s_Pass}: the generator did not build the land rows");
                // the looks this pass runs with, read back from the script
                var s_Timeline = Top()[..^"instance1.".Length];
                var s_Looks = $"{(await ProbeClip(s_Timeline + "vehOwnHeight")).Trim('"')}/{(await ProbeClip(s_Timeline + "vehIconHeight")).Trim('"')}/{(await ProbeClip(s_Timeline + "vehIconWhite")).Trim('"')}";
                var s_WantLooks = p_Control ? "0/0/0" : $"{s_CutText}/{s_HighText}/{s_WhiteText}";
                if (s_Looks != s_WantLooks) throw new Exception($"{s_Pass}: the rows script runs with the looks {s_Looks}, expected {s_WantLooks}");
                var s_RowCount = await Num("KitView_01.m_slots.length");
                var s_Rows = double.IsNaN(s_RowCount) ? 0 : (int)s_RowCount;
                var s_Game = s_Rows - c_Rows.Length;
                if (s_Game < 1) throw new Exception($"{s_Pass}: TIERRA lists {s_Rows} row(s); expected the game's classes and our {c_Rows.Length} after them");
                string Slot(int r) => $"KitView_01.mcContainer.Slot_{r}.";
                // ⛔ the list measures a row's height from its bounds when it attaches it: a clip of ours outside the row makes every row
                // that tall (2026-09-26: an icon parked at -4000 did, and only the photo showed it) -- the game's height, exactly
                var s_SlotH = await Num("KitView_01.m_slotHeight");
                if (s_SlotH != c_RowH) throw new Exception($"{s_Pass}: the list measured its rows {s_SlotH} px tall; the game's are {c_RowH}");
                // where each row is: the game's layout (index x 111) or ours (one under the other: ours cut + 2, the game's 111)
                var s_Ys = new System.Collections.Generic.List<double>();
                for (var r = 0; r < s_Rows; ++r) s_Ys.Add(await Num(Slot(r) + "_y"));
                var s_Uniform = Enumerable.Range(0, s_Rows).Select(r => (double)(r * c_RowH)).ToList();
                var s_Narrow = new System.Collections.Generic.List<double>();
                var s_Y = 0;
                for (var r = 0; r < s_Rows; ++r) { s_Narrow.Add(s_Y); s_Y += r >= s_Game ? s_Cut + 2 : c_RowH; }
                var s_Total = s_Y - 2;   // the rows' height with ours cut
                var s_AtUniform = s_Ys.SequenceEqual(s_Uniform); var s_AtNarrow = s_Ys.SequenceEqual(s_Narrow);
                if (p_Control ? (!s_AtUniform || s_AtNarrow) : !s_AtNarrow)
                    throw new Exception($"{s_Pass}: rows at y {string.Join(", ", s_Ys)}; expected {(p_Control ? "the game's" : "ours")} {string.Join(", ", p_Control ? s_Uniform : s_Narrow)}");
                // each row: the mask and the mouse area (ours cut, the game's whole: as tall as the first row's, a row of the game's), and
                // on ours the icon -- its frame, its scale, and the inner picture's frame
                var s_MouseH = await Num(Slot(0) + "mouseArea._height");
                if (!(s_MouseH > s_Cut)) throw new Exception($"{s_Pass}: the game's first row has a mouse area {s_MouseH} px tall");
                for (var r = 0; r < s_Rows; ++r)
                {
                    var s_Own = r >= s_Game;
                    var s_OwnSaid = await Probe(Slot(r) + "_vehOwn");
                    var s_CutSaid = await Probe(Slot(r) + "vehRowCut._name");   // (a clip's own toString echoes "[object Object]" here)
                    var s_CutH = await Num(Slot(r) + "vehRowCut._height");
                    var s_Mouse = await Num(Slot(r) + "mouseArea._height");
                    var s_HasCut = s_CutSaid.Contains("vehRowCut");
                    var s_WantCut = s_Own && !p_Control;
                    if (s_OwnSaid != (s_Own ? "true" : "false") || s_HasCut != s_WantCut || (s_WantCut && s_CutH != s_Cut) || s_Mouse != (s_WantCut ? s_Cut : s_MouseH))
                        throw new Exception($"{s_Pass}: row {r} ({(s_Own ? "ours" : "the game's")}) is _vehOwn {s_OwnSaid}, mask \"{s_CutSaid}\" {s_CutH} px, mouse area {s_Mouse} px; expected {(s_WantCut ? $"a {s_Cut} px mask and mouse area" : $"no mask and a {s_MouseH} px mouse area")}");
                    if (!s_Own) continue;
                    var (s_Sid, s_Icon) = c_Rows[r - s_Game];
                    var s_Seen = await Probe(Slot(r) + "vehMapIcon._visible");
                    if (s_Icon == "")
                    {
                        if (s_Seen != "false") throw new Exception($"{s_Pass}: our row {r} ({s_Sid}) has no icon in the table and shows one ({s_Seen})");
                        continue;
                    }
                    var s_Frame = await Num(Slot(r) + "vehMapIcon._currentframe"); var s_XScale = await Num(Slot(r) + "vehMapIcon._xscale");
                    var s_Inner = await Probe(Slot(r) + "vehMapIcon.icon._currentframe");
                    var s_WantScale = p_Control ? 100 : 100 * s_High / s_Glyph[s_Sid];   // every glyph drawn _vehIconHeight tall
                    if (s_Seen != "true" || s_Frame != c_Frames[s_Icon] || System.Math.Abs(s_XScale - s_WantScale) > 0.5 || (!p_Control && s_Inner != s_White.ToString()))
                        throw new Exception($"{s_Pass}: our row {r} ({s_Sid}) shows its icon {s_Seen} on frame {s_Frame} at {s_XScale} %, its inner picture on frame {s_Inner}; expected {s_Icon} ({c_Frames[s_Icon]}) at {s_WantScale} %{(p_Control ? "" : $", the inner picture on {s_White} (white)")}");
                }
                // still or cycling: our first row's inner picture read ten times over a second
                var s_Blink = new System.Collections.Generic.List<string>();
                for (var i = 0; i < 10; ++i) { s_Blink.Add(await Probe(Slot(s_Game) + "vehMapIcon.icon._currentframe")); await Task.Delay(100); }
                var s_Kinds = s_Blink.Distinct().ToList();
                if (p_Control ? s_Kinds.Count < 2 : (s_Kinds.Count != 1 || s_Kinds[0] != s_White.ToString()))
                    throw new Exception($"{s_Pass}: our first row's icon showed its picture on frames {string.Join(",", s_Blink)} over a second; expected {(p_Control ? "its own animation (several)" : $"only {s_White}")}");
                var s_Shot = Path.Combine(p_OutDir, p_Control ? "selftest_ruffle_vehownrow_control.png" : "selftest_ruffle_vehownrow.png");
                await m_RuffleHost!.CaptureAsync(s_Shot);
                s_Shots.Add(s_Shot);
                // the scroll: the list's mask, the bar over the rows' height, and the container with the last row selected, then the first
                var s_View = await Num("KitView_01.m_mask._height");
                if (!(s_Total > s_View + 4)) throw new Exception($"{s_Pass}: the rows ({s_Total} px with ours cut) do not overflow the list ({s_View} px): the scroll is not checked -- add rows");
                m_RuffleHost.Dispatch(Top() + "KitView_01", "selectSlot", s_Rows - 1, true);
                await Task.Delay(600);
                var s_Bar = await Probe("KitView_01.ScrollBar._visible"); var s_Target = await Num("KitView_01.ScrollBar.m_targetSize");
                var s_Bottom = await Num("KitView_01.mcContainer._y");
                var s_Scrolled = "";
                if (!p_Control)
                {
                    s_Scrolled = Path.Combine(p_OutDir, "selftest_ruffle_vehownrow_scrolled.png");
                    await m_RuffleHost.CaptureAsync(s_Scrolled);
                    s_Shots.Add(s_Scrolled);
                }
                m_RuffleHost.Dispatch(Top() + "KitView_01", "selectSlot", 0, true);
                await Task.Delay(600);
                var s_TopY = await Num("KitView_01.mcContainer._y");
                var s_WantBottom = -(s_Total - s_View);
                var s_ScrollOurs = s_Bar == "true" && s_Target == s_Total && System.Math.Abs(s_Bottom - s_WantBottom) <= 1 && s_TopY == 0;
                if (p_Control ? s_ScrollOurs : !s_ScrollOurs)
                    throw new Exception($"{s_Pass}: the bar shows {s_Bar} over {s_Target} px, the rows at y {s_Bottom} with the last selected and {s_TopY} with the first; ours is a bar over {s_Total} px, {s_WantBottom} and 0 (the list's mask {s_View} px)");
                var s_Said = $"{s_Rows} rows ({s_Game} of the game's) at y {string.Join("/", s_Ys)}, bar over {s_Target} px, last row selected at {s_Bottom} (mask {s_View}), icon picture {string.Join(",", s_Blink)} ({Path.GetFileName(s_Shot)}{(s_Scrolled == "" ? "" : ", " + Path.GetFileName(s_Scrolled))})";
                if (p_Control) return s_Said;
                // the document's rows, as before: ours only CAMUFLAJE in PERSONAL.'s place (456), no class icon, bar or slots, the minimap icon
                // where the class icon sits; the game's row 0 unchanged
                async Task<System.Collections.Generic.Dictionary<string, string>> Read(int r)
                {
                    var s_Out = new System.Collections.Generic.Dictionary<string, string>();
                    foreach (var s_Field in new[] { "_vehOwn", "header.text", "button1._visible", "button1._x", "button2._visible", "button2._x", "button2.currentLabel",
                                                    "headerIcon._visible", "headerIcon._x", "headerIcon._y", "bar._visible", "slotLabel1._visible", "slot1._visible",
                                                    "vehMapIcon._visible", "vehMapIcon._x", "vehMapIcon._y", "vehMapIcon._width", "vehMapIcon._height" })
                        s_Out[s_Field] = await Probe(Slot(r) + s_Field);
                    return s_Out;
                }
                var o = await Read(s_Game);
                var g = await Read(0);
                string Say(System.Collections.Generic.Dictionary<string, string> d) => string.Join(", ", d.Select(kv => kv.Key + "=" + kv.Value));
                var s_Word = m_Settings.Language switch { "us" => "CAMOUFLAGE", "es" => "CAMUFLAJE", "fr" => "CAMOUFLAGE", "ge" => "TARNUNG", "it" => "MIMETICA", _ => null };
                if (o["_vehOwn"] != "true" || o["button1._visible"] != "false" || o["button2._visible"] != "true" || o["button2._x"] != "456" ||
                    (s_Word != null && o["button2.currentLabel"] != s_Word) || o["headerIcon._visible"] != "false" || o["bar._visible"] != "false" ||
                    o["slotLabel1._visible"] != "false" || o["slot1._visible"] != "false")
                    throw new Exception($"our row ({s_Game}) is not dressed as keku asked (only CAMUFLAJE at 456, no star/bar/slots): {Say(o)}");
                if (o["vehMapIcon._visible"] != "true" || o["vehMapIcon._x"] != o["headerIcon._x"] || o["vehMapIcon._y"] != o["headerIcon._y"])
                    throw new Exception($"our row's minimap icon is not where the class icon sits: {Say(o)}");
                if (string.IsNullOrEmpty(o["header.text"]) || o["header.text"].StartsWith("ID_")) throw new Exception($"our row's name reads \"{o["header.text"]}\"");
                if (g["_vehOwn"] != "false" || g["button1._visible"] != "true" || g["button1._x"] != "456" || g["button2._x"] != "336" ||
                    g["headerIcon._visible"] != "true" || g["vehMapIcon._visible"] != "false")
                    throw new Exception($"the game's row 0 lost its look next to ours: {Say(g)}");
                // the receipt: the rows script told the Lua which rows it dressed
                var s_Signal = (await ProbeClip("_global._camoLastSignal")).Trim('"');
                var s_Receipt = $"VORland,{c_Rows.Length}/{s_Rows}," + string.Join(";", c_Rows.Select(x => x.Sid + "=" + x.Icon));
                if (!LogBox.Text[s_Mark..].Contains(s_Receipt) && s_Signal != s_Receipt) throw new Exception($"the rows script did not report our rows: last signal \"{s_Signal}\", expected {s_Receipt}");
                // Enter on our first row: the concept the game hands the focused list (Activate, released) fires CAMUFLAJE's event, never PERSONAL.'s
                var s_Activate = (await ProbeClip("_global.fb.Input.InputConceptActions.Action_Activate")).Trim('"');
                var s_ActivateIndex = Array.IndexOf(UiHost.InputConceptNames, "Activate");
                if (s_Activate != s_ActivateIndex.ToString()) throw new Exception($"the game's Action_Activate is {s_Activate}, the host hands {s_ActivateIndex}");
                m_RuffleHost.Dispatch(Top() + "KitView_01", "selectSlot", s_Game, true);
                await Task.Delay(400);
                s_Mark = LogBox.Text.Length;
                s_Host.FireInputConcept(s_ActivateIndex, false);
                await WaitTop("customizelandcamoscreen", "after Enter on our row");
                var s_Enter = LogBox.Text[s_Mark..];
                if (!s_Enter.Contains("KitView_01 fired OnChanged") || s_Enter.Contains("KitView_01 fired OnItemReleased") || s_Enter.Contains("GotoCustomize"))
                    throw new Exception("Enter on our row did not fire CAMUFLAJE alone: " + s_Enter.Trim()[..System.Math.Min(900, s_Enter.Trim().Length)]);
                // the window: the vehicle's name (the class the window files camos under is the row's SID), only "Sin camuflaje"
                var s_Name = await Probe("TextField_01.m_textField.txtDisplay.text");
                for (var i = 0; i < 5 && await Probe("CamoGrid.gridData.length") != "1"; ++i) await Task.Delay(100);
                var s_Cells = await Probe("CamoGrid.gridData.length");
                if (string.IsNullOrEmpty(s_Name) || s_Name.StartsWith("ID_") || s_Cells != "1")
                    throw new Exception($"the window opened from our row reads \"{s_Name}\" with {s_Cells} cell(s); expected the vehicle's name and only the no-camo cell");
                var s_WindowShot = Path.Combine(p_OutDir, "selftest_ruffle_vehownwindow.png");
                await m_RuffleHost.CaptureAsync(s_WindowShot);
                s_Shots.Add(s_WindowShot);
                if (!s_Host.FireBack()) throw new Exception("Back found no Deactivate output on the window");
                await WaitTop("customizelandscreen", "after Back on the window");
                return s_Said + $"; ours first \"{o["header.text"]}\" -- only {o["button2.currentLabel"]} at {o["button2._x"]}, icon at ({o["vehMapIcon._x"]},{o["vehMapIcon._y"]}) {o["vehMapIcon._width"]}x{o["vehMapIcon._height"]}; the game's row 0 \"{g["header.text"]}\" unchanged; receipt {s_Receipt}; Enter → CAMUFLAJE → window \"{s_Name}\" with {s_Cells} cell ({Path.GetFileName(s_WindowShot)}); Back → TIERRA";
            }
        }

        /// <summary>
        /// A build with the recorder riding in it (Build mod… ▸ Record), on the camo menu document, without a mount: the accessories
        /// movie carries the document's own frame script AND the recorder behind it, ext/Client ends with the receiver, ext/Server is the
        /// database side, mod.json says so; without Record none of that is there.
        /// </summary>
        Task StepRecordBuild(Action<string> R, string p_OutDir)
        {
            var s_DocFile = Path.Combine(AppContext.BaseDirectory, "Data", "CamoMenu.json");
            var s_Doc = Newtonsoft.Json.JsonConvert.DeserializeObject<ScreenDocument>(File.ReadAllText(s_DocFile)) ?? throw new Exception("Data/CamoMenu.json does not parse");
            var s_Screens = s_Doc.Screens.Where(s => !s.New).Select(s => (s.Movie ?? "ui/assets/" + s.Partition.Split('/').Last()).Split('/').Last()).ToList();
            var s_Log = new StringWriter();
            var s_Emitter = new ModEmitter(s_Doc, Path.GetDirectoryName(s_DocFile)!, Path.Combine(p_OutDir, "mods_record"), m_Rime.PartitionJson, m_Rime.ResourceBytes, s_Log, Path.Combine(p_OutDir, "build_record"), m_Rime.PartitionNameByGuid) { Record = DataRecorder.Parts(s_Doc.Name, s_Screens) };
            s_Emitter.Emit();
            Log(s_Log.ToString());
            var s_MovieFile = s_Emitter.Movies.FirstOrDefault(m => m.Resource.EndsWith("customizeaccessoriesscreen")).File ?? throw new Exception("the accessories movie was not written: " + string.Join(", ", s_Emitter.Movies.Select(m => m.Resource)));
            var s_Movie = GfxMovie.Load(s_MovieFile);
            if (!DataRecorder.IsInjected(s_Movie)) throw new Exception("the built accessories movie does not carry the recorder");
            if (!s_Movie.FrameScripts().Contains("camomenu")) throw new Exception("the built accessories movie lost the document's camo menu script: " + string.Join(",", s_Movie.FrameScripts()));
            // the recorder's DoAction comes after the document's script (it wraps the edited screen)
            var s_Actions = s_Movie.Tags.TakeWhile(t => t.Code != GfxMovie.TagShowFrame).Where(t => t.Code == 12).Select(t => System.Text.Encoding.Latin1.GetString(t.Body)).ToList();
            var s_ScriptAt = s_Actions.FindIndex(a => a.Contains("_rueScript_camomenu")); var s_RecorderAt = s_Actions.FindIndex(a => a.Contains(DataRecorder.Marker));
            if (s_ScriptAt < 0 || s_RecorderAt < 0 || s_RecorderAt < s_ScriptAt) throw new Exception($"frame actions out of order: camo script at {s_ScriptAt}, recorder at {s_RecorderAt} of {s_Actions.Count}");
            var s_Client = File.ReadAllText(Path.Combine(s_Emitter.ModDir, "ext", "Client", "__init__.lua"));
            var s_ServerFile = Path.Combine(s_Emitter.ModDir, "ext", "Server", "__init__.lua");
            if (!s_Client.Contains("[PROBE]") || !s_Client.Contains("the recorder's receiver") || !s_Client.Contains("uirecdump")) throw new Exception("ext/Client lacks the mod's own part or the recorder's receiver");
            if (!File.Exists(s_ServerFile) || !File.ReadAllText(s_ServerFile).Contains("uirec")) throw new Exception("ext/Server (the recorder's database side) is missing");
            var s_ModJson = JObject.Parse(File.ReadAllText(Path.Combine(s_Emitter.ModDir, "mod.json")));
            if (!((string?)s_ModJson["Description"] ?? "").Contains("recorder")) throw new Exception("mod.json does not say the recorder rides in the mod");
            // without Record: no recorder, no ext/Server (a previous recording build's is removed)
            var s_Plain = new ModEmitter(s_Doc, Path.GetDirectoryName(s_DocFile)!, Path.Combine(p_OutDir, "mods_record"), m_Rime.PartitionJson, m_Rime.ResourceBytes, s_Log, Path.Combine(p_OutDir, "build_record"), m_Rime.PartitionNameByGuid);
            s_Plain.Emit();
            var s_PlainMovie = GfxMovie.Load(s_Plain.Movies.First(m => m.Resource.EndsWith("customizeaccessoriesscreen")).File);
            if (DataRecorder.IsInjected(s_PlainMovie) || File.Exists(s_ServerFile) || File.ReadAllText(Path.Combine(s_Plain.ModDir, "ext", "Client", "__init__.lua")).Contains("uirecdump")) throw new Exception("a build without Record still carries the recorder");
            R($"record build: {s_Doc.Name} with Record — {Path.GetFileName(s_MovieFile)} carries the camo menu script then the recorder ({s_Actions.Count} frame actions), ext/Client = probe + receiver, ext/Server = the database side, mod.json says so; without Record none of it");
            return Task.CompletedTask;
        }

        /// <summary>The focused placement step: save the session, build a fresh editor, see it come back where this one is — twice, the second time maximised.</summary>
        async Task StepPlacement(Action<string> R)
        {
            // a place and size of the user's own, not the XAML's (the default came back by itself once and hid a size lost at Show)
            var s_Was = (Left, Top, Width, Height);
            var s_Area = Monitors.WorkingAreaOf(this);
            Left = s_Area.Left + 60; Top = s_Area.Top + 30; Width = System.Math.Min(1420, s_Area.Width - 60); Height = System.Math.Min(880, s_Area.Height - 30);
            await Task.Delay(300);
            PersistSession();
            var s_Dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            if (m_Settings.Window == null || System.Math.Abs(m_Settings.Window.Width - Width * s_Dpi.DpiScaleX) > 2 || System.Math.Abs(m_Settings.Window.Left - Left * s_Dpi.DpiScaleX) > 2) throw new Exception($"the session saved {m_Settings.Window}, this window is at ({Left * s_Dpi.DpiScaleX:0},{Top * s_Dpi.DpiScaleY:0}) {Width * s_Dpi.DpiScaleX:0}x{Height * s_Dpi.DpiScaleY:0} px");
            var s_Fresh = new MainWindow();
            var s_First = PlacementComesBack(s_Fresh);
            // the fresh window's WPF units agree with its pixels: what it shows with is what was restored, not the XAML's 1600x980
            if (System.Math.Abs(s_Fresh.Width - Width) > 1 || System.Math.Abs(s_Fresh.Height - Height) > 1 || System.Math.Abs(s_Fresh.Left - Left) > 1) throw new Exception($"the fresh editor's own size is {s_Fresh.Width}x{s_Fresh.Height} at ({s_Fresh.Left},{s_Fresh.Top}), expected {Width}x{Height} at ({Left},{Top})");
            // and shown, the way the user gets it (on this window's monitor: the seam's own), it is still there at that size
            s_Fresh.Show();
            await Task.Delay(400);
            var s_Shown = Monitors.PlacementOf(s_Fresh);
            if (s_Shown?.ToString() != m_Settings.Window.ToString()) throw new Exception($"shown, the fresh editor sits at {s_Shown}, the saved placement is {m_Settings.Window}");
            s_Fresh.Close();
            // maximised on this monitor: the normal rectangle and the state both ride with the session
            WindowState = WindowState.Maximized;
            await Task.Delay(300);
            PersistSession();
            if (m_Settings.Window?.Maximized != true) throw new Exception("the maximised state was not saved: " + m_Settings.Window);
            var s_Fresh2 = new MainWindow();
            var s_Second = PlacementComesBack(s_Fresh2);
            if (s_Fresh2.WindowState != WindowState.Maximized) throw new Exception("the fresh editor did not come back maximised");
            s_Fresh2.Show();
            await Task.Delay(400);
            var s_ShownMax = Monitors.Of(new System.Windows.Interop.WindowInteropHelper(s_Fresh2).Handle);
            if (s_Fresh2.WindowState != WindowState.Maximized || s_ShownMax != Monitors.Of(new System.Windows.Interop.WindowInteropHelper(this).Handle)) throw new Exception($"shown, the fresh editor is {s_Fresh2.WindowState} on the monitor at ({s_ShownMax?.Left},{s_ShownMax?.Top}); expected maximised on this window's");
            s_Fresh2.Close();
            WindowState = WindowState.Normal;
            (Left, Top, Width, Height) = s_Was;
            await Task.Delay(300);
            PersistSession();
            R($"placement: saved with the session and restored on a fresh editor — {s_First}; maximised: {s_Second}");
        }

        /// <summary>
        /// The walk's text size and picture steps, after the document is saved and built: what they drop must stay out of the shipped
        /// accessories movie (compared byte for byte with the one that ran in game), so they come last, on the accessories screen opened
        /// again, and only with the player opted in (both measure the live widget).
        /// </summary>
        async Task StepTextSizeAtTheEnd(Action<string> R, string p_OutDir)
        {
            if (Environment.GetEnvironmentVariable("RUE_SELFTEST_RUFFLE") != "1") { R("text size, image: skipped (RUE_SELFTEST_RUFFLE=1 measures the widgets in the player)"); return; }
            if (!await OpenScreen("ui/flow/screen/customizeaccessoriesscreen")) throw new Exception("the accessories screen did not open again for the text size step");
            await StepTextSize(R, p_OutDir);
            await StepImage(R, p_OutDir);
        }

        /// <summary>
        /// A PNG with alpha for the picture steps: 98x58 (not a multiple of 4: the texture pads), the left half opaque red, the right
        /// half half-clear blue, a 20x20 clear hole at (10,10). Written by WPF, whose encoder adds the colour-space chunks the build's
        /// decoder must leave alone.
        /// </summary>
        static string WriteTestPng(string p_Dir)
        {
            const int w = 98, h = 58;
            var s_Pixels = new byte[w * h * 4];
            for (var y = 0; y < h; ++y)
                for (var x = 0; x < w; ++x)
                {
                    var o = (y * w + x) * 4;
                    if (x >= 10 && x < 30 && y >= 10 && y < 30) continue;                       // the hole: clear
                    if (x < w / 2) { s_Pixels[o + 2] = 255; s_Pixels[o + 3] = 255; }            // red, opaque
                    else { s_Pixels[o] = 255; s_Pixels[o + 3] = 128; }                          // blue, half clear
                }
            var s_Bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, s_Pixels, w * 4);
            var s_Encoder = new PngBitmapEncoder();
            s_Encoder.Frames.Add(BitmapFrame.Create(s_Bitmap));
            var s_Path = Path.Combine(p_Dir, "test_image.png");
            using (var s_File = File.Create(s_Path)) s_Encoder.Save(s_File);
            return s_Path;
        }

        /// <summary>A pixel of a Bgra32 bitmap as (b, g, r, a).</summary>
        static (int B, int G, int R, int A) PixelAt(BitmapSource p_Bitmap, int x, int y)
        {
            var s_Buf = new byte[4];
            p_Bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), s_Buf, 4, 0);
            return (s_Buf[0], s_Buf[1], s_Buf[2], s_Buf[3]);
        }

        /// <summary>
        /// A picture of the user's, end to end but for the game: a PNG with alpha → Add image → the document's list and the widget
        /// (ImageManager with StaticUrl = its texture name, fitted), the stage drawing the picture at its size where it was dropped,
        /// the Properties panel, the live preview asking for the texture and getting the PNG (the clip holds it at its size), and the
        /// build's conversion: DXT5 padded to 100x60, one mip, the pixels back as put in (red, half-clear blue, a clear hole), the
        /// TextureAsset stub, the recipe lines, mod.json's second superbundle and the Lua that mounts it. The build's decoder is
        /// checked against WPF's on the same file first.
        /// </summary>
        async Task StepImage(Action<string> R, string p_OutDir)
        {
            var s_PngPath = WriteTestPng(p_OutDir);
            // the build's own PNG decoder against WPF's, byte for byte (the colour-space chunks WPF wrote must change nothing)
            var s_Mine = PngImage.Load(s_PngPath);
            BitmapSource s_Wpf;
            using (var s_Stream = File.OpenRead(s_PngPath))
                s_Wpf = new FormatConvertedBitmap(BitmapFrame.Create(s_Stream, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
            var s_WpfPixels = new byte[98 * 58 * 4];
            s_Wpf.CopyPixels(s_WpfPixels, 98 * 4, 0);
            if (s_Mine.Width != 98 || s_Mine.Height != 58 || !s_Mine.Bgra.SequenceEqual(s_WpfPixels))
            {
                var s_First = Enumerable.Range(0, System.Math.Min(s_Mine.Bgra.Length, s_WpfPixels.Length)).FirstOrDefault(i => s_Mine.Bgra[i] != s_WpfPixels[i], -1);
                throw new Exception($"the build's PNG decoder disagrees with WPF's on test_image.png ({s_Mine.Width}x{s_Mine.Height}; first different byte at {s_First})");
            }
            CentreTabs.SelectedIndex = 0;
            // the user's path: the picture widget from the palette, then its Custom texture row (… = a PNG chosen; Fit = the box its size)
            var s_Before = m_Current!.Placements.Select(p => p.Name).ToHashSet();
            if (!HandleStageDrop(new DataObject(PartitionFormat, c_ImageWidget), new Point(300, 200))) throw new Exception("the picture widget could not be dropped");
            var s_Name = m_Current.Placements.First(p => p.Added && !s_Before.Contains(p.Name)).Name;
            m_SelectedPlacement = s_Name; m_SelectedNode = s_Name; m_SelectedKey = null; m_Stage.Selected = s_Name; ShowProperties();
            System.Collections.Generic.IEnumerable<PropRow> Flat(System.Collections.Generic.IEnumerable<PropRow> p_Rows) => p_Rows.SelectMany(r => new[] { r }.Concat(Flat(r.Children)));
            PropRow RowAt(string p_Path) => Flat(PropertiesGrid.Rows).FirstOrDefault(r => r.Path == p_Path) ?? throw new Exception("no property row " + p_Path + " (have: " + string.Join(", ", Flat(PropertiesGrid.Rows).Select(r => r.Path).Take(40)) + ")");
            var s_CustomRow = RowAt("img.custom");
            if (s_CustomRow.Value?.ToString() != "" || s_CustomRow.OnAction == null || Flat(PropertiesGrid.Rows).Any(r => r.Path == "img.fit")) throw new Exception("the Custom texture row does not start empty with its … button (and no Box row yet)");
            if (SetCustomTextureFile(s_Name, s_PngPath) == null) throw new Exception("Custom texture refused the test PNG");
            if (Renderer().DrawnPictures.Any(d => d.Path.EndsWith(s_Name) && (System.Math.Abs(d.Box.Width - 98) < 0.5 && System.Math.Abs(d.Box.Height - 58) < 0.5))) throw new Exception("the box took the picture's size before Fit was asked for");
            var s_FitRow = RowAt("img.fit");
            if (s_FitRow.OnAction == null || !(s_FitRow.Text ?? "").Contains("98x58")) throw new Exception("the Box row does not offer Fit for a 98x58 picture: " + s_FitRow.Text);
            s_FitRow.OnAction();
            if (RowAt("img.custom").Value?.ToString() != "test_image" && RowAt("img.custom").Value?.ToString() != "test_image2") throw new Exception("the Custom texture row does not read the picture back: " + RowAt("img.custom").Value);
            if (!(RowAt("img.fit").Text ?? "").Contains("1:1")) throw new Exception("after Fit the Box row does not say 1:1: " + RowAt("img.fit").Text);
            var s_Entry = DocScreen(m_Current!.Partition) ?? throw new Exception("no document screen after Custom texture");
            var s_Node = s_Entry.Nodes.FirstOrDefault(n => n.InstanceName == s_Name) ?? throw new Exception($"no document node {s_Name}");
            // the walk may have added the same file on another screen already (test_image2 then): the node's StaticUrl names the entry
            var s_Image = m_Doc.ImageByTexture(s_Node.Properties.TryGetValue("StaticUrl", out var s_Named) ? s_Named : "") ?? throw new Exception("the document does not list the picture the node names: " + string.Join(",", m_Doc.Images.Select(i => i.Name)));
            if (!s_Image.Name.StartsWith("test_image")) throw new Exception("the picture was not named after its file: " + s_Image.Name);
            var s_Texture = m_Doc.ImageTextureName(s_Image);
            if (s_Node.Widget != "ui/assets/imagemanager" || !s_Node.Properties.TryGetValue("StaticUrl", out var s_Url) || s_Url != s_Texture || !s_Node.Properties.TryGetValue("ResizeToTarget", out var s_Fit) || s_Fit != "true")
                throw new Exception($"the picture's node is {s_Node.Widget} with {string.Join(", ", s_Node.Properties.Select(kv => kv.Key + "=" + kv.Value))}, expected imagemanager with StaticUrl={s_Texture}, ResizeToTarget=true");
            if (!s_Image.File.StartsWith("images/") || !File.Exists(ImageTextures.ImagePath(s_Image, DocDir))) throw new Exception($"the picture was not kept with the document: {s_Image.File}");
            var s_Drawn = Renderer().DrawnPictures.FirstOrDefault(d => d.Path == s_Name || d.Path.EndsWith("/" + s_Name)) ?? throw new Exception($"the stage drew no picture for {s_Name} (drawn: {string.Join(", ", Renderer().DrawnPictures.Select(d => d.Path))})");
            if (s_Drawn.Url != s_Texture || System.Math.Abs(s_Drawn.Box.X - 300) > 0.5 || System.Math.Abs(s_Drawn.Box.Y - 200) > 0.5 || System.Math.Abs(s_Drawn.Box.Width - 98) > 0.5 || System.Math.Abs(s_Drawn.Box.Height - 58) > 0.5 || System.Math.Abs(s_Drawn.Picture.Width - 98) > 0.5 || System.Math.Abs(s_Drawn.Picture.Height - 58) > 0.5)
                throw new Exception($"the stage draws {s_Drawn.Url} in a {s_Drawn.Box} box, picture {s_Drawn.Picture}; expected {s_Texture} in a 98x58 box at (300,200), the picture 1:1");
            if (!PropertiesTitle.Text.Contains(s_Name)) throw new Exception($"the Properties title reads \"{PropertiesTitle.Text}\" with the picture widget selected, expected {s_Name}");
            var s_ImagesRow = Flat(PropertiesGrid.Rows).FirstOrDefault(r => r.Path == "doc.images") ?? throw new Exception("no Images row in the Document category");
            if (s_ImagesRow.Children.Count != m_Doc.Images.Count || !s_ImagesRow.Children.Any(c => c.Name == s_Image.Name && (c.Text ?? "").Contains(s_Texture))) throw new Exception($"the Images row lists {s_ImagesRow.Children.Count} pictures: {string.Join(", ", s_ImagesRow.Children.Select(c => c.Name + ": " + c.Text))}");
            // the photo shows the Custom texture rows: the panel scrolled to them
            if (PropertiesGrid.Editors.TryGetValue("img.custom", out var s_CustomEditor)) { s_CustomEditor.BringIntoView(); UpdateLayout(); await Task.Delay(100); }
            Photograph(Path.Combine(p_OutDir, "selftest_image.png"));

            // the live preview: the widget asks the engine for the texture by name, the host says it exists, the server serves the PNG, the clip holds it at its size
            var s_LogStart = LogBox.Text.Length;
            await StartPreviewForSeam(p_OutDir);
            var s_Served = "GET /ui/assets/img/" + s_Texture + " -> 200";   // the log keeps the path as the movie asked it
            for (var i = 0; i < 300 && !LogBox.Text[s_LogStart..].Contains(s_Served, StringComparison.OrdinalIgnoreCase); ++i) await Task.Delay(50);
            if (!LogBox.Text[s_LogStart..].Contains(s_Served, StringComparison.OrdinalIgnoreCase)) throw new Exception($"the preview did not serve the picture ({s_Served} not logged); asked: {string.Join(" | ", LogBox.Text[s_LogStart..].Split('\n').Where(l => l.Contains("textureExists") || l.Contains("loadTextureAsync") || l.Contains("/img/")).Take(6))}");
            using var s_Http = new System.Net.Http.HttpClient();
            var s_Bytes = await s_Http.GetByteArrayAsync(m_RuffleServer!.Url + "ui/assets/img/" + s_Texture.ToLowerInvariant());
            var (s_PngW, s_PngH) = (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(s_Bytes.AsSpan(16, 4)), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(s_Bytes.AsSpan(20, 4)));
            if (s_PngW != 98 || s_PngH != 58) throw new Exception($"the preview serves the picture as {s_PngW}x{s_PngH}, expected 98x58");
            var s_HolderPath = $"_level0.sc1.instance1.{s_Name}.imgHolder._width";
            var s_Holder = "";
            for (var i = 0; i < 100; ++i) { s_Holder = (await ProbeClip(s_HolderPath)).Trim('"'); if (double.TryParse(s_Holder, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s_W0) && s_W0 > 1) break; await Task.Delay(100); }
            if (!double.TryParse(s_Holder, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s_HolderW) || System.Math.Abs(s_HolderW - 98) > 1.5) throw new Exception($"in the player the picture clip spans {s_Holder} stage units, expected ~98 (the picture fitted 1:1 into its box)");
            await m_RuffleHost!.CaptureAsync(Path.Combine(p_OutDir, "selftest_ruffle_image.png"));

            // the build's part that needs no mount: the conversion and what the recipe ships (the walk's build runs the lines against the game)
            var s_BuildLog = new StringWriter();
            var s_Emitter = new ModEmitter(m_Doc, DocDir, Path.Combine(p_OutDir, "mods_image"), m_Rime.PartitionJson, m_Rime.ResourceBytes, s_BuildLog, Path.Combine(p_OutDir, "build_image"), m_Rime.PartitionNameByGuid);
            s_Emitter.Emit();
            Log(s_BuildLog.ToString());
            if (s_Emitter.Pictures.Count != m_Doc.Images.Count) throw new Exception($"the emitter prepared {s_Emitter.Pictures.Count} pictures, expected {m_Doc.Images.Count}");
            var s_Picture = s_Emitter.Pictures.First(p => p.TextureName == s_Texture);
            var s_Header = ImageTextures.ReadDdsHeader(s_Picture.Dds);
            if (s_Picture.TextureName != s_Texture || s_Header.FourCc != "DXT5" || s_Header.Width != 100 || s_Header.Height != 60 || s_Header.Mips > 1) throw new Exception($"the texture came out as {s_Picture.TextureName} {s_Header.FourCc} {s_Header.Width}x{s_Header.Height} {s_Header.Mips} mip(s), expected {s_Texture} DXT5 100x60 with one");
            if (new FileInfo(s_Picture.PixelsBin).Length != 6000) throw new Exception($"the chunk holds {new FileInfo(s_Picture.PixelsBin).Length} bytes, expected 6000 (100x60 DXT5)");
            var s_Back = DdsImage.Load(File.ReadAllBytes(s_Picture.Dds)) ?? throw new Exception("the DDS does not decode");
            var s_Red = PixelAt(s_Back, 5, 40); var s_Blue = PixelAt(s_Back, 80, 40); var s_Hole = PixelAt(s_Back, 15, 15); var s_Pad = PixelAt(s_Back, 99, 59);
            if (s_Red.A != 255 || s_Red.R < 240 || s_Red.G > 15 || s_Red.B > 15) throw new Exception($"the red half came back as bgra {s_Red} (the picture darkened or lost its colour on the way to DXT5)");
            if (s_Blue.B < 240 || s_Blue.A < 112 || s_Blue.A > 144) throw new Exception($"the half-clear blue came back as bgra {s_Blue}, expected blue with alpha ~128");
            if (s_Hole.A != 0 || s_Pad.A != 0) throw new Exception($"the clear hole came back with alpha {s_Hole.A} and the padding with {s_Pad.A}, expected 0");
            var s_Stub = JObject.Parse(File.ReadAllText(s_Picture.PartitionJson));
            if ((string?)s_Stub["Name"] != s_Texture || (string?)s_Stub["Instances"]?[(string?)s_Stub["PrimaryInstanceGuid"] ?? ""]?["$type"] != "TextureAsset") throw new Exception("the TextureAsset stub is not shaped as the camo thumbnails': " + s_Stub.ToString(Newtonsoft.Json.Formatting.None));
            var s_Recipe = s_Emitter.RecipeLines().ToList();
            var s_ChunksSb = $"build_sb Win32/{m_Doc.ChunksSuperbundle} Frostbite2_0";
            foreach (var s_Expected in new[] { $"add_dds_texture \"{s_Texture}\" \"{s_Picture.Dds}\" false true false \"UI\" \"{ImageTextures.GroupDonor}\" true \"{s_Picture.ChunkGuid}\"", $"add_json_partition \"{s_Texture}\" \"{s_Picture.PartitionJson}\"", $"remove_chunk {s_Picture.ChunkGuid}", $"add_chunk {s_Picture.ChunkGuid} \"{s_Picture.PixelsBin}\" \"{s_Texture.ToLowerInvariant()}\"" })
                if (!s_Recipe.Contains(s_Expected)) throw new Exception("the recipe lacks: " + s_Expected + "\n" + string.Join("\n", s_Recipe));
            var s_SbLine = s_Recipe.FindIndex(l => l.StartsWith(s_ChunksSb) && l.EndsWith(" false"));
            var s_ChunkLine = s_Recipe.FindIndex(l => l.StartsWith("add_chunk "));
            if (s_SbLine < 0 || s_ChunkLine < s_SbLine || s_Recipe.FindIndex(l => l.StartsWith("add_dds_texture")) > s_Recipe.FindIndex(l => l == "build")) throw new Exception("the recipe's order is wrong (texture in the UI bundle, then the NONCAS chunk store with the pixels):\n" + string.Join("\n", s_Recipe));
            var s_ModJson = JObject.Parse(File.ReadAllText(Path.Combine(s_Emitter.ModDir, "mod.json")));
            var s_Superbundles = (s_ModJson["Superbundles"] as JArray)?.Select(t => (string?)t).ToList() ?? new System.Collections.Generic.List<string?>();
            if (s_Superbundles.Count != 2 || !s_Superbundles.Contains("Win32/" + m_Doc.ChunksSuperbundle)) throw new Exception("mod.json does not declare the chunk store: " + string.Join(",", s_Superbundles));
            var s_Shared = File.ReadAllText(Path.Combine(s_Emitter.ModDir, "ext", "Shared", "__init__.lua"));
            if (!s_Shared.Contains($"local CHUNKS = \"{m_Doc.ChunksSuperbundle}\"") || !s_Shared.Contains("MountSuperBundle(CHUNKS)")) throw new Exception("the shared Lua does not mount the chunk store");
            // a picture taken out of the document: the list and the stage let go of it (the node stays, showing nothing)
            RemoveImage(s_Image);
            if (m_Doc.Images.Any(i => i.Name == s_Image.Name) || Renderer().DrawnPictures.Any(d => d.Url == s_Texture)) throw new Exception("the removed picture is still listed or drawn");
            Undo();
            if (!m_Doc.Images.Any(i => i.Name == s_Image.Name)) throw new Exception("undo did not bring the removed picture back");
            R($"image: test_image.png 98x58 with alpha (decoder = WPF byte for byte) → {s_Name} (imagemanager, StaticUrl {s_Texture}, fitted) at (300,200), drawn 98x58 on the stage (selftest_image.png), listed under Document ▸ Images; preview: the player asked, the server served the PNG 98x58, the clip spans {s_HolderW:0.#} (selftest_ruffle_image.png); build: DXT5 100x60 1 mip, 6000 bytes of pixels, red/blue/hole/padding back as put in, TextureAsset stub, recipe with the texture in the UI bundle + chunk store Win32/{m_Doc.ChunksSuperbundle}, mod.json with both superbundles, Lua mounts the store; removed → gone from list and stage, undone → back");
        }

        /// <summary>
        /// The text size knob: a text field dropped on the accessories screen with a static text, its Text size row set to 40 px — the
        /// document gets a textsize op, the movie the clip variable and one frame action, the stage art draws the glyphs at 40 px, and the
        /// live preview's field measures a 40 px text (the game's own font at that size) after the widget received its text.
        /// </summary>
        async Task StepTextSize(Action<string> R, string p_OutDir)
        {
            var s_Screen = m_Current!.Partition;
            CentreTabs.SelectedIndex = 0;
            // the field this step drops, not one an earlier step added (the walk's static text step leaves TextField_02 on this screen)
            var s_Before = m_Current.Placements.Select(p => p.Name).ToHashSet();
            var s_TextField = HandleStageDrop(new DataObject(PartitionFormat, "ui/assets/textfield"), new Point(500, 400)) ? m_Current.Placements.First(p => p.Name.StartsWith("TextField_") && p.Added && !s_Before.Contains(p.Name)) : throw new Exception("the text field did not land on the stage");
            System.Collections.Generic.IEnumerable<PropRow> Flat(System.Collections.Generic.IEnumerable<PropRow> p_Rows) => p_Rows.SelectMany(r => new[] { r }.Concat(Flat(r.Children)));
            PropRow RowAt(string p_Path) => Flat(PropertiesGrid.Rows).FirstOrDefault(r => r.Path == p_Path) ?? throw new Exception("no property row " + p_Path + " (have: " + string.Join(", ", Flat(PropertiesGrid.Rows).Select(r => r.Path).Take(40)) + ")");
            RowAt("f.StaticText").OnChange!("BIG TEXT");
            GfxRenderer.DrawnText TfRow() => Renderer().DrawnTexts.FirstOrDefault(d => d.Path.Contains("/" + s_TextField.Name + "/bold1/")) ?? throw new Exception("the text field's bold1 row was not drawn");
            if (System.Math.Abs(TfRow().SizePx - 20) > 0.01) throw new Exception($"before the knob the bold1 row draws at {TfRow().SizePx} px, expected the symbol's 20");
            // the panel's title names what is selected (keku 19:50: "al lado de propiedades añade el título de lo que se está seleccionando")
            if (!PropertiesTitle.Text.Contains(s_TextField.Name) || !PropertiesTitle.Text.Contains("WidgetNode") || !PropertiesTitle.Text.Contains("textfield")) throw new Exception($"the Properties title reads \"{PropertiesTitle.Text}\", expected the selected node's name, type and widget");
            {
                UpdateLayout();
                var s_Shot = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
                s_Shot.Render(this);
                var s_Enc = new PngBitmapEncoder(); s_Enc.Frames.Add(BitmapFrame.Create(s_Shot));
                using var s_File = File.Create(Path.Combine(p_OutDir, "selftest_props_title.png")); s_Enc.Save(s_File);
            }
            var s_SizeRow = RowAt("tx.size");
            if (s_SizeRow.Value?.ToString() != "") throw new Exception("the Text size row does not start empty (the row type's own size)");
            s_SizeRow.OnChange!("40");
            var s_Entry = DocScreen(s_Screen) ?? throw new Exception("no document screen");
            if (!s_Entry.Stage.Contains($"textsize:{s_TextField.Name}:40")) throw new Exception("the Text size row did not add the textsize op: " + string.Join(" | ", s_Entry.Stage));
            var s_Placed = m_Current.Placements.First(p => p.Name == s_TextField.Name);
            if (s_Placed.ClipVars == null || !s_Placed.ClipVars.Any(v => v.Name == "_rueTextSize" && Convert.ToDouble(v.Value, System.Globalization.CultureInfo.InvariantCulture) == 40)) throw new Exception("the op did not set _rueTextSize=40 on the clip: " + (s_Placed.ClipVars == null ? "(none)" : GfxMovie.FormatClipVars(s_Placed.ClipVars)));
            if (!m_Current.Movie!.FrameScripts().Contains("textsize")) throw new Exception("the op did not add the textsize frame action to the movie");
            if (System.Math.Abs(TfRow().SizePx - 40) > 0.01) throw new Exception($"with the knob at 40 the stage draws the row at {TfRow().SizePx} px");
            if (RowAt("tx.size").Value?.ToString() != "40") throw new Exception("the Text size row does not read back 40");
            if (Flat(PropertiesGrid.Rows).Any(r => r.Path == "cv._rueTextSize")) throw new Exception("the op's own clip variable shows as an editable clip variable");
            // the live preview: the field's text measures the size (the widget's own font, vector), and grows past its 20 px row box
            await StartPreviewForSeam(p_OutDir);
            var s_FieldPath = $"_level0.sc1.instance1.{s_TextField.Name}.bold1.txtDisplay";
            for (var i = 0; i < 100 && (await ProbeClip(s_FieldPath + ".text")).Trim('"') != "BIG TEXT"; ++i) await Task.Delay(50);
            var s_Text = (await ProbeClip(s_FieldPath + ".text")).Trim('"');
            if (s_Text != "BIG TEXT") throw new Exception($"the preview's field reads \"{s_Text}\", expected the static text");
            var s_HeightText = await ProbeClip(s_FieldPath + ".textHeight");
            if (!double.TryParse(s_HeightText.Trim('"'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s_TextHeight) || s_TextHeight < 40 || s_TextHeight > 60) throw new Exception($"the preview's field measures textHeight {s_HeightText} for a 40 px text, expected ~44-52 (20 px gives ~24)");
            var s_Format = await ProbeClip(s_FieldPath + ".getNewTextFormat().size");
            await m_RuffleHost!.CaptureAsync(Path.Combine(p_OutDir, "selftest_ruffle_textsize.png"));
            // back to the row type's own size: the op goes, the stage draws 20 px again (the preview took the selection: the field is selected again)
            m_SelectedPlacement = s_TextField.Name; m_SelectedNode = s_TextField.Name; m_SelectedKey = null; m_Stage.Selected = s_TextField.Name; ShowProperties();
            RowAt("tx.size").OnChange!("");
            if (s_Entry.Stage.Any(o => o.StartsWith("textsize:" + s_TextField.Name + ":")) || System.Math.Abs(TfRow().SizePx - 20) > 0.01) throw new Exception("clearing the Text size did not remove the op / bring the 20 px row back");
            R($"text size: {s_TextField.Name} Text size (px) = 40 → textsize op, _rueTextSize=40 on the clip + the textsize frame action, stage art at 40 px; live preview: the field's textHeight {s_TextHeight:0.#} (format size {s_Format}) for \"{s_Text}\" (selftest_ruffle_textsize.png); cleared → 20 px again");
        }

        /// <summary>
        /// RUE_SELFTEST_FOCUS=&lt;step&gt;: only what the named step needs - the accessories screen opened from the cache, the player up when the
        /// step plays it, that step - and nothing of the rest of the walk (keku, 2026-09-16: "para de ir por todo el proceso... solo haz lo
        /// estrictamente necesario"). The whole walk stays the gate before a delivery; this is the loop while a step is being made.
        /// Steps: resolution, textsize, placement, image, depth.
        /// </summary>
        async Task<int> SelfTestFocused(string p_Step, Action<string> R, string p_OutDir)
        {
            if (!m_Rime.HasCache) throw new Exception("a focused run needs the warm cache");
            for (var i = 0; i < 300 && m_Restoring; ++i) await Task.Delay(100);   // the last run's session coming back: the session is not saved while it does
            if (!await OpenScreen("ui/flow/screen/customizeaccessoriesscreen")) throw new Exception("the accessories screen did not open");
            R($"focused run: {p_Step} - the accessories screen opened from the cache, nothing else of the walk");
            switch (p_Step.ToLowerInvariant())
            {
                case "resolution":
                    await StartPreviewForSeam(p_OutDir);
                    await StepResolution(R, p_OutDir);
                    break;
                case "textsize":
                    await StepTextSize(R, p_OutDir);
                    break;
                case "placement":
                    await StepPlacement(R);
                    break;
                case "record":
                    await StepRecordBuild(R, p_OutDir);
                    break;
                case "camomenu":
                {
                    // the camo menu document (Data/CamoMenu.json, a copy in the out folder) takes the stage
                    var s_CamoDoc = Path.Combine(p_OutDir, "CamoMenu.json");
                    File.Copy(Path.Combine(AppContext.BaseDirectory, "Data", "CamoMenu.seam.json"), s_CamoDoc, true);   // the shipping document plus a test table entry
                    m_Doc = Newtonsoft.Json.JsonConvert.DeserializeObject<ScreenDocument>(File.ReadAllText(s_CamoDoc)) ?? throw new Exception("the camo menu document did not parse");
                    m_DocPath = s_CamoDoc; ClearHistory(); RegisterNewScreens(); UpdateSaveButtons(); ApplyDocumentToCurrent(); ShowProperties();
                    await StepCamoMenu(R, p_OutDir);
                    break;
                }
                case "vehiclecamo":
                {
                    // the camo menu document (the seam copy), as for camomenu: its TIERRA / AIRE edits, the kit movie and the vehicle windows
                    var s_CamoDoc = Path.Combine(p_OutDir, "CamoMenu.json");
                    File.Copy(Path.Combine(AppContext.BaseDirectory, "Data", "CamoMenu.seam.json"), s_CamoDoc, true);
                    m_Doc = Newtonsoft.Json.JsonConvert.DeserializeObject<ScreenDocument>(File.ReadAllText(s_CamoDoc)) ?? throw new Exception("the camo menu document did not parse");
                    m_DocPath = s_CamoDoc; ClearHistory(); RegisterNewScreens(); UpdateSaveButtons(); ApplyDocumentToCurrent(); ShowProperties();
                    await StepVehicleCamo(R, p_OutDir);
                    break;
                }
                case "soldierskin":
                {
                    // the camo menu document (the seam copy), as for vehiclecamo: the flow graph's door moved, the soldier's window
                    var s_CamoDoc = Path.Combine(p_OutDir, "CamoMenu.json");
                    File.Copy(Path.Combine(AppContext.BaseDirectory, "Data", "CamoMenu.seam.json"), s_CamoDoc, true);
                    m_Doc = Newtonsoft.Json.JsonConvert.DeserializeObject<ScreenDocument>(File.ReadAllText(s_CamoDoc)) ?? throw new Exception("the camo menu document did not parse");
                    m_DocPath = s_CamoDoc; ClearHistory(); RegisterNewScreens(); UpdateSaveButtons(); ApplyDocumentToCurrent(); ShowProperties();
                    await StepSoldierSkin(R, p_OutDir);
                    break;
                }
                case "sidearmcamo":
                {
                    // the camo menu document (the seam copy), as for soldierskin: the LOADOUT screen's SIDEARM door, the pistol's window
                    var s_CamoDoc = Path.Combine(p_OutDir, "CamoMenu.json");
                    File.Copy(Path.Combine(AppContext.BaseDirectory, "Data", "CamoMenu.seam.json"), s_CamoDoc, true);
                    m_Doc = Newtonsoft.Json.JsonConvert.DeserializeObject<ScreenDocument>(File.ReadAllText(s_CamoDoc)) ?? throw new Exception("the camo menu document did not parse");
                    m_DocPath = s_CamoDoc; ClearHistory(); RegisterNewScreens(); UpdateSaveButtons(); ApplyDocumentToCurrent(); ShowProperties();
                    await StepSidearmCamo(R, p_OutDir);
                    break;
                }
                case "gadgetcamo":
                {
                    // the camo menu document (the seam copy), as for sidearmcamo: the LOADOUT screen's GADGET1 door, the crossbow's window
                    var s_CamoDoc = Path.Combine(p_OutDir, "CamoMenu.json");
                    File.Copy(Path.Combine(AppContext.BaseDirectory, "Data", "CamoMenu.seam.json"), s_CamoDoc, true);
                    m_Doc = Newtonsoft.Json.JsonConvert.DeserializeObject<ScreenDocument>(File.ReadAllText(s_CamoDoc)) ?? throw new Exception("the camo menu document did not parse");
                    m_DocPath = s_CamoDoc; ClearHistory(); RegisterNewScreens(); UpdateSaveButtons(); ApplyDocumentToCurrent(); ShowProperties();
                    await StepGadgetCamo(R, p_OutDir);
                    break;
                }
                case "vehiclerow":
                {
                    // the camo menu document (the seam copy), as for vehiclecamo: the TIERRA rows' script, the kit movie with the minimap icon
                    var s_CamoDoc = Path.Combine(p_OutDir, "CamoMenu.json");
                    File.Copy(Path.Combine(AppContext.BaseDirectory, "Data", "CamoMenu.seam.json"), s_CamoDoc, true);
                    m_Doc = Newtonsoft.Json.JsonConvert.DeserializeObject<ScreenDocument>(File.ReadAllText(s_CamoDoc)) ?? throw new Exception("the camo menu document did not parse");
                    m_DocPath = s_CamoDoc; ClearHistory(); RegisterNewScreens(); UpdateSaveButtons(); ApplyDocumentToCurrent(); ShowProperties();
                    await StepVehicleOwnRow(R, p_OutDir);
                    break;
                }
                case "depth":
                    // a clean document: the step reasons about the shipped clips' neighbours
                    m_Doc = new ScreenDocument { Name = "UiEditorSelfTest", Superbundle = "uieditorselftest/ui", Bundle = "uieditorselftest/uibundle" };
                    m_DocPath = Path.Combine(p_OutDir, "UiEditorSelfTest.json");
                    ClearHistory();
                    ApplyDocumentToCurrent();
                    await StepDepth(R);
                    break;
                case "image":
                    // a document of its own with a folder: the picture is kept under it (images/), as for a saved document of the user's
                    m_Doc = new ScreenDocument { Name = "UiEditorSelfTest", Superbundle = "uieditorselftest/ui", Bundle = "uieditorselftest/uibundle" };
                    m_DocPath = Path.Combine(p_OutDir, "UiEditorSelfTest.json");
                    ClearHistory();
                    ApplyDocumentToCurrent();
                    await StepImage(R, p_OutDir);
                    break;
                default:
                    throw new Exception($"unknown focused step '{p_Step}' (resolution, textsize, placement, image, depth, record, camomenu, vehiclecamo, vehiclerow, soldierskin, sidearmcamo, gadgetcamo)");
            }
            R("PASS (focused: " + p_Step + ")");
            return 0;
        }
    }
}
