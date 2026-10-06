using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimeLib.Cmd.UiBuilder;
using RimeUIEditor.Model;
using RimeUIEditor.View;

namespace RimeUIEditor
{
    /// <summary>
    /// The engine's part of the UI, done by the editor for the live preview: what the game's UI system does around the movies.
    ///
    /// A screen on the stack gets, when it reports itself loaded, the screen's own initializeScreen with one record per widget the
    /// way the engine builds them (InstanceName, NumEvents and Event_i = the UIWidgetEventID values of its contract, ZDepthLevel,
    /// its WidgetProperties, data: the static texts of its binding and the data keys the host holds) — or, when a recording of the
    /// screen exists (Record data… in the game, Import recording…), the records the game itself handed over. Then the screen is
    /// entered through the flow graph: the StateNode that shows it in its parent graph (found among the game's UIGraphAssets)
    /// fires the screen's InstanceInputNode, and when every widget reports its enter complete the StateNode's Initialized output
    /// fires.
    ///
    /// Every event a widget fires (fireEvent with the event's UIWidgetEventID value) walks the screen graph from that output port:
    /// a wire into another widget becomes handleInEvent on it, a DataSetNode writes the host's data table (and refreshes the widgets
    /// bound to that key, with the recorded payload when there is one), a ComparisonLogicNode picks the output named by the value,
    /// a screen output (InstanceOutputNode) continues in the parent graph from the StateNode's output of that name: a wire into
    /// another StateNode pushes that screen into the player (converted on the spot, loaded as a new clip, initialised, entered),
    /// a DialogNode pushes its popup screen with the dialog's title, text and buttons as data, a wire's NumScreensToPop pops that
    /// many screens first, an ActionNode is logged under its UIAction name (the engine's actions have no stand-in outside the
    /// game). Back (the controller's Deactivate/Back, Esc in the game) fires the StateNode's input-event output of that name.
    /// Every step shows in the editor's log as [host] lines.
    /// </summary>
    public sealed class UiHost
    {
        /// <summary>What the host needs from the editor to load, convert and feed other screens than the first.</summary>
        public sealed record Services(
            Func<string, ScreenModel?> Model,
            Func<string, ScreenEntry?> Doc,
            Func<string, DataRecorder.Recording?> Recording,
            Func<string, ScreenEntry?, bool> Convert,
            Func<string, JObject?> PartitionJson,
            Func<IEnumerable<string>> FlowGraphs,
            Func<string, bool> TextureExists,
            Func<string, string, (JToken? Value, string How)> Generate,
            Func<string, UiHost, bool>? Action = null,
            Func<string, object?, UiHost, bool>? DataSet = null);

        /// <summary>A screen on the player's stack: its clip under the shell root, its graph, and what the flow graph knows about it.</summary>
        sealed class ScreenRun
        {
            public string Clip = "";
            public string Root = "instance1";
            public ScreenModel Model = null!;
            public ScreenEntry? Doc;
            public DataRecorder.Recording? Recording;
            public NodeInfo? StateNode;      // in the flow graph
            public NodeEntry? DocStateNode;  // or the document's own StateNode that pushed it (a screen the document adds to the flow graph): its ports and wires are the document's
            public string? Entry;            // the StateNode input port the push came through (the screen's InstanceInputNode of that name)
            public JToken? EntryParam;
            public bool Initialised;
            public int Widgets, Recorded;
            public string? Focused;          // the widget that last asked for the focus (its FocusIndex is the group that holds it)
            public int FocusGroup;           // the focus group the engine holds: the widgets whose FocusIndex is this one are focused (0 when the screen comes up)
            public string Screen => Model.Partition.Split('/').Last();
        }

        /// <summary>Where a graph walk happens: a screen (with its clip) or the flow graph (no clip).</summary>
        sealed record Scope(ScreenModel Model, ScreenEntry? Doc, ScreenRun? Run);

        readonly WidgetCatalog? m_Catalog;
        readonly Func<string, string?> m_PartitionByGuid;
        readonly RuffleHostWindow m_Window;
        readonly TextDatabase? m_Texts;
        readonly Action<string> m_Log;
        readonly Services? m_Services;
        readonly string[] m_EventNames = UiTypeCatalog.EnumMembers("UIWidgetEventID");
        readonly Dictionary<int, string> m_ActionNames = new();
        readonly List<ScreenRun> m_Stack = new();
        ScreenRun m_First = null!;   // the screen the preview opened with: what a reload of the page comes back to
        ScreenModel? m_Flow;
        ScreenEntry? m_FlowDoc;
        int m_NextClip = 2, m_NextDepth = 100;
        List<string>? m_DialogButtons;   // the labels of the popup on top, for the comparison its button index feeds

        /// <summary>The data components' values the host holds, by key (the engine's hash of UI_&lt;COMP&gt;_&lt;SOURCE&gt;): strings, or the values a recording captured.</summary>
        public Dictionary<string, object?> Data { get; } = new();
        /// <summary>Widget records handed to initializeScreen so far, over every screen of the stack.</summary>
        public int WidgetsInitialised { get; private set; }
        /// <summary>How many of those came from a recording (the rest are built from the graph).</summary>
        public int RecordedWidgetsUsed { get; private set; }
        public int EventsFired { get; private set; }
        public int Pushes { get; private set; }
        public int Pops { get; private set; }
        /// <summary>Textures the movies asked for through loadTextureAsync, and how many the editor could serve.</summary>
        public int TexturesAsked { get; private set; }
        public int TexturesFound { get; private set; }
        /// <summary>The recorder's echo from inside the movie: the last record it sent (init or refresh), and how many so far.</summary>
        public JObject? LastRecord { get; private set; }
        public int RecordsEchoed { get; private set; }
        /// <summary>The first screen's recording, when one was imported.</summary>
        public DataRecorder.Recording? Recording => m_Stack.Count > 0 ? m_Stack[0].Recording : null;
        public int TextsPushed { get; private set; }
        /// <summary>The flow graph the first screen runs under (its StateNode's parent), or "" when none references it.</summary>
        public string ParentGraph => m_Flow?.Partition ?? "";
        /// <summary>The screens on the player, bottom to top, as "clip:screen".</summary>
        public IReadOnlyList<string> Stack => m_Stack.Select(r => r.Clip + ":" + r.Screen).ToList();
        public string? CurrentScreen => m_Stack.LastOrDefault()?.Model.Partition;

        public UiHost(ScreenModel p_Screen, ScreenEntry? p_Doc, WidgetCatalog? p_Catalog, Func<string, string?> p_PartitionByGuid, RuffleHostWindow p_Window, TextDatabase? p_Texts, Action<string> p_Log, DataRecorder.Recording? p_Recording = null, Services? p_Services = null)
        {
            m_Catalog = p_Catalog; m_PartitionByGuid = p_PartitionByGuid; m_Window = p_Window; m_Texts = p_Texts; m_Log = p_Log; m_Services = p_Services;
            var s_First = new ScreenRun { Clip = "sc1", Model = p_Screen, Doc = p_Doc, Recording = p_Recording, Root = p_Screen.Movie != null ? DataRecorder.RootClipName(p_Screen.Movie) : "instance1" };
            m_First = s_First;
            m_Stack.Add(s_First);
            SeedData(p_Recording);
            if (p_Recording != null) m_Log($"[host] recording of {p_Recording.Screen} loaded: {p_Recording.Summary}");
            // the engine's action names by ActionKey (the game's type information has no enum for them; Data/ui_actions.json holds the
            // 180 names the shipped graphs use, as the EBX dumps annotate them)
            try
            {
                var s_Path = System.IO.Path.Combine(AppContext.BaseDirectory, "Data", "ui_actions.json");
                if (System.IO.File.Exists(s_Path))
                    foreach (var p in JObject.Parse(System.IO.File.ReadAllText(s_Path)).Properties())
                        if (int.TryParse(p.Name, out var s_Key)) m_ActionNames[s_Key] = (string?)p.Value ?? "";
            }
            catch (Exception s_Ex) { m_Log("[host] action names not loaded: " + s_Ex.Message); }
            ResolveFlow(s_First);
            m_Window.BridgeReady += OnBridgeReady;
            m_Window.Navigating += OnNavigating;
            if (m_Window.IsBridgeReady) OnBridgeReady();
        }

        /// <summary>Stops listening to the player window (a new host takes over).</summary>
        public void Detach() { m_Window.BridgeReady -= OnBridgeReady; m_Window.Navigating -= OnNavigating; }

        /// <summary>The page reloads (Reload): every clip is gone, the stack is the first screen again, waiting for its load.</summary>
        void OnNavigating()
        {
            var s_First = m_First;
            m_Stack.Clear();
            s_First.Initialised = false; s_First.Widgets = 0; s_First.Recorded = 0;
            m_Stack.Add(s_First);
            m_NextClip = 2; m_NextDepth = 100; m_DialogButtons = null;
            m_Log($"[host] page reloads: the stack is {s_First.Clip} {s_First.Screen} again");
        }

        void SeedData(DataRecorder.Recording? p_Recording)
        {
            if (p_Recording == null) return;
            foreach (var p in p_Recording.GetData.Properties())
            {
                var s_Value = p_Recording.DataValue(p.Name);
                if (s_Value != null) Data[p.Name] = s_Value;
            }
        }

        /// <summary>The bridge is up: the page gets the texts the movie's formatString answers from, and the data values held (recorded ones included).</summary>
        void OnBridgeReady()
        {
            var s_Texts = new Dictionary<string, string>();
            if (m_Texts != null) foreach (var kv in m_Texts.All) s_Texts[kv.Key.ToString()] = kv.Value;
            foreach (var r in m_Stack) if (r.Recording != null) foreach (var p in r.Recording.Texts.Properties()) if (p.Value.Type == JTokenType.String) s_Texts[p.Name] = (string)p.Value!;
            var s_Language = m_Texts?.Language switch { "us" => "en", "ge" => "de", "jp" => "ja", null or "" => "en", var l => l };
            if (s_Texts.Count > 0) { m_Window.SetTexts(s_Texts, s_Language); TextsPushed = s_Texts.Count; }
            if (Data.Count > 0) m_Window.SetData(Data);
            m_Log($"[host] bridge ready: {s_Texts.Count} texts ({s_Language}) and {Data.Count} data values pushed to the page");
        }

        // ------------------------------------------------------------------------------------------ the flow graph

