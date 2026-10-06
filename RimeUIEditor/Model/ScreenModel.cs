using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimeLib.Cmd.Scaleform;
using RimeLib.Cmd.UiBuilder;

namespace RimeUIEditor.Model
{
    /// <summary>One widget node of a screen graph as the editor shows it (read from the shipped EBX).</summary>
    public class WidgetInfo
    {
        public string Guid = "";
        public string InstanceName = "";
        public string Name = "";
        public string? WidgetPartition;      // ui/assets/<widget>
        public int FocusIndex;
        public int ZDepthLevel;
        public List<(string Name, string Value)> Properties = new();
        public string? BindingType;
        public List<(string DataName, long DataKey, string? Category)> Bindings = new();
        public List<(string Field, string Query)> Inputs = new();
        public List<(string Field, string Query)> Outputs = new();
    }

    /// <summary>One port of a node: the slot it lives in, the name the graph shows, its instance guid, its direction.</summary>
    public class PortInfo
    {
        public string Field = "";           // In / Out / Outputs / Inputs …
        public string Name = "";            // what the graph shows: the widget event, the port's own name, or the field
        public string Guid = "";            // the UINodePort instance
        public bool IsInput;
        public string Query = "";           // UIWidgetEventID without prefix ("" when None)
        public string InputEvent = "";      // UIInputAction without prefix, for UIInputEventNodePort
        /// <summary>
        /// The port is named by a shipped connection but sits in none of the node's port slots: a twin (same name) of a port the node
        /// lists. The game hooks such a wire by the port's name, so it is live wiring (the loadout screen's SetWeapon output leaves this way).
        /// </summary>
        public bool Dangling;
    }

    /// <summary>Any node of the graph (widget or logic) with its ports, as the graph view draws it.</summary>
    public class NodeInfo
    {
        public string Guid = "";
        public string Type = "";
        public string Label = "";
        public JObject Json = new();
        public List<PortInfo> Ports = new();
        /// <summary>Port references (JumpNode.TargetPort): field -> the node label and port name it points at.</summary>
        public List<(string Field, string Node, string Port)> PortRefs = new();
        public bool IsWidget => Type == "WidgetNode";
        /// <summary>The type is node-shaped in the data but unknown to the game's type catalogue.</summary>
        public bool UnknownType;
    }

    public class ConnectionInfo
    {
        public string Guid = "";
        public string FromGuid = "", FromPortGuid = "", ToGuid = "", ToPortGuid = "";
        public string From = "", FromPort = "", To = "", ToPort = "";   // labels and port names as the graph shows them
        public int NumScreensToPop;
        /// <summary>An endpoint names a port outside its node's port slots (see PortInfo.Dangling).</summary>
        public bool Dangling;
    }

    public class PlacementInfo
    {
        public string Name = "";
        public string Path = "";                // names from the root sprite: instance1/PageHeader_01
        public ushort CharacterId;
        public ushort Depth;
        public ushort SpriteId;
        public double LocalX, LocalY, ScaleX, ScaleY;   // the placement's own matrix (px / factors) — what the stage ops edit
        public double RotateSkew0, RotateSkew1;         // the matrix's rotate/skew terms (fixed 16.16 as factors)
        public GfxMovie.Affine Parent;          // stage <- the sprite's space (twips)
        public GfxMovie.Affine World;           // stage <- the placement's own space (twips)
        public double X, Y;                     // the placement origin ON THE STAGE (px)
        /// <summary>Rotation of the placement's own matrix in degrees (atan2 of the first column; positive = clockwise on screen), 0 for a plain scale.</summary>
        public double Rotation => System.Math.Atan2(RotateSkew0, ScaleX) * 180.0 / System.Math.PI;
        /// <summary>The scale along the placement's own x axis (the first column's length): ScaleX itself when it is not rotated.</summary>
        public double SizeX => System.Math.Sqrt(ScaleX * ScaleX + RotateSkew0 * RotateSkew0);
        /// <summary>The scale along the placement's own y axis (the second column's length).</summary>
        public double SizeY => System.Math.Sqrt(RotateSkew1 * RotateSkew1 + ScaleY * ScaleY);
        public GfxMovie.Bounds Bounds;          // stage twips, empty when unknown
        public string? ImportUrl;
        public bool Added;                      // placed by the document, not shipped
        /// <summary>The construct-time variables the placement sets on itself (the widget's per-instance parameters FrostEd bakes into the clip actions), or null when it has none.</summary>
        public List<(string Name, object? Value)>? ClipVars;

