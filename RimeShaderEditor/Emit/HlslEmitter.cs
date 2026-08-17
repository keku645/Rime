using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RimeShaderEditor.Graph;

namespace RimeShaderEditor.Emit;

public class EmitResult
{
    public string Hlsl { get; set; } = "";
    public List<string> Errors { get; } = new();
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// Turns a graph into a BF3 GBufferLayout0 pixel shader.
///
/// The shell (cbuffer register/offsets, texture registers, interpolator semantics and the four render
/// targets) is not a guess: it was read off the vanilla shader with fxc /dumpbin, because
/// replace_shader_bytecode swaps only the bytecode and leaves the shaderdb binding table untouched — so a
/// generated shader has to consume exactly the slots the target already declares.
/// </summary>
public class HlslEmitter
{
    private sealed class Scope : IEmitScope
    {
        public required HlslEmitter Emitter { get; init; }
        public required GraphNode Node { get; init; }
        public required StringBuilder Body { get; init; }

        public string In(string p_Port)
        {
            var s_PortDef = Node.Def.FindInput(p_Port);
            if (s_PortDef == null)
                return "0";

            var s_Connection = Emitter.m_Graph.ConnectionInto(Node.Id, p_Port);
            if (s_Connection == null)
            {
                // An unconnected input takes the value typed in the properties panel, falling back to the
                // node's own default. A value that does not parse is reported rather than silently ignored.
                var s_Override = Node.GetInputOverride(p_Port);
                if (s_Override != null && s_PortDef.IsEditable)
                {
                    var s_Literal = PortTypeUtils.FormatLiteral(s_Override, s_PortDef.Type);
                    if (s_Literal != null)
                        return s_Literal;

                    Emitter.m_Result.Errors.Add(
                        $"'{Node.Def.Title}' input '{p_Port}': '{s_Override}' is not a valid " +
                        $"{s_PortDef.Type.ComponentCount()}-component value.");
                    return s_PortDef.Default ?? "0";
                }

                if (s_PortDef.Default != null)
                    return s_PortDef.Default;

                Emitter.m_Result.Errors.Add(
                    $"'{Node.Def.Title}' has nothing connected to its required input '{p_Port}'.");
                return "0";
            }

            if (!Emitter.m_Outputs.TryGetValue((s_Connection.FromNode, s_Connection.FromPort), out var s_Source))
            {
                Emitter.m_Result.Errors.Add(
                    $"'{Node.Def.Title}' input '{p_Port}' reads an output that was never produced.");
                return "0";
            }

            var s_Adapted = PortTypeUtils.Adapt(s_Source.Expression, s_Source.Type, s_PortDef.Type);
            if (s_Adapted == null)
            {
                Emitter.m_Result.Errors.Add(
                    $"'{Node.Def.Title}' input '{p_Port}' expects {s_PortDef.Type} but receives {s_Source.Type}.");
                return "0";
            }

            return s_Adapted;
        }

        public bool IsConnected(string p_Port) =>
            Emitter.m_Graph.ConnectionInto(Node.Id, p_Port) != null;

        public bool HasExplicitValue(string p_Port) =>
            IsConnected(p_Port) || Node.GetInputOverride(p_Port) != null;

        public void Out(string p_Port, string p_Expression)
        {
            var s_PortDef = Node.Def.FindOutput(p_Port);
            var s_Type = s_PortDef?.Type ?? ShaderPortType.SptVec4;
            var s_Name = Declare($"{Sanitize(Node.Def.Kind)}_{Sanitize(p_Port)}", s_Type, p_Expression);
            Emitter.m_Outputs[(Node.Id, p_Port)] = (s_Name, s_Type);
        }

        public void Line(string p_Code) => Body.AppendLine($"    {p_Code}");

        public string Declare(string p_Hint, ShaderPortType p_Type, string p_Expression)
        {
            var s_Name = $"{p_Hint}_{Emitter.m_TempCounter++}";
            Body.AppendLine($"    {p_Type.HlslType()} {s_Name} = {p_Expression};");
            return s_Name;
        }

        public string Param(string p_Name) => Node.GetParam(p_Name);

        public float ParamScalar(string p_Name) =>
            float.TryParse(Node.GetParam(p_Name), NumberStyles.Float, CultureInfo.InvariantCulture, out var s_Value)
                ? s_Value
                : 0.0f;

        public TEnum ParamEnum<TEnum>(string p_Name) where TEnum : struct, Enum =>
            Enum.TryParse<TEnum>(Node.GetParam(p_Name), out var s_Value) ? s_Value : default;