        /// <summary>The parent graph of a screen: the UIGraphAsset whose StateNode (or DialogNode) shows it — the one with the most wired outputs when several do.</summary>
        void ResolveFlow(ScreenRun p_Run)
        {
            if (m_Services == null) return;
            var s_Guid = p_Run.Model.PartitionGuid;
            var s_Candidates = new List<(string Graph, int Wired)>();
            foreach (var s_Graph in m_Services.FlowGraphs())
            {
                JObject? s_Json;
                try { s_Json = m_Services.PartitionJson(s_Graph); } catch { continue; }
                if (s_Json?["Instances"] is not JObject s_Instances) continue;
                var s_Wired = 0; var s_Found = false;
                foreach (var s_Node in s_Instances.Properties().Select(p => p.Value).OfType<JObject>())
                {
                    if ((string?)s_Node["$type"] is not ("StateNode" or "DialogNode")) continue;
                    if (!string.Equals((string?)s_Node["Screen"]?["PartitionGuid"], s_Guid, StringComparison.OrdinalIgnoreCase)) continue;
                    s_Found = true;
                    s_Wired += (s_Node["Outputs"] as JArray)?.Count ?? 0;
                }
                if (s_Found) s_Candidates.Add((s_Graph, s_Wired));
            }
            if (s_Candidates.Count == 0) { m_Log($"[host] no flow graph shows {p_Run.Screen}: its outputs end here (the game would leave the screen)"); return; }
            var s_Pick = s_Candidates.OrderByDescending(c => c.Wired).ThenBy(c => c.Graph, StringComparer.Ordinal).First().Graph;
            m_Flow = m_Services.Model(s_Pick);
            m_FlowDoc = m_Services.Doc(s_Pick);
            p_Run.StateNode = FindStateNode(p_Run.Model.PartitionGuid);
            // the StateNode's named inputs are the screen's entry points (EnterScreen…); the plain In slot is the node's own
            p_Run.Entry = (p_Run.StateNode?.Ports.FirstOrDefault(p => p.IsInput && p.Field == "Inputs") ?? p_Run.StateNode?.Ports.FirstOrDefault(p => p.IsInput))?.Name;
            m_Log($"[host] flow graph: {s_Pick} ({p_Run.StateNode?.Label ?? "no StateNode?"}, entry {p_Run.Entry ?? "none"})" + (s_Candidates.Count > 1 ? $" — also shown by {string.Join(", ", s_Candidates.Where(c => c.Graph != s_Pick).Select(c => c.Graph))}" : ""));
        }

        NodeInfo? FindStateNode(string p_ScreenPartitionGuid) =>
            m_Flow?.AllNodes.FirstOrDefault(n => n.Type is "StateNode" or "DialogNode" && string.Equals((string?)n.Json["Screen"]?["PartitionGuid"], p_ScreenPartitionGuid, StringComparison.OrdinalIgnoreCase));

        Scope FlowScope() => new(m_Flow!, m_FlowDoc, null);

        // ------------------------------------------------------------------------------------------ event ids, names

        /// <summary>The UIWidgetEventID value of an event query ("OnItemOver" → its enum value), -1 when unknown.</summary>
        public int EventValue(string p_Query) => Array.FindIndex(m_EventNames, n => n == "UIWidgetEventID_" + p_Query || n == p_Query);

        /// <summary>The event query of a UIWidgetEventID value, or the number itself when out of range.</summary>
        public string EventQuery(int p_Value) => p_Value >= 0 && p_Value < m_EventNames.Length ? m_EventNames[p_Value].Replace("UIWidgetEventID_", "") : p_Value.ToString();

        string KeyName(string p_Key) =>
            m_Catalog?.Components.Values.SelectMany(c => c.Keys.Where(k => k.Value.ToString() == p_Key).Select(k => c.Name.Split('/').Last() + "." + k.Key)).FirstOrDefault() ?? ("key " + p_Key);

        string ActionName(JToken? p_Key) => p_Key != null && p_Key.Type is JTokenType.Integer or JTokenType.Float && m_ActionNames.TryGetValue((int)p_Key, out var n) ? n : "action " + p_Key;

        static string Short(JToken? t, int p_Max = 160)
        {
            if (t == null || t.Type == JTokenType.Null) return "null";
            var s = t.Type == JTokenType.String ? "\"" + t + "\"" : t.ToString(Newtonsoft.Json.Formatting.None);
            return s.Length > p_Max ? s[..p_Max] + "…" : s;
        }

        static JToken? ToToken(object? v) => v == null ? null : v as JToken ?? JToken.FromObject(v);

        /// <summary>
        /// The engine's text translator: a string that is a text id (ID_…) lands in a text field already localised. Done here on the data
        /// handed to the widgets (recorded or generated), in place, on every string value; paths and other values are left alone.
        /// </summary>
        int Localise(JToken p_Token)
        {
            if (m_Texts == null) return 0;
            var s_Count = 0;
            switch (p_Token)
            {
                case JObject o:
                    foreach (var p in o.Properties().ToList())
                    {
                        if (p.Value.Type == JTokenType.String && ((string)p.Value!).StartsWith("ID_", StringComparison.Ordinal))
                        {
                            var s_Text = LocaliseText((string)p.Value!);
                            if (s_Text != null) { p.Value = s_Text; ++s_Count; }
                        }
                        else s_Count += Localise(p.Value);
                    }
                    break;
                case JArray a:
                    for (var i = 0; i < a.Count; ++i)
                    {
                        if (a[i].Type == JTokenType.String && ((string)a[i]!).StartsWith("ID_", StringComparison.Ordinal))
                        {
                            var s_Text = LocaliseText((string)a[i]!);
                            if (s_Text != null) { a[i] = s_Text; ++s_Count; }
                        }
                        else s_Count += Localise(a[i]);
                    }
                    break;
            }
            return s_Count;
        }

        /// <summary>A text id, or a ';'-joined list of them (a TabBar's tabs, a button's toggle texts), as the engine's translator would show it; null when nothing is known.</summary>
        string? LocaliseText(string p_Value)
        {
            if (m_Texts == null) return null;
            if (!p_Value.Contains(';')) return m_Texts.Lookup(p_Value);
            var s_Parts = p_Value.Split(';');
            var s_Any = false;
            for (var i = 0; i < s_Parts.Length; ++i)
            {
                if (!s_Parts[i].StartsWith("ID_", StringComparison.Ordinal)) continue;
                var s_Text = m_Texts.Lookup(s_Parts[i]);
                if (s_Text != null) { s_Parts[i] = s_Text; s_Any = true; }
            }
            return s_Any ? string.Join(";", s_Parts) : null;
        }

        /// <summary>The data keys the loaded graphs read or write, with their component names — what a user may want to set by hand.</summary>
        public IEnumerable<(string Key, string Label)> ReferencedKeys()
        {
            var s_Seen = new HashSet<string>();
            foreach (var s_Model in m_Stack.Select(r => r.Model).Concat(m_Flow != null ? new[] { m_Flow } : Array.Empty<ScreenModel>()))
                foreach (var n in s_Model.AllNodes)
                {
                    var s_Key = n.Type switch
                    {
                        "ComparisonLogicNode" => n.Json["DataSourceInfo"]?["DataKey"]?.ToString(),
                        "DataSetNode" or "DataGetNode" or "RefreshNode" or "DataToggleNode" or "DataIncrementNode" or "DataStepNode" => n.Json["DataSource"]?["DataKey"]?.ToString(),
                        _ => null,
                    };
                    if (s_Key == null || s_Key == "0" || !s_Seen.Add(s_Key)) continue;
                    yield return (s_Key, $"{KeyName(s_Key)} ({n.Type} {n.Label})");
                }
        }

        /// <summary>Data values the generator built from the game's data for the profile, so far.</summary>
        public int Generated { get; private set; }
        readonly HashSet<string> m_GeneratedKeys = new();   // the keys the generator filled: built again when the profile changes

        /// <summary>
        /// A key the widgets or the graph read and the host does not hold yet: the generator's stand-in for the component (built from
        /// the game's EBX for the profile), held and pushed to the page. False when nothing could be generated.
        /// </summary>
        bool EnsureGenerated(string p_Key)
        {
            if (p_Key == "0") return false;
            if (Data.TryGetValue(p_Key, out var s_Held) && s_Held != null) return true;
            if (m_Services == null) return false;
            var s_Name = KeyName(p_Key);
            var s_Dot = s_Name.IndexOf('.');
            if (s_Name.StartsWith("key ", StringComparison.Ordinal) || s_Dot < 0) return false;
            var (s_Value, s_How) = m_Services.Generate(s_Name[..s_Dot], s_Name[(s_Dot + 1)..]);
            if (s_Value == null) return false;
            Data[p_Key] = s_Value;
            m_GeneratedKeys.Add(p_Key);
            m_Window.SetData(new Dictionary<string, object?> { [p_Key] = s_Value });
            ++Generated;
            m_Log($"[host] generated {s_Name} ← {s_How}: {Short(s_Value, 100)}");
            return true;
        }

        /// <summary>
        /// The profile changed (the player's toolbar: another kit or primary weapon): every value the generator built is built again for the new
        /// profile and the widgets bound to it refresh — what the engine's components do when the player picks another kit. Returns how many.
        /// </summary>
        public int Regenerate(string p_What)
        {
            if (m_Services == null) return 0;
            var s_Count = 0;
            foreach (var s_Key in m_GeneratedKeys.ToList())
            {
                var s_Name = KeyName(s_Key); var s_Dot = s_Name.IndexOf('.');
                if (s_Dot < 0) continue;
                var (s_Value, s_How) = m_Services.Generate(s_Name[..s_Dot], s_Name[(s_Dot + 1)..]);
                if (s_Value == null) { Data.Remove(s_Key); m_GeneratedKeys.Remove(s_Key); m_Log($"[host] {s_Name}: nothing generated for {p_What} (the key is dropped)"); continue; }
                Data[s_Key] = s_Value;
                m_Window.SetData(new Dictionary<string, object?> { [s_Key] = s_Value });
                ++Generated; ++s_Count;
                m_Log($"[host] regenerated {s_Name} ← {s_How}: {Short(s_Value, 100)}");
                Refresh(s_Key, s_Value, 0);
            }
            m_Log($"[host] profile {p_What}: {s_Count} generated value(s) built again");
            return s_Count;
        }

        /// <summary>A held value by its component.source name (null when nothing is held under it).</summary>
        public object? Value(string p_Name) => Data.FirstOrDefault(kv => KeyName(kv.Key) == p_Name).Value;

