using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimeLib.Cmd.Scaleform;
using RimeLib.Cmd.UiBuilder;

namespace RimeUIEditor
{
    /// <summary>
    /// Records, in the game, what its UI system hands a screen — and plays it back in the live preview.
    ///
    /// The game's UI system drives a screen through two calls on the screen's clip: initializeScreen(data) with one record per
    /// widget (InstanceName, HasFocus, NumEvents, Event_i, Align, ZDepthLevel, the widget properties, the bound data channels)
    /// and refreshScreen([{widgetName, widgetData}]) for every later data update. A compiled frame action (Data/UiRecorder.avm1,
    /// source Data/UiRecorder.as) injected on the screen movie's main timeline wraps both on the screen's own instance,
    /// serialises what arrives as JSON and sends it: in the game as bits over requestTypingInput — the AS2 log channel, decoded
    /// by the mod's client Lua into "[AS2] #R…" lines — and in the preview through ExternalInterface ("rec"). Record data…
    /// writes that mod for the open screen; Import recording… turns the copied client console into a fixture the preview's
    /// host feeds the screen from, so widgets built from game data (kit rows, button bars, info boxes) come up with content.
    /// </summary>
    public static class DataRecorder
    {
        public const string ModName = "RimeUiRecorder";
        public const string Superbundle = "rimeuirec/ui";
        public const string Bundle = "rimeuirec/uibundle";
        /// <summary>A variable the injected prologue sets: its name in a DoAction body marks a movie as carrying the recorder.</summary>
        public const string Marker = "_rueScreen";

        public static string ActionPath => Path.Combine(AppContext.BaseDirectory, "Data", "UiRecorder.avm1");
        public static bool Available => File.Exists(ActionPath);
        static byte[]? s_Action;
        /// <summary>The compiled recorder: a DoAction body (End included) that expects _rueScreen and _rueRoot set on its timeline.</summary>
        public static byte[] Action() => s_Action ??= File.ReadAllBytes(ActionPath);

        /// <summary>The screen's root clip on the main timeline (instance1 on every shipped screen).</summary>
        public static string RootClipName(GfxMovie p_Movie) => GfxMovie.FrameOnePlacements(p_Movie.Tags).FirstOrDefault(p => p.Name != null)?.Name ?? "instance1";

        /// <summary>
        /// Puts the recorder on a screen movie: one DoAction on the main timeline, in front of its ShowFrame — frame actions run after the
        /// frame's placements exist and before the loader reports the movie loaded (screenLoaded → the engine's initializeScreen comes
        /// after that), so the wrappers are on the screen instance in time. Nothing else in the movie changes.
        /// </summary>
        public static void Inject(GfxMovie p_Movie, string p_ScreenName, string? p_RootName = null)
        {
            if (IsInjected(p_Movie)) return;
            var s_Root = p_RootName ?? RootClipName(p_Movie);
            var s_Body = GfxToSwf.AssignStrings(new[] { (Marker, p_ScreenName), ("_rueRoot", s_Root) }).Concat(Action()).ToArray();
            var s_Index = p_Movie.Tags.FindIndex(t => t.Code == GfxMovie.TagShowFrame);
            if (s_Index < 0) throw new Exception("the movie has no ShowFrame on its main timeline");
            p_Movie.Tags.Insert(s_Index, GfxTag.Create(12, s_Body));
        }

        public static bool IsInjected(GfxMovie p_Movie) => p_Movie.Tags.Any(t => t.Code == 12 && Encoding.Latin1.GetString(t.Body).Contains(Marker));

        /// <summary>
        /// The recorder as a passenger of a document's own mod build (ModEmitter.Record): the same injection, receiver and server as the
        /// stand-alone recorder mod, under the mod's name — so the EDITED screens are recorded with nothing else installed.
        /// </summary>
        public static ModEmitter.Recorder Parts(string p_ModName, IEnumerable<string> p_Screens)
        {
            if (!Available) throw new Exception("the compiled recorder (Data/UiRecorder.avm1) is not next to the editor");
            var s_List = string.Join(", ", p_Screens);
            return new ModEmitter.Recorder(
                (m, s) => Inject(m, s),
                c_Receiver.Replace("{{MOD}}", p_ModName).Replace("{{SCREEN}}", s_List),
                c_Server.Replace("{{MOD}}", p_ModName).Replace("{{SCREEN}}", s_List),
                $"Carries the Rime UI editor's recorder in its screen movies ({s_List}): the client console shows [AS2] #R… lines, F5 sends them to the server (mod.db, table uirec) for Import recording… in the editor.");
        }

        // ------------------------------------------------------------------------------------------ the mod

        /// <summary>MoviePath is the first screen's movie; MoviePaths has one per screen, in the order given.</summary>
        public record ModResult(string ModDir, string WorkDir, string MoviePath, string SbPath, IReadOnlyList<string> Recipe, bool Built, IReadOnlyList<string> MoviePaths);

        public static ModResult BuildMod(RimeUiService p_Rime, string p_MovieResource, string p_ModsRoot, string p_WorkRoot, TextWriter p_Log, bool p_Build)
            => BuildMod(p_Rime, new[] { p_MovieResource }, p_ModsRoot, p_WorkRoot, p_Log, p_Build);

