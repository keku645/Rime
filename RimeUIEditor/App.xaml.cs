using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace RimeUIEditor
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // The Frostbite support assemblies are resolved at mount time by name; preload everything we ship,
            // exactly like RimeREPL does.
            foreach (var s_Dll in Directory.GetFiles(AppContext.BaseDirectory, "RimeLib.*.dll"))
            {
                try { System.Reflection.Assembly.LoadFrom(s_Dll); }
                catch { /* native or mismatched: skip */ }
            }
            base.OnStartup(e);

            var s_Crash = Path.Combine(Path.GetTempPath(), "RimeUIEditor_crash.txt");
            // an exception in a UI handler is logged (crash file + the window's log) and the editor stays up
            DispatcherUnhandledException += (_, x) =>
            {
                File.WriteAllText(s_Crash, x.Exception.ToString());
                if (Current?.MainWindow is MainWindow s_Main) { s_Main.LogError("ERROR: " + x.Exception.Message + "  (details in " + s_Crash + ")"); x.Handled = true; }
            };
            AppDomain.CurrentDomain.UnhandledException += (_, x) => { File.WriteAllText(s_Crash, x.ExceptionObject.ToString()); };

            // --selftest <bf3> <mods root> <reference .gfx> <out dir>  -> drives the window, exits with 0/1
            var a = e.Args;
            if (a.Length >= 5 && a[0] == "--selftest")
            {
                EditorSettings.Headless = true;
                var s_Window = new MainWindow();
                View.Monitors.PlaceForSeam(s_Window);
                s_Window.Loaded += async (_, _) =>
                {
                    var s_Code = await s_Window.SelfTest(a[1], a[2], a[3], a[4]);
                    Shutdown(s_Code);
                };
                s_Window.Show();
                return;
            }
            // --graphaudit <out dir>  -> every UI graph through the model and the graph view, from the cache; exits 0/1
            if (a.Length >= 2 && a[0] == "--graphaudit")
            {
                EditorSettings.Headless = true;
                var s_Window = new MainWindow();
                View.Monitors.PlaceForSeam(s_Window);
                s_Window.Loaded += async (_, _) =>
                {
                    var s_Code = await s_Window.GraphAudit(a[1]);
                    Shutdown(s_Code);
                };
                s_Window.Show();
                return;
            }
            // --dialogshot <out dir>: photographs the first-run and Settings dialogs with scratch settings (RUE_SETTINGS)
            if (a.Length >= 2 && a[0] == "--dialogshot")
            {
                EditorSettings.Headless = true;
                // the first dialog closed is the application's main window: with the default shutdown mode its Close() starts the
                // shutdown, Application.Current goes null and the next dialog's templates (a StaticResource of the App inside the
                // Button template) crash while measuring — the shots decide when the run ends
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var s_Settings = EditorSettings.Load();
                Directory.CreateDirectory(a[1]);
                var s_First = new View.FirstRunWindow(s_Settings);
                View.Monitors.PlaceForSeam(s_First);
                s_First.Show();
                Shot(s_First, Path.Combine(a[1], "firstrun.png"));
                s_First.Close();
                var s_Dialog = new View.SettingsWindow(new Window(), s_Settings, () => { });
                View.Monitors.PlaceForSeam(s_Dialog);
                s_Dialog.Show();
                Shot(s_Dialog, Path.Combine(a[1], "settings.png"));
                s_Dialog.Close();
                // the Build dialog over a document that touches a shipped screen and makes a new one
                var s_Sample = new RimeLib.Cmd.UiBuilder.ScreenDocument { Name = "MyUiMod", Superbundle = "myuimod/ui", Bundle = "myuimod/uibundle" };
                s_Sample.Screens.Add(new RimeLib.Cmd.UiBuilder.ScreenEntry
                {
                    Partition = "ui/flow/screen/customizeaccessoriesscreen",
                    Stage = { "import:Grid.swf:11:Grid", "add:10:CamoGrid_01:11:11:740:260:4.15:4.84" },
                    Nodes =
                    {
                        new RimeLib.Cmd.UiBuilder.NodeEntry { InstanceName = "CamoGrid_01", Widget = "ui/assets/grid" },
                        new RimeLib.Cmd.UiBuilder.NodeEntry { InstanceName = "SetButtons", Type = "DataSetNode", Connections = { new RimeLib.Cmd.UiBuilder.ConnectionEntry { FromPort = "Out", ToNode = "Confirm", ToPort = "In" } } },
                    },
                    RemovedConnections = { new RimeLib.Cmd.UiBuilder.RemovedConnectionEntry { Guid = "A38893AA-11D5-481A-BE62-C489F9B33652", From = "ConsoleButtonBar_01.OnItemOut", To = "Confirm.In" } },
                });
                s_Sample.Screens.Add(new RimeLib.Cmd.UiBuilder.ScreenEntry { Partition = "ui/flow/screen/myscreen", New = true, Title = "MyScreen", Nodes = { new RimeLib.Cmd.UiBuilder.NodeEntry { InstanceName = "Button_01", Widget = "ui/assets/button" } } });
                var s_Build = new View.BuildDialog(new Window(), s_Sample, s_Settings);
                View.Monitors.PlaceForSeam(s_Build);
                s_Build.Show();
                Shot(s_Build, Path.Combine(a[1], "build.png"));
                s_Build.Close();
                Shutdown(0);
                return;
            }
            // --render <movie resource> <out.png> [scale]: draws frame 1 of a movie from the cache (RUE_SETTINGS scratch or
            // the machine's settings), on the stage background, at 1:1 stage pixels — the seam for the art renderer
            if (a.Length >= 3 && a[0] == "--render")
            {
                EditorSettings.Headless = true;
                var s_Code = RenderToPng(a[1], a[2], a.Length >= 4 ? double.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture) : 1.0);
                Shutdown(s_Code);
                return;
            }
            // --ruffle <movie resource> <out dir>: converts a screen movie and every movie it imports (transitively) from the cache
            // into plain SWFs laid out under <out dir>/ui/..., plus the font library the imports ask for — what a Flash player
            // (Ruffle) opens to run the game's own ActionScript
            if (a.Length >= 3 && a[0] == "--ruffle")
            {
                EditorSettings.Headless = true;
                var s_Code = ConvertForRuffle(a[1], a[2], a.Length >= 4 ? a[3] : null);
                Shutdown(s_Code);
                return;
            }
            // --recorder <mods root> <movie resource> [<movie resource>…]: Record data… from the command line — the recorder mod for
            // those screens, its superbundle built against the game of the settings (RUE_SETTINGS scratch or the machine's), which is
            // mounted here (minutes)
            if (a.Length >= 3 && a[0] == "--recorder")
            {
                EditorSettings.Headless = true;
                var s_Code = BuildRecorder(a[1], a.Skip(2).ToList());
                Shutdown(s_Code);
                return;
            }
            // --build <document.json> <Mods root> [--record]: Build mod… from the command line — the game of the settings mounted (minutes),
            // the document's mod written and its superbundle(s) built; --record puts the editor's recorder in the screen movies (a diagnosis build)
            if (a.Length >= 3 && a[0] == "--build")
            {
                EditorSettings.Headless = true;
                var s_Code = BuildDocument(a[1], a[2], a.Skip(3).Contains("--record"));
                Shutdown(s_Code);
                return;
            }
            // --warm: mounts the game of the settings and fills its cache with everything the editor reads (idempotent: what is there
            // is kept, what a newer editor also caches — icons, the customization partitions — is added). What Mount does in the editor,
            // from the command line (minutes)
            if (a.Length >= 1 && a[0] == "--warm")
            {
                EditorSettings.Headless = true;
                var s_Settings = EditorSettings.Load();
                if (!EditorSettings.LooksLikeGame(s_Settings.GamePath) || string.IsNullOrWhiteSpace(s_Settings.CachePath)) { Console.Error.WriteLine("the settings need a game folder and a cache folder"); Shutdown(2); return; }
                var s_Rime = new RimeLib.Cmd.UiBuilder.RimeUiService();
                var s_CacheDir = RimeLib.Cmd.UiBuilder.RimeUiService.CacheDirFor(s_Settings.CachePath, s_Settings.GamePath);
                s_Rime.UseCache(s_CacheDir);
                try
                {
                    Console.WriteLine("mounting " + s_Settings.GamePath + " (minutes)…");
                    s_Rime.Mount(s_Settings.GamePath, Console.Out);
                    s_Rime.WarmCache(Console.Out, (d, t, w) => { if (d % 500 == 0) Console.WriteLine($"  {d}/{t} {w}"); });
                    Console.WriteLine("CACHE WARM: " + s_CacheDir);
                    Shutdown(0);
                }
                catch (Exception s_Ex) { Console.Error.WriteLine("CACHE WARM FAILED: " + s_Ex.Message); Shutdown(1); }
                return;
            }
            // --texts <ID …>: the game's text for each id in every language the game ships (mounts the game: minutes) — what a title id says
            // to every player; with a word instead of an id (no ID_ prefix) the ids whose English text contains it, with their texts
            if (a.Length >= 2 && a[0] == "--texts")
            {
                EditorSettings.Headless = true;
                var s_Settings = EditorSettings.Load();
                if (!EditorSettings.LooksLikeGame(s_Settings.GamePath)) { Console.Error.WriteLine("the settings need a game folder"); Shutdown(2); return; }
                var s_Rime = new RimeLib.Cmd.UiBuilder.RimeUiService();
                if (!string.IsNullOrWhiteSpace(s_Settings.CachePath)) s_Rime.UseCache(RimeLib.Cmd.UiBuilder.RimeUiService.CacheDirFor(s_Settings.CachePath, s_Settings.GamePath));
                try
                {
                    Console.WriteLine("mounting " + s_Settings.GamePath + " (minutes)…");
                    s_Rime.Mount(s_Settings.GamePath, Console.Out);
                    var s_Langs = new[] { "us", "es", "fr", "ge", "it", "pl", "ru", "cs", "jp", "ko" };
                    var s_Dbs = new Dictionary<string, RimeLib.Cmd.UiBuilder.TextDatabase>();
                    foreach (var s_Lang in s_Langs)
                    {
                        try { s_Dbs[s_Lang] = RimeLib.Cmd.UiBuilder.TextDatabase.Load(s_Rime, s_Lang); }
                        catch (Exception s_Ex) { Console.WriteLine($"{s_Lang}: not loaded ({s_Ex.Message})"); }
                    }
                    // "@<file>": every id in that file, resolved -- the database keys by HASH, so a name is the
                    // only way back into it, and the names live in the data (External/keku/find_text_ids.py
                    // --list writes them). One line per id that resolves: id, then each language.
                    var s_Args = a.Skip(1).SelectMany(p_A => p_A.StartsWith("@", StringComparison.Ordinal) && File.Exists(p_A[1..])
                        ? File.ReadAllLines(p_A[1..]).Where(p_L => p_L.Trim().Length > 0)
                        : new[] { p_A }).ToList();

                    // a handful of ids: every language under each. A whole list: one line each, English and
                    // Spanish, and only those that resolve -- a 6000-id sweep is for READING, not for scrolling.
                    var s_Compact = s_Args.Count > 50;

                    foreach (var s_Arg in s_Args)
                    {
                        if (s_Arg.StartsWith("ID_", StringComparison.Ordinal))
                        {
                            if (s_Compact)
                            {
                                var s_Text = s_Dbs.TryGetValue("us", out var s_UsDb) ? s_UsDb.Lookup(s_Arg) : null;
                                if (s_Text != null)
                                    Console.WriteLine($"{s_Arg}\t{s_Text.Replace("\n", " ")}\t" +
                                                      (s_Dbs.TryGetValue("es", out var s_EsDb) ? s_EsDb.Lookup(s_Arg)?.Replace("\n", " ") : null));
                                continue;
                            }

                            Console.WriteLine(s_Arg);
                            foreach (var (s_Lang, s_Db) in s_Dbs) Console.WriteLine($"  {s_Lang}: {s_Db.Lookup(s_Arg) ?? "(none)"}");
                            continue;
                        }
                        // a word: every English text containing it, with the hash (the id is not stored: it is named only where the data uses it)
                        if (!s_Dbs.TryGetValue("us", out var s_Us)) continue;
                        Console.WriteLine($"texts containing \"{s_Arg}\" (English), with every language:");
                        foreach (var kv in s_Us.All.Where(kv => kv.Value.Contains(s_Arg, StringComparison.OrdinalIgnoreCase)).OrderBy(kv => kv.Value.Length).Take(60))
                            Console.WriteLine($"  {kv.Key}: " + string.Join(" | ", s_Dbs.Select(d => d.Key + "=" + (d.Value.All.FirstOrDefault(x => x.Key == kv.Key).Value ?? "(none)"))));
                    }
                    Shutdown(0);
                }
                catch (Exception s_Ex) { Console.Error.WriteLine("TEXTS FAILED: " + s_Ex.Message); Shutdown(1); }
                return;
            }
            // --import <text file>: Import recording… from the command line — the recorder's lines (a copied console, or what uirec_db.py
            // wrote from the mod's database) into one fixture per screen under the preview root of the settings
            if (a.Length >= 2 && a[0] == "--import")
            {
                EditorSettings.Headless = true;
                var s_Report = new DataRecorder.ImportReport();
                var s_All = DataRecorder.ImportAll(File.ReadAllText(a[1]), EditorSettings.DefaultPreviewRoot(), s_Report);
                foreach (var (s_Rec, s_Path) in s_All) Console.WriteLine($"RECORDING IMPORTED: {s_Rec.Screen} -> {s_Path} ({s_Rec.Summary})");
                Console.WriteLine(s_Report.ToString());
                Shutdown(s_All.Count > 0 ? 0 : 1);
                return;
            }
            new MainWindow().Show();
        }

        static int BuildDocument(string p_DocPath, string p_ModsRoot, bool p_Record)
        {
            var s_Settings = EditorSettings.Load();
            if (!EditorSettings.LooksLikeGame(s_Settings.GamePath)) { Console.Error.WriteLine("the game folder of the settings is not set or is not the install root: " + s_Settings.GamePath); return 2; }
            if (!File.Exists(p_DocPath)) { Console.Error.WriteLine("no document at " + p_DocPath); return 2; }
            var s_Doc = Newtonsoft.Json.JsonConvert.DeserializeObject<RimeLib.Cmd.UiBuilder.ScreenDocument>(File.ReadAllText(p_DocPath));
            if (s_Doc == null) { Console.Error.WriteLine("the document does not parse: " + p_DocPath); return 2; }
            RimeLib.Cmd.UiBuilder.ImageTextures.TexconvOverride = s_Settings.TexconvPath;
            var s_Rime = new RimeLib.Cmd.UiBuilder.RimeUiService();
            var s_CacheDir = RimeLib.Cmd.UiBuilder.RimeUiService.CacheDirFor(s_Settings.CachePath, s_Settings.GamePath);
            if (!s_Rime.UseCache(s_CacheDir)) Console.WriteLine("no warm cache at " + s_CacheDir + " — everything comes from the mount");
            try
            {
                Console.WriteLine("mounting " + s_Settings.GamePath + " (minutes)…");
                s_Rime.Mount(s_Settings.GamePath, Console.Out);
                var s_Record = p_Record ? DataRecorder.Parts(s_Doc.Name, s_Doc.Screens.Where(s => s.Movie != null || s.New).Select(s => (s.Movie ?? "ui/assets/" + s.Partition.Split('/').Last()).Split('/').Last())) : null;
                var s_Work = Path.Combine(EditorSettings.DefaultBuildRoot(), s_Doc.Name);
                var s_Dir = s_Rime.BuildMod(s_Doc, Path.GetDirectoryName(Path.GetFullPath(p_DocPath))!, p_ModsRoot, Console.Out, s_Work, s_Record);
                Console.WriteLine($"MOD BUILT: {s_Dir}{(s_Record != null ? " — WITH THE RECORDER inside" : "")} (build files in {s_Work})");
                return 0;
            }
            catch (Exception s_Ex) { Console.Error.WriteLine("BUILD FAILED: " + s_Ex.Message); return 1; }
        }

        static int BuildRecorder(string p_ModsRoot, IReadOnlyList<string> p_Movies)
        {
            var s_Settings = EditorSettings.Load();
            if (!EditorSettings.LooksLikeGame(s_Settings.GamePath)) { Console.Error.WriteLine("the game folder of the settings is not set or is not the install root: " + s_Settings.GamePath); return 2; }
            var s_Rime = new RimeLib.Cmd.UiBuilder.RimeUiService();
            var s_CacheDir = RimeLib.Cmd.UiBuilder.RimeUiService.CacheDirFor(s_Settings.CachePath, s_Settings.GamePath);
            if (!s_Rime.UseCache(s_CacheDir)) Console.WriteLine("no warm cache at " + s_CacheDir + " — the movies come from the mount");
            try
            {
                Console.WriteLine("mounting " + s_Settings.GamePath + " (minutes)…");
                s_Rime.Mount(s_Settings.GamePath, Console.Out);
                var s_Result = RimeUIEditor.DataRecorder.BuildMod(s_Rime, p_Movies, p_ModsRoot, EditorSettings.DefaultBuildRoot(), Console.Out, true);
                Console.WriteLine($"RECORDER MOD WRITTEN: {s_Result.ModDir} — {s_Result.MoviePaths.Count} screen movie(s); superbundle {s_Result.SbPath} ({new FileInfo(s_Result.SbPath).Length} bytes)");
                return 0;
            }
            catch (Exception s_Ex) { Console.Error.WriteLine("RECORDER MOD FAILED: " + s_Ex.Message); return 1; }
        }

        static int RenderToPng(string p_Resource, string p_Out, double p_Scale)
        {
            var s_Settings = EditorSettings.Load();
            var s_Rime = new RimeLib.Cmd.UiBuilder.RimeUiService();
            var s_CacheDir = RimeLib.Cmd.UiBuilder.RimeUiService.CacheDirFor(s_Settings.CachePath, s_Settings.GamePath);
            if (!s_Rime.UseCache(s_CacheDir)) { Console.Error.WriteLine("no warm cache at " + s_CacheDir); return 2; }
            var s_Movies = new System.Collections.Generic.Dictionary<string, RimeLib.Cmd.Scaleform.GfxMovie?>();
            RimeLib.Cmd.Scaleform.GfxMovie? Movie(string n)
            {
                if (s_Movies.TryGetValue(n, out var m)) return m;
                try { m = RimeLib.Cmd.Scaleform.GfxMovie.Load(s_Rime.ResourceBytes(n)); } catch { m = null; }
                return s_Movies[n] = m;
            }
            System.Windows.Media.Imaging.BitmapSource? Texture(string n)
            {
                try { return View.DdsImage.Load(s_Rime.TextureDds(n)); } catch { return null; }
            }
            var s_FontMap = new System.Collections.Generic.Dictionary<string, string>();
            var s_MapLang = s_Settings.Language switch { "us" => "en", "ge" => "de", "jp" => "ja", _ => s_Settings.Language };
            try
            {
                var s_Json = s_Rime.PartitionJson($"ui/static/fontcollection_{s_MapLang}_fontmapcollection");
                foreach (var s_Font in ((Newtonsoft.Json.Linq.JObject)s_Json["Instances"]!).Properties().Select(p => p.Value).OfType<Newtonsoft.Json.Linq.JObject>().SelectMany(i => (i["Fonts"] as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray()).OfType<Newtonsoft.Json.Linq.JObject>()))
                    foreach (var s_Name in (s_Font["ScaleformFontName"] as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray()))
                        s_FontMap[(string)s_Name!] = (string?)s_Font["FontLongName"] ?? "";
            }
            catch (Exception s_Ex) { Console.Error.WriteLine("font map: " + s_Ex.Message); }
            Console.WriteLine("font map: " + string.Join(", ", s_FontMap.Select(kv => kv.Key + "=" + kv.Value)));
            RimeLib.Cmd.UiBuilder.TextDatabase? s_Texts = null;
            try { s_Texts = RimeLib.Cmd.UiBuilder.TextDatabase.Load(s_Rime, s_Settings.Language); } catch (Exception s_Ex) { Console.Error.WriteLine("texts: " + s_Ex.Message); }
            var s_WidgetSettings = RimeLib.Cmd.UiBuilder.WidgetSettings.Load();
            var s_Renderer = new View.GfxRenderer(Movie, Texture, s_FontMap, $"ui/static/fontcollection_{s_MapLang}_fontlib", s_Texts)
            {
                ClassOf = r => s_WidgetSettings?.Get(r)?.Class,
            };
            // a screen movie: the texts its bindings fix (page headers) come from the screen's graph
            try
            {
                var s_Partition = "ui/flow/screen/" + p_Resource.Split('/').Last();
                if (s_Rime.Partitions("ui/flow/screen/").Contains(s_Partition))
                {
                    var s_Screen = Model.ScreenModel.Load(s_Rime, s_Partition, Movie);
                    var s_Static = RimeUIEditor.MainWindow.StaticTexts(s_Screen, s_Texts);
                    if (s_Static.Count > 0) s_Renderer.TextFor = p => RimeUIEditor.MainWindow.TextOverride(s_Static, p);
                    Console.WriteLine("static texts: " + string.Join(", ", s_Static.Select(kv => kv.Key + "=" + kv.Value)));
                }
            }
            catch (Exception s_Ex) { Console.Error.WriteLine("screen graph: " + s_Ex.Message); }
            var s_Drawing = s_Renderer.RenderMovie(p_Resource);
            foreach (var w in s_Renderer.Warnings) Console.Error.WriteLine("warn: " + w);
            foreach (var t in s_Renderer.TextPaths) Console.WriteLine("text: " + t);
            foreach (var d in s_Renderer.DrawnTexts) Console.WriteLine($"drawn: {d.Path} = \"{d.Text}\" {d.SizePx:0.#}px {d.Font} box {d.Box.Width:0.#}x{d.Box.Height:0.#} at {d.Box.X:0.#},{d.Box.Y:0.#}");
            Console.WriteLine($"{p_Resource}: {s_Renderer.ShapesDrawn} shapes, {s_Renderer.BitmapsDrawn} bitmaps, {s_Renderer.TextsDrawn} texts, {s_Renderer.ImportsResolved} imports resolved, {s_Renderer.ImportsMissing} missing, {s_Renderer.Warnings.Count} warnings");
            if (s_Drawing == null) { Console.Error.WriteLine("nothing drawn"); return 1; }
            // a widget movie sits at its own origin: frame the drawing's bounds; a screen is the 1280x720 stage
            var s_Bounds = s_Drawing.Bounds;
            var s_IsScreen = s_Bounds.Width > 800 || p_Resource.Contains("screen");
            var s_Rect = s_IsScreen ? new Rect(0, 0, View.StageCanvas.StageWidth, View.StageCanvas.StageHeight)
                                     : new Rect(Math.Floor(s_Bounds.X) - 8, Math.Floor(s_Bounds.Y) - 8, Math.Ceiling(s_Bounds.Width) + 16, Math.Ceiling(s_Bounds.Height) + 16);
            Console.WriteLine($"bounds {s_Bounds.X:0.#},{s_Bounds.Y:0.#} {s_Bounds.Width:0.#}x{s_Bounds.Height:0.#} px");
            var s_W = (int)Math.Max(1, s_Rect.Width * p_Scale); var s_H = (int)Math.Max(1, s_Rect.Height * p_Scale);
            var s_Visual = new System.Windows.Media.DrawingVisual();
            using (var s_Dc = s_Visual.RenderOpen())
            {
                s_Dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 20, 24)), null, new Rect(0, 0, s_W, s_H));
                s_Dc.PushTransform(new System.Windows.Media.MatrixTransform(p_Scale, 0, 0, p_Scale, -s_Rect.X * p_Scale, -s_Rect.Y * p_Scale));
                s_Dc.DrawDrawing(s_Drawing);
                s_Dc.Pop();
            }
            var s_Bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(s_W, s_H, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            s_Bitmap.Render(s_Visual);
            var s_Encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            s_Encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(s_Bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(p_Out))!);
            using var s_File = File.Create(p_Out);
            s_Encoder.Save(s_File);
            Console.WriteLine("wrote " + p_Out);
            return 0;
        }

        static int ConvertForRuffle(string p_Resource, string p_OutDir, string? p_DocumentPath)
        {
            // the document's edits of the screen (stage ops, inline bindings with static texts), when a document is given
            RimeLib.Cmd.UiBuilder.ScreenEntry? s_DocEntry = null;
            List<RimeLib.Cmd.UiBuilder.MovieOverride>? s_DocMovies = null;
            if (p_DocumentPath != null)
            {
                try
                {
                    var s_Doc = Newtonsoft.Json.JsonConvert.DeserializeObject<RimeLib.Cmd.UiBuilder.ScreenDocument>(File.ReadAllText(p_DocumentPath));
                    s_DocMovies = s_Doc?.Movies;
                    s_DocEntry = s_Doc?.Screens.FirstOrDefault(s => string.Equals(s.Partition, "ui/flow/screen/" + p_Resource.Split('/').Last(), StringComparison.OrdinalIgnoreCase));
                    Console.WriteLine(s_DocEntry != null ? $"document {p_DocumentPath}: {s_DocEntry.Stage.Count} stage ops, {s_DocEntry.Nodes.Count} nodes for this screen" : $"document {p_DocumentPath}: nothing for this screen");
                }
                catch (Exception s_Ex) { Console.Error.WriteLine("document: " + s_Ex.Message); }
            }
            var s_Settings = EditorSettings.Load();
            var s_Rime = new RimeLib.Cmd.UiBuilder.RimeUiService();
            var s_CacheDir = RimeLib.Cmd.UiBuilder.RimeUiService.CacheDirFor(s_Settings.CachePath, s_Settings.GamePath);
            if (!s_Rime.UseCache(s_CacheDir)) { Console.Error.WriteLine("no warm cache at " + s_CacheDir); return 2; }
            var s_Result = RufflePreview.Convert(s_Rime, s_Settings.Language, p_Resource, s_DocEntry, p_OutDir, Console.WriteLine, s_DocMovies);
            Console.WriteLine($"{s_Result.Written} movies written under {p_OutDir}, {s_Result.Failed} failed; serve the folder over http and open {s_Result.ShellPath}");
            return s_Result.Failed == 0 ? 0 : 1;
        }

        static void Shot(Window p_Window, string p_Path)
        {
            // a SizeToContent window has no size until the dispatcher lays it out: let one Loaded-priority pass run
            p_Window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            p_Window.UpdateLayout();
            var s_Root = (FrameworkElement)p_Window.Content;
            if (s_Root.ActualWidth < 1)
            {
                s_Root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                s_Root.Arrange(new Rect(s_Root.DesiredSize));
            }
            var s_W = (int)Math.Max(p_Window.ActualWidth, s_Root.ActualWidth + 32);
            var s_H = (int)Math.Max(p_Window.ActualHeight, s_Root.ActualHeight + 32);
            var s_Bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(Math.Max(1, s_W), Math.Max(1, s_H), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            // render the content root on the window's background: a window that has not finished its first
            // layout renders blank, its content does not
            var s_Visual = new System.Windows.Media.DrawingVisual();
            using (var s_Dc = s_Visual.RenderOpen())
            {
                s_Dc.DrawRectangle(p_Window.Background, null, new Rect(0, 0, s_W, s_H));
                s_Dc.DrawRectangle(new System.Windows.Media.VisualBrush(s_Root), null, new Rect(16, 16, s_Root.ActualWidth, s_Root.ActualHeight));
            }
            s_Bitmap.Render(s_Visual);
            var s_Encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            s_Encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(s_Bitmap));
            using var s_File = File.Create(p_Path);
            s_Encoder.Save(s_File);
        }
    }
}