        /// <summary>Drops a held value by its component.source name (a selection the engine's action consumed).</summary>
        public void Forget(string p_Name)
        {
            foreach (var s_Key in Data.Keys.Where(k => KeyName(k) == p_Name).ToList()) Data.Remove(s_Key);
        }

        /// <summary>A value set by hand (the player's toolbar): the table, the page, and the widgets bound to the key.</summary>
        public void SetDataKey(string p_Key, string p_Value)
        {
            Data[p_Key] = p_Value;
            m_Window.SetData(new Dictionary<string, object?> { [p_Key] = p_Value });
            m_Log($"[host] set {KeyName(p_Key)} = \"{p_Value}\" (by hand)");
            Refresh(p_Key, p_Value, 0);
        }

        // ------------------------------------------------------------------------------------------ calls from the movie

        ScreenRun? RunOf(string p_Path)
        {
            var s_Parts = p_Path.Split('.');
            var s_Clip = s_Parts.Length > 1 && s_Parts[0] == "_level0" ? s_Parts[1] : s_Parts[0];
            return m_Stack.FirstOrDefault(r => r.Clip == s_Clip);
        }

        /// <summary>A call the ActionScript made on an engine component (through the bridge).</summary>
        public void HandleCall(string p_Comp, string p_Method, JArray p_Args)
        {
            var s_Args = string.Join(", ", p_Args.Select(a => Short(a, 80)));
            switch (p_Comp, p_Method)
            {
                case ("screen", "loaded"):
                {
                    var s_Clip = p_Args.Count > 0 && p_Args[0].Type == JTokenType.String ? (string)p_Args[0]! : "sc1";
                    var s_Run = m_Stack.FirstOrDefault(r => r.Clip == s_Clip);
                    if (s_Run == null) { m_Log($"[host] {s_Clip} reports itself loaded but is not on the stack (popped, or a clip of an earlier page): ignored"); return; }
                    if (s_Run.Initialised) { m_Log($"[host] {s_Clip} {s_Run.Screen} reports itself loaded again: ignored (already initialised)"); return; }
                    InitialiseWidgets(s_Run);
                    return;
                }
                case ("rec", "record") when p_Args.Count >= 2:
                    OnRecord((string?)p_Args[0] ?? "", (string?)p_Args[1] ?? "");
                    return;
                case ("UIWidgetEventComp", "fireEvent") when p_Args.Count >= 2:
                    OnFire((string?)p_Args[0] ?? "", p_Args[1], p_Args.Count > 2 ? p_Args[2] : null);
                    return;
                case ("UIWidgetEventComp", "requestFocus") when p_Args.Count >= 2:
                {
                    // the engine's focus manager, as the game's own traffic shows it (measured 2026-09-16): the focus is held by a GROUP — the
                    // widgets whose FocusIndex is the requester's. A request for the group already held changes nothing; otherwise the engine
                    // walks the widgets in the init order and calls onFocused() on those of the new group and onUnfocused() on those of the
                    // old one (the first kit row, the info box, the button bar and the header share group 0: they focus and unfocus together)
                    var s_Run = m_Stack.FirstOrDefault(r => r.Clip == p_Args[0].ToString()) ?? m_Stack.LastOrDefault();
                    var s_Widget = p_Args[1].ToString();
                    if (s_Run == null) { m_Log($"[game] requestFocus({s_Args}): no screen on the stack"); return; }
                    var s_Asker = Widgets(s_Run).FirstOrDefault(w => w.Label == s_Widget);
                    var s_Group = s_Asker?.FocusIndex ?? 0;
                    if (s_Group == s_Run.FocusGroup) { s_Run.Focused = s_Widget; m_Log($"[host] focus on {s_Run.Screen}: {s_Widget} asked, group {s_Group} held already (no call: the screen's own init gave the group onFocused(true))"); return; }
                    var s_Gained = new List<string>(); var s_Lost = new List<string>();
                    foreach (var w in Widgets(s_Run))
                    {
                        if (w.FocusIndex == s_Group) { m_Window.Dispatch(Path(s_Run, w.Label), "onFocused", false); s_Gained.Add(w.Label); }
                        else if (w.FocusIndex == s_Run.FocusGroup) { m_Window.Dispatch(Path(s_Run, w.Label), "onUnfocused"); s_Lost.Add(w.Label); }
                    }
                    m_Log($"[host] focus on {s_Run.Screen}: {s_Widget} onFocused(false) — group {s_Run.FocusGroup} → {s_Group}: {string.Join(", ", s_Gained.Select(g => g + " onFocused(false)"))}{(s_Lost.Count > 0 ? "; " + string.Join(", ", s_Lost.Select(l => l + " onUnfocused()")) : "")}");
                    s_Run.FocusGroup = s_Group;
                    s_Run.Focused = s_Widget;
                    return;
                }
                case ("rue", "conceptHanded") when p_Args.Count >= 3:
                {
                    // the page walked the clips for an input concept (DispatchConcept): who was handed it, in order
                    var s_Run = m_Stack.LastOrDefault();
                    var s_Handed = (p_Args[2] as JArray ?? new JArray()).Select(p => p.ToString().Split('.').Last()).ToList();
                    var s_Concept = (int?)p_Args[1] ?? -1;
                    var s_Name = s_Concept >= 0 && s_Concept < InputConceptNames.Length ? InputConceptNames[s_Concept] : s_Concept.ToString();
                    LastConceptHanded = s_Handed;
                    m_Log($"[host] input concept {s_Name} {((bool?)p_Args[0] == true ? "pressed" : "released")} on {s_Run?.Screen}: {string.Join(", ", s_Handed)}");
                    return;
                }
                case ("UIDataInterfaceComp", "getData") when p_Args.Count >= 1:
                {
                    var s_Key = p_Args[0].ToString();
                    var s_Now = !Data.ContainsKey(s_Key) && EnsureGenerated(s_Key);
                    m_Log($"[game] getData({s_Key}) = {KeyName(s_Key)}" + (Data.TryGetValue(s_Key, out var v) && v != null ? $"  → {Short(ToToken(v), 80)}{(s_Now ? " (generated now: the next read answers)" : "")}" : "  (no value held: the game would read its data component here)"));
                    return;
                }
                case ("UIDataInterfaceComp", "setData") when p_Args.Count >= 2:
                    Data[p_Args[0].ToString()] = p_Args[1].Type == JTokenType.Null ? null : p_Args[1].ToString();
                    m_Log($"[game] setData({KeyName(p_Args[0].ToString())} = {p_Args[1]})");
                    return;
                case ("UIScreenEventComp", "screenEnterCompleted"):
                {
                    var s_Run = p_Args.Count > 0 ? RunOf(p_Args[0].ToString()) : null;
                    m_Log($"[game] screenEnterCompleted({s_Args}): every widget reported its enter complete — {s_Run?.Screen ?? "the screen"} is up");
                    if (s_Run != null) OnScreenInitialised(s_Run);
                    return;
                }
                case ("UIScreenEventComp", "screenInitializeError") when p_Args.Count >= 2:
                    m_Log($"[game] screenInitializeError({s_Args}): the screen has no clip named {p_Args[1]} (a record for a widget that is not on the stage)");
                    return;
                case ("UITextureStreamingComponent", "loadTextureAsync") when p_Args.Count >= 3:
                {
                    // the engine streams the texture in and calls back <clip>.loadTextureAsyncDone(result, url); the image manager then loads
                    // "img://url" — redirected to ./img/url, which the preview's server answers from the texture (cache or mounted game)
                    var s_Clip = p_Args[1].ToString(); var s_Url = p_Args[2].ToString();
                    var s_Found = m_Services?.TextureExists(s_Url.ToLowerInvariant()) ?? false;
                    ++TexturesAsked; if (s_Found) ++TexturesFound;
                    m_Window.Dispatch(s_Clip, "loadTextureAsyncDone", s_Found, s_Url);
                    m_Log($"[host] texture {s_Url} for {s_Clip.Split('.').Last()}: {(s_Found ? "found → loadTextureAsyncDone(true)" : "NOT in the cache nor the mounted game → loadTextureAsyncDone(false)")}");
                    return;
                }
                case ("UITextureStreamingComponent", "unloadTextureAsync"):
                    return;
                default:
                    m_Log($"[game] {p_Comp}.{p_Method}({s_Args})");
                    return;
            }
        }

        /// <summary>The recorder inside the movie echoed what the screen received: the same record the game would send out over the log channel.</summary>
        void OnRecord(string p_Id, string p_Json)
        {
            ++RecordsEchoed;
            try
            {
                var s_Record = JObject.Parse(p_Json);
                var s_Kind = (string?)s_Record["kind"] ?? "";
                // the traffic batches (input concepts, focus, in-events, fireEvent…) are kept apart: LastRecord stays the last DATA record
                // (init / refresh), which is what the seams and the import wait for
                if (s_Kind == "event")
                {
                    var s_Batch = (s_Record["events"] as JArray ?? new JArray()).OfType<JObject>().ToList();
                    EventsEchoed.AddRange(s_Batch);
                    m_Log($"[host] recorder echo {p_Id} (event, {s_Record["screen"]}): {s_Batch.Count} event(s) — {string.Join(", ", s_Batch.Take(6).Select(e => $"{e["w"]}.{e["m"]}({string.Join(",", (e["a"] as JArray ?? new JArray()).Select(a => a.ToString()))})"))}{(s_Batch.Count > 6 ? ", …" : "")}");
                    return;
                }
                LastRecord = s_Record;
                var s_Count = s_Kind == "init" ? CountOf(s_Record["widgets"]) : s_Kind == "refresh" ? CountOf(s_Record["list"]) : 0;
                m_Log($"[host] recorder echo {p_Id} ({s_Kind}, {s_Record["screen"]}): {s_Count} record(s), {(s_Record["getData"] as JObject)?.Count ?? 0} data answers, {(s_Record["texts"] as JObject)?.Count ?? 0} texts, {p_Json.Length} chars — what the game's log channel would carry");
            }
            catch (Exception s_Ex) { m_Log($"[host] recorder echo {p_Id} is not JSON: {s_Ex.Message}"); }
        }

        /// <summary>The traffic the recorder inside the movie noted on the widgets ({t, w, m, a, r}), every screen of this host, in order.</summary>
        public List<JObject> EventsEchoed { get; } = new();

        static int CountOf(JToken? t) => t is JArray a ? a.Count : t is JObject o ? ((int?)o["length"] ?? o.Properties().Count(p => int.TryParse(p.Name, out _))) : 0;