        /// <summary>
        /// Writes the recorder mod for one or more screens into the Mods folder: each screen's shipped movie with the recorder
        /// injected (ext/Shared delivers them the way every UI mod of the editor does), the client receiver (ext/Client), the
        /// server side that keeps the records (ext/Server), mod.json, and — when p_Build — its superbundle built against the
        /// mounted game. One mod records a whole walk through the game's menus: every record names its screen, and the run
        /// counter is shared by all the screens of a session, so the import tells them apart. Intermediates (the movies, the
        /// recipe) go to p_WorkRoot/RimeUiRecorder.
        /// </summary>
        public static ModResult BuildMod(RimeUiService p_Rime, IReadOnlyList<string> p_MovieResources, string p_ModsRoot, string p_WorkRoot, TextWriter p_Log, bool p_Build)
        {
            if (!Available) throw new Exception("the compiled recorder (Data/UiRecorder.avm1) is not next to the editor");
            var s_Resources = p_MovieResources.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (s_Resources.Count == 0) throw new Exception("no screen movie to record");
            var s_Screens = s_Resources.Select(r => r.Split('/').Last()).ToList();
            var s_ScreenList = string.Join(", ", s_Screens);
            var s_ModDir = Path.Combine(p_ModsRoot, ModName);
            var s_Work = Path.Combine(p_WorkRoot, ModName);
            Directory.CreateDirectory(s_Work);
            Directory.CreateDirectory(Path.Combine(s_ModDir, "ext", "Shared"));
            Directory.CreateDirectory(Path.Combine(s_ModDir, "ext", "Client"));
            Directory.CreateDirectory(Path.Combine(s_ModDir, "ext", "Server"));
            var s_SbDir = Path.Combine(s_ModDir, "sb");
            Directory.CreateDirectory(s_SbDir);

            var s_MoviePaths = new List<string>();
            foreach (var (s_Resource, s_Screen) in s_Resources.Zip(s_Screens))
            {
                var s_Movie = GfxMovie.Load(p_Rime.ResourceBytes(s_Resource));
                var s_Root = RootClipName(s_Movie);
                Inject(s_Movie, s_Screen, s_Root);
                var s_MoviePath = Path.Combine(s_Work, s_Screen + ".gfx");
                s_Movie.Save(s_MoviePath);
                s_MoviePaths.Add(s_MoviePath);
                p_Log.WriteLine($"{s_MoviePath}: the shipped movie with the recorder in front of its frame (root clip {s_Root}, {Action().Length} bytes of script)");
            }

            WriteLf(Path.Combine(s_ModDir, "ext", "Shared", "__init__.lua"), LuaTemplates.Shared.Replace("{{MOD}}", ModName).Replace("{{SUPERBUNDLE}}", Superbundle).Replace("{{BUNDLE}}", Bundle));
            WriteLf(Path.Combine(s_ModDir, "ext", "Client", "__init__.lua"), c_Receiver.Replace("{{MOD}}", ModName).Replace("{{SCREEN}}", s_ScreenList));
            WriteLf(Path.Combine(s_ModDir, "ext", "Server", "__init__.lua"), c_Server.Replace("{{MOD}}", ModName).Replace("{{SCREEN}}", s_ScreenList));
            var s_ModJson = new JObject
            {
                ["Name"] = ModName,
                ["Authors"] = new JArray("keku"),
                ["Description"] = $"Rime UI editor: records what the game's UI system hands the screen{(s_Screens.Count > 1 ? "s" : "")} {s_ScreenList} (the widgets' init data and every data refresh): the client console shows it as [AS2] #R… lines, and F5 (or the console command uirecdump) sends it to the server, which keeps it in this mod's database (mod.db, table uirec) for Import recording… in the editor. Ships the screens' shipped movies with the recorder in front of their frame; nothing else changes. Disable any other mod that ships one of these screens while recording.",
                ["URL"] = "none",
                ["Version"] = "1.0.0",
                ["HasWebUI"] = false,
                ["HasVeniceEXT"] = true,
                ["Superbundles"] = new JArray("Win32/" + Superbundle),
                ["Tags"] = new JArray("gameplay"),
                ["Dependencies"] = new JObject { ["veniceext"] = "^1.0.0" },
            };
            WriteLf(Path.Combine(s_ModDir, "mod.json"), s_ModJson.ToString(Formatting.Indented) + "\n");

            var s_Recipe = new List<string>
            {
                $"build_sb Win32/{Superbundle} Frostbite2_0 \"{s_SbDir}\"",
                $"build_bundle Win32/{Bundle}",
            };
            foreach (var (s_Resource, s_MoviePath) in s_Resources.Zip(s_MoviePaths))
                s_Recipe.Add($"replace_resource {s_Resource} 1 \"{s_MoviePath}\"");
            s_Recipe.Add("build");
            s_Recipe.Add("build");
            WriteLf(Path.Combine(s_Work, "rime_build.txt"), string.Join("\n", s_Recipe) + "\n");
            var s_SbPath = Path.Combine(s_SbDir, "Win32", Superbundle.Replace('/', Path.DirectorySeparatorChar) + ".sb");
            if (p_Build) p_Rime.RunRecipe(s_Recipe, p_Log);
            p_Log.WriteLine($"mod folder written: {s_ModDir} (mod.json, ext/, sb/; {s_Screens.Count} screen movie(s): {s_ScreenList}){(p_Build ? "" : " — superbundle not built (no game mounted)")}");
            return new ModResult(s_ModDir, s_Work, s_MoviePaths[0], s_SbPath, s_Recipe, p_Build, s_MoviePaths);
        }

