using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RimeLib.Cmd.UiBuilder
{
    /// <summary>
    /// The document's graph edits applied to a screen's partition JSON (the dump's shape: instances keyed by guid, references as
    /// {PartitionGuid, InstanceGuid}, enums by member name, structs inline). The result is the partition the bundle ships under the
    /// screen's own name and guid, in place of the game's copy — the client then needs no runtime edits. Everything mirrors what
    /// the generated Lua does at load (LuaTemplates): the same nodes, properties, bindings, ports, wires and removed wires, born
    /// with the same deterministic guids, so the two deliveries describe the same graph.
    /// </summary>
    public static class StaticGraph
    {
        sealed class Ctx
        {
            public ScreenDocument Doc = null!;
            public ScreenEntry Screen = null!;
            public JObject Instances = null!;
            public JObject Asset = null!;
            public string Partition = "", Primary = "";
            public bool IsScreen = true;
            public Func<string, JObject> PartitionJson = null!;
            public TextWriter Log = null!;
            public readonly Dictionary<string, (string Guid, JObject Node)> ByLabel = new(StringComparer.Ordinal);
            public int Added, Edited, Wired, Unwired;
        }

        static string G(params string[] p_Parts) => ModEmitter.StableGuid(p_Parts).ToLowerInvariant();

        static JObject Ref(string p_Partition, string p_Instance) => new() { ["PartitionGuid"] = p_Partition, ["InstanceGuid"] = p_Instance };

        static bool SameGuid(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>A child of an object, or null — the dump writes a null reference as a JSON null, and JSON.NET throws on ?[] over one.</summary>
        static JToken? Sub(JToken? p_Token, string p_Name) => p_Token is JObject o ? o[p_Name] : null;
        static string? GuidOf(JToken? p_Ref) => (string?)Sub(p_Ref, "InstanceGuid");

        /// <summary>The node's label the way the Lua and the editor name it: InstanceName when it has one, else Name.</summary>
        static string LabelOf(JObject p_Node)
        {
            var s_Instance = (string?)p_Node["InstanceName"];
            return !string.IsNullOrEmpty(s_Instance) ? s_Instance : (string?)p_Node["Name"] ?? "";
        }

        /// <summary>
        /// Applies one screen entry to its partition dump. p_PartitionJson resolves any partition the edits reference (widget assets,
        /// data components, screens — the document's own new screens included).
        /// </summary>
        public static JObject Apply(ScreenDocument p_Doc, ScreenEntry p_Screen, JObject p_Vanilla, Func<string, JObject> p_PartitionJson, TextWriter p_Log)
        {
            var s_Json = (JObject)p_Vanilla.DeepClone();
            var c = new Ctx
            {
                Doc = p_Doc, Screen = p_Screen, PartitionJson = p_PartitionJson, Log = p_Log,
                Instances = (JObject)s_Json["Instances"]!, Partition = (string)s_Json["PartitionGuid"]!, Primary = (string)s_Json["PrimaryInstanceGuid"]!,
            };
            c.Asset = (JObject)c.Instances[c.Primary]!;
            c.IsScreen = (string?)c.Asset["$type"] != "UIGraphAsset";
            foreach (var s_Ref in (JArray)c.Asset["Nodes"]!)
            {
                var s_Guid = GuidOf(s_Ref);
                if (s_Guid != null && FindInstance(c, s_Guid) is { } s_Node && !c.ByLabel.ContainsKey(LabelOf(s_Node))) c.ByLabel[LabelOf(s_Node)] = (s_Guid, s_Node);
            }
            var s_AssetInfo = UiTypeCatalog.Describe((string)c.Asset["$type"]!);

            // the asset's own fields
            foreach (var (s_Name, s_Value) in p_Screen.Fields)
            {
                if (s_Name is "Nodes" or "Connections") continue;
                c.Asset[s_Name] = ToDump(c, s_AssetInfo?.Field(s_Name), s_Value, p_Doc.Name + "|" + p_Screen.Partition + "|screen|" + s_Name);
            }

            // shipped wires the document takes out: the connection instance goes, and its entry in the asset's list
            foreach (var r in p_Screen.RemovedConnections)
            {
                var s_Key = c.Instances.Properties().FirstOrDefault(p => SameGuid(p.Name, r.Guid))?.Name;
                var s_List = (JArray)c.Asset["Connections"]!;
                var s_Entry = s_List.FirstOrDefault(x => SameGuid(GuidOf(x), r.Guid));
                if (s_Key == null || s_Entry == null) { p_Log.WriteLine($"WARNING: removed connection {r.From} -> {r.To} ({r.Guid}) is not on {p_Screen.Partition} -- skipped"); continue; }
                c.Instances.Remove(s_Key); s_Entry.Remove(); c.Unwired++;
            }

            // every node first (a wire may lead to a node listed later in the document), the wires after
            foreach (var e in p_Screen.Nodes) ApplyNode(c, e);
            foreach (var e in p_Screen.Nodes)
                if (c.ByLabel.TryGetValue(e.InstanceName, out var s_Wired))
                    foreach (var w in e.Connections) Wire(c, e, KeyOf(c, e), s_Wired.Guid, s_Wired.Node, w);
            p_Log.WriteLine($"static graph {p_Screen.Partition}: {c.Added} node(s) added, {c.Edited} edited, {c.Wired} wire(s), {c.Unwired} shipped wire(s) removed -> {c.Instances.Count} instances");
            return s_Json;
        }

        static JObject? FindInstance(Ctx c, string p_Guid)
        {
            var p = c.Instances.Properties().FirstOrDefault(p => SameGuid(p.Name, p_Guid));
            return p?.Value as JObject;
        }

        static string KeyOf(Ctx c, NodeEntry e) => c.Doc.Name + "|" + c.Screen.Partition + "|" + e.InstanceName;

        static void ApplyNode(Ctx c, NodeEntry e)
        {
            var s_Type = string.IsNullOrEmpty(e.Type) ? "WidgetNode" : e.Type;
            var s_IsWidget = s_Type == "WidgetNode";
            var s_Info = UiTypeCatalog.Describe(s_Type);
            var s_Key = KeyOf(c, e);
            JObject s_Node; string s_Guid;
            if (e.New)
            {
                s_Guid = G(s_Key, "node");
                s_Node = new JObject
                {
                    ["$type"] = s_Type, ["Name"] = e.InstanceName, ["ParentGraph"] = Ref(c.Partition, c.Primary), ["IsRootNode"] = false, ["ParentIsScreen"] = c.IsScreen,
                };
                if (s_IsWidget)
                {
                    if (e.Widget == null) { c.Log.WriteLine($"WARNING: new widget node {e.InstanceName} has no widget asset -- skipped"); return; }
                    s_Node["WidgetAsset"] = AssetRef(c, e.Widget);
                    s_Node["FocusIndex"] = e.FocusIndex; s_Node["ZDepthLevel"] = e.ZDepthLevel;
                    s_Node["VerticalAlign"] = "WVA_Center"; s_Node["HorisontalAlign"] = "WHA_Center";
                    s_Node["DataBinding"] = null; s_Node["WidgetProperties"] = new JArray(); s_Node["InstanceName"] = e.InstanceName;
                    s_Node["Inputs"] = new JArray(); s_Node["Outputs"] = new JArray(); s_Node["AlwaysInFocus"] = false;
                }
                else if (s_Info != null)
                {
                    foreach (var f in s_Info.Fields)
                    {
                        if (f.Name is "Name" or "ParentGraph" or "IsRootNode" or "ParentIsScreen") continue;
                        if (f.Kind == UiTypeCatalog.Kind.Port)
                        {
                            if (UiTypeCatalog.IsPortReference(s_Type, f.Name)) { s_Node[f.Name] = e.Fields.TryGetValue(f.Name, out var s_Spec) ? PortRef(c, (string?)s_Spec) : null; continue; }
                            // the node's own single port (In/Out/True/False/Show/Hide), born with the Lua's guid
                            s_Node[f.Name] = Ref(c.Partition, NewPort(c, G(s_Key, "port", f.Name), f.Name, f.Name, null, null));
                            continue;
                        }
                        if (f.Kind == UiTypeCatalog.Kind.PortArray) { s_Node[f.Name] = new JArray(); continue; }
                        // a screen output's Id is the engine's hash of its name (the four outputs of the accessories screen: Confirm,
                        // AccessoryChanged, WeaponChanged, Spawn — all djb2-xor of the name), unless the document says otherwise
                        if (s_Type == "InstanceOutputNode" && f.Name == "Id" && !e.Fields.ContainsKey("Id")) { s_Node["Id"] = DataKeys.FbHash(e.InstanceName); continue; }
                        s_Node[f.Name] = e.Fields.TryGetValue(f.Name, out var v) ? ToDump(c, f, v, s_Key + "|" + f.Name) : DefaultDump(f);
                    }
                }
                else c.Log.WriteLine($"WARNING: node {e.InstanceName}: '{s_Type}' is not a known UI node type");
                c.Instances[s_Guid] = s_Node;
                ((JArray)c.Asset["Nodes"]!).Add(Ref(c.Partition, s_Guid));
                c.ByLabel[e.InstanceName] = (s_Guid, s_Node);
                c.Added++;
                c.Log.WriteLine($"  [NODE] new {s_Type} {e.InstanceName}{(e.Widget != null ? " (" + e.Widget + ")" : "")} = {s_Guid}");
            }
            else
            {
                if (!c.ByLabel.TryGetValue(e.InstanceName, out var s_Found)) { c.Log.WriteLine($"  [NODE] {e.InstanceName} NOT found on {c.Screen.Partition} -- skipped"); return; }
                (s_Guid, s_Node) = s_Found;
                if (s_IsWidget && e.Widget != null) s_Node["WidgetAsset"] = AssetRef(c, e.Widget);
                foreach (var (s_Name, s_Value) in e.Fields)
                {
                    if (s_Name == "DataBinding" && s_IsWidget) continue;   // below, with the binding
                    var f = s_Info?.Field(s_Name);
                    if (f != null && UiTypeCatalog.IsPortReference(s_Type, s_Name)) { s_Node[s_Name] = PortRef(c, (string?)s_Value); continue; }
                    if (f != null && f.Kind is UiTypeCatalog.Kind.Port or UiTypeCatalog.Kind.PortArray) { c.Log.WriteLine($"WARNING: {e.InstanceName}.{s_Name} is a port: use 'ports'/'connections'"); continue; }
                    s_Node[s_Name] = ToDump(c, f, s_Value, s_Key + "|" + s_Name);
                }
                c.Edited++;
                c.Log.WriteLine($"  [NODE] edit {e.InstanceName}");
            }

            // named array ports asked for (a ComparisonLogicNode's "0"/"1", a StateNode's controller action)
            foreach (var p in e.Ports)
            {
                var s_Array = s_Node[p.Field] as JArray;
                if (s_Array == null) { s_Array = new JArray(); s_Node[p.Field] = s_Array; }
                if (s_Array.Any(x => (string?)PortOf(c, x)?["Name"] == p.Name)) continue;
                s_Array.Add(Ref(c.Partition, NewPort(c, G(s_Key, "aport", p.Field, p.Name), p.Name, p.InstanceName ?? p.Name, p.Event, p.InputEvent)));
            }

            if (s_IsWidget)
            {
                // properties: replaced as a whole (the document holds the list) unless asked to keep the shipped ones
                if (e.New || e.Properties.Count > 0 || e.ReplaceProperties)
                {
                    var s_Props = s_Node["WidgetProperties"] as JArray ?? new JArray();
                    if (e.ReplaceProperties) s_Props = new JArray();
                    foreach (var (k, v) in e.Properties)
                    {
                        var s_Existing = s_Props.FirstOrDefault(x => (string?)x["Name"] == k);
                        if (s_Existing != null) s_Existing["Value"] = v; else s_Props.Add(new JObject { ["Name"] = k, ["Value"] = v });
                    }
                    s_Node["WidgetProperties"] = s_Props;
                }
                ApplyBinding(c, e, s_Key, s_Node);
            }

            // the node's wires are made once every node of the document exists (Apply)
        }

        // ------------------------------------------------------------------------------------------ bindings

        static void ApplyBinding(Ctx c, NodeEntry e, string p_Key, JObject p_Node)
        {
            var s_Current = GuidOf(p_Node["DataBinding"]) is { } g ? FindInstance(c, g) : null;
            if (e.Fields.TryGetValue("DataBinding", out var s_Inline) && s_Inline is JObject s_Obj && (string?)s_Obj["$type"] is { } s_InstType)
            {
                // an instance described inline (any binding type): edited in place when the node carries one of that type, else made
                var s_Info = UiTypeCatalog.Describe(s_InstType);
                if (s_Current != null && (string?)s_Current["$type"] == s_InstType)
                {
                    foreach (var p in s_Obj.Properties().Where(p => p.Name != "$type")) s_Current[p.Name] = ToDump(c, s_Info?.Field(p.Name), p.Value, p_Key + "|DataBinding|" + p.Name);
                    c.Log.WriteLine($"    [BIND] {e.InstanceName}: {s_InstType} edited in place");
                }
                else
                {
                    var s_Guid = G(p_Key + "|DataBinding", "instance", s_InstType);
                    var s_New = new JObject { ["$type"] = s_InstType };
                    if (s_Info != null)
                        foreach (var f in s_Info.Fields)
                            s_New[f.Name] = s_Obj[f.Name] is { } v ? ToDump(c, f, v, p_Key + "|DataBinding|" + f.Name) : DefaultDump(f);
                    foreach (var p in s_Obj.Properties().Where(p => p.Name != "$type" && s_New[p.Name] == null)) s_New[p.Name] = ToDump(c, null, p.Value, p_Key + "|DataBinding|" + p.Name);
                    c.Instances[s_Guid] = s_New;
                    p_Node["DataBinding"] = Ref(c.Partition, s_Guid);
                    c.Log.WriteLine($"    [BIND] {e.InstanceName}: new {s_InstType} = {s_Guid}");
                }
                return;
            }
            if (e.Binding == null) return;
            var b = e.Binding;
            JToken? s_Category = null;
            if (b.Category != null) s_Category = AssetRef(c, b.Category);
            else if (b.CategoryFromNode != null && c.ByLabel.TryGetValue(b.CategoryFromNode, out var s_From) && GuidOf(s_From.Node["DataBinding"]) is { } fg)
                s_Category = (FindInstance(c, fg)?["Bindings"] is JArray { Count: > 0 } s_FromList ? Sub(s_FromList[0], "DataCategory") : null)?.DeepClone();
            var s_Source = new JObject
            {
                ["DataName"] = b.DataName, ["DataCategory"] = s_Category, ["DataKey"] = b.DataKey,
                ["UseDirectAccess"] = b.UseDirectAccess, ["UpdateOnInitialize"] = b.UpdateOnInitialize,
            };
            if (s_Current != null && (string?)s_Current["$type"] == "UIDynamicDataBinding" && s_Current["Bindings"] is JArray { Count: > 0 } s_List)
            {
                s_List[0] = s_Source;
                c.Log.WriteLine($"    [BIND] {e.InstanceName} rewired to {b.DataName} / {b.DataKey}");
            }
            else
            {
                var s_Guid = G(p_Key, "binding");
                c.Instances[s_Guid] = new JObject { ["$type"] = "UIDynamicDataBinding", ["Bindings"] = new JArray(s_Source), ["Refresh"] = true };
                p_Node["DataBinding"] = Ref(c.Partition, s_Guid);
                c.Log.WriteLine($"    [BIND] {e.InstanceName} new binding {b.DataName} / {b.DataKey} = {s_Guid}");
            }
        }

        // ------------------------------------------------------------------------------------------ ports and wires

        static JObject? PortOf(Ctx c, JToken p_Ref) => GuidOf(p_Ref) is { } g ? FindInstance(c, g) : null;

        /// <summary>A new UINodePort (or UIInputEventNodePort) instance; returns its guid.</summary>
        static string NewPort(Ctx c, string p_Guid, string p_Name, string p_InstanceName, string? p_Event, string? p_InputEvent)
        {
            var s_Port = new JObject
            {
                ["$type"] = p_InputEvent != null ? "UIInputEventNodePort" : "UINodePort",
                ["Name"] = p_Name, ["InstanceName"] = p_InstanceName,
                ["Query"] = p_Event != null ? EnumMember("UIWidgetEventID", p_Event) : "UIWidgetEventID_None",
                ["AllowManualRemove"] = false,
            };
            if (p_InputEvent != null) s_Port["InputEventType"] = EnumMember("UIInputAction", p_InputEvent);
            c.Instances[p_Guid] = s_Port;
            return p_Guid;
        }

        static string EnumMember(string p_Enum, string p_Value)
        {
            var s_Members = UiTypeCatalog.EnumMembers(p_Enum);
            if (s_Members.Contains(p_Value)) return p_Value;
            return s_Members.Contains(p_Enum + "_" + p_Value) ? p_Enum + "_" + p_Value : p_Value;
        }

        /// <summary>"Node.port" → a reference to that node's port (JumpNode.TargetPort).</summary>
        static JToken? PortRef(Ctx c, string? p_Spec)
        {
            if (string.IsNullOrEmpty(p_Spec) || !p_Spec.Contains('.')) return null;
            var s_Dot = p_Spec.IndexOf('.');
            if (!c.ByLabel.TryGetValue(p_Spec[..s_Dot], out var s_Target)) { c.Log.WriteLine($"WARNING: port reference '{p_Spec}': node not found"); return null; }
            var s_Port = ResolvePort(c, s_Target.Node, p_Spec[(s_Dot + 1)..], true, null, null);
            return s_Port == null ? null : Ref(c.Partition, s_Port);
        }

        /// <summary>The field a port spec names, whatever the spelling (In, inValue, Outputs:Name, outputs:Name).</summary>
        static (string Field, string? Name) SplitSpec(JObject p_Node, string p_Spec)
        {
            var i = p_Spec.IndexOf(':');
            var s_Field = i < 0 ? p_Spec : p_Spec[..i];
            var s_Name = i < 0 ? null : p_Spec[(i + 1)..];
            // the node's own spelling of the field (a legacy Lua spelling, inValue/trueValue, maps back)
            var s_Real = p_Node.Properties().Select(p => p.Name).FirstOrDefault(n => string.Equals(n, s_Field, StringComparison.OrdinalIgnoreCase) || string.Equals(UiTypeCatalog.LuaName(n), s_Field, StringComparison.OrdinalIgnoreCase));
            return (s_Real ?? s_Field, s_Name);
        }

        /// <summary>The guid of the port a spec names on a node; an array port that does not exist yet is made when p_MakeGuid is given.</summary>
        static string? ResolvePort(Ctx c, JObject p_Node, string p_Spec, bool p_Input, string? p_MakeGuid, string? p_Event)
        {
            var (s_Field, s_Name) = SplitSpec(p_Node, p_Spec);
            if (s_Name == null)
            {
                return GuidOf(p_Node[s_Field]);
            }
            var s_Array = p_Node[s_Field] as JArray;
            if (s_Array == null) { s_Array = new JArray(); p_Node[s_Field] = s_Array; }
            foreach (var x in s_Array)
            {
                var s_Port = PortOf(c, x);
                if (s_Port != null && (string.Equals((string?)s_Port["Name"], s_Name, StringComparison.OrdinalIgnoreCase) || (p_Event != null && (string?)s_Port["Query"] == EnumMember("UIWidgetEventID", p_Event))))
                    return GuidOf(x);
            }
            if (p_MakeGuid == null) return null;
            var s_New = NewPort(c, p_MakeGuid, s_Name, s_Name, p_Event, null);
            s_Array.Add(Ref(c.Partition, s_New));
            return s_New;
        }

        /// <summary>A widget's port for an event, by query, in Inputs or Outputs; made (named after the node, the way the shipped ones are) when missing.</summary>
        static string PortByEvent(Ctx c, JObject p_Node, string p_Label, bool p_Input, string p_Event, string p_MakeGuid)
        {
            var s_Field = p_Input ? "Inputs" : "Outputs";
            var s_Array = p_Node[s_Field] as JArray;
            if (s_Array == null) { s_Array = new JArray(); p_Node[s_Field] = s_Array; }
            var s_Query = EnumMember("UIWidgetEventID", p_Event);
            foreach (var x in s_Array)
                if (PortOf(c, x) is { } s_Port && (string?)s_Port["Query"] == s_Query) return GuidOf(x)!;
            var s_New = NewPort(c, p_MakeGuid, p_Label, p_Label, p_Event, null);
            s_Array.Add(Ref(c.Partition, s_New));
            c.Log.WriteLine($"    [PORT] {p_Label} new {(p_Input ? "input" : "output")} port {p_Event}");
            return s_New;
        }

        static void Wire(Ctx c, NodeEntry e, string p_Key, string p_Guid, JObject p_Node, ConnectionEntry w)
        {
            var s_From = w.Event ?? w.FromPort ?? "Out";
            var s_To = w.ToEvent ?? w.ToPort ?? w.ToField ?? "inValue";
            var s_CKey = p_Key + "|" + s_From + ">" + w.ToNode + "." + s_To;
            if (!c.ByLabel.TryGetValue(w.ToNode, out var s_Target)) { c.Log.WriteLine($"    [WIRE] {e.InstanceName}.{s_From} -> {w.ToNode}.{s_To}: target node not found -- SKIPPED"); return; }
            var s_SourcePort = w.Event != null
                ? PortByEvent(c, p_Node, e.InstanceName, false, w.Event, G(s_CKey, "port"))
                : ResolvePort(c, p_Node, w.FromPort ?? "Out", false, G(s_CKey, "port"), null);
            var s_TargetPort = w.ToEvent != null
                ? PortByEvent(c, s_Target.Node, w.ToNode, true, w.ToEvent, G(s_CKey, "tport"))
                : ResolvePort(c, s_Target.Node, w.ToPort ?? w.ToField ?? "In", true, G(s_CKey, "tport"), null);
            if (s_SourcePort == null || s_TargetPort == null) { c.Log.WriteLine($"    [WIRE] {e.InstanceName}.{s_From} -> {w.ToNode}.{s_To}: {(s_SourcePort == null ? "source" : "target")} port not found -- SKIPPED"); return; }
            var s_Guid = G(s_CKey, "conn");
            c.Instances[s_Guid] = new JObject
            {
                ["$type"] = "UINodeConnection",
                ["SourceNode"] = Ref(c.Partition, p_Guid), ["TargetNode"] = Ref(c.Partition, s_Target.Guid),
                ["SourcePort"] = Ref(c.Partition, s_SourcePort), ["TargetPort"] = Ref(c.Partition, s_TargetPort),
                ["NumScreensToPop"] = w.Pop,
            };
            ((JArray)c.Asset["Connections"]!).Add(Ref(c.Partition, s_Guid));
            c.Wired++;
            c.Log.WriteLine($"    [WIRE] {e.InstanceName}.{s_From} -> {w.ToNode}.{s_To} = {s_Guid}{(w.Pop > 0 ? $" (pops {w.Pop})" : "")}");
        }

        // ------------------------------------------------------------------------------------------ values

        /// <summary>A reference to another partition's primary instance (a widget asset, a data component, a screen).</summary>
        static JToken AssetRef(Ctx c, string p_Partition)
        {
            var j = c.PartitionJson(p_Partition);
            return Ref((string)j["PartitionGuid"]!, (string)j["PrimaryInstanceGuid"]!);
        }

        /// <summary>A document value in the dump's shape, typed by the catalogue's field (refs resolved, structs completed, enums spelled out).</summary>
        static JToken? ToDump(Ctx c, UiTypeCatalog.FieldInfo? f, JToken v, string p_Key)
        {
            var inv = CultureInfo.InvariantCulture;
            if (v.Type == JTokenType.Null) return null;
            // an inline instance (a binding described in the document): its own instance, referenced
            if ((f == null || f.Kind == UiTypeCatalog.Kind.Ref) && v is JObject s_Inst && (string?)s_Inst["$type"] is { } s_InstType)
            {
                var s_Info = UiTypeCatalog.Describe(s_InstType);
                var s_Guid = G(p_Key, "instance", s_InstType);
                var s_New = new JObject { ["$type"] = s_InstType };
                if (s_Info != null)
                    foreach (var sf in s_Info.Fields)
                        s_New[sf.Name] = s_Inst[sf.Name] is { } iv ? ToDump(c, sf, iv, p_Key + "|" + sf.Name) : DefaultDump(sf);
                c.Instances[s_Guid] = s_New;
                return Ref(c.Partition, s_Guid);
            }
            var s_Kind = f?.Kind ?? GuessKind(v);
            switch (s_Kind)
            {
                case UiTypeCatalog.Kind.Bool: return v.Type == JTokenType.Boolean ? (bool)v : string.Equals((string?)v, "true", StringComparison.OrdinalIgnoreCase);
                case UiTypeCatalog.Kind.Int: return v.Type == JTokenType.Integer ? (long)v : long.TryParse((string?)v, NumberStyles.Integer, inv, out var l) ? l : 0;
                case UiTypeCatalog.Kind.Float: return v.Type is JTokenType.Float or JTokenType.Integer ? (double)v : double.TryParse((string?)v, NumberStyles.Float, inv, out var d) ? d : 0.0;
                case UiTypeCatalog.Kind.String: return (string?)v ?? "";
                case UiTypeCatalog.Kind.Enum: return EnumMember(f!.TypeName, (string?)v ?? "");
                case UiTypeCatalog.Kind.Ref:
                {
                    var s_Name = (string?)v;
                    return string.IsNullOrEmpty(s_Name) ? null : AssetRef(c, s_Name);
                }
                case UiTypeCatalog.Kind.Struct:
                {
                    var s_Struct = UiTypeCatalog.Describe(f!.TypeName);
                    var o = v as JObject ?? new JObject();
                    var r = new JObject();
                    if (s_Struct != null)
                        foreach (var sf in s_Struct.Fields) r[sf.Name] = o[sf.Name] is { } sv ? ToDump(c, sf, sv, p_Key + "|" + sf.Name) : DefaultDump(sf);
                    else foreach (var p in o.Properties()) r[p.Name] = ToDump(c, null, p.Value, p_Key + "|" + p.Name);
                    return r;
                }
                case UiTypeCatalog.Kind.List:
                {
                    var a = v as JArray ?? new JArray();
                    var s_Element = new UiTypeCatalog.FieldInfo { Name = f!.Name, Kind = f.ElementKind, TypeName = f.TypeName };
                    var r = new JArray();
                    var i = 0;
                    foreach (var item in a) { var t = ToDump(c, s_Element, item, p_Key + "|" + i++); if (t != null) r.Add(t); }
                    return r;
                }
                default: return v.DeepClone();
            }
        }

        static UiTypeCatalog.Kind GuessKind(JToken v) => v.Type switch
        {
            JTokenType.Boolean => UiTypeCatalog.Kind.Bool,
            JTokenType.Integer => UiTypeCatalog.Kind.Int,
            JTokenType.Float => UiTypeCatalog.Kind.Float,
            JTokenType.Object => UiTypeCatalog.Kind.Struct,
            JTokenType.Array => UiTypeCatalog.Kind.List,
            _ => UiTypeCatalog.Kind.String,
        };

        /// <summary>What a field holds when the document says nothing (a new node's untouched fields): the type's zero, the way the dumps spell it.</summary>
        static JToken? DefaultDump(UiTypeCatalog.FieldInfo f)
        {
            switch (f.Kind)
            {
                case UiTypeCatalog.Kind.Bool: return false;
                case UiTypeCatalog.Kind.Int: return 0;
                case UiTypeCatalog.Kind.Float: return 0.0;
                case UiTypeCatalog.Kind.String: return "";
                case UiTypeCatalog.Kind.Enum: return UiTypeCatalog.EnumMembers(f.TypeName).FirstOrDefault() ?? "";
                case UiTypeCatalog.Kind.Struct:
                {
                    var s_Struct = UiTypeCatalog.Describe(f.TypeName);
                    var r = new JObject();
                    if (s_Struct != null) foreach (var sf in s_Struct.Fields) r[sf.Name] = DefaultDump(sf);
                    return r;
                }
                case UiTypeCatalog.Kind.List: case UiTypeCatalog.Kind.PortArray: return new JArray();
                default: return null;
            }
        }
    }
}
