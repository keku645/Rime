using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RimeLib.Cmd.UiBuilder
{
    /// <summary>
    /// A UI mod described as data. One document = one mod = N screens; each screen = stage edits on its own
    /// Scaleform movie + nodes added to / edited in its EBX graph at runtime + connections between nodes.
    /// Everything in here maps 1:1 onto mechanisms verified in game (see the mod the emitter reproduces).
    /// </summary>
    public class ScreenDocument
    {
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("description")] public string Description { get; set; } = "";
        /// <summary>The mod's version as mod.json states it (major.minor.patch).</summary>
        [JsonProperty("version")] public string Version { get; set; } = "1.0.0";
        /// <summary>Replace on load: JSON.NET otherwise APPENDS the file's authors to the default one (every open doubled the list).</summary>
        [JsonProperty("authors", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> Authors { get; set; } = new() { "keku" };
        /// <summary>Superbundle path without the Win32/ prefix, e.g. "mymod/ui". One name per mod, never shared.</summary>
        [JsonProperty("superbundle")] public string Superbundle { get; set; } = "";
        [JsonProperty("bundle")] public string Bundle { get; set; } = "";
        /// <summary>
        /// How the graph edits reach the game: "lua" = ext/Client edits each screen's live instance at load (verified in game);
        /// "static" = each edited screen ships as a whole partition inside the bundle (add_json_partition under the screen's own
        /// name and guid, the document applied to the game's dump by StaticGraph) and ext/Client only probes what loaded.
        /// </summary>
        [JsonProperty("graphDelivery")] public string GraphDelivery { get; set; } = "lua";
        [JsonIgnore] public bool IsStatic => string.Equals(GraphDelivery, "static", System.StringComparison.OrdinalIgnoreCase);
        [JsonProperty("screens")] public List<ScreenEntry> Screens { get; set; } = new();
        /// <summary>Extra movie overrides shipped as-is (e.g. a widget .gfx with adapter ActionScript).</summary>
        [JsonProperty("movies")] public List<MovieOverride> Movies { get; set; } = new();
        /// <summary>
        /// The document's own pictures (PNG, alpha kept): each ships as a texture of the game under UI/Art/&lt;mod&gt;/&lt;name&gt;
        /// (DXT5, one mip, no sRGB — the shape of the game's 2744 ui/art textures), which any widget that shows a picture by
        /// name can load (an ImageManager's StaticUrl, a row's ItemImage). The build converts them; the editor and the preview
        /// read the PNG.
        /// </summary>
        [JsonProperty("images")] public List<ImageEntry> Images { get; set; } = new();
        /// <summary>
        /// Client-side Lua modules of the document (files relative to the document's folder): each ships as ext/Client/&lt;file name&gt;
        /// and the generated ext/Client/__init__.lua requires it by its name without the extension. What the screens cannot do from
        /// ActionScript or the graph (moving the customization mannequin, reading the cursor) lives there.
        /// </summary>
        [JsonProperty("clientScripts", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> ClientScripts { get; set; } = new();
        /// <summary>
        /// True = the mod carries the receiver of the ActionScript → Lua channel (LuaTemplates.As2Channel): the document's
        /// ActionScript calls _global.fbLog(text) and the client scripts get the event AS2:Frame(text). Off by default: the hook it
        /// installs (UI:EnableTypingMode) costs nothing while nothing is sent, but a mod that sends nothing has no use for it.
        /// </summary>
        [JsonProperty("as2Channel")] public bool As2Channel { get; set; } = false;

        /// <summary>
        /// The name of an EXISTING mod this document is built INTO instead of a mod of its own (empty = its own mod,
        /// the default). What it changes, and nothing else: the files land in that mod's folder; the generated Lua
        /// goes to ext/Client/&lt;name&gt;Client.lua and ext/Shared/&lt;name&gt;Shared.lua instead of __init__.lua, so the
        /// host mod's own init survives (the host requires the two modules); mod.json is MERGED (its fields are left
        /// alone and our superbundles are added to its list, not written over it); and nothing of the host's is
        /// deleted — an ext/Server of the host is not a leftover recorder of ours.
        /// ⛔ WHY IT CANNOT JUST WRITE __init__.lua THERE: that is where a host mod's own code lives (the camo
        /// framework's is 125 KB of it), and a build would replace it in silence.
        /// </summary>
        [JsonProperty("mergeInto")] public string MergeInto { get; set; } = "";
        [JsonIgnore] public bool IsMerged => !string.IsNullOrWhiteSpace(MergeInto);
        /// <summary>The Lua module names the host requires when this document is merged into it.</summary>
        [JsonIgnore] public string ClientModule => ImageFolder + "Client";
        [JsonIgnore] public string SharedModule => ImageFolder + "Shared";

        /// <summary>The folder under UI/Art the document's textures are named in: the mod's name, letters and digits only.</summary>
        [JsonIgnore] public string ImageFolder => new string(Name.Trim().Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray()) is { Length: > 0 } s ? s : "Mod";

        /// <summary>The game name of a document picture's texture: UI/Art/&lt;mod&gt;/&lt;name&gt; (what a widget asks for; the resource is its lower-case form).</summary>
        public string ImageTextureName(ImageEntry p_Image) => $"UI/Art/{ImageFolder}/{p_Image.Name}";

        /// <summary>The document picture a texture path names (case-insensitive, with or without the ui/art prefix), or null.</summary>
        public ImageEntry? ImageByTexture(string p_Path)
        {
            var s_Path = p_Path.Replace('\\', '/').Trim('/');
            foreach (var s_Image in Images)
                if (string.Equals(s_Path, ImageTextureName(s_Image), System.StringComparison.OrdinalIgnoreCase)) return s_Image;
            return null;
        }

        /// <summary>
        /// The superbundle that carries the pictures' pixels (a chunk store, beside the UI superbundle: "mymod/ui" → "mymod/chunks"):
        /// a texture of the UI kind streams its mips from a chunk the engine looks up in the mounted stores.
        /// </summary>
        [JsonIgnore] public string ChunksSuperbundle => Superbundle.Contains('/') ? Superbundle[..Superbundle.LastIndexOf('/')] + "/chunks" : Superbundle + "chunks";
    }

    /// <summary>A picture of the document: its name in the game (letters, digits, _) and its PNG file, relative to the document's folder when it lives there.</summary>
    public class ImageEntry
    {
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("file")] public string File { get; set; } = "";
    }

    public class MovieOverride
    {
        [JsonProperty("resource")] public string Resource { get; set; } = "";
        /// <summary>Path of the .gfx to ship, relative to the document's folder. Empty = the game's own movie of that resource, with the stage ops below applied.</summary>
        [JsonProperty("file")] public string File { get; set; } = "";
        /// <summary>
        /// gfx_stage_edit ops applied to the movie (the file when one is given, the game's own otherwise): what a screen's stage is,
        /// for a movie that is not a screen — a widget's (ui/assets/kitview: its row symbols), which every screen placing it then shows.
        /// </summary>
        [JsonProperty("stage")] public List<string> Stage { get; set; } = new();
    }

    public class ScreenEntry
    {
        /// <summary>EBX partition of the screen, e.g. ui/flow/screen/customizeaccessoriesscreen.</summary>
        [JsonProperty("partition")] public string Partition { get; set; } = "";
        /// <summary>SwfMovie resource of the screen, e.g. ui/assets/customizeaccessoriesscreen. Defaults to ui/assets/&lt;partition file name&gt;.</summary>
        [JsonProperty("movie")] public string? Movie { get; set; }
        /// <summary>
        /// A screen the document creates from nothing (no such partition in the game): its movie and its EBX partition
        /// are cloned from the template screen (the game's smallest, ui/flow/screen/emptyscreen: 1280x720, the screen's
        /// ActionScript class, an input listener, the root sprite) under fresh guids, then edited like any other screen;
        /// the build ships both as NEW resources of the bundle (add_resource + add_json_partition).
        /// </summary>
        [JsonProperty("new")] public bool New { get; set; }
        /// <summary>The shipped screen a new one is cloned from.</summary>
        [JsonProperty("template")] public string Template { get; set; } = "ui/flow/screen/emptyscreen";
        /// <summary>A new screen's name as typed (its asset is UI/Flow/Screen/&lt;Title&gt;; the partition name is its lower-case form).</summary>
        [JsonProperty("title")] public string? Title { get; set; }
        /// <summary>
        /// This entry's own delivery ("static" or "lua"), overriding the document's: a flow graph edited live ("lua") keeps the
        /// game's own partition — a static copy of it would drag copies of everything it imports (every screen it chains, its audio
        /// mapping, the post-process effect) whose imports outside ui/ no longer bind.
        /// </summary>
        [JsonProperty("delivery")] public string? Delivery { get; set; }
        /// <summary>gfx_stage_edit ops applied to the vanilla movie (move / scale / rotate / char / remove / import / add / vars).</summary>
        [JsonProperty("stage")] public List<string> Stage { get; set; } = new();
        [JsonProperty("nodes")] public List<NodeEntry> Nodes { get; set; } = new();
        /// <summary>
        /// Fields of the screen asset itself (UIScreenAsset / UIGraphAsset: Modal, ProtectScreens, BundleAssetName…), by
        /// EBX name, written on the live asset after MakeWritable. Same value spelling as a node's fields.
        /// </summary>
        [JsonProperty("fields")] public Dictionary<string, JToken> Fields { get; set; } = new();
        /// <summary>
        /// Shipped connections the mod takes out of the screen at load (Alt+click on a wire in the editor): each names the
        /// UINodeConnection instance by its guid; the endpoints are there for the log. Erased from the asset's connection
        /// list before the document's nodes are applied, so a removed wire can be replaced by a new one between the same ports.
        /// </summary>
        [JsonProperty("removedConnections")] public List<RemovedConnectionEntry> RemovedConnections { get; set; } = new();
        /// <summary>Whether the document changes this screen's graph (nodes, wires, removed wires, asset fields) — what a static build ships as a partition.</summary>
        [JsonIgnore] public bool HasGraphEdits => Nodes.Count > 0 || Fields.Count > 0 || RemovedConnections.Count > 0;
    }

    /// <summary>A shipped wire the document disconnects: the connection's instance guid and "Node.port" labels of its two ends.</summary>
    public class RemovedConnectionEntry
    {
        [JsonProperty("guid")] public string Guid { get; set; } = "";
        [JsonProperty("from")] public string From { get; set; } = "";
        [JsonProperty("to")] public string To { get; set; } = "";
    }

    /// <summary>
    /// One node of the screen graph: a WidgetNode (the shorthand fields: widget, properties, binding) or any
    /// other node type of the game's UI (type + fields). Fields use the EBX/C# names of the type
    /// (DialogTitle, Params, DataSource…), values as JSON: scalars, enum member names, structs as objects,
    /// lists as arrays, references to other assets as partition names ("ui/uicomponents/uicustomizationcomp").
    /// </summary>
    public class NodeEntry
    {
        /// <summary>The node's label: WidgetNode.InstanceName or, for other types, UINodeData.Name.</summary>
        [JsonProperty("instanceName")] public string InstanceName { get; set; } = "";
        /// <summary>true = build a new node; false = edit the node with this label.</summary>
        [JsonProperty("new")] public bool New { get; set; } = true;
        /// <summary>Node type (any UINodeData descendant): WidgetNode, DataSetNode, ActionNode, DialogNode…</summary>
        [JsonProperty("type")] public string Type { get; set; } = "WidgetNode";
        /// <summary>UIWidgetAsset partition, e.g. ui/assets/grid. Required for new WidgetNodes; optional (swap) for edits.</summary>
        [JsonProperty("widget")] public string? Widget { get; set; }
        [JsonProperty("focusIndex")] public int FocusIndex { get; set; } = -1;
        [JsonProperty("zDepthLevel")] public int ZDepthLevel { get; set; } = 0;
        /// <summary>Widget properties (p_* and friends). For edits: rewritten in place, extra ones appended, the rest erased when replaceProperties is true.</summary>
        [JsonProperty("properties")] public Dictionary<string, string> Properties { get; set; } = new();
        [JsonProperty("replaceProperties")] public bool ReplaceProperties { get; set; } = true;
        [JsonProperty("binding")] public BindingEntry? Binding { get; set; }
        /// <summary>
        /// Typed fields of the node (any type), by EBX name. See UiTypeCatalog for the kinds each type has.
        /// A port reference (JumpNode.TargetPort) takes "Node.port" — the node label and a port spec ("Confirm.In", "Pick.Outputs:0").
        /// </summary>
        [JsonProperty("fields")] public Dictionary<string, JToken> Fields { get; set; } = new();
        /// <summary>Named ports to create in a port array (Outputs / Inputs), e.g. a ComparisonLogicNode's "0" and "1".</summary>
        [JsonProperty("ports")] public List<PortEntry> Ports { get; set; } = new();
        [JsonProperty("connections")] public List<ConnectionEntry> Connections { get; set; } = new();
        /// <summary>Editor-only: where the node sits on the graph canvas.</summary>
        [JsonProperty("graphX")] public double GraphX { get; set; }
        [JsonProperty("graphY")] public double GraphY { get; set; }
    }

    public class PortEntry
    {
        /// <summary>The port array field, e.g. Outputs.</summary>
        [JsonProperty("field")] public string Field { get; set; } = "Outputs";
        /// <summary>The port's name (what the node routes on: a ComparisonLogicNode output "0", a list entry "ID_M_YES").</summary>
        [JsonProperty("name")] public string Name { get; set; } = "";
        /// <summary>The port's InstanceName when it differs from its name (a StateNode's "Initialized [Screen]" port is "Initialized" there); the name otherwise.</summary>
        [JsonProperty("instanceName")] public string? InstanceName { get; set; }
        /// <summary>Optional UIWidgetEventID (without prefix) for widget-style ports; None otherwise.</summary>
        [JsonProperty("event")] public string? Event { get; set; }
        /// <summary>
        /// Optional UIInputAction (without prefix): the port becomes a UIInputEventNodePort, the kind a
        /// StateNode's Outputs use to react to a controller/keyboard action (Back, Deactivate, Menu…).
        /// </summary>
        [JsonProperty("inputEvent")] public string? InputEvent { get; set; }
    }

    public class BindingEntry
    {
        [JsonProperty("dataName")] public string DataName { get; set; } = "";
        [JsonProperty("dataKey")] public int DataKey { get; set; }
        /// <summary>Take the DataCategory (the UIComponentData) from this node's first binding source.</summary>
        [JsonProperty("categoryFromNode")] public string? CategoryFromNode { get; set; }
        /// <summary>Or name the UIComponentData partition directly, e.g. ui/uicomponents/uicustomizationcomp.</summary>
        [JsonProperty("category")] public string? Category { get; set; }
        [JsonProperty("useDirectAccess")] public bool UseDirectAccess { get; set; } = false;
        [JsonProperty("updateOnInitialize")] public bool UpdateOnInitialize { get; set; } = true;
    }

    /// <summary>
    /// A wire out of this node. Widget style: an event the widget fires (a new output port with that query).
    /// Generic style: fromPort names one of the node's ports ("Out", "True", "Outputs:0"); toPort names the
    /// target's ("In", "Show", "Inputs:OnShow"). The legacy toEvent/toField spellings keep working.
    /// </summary>
    public class ConnectionEntry
    {
        /// <summary>UIWidgetEventID (without prefix) the source widget fires, e.g. OnItemOver.</summary>
        [JsonProperty("event")] public string? Event { get; set; }
        /// <summary>Source port of a non-widget node: a port field (Out, True, False, Show, Hide) or "Outputs:&lt;name&gt;".</summary>
        [JsonProperty("fromPort")] public string? FromPort { get; set; }
        /// <summary>Target node: a widget's InstanceName, or a logic node's Name (e.g. Confirm, SetAccessory4).</summary>
        [JsonProperty("toNode")] public string ToNode { get; set; } = "";
        /// <summary>For widget targets: the input event to hit (e.g. OnShow).</summary>
        [JsonProperty("toEvent")] public string? ToEvent { get; set; }
        /// <summary>Legacy: the target's port field in Lua spelling (inValue).</summary>
        [JsonProperty("toField")] public string? ToField { get; set; }
        /// <summary>Target port: a port field (In, Show, Hide) or "Inputs:&lt;name or event&gt;".</summary>
        [JsonProperty("toPort")] public string? ToPort { get; set; }
        /// <summary>
        /// Screens popped off the player before the wire is followed (UINodeConnection.NumScreensToPop): the way a flow graph
        /// replaces the screen on top with the next one (1) instead of stacking it (0, the default).
        /// </summary>
        [JsonProperty("pop")] public int Pop { get; set; }
    }
}
