using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimeShaderEditor.Graph;

public class GraphNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>Free-text note rendered as a bubble above the node. Null (the common case) is not serialized.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Comment { get; set; }

    public Dictionary<string, string> Params { get; set; } = new();

    [JsonIgnore]
    public NodeDef Def => Palette.Get(Kind);

    public string GetParam(string p_Name)
    {
        if (Params.TryGetValue(p_Name, out var s_Value))
            return s_Value;

        var s_Def = Def.Params.Find(p_P => p_P.Name == p_Name);
        return s_Def?.Default ?? "0";
    }

    /// <summary>
    /// Per-pin constant for an unconnected input, stored alongside the node parameters under an "in:" prefix so
    /// it travels with the saved graph without a second dictionary.
    /// </summary>
    private static string InputKey(string p_Port) => $"in:{p_Port}";

    public string? GetInputOverride(string p_Port) =>
        Params.TryGetValue(InputKey(p_Port), out var s_Value) && s_Value.Trim().Length > 0 ? s_Value : null;

    public void SetInputOverride(string p_Port, string? p_Value)
    {
        if (p_Value == null || p_Value.Trim().Length == 0)
            Params.Remove(InputKey(p_Port));
        else
            Params[InputKey(p_Port)] = p_Value;
    }
}

public class GraphConnection
{
    public string FromNode { get; set; } = "";
    public string FromPort { get; set; } = "";
    public string ToNode { get; set; } = "";
    public string ToPort { get; set; } = "";
}

/// <summary>
/// A comment box on the canvas: a titled, coloured region that moves the nodes inside it when dragged by its
/// title bar. Purely organisational - groups never affect emission, translation or baking; they only exist so
/// a hundred-node graph can be read the way its author thinks about it.
/// </summary>
public class GraphGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Comment";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 200;

    /// <summary>Index into the canvas's preset palette, so saved files stay stable across theme tweaks.</summary>
    public int Colour { get; set; }
}

public class ShaderGraph
{
    public string Name { get; set; } = "Untitled";

    /// <summary>Shader this graph is authored against; its binding table defines the available input nodes.</summary>
    public string TargetShader { get; set; } = "";

    /// <summary>
    /// Fingerprint (FNV-1a of the emitted HLSL) of this graph AS TRANSLATED from the game, stamped by the
    /// translate step. It is what lets a variation bake decide honestly whether the user changed the shader's
    /// LOGIC (current emission hashes differently -> the variation needs its own cloned shader) or only its
    /// textures (same hash -> the variation rides the vanilla shader). Absent in older files / hand-made graphs.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TranslatedHlslHash { get; set; }

    /// <summary>
    /// When set, the bake creates a brand-new object VARIATION under this asset name (full path, sibling of
    /// the object like the game's own variations) instead of replacing the target shader: the target's
    /// database entry is cloned under a fresh sibling name, custom textures ride under sibling names, and a
    /// mesh-variation entry keyed by FNV(name) makes the variation spawnable. Null = plain replacement bake.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BakeVariation { get; set; }

    /// <summary>
    /// Whether the baked mod should point the level's OWN copies of the object at this variation as the level
    /// loads. Without it a variation only shows on objects placed with it, so the map looks unchanged — which
    /// is almost never what someone authoring a variation wants, hence the default.
    ///
    /// It lives on the GRAPH and not on the bake request because it is a property of THIS shader: a mod can
    /// carry several, and one of them may be meant for hand placement while another replaces what is already
    /// in the map. Meaningless without <see cref="BakeVariation"/>.
    /// </summary>
    public bool ApplyVariationToLevel { get; set; } = true;

    /// <summary>
    /// The mesh the baked variation's database entry rides on. Stamped from the preview's (mesh, variation)
    /// selection when the open document bakes; empty means the bake picks one — and a shared shader can be
    /// used by meshes that are NOT resident when the level bundle loads (a destruction mesh was the first
    /// found for the glass preset), whose entry then crashes the load registering against a null mesh. The
    /// user's pick is the only reliable answer to "which object is this variation for".
    /// </summary>
    public string? BakeMesh { get; set; }

    /// <summary>
    /// The contract family this graph's explicit interpolator indices were TRANSLATED under, stamped by the
    /// translator. Emission shifts a graph's saved indices when compiling into a probe twin (the SH rows
    /// displace the base layout), which is right for a graph that speaks BASE numbering — but a graph
    /// translated straight from a probe solution already speaks the shifted numbering, and shifting it again
    /// reads past the layout. Null (every pre-existing graph, every hand-authored one) means base numbering.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TranslatedFamily { get; set; }