        static void WriteLf(string p_Path, string p_Text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p_Path)!);
            File.WriteAllText(p_Path, p_Text.Replace("\r\n", "\n"), new UTF8Encoding(false));
        }

        // ------------------------------------------------------------------------------------------ recordings

        /// <summary>Where the preview keeps the fixture of a screen: &lt;preview root&gt;/data/&lt;screen&gt;.json.</summary>
        public static string FixturePath(string p_PreviewRoot, string p_Screen) => Path.Combine(p_PreviewRoot, "data", p_Screen.Split('/').Last() + ".json");

        static readonly Regex s_Frame = new(@"#R(?<rid>\d+\.[a-z]\d+)\|(?<seq>\d+)\|(?<total>\d+)\|(?<len>\d+)\|", RegexOptions.Compiled);
        static readonly Regex s_Whole = new(@"\[UIREC\] (?<rid>\d+\.[a-z]\d+) (?<json>\{[^\r\n]*)", RegexOptions.Compiled);

        /// <summary>What an import saw, for the log.</summary>
        public sealed class ImportReport
        {
            public int Frames, Truncated, WholeLines, Complete;
            public List<string> Incomplete = new();
            public List<int> Runs = new();
            public List<string> Notes = new();
            public override string ToString() =>
                $"{Frames} chunk frame(s){(Truncated > 0 ? $" ({Truncated} cut short)" : "")}, {WholeLines} whole line(s), {Complete} record(s) complete" +
                (Incomplete.Count > 0 ? $", incomplete: {string.Join(", ", Incomplete)}" : "") + (Runs.Count > 0 ? $"; runs {string.Join(", ", Runs)}" : "") +
                (Notes.Count > 0 ? "; " + string.Join("; ", Notes) : "");
        }

        /// <summary>
        /// Reads a copied client console (or any text holding the "[AS2] #R…" chunk lines and/or the "[UIREC] &lt;id&gt; {…}" whole lines
        /// the receiver prints) into a recording: the latest run that has an init record, with that run's refreshes and every
        /// captured component answer. Null when no complete init record is in the text.
        /// </summary>
        public static Recording? Parse(string p_Text, ImportReport p_Report) => ParseAll(p_Text, p_Report).FirstOrDefault();

        /// <summary>
        /// Every screen recorded in the text, one recording each: a recorder mod that ships several screens (a walk through the
        /// menus) writes them all into one database, each record naming its screen and the run counter shared by the session.
        /// Per screen the latest run with an init record is kept, with that run's refreshes. The screen recorded last comes first.
        /// </summary>
        public static List<Recording> ParseAll(string p_Text, ImportReport p_Report)
        {
            var s_Records = ParseRecords(p_Text, p_Report);
            var s_Out = new List<Recording>();
            var s_Inits = s_Records.Values.Where(r => (string?)r["kind"] == "init").ToList();
            p_Report.Runs = s_Records.Values.Select(r => (int?)r["run"] ?? 0).Distinct().OrderBy(r => r).ToList();
            if (s_Inits.Count == 0) { p_Report.Notes.Add("no complete init record: the screen's initializeScreen was not captured"); return s_Out; }
            foreach (var s_Group in s_Inits.GroupBy(r => (string?)r["screen"] ?? "").OrderByDescending(g => g.Max(r => (int?)r["run"] ?? 0)))
            {
                var s_Rec = Build(s_Records, s_Group.ToList());
                if (s_Group.Count() > 1) p_Report.Notes.Add($"{s_Rec.Screen}: {s_Group.Count()} init records, run {s_Rec.Run} kept (the latest)");
                s_Out.Add(s_Rec);
            }
            return s_Out;
        }

        /// <summary>The recording of one screen from its init records (the latest run wins) and that run's refreshes.</summary>
        static Recording Build(Dictionary<string, JObject> p_Records, List<JObject> p_Inits)
        {
            var s_Init = p_Inits.OrderByDescending(r => (int?)r["run"] ?? 0).ThenByDescending(r => (int?)r["seq"] ?? 0).First();
            var s_Run = (int?)s_Init["run"] ?? 0;
            var s_Rec = new Recording
            {
                Screen = (string?)s_Init["screen"] ?? "",
                Root = (string?)s_Init["root"] ?? "instance1",
                Run = s_Run,
                Captured = (bool?)s_Init["captured"] == true,
                ImportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            };
            s_Rec.Widgets = WidgetList(s_Init["widgets"]);
            foreach (var p in (s_Init["getData"] as JObject ?? new JObject()).Properties()) s_Rec.GetData[p.Name] = p.Value.DeepClone();
            foreach (var p in (s_Init["texts"] as JObject ?? new JObject()).Properties()) s_Rec.Texts[p.Name] = p.Value.DeepClone();
            foreach (var r in p_Records.Values.Where(r => (string?)r["kind"] == "refresh" && ((int?)r["run"] ?? 0) == s_Run).OrderBy(r => (int?)r["seq"] ?? 0))
            {
                s_Rec.Refreshes.Add(new JObject
                {
                    ["seq"] = (int?)r["seq"] ?? 0,
                    ["list"] = new JArray(WidgetList(r["list"])),
                    ["getData"] = r["getData"]?.DeepClone() ?? new JObject(),
                    ["texts"] = r["texts"]?.DeepClone() ?? new JObject(),
                });
                foreach (var p in (r["getData"] as JObject ?? new JObject()).Properties()) s_Rec.GetData[p.Name] = p.Value.DeepClone();
                foreach (var p in (r["texts"] as JObject ?? new JObject()).Properties()) s_Rec.Texts[p.Name] = p.Value.DeepClone();
            }
            // the traffic after the init, in order: every "event" record of the run holds a batch of {t, w, m, a, r}
            foreach (var r in p_Records.Values.Where(r => (string?)r["kind"] == "event" && ((int?)r["run"] ?? 0) == s_Run).OrderBy(r => (int?)r["seq"] ?? 0))
                s_Rec.Events.AddRange((r["events"] as JArray ?? new JArray()).OfType<JObject>().Select(e => (JObject)e.DeepClone()));
            return s_Rec;
        }

        /// <summary>The complete records in the text by id: whole lines first, then the chunk frames reassembled.</summary>
        static Dictionary<string, JObject> ParseRecords(string p_Text, ImportReport p_Report)
        {
            var s_Records = new Dictionary<string, JObject>(StringComparer.Ordinal);
            // whole lines first: a complete record in one line needs no reassembly
            foreach (Match m in s_Whole.Matches(p_Text))
            {
                ++p_Report.WholeLines;
                try { s_Records[m.Groups["rid"].Value] = JObject.Parse(m.Groups["json"].Value); }
                catch { p_Report.Notes.Add($"whole line {m.Groups["rid"].Value} is not valid JSON (cut by the console?) — its chunks are used instead"); }
            }
            var s_Chunks = new Dictionary<string, (int Total, SortedDictionary<int, string> Parts)>(StringComparer.Ordinal);
            foreach (Match m in s_Frame.Matches(p_Text))
            {
                ++p_Report.Frames;
                var s_Rid = m.Groups["rid"].Value;
                var s_Seq = int.Parse(m.Groups["seq"].Value, CultureInfo.InvariantCulture);
                var s_Total = int.Parse(m.Groups["total"].Value, CultureInfo.InvariantCulture);
                var s_Len = int.Parse(m.Groups["len"].Value, CultureInfo.InvariantCulture);
                var s_Start = m.Index + m.Length;
                // the record is known from its header even when this chunk is cut short (a console line lost its tail): it counts as incomplete below
                if (!s_Chunks.TryGetValue(s_Rid, out var s_Entry)) s_Chunks[s_Rid] = s_Entry = (s_Total, new SortedDictionary<int, string>());
                if (s_Start + s_Len > p_Text.Length) { ++p_Report.Truncated; continue; }
                var s_Payload = p_Text.Substring(s_Start, s_Len);
                if (s_Payload.Contains('\n') || s_Payload.Contains('\r')) { ++p_Report.Truncated; continue; }
                s_Entry.Parts[s_Seq] = s_Payload;
            }
            foreach (var (s_Rid, (s_Total, s_Parts)) in s_Chunks)
            {
                if (s_Records.ContainsKey(s_Rid)) continue;
                if (s_Parts.Count < s_Total || Enumerable.Range(1, s_Total).Any(i => !s_Parts.ContainsKey(i))) { p_Report.Incomplete.Add($"{s_Rid} ({s_Parts.Count}/{s_Total})"); continue; }
                var s_Json = string.Concat(Enumerable.Range(1, s_Total).Select(i => s_Parts[i]));
                try { s_Records[s_Rid] = JObject.Parse(s_Json); }
                catch (Exception s_Ex) { p_Report.Incomplete.Add($"{s_Rid} (joined text is not JSON: {s_Ex.Message})"); }
            }
            p_Report.Complete = s_Records.Count;
            return s_Records;
        }

        /// <summary>The records of an array the game passed: a real array, or an object with a length and numbered keys (ObjectToArray's shape).</summary>
        static List<JObject> WidgetList(JToken? p_Array)
        {
            var s_List = new List<JObject>();
            if (p_Array is JArray a) { s_List.AddRange(a.OfType<JObject>()); return s_List; }
            if (p_Array is JObject o)
            {
                var s_Length = (int?)o["length"] ?? 0;
                for (var i = 0; i < s_Length; ++i) if (o[i.ToString(CultureInfo.InvariantCulture)] is JObject e) s_List.Add(e);
                if (s_Length == 0) s_List.AddRange(o.Properties().Where(p => int.TryParse(p.Name, out _)).OrderBy(p => int.Parse(p.Name, CultureInfo.InvariantCulture)).Select(p => p.Value).OfType<JObject>());
            }
            return s_List;
        }

        /// <summary>Imports a copied console into the fixture of its screen under the preview root. Returns the recording and where it went; null when nothing complete was found.</summary>
        public static (Recording? Recording, string? Path) Import(string p_Text, string p_PreviewRoot, ImportReport p_Report)
        {
            var s_All = ImportAll(p_Text, p_PreviewRoot, p_Report);
            return s_All.Count > 0 ? s_All[0] : (null, null);
        }

        /// <summary>Imports every screen recorded in the text, each into its own fixture under the preview root: the recordings and where they went.</summary>
        public static List<(Recording Recording, string Path)> ImportAll(string p_Text, string p_PreviewRoot, ImportReport p_Report)
        {
            var s_Out = new List<(Recording, string)>();
            foreach (var s_Rec in ParseAll(p_Text, p_Report))
            {
                if (s_Rec.Screen == "") { p_Report.Notes.Add("a record names no screen — skipped"); continue; }
                var s_Path = FixturePath(p_PreviewRoot, s_Rec.Screen);
                s_Rec.Save(s_Path);
                s_Out.Add((s_Rec, s_Path));
            }
            return s_Out;
        }

        /// <summary>A screen's data as the game handed it over once: the init record of every widget, the answers the components gave, the refreshes that followed.</summary>
        public sealed class Recording
        {
            public string Screen = "";
            public string Root = "instance1";
            public int Run;
            public bool Captured;
            public string ImportedAt = "";
            /// <summary>The init records, one per widget, exactly as passed to initializeScreen (InstanceName inside each).</summary>
            public List<JObject> Widgets = new();
            /// <summary>Data key → the engine's getData answer ({value: …}) while the screen initialised or refreshed.</summary>
            public JObject GetData = new();
            /// <summary>"id" or "id,param,…" → the engine's formatString answer.</summary>
            public JObject Texts = new();
            /// <summary>Every refreshScreen after the init, in order: {seq, list: [{widgetName, widgetData}], getData, texts}.</summary>
            public List<JObject> Refreshes = new();
            /// <summary>
            /// The traffic between the engine and the widgets after the init, in order: {t: ms since the movie started, w: the widget's
            /// instance name ("" = the screen), m: the method — onInputConceptPressed/Released(concept), onFocused(initialising)/onUnfocused,
            /// handleInEvent(event, param), requestFocus, fireEvent(event id, param, param2), exitScreen — a: its arguments, r: what it returned}.
            /// </summary>
            public List<JObject> Events = new();

            public JObject? InitRecord(string p_InstanceName) => Widgets.FirstOrDefault(w => (string?)w["InstanceName"] == p_InstanceName);
            public IEnumerable<string> InstanceNames => Widgets.Select(w => (string?)w["InstanceName"] ?? "").Where(n => n != "");

            /// <summary>The latest refreshed payload the game sent a widget on a data channel ("KitSelector_05", "Setup"), or null.</summary>
            public JToken? RefreshPayload(string p_Widget, string p_DataName)
            {
                for (var i = Refreshes.Count - 1; i >= 0; --i)
                    foreach (var e in (Refreshes[i]["list"] as JArray ?? new JArray()).OfType<JObject>())
                        if ((string?)e["widgetName"] == p_Widget && e["widgetData"] is JObject d && d[p_DataName] is { } v && v.Type != JTokenType.Null) return v;
                return null;
            }

            /// <summary>The value the engine answered for a data key, or null (the answer is {value: …}).</summary>
            public JToken? DataValue(string p_Key) => GetData[p_Key] is JObject o && o["value"] is { } v && v.Type != JTokenType.Null ? v : null;

            public JObject ToJson() => new()
            {
                ["screen"] = Screen, ["root"] = Root, ["run"] = Run, ["captured"] = Captured, ["importedAt"] = ImportedAt,
                ["widgets"] = new JArray(Widgets), ["getData"] = GetData, ["texts"] = Texts, ["refreshes"] = new JArray(Refreshes), ["events"] = new JArray(Events),
            };

            public static Recording FromJson(JObject j) => new()
            {
                Screen = (string?)j["screen"] ?? "", Root = (string?)j["root"] ?? "instance1", Run = (int?)j["run"] ?? 0, Captured = (bool?)j["captured"] == true,
                ImportedAt = (string?)j["importedAt"] ?? "",
                Widgets = (j["widgets"] as JArray ?? new JArray()).OfType<JObject>().ToList(),
                GetData = j["getData"] as JObject ?? new JObject(), Texts = j["texts"] as JObject ?? new JObject(),
                Refreshes = (j["refreshes"] as JArray ?? new JArray()).OfType<JObject>().ToList(),
                Events = (j["events"] as JArray ?? new JArray()).OfType<JObject>().ToList(),
            };

            public void Save(string p_Path)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(p_Path)!);
                File.WriteAllText(p_Path, ToJson().ToString(Formatting.Indented), new UTF8Encoding(false));
            }

            /// <summary>The fixture at a path, or null when there is none or it does not parse.</summary>
            public static Recording? Load(string p_Path)
            {
                try { return File.Exists(p_Path) ? FromJson(JObject.Parse(File.ReadAllText(p_Path))) : null; }
                catch { return null; }
            }

            public string Summary => $"run {Run}, {Widgets.Count} widgets ({string.Join(", ", InstanceNames)}), {GetData.Count} data answers, {Texts.Count} texts, {Refreshes.Count} refreshes, {Events.Count} events{(Captured ? "" : " (component calls not captured)")}";

            /// <summary>The events of one method, or of every method when p_Method is null: (widget, arguments) in order.</summary>
            public IEnumerable<(string Widget, JArray Args, JToken? Result)> EventsOf(string? p_Method) =>
                Events.Where(e => p_Method == null || (string?)e["m"] == p_Method).Select(e => ((string?)e["w"] ?? "", e["a"] as JArray ?? new JArray(), e["r"]));
        }

        // ------------------------------------------------------------------------------------------ the receiver

        /// <summary>ext/Client of the recorder mod: the AS2 log channel decoder (bits over UI:EnableTypingMode) plus the reassembly of the recorder's chunks.</summary>
        const string c_Receiver = @"-- {{MOD}} -- the receiver (ext/Client). Written by the Rime UI editor (Record data…); do not edit.