        // ------------------------------------------------------------------------------------------ widgets

        record WidgetRef(string Label, string? Asset, JObject? Node, JObject? Binding, IReadOnlyList<(string Name, string Value)> Properties, int ZDepth, bool FromDocument, int FocusIndex, bool AlwaysInFocus);

        IEnumerable<WidgetRef> Widgets(ScreenRun p_Run)
        {
            var s_Seen = new HashSet<string>();
            foreach (var w in p_Run.Model.Widgets)
            {
                var s_Node = p_Run.Model.AllNodes.FirstOrDefault(n => n.IsWidget && n.Label == w.InstanceName)?.Json;
                JObject? s_Binding = null;
                if (s_Node?["DataBinding"] is JObject s_Ref && (string?)s_Ref["InstanceGuid"] is { } s_Guid && p_Run.Model.Instances[s_Guid] is JObject b) s_Binding = b;
                // the document's word on a shipped node: an inline binding, edited properties
                var s_DocEdit = p_Run.Doc?.Nodes.FirstOrDefault(n => n.InstanceName == w.InstanceName);
                if (s_DocEdit != null && s_DocEdit.Fields.TryGetValue("DataBinding", out var s_Inline) && s_Inline is JObject s_Obj && s_Obj["$type"] != null) s_Binding = s_Obj;
                // edited properties the way the build applies them (StaticGraph / the client's Lua): replaceProperties = exactly the
                // document's list; otherwise the shipped list with the document's rewritten in place and the new ones appended
                var s_Props = w.Properties;
                if (s_DocEdit != null && s_DocEdit.Properties.Count > 0)
                {
                    if (s_DocEdit.ReplaceProperties)
                        s_Props = s_DocEdit.Properties.Select(p => (p.Key, p.Value)).ToList();
                    else
                    {
                        s_Props = w.Properties.Select(p => s_DocEdit.Properties.TryGetValue(p.Name, out var v) ? (p.Name, v) : p).ToList();
                        s_Props.AddRange(s_DocEdit.Properties.Where(kv => !w.Properties.Any(p => p.Name == kv.Key)).Select(kv => (kv.Key, kv.Value)));
                    }
                }
                var s_Focus = s_DocEdit != null && s_DocEdit.Fields.TryGetValue("FocusIndex", out var s_DocFocus) && int.TryParse(s_DocFocus?.ToString(), out var s_F) ? s_F : (int?)s_Node?["FocusIndex"] ?? 0;
                var s_Always = s_DocEdit != null && s_DocEdit.Fields.TryGetValue("AlwaysInFocus", out var s_DocAlways) ? string.Equals(s_DocAlways?.ToString(), "true", StringComparison.OrdinalIgnoreCase) : (bool?)s_Node?["AlwaysInFocus"] ?? false;
                s_Seen.Add(w.InstanceName);
                yield return new WidgetRef(w.InstanceName, w.WidgetPartition, s_Node, s_Binding, s_Props, w.ZDepthLevel, false, s_Focus, s_Always);
            }
            foreach (var n in p_Run.Doc?.Nodes ?? new List<NodeEntry>())
            {
                if (n.Widget == null || !s_Seen.Add(n.InstanceName)) continue;
                var s_Binding = n.Fields.TryGetValue("DataBinding", out var s_Inline) && s_Inline is JObject s_Obj && s_Obj["$type"] != null ? s_Obj : null;
                var s_Focus = n.Fields.TryGetValue("FocusIndex", out var s_DocFocus) && int.TryParse(s_DocFocus?.ToString(), out var s_F) ? s_F : n.FocusIndex;   // the entry's own (-1 = never focused, as the build writes it)
                var s_Always = n.Fields.TryGetValue("AlwaysInFocus", out var s_DocAlways) && string.Equals(s_DocAlways?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
                yield return new WidgetRef(n.InstanceName, n.Widget, null, s_Binding, n.Properties.Select(p => (p.Key, p.Value)).ToList(), n.ZDepthLevel, true, s_Focus, s_Always);
            }
        }

        /// <summary>The widget the host's focus manager holds the focus on, on the screen on top (null before any requestFocus).</summary>
        public string? FocusedWidget => m_Stack.LastOrDefault()?.Focused;

        static string Path(ScreenRun p_Run, string p_Label) => $"_level0.{p_Run.Clip}.{p_Run.Root}.{p_Label}";
        static string RootPath(ScreenRun p_Run) => $"_level0.{p_Run.Clip}.{p_Run.Root}";

        /// <summary>
        /// The record the engine hands a widget through initializeScreen: the game's own when the recording has it, else built from its
        /// node, its contract and its binding. Align = the node's horizontal/vertical alignment as the widget's base class reads it
        /// (0 = left/top, 1 = centre, 2 = right/bottom, applied against the stage rectangles the shell defines).
        /// </summary>
        (JObject Record, bool Recorded) InitData(ScreenRun p_Run, WidgetRef w)
        {
            var s_Recorded = p_Run.Recording?.InitRecord(w.Label);
            if (s_Recorded != null)
            {
                var s_Copy = (JObject)s_Recorded.DeepClone();
                s_Copy["InstanceName"] = w.Label;
                if (s_Copy["data"] is not JObject s_Data) s_Copy["data"] = s_Data = new JObject();
                // a channel the record does not carry but the host holds (generated for the profile) fills the gap
                if (w.Binding != null && (string?)w.Binding["$type"] == "UIDynamicDataBinding")
                    foreach (var s_Source in (w.Binding["Bindings"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        var s_Name = (string?)s_Source["DataName"] ?? ""; var s_Key = s_Source["DataKey"]?.ToString() ?? "0";
                        if (s_Name != "" && s_Data[s_Name] == null && Data.TryGetValue(s_Key, out var s_Value) && s_Value != null) s_Data[s_Name] = ToToken(s_Value)?.DeepClone();
                    }
                // the whole record: the data channels and the widget properties that are text ids (a button's TextData "ID_M_BACK") —
                // the engine's translator shows them localised, and this player has no translator
                Localise(s_Copy);
                return (s_Copy, true);
            }
            var s_Init = new JObject();
            s_Init["InstanceName"] = w.Label;
            // HasFocus = the node's FocusIndex is 0 (measured 2026-09-16 on the game's own record: the first kit row, the info box, the button
            // bar and the page header carry it; rows 1..4 and the -1 widgets do not) — the screen's ActionScript calls onFocused(true) on them
            s_Init["HasFocus"] = (int?)w.Node?["FocusIndex"] == 0;
            var s_Events = w.Asset != null ? m_Catalog?.Get(w.Asset)?.Events : null;
            s_Init["NumEvents"] = s_Events?.Count ?? 0;
            for (var i = 0; i < (s_Events?.Count ?? 0); ++i) s_Init["Event_" + i] = EventValue(s_Events![i].Query);
            s_Init["ZDepthLevel"] = w.ZDepth;
            s_Init["Align"] = new JArray(AlignValue((string?)w.Node?["HorisontalAlign"], "WHA_"), AlignValue((string?)w.Node?["VerticalAlign"], "WVA_"));
            foreach (var (s_Name, s_Value) in w.Properties) s_Init[s_Name] = s_Value;
            s_Init["data"] = BindingData(w.Binding);
            Localise(s_Init);   // the properties that are text ids too (a button's TextData), as the engine's translator shows them
            return (s_Init, false);
        }

        /// <summary>A WidgetNode's HorisontalAlign (WHA_Left/Center/Right) or VerticalAlign (WVA_Top/Center/Bottom) as the record carries it: 0, 1, 2.</summary>
        static int AlignValue(string? p_Enum, string p_Prefix) => (p_Enum ?? "").Replace(p_Prefix, "") switch { "Left" or "Top" => 0, "Right" or "Bottom" => 2, _ => 1 };

        /// <summary>The data channels a binding fixes or the host can serve: static texts, and the data keys the host holds.</summary>
        JObject BindingData(JObject? b)
        {
            var d = new JObject();
            if (b == null) return d;
            string Resolve(string? s) => s == null ? "" : (m_Texts?.Resolve(s) ?? s);
            switch ((string?)b["$type"])
            {
                case "UIPageHeaderBinding":
                    // the game hands the three channels, the icon empty when there is none (measured on its own record): the static text, or
                    // the value the host holds for a channel bound to a key
                    foreach (var s_Channel in new[] { "SubHeader", "Header", "Icon" })
                    {
                        var s_ChannelKey = b[s_Channel]?["DataKey"]?.ToString() ?? "0";
                        d[s_Channel] = s_ChannelKey != "0" && Data.TryGetValue(s_ChannelKey, out var s_Held) && s_Held != null ? ToToken(s_Held)?.DeepClone() : Resolve((string?)b["Static" + s_Channel] ?? "");
                    }
                    break;
                case "UIButtonDataBinding":
                {
                    // the button bar: its static flags, an empty default set as null, and the layout string when the host already holds it
                    d["InputOnRelease"] = (bool?)b["InputOnRelease"] ?? false;
                    d["Visible"] = (bool?)b["Visible"] ?? true;
                    d["DefaultButtonSet"] = JValue.CreateNull();
                    var s_Key = b["ButtonsDatasource"]?["DataKey"]?.ToString() ?? "0";
                    if (s_Key != "0" && Data.TryGetValue(s_Key, out var s_Buttons) && s_Buttons != null) d["Buttons"] = ToToken(s_Buttons)?.DeepClone();
                    break;
                }
                case "UITextDataBinding":
                    if (!string.IsNullOrEmpty((string?)b["StaticText"])) d["Text"] = Resolve((string?)b["StaticText"]);
                    break;
                case "UIDynamicDataBinding":
                    foreach (var s_Source in (b["Bindings"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        var s_Name = (string?)s_Source["DataName"] ?? ""; var s_Key = s_Source["DataKey"]?.ToString() ?? "0";
                        if (s_Name != "" && Data.TryGetValue(s_Key, out var s_Value) && s_Value != null) d[s_Name] = ToToken(s_Value)?.DeepClone();
                    }
                    break;
            }
            Localise(d);
            return d;
        }

        /// <summary>The screen is loaded: initializeScreen with every widget's record, as the engine does — then the screen is entered through its flow graph.</summary>
        /// <summary>The keys the widgets' base code asks the engine about the machine and the session, answered before any widget initialises: an unanswered platform reads as console (buttons hidden, no mouse on the kit rows).</summary>
        static readonly string[] c_EngineFacts = { "UISettingsComp.Platform", "UISettingsComp.Ps3EnterButton", "FrontEndComp.InFrontend", "FrontEndComp.IsPro" };

        /// <summary>The data key (the engine's hash) of a component.source name known to the catalogue, or null.</summary>
        string? KeyOf(string p_Name) =>
            m_Catalog?.Components.Values.SelectMany(c => c.Keys.Where(k => c.Name.Split('/').Last() + "." + k.Key == p_Name).Select(k => k.Value.ToString())).FirstOrDefault();

        void InitialiseWidgets(ScreenRun p_Run)
        {
            foreach (var s_Fact in c_EngineFacts) if (KeyOf(s_Fact) is { } s_FactKey) EnsureGenerated(s_FactKey);
            // what the screen's widgets and graph read from the components: generated for the profile where nothing is held yet — every
            // DataKey a binding names, whatever its shape (UIDynamicDataBinding.Bindings[], UIPageHeaderBinding.Header/SubHeader,
            // UIButtonDataBinding.ButtonsDatasource…: the KITS screen's header stayed empty while only the dynamic bindings were read)
            foreach (var w in Widgets(p_Run))
                if (w.Binding != null)
                    foreach (var s_KeyProp in w.Binding.Descendants().OfType<JProperty>().Where(p => p.Name == "DataKey"))
                        EnsureGenerated(s_KeyProp.Value?.ToString() ?? "0");
            foreach (var (s_Key, _) in ReferencedKeys().ToList()) EnsureGenerated(s_Key);
            var s_Records = new JArray();
            var s_Lines = new List<string>();
            p_Run.Widgets = 0; p_Run.Recorded = 0;
            foreach (var w in Widgets(p_Run))
            {
                var (s_Init, s_Recorded) = InitData(p_Run, w);
                s_Records.Add(s_Init);
                ++p_Run.Widgets; ++WidgetsInitialised;
                if (s_Recorded) { ++p_Run.Recorded; ++RecordedWidgetsUsed; }
                var s_Data = s_Init["data"] as JObject;
                s_Lines.Add($"{w.Label}[{s_Init["NumEvents"]} events{(s_Data != null && s_Data.Count > 0 ? ", data " + string.Join("/", s_Data.Properties().Select(p => p.Name)) : "")}{(s_Recorded ? ", recorded" : "")}]");
            }
            p_Run.Initialised = true;
            m_Window.Dispatch(RootPath(p_Run), "initializeScreen", s_Records);
            m_Log($"[host] {p_Run.Clip} {p_Run.Screen} loaded: initializeScreen with {p_Run.Widgets} widget records ({p_Run.Recorded} from the recording) — {string.Join(", ", s_Lines)}");
            if (p_Run.Recording != null && p_Run.Recorded == 0) m_Log($"[host] the recording is of {p_Run.Recording.Screen} and names {string.Join(", ", p_Run.Recording.InstanceNames)}: none of them is on this screen");
            // the screen is entered: the flow graph's StateNode fires the screen's InstanceInputNode of the port the push came through
            // (every entry point when none is known), whose wiring sets up the data the widgets show — the graph runs from there
            var s_Scope = new Scope(p_Run.Model, p_Run.Doc, p_Run);
            var s_Entries = p_Run.Model.AllNodes.Where(n => n.Type == "InstanceInputNode").ToList();
            var s_Chosen = p_Run.Entry != null ? s_Entries.Where(n => n.Label == p_Run.Entry).ToList() : new List<NodeInfo>();
            if (s_Chosen.Count == 0) s_Chosen = s_Entries;
            foreach (var s_Entry in s_Chosen)
            {
                m_Log($"[host] entering {p_Run.Screen}: {s_Entry.Label}" + (p_Run.EntryParam != null ? $" ({Short(p_Run.EntryParam, 60)})" : ""));
                Continue(s_Scope, s_Entry, p_Run.Doc?.Nodes.FirstOrDefault(n => n.InstanceName == s_Entry.Label), "Out", p_Run.EntryParam, 0);
            }
            // a screen the document made has its entry points in the document only (its template ships none)
            if (s_Chosen.Count == 0)
                foreach (var s_DocEntry in (p_Run.Doc?.Nodes ?? new List<NodeEntry>()).Where(n => n.Type == "InstanceInputNode" && (p_Run.Entry == null || n.InstanceName == p_Run.Entry)))
                {
                    m_Log($"[host] entering {p_Run.Screen}: {s_DocEntry.InstanceName} (document)" + (p_Run.EntryParam != null ? $" ({Short(p_Run.EntryParam, 60)})" : ""));
                    Continue(s_Scope, null, s_DocEntry, "Out", p_Run.EntryParam, 0);
                }
        }

        /// <summary>Every widget of the screen reported its enter complete: the StateNode's Initialized output fires in the flow graph.</summary>
        void OnScreenInitialised(ScreenRun p_Run)
        {
            if (m_Flow != null && p_Run.StateNode == null && p_Run.DocStateNode != null)
            {
                // the document's StateNode: its Initialized output is the document's port, its wires the document's
                var s_DocPort = p_Run.DocStateNode.Ports.FirstOrDefault(p => p.Name.StartsWith("Initialized", StringComparison.Ordinal));
                if (s_DocPort == null) return;
                m_Log($"[host] {p_Run.DocStateNode.InstanceName}.{s_DocPort.Name} (flow graph, document)");
                Continue(FlowScope(), null, p_Run.DocStateNode, s_DocPort.Name, null, 1);
                return;
            }
            if (m_Flow == null || p_Run.StateNode == null) return;
            var s_Port = p_Run.StateNode.Ports.FirstOrDefault(p => !p.IsInput && (p.Name.StartsWith("Initialized", StringComparison.Ordinal)));
            if (s_Port == null) return;
            m_Log($"[host] {p_Run.StateNode.Label}.{s_Port.Name} (flow graph)");
            FollowWires(FlowScope(), p_Run.StateNode, s_Port, null, 1);
        }

        // ------------------------------------------------------------------------------------------ events

        void OnFire(string p_Path, JToken p_Event, JToken? p_Param)
        {
            ++EventsFired;
            var s_Instance = p_Path.Split('.').Last();
            var s_Run = RunOf(p_Path) ?? m_Stack.LastOrDefault();
            var s_Value = p_Event.Type is JTokenType.Integer or JTokenType.Float ? (int)p_Event : -1;
            var s_Query = s_Value >= 0 ? EventQuery(s_Value) : p_Event.ToString();
            var s_Param = p_Param == null || p_Param.Type == JTokenType.Null ? null : p_Param;
            m_Log($"[host] {s_Instance} fired {s_Query}" + (s_Param != null ? $" ({Short(s_Param, 80)})" : "") + (s_Run != null && m_Stack.Count > 1 ? $" [{s_Run.Clip} {s_Run.Screen}]" : ""));
            if (s_Run == null) { m_Log("[host]   no screen on the stack for " + p_Path); return; }
            var s_Scope = new Scope(s_Run.Model, s_Run.Doc, s_Run);
            var s_Followed = 0;
            var s_Node = s_Run.Model.AllNodes.FirstOrDefault(n => n.IsWidget && n.Label == s_Instance);
            if (s_Node != null)
            {
                var s_Port = s_Node.Ports.FirstOrDefault(p => !p.IsInput && p.Query == s_Query);
                if (s_Port != null)
                    foreach (var s_Wire in WiresFrom(s_Scope, s_Node, s_Port))
                    { FollowWire(s_Scope, s_Wire, s_Param, 0); ++s_Followed; }
            }
            var s_DocNode = s_Run.Doc?.Nodes.FirstOrDefault(n => n.InstanceName == s_Instance);
            foreach (var c in s_DocNode?.Connections ?? new List<ConnectionEntry>())
                if (PortMatches(c.Event ?? c.FromPort, s_Query)) { Follow(s_Scope, c.ToNode, c.ToEvent ?? c.ToPort ?? "", s_Param, 0, c.Pop); ++s_Followed; }
            if (s_Followed == 0) m_Log($"[host]   nothing wired to {s_Instance}.{s_Query}");
        }

        /// <summary>Back: the controller's Deactivate/Back on the screen on top (Esc in the game) — the StateNode's input-event output of that name.</summary>
        public bool FireBack()
        {
            foreach (var s_Action in new[] { "Back", "Deactivate", "Menu" })
                if (FireInput(s_Action)) return true;
            m_Log("[host] Back: the screen on top has no Back/Deactivate input in its flow graph" + (m_Flow == null ? " (no flow graph)" : ""));
            return false;
        }

        /// <summary>A controller action (UIInputAction name: Back, Deactivate…) on the screen on top: its StateNode's input-event outputs of that action.</summary>
        public bool FireInput(string p_Action)
        {
            var s_Run = m_Stack.LastOrDefault();
            if (s_Run == null || m_Flow == null) return false;
            if (s_Run.StateNode == null)
            {
                // a screen the document's own StateNode pushed: its input-event outputs are the document's ports, its wires the document's
                if (s_Run.DocStateNode == null) return false;
                var s_DocPorts = s_Run.DocStateNode.Ports.Where(p => string.Equals(p.InputEvent, p_Action, StringComparison.OrdinalIgnoreCase)).ToList();
                if (s_DocPorts.Count == 0) return false;
                foreach (var s_DocPort in s_DocPorts)
                {
                    m_Log($"[host] input {p_Action} on {s_Run.Screen}: {s_Run.DocStateNode.InstanceName}.{s_DocPort.Name} (flow graph, document)");
                    Continue(FlowScope(), null, s_Run.DocStateNode, s_DocPort.Name, null, 1);
                }
                return true;
            }
            var s_Ports = s_Run.StateNode.Ports.Where(p => !p.IsInput && string.Equals(p.InputEvent, p_Action, StringComparison.OrdinalIgnoreCase)).ToList();
            if (s_Ports.Count == 0) return false;
            foreach (var s_Port in s_Ports)
            {
                m_Log($"[host] input {p_Action} on {s_Run.Screen}: {s_Run.StateNode.Label}.{s_Port.Name} (flow graph)");
                FollowWires(FlowScope(), s_Run.StateNode, s_Port, null, 1);
            }
            return true;
        }

        /// <summary>The input concepts of fb.Input.InputConceptActions, by value (what the engine hands the widgets for a key or a controller button).</summary>
        public static readonly string[] InputConceptNames = { "NavigateUp", "NavigateDown", "NavigateLeft", "NavigateRight", "TabLeft", "TabRight", "Activate", "Deactivate", "Menu", "Cancel", "OK", "Back", "Tab", "Edit", "View", "LThumb", "RThumb", "MapZoom", "MapSize", "SayAllChat", "TeamChat", "SquadChat", "Com" };

        /// <summary>
        /// An input concept (a key on PC: the arrows = NavigateUp/Down/Left/Right, Enter = Activate, Esc = Deactivate) pressed or
        /// released on the screen on top — handed the way the game's own traffic shows it (measured 2026-09-16): the engine walks the
        /// widgets in the init order and calls onInputConceptPressed/Released(concept) on those of the focus group it holds and on the
        /// ones the node marks AlwaysInFocus (the console button bar: it must see Back whoever is focused); a press goes no further
        /// than the first widget that fires an event back during its handler (a kit row stepping its item, a row moving the focus),
        /// a release reaches every eligible widget. The page does the walk (it sees the fired event synchronously) and reports back
        /// who was handed the concept (LastConceptHanded). Returns how many widgets were eligible.
        /// </summary>
        public int FireInputConcept(int p_Concept, bool p_Pressed)
        {
            var s_Run = m_Stack.LastOrDefault();
            if (s_Run == null || !s_Run.Initialised) { m_Log("[host] input concept: no initialised screen on top"); return 0; }
            var s_Name = p_Concept >= 0 && p_Concept < InputConceptNames.Length ? InputConceptNames[p_Concept] : p_Concept.ToString();
            var s_Eligible = Widgets(s_Run).Where(w => w.AlwaysInFocus || w.FocusIndex == s_Run.FocusGroup).Select(w => w.Label).ToList();
            m_Window.DispatchConcept(p_Concept, p_Pressed, s_Eligible.Select(l => Path(s_Run, l)));
            m_Log($"[host] input concept {s_Name} {(p_Pressed ? "pressed" : "released")} offered on {s_Run.Screen} to group {s_Run.FocusGroup} + AlwaysInFocus, in the init order: {string.Join(", ", s_Eligible)}{(p_Pressed ? " (a press stops at the first widget that fires an event)" : "")}");
            return s_Eligible.Count;
        }

        /// <summary>The widgets the page handed the last input concept to, in order (from rue.conceptHanded).</summary>
        public List<string> LastConceptHanded { get; private set; } = new();

        static HashSet<string> RemovedWires(ScreenEntry? p_Doc) => new((p_Doc?.RemovedConnections ?? new List<RemovedConnectionEntry>()).Select(r => r.Guid.ToLowerInvariant()));

        // ------------------------------------------------------------------------------------------ the graph walk

        void FollowWire(Scope p_Scope, ConnectionInfo p_Wire, JToken? p_Param, int p_Depth)
        {
            if (p_Wire.NumScreensToPop > 0) PopScreens(p_Wire.NumScreensToPop, p_Depth);
            // the wire's end by its guid: labels repeat in the shipped graphs (the loadout screen has six DataSetNodes named SetWeaponCategory,
            // one per row, each with its own Param), so a name would always land on the first
            var s_Node = p_Scope.Model.AllNodes.FirstOrDefault(n => n.Guid == p_Wire.ToGuid) ?? p_Scope.Model.AllNodes.FirstOrDefault(n => n.Label == p_Wire.To);
            var s_Label = s_Node?.Label ?? p_Wire.To;
            FollowNode(p_Scope, s_Node, p_Scope.Doc?.Nodes.FirstOrDefault(n => n.InstanceName == s_Label), s_Label, p_Wire.ToPort, p_Param, p_Depth);
        }

        /// <summary>A document wire's source spec ("Out", "OnItemReleased", "Outputs:CamoButton") names this port: the spec itself, or the name after the array field.</summary>
        static bool PortMatches(string? p_Spec, string p_Port)
        {
            if (p_Spec == null) return false;
            if (p_Spec == p_Port) return true;
            var i = p_Spec.IndexOf(':');
            return i >= 0 && p_Spec[(i + 1)..] == p_Port;
        }

        /// <summary>A connection of the document names its end by label (the document has no guids for shipped nodes); its pop count, like a shipped wire's, pops screens first.</summary>
        void Follow(Scope p_Scope, string p_Label, string p_Port, JToken? p_Param, int p_Depth, int p_Pop = 0)
        {
            if (p_Pop > 0) PopScreens(p_Pop, p_Depth);
            FollowNode(p_Scope, p_Scope.Model.AllNodes.FirstOrDefault(n => n.Label == p_Label), p_Scope.Doc?.Nodes.FirstOrDefault(n => n.InstanceName == p_Label), p_Label, p_Port, p_Param, p_Depth);
        }

        void FollowNode(Scope p_Scope, NodeInfo? s_Node, NodeEntry? s_DocNode, string p_Label, string p_Port, JToken? p_Param, int p_Depth)
        {
            if (p_Depth > 32) { m_Log("[host]   stopped: the wiring loops"); return; }
            var s_Type = s_Node?.Type ?? s_DocNode?.Type ?? "";
            var s_Json = s_Node?.Json ?? (s_DocNode != null ? new JObject(s_DocNode.Fields.Select(f => new JProperty(f.Key, f.Value))) : null);
            var s_Indent = "[host]   " + new string(' ', p_Depth * 2) + "→ ";
            switch (s_Type)
            {
                case "WidgetNode":
                {
                    if (p_Scope.Run == null) { m_Log($"{s_Indent}{p_Label}.{p_Port}: a widget outside a screen"); return; }
                    var s_Value = EventValue(p_Port);
                    m_Window.Dispatch(Path(p_Scope.Run, p_Label), "handleInEvent", s_Value, p_Param?.Type == JTokenType.Null ? null : p_Param);
                    m_Log($"{s_Indent}{p_Label}.{p_Port}: handleInEvent({s_Value}{(p_Param != null ? ", " + Short(p_Param, 80) : "")})");
                    return;
                }
                case "DataSetNode":
                {
                    var s_Key = s_Json?["DataSource"]?["DataKey"]?.ToString() ?? "0";
                    var s_ParamField = (string?)s_Json?["Param"] ?? "";
                    var s_Empty = (bool?)s_Json?["SetToEmptyString"] == true;
                    object? s_Value = s_ParamField != "" ? s_ParamField : s_Empty ? "" : p_Param?.Type == JTokenType.Null ? null : p_Param?.ToString();
                    Data[s_Key] = s_Value;
                    m_Window.SetData(new Dictionary<string, object?> { [s_Key] = s_Value });
                    m_Log($"{s_Indent}{p_Label}: set {KeyName(s_Key)} = {(s_Value == null ? "null" : "\"" + s_Value + "\"")}");
                    Refresh(s_Key, s_Value, p_Depth + 1);
                    // a key the engine's components act on when written (the KITS screen's SetKit moves the profile to that kit, the vehicle
                    // screens' SetVehicle/SetVehicleCategory pick the vehicle, the appearance screen's SetAppearance the camo): the stand-in
                    // takes it into the profile and the generated values are built again
                    var s_StoodIn = false;
                    try { s_StoodIn = m_Services?.DataSet?.Invoke(KeyName(s_Key), s_Value, this) ?? false; }
                    catch (Exception s_Ex) { m_Log($"{s_Indent}{p_Label}: the stand-in for setting {KeyName(s_Key)} failed: {s_Ex.Message}"); }
                    if (s_StoodIn) { m_Log($"{s_Indent}{p_Label}: the profile took {KeyName(s_Key)} = \"{s_Value}\" — the generated values are built again"); Regenerate("set " + KeyName(s_Key)); }
                    Continue(p_Scope, s_Node, s_DocNode, "Out", p_Param, p_Depth + 1);
                    return;
                }
                case "InstanceOutputNode":
                    if (p_Scope.Run != null) OnScreenOutput(p_Scope.Run, p_Label, s_Json, p_Param, p_Depth);
                    else m_Log($"{s_Indent}{p_Label}: output of the flow graph itself (Id {s_Json?["Id"]}) — the game would leave this graph here");
                    return;
                case "InstanceInputNode":
                    m_Log($"{s_Indent}{p_Label}: entry point");
                    Continue(p_Scope, s_Node, s_DocNode, "Out", p_Param, p_Depth + 1);
                    return;
                case "StateNode":
                {
                    if (s_Node == null)
                    {
                        // the document's own StateNode (a screen the document adds to the flow graph): its Screen field names the partition
                        var s_DocScreen = s_DocNode != null && s_DocNode.Fields.TryGetValue("Screen", out var s_ScreenField) ? (string?)s_ScreenField : null;
                        if (string.IsNullOrEmpty(s_DocScreen)) { m_Log($"{s_Indent}{p_Label}: StateNode of the document with no Screen — not pushed"); return; }
                        PushScreen(s_DocScreen, p_Label, p_Port, p_Param, p_Depth, null, s_DocNode);
                        return;
                    }
                    if (p_Port is "Show" or "Hide")
                    {
                        // a screen already on the player, shown or hidden in place (the HUD graphs drive their screens this way)
                        var s_Shown = m_Stack.FirstOrDefault(r => r.StateNode?.Guid == s_Node.Guid);
                        if (s_Shown == null) { m_Log($"{s_Indent}{p_Label}.{p_Port}: the screen is not on the player (nothing to {p_Port.ToLowerInvariant()})"); return; }
                        m_Window.Dispatch("_level0", "rueShowScreen", s_Shown.Clip, p_Port == "Show");
                        m_Log($"{s_Indent}{p_Label}.{p_Port}: {s_Shown.Clip} {s_Shown.Screen} {(p_Port == "Show" ? "shown" : "hidden")}");
                        return;
                    }
                    PushScreen(s_Node, p_Port, p_Param, p_Depth);
                    return;
                }
                case "DialogNode":
                    if (s_Node == null) { m_Log($"{s_Indent}{p_Label}: DialogNode of the document only, not simulated"); return; }
                    PushDialog(s_Node, p_Port, p_Param, p_Depth);
                    return;
                case "ActionNode":
                {
                    // the engine's actions that turn the screen's selections into the profile (the kit customization ones) have a stand-in in
                    // the editor: it reads the keys the screen's DataSetNodes wrote and changes the profile, and the generated values are built
                    // again; the rest (spawn, post-process, store to the server) are logged
                    var s_ActionName = ActionName(s_Json?["ActionKey"]);
                    var s_StoodIn = false;
                    try { s_StoodIn = m_Services?.Action?.Invoke(s_ActionName, this) ?? false; }
                    catch (Exception s_Ex) { m_Log($"{s_Indent}{p_Label}: the stand-in for {s_ActionName} failed: {s_Ex.Message}"); }
                    m_Log($"{s_Indent}{p_Label}: engine action {s_ActionName}({s_Json?["Params"]?.ToString(Newtonsoft.Json.Formatting.None)}{(p_Param != null && (bool?)s_Json?["AppendIncomingParams"] == true ? " + " + Short(p_Param, 60) : "")})" + (s_StoodIn ? " — stood in for: the profile took the selection, the generated values are built again" : " — no stand-in outside the game"));
                    if (s_StoodIn) Regenerate("action " + s_ActionName);
                    Continue(p_Scope, s_Node, s_DocNode, "Out", p_Param, p_Depth + 1);
                    return;
                }
                case "ComparisonLogicNode":
                {
                    var s_Key = s_Json?["DataSourceInfo"]?["DataKey"]?.ToString() ?? "0";
                    EnsureGenerated(s_Key);
                    var s_Held = Data.TryGetValue(s_Key, out var v) && v != null;
                    string s_Value; string s_From;
                    if (s_Held) { s_Value = v!.ToString() ?? ""; s_From = KeyName(s_Key); }
                    else if (p_Param != null && p_Param.Type is JTokenType.Integer or JTokenType.Float && m_DialogButtons != null && (int)p_Param >= 0 && (int)p_Param < m_DialogButtons.Count) { s_Value = m_DialogButtons[(int)p_Param]; s_From = $"the popup's button {(int)p_Param}"; }
                    else if (p_Param != null && p_Param.Type == JTokenType.String)
                    {
                        s_Value = (string)p_Param!; s_From = "the event's value";
                        // a popup's button fires the label it was given (PopupButtonsManager.releaseHandler): the game hands the ids and its
                        // translator renders them, the host handed the localised text so the movie could show it — the text goes back to
                        // the id the comparison's outputs are named by (RevertAccessories: ID_M_POPUP_OK)
                        if (m_DialogButtons != null)
                        {
                            var s_Id = m_DialogButtons.FirstOrDefault(id => id == s_Value || string.Equals(m_Texts?.Resolve(id) ?? id, s_Value, StringComparison.Ordinal));
                            if (s_Id != null && s_Id != s_Value) { s_From = $"the event's value \"{s_Value}\" = the popup's button"; s_Value = s_Id; }
                        }
                    }
                    else
                    {
                        m_Log($"{s_Indent}{p_Label}: compares {KeyName(s_Key)}, which holds no value here (the game's component answers it) — outputs: {string.Join(", ", s_Node?.Ports.Where(p => !p.IsInput).Select(p => p.Name) ?? Array.Empty<string>())}; set the key in the player's toolbar to follow a branch");
                        return;
                    }
                    m_Log($"{s_Indent}{p_Label}: compares {s_From} = \"{s_Value}\"");
                    // the outputs are named by the compared value as the game writes it: a boolean source gives True/False, so 1/0/true/false land there
                    var s_Out = s_Node?.Ports.FirstOrDefault(p => !p.IsInput && string.Equals(p.Name, s_Value, StringComparison.OrdinalIgnoreCase));
                    if (s_Out == null && s_Value is "1" or "0" or "true" or "false" or "True" or "False")
                    {
                        var s_Bool = s_Value is "1" or "true" or "True" ? "True" : "False";
                        s_Out = s_Node?.Ports.FirstOrDefault(p => !p.IsInput && p.Name == s_Bool);
                    }
                    if (s_Out == null) { m_Log($"{s_Indent}  no output named \"{s_Value}\" (outputs: {string.Join(", ", s_Node?.Ports.Where(p => !p.IsInput).Select(p => p.Name) ?? Array.Empty<string>())})"); return; }
                    FollowWires(p_Scope, s_Node!, s_Out, p_Param, p_Depth + 1);
                    return;
                }
                case "RefreshNode":
                {
                    var s_Key = s_Json?["DataSource"]?["DataKey"]?.ToString() ?? "0";
                    m_Log($"{s_Indent}{p_Label}: refresh {KeyName(s_Key)}");
                    Refresh(s_Key, Data.TryGetValue(s_Key, out var v) ? v : null, p_Depth + 1);
                    Continue(p_Scope, s_Node, s_DocNode, "Out", p_Param, p_Depth + 1);
                    return;
                }
                case "DataGetNode":
                {
                    var s_Key = s_Json?["DataSource"]?["DataKey"]?.ToString() ?? "0";
                    EnsureGenerated(s_Key);
                    var s_Value = Data.TryGetValue(s_Key, out var v) ? v : null;
                    m_Log($"{s_Indent}{p_Label}: get {KeyName(s_Key)} = {(s_Value == null ? "null" : Short(ToToken(s_Value), 80))}");
                    Continue(p_Scope, s_Node, s_DocNode, "Out", ToToken(s_Value), p_Depth + 1);
                    return;
                }
                case "":
                    m_Log($"{s_Indent}{p_Label}: no such node in this graph");
                    return;
                default:
                    m_Log($"{s_Indent}{p_Label} ({s_Type}): not simulated, followed through Out");
                    Continue(p_Scope, s_Node, s_DocNode, "Out", p_Param, p_Depth + 1);
                    return;
            }
        }

        void Continue(Scope p_Scope, NodeInfo? p_Node, NodeEntry? p_DocNode, string p_Port, JToken? p_Param, int p_Depth)
        {
            if (p_Node != null)
            {
                var s_Port = p_Node.Ports.FirstOrDefault(p => !p.IsInput && (p.Name == p_Port || p.Field == p_Port));
                if (s_Port != null) FollowWires(p_Scope, p_Node, s_Port, p_Param, p_Depth);
            }
            foreach (var c in p_DocNode?.Connections ?? new List<ConnectionEntry>())
                if (PortMatches(c.Event ?? c.FromPort, p_Port)) Follow(p_Scope, c.ToNode, c.ToEvent ?? c.ToPort ?? "", p_Param, p_Depth, c.Pop);
        }

        /// <summary>
        /// The wires leaving a port: those hooked to the port itself and those hooked to a twin the node does not list — the shipped graphs
        /// carry orphan port instances with the name of a listed port (the loadout screen's StateNode has a second "SetWeapon", "Confirm",
        /// "Spawn" and "AccessoriesButton" that its wires hang from; the model keeps them as dangling ports), and the engine goes by the name.
        /// </summary>
        IEnumerable<ConnectionInfo> WiresFrom(Scope p_Scope, NodeInfo p_Node, PortInfo p_Port)
        {
            var s_Removed = RemovedWires(p_Scope.Doc);
            var s_Guids = new HashSet<string>(p_Node.Ports.Where(p => !p.IsInput && (p.Guid == p_Port.Guid || (p.Name == p_Port.Name && (!p_Node.IsWidget || p.Query == p_Port.Query)))).Select(p => p.Guid));
            return p_Scope.Model.Wires.Where(x => x.FromGuid == p_Node.Guid && s_Guids.Contains(x.FromPortGuid) && !s_Removed.Contains(x.Guid.ToLowerInvariant())).ToList();
        }

        void FollowWires(Scope p_Scope, NodeInfo p_Node, PortInfo p_Port, JToken? p_Param, int p_Depth)
        {
            foreach (var s_Wire in WiresFrom(p_Scope, p_Node, p_Port)) FollowWire(p_Scope, s_Wire, p_Param, p_Depth);
            var s_DocNode = p_Scope.Doc?.Nodes.FirstOrDefault(n => n.InstanceName == p_Node.Label);
            foreach (var c in s_DocNode?.Connections ?? new List<ConnectionEntry>())
                if (PortMatches(c.Event ?? c.FromPort, p_Port.Name)) Follow(p_Scope, c.ToNode, c.ToEvent ?? c.ToPort ?? "", p_Param, p_Depth, c.Pop);
        }

        /// <summary>A screen output fired: the parent graph continues from the StateNode's output of that name.</summary>
        void OnScreenOutput(ScreenRun p_Run, string p_Label, JObject? p_Json, JToken? p_Param, int p_Depth)
        {
            var s_Indent = "[host]   " + new string(' ', p_Depth * 2) + "→ ";
            var s_Label = p_Label;
            var s_Destroy = (bool?)p_Json?["DestroyGraph"] == true;
            if (m_Flow != null && p_Run.StateNode == null && p_Run.DocStateNode != null)
            {
                // the screen was pushed by the document's StateNode: the parent graph continues from that node's output of this name (its wires)
                if (!p_Run.DocStateNode.Ports.Any(p => p.Name == s_Label) && !p_Run.DocStateNode.Connections.Any(c => PortMatches(c.Event ?? c.FromPort, s_Label)))
                { m_Log($"{s_Indent}{s_Label}: screen output, but the document's {p_Run.DocStateNode.InstanceName} has no output of that name (ports: {string.Join(", ", p_Run.DocStateNode.Ports.Select(p => p.Name))})"); return; }
                m_Log($"{s_Indent}{s_Label}: screen output{(s_Destroy ? " (DestroyGraph)" : "")} → {p_Run.DocStateNode.InstanceName}.{s_Label} in {m_Flow.Partition.Split('/').Last()} (document)");
                Continue(FlowScope(), null, p_Run.DocStateNode, s_Label, p_Param, p_Depth + 1);
                return;
            }
            if (m_Flow == null || p_Run.StateNode == null)
            {
                m_Log($"{s_Indent}{s_Label}: screen output (Id {p_Json?["Id"]}, DestroyGraph {s_Destroy}) — no flow graph here, the game would leave the screen");
                return;
            }
            var s_Port = p_Run.StateNode.Ports.FirstOrDefault(p => !p.IsInput && p.Name == s_Label) ?? p_Run.StateNode.Ports.FirstOrDefault(p => !p.IsInput && p.Name.StartsWith(s_Label + " ", StringComparison.Ordinal));
            var s_DocEdit = m_FlowDoc?.Nodes.FirstOrDefault(n => n.InstanceName == p_Run.StateNode.Label);
            var s_DocHas = s_DocEdit != null && (s_DocEdit.Ports.Any(p => p.Name == s_Label) || s_DocEdit.Connections.Any(c => PortMatches(c.Event ?? c.FromPort, s_Label)));
            if (s_Port == null && !s_DocHas)
            {
                m_Log($"{s_Indent}{s_Label}: screen output, but {p_Run.StateNode.Label} in {m_Flow.Partition.Split('/').Last()} has no output of that name (outputs: {string.Join(", ", p_Run.StateNode.Ports.Where(p => !p.IsInput).Select(p => p.Name))})");
                return;
            }
            m_Log($"{s_Indent}{s_Label}: screen output{(s_Destroy ? " (DestroyGraph)" : "")} → {p_Run.StateNode.Label}.{s_Port?.Name ?? s_Label} in {m_Flow.Partition.Split('/').Last()}{(s_Port == null ? " (the document's port)" : "")}");
            if (s_Port != null) FollowWires(FlowScope(), p_Run.StateNode, s_Port, p_Param, p_Depth + 1);
            else Continue(FlowScope(), null, s_DocEdit, s_Label, p_Param, p_Depth + 1);
        }

        // ------------------------------------------------------------------------------------------ the screen stack

        void PopScreens(int p_Count, int p_Depth)
        {
            var s_Indent = "[host]   " + new string(' ', p_Depth * 2);
            for (var i = 0; i < p_Count; ++i)
            {
                if (m_Stack.Count == 0) { m_Log($"{s_Indent}pop: the stack is empty"); return; }
                var s_Top = m_Stack[^1];
                m_Stack.RemoveAt(m_Stack.Count - 1);
                ++Pops;
                m_Window.Dispatch("_level0", "rueUnloadScreen", s_Top.Clip);
                m_Log($"{s_Indent}pop {s_Top.Clip} {s_Top.Screen} ({m_Stack.Count} left{(m_Stack.Count > 0 ? ", top " + m_Stack[^1].Screen : "")})");
                // the popup's button labels stay for the comparison its PopupButtonReleased wire leads to: that wire pops the popup FIRST
                // (NumScreensToPop 1) and the comparison reads the pressed button's label after; the next screen push drops them
            }
        }

        /// <summary>A StateNode's input was hit: its screen is converted, loaded as a new clip on the player and, once loaded, initialised and entered through that port.</summary>
        void PushScreen(NodeInfo p_StateNode, string p_Port, JToken? p_Param, int p_Depth)
        {
            var s_Indent = "[host]   " + new string(' ', p_Depth * 2) + "→ ";
            var s_Guid = (string?)p_StateNode.Json["Screen"]?["PartitionGuid"];
            var s_Partition = s_Guid != null ? m_PartitionByGuid(s_Guid) : null;
            if (s_Partition == null) { m_Log($"{s_Indent}{p_StateNode.Label}.{p_Port}: its screen is not in the game's data"); return; }
            PushScreen(s_Partition, p_StateNode.Label, p_Port, p_Param, p_Depth, p_StateNode, null);
        }

        /// <summary>The push itself, by the screen's partition: for the flow graph's StateNode that asked for it, or the document's own StateNode (a screen the document adds).</summary>
        void PushScreen(string s_Partition, string p_Label, string p_Port, JToken? p_Param, int p_Depth, NodeInfo? p_StateNode, NodeEntry? p_DocStateNode)
        {
            var s_Indent = "[host]   " + new string(' ', p_Depth * 2) + "→ ";
            if (m_Services == null) { m_Log($"{s_Indent}{p_Label}.{p_Port}: would push {s_Partition} (no services to load it here)"); return; }
            var s_Clip = "sc" + m_NextClip++;
            var s_Depth = m_NextDepth++;
            ++Pushes;
            if (p_StateNode?.Type != "DialogNode") m_DialogButtons = null;
            m_Log($"{s_Indent}{p_Label}.{p_Port}: push {s_Partition} as {s_Clip}…{(p_DocStateNode != null ? " (the document's StateNode)" : "")}");
            var s_Partition2 = s_Partition;
            Task.Run(() =>
            {
                ScreenModel? s_Model = null; ScreenEntry? s_Doc = null; DataRecorder.Recording? s_Recording = null; var s_Ok = false; string s_Error = "";
                try
                {
                    s_Model = m_Services.Model(s_Partition2);
                    if (s_Model == null || !s_Model.HasStage) s_Error = "no movie for the screen";
                    else
                    {
                        s_Doc = m_Services.Doc(s_Partition2);
                        s_Recording = m_Services.Recording(s_Model.MovieName);
                        s_Ok = m_Services.Convert(s_Model.MovieName, s_Doc);
                        if (!s_Ok) s_Error = "the conversion failed";
                    }
                }
                catch (Exception s_Ex) { s_Error = s_Ex.Message; }
                m_Window.Dispatcher.InvokeAsync(() =>
                {
                    if (!s_Ok || s_Model == null) { m_Log($"[host] push {s_Partition2} failed: {s_Error}"); return; }
                    var s_Run = new ScreenRun
                    {
                        Clip = s_Clip, Model = s_Model, Doc = s_Doc, Recording = s_Recording, StateNode = p_StateNode, DocStateNode = p_DocStateNode, Entry = p_Port.Contains(':') ? p_Port[(p_Port.IndexOf(':') + 1)..] : p_Port, EntryParam = p_Param,
                        Root = s_Model.Movie != null ? DataRecorder.RootClipName(s_Model.Movie) : "instance1",
                    };
                    m_Stack.Add(s_Run);
                    if (s_Recording != null) { SeedData(s_Recording); m_Window.SetData(Data); }
                    m_Window.Dispatch("_level0", "rueLoadScreen", s_Clip, s_Model.MovieName.Split('/').Last() + ".swf", s_Depth);
                    m_Log($"[host] {s_Clip} {s_Run.Screen} loading (depth {s_Depth}, stack: {string.Join(" < ", Stack)})" + (s_Recording != null ? $" with its recording ({s_Recording.Widgets.Count} widgets)" : ""));
                });
            });
        }

        /// <summary>A DialogNode: its popup screen pushed with the dialog's title, text and buttons on the keys the popup's widgets are bound to.</summary>
        void PushDialog(NodeInfo p_Dialog, string p_Port, JToken? p_Param, int p_Depth)
        {
            var s_Indent = "[host]   " + new string(' ', p_Depth * 2) + "→ ";
            string Resolve(string? s) => s == null ? "" : (m_Texts?.Resolve(s) ?? s);
            var s_Title = (string?)p_Dialog.Json["DialogTitle"] ?? ""; var s_Text = (string?)p_Dialog.Json["DialogText"] ?? "";
            var s_Buttons = (p_Dialog.Json["Buttons"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            m_DialogButtons = s_Buttons.Select(b => (string?)b["Label"] ?? "").ToList();
            var s_ButtonData = new JArray(s_Buttons.Select(b => new JObject { ["Label"] = Resolve((string?)b["Label"]), ["InputConcept"] = b["InputConcept"]?.ToString() ?? "" }));
            // the popup screen's widgets say which keys carry the title, the text and the buttons
            var s_Guid = (string?)p_Dialog.Json["Screen"]?["PartitionGuid"];
            var s_Partition = s_Guid != null ? m_PartitionByGuid(s_Guid) : null;
            var s_Popup = s_Partition != null && m_Services != null ? m_Services.Model(s_Partition) : null;
            var s_Set = new Dictionary<string, object?>();
            if (s_Popup != null)
                foreach (var w in s_Popup.Widgets)
                    foreach (var (s_Name, s_Key, _) in w.Bindings)
                    {
                        object? s_Value = s_Name switch { "Title" => Resolve(s_Title), "Text" or "Description" => Resolve(s_Text), "Buttons" => s_ButtonData, "Visible" => "1", _ => null };
                        if (s_Value != null && s_Key != 0) s_Set[s_Key.ToString()] = s_Value;
                    }
            foreach (var kv in s_Set) Data[kv.Key] = kv.Value;
            if (s_Set.Count > 0) m_Window.SetData(s_Set);
            m_Log($"{s_Indent}{p_Dialog.Label}: dialog \"{Resolve(s_Title)}\" / \"{Resolve(s_Text)}\" [{string.Join(" | ", m_DialogButtons.Select(Resolve))}] → {s_Set.Count} data keys set{(s_Popup == null ? " (popup screen not loaded)" : "")}");
            PushScreen(p_Dialog, p_Port, p_Param, p_Depth);
        }

        /// <summary>
        /// A data key changed: every widget bound to it, on every screen of the stack, gets update&lt;DataName&gt;Data(value), as the engine
        /// refreshes bindings — with the payload the game sent that widget on that channel when the recording has one.
        /// </summary>
        void Refresh(string p_Key, object? p_Value, int p_Depth)
        {
            foreach (var s_Run in m_Stack.Where(r => r.Initialised).ToList())
                foreach (var w in Widgets(s_Run))
                {
                    if (w.Binding == null || (string?)w.Binding["$type"] != "UIDynamicDataBinding") continue;
                    foreach (var s_Source in (w.Binding["Bindings"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        if (s_Source["DataKey"]?.ToString() != p_Key) continue;
                        var s_Name = (string?)s_Source["DataName"] ?? "";
                        if (s_Name == "") continue;
                        var s_Recorded = s_Run.Recording?.RefreshPayload(w.Label, s_Name)?.DeepClone();
                        if (s_Recorded != null) Localise(s_Recorded);
                        // a generated payload carries text ids: localised on a copy, as the engine's translator does on the way to the widget
                        // (a bare text id too: the kit's name is one string)
                        var s_Payload = s_Recorded ?? ToToken(p_Value)?.DeepClone();
                        if (s_Recorded == null && s_Payload != null)
                        {
                            if (s_Payload.Type == JTokenType.String && ((string)s_Payload!).StartsWith("ID_", StringComparison.Ordinal)) s_Payload = m_Texts?.Lookup((string)s_Payload!) is { } s_Text ? s_Text : s_Payload;
                            else Localise(s_Payload);
                        }
                        // the engine's entry for a data update is the WIDGET's refresh({DataName: payload}) — UIWidget.refresh → setupRefreshData →
                        // update<DataName>Data — never the screen's refreshScreen (measured 2026-09-16 on the game's traffic: a weapon change
                        // refreshed the five accessory rows that way, one call each)
                        m_Window.Dispatch(Path(s_Run, w.Label), "refresh", new JObject { [s_Name] = s_Payload ?? JValue.CreateNull() });
                        m_Log($"[host]   {new string(' ', p_Depth * 2)}↻ {w.Label}.refresh({{{s_Name}}}) → update{s_Name}Data({(s_Recorded != null ? "recorded payload " + Short(s_Recorded, 60) : Short(s_Payload, 60))})" + (m_Stack.Count > 1 ? $" [{s_Run.Clip}]" : ""));
                    }
                }
        }
    }
}
