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

public class ShaderGraph
{
    public string Name { get; set; } = "Untitled";

    /// <summary>Shader this graph is authored against; its binding table defines the available input nodes.</summary>
    public string TargetShader { get; set; } = "";

    public List<GraphNode> Nodes { get; set; } = new();
    public List<GraphConnection> Connections { get; set; } = new();

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