    /// <summary>
    /// A MASTER document embeds every variation of its object as a full graph here — one file holds them
    /// all; the editor unpacks them into working drafts on open and re-embeds on save, and the bake ships
    /// every one without the user listing files by hand. Null on plain single-look documents.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ShaderGraph>? Variations { get; set; }

    public List<GraphNode> Nodes { get; set; } = new();
    public List<GraphConnection> Connections { get; set; } = new();

    /// <summary>Comment boxes. Absent in older files, which deserialize to an empty list.</summary>
    public List<GraphGroup> Groups { get; set; } = new();

    public GraphNode? FindNode(string p_Id) => Nodes.Find(p_N => p_N.Id == p_Id);

    public GraphConnection? ConnectionInto(string p_NodeId, string p_Port) =>
        Connections.Find(p_C => p_C.ToNode == p_NodeId && p_C.ToPort == p_Port);

    public IEnumerable<GraphConnection> ConnectionsFrom(string p_NodeId, string p_Port)
    {
        foreach (var s_Connection in Connections)
            if (s_Connection.FromNode == p_NodeId && s_Connection.FromPort == p_Port)
                yield return s_Connection;
    }

    public GraphNode? Root => Nodes.Find(p_N => p_N.Def.IsRoot);

    /// <summary>
    /// Connects an output to an input, replacing whatever fed that input. Inputs take exactly one wire;
    /// outputs may fan out. Rejects wires that would introduce a cycle.
    /// </summary>
    public bool Connect(string p_FromNode, string p_FromPort, string p_ToNode, string p_ToPort)
    {
        if (p_FromNode == p_ToNode)
            return false;

        if (WouldCycle(p_FromNode, p_ToNode))
            return false;

        Connections.RemoveAll(p_C => p_C.ToNode == p_ToNode && p_C.ToPort == p_ToPort);
        Connections.Add(new GraphConnection
        {
            FromNode = p_FromNode,
            FromPort = p_FromPort,
            ToNode = p_ToNode,
            ToPort = p_ToPort,
        });

        return true;
    }

    public void Disconnect(string p_ToNode, string p_ToPort) =>
        Connections.RemoveAll(p_C => p_C.ToNode == p_ToNode && p_C.ToPort == p_ToPort);

    public void RemoveNode(string p_Id)
    {
        Nodes.RemoveAll(p_N => p_N.Id == p_Id);
        Connections.RemoveAll(p_C => p_C.FromNode == p_Id || p_C.ToNode == p_Id);
    }

    private bool WouldCycle(string p_FromNode, string p_ToNode)
    {
        // The new wire makes p_ToNode depend on p_FromNode, so a cycle exists iff p_FromNode already
        // depends on p_ToNode.
        var s_Stack = new Stack<string>();
        var s_Seen = new HashSet<string>();
        s_Stack.Push(p_FromNode);

        while (s_Stack.Count > 0)
        {
            var s_Current = s_Stack.Pop();
            if (s_Current == p_ToNode)
                return true;

            if (!s_Seen.Add(s_Current))
                continue;

            foreach (var s_Connection in Connections)
                if (s_Connection.ToNode == s_Current)
                    s_Stack.Push(s_Connection.FromNode);
        }

        return false;
    }

    /// <summary>Dependency-first ordering of the nodes that actually feed the root.</summary>
    public List<GraphNode> TopologicalFromRoot()
    {
        var s_Result = new List<GraphNode>();
        var s_Root = Root;
        if (s_Root == null)
            return s_Result;

        var s_Visiting = new HashSet<string>();
        var s_Done = new HashSet<string>();

        void Visit(GraphNode p_Node)
        {
            if (s_Done.Contains(p_Node.Id) || !s_Visiting.Add(p_Node.Id))
                return;

            foreach (var s_Port in p_Node.Def.Inputs)
            {
                var s_Connection = ConnectionInto(p_Node.Id, s_Port.Name);
                var s_Source = s_Connection == null ? null : FindNode(s_Connection.FromNode);
                if (s_Source != null)
                    Visit(s_Source);
            }

            s_Visiting.Remove(p_Node.Id);
            s_Done.Add(p_Node.Id);
            s_Result.Add(p_Node);
        }

        Visit(s_Root);
        return s_Result;
    }

    private static readonly JsonSerializerOptions s_JsonOptions = new()
    {
        WriteIndented = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, s_JsonOptions);

    public static ShaderGraph FromJson(string p_Json) =>
        JsonSerializer.Deserialize<ShaderGraph>(p_Json) ?? new ShaderGraph();
}
