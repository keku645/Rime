using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RimeLib.Cmd.UiBuilder
{
    /// <summary>
    /// What the game's own data says about the UI's building blocks, measured once over the mounted game or the
    /// cache and kept next to the cache as widget_catalog.json:
    ///   . per widget asset: the events its contract declares (UIWidgetAsset.WidgetEvents), the WidgetProperties
    ///     the shipped screens set on it (name, how often, the values used) and the binding types it is fed with;
    ///   . per data component (ui/uicomponents/*): its DataSources and the DataKey each one hashes to;
    ///   . every ui/ partition's primary type (the data explorer's Type column);
    ///   . which port slots the shipped connections join (WidgetNode.Outputs → DataSetNode.In: 309…), the evidence
    ///     behind "compatible" when a wire is dragged.
    /// So the editor can offer a widget's real settings, name a DataKey, and browse the UI database by type.
    /// </summary>
    public class WidgetCatalog
    {
        public const int Version = 4;

        public class PropertyStats
        {
            [JsonProperty("uses")] public int Uses;
            /// <summary>Value -> how many nodes set it (the most common values first).</summary>
            [JsonProperty("values")] public Dictionary<string, int> Values = new();
        }

        public class WidgetInfo
        {
            [JsonProperty("asset")] public string Asset = "";
            [JsonProperty("uses")] public int Uses;
            /// <summary>The widget's event contract: (name the ActionScript fires / receives, UIWidgetEventID without prefix, fired by the widget).</summary>
            [JsonProperty("events")] public List<WidgetEvent> Events = new();
            [JsonProperty("properties")] public Dictionary<string, PropertyStats> Properties = new();
            [JsonProperty("bindings")] public Dictionary<string, int> BindingTypes = new();
            /// <summary>Property names the widget's own ActionScript reads (from its decompiled code), when measured.</summary>
            [JsonProperty("codeProperties")] public List<string> CodeProperties = new();
            /// <summary>How many placements of the widget the screen movies hold (the base of the clip variable counts).</summary>
            [JsonProperty("placements")] public int Placements;
            /// <summary>
            /// The construct-time variables FrostEd bakes into the widget's placements (PlaceObject2 clip actions: m_rowType, m_viewType,
            /// buttonType…), name → how many placements set it and with which values. A placement the editor adds gets the usual ones.
            /// </summary>
            [JsonProperty("clipVars")] public Dictionary<string, PropertyStats> ClipVars = new();

            /// <summary>The variables at least half of the widget's placements set, each with its most common value — what a new placement should carry.</summary>
            public List<(string Name, object? Value)> UsualClipVars()
            {
                var s_Out = new List<(string, object?)>();
                if (Placements == 0) return s_Out;
                foreach (var (s_Name, s_Stats) in ClipVars)
                {
                    if (s_Stats.Uses * 2 < Placements || s_Stats.Values.Count == 0) continue;
                    s_Out.Add((s_Name, Scaleform.ClipActions.ParseValue(s_Stats.Values.OrderByDescending(kv => kv.Value).First().Key)));
                }
                return s_Out;
            }
        }

        public class WidgetEvent
        {
            [JsonProperty("name")] public string Name = "";
            [JsonProperty("query")] public string Query = "";
            [JsonProperty("isOutput")] public bool IsOutput;
        }

        public class ComponentInfo
        {
            /// <summary>Partition name (ui/uicomponents/uicustomizationcomp).</summary>
            [JsonProperty("partition")] public string Partition = "";
            /// <summary>The asset's own Name (UI/UIComponents/UICustomizationComp) — what the key hashes.</summary>
            [JsonProperty("name")] public string Name = "";
            [JsonProperty("type")] public string Type = "";
            [JsonProperty("sources")] public List<string> Sources = new();
            /// <summary>Source name -> DataKey (fb hash of "UI_&lt;component&gt;_&lt;source&gt;" upper-cased).</summary>
            [JsonProperty("keys")] public Dictionary<string, int> Keys = new();
            /// <summary>How many bindings/nodes of the shipped graphs use each source (0 for the unused ones).</summary>
            [JsonProperty("uses")] public Dictionary<string, int> Uses = new();

            public string? SourceOf(int p_Key) => Keys.FirstOrDefault(kv => kv.Value == p_Key).Key;
        }

        /// <summary>Set by Build; a file written by an older build (no version, or a lower one) is rebuilt rather than trusted.</summary>
        [JsonProperty("version")] public int FileVersion;
        [JsonProperty("widgets")] public Dictionary<string, WidgetInfo> Widgets = new(StringComparer.OrdinalIgnoreCase);
        [JsonProperty("components")] public Dictionary<string, ComponentInfo> Components = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Every ui/ partition -> the $type of its primary instance.</summary>
        [JsonProperty("partitionTypes")] public Dictionary<string, string> PartitionTypes = new(StringComparer.OrdinalIgnoreCase);
        [JsonProperty("graphs")] public int Graphs;
        /// <summary>"SourceType.SourceSlot>TargetType.TargetSlot" -> how many shipped connections join those slots (a widget's events count as its Outputs / Inputs).</summary>
        [JsonProperty("wires")] public Dictionary<string, int> Wires = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Shipped connections with both ports on their nodes (the denominator of every "never wired" claim).</summary>
        [JsonProperty("totalWires")] public int TotalWires;

        public WidgetInfo? Get(string p_Asset) => Widgets.TryGetValue(p_Asset, out var w) ? w : null;

        public static string WireKey(string p_FromType, string p_FromSlot, string p_ToType, string p_ToSlot) => $"{p_FromType}.{p_FromSlot}>{p_ToType}.{p_ToSlot}";

        /// <summary>How many shipped connections join a source slot of one node type to a target slot of another.</summary>
        public int WireCount(string p_FromType, string p_FromSlot, string p_ToType, string p_ToSlot) =>
            Wires.TryGetValue(WireKey(p_FromType, p_FromSlot, p_ToType, p_ToSlot), out var n) ? n : 0;

        /// <summary>A component by partition name or by its asset Name.</summary>
        public ComponentInfo? Component(string? p_NameOrPartition)
        {
            if (string.IsNullOrEmpty(p_NameOrPartition)) return null;
            if (Components.TryGetValue(p_NameOrPartition, out var c)) return c;
            return Components.Values.FirstOrDefault(x => string.Equals(x.Name, p_NameOrPartition, StringComparison.OrdinalIgnoreCase));
        }

        public static string FileFor(string p_CacheDir) => Path.Combine(p_CacheDir, "widget_catalog.json");

        public static WidgetCatalog? LoadCached(string? p_CacheDir)
        {
            if (p_CacheDir == null || !File.Exists(FileFor(p_CacheDir))) return null;
            try
            {
                var c = JsonConvert.DeserializeObject<WidgetCatalog>(File.ReadAllText(FileFor(p_CacheDir)));
                return c != null && c.FileVersion == Version ? c : null;   // an older file is rebuilt
            }
            catch { return null; }
        }

        /// <summary>Written to a temporary file first, so a reader never sees a half-written catalogue.</summary>
        public void Save(string p_CacheDir)
        {
            var s_Tmp = FileFor(p_CacheDir) + ".tmp";
            File.WriteAllText(s_Tmp, JsonConvert.SerializeObject(this, Formatting.Indented));
            File.Move(s_Tmp, FileFor(p_CacheDir), true);
        }

        /// <summary>Measures every UI graph the service lists (ui/flow/screen + ui/flow/graph), every widget asset, every component and every ui/ partition's type.</summary>
        public static WidgetCatalog Build(RimeUiService p_Rime, Action<int, int, string>? p_Progress = null)
        {
            var s_Catalog = new WidgetCatalog { FileVersion = Version };
            static string? Sub(JToken? t, string k) => t is JObject o ? (string?)o[k] : null;
            // every ui/ partition's primary type (also fills the guid map on the way)
            var s_All = p_Rime.Partitions("ui/").ToList();
            var s_Done = 0;
            foreach (var s_Name in s_All)
            {
                try
                {
                    var s_Json = p_Rime.PartitionJson(s_Name);
                    var s_Type = (s_Json["Instances"] as JObject)?[(string?)s_Json["PrimaryInstanceGuid"] ?? ""]?["$type"]?.ToString();
                    if (s_Type != null) s_Catalog.PartitionTypes[s_Name] = s_Type;
                }
                catch { /* a partition the cache lacks */ }
                if (++s_Done % 100 == 0) p_Progress?.Invoke(s_Done, s_All.Count, s_Name);
            }
            // the widget contracts, from the assets themselves
            foreach (var s_Asset in s_All.Where(n => n.StartsWith("ui/assets/", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var s_Json = p_Rime.PartitionJson(s_Asset);
                    var s_Prim = (s_Json["Instances"] as JObject)?[(string?)s_Json["PrimaryInstanceGuid"] ?? ""] as JObject;
                    if (s_Prim == null || (string?)s_Prim["$type"] != "UIWidgetAsset") continue;
                    var w = new WidgetInfo { Asset = s_Asset };
                    foreach (var e in (s_Prim["WidgetEvents"] as JArray ?? new JArray()).OfType<JObject>())
                        w.Events.Add(new WidgetEvent { Name = (string?)e["Name"] ?? "", Query = ((string?)e["Query"] ?? "").Replace("UIWidgetEventID_", ""), IsOutput = (bool?)e["IsOutput"] ?? false });
                    s_Catalog.Widgets[s_Asset] = w;
                }
                catch { /* not every ui/assets partition is a widget */ }
            }
            // the components and their keys
            foreach (var s_Part in s_All.Where(n => n.StartsWith("ui/uicomponents/", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var s_Json = p_Rime.PartitionJson(s_Part);
                    var s_Prim = (s_Json["Instances"] as JObject)?[(string?)s_Json["PrimaryInstanceGuid"] ?? ""] as JObject;
                    if (s_Prim == null || s_Prim["DataSources"] is not JArray s_Sources) continue;
                    var c = new ComponentInfo { Partition = s_Part, Name = (string?)s_Prim["Name"] ?? s_Part, Type = (string?)s_Prim["$type"] ?? "" };
                    foreach (var s in s_Sources.Select(x => (string?)x).Where(x => !string.IsNullOrEmpty(x)))
                    {
                        c.Sources.Add(s!);
                        c.Keys[s!] = DataKeys.Compute(c.Name, s!);
                        c.Uses[s!] = 0;
                    }
                    s_Catalog.Components[s_Part] = c;
                }
                catch { /* not a component */ }
            }
            var s_ComponentByGuid = new Dictionary<string, ComponentInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in s_Catalog.Components.Values)
            {
                try { s_ComponentByGuid[(string)p_Rime.PartitionJson(c.Partition)["PartitionGuid"]!] = c; } catch { }
            }
            // the uses, from every graph
            var s_Graphs = s_All.Where(n => n.StartsWith("ui/flow/screen/", StringComparison.OrdinalIgnoreCase) || n.StartsWith("ui/flow/graph/", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var s_Graph in s_Graphs)
            {
                JObject s_Json;
                try { s_Json = p_Rime.PartitionJson(s_Graph); } catch { continue; }
                if (s_Json["Instances"] is not JObject s_Instances) continue;
                if (s_Catalog.PartitionTypes.TryGetValue(s_Graph, out var s_GraphType) && s_GraphType is not ("UIScreenAsset" or "UIGraphAsset")) continue;   // ui/flow/graph also holds audio event assets
                s_Catalog.Graphs++;
                // which slots the connections join: port guid -> (owner type, slot), then every UINodeConnection
                var s_PortSlot = new Dictionary<string, (string Type, string Slot)>();
                foreach (var s_Inst in s_Instances.Properties().Select(p => p.Value).OfType<JObject>())
                {
                    var s_Type = (string?)s_Inst["$type"] ?? "";
                    if (s_Type.Contains("NodePort") || s_Type == "UINodeConnection") continue;
                    foreach (var p in s_Inst.Properties())
                    {
                        if (p.Value is JObject s_One && Sub(s_One, "InstanceGuid") is { } g1 && s_Instances[g1] is JObject o1 && ((string?)o1["$type"] ?? "").Contains("NodePort")) s_PortSlot[g1] = (s_Type, p.Name);
                        else if (p.Value is JArray s_Many)
                            foreach (var e in s_Many.OfType<JObject>())
                                if (Sub(e, "InstanceGuid") is { } g2 && s_Instances[g2] is JObject o2 && ((string?)o2["$type"] ?? "").Contains("NodePort")) s_PortSlot[g2] = (s_Type, p.Name);
                    }
                }
                foreach (var s_Conn in s_Instances.Properties().Select(p => p.Value).OfType<JObject>().Where(o => (string?)o["$type"] == "UINodeConnection"))
                {
                    var s_Src = Sub(s_Conn["SourcePort"], "InstanceGuid"); var s_Dst = Sub(s_Conn["TargetPort"], "InstanceGuid");
                    if (s_Src == null || s_Dst == null || !s_PortSlot.TryGetValue(s_Src, out var s_From) || !s_PortSlot.TryGetValue(s_Dst, out var s_To)) continue;
                    // JumpNode.TargetPort is a reference to another node's port, never a slot of its own: the 2 shipped wires that name it are dead data
                    if (UiTypeCatalog.IsPortReference(s_To.Type, s_To.Slot) || UiTypeCatalog.IsPortReference(s_From.Type, s_From.Slot)) continue;
                    var s_Key = WireKey(s_From.Type, s_From.Slot, s_To.Type, s_To.Slot);
                    s_Catalog.Wires[s_Key] = s_Catalog.Wires.TryGetValue(s_Key, out var s_N) ? s_N + 1 : 1;
                    s_Catalog.TotalWires++;
                }
                foreach (var s_Inst in s_Instances.Properties().Select(p => p.Value).OfType<JObject>())
                {
                    // every UIDataSourceInfo anywhere in the instance: count the source it names
                    var s_Stack = new Stack<JToken>(); s_Stack.Push(s_Inst);
                    while (s_Stack.Count > 0)
                    {
                        var x = s_Stack.Pop();
                        if (x is JObject o)
                        {
                            if (o["DataKey"] != null && o["DataCategory"] is JObject s_Cat && Sub(s_Cat, "PartitionGuid") is { } s_CatGuid && s_ComponentByGuid.TryGetValue(s_CatGuid, out var s_Comp))
                            {
                                var s_Source = s_Comp.SourceOf((int?)o["DataKey"] ?? 0);
                                if (s_Source != null) s_Comp.Uses[s_Source] = s_Comp.Uses.TryGetValue(s_Source, out var u) ? u + 1 : 1;
                            }
                            foreach (var p in o.Properties()) s_Stack.Push(p.Value);
                        }
                        else if (x is JArray a) foreach (var i in a) s_Stack.Push(i);
                    }
                    if ((string?)s_Inst["$type"] != "WidgetNode") continue;
                    var s_AssetGuid = Sub(s_Inst["WidgetAsset"], "PartitionGuid");
                    var s_Asset = s_AssetGuid != null ? p_Rime.PartitionNameByGuid(s_AssetGuid) : null;
                    if (s_Asset == null) continue;
                    if (!s_Catalog.Widgets.TryGetValue(s_Asset, out var w)) { w = new WidgetInfo { Asset = s_Asset }; s_Catalog.Widgets[s_Asset] = w; }
                    w.Uses++;
                    foreach (var p in (s_Inst["WidgetProperties"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        var s_Name = (string?)p["Name"] ?? ""; var s_Value = (string?)p["Value"] ?? "";
                        if (!w.Properties.TryGetValue(s_Name, out var s_Stats)) { s_Stats = new PropertyStats(); w.Properties[s_Name] = s_Stats; }
                        s_Stats.Uses++;
                        s_Stats.Values[s_Value] = s_Stats.Values.TryGetValue(s_Value, out var c) ? c + 1 : 1;
                    }
                    var s_BindGuid = Sub(s_Inst["DataBinding"], "InstanceGuid");
                    var s_BindType = s_BindGuid != null && s_Instances[s_BindGuid] is JObject b ? (string?)b["$type"] : null;
                    if (s_BindType != null) w.BindingTypes[s_BindType] = w.BindingTypes.TryGetValue(s_BindType, out var n) ? n + 1 : 1;
                }
            }
            // the construct-time variables of every placement, from the screen movies (the widget = the import the placement's character comes from)
            foreach (var s_Screen in s_Graphs.Where(n => n.Contains("/flow/screen/", StringComparison.OrdinalIgnoreCase)))
            {
                Scaleform.GfxMovie s_Movie;
                try { s_Movie = Scaleform.GfxMovie.Load(p_Rime.ResourceBytes("ui/assets/" + s_Screen.Split('/').Last())); }
                catch { continue; }
                var s_Imports = new Dictionary<ushort, string>();
                foreach (var i in s_Movie.Imports()) foreach (var (s_Id, _) in i.Characters) s_Imports[s_Id] = i.Url;
                foreach (var sp in s_Movie.StagePlacements())
                {
                    if (!s_Imports.TryGetValue(sp.Place.CharacterId, out var s_Url)) continue;
                    var s_Asset = "ui/assets/" + s_Url.Split('/').Last().ToLowerInvariant().Replace(".swf", "");
                    if (!s_Catalog.Widgets.TryGetValue(s_Asset, out var w)) { w = new WidgetInfo { Asset = s_Asset }; s_Catalog.Widgets[s_Asset] = w; }
                    w.Placements++;
                    List<(string Name, object? Value)>? s_Vars = null;
                    try { s_Vars = sp.Place.ClipVars; } catch { /* a record this parser does not read: counted as a placement without */ }
                    foreach (var (s_Name, s_Value) in s_Vars ?? new List<(string, object?)>())
                    {
                        if (!w.ClipVars.TryGetValue(s_Name, out var s_Stats)) { s_Stats = new PropertyStats(); w.ClipVars[s_Name] = s_Stats; }
                        s_Stats.Uses++;
                        var s_Text = Scaleform.ClipActions.FormatValue(s_Value);
                        s_Stats.Values[s_Text] = s_Stats.Values.TryGetValue(s_Text, out var c) ? c + 1 : 1;
                    }
                }
            }
            foreach (var w in s_Catalog.Widgets.Values)
            {
                foreach (var s in w.Properties.Values)
                    s.Values = s.Values.OrderByDescending(kv => kv.Value).Take(12).ToDictionary(kv => kv.Key, kv => kv.Value);
                foreach (var s in w.ClipVars.Values)
                    s.Values = s.Values.OrderByDescending(kv => kv.Value).Take(12).ToDictionary(kv => kv.Key, kv => kv.Value);
            }
            p_Progress?.Invoke(s_All.Count, s_All.Count, "done");
            return s_Catalog;
        }
    }
}
