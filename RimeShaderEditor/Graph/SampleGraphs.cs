namespace RimeShaderEditor.Graph;

public static class SampleGraphs
{
    private static GraphNode Node(string p_Kind, double p_X, double p_Y, params (string Name, string Value)[] p_Params)
    {
        var s_Node = new GraphNode { Kind = p_Kind, X = p_X, Y = p_Y };
        foreach (var (s_Name, s_Value) in p_Params)
            s_Node.Params[s_Name] = s_Value;

        return s_Node;
    }

    /// <summary>
    /// Rebuilds the vanilla Props/StreetProps/OilDrumBarrel_01 GBuffer shader as a graph. It exists as a
    /// correctness harness: emitting this should produce a shader that behaves like the stock one, so any
    /// visible difference in game is a bug in the emitter rather than in the author's graph.
    /// </summary>
    public static ShaderGraph VanillaEquivalent()
    {
        var s_Graph = new ShaderGraph
        {
            Name = "OilDrum vanilla equivalent",
            TargetShader = "Props/StreetProps/OilDrumBarrel_01/SS_OilDrumBarrel_01",
        };

        var s_Colormap = Node("Texture", -520, -160, ("Register", "1"));
        var s_Split = Node("MixOut", -300, -160);
        var s_NormalMap = Node("NormalMap", -300, 120, ("Register", "2"));

        var s_SmoothGain = Node("Scalar", -300, 40, ("Value", "4"));
        var s_SpecGain = Node("Scalar", -300, -20, ("Value", "2"));
        var s_Smoothness = Node("Multiply", -80, 40);
        var s_Specular = Node("Multiply", -80, -20);

        var s_Root = Node("StandardRoot", 200, -60);

        s_Graph.Nodes.AddRange(new[]
        {
            s_Colormap, s_Split, s_NormalMap, s_SmoothGain, s_SpecGain, s_Smoothness, s_Specular, s_Root,
        });

        s_Graph.Connect(s_Colormap.Id, "Out", s_Split.Id, "Input");
        s_Graph.Connect(s_Split.Id, "RGB", s_Root.Id, "Diffuse");

        s_Graph.Connect(s_Split.Id, "B", s_Smoothness.Id, "Input1");
        s_Graph.Connect(s_SmoothGain.Id, "Out", s_Smoothness.Id, "Input2");
        s_Graph.Connect(s_Smoothness.Id, "Out", s_Root.Id, "Smoothness");

        s_Graph.Connect(s_Split.Id, "B", s_Specular.Id, "Input1");
        s_Graph.Connect(s_SpecGain.Id, "Out", s_Specular.Id, "Input2");
        s_Graph.Connect(s_Specular.Id, "Out", s_Root.Id, "Specular");

        s_Graph.Connect(s_NormalMap.Id, "Out", s_Root.Id, "Normal");

        return s_Graph;
    }
}
