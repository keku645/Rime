using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RimeShaderEditor.Graph;

/// <summary>
/// The node catalog. Node names, port types and the enum-valued parameters come from DICE's own vocabulary,
/// recovered from the shipped BF3 engine (fb::ShaderPortType, fb::BlendShaderMode, fb::CurveShaderType) â€”
/// the FrostED graph instances were stripped at bake time but these enums survive in the binary.
/// The per-mode blend FORMULAS are conventional, not verified against FrostED.
/// </summary>
public static class Palette
{
    private static readonly Dictionary<string, NodeDef> s_Defs = new();
    public static IReadOnlyCollection<NodeDef> All => s_Defs.Values;

    public static NodeDef Get(string p_Kind) =>
        s_Defs.TryGetValue(p_Kind, out var s_Def) ? s_Def : s_Defs["Scalar"];

    public static bool Has(string p_Kind) => s_Defs.ContainsKey(p_Kind);

    private static void Add(NodeDef p_Def) => s_Defs[p_Def.Kind] = p_Def;

    private static string Lit(float p_Value) => p_Value.ToString("0.0######", CultureInfo.InvariantCulture);

    static Palette()
    {
        AddInputs();
        AddTextures();
        AddConstants();
        AddMath();
        AddMoreMath();
        AddVector();
        AddUv();
        AddRoot();
    }