        /// <summary>The local x/y (px) that puts the origin at a stage point (px).</summary>
        public (double X, double Y) LocalFor(double p_StageX, double p_StageY)
        {
            var (lx, ly) = Parent.Inverse().Apply(p_StageX * 20, p_StageY * 20);
            return (lx / 20.0, ly / 20.0);
        }
    }

    /// <summary>
    /// A UI graph as loaded from the mounted game: its EBX graph (every node with its ports, every connection)
    /// and, for a screen, its Scaleform stage (placements with bounds resolved through the imported widget
    /// movies). A UIGraphAsset (the flow graphs that chain screens with StateNodes/DialogNodes) has no stage.
    /// </summary>
    public class ScreenModel
    {
        public string Partition = "";
        public string AssetType = "";           // UIScreenAsset | UIGraphAsset
        public string MovieName = "";
        public string PartitionGuid = "";
        public string AssetGuid = "";
        public List<WidgetInfo> Widgets = new();
        public List<(string Type, string Name)> OtherNodes = new();
        public List<(string From, string FromPort, string To, string ToPort)> Connections = new();
        public List<NodeInfo> AllNodes = new();
        public List<ConnectionInfo> Wires = new();
        public JObject Instances = new();
        public GfxMovie? Movie;
        public byte[] VanillaMovie = Array.Empty<byte>();
        public List<PlacementInfo> Placements = new();
        public Dictionary<string, GfxMovie?> ImportedMovies = new();
        /// <summary>Port instances of the partition that no node and no connection uses (dead data, counted for the audit).</summary>
        public int UnusedPorts;

        public bool HasStage => Movie != null;
        public bool IsScreen => AssetType == "UIScreenAsset";

        public static bool IsGraphAssetType(string? p_Type) => p_Type is "UIScreenAsset" or "UIGraphAsset";

        /// <summary>A screen the document creates: not in the game, its partition and movie come from the template (see NewScreen).</summary>
        public bool IsNew;