--
-- The screen movie this mod ships ({{SCREEN}}) carries a frame action that wraps initializeScreen and refreshScreen on
-- the screen's own clip and sends what the game's UI system handed it as BITS over requestTypingInput(bool): the call the
-- vanilla chat box makes, which lands in the client hook UI:EnableTypingMode. Frame: 0x55 sync | len | bytes | xor(len,
-- bytes), MSB first, one call per bit, all in one synchronous burst; a real chat toggle is an isolated call and is passed
-- on to the engine, our frames are swallowed (hook:Return()).
--
-- What to read in the client console, then copy the whole console into the editor's Import recording…:
--   [AS2] #R<run>.<kind><seq>|<chunk>|<chunks>|<len>|<payload>   one chunk of a record (the importer reassembles these)
--   [UIREC] <run>.<kind><seq> {…}                                 the same record joined here, on one line
--   [UIREC] record <id> complete (N chunks, M bytes)              the count that tells you the copy is worth making
--   [AS2CHAN] frame BAD … / PARTIAL …                             the bridge works, a frame did not decode (copy anyway)
-- Kinds: h = the screen movie ran the recorder (hello), i = initializeScreen (the widgets' init data), r = refreshScreen.

local SYNC = 0x55
local BURST_GAP_MS = 60
local STALE_MS = 500

local m_Bits = {}
local m_LastMs = 0
local m_Passthrough = false
local m_JustCompleted = false
local m_Stats = { calls = 0, frames = 0, bad = 0, passed = 0, dropped = 0, records = 0 }
local m_Records = {}   -- id -> { total = n, parts = { [seq] = payload }, count = k, done = bool }
local m_Done = {}      -- the records decoded whole, in order: what F5 sends to the server for the mod's database

local function LOG(p_Text)
	print('[AS2CHAN] ' .. tostring(p_Text))
end

local function BitsToBytes(p_Bits)
	local s_Bytes = {}
	for i = 1, #p_Bits - 7, 8 do
		local l_Value = 0
		for k = 0, 7 do
			l_Value = l_Value * 2 + p_Bits[i + k]
		end
		s_Bytes[#s_Bytes + 1] = l_Value
	end
	return s_Bytes
end

local function Hex(p_Bytes)
	local s_Out = {}
	for _, l_Byte in ipairs(p_Bytes) do
		s_Out[#s_Out + 1] = string.format('%02X', l_Byte)
	end
	return table.concat(s_Out, ' ')
end

local function BitString(p_Bits)
	local s_Out = {}
	for i, l_Bit in ipairs(p_Bits) do
		s_Out[#s_Out + 1] = tostring(l_Bit)
		if i % 8 == 0 then
			s_Out[#s_Out + 1] = ' '
		end
	end
	return table.concat(s_Out)
end

local function ExpectedBits(p_Bits)
	if #p_Bits < 16 then
		return nil
	end
	local s_Len = 0
	for k = 9, 16 do
		s_Len = s_Len * 2 + p_Bits[k]
	end
	return 8 * (3 + s_Len)
end

local function Xor8(a, b)
	local s_Result = 0
	local s_Mask = 1
	for _ = 1, 8 do
		local l_A = a % 2
		local l_B = b % 2
		if l_A ~= l_B then
			s_Result = s_Result + s_Mask
		end
		a = (a - l_A) / 2
		b = (b - l_B) / 2
		s_Mask = s_Mask * 2
	end
	return s_Result
end

local function DecodeFrame(p_Bits)
	local s_Bytes = BitsToBytes(p_Bits)
	if #s_Bytes < 3 or s_Bytes[1] ~= SYNC then
		return nil, 'sync', s_Bytes
	end
	local s_Len = s_Bytes[2]
	if #s_Bytes ~= s_Len + 3 then
		return nil, 'length', s_Bytes
	end
	local s_Xor = s_Len
	local s_Chars = {}
	for i = 1, s_Len do
		local l_Byte = s_Bytes[2 + i]
		s_Xor = Xor8(s_Xor, l_Byte)
		s_Chars[#s_Chars + 1] = string.char(l_Byte)
	end
	if s_Xor ~= s_Bytes[#s_Bytes] then
		return nil, string.format('checksum (got %02X want %02X)', s_Bytes[#s_Bytes], s_Xor), s_Bytes
	end
	return table.concat(s_Chars), nil, s_Bytes
end

-- A decoded frame: a chunk of a record is kept until every chunk of that record is in, then the record prints whole.
local function OnText(p_Text)
	print('[AS2] ' .. p_Text)
	-- every mod hears a decoded frame (the document's client scripts listen for theirs; this receiver stands in for the channel's)
	Events:Dispatch('AS2:Frame', p_Text)
	local s_Id, s_Seq, s_Total, s_Len, s_Payload = string.match(p_Text, '^#R([%w%.]+)|(%d+)|(%d+)|(%d+)|(.*)$')
	if s_Id == nil then
		return
	end
	s_Seq = tonumber(s_Seq)
	s_Total = tonumber(s_Total)
	local s_Record = m_Records[s_Id]
	if s_Record == nil then
		s_Record = { total = s_Total, parts = {}, count = 0, done = false }
		m_Records[s_Id] = s_Record
	end
	if s_Record.parts[s_Seq] == nil then
		s_Record.parts[s_Seq] = s_Payload
		s_Record.count = s_Record.count + 1
	end
	if not s_Record.done and s_Record.count >= s_Record.total then
		local s_Joined = {}
		for i = 1, s_Record.total do
			s_Joined[#s_Joined + 1] = s_Record.parts[i] or ''
		end
		local s_Json = table.concat(s_Joined)
		s_Record.done = true
		m_Stats.records = m_Stats.records + 1
		m_Done[#m_Done + 1] = { id = s_Id, json = s_Json }
		print('[UIREC] ' .. s_Id .. ' ' .. s_Json)
		print(string.format('[UIREC] record %s complete (%d chunks, %d bytes)', s_Id, s_Record.total, #s_Json))
	end
end

local function Reset()
	m_Bits = {}
	m_Passthrough = false
end

local function Flush(p_Why)
	if #m_Bits == 0 then
		return
	end
	if #m_Bits == 1 then
		LOG(string.format('isolated call typing=%s passed to the engine (%s)', tostring(m_Bits[1] == 1), p_Why))
	elseif m_Passthrough then
		LOG(string.format('non-protocol burst of %d calls, all passed (%s)', #m_Bits, p_Why))
	else
		m_Stats.dropped = m_Stats.dropped + 1
		LOG(string.format('PARTIAL frame dropped after %d bits (%s): %s', #m_Bits, p_Why, BitString(m_Bits)))
	end
	Reset()
end

Hooks:Install('UI:EnableTypingMode', 1, function(p_Hook, p_Enable)
	m_Stats.calls = m_Stats.calls + 1
	local s_Now = SharedUtils:GetTimeMS()
	if m_JustCompleted then
		m_JustCompleted = false
		if #m_Bits == 0 and (s_Now - m_LastMs) <= BURST_GAP_MS and not p_Enable then
			m_LastMs = s_Now
			p_Hook:Return()
			return
		end
	end
	if #m_Bits > 0 and (s_Now - m_LastMs) > BURST_GAP_MS then
		Flush('gap ' .. tostring(s_Now - m_LastMs) .. ' ms')
	end
	m_LastMs = s_Now
	m_Bits[#m_Bits + 1] = p_Enable and 1 or 0
	if #m_Bits == 1 then
		m_Stats.passed = m_Stats.passed + 1
		p_Hook:Pass(p_Enable)
		return
	end
	if not m_Passthrough and #m_Bits <= 8 then
		local s_Want = (#m_Bits % 2 == 0) and 1 or 0
		if m_Bits[#m_Bits] ~= s_Want then
			m_Passthrough = true
		end
	end
	if m_Passthrough then
		m_Stats.passed = m_Stats.passed + 1
		p_Hook:Pass(p_Enable)
		return
	end
	p_Hook:Return()
	local s_Expected = ExpectedBits(m_Bits)
	if s_Expected ~= nil and #m_Bits >= s_Expected then
		local s_Text, s_Error, s_Bytes = DecodeFrame(m_Bits)
		if s_Text ~= nil then
			m_Stats.frames = m_Stats.frames + 1
			OnText(s_Text)
		else
			m_Stats.bad = m_Stats.bad + 1
			LOG(string.format('frame BAD (%s) raw=%s', tostring(s_Error), Hex(s_Bytes)))
		end
		Reset()
		m_JustCompleted = true
	end
end)

Events:Subscribe('Engine:Update', function()
	if #m_Bits > 0 and (SharedUtils:GetTimeMS() - m_LastMs) > STALE_MS then
		Flush('stale')
	end
end)

Events:Subscribe('Level:Loaded', function()
	print('[UIREC] armed for {{SCREEN}}: open that screen in the game, wait for ""[UIREC] record N.i0 complete"", then press F5 (or type uirecdump): the records go to the mod database, Admin/Mods/{{MOD}}/mod.db')
end)

Console:Register('uirec', 'UI recorder stats', function()
	return string.format('calls=%d frames=%d bad=%d passed=%d dropped=%d records=%d pending=%d decoded=%d',
		m_Stats.calls, m_Stats.frames, m_Stats.bad, m_Stats.passed, m_Stats.dropped, m_Stats.records, #m_Bits, #m_Done)
end)

-- F5 (or the console command uirecdump): every record decoded so far goes to the server in pieces and the server keeps it in the
-- mod's own database (Admin/Mods/{{MOD}}/mod.db, table uirec) — the console's scrollback limit does not apply to it; the editor's
-- Import recording… reads the text External/keku/uirec_db.py makes from that file
local DUMP_CHUNK = 900

local function Dump(p_Why)
	if #m_Done == 0 then
		print('[UIREC] ' .. p_Why .. ': nothing decoded yet (open the screen first, wait for a record to complete)')
		return
	end
	local s_Pieces = 0
	for _, l_Record in ipairs(m_Done) do
		local l_Total = math.ceil(#l_Record.json / DUMP_CHUNK)
		if l_Total < 1 then
			l_Total = 1
		end
		for i = 1, l_Total do
			NetEvents:Send('{{MOD}}:Record', l_Record.id, i, l_Total, string.sub(l_Record.json, (i - 1) * DUMP_CHUNK + 1, i * DUMP_CHUNK))
			s_Pieces = s_Pieces + 1
		end
	end
	NetEvents:Send('{{MOD}}:Flush', #m_Done)
	print(string.format('[UIREC] %s: %d record(s) sent to the server in %d piece(s); the server answers when mod.db has them', p_Why, #m_Done, s_Pieces))
end

Events:Subscribe('Client:UpdateInput', function()
	if InputManager:WentKeyDown(InputDeviceKeys.IDK_F5) then
		Dump('F5')
	end
end)

NetEvents:Subscribe('{{MOD}}:Saved', function(p_Count, p_Bytes, p_Error)
	if p_Error ~= nil and p_Error ~= '' then
		print('[UIREC] the server could not save: ' .. tostring(p_Error))
	else
		print(string.format('[UIREC] saved: %d record(s), %d bytes, in Admin/Mods/{{MOD}}/mod.db (table uirec) — the editor imports it from there', tonumber(p_Count) or 0, tonumber(p_Bytes) or 0))
	end
end)

Console:Register('uirecdump', 'send the decoded records to the server (mod.db)', function()
	Dump('uirecdump')
	return 'sent'
end)
";

        /// <summary>ext/Server of the recorder mod: keeps what the client decoded in the mod's database (the console's scrollback limit does not apply).</summary>
        const string c_Server = @"-- {{MOD}} -- ext/Server. Written by the Rime UI editor (Record data…); do not edit.
--
-- Keeps what the client decoded (F5 / uirecdump on the client) in the mod's own database, Admin/Mods/{{MOD}}/mod.db, table uirec
-- (id, run, kind, seq, savedAt, json). A record arrives in pieces ({{MOD}}:Record id part total piece), the client's {{MOD}}:Flush
-- closes the batch: every whole record is written with INSERT OR REPLACE (a second F5 rewrites the same ids) and the client gets
-- {{MOD}}:Saved back. External/keku/uirec_db.py turns the table into the lines the editor's Import recording… reads.

local m_Pending = {}   -- id -> { total = n, parts = { [part] = piece }, count = k }

local function LOG(p_Text)
	print('[UIREC/srv] ' .. tostring(p_Text))
end

local function Save(p_Records)
	if not SQL:Open() then
		return 0, 0, 'SQL:Open failed: ' .. tostring(SQL:Error())
	end
	local s_Count, s_Bytes = 0, 0
	local s_Error = nil
	if not SQL:Query('CREATE TABLE IF NOT EXISTS uirec (id TEXT PRIMARY KEY, run INTEGER, kind TEXT, seq INTEGER, savedAt INTEGER, json TEXT)') then
		s_Error = 'create table: ' .. tostring(SQL:Error())
	else
		for _, l_Record in ipairs(p_Records) do
			local l_Run, l_Kind, l_Seq = string.match(l_Record.id, '^(%d+)%.(%a)(%d+)$')
			if SQL:Query('INSERT OR REPLACE INTO uirec (id, run, kind, seq, savedAt, json) VALUES (?, ?, ?, ?, ?, ?)', l_Record.id, tonumber(l_Run) or 0, l_Kind or '', tonumber(l_Seq) or 0, SharedUtils:GetTimeMS(), l_Record.json) then
				s_Count = s_Count + 1
				s_Bytes = s_Bytes + #l_Record.json
			else
				s_Error = 'insert ' .. l_Record.id .. ': ' .. tostring(SQL:Error())
			end
		end
	end
	SQL:Close()
	return s_Count, s_Bytes, s_Error
end

NetEvents:Subscribe('{{MOD}}:Record', function(p_Player, p_Id, p_Part, p_Total, p_Piece)
	local s_Id = tostring(p_Id)
	local s_Record = m_Pending[s_Id]
	if s_Record == nil then
		s_Record = { total = tonumber(p_Total) or 1, parts = {}, count = 0 }
		m_Pending[s_Id] = s_Record
	end
	local s_Part = tonumber(p_Part) or 1
	if s_Record.parts[s_Part] == nil then
		s_Record.parts[s_Part] = tostring(p_Piece)
		s_Record.count = s_Record.count + 1
	end
end)

NetEvents:Subscribe('{{MOD}}:Flush', function(p_Player, p_Expected)
	local s_Whole = {}
	for l_Id, l_Record in pairs(m_Pending) do
		if l_Record.count >= l_Record.total then
			local l_Joined = {}
			for i = 1, l_Record.total do
				l_Joined[#l_Joined + 1] = l_Record.parts[i] or ''
			end
			s_Whole[#s_Whole + 1] = { id = l_Id, json = table.concat(l_Joined) }
		end
	end
	m_Pending = {}
	local s_Count, s_Bytes, s_Error = Save(s_Whole)
	local s_Who = '?'
	if p_Player ~= nil then
		s_Who = tostring(p_Player.name)
	end
	LOG(string.format('%s: %d of %s record(s) whole, %d saved (%d bytes) in mod.db%s', s_Who, #s_Whole, tostring(p_Expected), s_Count, s_Bytes, s_Error ~= nil and (' -- ' .. s_Error) or ''))
	if p_Player ~= nil then
		NetEvents:SendTo('{{MOD}}:Saved', p_Player, s_Count, s_Bytes, s_Error or '')
	end
end)
";
    }
}