    private static void AddInputs()
    {
        Add(new NodeDef
        {
            Kind = "TexCoord",
            Title = "TexCoord",
            Category = "Inputs",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec2 } },
            Params = { new ParamDef { Name = "Tiling", Kind = ParamKind.Text, Default = "1,1" } },
            // No coordinate-set selector: every StreamableTexture measured across the drum, the parachute and
            // the storm drain binds VertexElementUsage_TexCoord0, and this contract carries a single UV
            // interpolator, so a set index would offer exactly one valid value. Multi-set support needs a
            // target whose vertex shader actually outputs more than one.
            Emit = (p_S, p_N) => p_S.Out("Out", Tiled(p_S, p_N, "i.TexCoord.xy")),
        });

        Add(new NodeDef
        {
            Kind = "WorldPos",
            Title = "World Position",
            Category = "Inputs",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec3 } },
            Emit = (p_S, _) => p_S.Out("Out", "i.WorldPos.xyz"),
        });

        Add(new NodeDef
        {
            Kind = "CameraPos",
            Title = "Camera Position",
            Category = "Inputs",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec3 } },
            Emit = (p_S, _) => p_S.Out("Out", "cameraPos"),
        });

        Add(new NodeDef
        {
            Kind = "Time",
            Title = "Time",
            Category = "Inputs",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Emit = (p_S, _) => p_S.Out("Out", "time"),
        });

        Add(new NodeDef
        {
            Kind = "ScreenSize",
            Title = "Screen Size",
            Category = "Inputs",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Emit = (p_S, _) => p_S.Out("Out", "screenSize"),
        });

        Add(new NodeDef
        {
            Kind = "GeometricNormal",
            Title = "Geometric Normal",
            Category = "Inputs",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec3 } },
            // The tangent frame arrives as three interpolated rows; its third column is the surface normal.
            Emit = (p_S, _) => p_S.Out("Out",
                "normalize(float3(i.TangentRow0.z, i.TangentRow1.z, i.TangentRow2.z))"),
        });

        Add(new NodeDef
        {
            Kind = "ViewDirection",
            Title = "View Direction",
            Category = "Inputs",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec3 } },
            Emit = (p_S, _) => p_S.Out("Out", "normalize(cameraPos - i.WorldPos.xyz)"),
        });

        Add(new NodeDef
        {
            Kind = "ScreenPosition",
            Title = "Screen Position",
            Category = "Inputs",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec2 } },
            // Rasteriser position over the render-target size gives 0..1 screen UVs.
            Emit = (p_S, _) => p_S.Out("Out", "i.Position.xy * screenSize.zw"),
        });

        Add(new NodeDef
        {
            Kind = "ViewportZParams",
            Title = "Viewport Z Params",
            Category = "Inputs",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Emit = (p_S, _) => p_S.Out("Out", "viewportZMinMaxKzKw"),
        });

        Add(new NodeDef
        {
            Kind = "Fresnel",
            Title = "Fresnel",
            Category = "Inputs",
            Inputs =
            {
                new PortDef { Name = "Normal", Type = ShaderPortType.SptVec3, Default = "float3(0, 0, 1)" },
                new PortDef { Name = "Exponent", Type = ShaderPortType.SptScalar, Default = "4.0" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            // Same form the vanilla GBuffer shader uses: saturate(1.001 - N.V) raised to a power. The input is
            // a TANGENT-space normal, matched to how the NormalMap node hands one over.
            Emit = (p_S, _) =>
            {
                var s_Normal = p_S.Declare("fresnelN", ShaderPortType.SptVec3, p_S.In("Normal"));
                var s_World = p_S.Declare("fresnelWs", ShaderPortType.SptVec3,
                    $"normalize(float3(dot({s_Normal}, i.TangentRow0.xyz), " +
                    $"dot({s_Normal}, i.TangentRow1.xyz), dot({s_Normal}, i.TangentRow2.xyz)))");
                var s_View = p_S.Declare("fresnelV", ShaderPortType.SptVec3,
                    "normalize(cameraPos - i.WorldPos.xyz)");
                p_S.Out("Out",
                    $"pow(saturate(1.001 - dot({s_World}, {s_View})), {p_S.In("Exponent")})");
            },
        });
    }

    /// <summary>
    /// Sample or SampleLevel depending on whether the Lod pin is wired â€” an explicit mip has to bypass the
    /// hardware's derivative-based selection, so it cannot be folded into Sample.
    /// </summary>
    private static string SampleCall(IEmitScope p_Scope, GraphNode p_Node, string p_Register)
    {
        var s_Texture = HlslNames.TextureVar(p_Register);
        var s_Coord = Tiled(p_Scope, p_Node, p_Scope.In("Coord"));

        return p_Scope.HasExplicitValue("Lod")
            ? $"{s_Texture}.SampleLevel(sampler0, {s_Coord}, {p_Scope.In("Lod")})"
            : $"{s_Texture}.Sample(sampler0, {s_Coord})";
    }

    /// <summary>
    /// Applies the node's Tiling to a UV. This is not a borrowed convenience: DICE bakes a per-texture UV scale
    /// straight into the compiled shader — the Parachute pixel shader multiplies its texcoord by 20.0 and 0.5,
    /// which are exactly 1/Factor for the 0.05 and 2 recorded on those StreamableTextures in the shaderdb.
    /// Exposed here as the tiling itself (the reciprocal of Factor) because that is the intuitive direction.
    /// </summary>
    private static string Tiled(IEmitScope p_Scope, GraphNode p_Node, string p_Coord)
    {
        var s_Tiling = p_Node.GetParam("Tiling");
        var s_Literal = PortTypeUtils.FormatLiteral(s_Tiling, ShaderPortType.SptVec2);
        if (s_Literal == null || s_Tiling.Trim() is "1" or "1,1")
            return p_Coord;

        return $"({p_Coord}) * {s_Literal}";
    }

    private static void AddTextures()
    {
        Add(new NodeDef
        {
            Kind = "Texture",
            Title = "Texture",
            Category = "Textures",
            HasTexturePreview = true,
            Inputs =
            {
                new PortDef { Name = "Coord", Type = ShaderPortType.SptVec2, Default = "i.TexCoord.xy", DefaultIsExpression = true },
                new PortDef { Name = "Lod", Type = ShaderPortType.SptScalar, Default = "0.0" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Params =
            {
                new ParamDef { Name = "Register", Kind = ParamKind.Text, Default = "1" },
                new ParamDef { Name = "Tiling", Kind = ParamKind.Text, Default = "1,1" },
            },
            Description = "Tiling multiplies the UV, the way DICE bakes 1/Factor from the shaderdb's " +
                          "StreamableTexture into the compiled shader. Register picks which tN slot to read.",
            Emit = (p_S, p_N) => p_S.Out("Out", SampleCall(p_S, p_N, p_N.GetParam("Register"))),
        });

        Add(new NodeDef
        {
            Kind = "NormalMap",
            Title = "Normal Map",
            Category = "Textures",
            HasTexturePreview = true,
            Inputs =
            {
                new PortDef { Name = "Coord", Type = ShaderPortType.SptVec2, Default = "i.TexCoord.xy", DefaultIsExpression = true },
                new PortDef { Name = "Lod", Type = ShaderPortType.SptScalar, Default = "0.0" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec3 } },
            Params =
            {
                new ParamDef { Name = "Register", Kind = ParamKind.Text, Default = "2" },
                new ParamDef { Name = "Tiling", Kind = ParamKind.Text, Default = "1,1" },
            },
            Description = "Decodes a BF3 DXT5nm normal map (X lives in the ALPHA channel). Tiling multiplies " +
                          "the UV, as DICE bakes 1/Factor into the compiled shader.",
            // BF3 normal maps are DXT5nm with X in the ALPHA channel: nx = (R*A)*2-1, ny = G*2-1,
            // nz reconstructed. Reading B instead produces a metallic-looking mess.
            Emit = (p_S, p_N) =>
            {
                var s_Sample = p_S.Declare("nmSample", ShaderPortType.SptVec4,
                    SampleCall(p_S, p_N, p_N.GetParam("Register")));
                var s_Xy = p_S.Declare("nmXy", ShaderPortType.SptVec2,
                    $"float2({s_Sample}.x * {s_Sample}.w, {s_Sample}.y) * 2.0 - 1.0");
                p_S.Out("Out",
                    $"float3({s_Xy}, sqrt(max(0.0, 1.0 - dot({s_Xy}, {s_Xy}))))");
            },
        });
    }

    private static void AddConstants()
    {
        Add(new NodeDef
        {
            Kind = "Color",
            Title = "Color",
            Category = "Constants",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptColor } },
            Params = { new ParamDef { Name = "Value", Kind = ParamKind.Color, Default = "1,1,1,1" } },
            Emit = (p_S, p_N) =>
            {
                var s_Parts = p_N.GetParam("Value").Split(',');
                var s_Values = new float[4];
                for (var i = 0; i < 4; i++)
                    s_Values[i] = i < s_Parts.Length &&
                                  float.TryParse(s_Parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var s_V)
                        ? s_V
                        : 1.0f;

                p_S.Out("Out",
                    $"float4({Lit(s_Values[0])}, {Lit(s_Values[1])}, {Lit(s_Values[2])}, {Lit(s_Values[3])})");
            },
        });

        Add(new NodeDef
        {
            Kind = "Scalar",
            Title = "Scalar",
            Category = "Constants",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Params = { new ParamDef { Name = "Value", Kind = ParamKind.Scalar, Default = "1" } },
            Emit = (p_S, p_N) => p_S.Out("Out", Lit(p_S.ParamScalar("Value"))),
        });
    }

    private static void AddMath()
    {
        AddBinary("Multiply", "Multiply", "{0} * {1}");
        AddBinary("Add", "Add", "{0} + {1}");
        AddBinary("Subtract", "Subtract", "{0} - {1}");

        Add(new NodeDef
        {
            Kind = "Lerp",
            Title = "Lerp",
            Category = "Math",
            Inputs =
            {
                new PortDef { Name = "Input1", Type = ShaderPortType.SptVec4, Default = "float4(0,0,0,0)" },
                new PortDef { Name = "Input2", Type = ShaderPortType.SptVec4, Default = "float4(1,1,1,1)" },
                new PortDef { Name = "Delta", Type = ShaderPortType.SptScalar, Default = "0.5" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Emit = (p_S, _) => p_S.Out("Out",
                $"lerp({p_S.In("Input1")}, {p_S.In("Input2")}, {p_S.In("Delta")})"),
        });

        Add(new NodeDef
        {
            Kind = "Blend",
            Title = "Blend",
            Category = "Math",
            Inputs =
            {
                new PortDef { Name = "Color1", Type = ShaderPortType.SptVec4, Default = "float4(0,0,0,1)" },
                new PortDef { Name = "Color2", Type = ShaderPortType.SptVec4, Default = "float4(1,1,1,1)" },
                new PortDef { Name = "Opacity", Type = ShaderPortType.SptScalar, Default = "1.0" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Params =
            {
                new ParamDef
                {
                    Name = "Mode", Kind = ParamKind.Enum, Default = nameof(BlendShaderMode.BsmLerp),
                    EnumType = typeof(BlendShaderMode),
                },
            },
            Emit = (p_S, _) =>
            {
                var s_A = p_S.In("Color1");
                var s_B = p_S.In("Color2");
                var s_T = p_S.In("Opacity");
                var s_Blended = p_S.ParamEnum<BlendShaderMode>("Mode") switch
                {
                    BlendShaderMode.BsmAdd => $"{s_A} + {s_B}",
                    BlendShaderMode.BsmSubtract => $"{s_A} - {s_B}",
                    BlendShaderMode.BsmMultiply => $"{s_A} * {s_B}",
                    BlendShaderMode.BsmMultiply2x => $"{s_A} * {s_B} * 2.0",
                    BlendShaderMode.BsmScreen => $"1.0 - (1.0 - {s_A}) * (1.0 - {s_B})",
                    BlendShaderMode.BsmDifference => $"abs({s_A} - {s_B})",
                    BlendShaderMode.BsmLighten => $"max({s_A}, {s_B})",
                    BlendShaderMode.BsmDarken => $"min({s_A}, {s_B})",
                    BlendShaderMode.BsmOverlay =>
                        $"lerp(2.0 * {s_A} * {s_B}, 1.0 - 2.0 * (1.0 - {s_A}) * (1.0 - {s_B}), step(0.5, {s_A}))",
                    _ => s_B,
                };

                p_S.Out("Out", $"lerp({s_A}, {s_Blended}, {s_T})");
            },
        });

        AddUnary("Normalize", "Normalize", ShaderPortType.SptVec3, "normalize({0})");
        AddUnary("Saturate", "Saturate", ShaderPortType.SptVec4, "saturate({0})");
        AddUnary("OneMinus", "One Minus", ShaderPortType.SptVec4, "1.0 - {0}");

        Add(new NodeDef
        {
            Kind = "Dot",
            Title = "Dot",
            Category = "Math",
            Inputs =
            {
                new PortDef { Name = "A", Type = ShaderPortType.SptVec3, Default = "float3(0,0,1)" },
                new PortDef { Name = "B", Type = ShaderPortType.SptVec3, Default = "float3(0,0,1)" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Emit = (p_S, _) => p_S.Out("Out", $"dot({p_S.In("A")}, {p_S.In("B")})"),
        });

        Add(new NodeDef
        {
            Kind = "Power",
            Title = "Power",
            Category = "Math",
            Inputs =
            {
                new PortDef { Name = "Base", Type = ShaderPortType.SptScalar, Default = "0.5" },
                new PortDef { Name = "Exponent", Type = ShaderPortType.SptScalar, Default = "2.0" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Emit = (p_S, _) => p_S.Out("Out",
                $"pow(max(1e-6, {p_S.In("Base")}), {p_S.In("Exponent")})"),
        });

        Add(new NodeDef
        {
            Kind = "Curve",
            Title = "Curve",
            Category = "Math",
            Inputs = { new PortDef { Name = "Input", Type = ShaderPortType.SptScalar, Default = "time", DefaultIsExpression = true } },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Params =
            {
                new ParamDef
                {
                    Name = "Type", Kind = ParamKind.Enum, Default = nameof(CurveShaderType.CstSine),
                    EnumType = typeof(CurveShaderType),
                },
            },
            Emit = (p_S, _) =>
            {
                var s_X = p_S.In("Input");
                p_S.Out("Out", p_S.ParamEnum<CurveShaderType>("Type") switch
                {
                    CurveShaderType.CstSineNormalized => $"sin({s_X} * 6.2831853) * 0.5 + 0.5",
                    CurveShaderType.CstSawtooth => $"frac({s_X})",
                    CurveShaderType.CstTriangle => $"abs(frac({s_X}) * 2.0 - 1.0)",
                    CurveShaderType.CstSquare => $"step(0.5, frac({s_X})) * 2.0 - 1.0",
                    _ => $"sin({s_X} * 6.2831853)",
                });
            },
        });

        Add(new NodeDef
        {
            Kind = "MixOut",
            Title = "MixOut",
            Category = "Math",
            Inputs = { new PortDef { Name = "Input", Type = ShaderPortType.SptVec4, Default = "float4(0,0,0,0)" } },
            Outputs =
            {
                new PortDef { Name = "RGB", Type = ShaderPortType.SptVec3 },
                new PortDef { Name = "R", Type = ShaderPortType.SptScalar },
                new PortDef { Name = "G", Type = ShaderPortType.SptScalar },
                new PortDef { Name = "B", Type = ShaderPortType.SptScalar },
                new PortDef { Name = "A", Type = ShaderPortType.SptScalar },
            },
            Emit = (p_S, _) =>
            {
                var s_In = p_S.Declare("mix", ShaderPortType.SptVec4, p_S.In("Input"));
                p_S.Out("RGB", $"{s_In}.xyz");
                p_S.Out("R", $"{s_In}.x");
                p_S.Out("G", $"{s_In}.y");
                p_S.Out("B", $"{s_In}.z");
                p_S.Out("A", $"{s_In}.w");
            },
        });
    }

    /// <summary>
    /// Wrappers over HLSL intrinsics. Unlike the Blend modes, the Curve shapes, the port types and the root
    /// pins â€” which come from DICE's own enums â€” these node NAMES are ours: BF3 ships no shader-graph node
    /// classes at all (verified against the retail client binary, its decompilation, and both server builds),
    /// so there is no authoritative list to mirror.
    /// </summary>
    private static void AddMoreMath()
    {
        AddBinary("Divide", "Divide", "{0} / {1}");
        AddBinary("Min", "Min", "min({0}, {1})");
        AddBinary("Max", "Max", "max({0}, {1})");
        AddBinary("Fmod", "Modulo", "fmod({0}, {1})");
        AddBinary("Step", "Step", "step({0}, {1})");

        AddUnary("Abs", "Abs", ShaderPortType.SptVec4, "abs({0})");
        AddUnary("Floor", "Floor", ShaderPortType.SptVec4, "floor({0})");
        AddUnary("Ceil", "Ceil", ShaderPortType.SptVec4, "ceil({0})");
        AddUnary("Frac", "Frac", ShaderPortType.SptVec4, "frac({0})");
        AddUnary("Round", "Round", ShaderPortType.SptVec4, "round({0})");
        AddUnary("Sign", "Sign", ShaderPortType.SptVec4, "sign({0})");
        AddUnary("Negate", "Negate", ShaderPortType.SptVec4, "-({0})");

        AddUnary("Sqrt", "Sqrt", ShaderPortType.SptScalar, "sqrt(max(0.0, {0}))");
        AddUnary("Exp", "Exp", ShaderPortType.SptScalar, "exp({0})");
        AddUnary("Log2", "Log2", ShaderPortType.SptScalar, "log2(max(1e-6, {0}))");
        AddUnary("Sin", "Sin", ShaderPortType.SptScalar, "sin({0})");
        AddUnary("Cos", "Cos", ShaderPortType.SptScalar, "cos({0})");

        Add(new NodeDef
        {
            Kind = "Atan2",
            Title = "Atan2",
            Category = "Math",
            Inputs =
            {
                new PortDef { Name = "Y", Type = ShaderPortType.SptScalar, Default = "0.0" },
                new PortDef { Name = "X", Type = ShaderPortType.SptScalar, Default = "1.0" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Emit = (p_S, _) => p_S.Out("Out", $"atan2({p_S.In("Y")}, {p_S.In("X")})"),
        });

        Add(new NodeDef
        {
            Kind = "Clamp",
            Title = "Clamp",
            Category = "Math",
            Inputs =
            {
                new PortDef { Name = "Input", Type = ShaderPortType.SptVec4, Default = "float4(0,0,0,0)" },
                new PortDef { Name = "Min", Type = ShaderPortType.SptVec4, Default = "float4(0,0,0,0)" },
                new PortDef { Name = "Max", Type = ShaderPortType.SptVec4, Default = "float4(1,1,1,1)" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Emit = (p_S, _) => p_S.Out("Out",
                $"clamp({p_S.In("Input")}, {p_S.In("Min")}, {p_S.In("Max")})"),
        });

        Add(new NodeDef
        {
            Kind = "SmoothStep",
            Title = "SmoothStep",
            Category = "Math",
            Inputs =
            {
                new PortDef { Name = "Edge0", Type = ShaderPortType.SptScalar, Default = "0.0" },
                new PortDef { Name = "Edge1", Type = ShaderPortType.SptScalar, Default = "1.0" },
                new PortDef { Name = "Input", Type = ShaderPortType.SptScalar, Default = "0.5" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Emit = (p_S, _) => p_S.Out("Out",
                $"smoothstep({p_S.In("Edge0")}, {p_S.In("Edge1")}, {p_S.In("Input")})"),
        });

        Add(new NodeDef
        {
            Kind = "Remap",
            Title = "Remap",
            Category = "Math",
            Inputs =
            {
                new PortDef { Name = "Input", Type = ShaderPortType.SptScalar, Default = "0.0" },
                new PortDef { Name = "FromMin", Type = ShaderPortType.SptScalar, Default = "0.0" },
                new PortDef { Name = "FromMax", Type = ShaderPortType.SptScalar, Default = "1.0" },
                new PortDef { Name = "ToMin", Type = ShaderPortType.SptScalar, Default = "0.0" },
                new PortDef { Name = "ToMax", Type = ShaderPortType.SptScalar, Default = "1.0" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Emit = (p_S, _) =>
            {
                var s_From = p_S.Declare("remapFrom", ShaderPortType.SptScalar,
                    $"{p_S.In("FromMax")} - {p_S.In("FromMin")}");
                p_S.Out("Out",
                    $"{p_S.In("ToMin")} + ({p_S.In("Input")} - {p_S.In("FromMin")}) / " +
                    $"(abs({s_From}) < 1e-6 ? 1e-6 : {s_From}) * ({p_S.In("ToMax")} - {p_S.In("ToMin")})");
            },
        });
    }

    private static void AddVector()
    {
        Add(new NodeDef
        {
            Kind = "Append",
            Title = "Append",
            Category = "Vector",
            Inputs =
            {
                new PortDef { Name = "X", Type = ShaderPortType.SptScalar, Default = "0.0" },
                new PortDef { Name = "Y", Type = ShaderPortType.SptScalar, Default = "0.0" },
                new PortDef { Name = "Z", Type = ShaderPortType.SptScalar, Default = "0.0" },
                new PortDef { Name = "W", Type = ShaderPortType.SptScalar, Default = "1.0" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            // Always builds a float4; wiring it into a narrower input takes the leading components.
            Emit = (p_S, _) => p_S.Out("Out",
                $"float4({p_S.In("X")}, {p_S.In("Y")}, {p_S.In("Z")}, {p_S.In("W")})"),
        });

        Add(new NodeDef
        {
            Kind = "Swizzle",
            Title = "Swizzle",
            Category = "Vector",
            Inputs = { new PortDef { Name = "Input", Type = ShaderPortType.SptVec4, Default = "float4(0,0,0,0)" } },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Params = { new ParamDef { Name = "Channels", Kind = ParamKind.Text, Default = "xyzw" } },
            Emit = (p_S, p_N) =>
            {
                var s_Channels = new string(p_N.GetParam("Channels")
                    .Where(p_C => "xyzwrgba".Contains(p_C)).Take(4).ToArray());

                if (s_Channels.Length == 0)
                    s_Channels = "xyzw";

                var s_Input = p_S.Declare("swz", ShaderPortType.SptVec4, p_S.In("Input"));
                var s_Padding = string.Join("", Enumerable.Repeat(", 0", 4 - s_Channels.Length));
                p_S.Out("Out", $"float4({s_Input}.{s_Channels}{s_Padding})");
            },
        });

        Add(new NodeDef
        {
            Kind = "Cross",
            Title = "Cross",
            Category = "Vector",
            Inputs =
            {
                new PortDef { Name = "A", Type = ShaderPortType.SptVec3, Default = "float3(1,0,0)" },
                new PortDef { Name = "B", Type = ShaderPortType.SptVec3, Default = "float3(0,1,0)" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec3 } },
            Emit = (p_S, _) => p_S.Out("Out", $"cross({p_S.In("A")}, {p_S.In("B")})"),
        });

        Add(new NodeDef
        {
            Kind = "Length",
            Title = "Length",
            Category = "Vector",
            Inputs = { new PortDef { Name = "Input", Type = ShaderPortType.SptVec3, Default = "float3(0,0,0)" } },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Emit = (p_S, _) => p_S.Out("Out", $"length({p_S.In("Input")})"),
        });

        Add(new NodeDef
        {
            Kind = "Distance",
            Title = "Distance",
            Category = "Vector",
            Inputs =
            {
                new PortDef { Name = "A", Type = ShaderPortType.SptVec3, Default = "float3(0,0,0)" },
                new PortDef { Name = "B", Type = ShaderPortType.SptVec3, Default = "float3(0,0,0)" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Emit = (p_S, _) => p_S.Out("Out", $"distance({p_S.In("A")}, {p_S.In("B")})"),
        });

        Add(new NodeDef
        {
            Kind = "Reflect",
            Title = "Reflect",
            Category = "Vector",
            Inputs =
            {
                new PortDef { Name = "Incident", Type = ShaderPortType.SptVec3, Default = "float3(0,0,-1)" },
                new PortDef { Name = "Normal", Type = ShaderPortType.SptVec3, Default = "float3(0,0,1)" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec3 } },
            Emit = (p_S, _) => p_S.Out("Out", $"reflect({p_S.In("Incident")}, {p_S.In("Normal")})"),
        });
    }

    private static void AddUv()
    {
        Add(new NodeDef
        {
            Kind = "UvTransform",
            Title = "UV Transform",
            Category = "UV",
            Inputs =
            {
                new PortDef { Name = "Coord", Type = ShaderPortType.SptVec2, Default = "i.TexCoord.xy", DefaultIsExpression = true },
                new PortDef { Name = "Scale", Type = ShaderPortType.SptVec2, Default = "float2(1, 1)" },
                new PortDef { Name = "Offset", Type = ShaderPortType.SptVec2, Default = "float2(0, 0)" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec2 } },
            Emit = (p_S, _) => p_S.Out("Out",
                $"{p_S.In("Coord")} * {p_S.In("Scale")} + {p_S.In("Offset")}"),
        });

        Add(new NodeDef
        {
            Kind = "Panner",
            Title = "Panner",
            Category = "UV",
            Inputs =
            {
                new PortDef { Name = "Coord", Type = ShaderPortType.SptVec2, Default = "i.TexCoord.xy", DefaultIsExpression = true },
                new PortDef { Name = "Speed", Type = ShaderPortType.SptVec2, Default = "float2(0.1, 0)" },
                new PortDef { Name = "Time", Type = ShaderPortType.SptScalar, Default = "time", DefaultIsExpression = true },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec2 } },
            Emit = (p_S, _) => p_S.Out("Out",
                $"{p_S.In("Coord")} + {p_S.In("Speed")} * {p_S.In("Time")}"),
        });

        Add(new NodeDef
        {
            Kind = "Rotator",
            Title = "Rotator",
            Category = "UV",
            Inputs =
            {
                new PortDef { Name = "Coord", Type = ShaderPortType.SptVec2, Default = "i.TexCoord.xy", DefaultIsExpression = true },
                new PortDef { Name = "Centre", Type = ShaderPortType.SptVec2, Default = "float2(0.5, 0.5)" },
                new PortDef { Name = "Angle", Type = ShaderPortType.SptScalar, Default = "0.0" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec2 } },
            Emit = (p_S, _) =>
            {
                var s_Angle = p_S.Declare("rotAngle", ShaderPortType.SptScalar, p_S.In("Angle"));
                var s_Centre = p_S.Declare("rotCentre", ShaderPortType.SptVec2, p_S.In("Centre"));
                var s_Local = p_S.Declare("rotLocal", ShaderPortType.SptVec2,
                    $"{p_S.In("Coord")} - {s_Centre}");
                var s_Sin = p_S.Declare("rotSin", ShaderPortType.SptScalar, $"sin({s_Angle})");
                var s_Cos = p_S.Declare("rotCos", ShaderPortType.SptScalar, $"cos({s_Angle})");
                p_S.Out("Out",
                    $"float2({s_Local}.x * {s_Cos} - {s_Local}.y * {s_Sin}, " +
                    $"{s_Local}.x * {s_Sin} + {s_Local}.y * {s_Cos}) + {s_Centre}");
            },
        });
    }

    private static void AddBinary(string p_Kind, string p_Title, string p_Format)
    {
        Add(new NodeDef
        {
            Kind = p_Kind,
            Title = p_Title,
            Category = "Math",
            Inputs =
            {
                new PortDef { Name = "Input1", Type = ShaderPortType.SptVec4, Default = "float4(1,1,1,1)" },
                new PortDef { Name = "Input2", Type = ShaderPortType.SptVec4, Default = "float4(1,1,1,1)" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Emit = (p_S, _) => p_S.Out("Out",
                string.Format(p_Format, $"({p_S.In("Input1")})", $"({p_S.In("Input2")})")),
        });
    }

    private static void AddUnary(string p_Kind, string p_Title, ShaderPortType p_Type, string p_Format)
    {
        Add(new NodeDef
        {
            Kind = p_Kind,
            Title = p_Title,
            Category = "Math",
            Inputs = { new PortDef { Name = "Input", Type = p_Type, Default = p_Type == ShaderPortType.SptVec3 ? "float3(0,0,1)" : "float4(0,0,0,0)" } },
            Outputs = { new PortDef { Name = "Out", Type = p_Type } },
            Emit = (p_S, _) => p_S.Out("Out", string.Format(p_Format, $"({p_S.In("Input")})")),
        });
    }

    /// <summary>
    /// Opacity only means anything once the surface is alpha-tested or transparent, and that is a property of
    /// the shaderdb's SurfaceShaderInfo rather than of the pixel shader we generate.
    /// </summary>
    private static string? OpacityReason(GraphNode p_Node)
    {
        var s_Type = Enum.TryParse<SurfaceShaderType>(p_Node.GetParam("SurfaceShaderType"), out var s_Parsed)
            ? s_Parsed
            : SurfaceShaderType.SurfaceShaderType_Opaque;

        return s_Type == SurfaceShaderType.SurfaceShaderType_Opaque
            ? "ignored while SurfaceShaderType is Opaque"
            : null;
    }

    /// <summary>
    /// Every root writes the SAME four render targets; the only difference between the lighting models is the
    /// integer in RT2.w, which is what Dx11/DeferredOutdoorLight branches on when it reads the GBuffer. They
    /// exist as separate palette entries for discoverability, but share this one implementation so the packing
    /// cannot drift between them.
    /// </summary>
    private static void AddRootVariant(string p_Kind, string p_Title, ShaderLightingModel p_Model,
        string p_Description)
    {
        const string c_NotIdentified =
            "RT2.xyz is not read by the outdoor light pass; channel still unidentified";

        Add(new NodeDef
        {
            Kind = p_Kind,
            Title = p_Title,
            Category = "Output",
            Description = p_Description,
            IsRoot = true,
            Inputs =
            {
                new PortDef { Name = "Diffuse", Type = ShaderPortType.SptVec3, Default = "float3(0.5, 0.5, 0.5)" },
                new PortDef { Name = "Emissive", Type = ShaderPortType.SptVec3, Default = "float3(0, 0, 0)" },
                new PortDef { Name = "FresnelBias", Type = ShaderPortType.SptScalar, Default = "0.6" },
                new PortDef { Name = "FresnelExponent", Type = ShaderPortType.SptScalar, Default = "4.0" },
                new PortDef { Name = "Normal", Type = ShaderPortType.SptVec3, Default = "float3(0, 0, 1)" },
                new PortDef { Name = "Occlusion", Type = ShaderPortType.SptScalar, Default = "1.0",
                    UnavailableReason = _ => c_NotIdentified },
                new PortDef { Name = "Opacity", Type = ShaderPortType.SptScalar, Default = "1.0",
                    UnavailableReason = OpacityReason },
                // FrostED calls this pin Roughness, but the engine treats RT0.w as SMOOTHNESS: the outdoor
                // light pass derives specExp = 2^(w*10+1) and envmap LOD = (1-w)*10 from it, so 1 is a mirror
                // and 0 is matte. Named for what it does, or every graph authored here would come out inverted.
                new PortDef { Name = "Smoothness", Type = ShaderPortType.SptScalar, Default = "0.5" },
                new PortDef { Name = "SkyOcclusion", Type = ShaderPortType.SptScalar, Default = "1.0",
                    UnavailableReason = _ => c_NotIdentified },
                new PortDef { Name = "Specular", Type = ShaderPortType.SptScalar, Default = "0.5" },
            },
            Params =
            {
                new ParamDef
                {
                    Name = "SurfaceShaderType", Kind = ParamKind.Enum,
                    Default = nameof(SurfaceShaderType.SurfaceShaderType_Opaque),
                    EnumType = typeof(SurfaceShaderType),
                },
                new ParamDef { Name = "DoubleSided", Kind = ParamKind.Bool, Default = "false" },
                new ParamDef { Name = "GeometryPassEnable", Kind = ParamKind.Bool, Default = "true" },
            },
            Emit = (p_S, _) => EmitGBufferPacking(p_S, p_Model),
        });
    }

    /// <summary>
    /// Packing measured on both sides: the vanilla OilDrumBarrel GBuffer pixel shader that writes it, and
    /// Dx11/DeferredOutdoorLight (from Systems/ShaderProgramDb) that reads it back.
    /// </summary>
    private static void EmitGBufferPacking(IEmitScope p_S, ShaderLightingModel p_Model)
    {
        var s_TangentNormal = p_S.Declare("tsNormal", ShaderPortType.SptVec3, p_S.In("Normal"));
        var s_WorldNormal = p_S.Declare("wsNormal", ShaderPortType.SptVec3,
            $"normalize(float3(dot({s_TangentNormal}, i.TangentRow0.xyz), " +
            $"dot({s_TangentNormal}, i.TangentRow1.xyz), " +
            $"dot({s_TangentNormal}, i.TangentRow2.xyz)))");

        var s_View = p_S.Declare("viewDir", ShaderPortType.SptVec3, "normalize(cameraPos - i.WorldPos.xyz)");
        var s_Fresnel = p_S.Declare("fresnel", ShaderPortType.SptScalar,
            $"pow(saturate(1.001 - dot({s_WorldNormal}, {s_View})), {p_S.In("FresnelExponent")})");

        // lerp(bias, 1, f) reproduces vanilla's 0.6 + 0.4*f exactly at bias 0.6.
        var s_SpecScale = p_S.Declare("specScale", ShaderPortType.SptScalar,
            $"lerp({p_S.In("FresnelBias")}, 1.0, {s_Fresnel})");

        p_S.Line($"o.Target0.xyz = {s_WorldNormal} * 0.5 + 0.5;");
        p_S.Line($"o.Target0.w = {p_S.In("Smoothness")};");
        p_S.Line($"o.Target1.xyz = sqrt(max(0.0, {p_S.In("Diffuse")}));");
        p_S.Line($"o.Target1.w = sqrt(max(0.0, {s_SpecScale} * {p_S.In("Specular")}));");

        // RT2.w selects the lighting model: the reader compares it against 1.5/255, 2.5/255 and 3.5/255.
        p_S.Line($"o.Target2 = float4(0, 0, 0, {Lit((int) p_Model)} / 255.0);");

        // RT3 is emissive, read back as RT3.xyz * RT3.w * 8. Unconnected it reproduces vanilla's
        // (0,0,0,1/255) exactly so the correctness harness stays byte-faithful.
        if (p_S.HasExplicitValue("Emissive"))
            p_S.Line($"o.Target3 = float4({p_S.In("Emissive")}, 0.125);");
        else
            p_S.Line("o.Target3 = float4(0, 0, 0, 1.0 / 255.0);");
    }

    private static void AddRoot()
    {
        // Pin names are the real StandardRoot set from FrostED. The four models are separate entries only for
        // discoverability: they emit identical packing and differ solely in the RT2.w selector, so there is no
        // hidden per-model magic on the WRITE side — the difference is entirely in how the light pass reads it.
        // ShaderLightingModel_Translucent is deliberately absent: the deferred outdoor light pass has no branch
        // for it (translucency goes through the forward Default pass), and that contract is not measured yet.
        AddRootVariant("StandardRoot", "StandardRoot", ShaderLightingModel.ShaderLightingModel_Standard,
            "Deferred default. The light pass gives it WHITE specular of intensity RT1.w.");

        AddRootVariant("MetallicRoot", "MetallicRoot", ShaderLightingModel.ShaderLightingModel_Metallic,
            "Writes RT2.w = 1/255. The light pass TINTS the specular by the albedo (specular = RT1.rgb * RT1.w) " +
            "instead of using white, which is what makes a surface read as metal.");

        AddRootVariant("SkinRoot", "SkinRoot", ShaderLightingModel.ShaderLightingModel_Skin,
            "Writes RT2.w = 2/255. The light pass adds a wrap-lighting term: a cubic in N.L " +
            "(1.14989, -2.14564, 0.841609, 0.154141) times a GLOBAL subsurface colour from the light constants " +
            "-- that colour is not per-material, so no pin controls it.");

        AddRootVariant("DynamicEnvmapRoot", "DynamicEnvmapRoot",
            ShaderLightingModel.ShaderLightingModel_DynamicEnvmap,
            "Writes RT2.w = 3/255. The light pass samples the DYNAMIC envmap instead of the sky cubemap. " +
            "Note the dynamic envmap capture is not wired up in BF3 retail, so expect no reflection to appear.");
    }
}

public static class HlslNames
{
    /// <summary>Maps a texture register index to the variable name the target shader's binding table uses.</summary>
    public static string TextureVar(string p_Register) =>
        p_Register == "1" ? "texture_Texture" : $"texture_Texture{p_Register}";
}