        /// <summary>Loads a screen from the game's dump and movie — or, for a new screen, from the given partition JSON and movie bytes.</summary>
        public static ScreenModel Load(RimeUiService p_Rime, string p_Partition, Func<string, GfxMovie?> p_WidgetMovie, JObject? p_NewJson = null, byte[]? p_NewMovie = null)
        {
            var s_Model = new ScreenModel { Partition = p_Partition, IsNew = p_NewJson != null };
            var s_Json = p_NewJson ?? p_Rime.PartitionJson(p_Partition);
            s_Model.PartitionGuid = (string)s_Json["PartitionGuid"]!;
            s_Model.AssetGuid = (string)s_Json["PrimaryInstanceGuid"]!;
            var s_Instances = (JObject)s_Json["Instances"]!;
            s_Model.Instances = s_Instances;
            s_Model.AssetType = (s_Instances[s_Model.AssetGuid] as JObject)?["$type"]?.ToString() ?? "";
            if (!IsGraphAssetType(s_Model.AssetType))
                throw new Exception($"{p_Partition} is a {s_Model.AssetType}, not a UI graph (UIScreenAsset / UIGraphAsset)");

            static string? Sub(JToken? p_Tok, string p_Key) => p_Tok is JObject o ? (string?)o[p_Key] : null;
            string TypeOf(string? p_Guid) => p_Guid != null && s_Instances[p_Guid] is JObject o ? (string?)o["$type"] ?? "" : "";
            static bool IsPortType(string p_Type) => p_Type.Contains("NodePort");
            // a node: what the game's type info calls a UINodeData, or (a type the catalogue lacks) node-shaped data
            bool IsNode(string p_Type, JObject p_Inst) => !IsPortType(p_Type) && (UiTypeCatalog.IsNodeType(p_Type) || (p_Type.EndsWith("Node") && p_Inst["ParentGraph"] != null));
            static string LabelOf(JObject p_Inst, string p_Type)
            {
                var l = (string?)p_Inst["InstanceName"];
                if (string.IsNullOrEmpty(l)) l = (string?)p_Inst["Name"];
                return string.IsNullOrEmpty(l) ? p_Type : l;
            }

            // ---- nodes and the ports they own
            var s_PortMap = new Dictionary<string, (NodeInfo Node, PortInfo Port)>();   // port guid -> owner
            var s_NodeByGuid = new Dictionary<string, NodeInfo>();
            var s_PendingRefs = new List<(NodeInfo Node, string Field, string PortGuid)>();
            foreach (var (s_Guid, s_Tok) in s_Instances)
            {
                var s_Inst = (JObject)s_Tok!;
                var s_Type = (string?)s_Inst["$type"] ?? "";
                if (!IsNode(s_Type, s_Inst)) continue;
                var s_Info = UiTypeCatalog.Describe(s_Type);
                var n = new NodeInfo { Guid = s_Guid, Type = s_Type, Label = LabelOf(s_Inst, s_Type), Json = s_Inst, UnknownType = s_Info == null || !s_Info.IsNode };
                foreach (var s_Prop in s_Inst.Properties())
                {
                    var f = s_Info?.Field(s_Prop.Name);
                    var s_Single = f?.Kind == UiTypeCatalog.Kind.Port || (f == null && s_Prop.Value is JObject po && po["InstanceGuid"] != null && IsPortType(TypeOf((string?)po["InstanceGuid"])));
                    var s_Array = f?.Kind == UiTypeCatalog.Kind.PortArray || (f == null && s_Prop.Value is JArray pa && pa.OfType<JObject>().Any(x => x["InstanceGuid"] != null && IsPortType(TypeOf((string?)x["InstanceGuid"]))));
                    if (!s_Single && !s_Array) continue;
                    if (s_Single && UiTypeCatalog.IsPortReference(s_Type, s_Prop.Name))
                    {
                        // a pointer at another node's port (JumpNode.TargetPort): resolved once every node's ports are known
                        var g = Sub(s_Prop.Value, "InstanceGuid");
                        if (g != null) s_PendingRefs.Add((n, s_Prop.Name, g));
                        continue;
                    }
                    var s_IsInput = UiTypeCatalog.PortIsInput(s_Prop.Name);
                    foreach (var s_Ref in s_Single ? new[] { s_Prop.Value } : (s_Prop.Value as JArray ?? new JArray()).ToArray())
                    {
                        var g = Sub(s_Ref, "InstanceGuid");
                        if (g == null || s_Instances[g] is not JObject s_Port || !IsPortType((string?)s_Port["$type"] ?? "")) continue;
                        var p = PortOf(s_Port, s_Prop.Name, s_Type == "WidgetNode");
                        p.Guid = g; p.IsInput = s_IsInput;
                        n.Ports.Add(p);
                        s_PortMap[g] = (n, p);
                    }
                }
                s_NodeByGuid[s_Guid] = n;
                s_Model.AllNodes.Add(n);
            }
            foreach (var (n, s_Field, g) in s_PendingRefs)
                n.PortRefs.Add((s_Field, s_PortMap.TryGetValue(g, out var t) ? t.Node.Label : "?", s_PortMap.TryGetValue(g, out var t2) ? t2.Port.Name : g));

            // ---- connections: every UINodeConnection of the partition, endpoints resolved to node + port.
            // An endpoint whose port sits in none of its node's slots is kept and marked dangling (the game ships 118 such endpoints).
            var s_Referenced = new HashSet<string>();
            foreach (var (s_Guid, s_Tok) in s_Instances)
            {
                var c = (JObject)s_Tok!;
                if ((string?)c["$type"] != "UINodeConnection") continue;
                var w = new ConnectionInfo
                {
                    Guid = s_Guid,
                    FromGuid = Sub(c["SourceNode"], "InstanceGuid") ?? "", FromPortGuid = Sub(c["SourcePort"], "InstanceGuid") ?? "",
                    ToGuid = Sub(c["TargetNode"], "InstanceGuid") ?? "", ToPortGuid = Sub(c["TargetPort"], "InstanceGuid") ?? "",
                    NumScreensToPop = (int?)c["NumScreensToPop"] ?? 0,
                };
                (string Label, string Port) Endpoint(string p_NodeGuid, string p_PortGuid, bool p_IsTarget)
                {
                    s_Referenced.Add(p_PortGuid);
                    s_NodeByGuid.TryGetValue(p_NodeGuid, out var s_Node);
                    if (s_PortMap.TryGetValue(p_PortGuid, out var s_Owned) && (s_Node == null || s_Owned.Node == s_Node))
                        return (s_Owned.Node.Label, s_Owned.Port.Name);
                    if (s_Node == null) return ("?", p_PortGuid.Length > 8 ? p_PortGuid[..8] : p_PortGuid);
                    // the node exists, the port is not among its slots: attach a dangling port so the wire still lands on the right node
                    var s_Existing = s_Node.Ports.FirstOrDefault(p => p.Guid == p_PortGuid);
                    if (s_Existing == null)
                    {
                        s_Existing = s_Instances[p_PortGuid] is JObject s_PortJson ? PortOf(s_PortJson, "?", s_Node.IsWidget) : new PortInfo { Field = "?", Name = "? " + (p_PortGuid.Length > 8 ? p_PortGuid[..8] : p_PortGuid) };
                        s_Existing.Guid = p_PortGuid; s_Existing.IsInput = p_IsTarget; s_Existing.Dangling = true;
                        s_Node.Ports.Add(s_Existing);
                    }
                    w.Dangling = true;
                    return (s_Node.Label, s_Existing.Name);
                }
                (w.From, w.FromPort) = Endpoint(w.FromGuid, w.FromPortGuid, false);
                (w.To, w.ToPort) = Endpoint(w.ToGuid, w.ToPortGuid, true);
                s_Model.Wires.Add(w);
                s_Model.Connections.Add((w.From, w.FromPort, w.To, w.ToPort));
            }
            s_Model.UnusedPorts = s_Instances.Properties().Count(p => p.Value is JObject o && IsPortType((string?)o["$type"] ?? "") && !s_PortMap.ContainsKey(p.Name) && !s_Referenced.Contains(p.Name));

            // ---- the widget view of the same nodes (properties panel)
            foreach (var n in s_Model.AllNodes)
            {
                if (!n.IsWidget) { s_Model.OtherNodes.Add((n.Type, n.Label)); continue; }
                var s_Inst = n.Json;
                var w = new WidgetInfo
                {
                    Guid = n.Guid,
                    InstanceName = (string?)s_Inst["InstanceName"] ?? "",
                    Name = (string?)s_Inst["Name"] ?? "",
                    FocusIndex = (int?)s_Inst["FocusIndex"] ?? 0,
                    ZDepthLevel = (int?)s_Inst["ZDepthLevel"] ?? 0,
                };
                var s_WidgetGuid = Sub(s_Inst["WidgetAsset"], "PartitionGuid");
                if (s_WidgetGuid != null) w.WidgetPartition = p_Rime.PartitionNameByGuid(s_WidgetGuid);
                foreach (var p in (s_Inst["WidgetProperties"] as JArray ?? new JArray()).OfType<JObject>())
                    w.Properties.Add(((string?)p["Name"] ?? "", (string?)p["Value"] ?? ""));
                var s_BindGuid = Sub(s_Inst["DataBinding"], "InstanceGuid");
                if (s_BindGuid != null && s_Instances[s_BindGuid] is JObject s_Bind)
                {
                    w.BindingType = (string?)s_Bind["$type"];
                    foreach (var b in (s_Bind["Bindings"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        var s_CatGuid = Sub(b["DataCategory"], "PartitionGuid");
                        w.Bindings.Add(((string?)b["DataName"] ?? "", (long?)b["DataKey"] ?? 0, s_CatGuid != null ? p_Rime.PartitionNameByGuid(s_CatGuid) : null));
                    }
                }
                foreach (var p in n.Ports.Where(p => !p.Dangling)) (p.IsInput ? w.Inputs : w.Outputs).Add((p.Field, p.Query));
                s_Model.Widgets.Add(w);
            }

            // ---- stage (screens only; a flow graph has no movie)
            if (s_Model.IsScreen)
            {
                s_Model.MovieName = "ui/assets/" + p_Partition.Split('/').Last();
                s_Model.VanillaMovie = p_NewMovie ?? p_Rime.ResourceBytes(s_Model.MovieName);
                s_Model.Movie = GfxMovie.Load(s_Model.VanillaMovie);
                s_Model.RefreshPlacements(p_WidgetMovie);
            }
            return s_Model;
        }

        /// <summary>A port as the graph names it: a widget's event, a logic port's own name, or its slot.</summary>
        static PortInfo PortOf(JObject p_Port, string p_Field, bool p_Widget)
        {
            var p = new PortInfo { Field = p_Field };
            var s_Query = ((string?)p_Port["Query"] ?? "").Replace("UIWidgetEventID_", "");
            p.Query = s_Query == "None" ? "" : s_Query;
            p.InputEvent = ((string?)p_Port["InputEventType"] ?? "").Replace("UIInputAction_", "");
            var s_Own = (string?)p_Port["InstanceName"];
            if (string.IsNullOrEmpty(s_Own)) s_Own = (string?)p_Port["Name"];
            if (p_Widget) p.Name = p.Query != "" ? p.Query : (string.IsNullOrEmpty(s_Own) ? p_Field : s_Own!);
            else p.Name = string.IsNullOrEmpty(s_Own) ? p_Field : s_Own!;
            // a controller/keyboard port (UIInputEventNodePort) shows the action it listens to, unless its name says so already
            if (p.InputEvent != "" && !p.Name.Contains(p.InputEvent, StringComparison.OrdinalIgnoreCase)) p.Name += " [" + p.InputEvent + "]";
            return p;
        }

        /// <summary>Recomputes placements + bounds from the current Movie (after stage ops were applied).</summary>
        public void RefreshPlacements(Func<string, GfxMovie?> p_WidgetMovie)
        {
            if (Movie == null) { Placements.Clear(); return; }
            var s_Imports = Movie.ImportedCharacters();
            (GfxMovie?, ushort) Resolve(string p_Url, string p_Symbol)
            {
                var s_Key = Path.GetFileNameWithoutExtension(p_Url).ToLowerInvariant();
                if (!ImportedMovies.TryGetValue(s_Key, out var m))
                {
                    m = p_WidgetMovie("ui/assets/" + s_Key);
                    ImportedMovies[s_Key] = m;
                }
                var id = m?.ExportedCharacter(p_Symbol);
                return (id == null ? null : m, id ?? 0);
            }
            var s_Old = new Dictionary<string, bool>();
            foreach (var s_P in Placements) s_Old[s_P.Name] = s_P.Added;
            Placements.Clear();
            foreach (var sp in Movie.StagePlacements())
            {
                var s_Place = sp.Place;
                var s_World = sp.World;
                var (s_X, s_Y) = sp.Parent.Apply(s_Place.Matrix?.TranslateX ?? 0, s_Place.Matrix?.TranslateY ?? 0);
                var p = new PlacementInfo
                {
                    Name = s_Place.Name!, Path = sp.Path, CharacterId = s_Place.CharacterId, Depth = s_Place.Depth, SpriteId = sp.Sprite.SpriteId,
                    LocalX = (s_Place.Matrix?.TranslateX ?? 0) / 20.0, LocalY = (s_Place.Matrix?.TranslateY ?? 0) / 20.0,
                    ScaleX = s_Place.Matrix?.ScaleXf ?? 1, ScaleY = s_Place.Matrix?.ScaleYf ?? 1,
                    RotateSkew0 = s_Place.Matrix is { HasRotate: true } m0 ? m0.RotateSkew0 / 65536.0 : 0, RotateSkew1 = s_Place.Matrix is { HasRotate: true } m1 ? m1.RotateSkew1 / 65536.0 : 0,
                    Parent = sp.Parent, World = s_World, X = s_X / 20.0, Y = s_Y / 20.0,
                    ImportUrl = s_Imports.TryGetValue(s_Place.CharacterId, out var imp) ? imp.Url : null,
                    Added = s_Old.TryGetValue(s_Place.Name!, out var a) && a,
                };
                try { p.ClipVars = s_Place.ClipVars; } catch { p.ClipVars = null; }
                p.Bounds = Movie.CharacterBounds(s_Place.CharacterId, Resolve).Transform(s_World);
                Placements.Add(p);
            }
        }
    }
}
