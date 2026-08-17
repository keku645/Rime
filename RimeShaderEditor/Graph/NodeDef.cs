using System;
using System.Collections.Generic;

namespace RimeShaderEditor.Graph;

public enum ParamKind
{
    Scalar,
    Color,
    Enum,
    Text,
    Bool,
}

public class PortDef
{
    public string Name { get; init; } = "";
    public ShaderPortType Type { get; init; } = ShaderPortType.SptScalar;

    /// <summary>HLSL literal used when nothing is wired into this input. Null makes the input mandatory.</summary>
    public string? Default { get; init; }

    /// <summary>
    /// True when Default is a contextual EXPRESSION (i.TexCoord.xy, time) rather than a constant, so the
    /// properties panel must not offer it as a number to type over.
    /// </summary>
    public bool DefaultIsExpression { get; init; }

    /// <summary>An unconnected constant input is editable — otherwise its value is frozen in the node.</summary>
    public bool IsEditable => Default != null && !DefaultIsExpression;

    /// <summary>
    /// Why this port currently does nothing, or null when it is live. Ports that exist for parity with
    /// FrostED but that the GBuffer pass does not consume are drawn dimmed with this as the reason, the way
    /// Unreal greys out material inputs its blend mode ignores — connecting one silently would be worse.
    /// </summary>
    public Func<GraphNode, string?>? UnavailableReason { get; init; }

    public string? WhyUnavailable(GraphNode p_Node) => UnavailableReason?.Invoke(p_Node);
}

public class ParamDef
{
    public string Name { get; init; } = "";
    public ParamKind Kind { get; init; } = ParamKind.Scalar;
    public string Default { get; init; } = "0";

    /// <summary>For ParamKind.Enum: the enum type whose names are offered in the properties panel.</summary>
    public Type? EnumType { get; init; }
}

/// <summary>
/// Everything a node emits into the generated HLSL. The emitter resolves each input to an expression
/// (already adapted to the declared port type) before handing the scope to the node.
/// </summary>
public interface IEmitScope
{
    string In(string p_Port);
    bool IsConnected(string p_Port);

    /// <summary>
    /// True when the author gave this input a value at all — by wire OR by typing a constant. Branch on this,
    /// not on IsConnected, or a value typed in the properties panel is discarded without a word.
    /// </summary>
    bool HasExplicitValue(string p_Port);
    void Out(string p_Port, string p_Expression);
    void Line(string p_Code);
    string Declare(string p_Hint, ShaderPortType p_Type, string p_Expression);
    string Param(string p_Name);
    float ParamScalar(string p_Name);
    TEnum ParamEnum<TEnum>(string p_Name) where TEnum : struct, Enum;
}

public class NodeDef
{
    public string Kind { get; init; } = "";
    public string Title { get; init; } = "";
    public string Category { get; init; } = "Math";

    /// <summary>Shown in the properties panel. Say what the engine does with this, not what the node is called.</summary>
    public string Description { get; init; } = "";
    public List<PortDef> Inputs { get; init; } = new();
    public List<PortDef> Outputs { get; init; } = new();
    public List<ParamDef> Params { get; init; } = new();

    /// <summary>True for the single terminal node of a graph; it consumes inputs and writes render targets.</summary>
    public bool IsRoot { get; init; }

    /// <summary>Node samples a texture, so the canvas reserves room for its thumbnail (as FrostED did).</summary>
    public bool HasTexturePreview { get; init; }

    public Action<IEmitScope, GraphNode> Emit { get; init; } = (_, _) => { };

    public PortDef? FindInput(string p_Name) => Inputs.Find(p_P => p_P.Name == p_Name);
    public PortDef? FindOutput(string p_Name) => Outputs.Find(p_P => p_P.Name == p_Name);
}