        private static string Sanitize(string p_Text)
        {
            var s_Builder = new StringBuilder();
            foreach (var s_Char in p_Text)
                s_Builder.Append(char.IsLetterOrDigit(s_Char) ? s_Char : '_');

            return s_Builder.ToString();
        }
    }

    private ShaderGraph m_Graph = new();
    private EmitResult m_Result = new();
    private readonly Dictionary<(string Node, string Port), (string Expression, ShaderPortType Type)> m_Outputs = new();
    private int m_TempCounter;

    public EmitResult Emit(ShaderGraph p_Graph)
    {
        m_Graph = p_Graph;
        m_Result = new EmitResult();
        m_Outputs.Clear();
        m_TempCounter = 0;

        var s_Root = p_Graph.Root;
        if (s_Root == null)
        {
            m_Result.Errors.Add("The graph has no StandardRoot node, so there is nothing to write.");
            return m_Result;
        }

        var s_Ordered = p_Graph.TopologicalFromRoot();
        var s_Body = new StringBuilder();

        foreach (var s_Node in s_Ordered)
        {
            var s_Scope = new Scope { Emitter = this, Node = s_Node, Body = s_Body };
            s_Node.Def.Emit(s_Scope, s_Node);
        }

        m_Result.Hlsl = BuildShell(p_Graph, s_Body.ToString());
        return m_Result;
    }

    private static string BuildShell(ShaderGraph p_Graph, string p_Body)
    {
        var s_Builder = new StringBuilder();
        s_Builder.AppendLine("// Generated by RimeShaderEditor. Do not edit by hand.");
        s_Builder.AppendLine($"// Graph:  {p_Graph.Name}");
        s_Builder.AppendLine($"// Target: {p_Graph.TargetShader}");
        s_Builder.AppendLine("//");
        s_Builder.AppendLine("// Slot layout is dictated by the target shader's existing shaderdb binding table:");
        s_Builder.AppendLine("//   cb2 viewConstants, s0 sampler0, t1/t2 textures, TEXCOORD0..4 interpolators,");
        s_Builder.AppendLine("//   SV_Target0..3 (DeferredShadingGBufferLayout0).");
        s_Builder.AppendLine();
        s_Builder.AppendLine("cbuffer viewConstants : register(b2)");
        s_Builder.AppendLine("{");
        s_Builder.AppendLine("    float  time                : packoffset(c0.x);");
        s_Builder.AppendLine("    float4 screenSize          : packoffset(c1);");
        s_Builder.AppendLine("    float4 viewportZMinMaxKzKw : packoffset(c19);");
        s_Builder.AppendLine("    float3 cameraPos           : packoffset(c20);");
        s_Builder.AppendLine("};");
        s_Builder.AppendLine();
        s_Builder.AppendLine("SamplerState sampler0          : register(s0);");
        s_Builder.AppendLine("Texture2D    texture_Texture   : register(t1);");
        s_Builder.AppendLine("Texture2D    texture_Texture2  : register(t2);");
        s_Builder.AppendLine();
        s_Builder.AppendLine("struct PsIn");
        s_Builder.AppendLine("{");
        s_Builder.AppendLine("    float4 Position    : SV_Position;");
        s_Builder.AppendLine("    float4 WorldPos    : TEXCOORD0;");
        s_Builder.AppendLine("    float4 TangentRow0 : TEXCOORD1;");
        s_Builder.AppendLine("    float4 TangentRow1 : TEXCOORD2;");
        s_Builder.AppendLine("    float4 TangentRow2 : TEXCOORD3;");
        s_Builder.AppendLine("    float4 TexCoord    : TEXCOORD4;");
        s_Builder.AppendLine("};");
        s_Builder.AppendLine();
        s_Builder.AppendLine("struct PsOut");
        s_Builder.AppendLine("{");
        s_Builder.AppendLine("    float4 Target0 : SV_Target0;");
        s_Builder.AppendLine("    float4 Target1 : SV_Target1;");
        s_Builder.AppendLine("    float4 Target2 : SV_Target2;");
        s_Builder.AppendLine("    float4 Target3 : SV_Target3;");
        s_Builder.AppendLine("};");
        s_Builder.AppendLine();
        s_Builder.AppendLine("PsOut main(PsIn i)");
        s_Builder.AppendLine("{");
        s_Builder.AppendLine("    PsOut o;");
        s_Builder.Append(p_Body);
        s_Builder.AppendLine("    return o;");
        s_Builder.AppendLine("}");

        return s_Builder.ToString();
    }
}
