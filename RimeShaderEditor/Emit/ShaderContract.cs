using System;
using System.Collections.Generic;
using System.Linq;

namespace RimeShaderEditor.Emit;

public enum ShaderFamily
{
    Unknown,
    RigidMesh,
    RigidMeshSubMaterial,
    Skinned,

    /// <summary>Four interpolators: the three tangent-frame component rows then the UV. MEASURED from the
    /// preset vertex shaders (2026-08-20): vegetation AND vehicle bodies assemble their outputs
    /// identically — o1/o2/o3 collect the x/y/z components of (tangent, bitangent, normal) exactly like
    /// the rigid rows, o4 carries the UV (vehicles pack TWO sets as xyzw, vegetation one as xy). One
    /// family serves both.</summary>
    Vegetation,
    Terrain,

    /// <summary>Three interpolators, no tangent frame: absolute world position, the world-space NORMAL
    /// itself, and one UV. MEASURED on PropPreset_DiffuseOnly's vertex shader: o1 = part-matrix × position
    /// (NOT camera-relative), o2 = normalized world normal, o3.xy = UV.</summary>
    SimpleSurface,

    /// <summary>Two interpolators: world normal and UV — the lightest layout (third-person weapons,
    /// projectiles). MEASURED on WeaponPreset3P's vertex shader.</summary>
    NormalOnly,

    /// <summary>Six interpolators, skinned characters: a raw per-vertex stream (three components consumed —
    /// role INFERRED as vertex colour/occlusion, not differential-verified), absolute world position, the
    /// three tangent rows, the UV. MEASURED on CharacterRoot_1P: the pixel side decodes normals with the
    /// canonical dp3-against-rows sequence.</summary>
    Character,

    /// <summary>The probe-lit rigid variant: the FOUR light-probe SH rows ride ahead of the rigid five as
    /// full float4 interpolators, shifting every standard input up by four. (The signature census shows
    /// EVERY family has a probe twin at +4 rows.)</summary>
    RigidMeshProbe,

    /// <summary>The probe-lit rows-and-UV twin (vegetation and vehicle bodies): the four SH rows ahead,
    /// the base four shifted to TC4-7. MEASURED on the paired vertex shaders (2026-08-23): o1..o4 load
    /// instance-vector rows 0..3 (the SH coefficients; the per-part matrices start at row 4), then the
    /// component-collected frame rows and the UV exactly as the base family assembles them. The pixel
    /// side dp4s (N,1) against TC0/1/2 for R/G/B and TC3 for the occlusion row — the same order as the
    /// rigid probe. Vehicles keep their two UV sets packed in TC7.xyzw.</summary>
    VegetationProbe,

    /// <summary>The probe-lit simple-surface twin: SH rows at TC0-3, then world position, world normal
    /// and UV at TC4-6. MEASURED on the diffuse-only preset's probe solutions (2026-08-23).</summary>
    SimpleSurfaceProbe,

    /// <summary>The probe-lit normal-and-UV twin (third-person weapons, projectiles): SH rows at TC0-3,
    /// then world normal and UV at TC4-5. MEASURED (2026-08-23).</summary>
    NormalOnlyProbe,

    /// <summary>The probe-lit character twin — THE EXCEPTION to "+4 ahead": the raw per-vertex stream
    /// KEEPS TC0 and the four SH rows ride at TC1-4, with world position, the tangent rows and the UV
    /// shifted to TC5-9. MEASURED on CharacterRoot_1P sol0 (2026-08-23): o1 = the v3 stream passthrough,
    /// o2..o5 = instance-vector rows 0..3, and the pixel side dp4s against v2/v3/v4 (R/G/B) and v5 (O).</summary>
    CharacterProbe,

    /// <summary>
    /// The lightmapped rigid variant that a STATIC MODEL GROUP draws: the rigid five with the lightmap's own
    /// UV at the end AND a full float4 at TC0 carrying the 2x2 transform that maps that UV into this
    /// instance's region of the lightmap atlas.
    ///
    /// ⛔ The transform rides an INTERPOLATOR, not a constant — that is what makes it per-instance, and why
    /// the shader carries no `lightMapUvTransform` name for the 6-interpolator lightmap rule to match on.
    /// MEASURED on the barrier's sol1 (2026-09-05): `dp2 r0.x, v1.xyxx, v7.xyxx` / `dp2 r0.z, v1.zwzz,
    /// v7.xyxx` is the transform applied to TC6, then TexCoord.zw is added as the atlas offset.
    ///
    /// This is the flavour the copies already placed in a map render with, so it is the one that decides
    /// whether a replacement is visible on them at all — every other flavour belongs to objects spawned or
    /// placed by hand.
    /// </summary>
    RigidMeshLightmap,

    /// <summary>The forward transparent pass: a single render target, the outdoor-light constants compiled
    /// in by name, and the rigid five with a FULL first row — TC0.w carries the vertex-computed distance
    /// fade (from transparentStartAndEndAndClamp), which the pixel side multiplies into the premultiplied
    /// alpha. MEASURED on the glass preset's paired vertex shader (2026-08-21); its probe twin is the
    /// standard +4 layout and classifies as RigidMeshProbe, which is positionally exact for it too.</summary>
    Forward,
}

/// <summary>How a pixel variant receives the light-probe SH coefficients, when it does at all.</summary>
public enum ProbeTransport
{
    None,

    /// <summary>Four leading float4 interpolators (R, G, B rows + the occlusion row).</summary>
    Interpolators,

    /// <summary>A cb0 block named lightProbeShR/G/B/O, filled by the engine per draw.</summary>
    Cbuffer,
}

/// <summary>
/// What a target shader's pixel stage receives, DETECTED from its compiled signature rather than chosen from a
/// dropdown. Choosing would be dangerous: pick the wrong family and the graph reads a different interpolator than
/// it thinks, which produces garbage with no error anywhere.
///
/// ⚠ Two different confidence levels live here on purpose:
///   - the SHAPE (how many interpolators, their components, which are integer) is read from the bytecode and is
///     therefore fact;
///   - the MEANING of each interpolator is only verified for <see cref="ShaderFamily.RigidMesh"/>, where a
///     bit-exact differential test against the game's own shader confirmed worldpos / tangent rows / UV.
/// For every other family the meaning is NOT measured, so the contract exposes the interpolators RAW and says so.
/// Inventing a name for an unmeasured interpolator is how a port silently reads the wrong thing.
/// </summary>
public sealed class ShaderContract
{
    public ShaderFamily Family { get; init; }
    public List<SignatureElement> Inputs { get; init; } = new();
    public int RenderTargets { get; init; }

    /// <summary>Everything the shader binds, with real registers.</summary>
    public List<ResourceBinding> Resources { get; init; } = new();

    /// <summary>
    /// Every named constant-buffer field the shader declares, with the cbN[element] it lives in. These are the
    /// artist-facing parameters - `external_SpecularScale`, `external_FresnelBias` - under DICE's own names.
    /// </summary>
    public List<DxbcSignature.ConstantField> ConstantFields { get; init; } = new();

    /// <summary>
    /// The parameter a `cbN[M].component` operand refers to, or null when nothing declares that byte.
    ///
    /// ⛔ The COMPONENT is part of the question. Several fields share a register - viewConstants packs `time` at
    /// c0.x with its neighbours in the same slot - so matching on the register alone returns whichever field the
    /// list happens to hold first, and the graph then reads a different constant than the shader did. Matching
    /// on the byte range is the only reading that is unambiguous, and it handles multi-register fields for free.
    /// </summary>
    public DxbcSignature.ConstantField? FieldAt(int p_Register, int p_Element, int p_Component = 0)
    {
        var s_Byte = p_Element * 16 + Math.Clamp(p_Component, 0, 3) * 4;
        return ConstantFields.FirstOrDefault(p_F => p_F.Register == p_Register &&
                                                    s_Byte >= p_F.Offset &&
                                                    s_Byte < p_F.Offset + Math.Max(4, p_F.Size));
    }

    /// <summary>
    /// The (register, element) a named external parameter occupies, or (-1,-1). The material data names its
    /// parameters WITHOUT the external_ prefix the compiler adds - the namespace bridge is measured (a typo,
    /// external_DiffuseBrighness, appears identically on both sides).
    /// </summary>
    public (int Register, int Element) ExternalFieldOf(string p_Name)
    {
        var s_Field = ConstantFields.FirstOrDefault(p_F =>
            p_F.Name.Equals("external_" + p_Name, StringComparison.OrdinalIgnoreCase) ||
            p_F.Name.Equals(p_Name, StringComparison.OrdinalIgnoreCase));

        return s_Field == null ? (-1, -1) : (s_Field.Register, s_Field.Offset / 16);
    }

    /// <summary>
    /// The shader's own material textures, ordered by the slot their NAME encodes — which is the order the
    /// shaderdb's StreamableTextures list uses. Their registers are whatever the bytecode says, NOT 1..n:
    /// on a lightmapped shader the engine holds t1..t4 and the material starts at t5.
    /// </summary>
    public List<ResourceBinding> MaterialTextures => Resources
        .Where(p_R => p_R.IsMaterialTexture)
        .OrderBy(p_R => p_R.MaterialSlot == 0 ? p_R.Register : p_R.MaterialSlot)
        .ToList();

    /// <summary>
    /// Where the texture that sits at <paramref name="p_Register"/> in <paramref name="p_Reference"/> lives in
    /// THIS contract, or -1 when this contract does not carry it.
    ///
    /// ⛔ A GRAPH'S REGISTERS BELONG TO THE FLAVOUR IT WAS TRANSLATED FROM. Emitting it against a different
    /// flavour without this is how a variant ends up sampling the engine's own textures: a lightmapped
    /// flavour keeps the engine in t1..t4 and its material in t5..t9, so the albedo the graph reads from t1
    /// is the lightmap's irradiance there. The two are matched by MATERIAL SLOT — the number the NAME
    /// encodes, which is an identity — never by register, which is exactly the thing that moves.
    /// </summary>
    public int RemapRegisterFrom(ShaderContract p_Reference, int p_Register)
    {
        var s_Source = p_Reference.Resources.FirstOrDefault(
            p_R => p_R.IsMaterialTexture && p_R.Register == p_Register);

        if (s_Source == null)
            return -1;

        // Named slots carry their identity; descriptively named ones (texture_Diffuse) have none, so they
        // fall back to position within the material list, which is the same order on both sides.
        if (s_Source.MaterialSlot != 0)
            return MaterialTextures.FirstOrDefault(p_R => p_R.MaterialSlot == s_Source.MaterialSlot)
                ?.Register ?? -1;

        var s_Index = p_Reference.MaterialTextures.FindIndex(p_R => p_R.Register == p_Register);
        var s_Mine = MaterialTextures;
        return s_Index >= 0 && s_Index < s_Mine.Count ? s_Mine[s_Index].Register : -1;
    }

    /// <summary>
    /// The sampler the MATERIAL's textures are read through. Zero normally — but a flavour that carries
    /// engine textures gives them s0 and starts the material at s1.
    ///
    /// ⛔ THIS IS NOT COSMETIC. Sampler state is what decides addressing and filtering, and the lightmap's
    /// sampler does not repeat: reading a tiling detail texture through it stretches the first texel across
    /// the surface, which shows up as broad diagonal BANDS, not as a missing texture. Measured on the
    /// barrier's own bytecode — the lightmapped flavour samples t1..t4 through s0 and its material through
    /// s1/s2/s3, while the plain flavour starts at s0.
    /// </summary>
    public int MaterialSamplerIndex => EngineTextures.Count > 0 ? 1 : 0;

    public string MaterialSampler => "sampler" + MaterialSamplerIndex;

    /// <summary>Which sampler each texture register is read through, from the shader's own instructions.</summary>
    public Dictionary<int, int> SamplerPairs { get; init; } = new();

    /// <summary>
    /// The sampler THIS flavour reads the texture at <paramref name="p_Register"/> through, falling back to
    /// the material's base when the body says nothing about it.
    ///
    /// ⛔ Per texture, not one for all of them: a flavour spreads its material across several samplers with
    /// different addressing, and putting every fetch through one of them leaves SOME textures tiling and the
    /// rest stretched — which reads as "the texture only shows up on part of the surface".
    /// </summary>
    public string SamplerFor(int p_Register) =>
        "sampler" + (SamplerPairs.TryGetValue(p_Register, out var s_Sampler) ? s_Sampler : MaterialSamplerIndex);

    /// <summary>Engine-bound textures (lightmaps, heightfield, virtual texture...), which must not be touched.</summary>
    public List<ResourceBinding> EngineTextures => Resources
        .Where(p_R => p_R.IsTexture && !p_R.IsMaterialTexture)
        .OrderBy(p_R => p_R.Register)
        .ToList();

    /// <summary>
    /// The register whose RDEF resource is named texture_&lt;param&gt;, or -1. The compiler names an external
    /// texture slot after its parameter (texture_Diffuse, texture_CamoTile…), which pins that binding with
    /// zero inference — no usage-class pairing can compete with the register being named in the bytecode.
    /// </summary>
    public int RegisterForExternalTexture(string p_Name)
    {
        var s_Match = Resources.FirstOrDefault(p_R => p_R.IsTexture &&
            p_R.Name.Equals("texture_" + p_Name, StringComparison.OrdinalIgnoreCase));
        return s_Match?.Register ?? -1;
    }

    /// <summary>
    /// The register the Nth streamable texture actually lands in. Returns 0 when unknown, so a caller can say
    /// "unknown" instead of falling back to the declaration-order guess that is wrong for lightmapped shaders.
    /// </summary>
    public int RegisterForStreamable(int p_Index)
    {
        var s_Textures = MaterialTextures;
        return p_Index >= 0 && p_Index < s_Textures.Count ? s_Textures[p_Index].Register : 0;
    }

    /// <summary>
    /// Material registers the shader's own instructions decode as DXT5nm NORMAL MAPS (resource swizzle xywz).
    /// Filled by the translation step, which has the disassembly in hand; empty when nothing analysed it.
    /// </summary>
    public List<int> NormalTextureRegisters { get; set; } = new();

    /// <summary>
    /// What each interpolator IS, keyed by TEXCOORD index, proven by how the shader uses it: a sample
    /// coordinate is a UV, the vector subtracted from cameraPos is the world position, the vector packed into
    /// RT0 is the world normal. Only filled for contracts whose meanings are NOT measured - the preview's
    /// vertex shader is generated from this so an "Unknown" family still previews with meaningful inputs
    /// instead of the rigid-mesh guesses.
    /// </summary>
    public Dictionary<int, string> InterpolatorMeanings { get; set; } = new();

    /// <summary>Whether (and how) this pixel variant receives the light-probe SH coefficients.</summary>
    public ProbeTransport Probes { get; set; } = ProbeTransport.None;

    /// <summary>
    /// The literal UV multiplier each material register's sample coordinate carries (1 = sampled at the raw
    /// interpolator). Absent = the scale is parameter-driven or computed, so it says nothing.
    ///
    /// This is the shaderdb `Factor` cross-check turned into a pairing aid: DICE bakes `uv * (1/Factor)` into
    /// the bytecode, so a streamable with factor 0.2 belongs in a slot whose coordinate is scaled by 5 - two
    /// independent sources agreeing, measured first on the concrete barrier.
    /// </summary>
    public Dictionary<int, float> TextureUvScales { get; set; } = new();

    /// <summary>Fills <see cref="TextureUvScales"/> from the sample coordinates' producers.</summary>
    public void ClassifyTextureScales(List<Translate.AsmInstruction> p_Instructions)
    {
        TextureUvScales.Clear();

        for (var i = 0; i < p_Instructions.Count; i++)
        {
            var s_Sample = p_Instructions[i];
            if (!s_Sample.Opcode.StartsWith("sample", StringComparison.Ordinal) ||
                s_Sample.Sources.FirstOrDefault(p_S => p_S.Kind == Translate.OperandKind.Resource)
                    is not { } s_Resource ||
                s_Sample.Sources.ElementAtOrDefault(0) is not { } s_Coord)
                continue;

            if (TextureUvScales.ContainsKey(s_Resource.Index))
                continue;

            if (s_Coord.Kind == Translate.OperandKind.Input)
            {
                TextureUvScales[s_Resource.Index] = 1f;
                continue;
            }

            if (s_Coord.Kind != Translate.OperandKind.Temp)
                continue;

            // The last write to the coordinate's first component before the sample - a literal multiply of an
            // interpolator is the baked tiling; anything else (cbuffer transforms, computed UVs) is unknown.
            var s_Component = s_Coord.Swizzle.Length > 0 ? s_Coord.Swizzle[0] : 0;
            for (var j = i - 1; j >= 0; j--)
            {
                var s_Producer = p_Instructions[j];
                if (s_Producer.Destination is not { Kind: Translate.OperandKind.Temp } s_Destination ||
                    s_Destination.Index != s_Coord.Index)
                    continue;

                var s_Mask = s_Destination.WriteMask.Length > 0 ? s_Destination.WriteMask : new[] { 0 };
                if (!s_Mask.Contains(s_Component))
                    continue;

                if (s_Producer.Opcode == "mul" &&
                    s_Producer.Sources.Any(p_S => p_S.Kind == Translate.OperandKind.Input) &&
                    s_Producer.Sources.FirstOrDefault(p_S => p_S.Kind == Translate.OperandKind.Literal)
                        is { Literal.Length: > 0 } s_Scale)
                    TextureUvScales[s_Resource.Index] = s_Scale.Literal[0];

                break;
            }
        }
    }

    /// <summary>Fills <see cref="InterpolatorMeanings"/> from the shader's own instruction stream.</summary>
    public void ClassifyInterpolators(List<Translate.AsmInstruction> p_Instructions)
    {
        InterpolatorMeanings.Clear();

        // The measured families keep the measured layout - reclassifying them could only move the guardians.
        if (SemanticsVerified)
            return;

        // A probe twin's meanings come from its MEASURED table, not the usage scan: SH rows never appear
        // as sample coordinates or output packs, so the scan would hand them the neutral filler — and a
        // dp4 against near-constant filler collapses under the clamp, deadening the probe term on BOTH
        // sides of a differential test (the exact trap the forward probe diff fell into).
        if (IsProbeTwin(Family))
        {
            foreach (var s_Element in Inputs.Where(p_I =>
                         p_I.Semantic.Equals("TEXCOORD", StringComparison.OrdinalIgnoreCase)))
                InterpolatorMeanings[s_Element.Index] = FieldNameFor(s_Element.Index) switch
                {
                    "WorldPos" => "WorldPos",
                    "WorldNormal" => "Normal",
                    "TexCoord" => "UvPair",
                    "VertexColor" => "Neutral",
                    var s_Field => s_Field,
                };

            return;
        }

        var s_ByRegister = new Dictionary<int, string>();

        foreach (var s_Instruction in p_Instructions)
        {
            // A sample's first operand is its coordinate: whatever interpolator it reads is a UV.
            if (s_Instruction.Opcode.StartsWith("sample", StringComparison.Ordinal) &&
                s_Instruction.Sources.ElementAtOrDefault(0) is { Kind: Translate.OperandKind.Input } s_Coord)
                s_ByRegister.TryAdd(s_Coord.Index, "UvPair");

            // The camera-relative subtract names the world position (cameraPos is cb2[20], measured).
            if (s_Instruction.Opcode == "add" &&
                s_Instruction.Sources.Any(p_S => p_S is { Kind: Translate.OperandKind.ConstBuffer, Index: 2, Element: 20 }) &&
                s_Instruction.Sources.FirstOrDefault(p_S => p_S is { Kind: Translate.OperandKind.Input, Negate: true }) is { } s_Pos)
                s_ByRegister[s_Pos.Index] = "WorldPos";

            // The *0.5+0.5 pack into RT0 names the world normal.
            if (s_Instruction.Opcode == "mad" &&
                s_Instruction.Destination is { Kind: Translate.OperandKind.Output, Index: 0 } &&
                s_Instruction.Sources.ElementAtOrDefault(0) is { Kind: Translate.OperandKind.Input } s_Normal)
                s_ByRegister[s_Normal.Index] = "Normal";
        }

        foreach (var (s_Register, s_Meaning) in s_ByRegister)
        {
            var s_Element = Inputs.FirstOrDefault(p_I => p_I.Register == s_Register &&
                p_I.Semantic.Equals("TEXCOORD", StringComparison.OrdinalIgnoreCase));

            if (s_Element != null)
                InterpolatorMeanings[s_Element.Index] = s_Meaning;
        }

        // Whatever the usage scan could NOT name gets NEUTRAL WHITE rather than the rigid-mesh guesses: a
        // variant gate compared against 0 reads true, a particle fade reads fully visible, a vertex colour
        // multiplies without darkening. A tangent-frame row in any of those roles is just structured noise.
        foreach (var s_Element in Inputs.Where(p_I =>
                     p_I.Semantic.Equals("TEXCOORD", StringComparison.OrdinalIgnoreCase)))
            InterpolatorMeanings.TryAdd(s_Element.Index, "Neutral");
    }

    /// <summary>
    /// Pairs a shader's streamable-texture list to material registers.
    ///
    /// ⛔⛔ NEITHER of the two obvious rules survives measurement. "texture_TextureN is the Nth streamable"
    /// (the Barrack's rule) mispairs 89 of 169 discriminating shaders against their own disassembly - the
    /// concrete barrier ends with a normal map in a colour slot, which renders as a violet object. Plain
    /// register order still mispairs 24. What holds is pairing BY USAGE CLASS: *_N streamables, in list order,
    /// onto the registers the shader decodes as normal maps, in register order; everything else onto the
    /// colour registers the same way. The barrier's own metadata confirms it from the other side: the
    /// streamables with factor 0.2 land exactly on the two slots the shader tiles by 5.
    ///
    /// Without usage information (no translation ran) it falls back to register order - the less-wrong rule.
    /// </summary>
    public Dictionary<int, string> PairStreamables(IEnumerable<(int Index, string Name)> p_Streamables) =>
        PairStreamables(p_Streamables.Select(p_S => (p_S.Index, p_S.Name, 0f)));

    public Dictionary<int, string> PairStreamables(IEnumerable<(int Index, string Name, float Factor)> p_Streamables,
        IEnumerable<int>? p_ExcludedRegisters = null)
    {
        var s_Excluded = p_ExcludedRegisters?.ToHashSet() ?? new HashSet<int>();
        var s_Registers = MaterialTextures.Select(p_T => p_T.Register)
            .Where(p_R => !s_Excluded.Contains(p_R))
            .OrderBy(p_R => p_R).ToList();
        var s_Ordered = p_Streamables.OrderBy(p_S => p_S.Index).ToList();
        var s_Result = new Dictionary<int, string>();

        var s_NormalRegisters = s_Registers.Where(p_R => NormalTextureRegisters.Contains(p_R)).ToList();
        if (s_NormalRegisters.Count == 0)
        {
            for (var i = 0; i < s_Ordered.Count && i < s_Registers.Count; i++)
                s_Result[s_Registers[i]] = s_Ordered[i].Name;

            return s_Result;
        }

        static bool IsNormalName(string p_Name)
        {
            var s_Base = p_Name.Split('/')[^1].ToLowerInvariant();
            return s_Base.EndsWith("_n") || s_Base.EndsWith("_nm") || s_Base.EndsWith("normal");
        }

        var s_ColourRegisters = s_Registers.Where(p_R => !NormalTextureRegisters.Contains(p_R)).ToList();
        AssignClass(s_Ordered.Where(p_S => IsNormalName(p_S.Name)).ToList(), s_NormalRegisters, s_Result);
        AssignClass(s_Ordered.Where(p_S => !IsNormalName(p_S.Name)).ToList(), s_ColourRegisters, s_Result);
        return s_Result;
    }

    /// <summary>
    /// Streamables onto registers within one usage class: FACTOR-confident matches first, list order for the
    /// rest. DICE bakes `uv * (1/Factor)` into the bytecode, so `scale * factor ≈ 1` pins a streamable to its
    /// slot with two independent sources - which is what disambiguates the shaders whose streamable list is a
    /// superset of what the chosen permutation samples (damage variants carry the extra entries).
    /// </summary>
    private void AssignClass(List<(int Index, string Name, float Factor)> p_Streams, List<int> p_Registers,
        Dictionary<int, string> p_Result)
    {
        var s_FreeRegisters = new List<int>(p_Registers);
        var s_Remaining = new List<(int Index, string Name, float Factor)>();

        foreach (var s_Stream in p_Streams)
        {
            var s_Match = s_Stream.Factor > 0
                ? s_FreeRegisters.Cast<int?>().FirstOrDefault(p_R =>
                    TextureUvScales.TryGetValue(p_R!.Value, out var s_Scale) &&
                    Math.Abs(s_Scale * s_Stream.Factor - 1f) <= 0.25f)
                : null;

            if (s_Match is { } s_Register)
            {
                p_Result[s_Register] = s_Stream.Name;
                s_FreeRegisters.Remove(s_Register);
            }
            else
            {
                s_Remaining.Add(s_Stream);
            }
        }

        foreach (var s_Stream in s_Remaining)
        {
            if (s_FreeRegisters.Count == 0)
                break;

            p_Result[s_FreeRegisters[0]] = s_Stream.Name;
            s_FreeRegisters.RemoveAt(0);
        }
    }

    /// <summary>True when the per-interpolator meaning is verified, not just the shape.</summary>
    public bool SemanticsVerified => Family == ShaderFamily.RigidMesh ||
                                     Family == ShaderFamily.RigidMeshSubMaterial ||
                                     Family == ShaderFamily.RigidMeshLightmap;

    /// <summary>
    /// True for the flavour a static model group's copies draw with: it owes the GBuffer a lightmap term
    /// that the graph itself has no way to produce, so an emitted variant must synthesize it or the object
    /// ships with the map's precomputed light missing from the channels the engine reads.
    /// </summary>
    public bool NeedsLightmapTerm => Family == ShaderFamily.RigidMeshLightmap;

    /// <summary>The families that receive the light-probe SH rows as interpolators.</summary>
    public static bool IsProbeTwin(ShaderFamily p_Family) => p_Family is
        ShaderFamily.RigidMeshProbe or ShaderFamily.VegetationProbe or
        ShaderFamily.SimpleSurfaceProbe or ShaderFamily.NormalOnlyProbe or
        ShaderFamily.CharacterProbe;

    /// <summary>The flat-interpolated integer that selects a submaterial, if this shader has one.</summary>
    public SignatureElement? SubMaterialSelector =>
        Inputs.FirstOrDefault(p_I => p_I.IsInteger && p_I.Semantic.Equals("TEXCOORD", StringComparison.OrdinalIgnoreCase));

    public IEnumerable<SignatureElement> Interpolators => Inputs
        .Where(p_I => p_I.Semantic.Equals("TEXCOORD", StringComparison.OrdinalIgnoreCase))
        .OrderBy(p_I => p_I.Index);

    /// <summary>
    /// The rigid-mesh contract the editor has always assumed, as an explicit object. Used when no target has been
    /// analysed, so the default path stays byte-for-byte what it was — the barrel graph is verified bit-exact
    /// against the game's own shader and must not move.
    /// </summary>
    public static ShaderContract RigidMeshDefault() => new()
    {
        Family = ShaderFamily.RigidMesh,
        RenderTargets = 4,
        Inputs = Enumerable.Range(0, 5).Select(p_I => new SignatureElement
        {
            Semantic = "TEXCOORD", Index = p_I, Register = p_I + 1,
            Mask = 0xF, UsedMask = p_I == 4 ? (byte) 0x3 : (byte) 0x7,
        }).ToList(),
    };

    /// <summary>
    /// The HLSL field name for an interpolator. The rigid-mesh layout keeps DICE's meanings because they are
    /// MEASURED (world position, the three tangent-basis rows, the UV); every other family gets a neutral
    /// InterpN, because naming an interpolator whose meaning has not been measured is how a graph silently reads
    /// the wrong thing.
    /// </summary>
    public string FieldNameFor(int p_TexCoordIndex)
    {
        // Forward shares the rigid table: same five positions, only TC0's .w means something extra (fade).
        if (Family is ShaderFamily.RigidMesh or ShaderFamily.RigidMeshSubMaterial or ShaderFamily.Forward)
            switch (p_TexCoordIndex)
            {
                case 0: return "WorldPos";
                case 1: return "TangentRow0";
                case 2: return "TangentRow1";
                case 3: return "TangentRow2";
                case 4: return "TexCoord";
            }

        // The lightmapped per-instance variant is the rigid layout shifted by ONE: the atlas transform
        // rides ahead of it, and the lightmap's own UV comes after. TexCoord keeps two UV sets in xyzw —
        // .zw is the atlas offset added to the transformed UV.
        if (Family == ShaderFamily.RigidMeshLightmap)
            switch (p_TexCoordIndex)
            {
                case 0: return "LightMapUvTransform";
                case 1: return "WorldPos";
                case 2: return "TangentRow0";
                case 3: return "TangentRow1";
                case 4: return "TangentRow2";
                case 5: return "TexCoord";
                case 6: return "LightMapUv";
            }

        // The probe variant is the rigid layout shifted by the four SH rows riding ahead of it.
        if (Family == ShaderFamily.RigidMeshProbe)
            switch (p_TexCoordIndex)
            {
                case 0: return "ProbeShR";
                case 1: return "ProbeShG";
                case 2: return "ProbeShB";
                case 3: return "ProbeShO";
                case 4: return "WorldPos";
                case 5: return "TangentRow0";
                case 6: return "TangentRow1";
                case 7: return "TangentRow2";
                case 8: return "TexCoord";
            }

        // The layouts measured from the preset VERTEX shaders (2026-08-20): the paired *_vs.dxbc names
        // nothing, but its output assembly does — position × part/bone matrices, cross-product tangent
        // frames collected component-wise into rows, UVs passed through. The pixel side confirms each with
        // the canonical dp3-against-rows decode.
        if (Family == ShaderFamily.Vegetation)
            switch (p_TexCoordIndex)
            {
                case 0: return "TangentRow0";
                case 1: return "TangentRow1";
                case 2: return "TangentRow2";
                case 3: return "TexCoord";
            }

        if (Family == ShaderFamily.SimpleSurface)
            switch (p_TexCoordIndex)
            {
                case 0: return "WorldPos";
                case 1: return "WorldNormal";
                case 2: return "TexCoord";
            }

        if (Family == ShaderFamily.NormalOnly)
            switch (p_TexCoordIndex)
            {
                case 0: return "WorldNormal";
                case 1: return "TexCoord";
            }

        if (Family == ShaderFamily.Character)
            switch (p_TexCoordIndex)
            {
                case 0: return "VertexColor";
                case 1: return "WorldPos";
                case 2: return "TangentRow0";
                case 3: return "TangentRow1";
                case 4: return "TangentRow2";
                case 5: return "TexCoord";
            }

        // The probe twins of the measured families: the four SH rows ride ahead (Character: after its
        // vertex stream) and the base layout follows, shifted by four. Measured on the paired vertex
        // shaders — the SH rows are instance-vector rows 0..3, consumed R/G/B/O in that order.
        if (Family == ShaderFamily.VegetationProbe)
            switch (p_TexCoordIndex)
            {
                case 0: return "ProbeShR";
                case 1: return "ProbeShG";
                case 2: return "ProbeShB";
                case 3: return "ProbeShO";
                case 4: return "TangentRow0";
                case 5: return "TangentRow1";
                case 6: return "TangentRow2";
                case 7: return "TexCoord";
            }

        if (Family == ShaderFamily.SimpleSurfaceProbe)
            switch (p_TexCoordIndex)
            {
                case 0: return "ProbeShR";
                case 1: return "ProbeShG";
                case 2: return "ProbeShB";
                case 3: return "ProbeShO";
                case 4: return "WorldPos";
                case 5: return "WorldNormal";
                case 6: return "TexCoord";
            }

        if (Family == ShaderFamily.NormalOnlyProbe)
            switch (p_TexCoordIndex)
            {
                case 0: return "ProbeShR";
                case 1: return "ProbeShG";
                case 2: return "ProbeShB";
                case 3: return "ProbeShO";
                case 4: return "WorldNormal";
                case 5: return "TexCoord";
            }

        if (Family == ShaderFamily.CharacterProbe)
            switch (p_TexCoordIndex)
            {
                case 0: return "VertexColor";
                case 1: return "ProbeShR";
                case 2: return "ProbeShG";
                case 3: return "ProbeShB";
                case 4: return "ProbeShO";
                case 5: return "WorldPos";
                case 6: return "TangentRow0";
                case 7: return "TangentRow1";
                case 8: return "TangentRow2";
                case 9: return "TexCoord";
            }

        return "Interp" + p_TexCoordIndex;
    }

    /// <summary>
    /// Field for a GRAPH-stored interpolator index. Graphs are translated against the BASE layout; the
    /// probe variant carries the same data four slots later (the SH rows ride ahead), so an index a node
    /// saved under the base numbering must shift before it names a field. Contract-derived indices (the
    /// shell, the SH rows, the submaterial selector) already speak this contract's numbering and use
    /// FieldNameFor directly. Measured on the sign: its translated UV node (base TC4) emitted i.WorldPos
    /// into the probe variant, so the instanced batch drew with garbage UVs while singles looked fine.
    /// </summary>
    /// <summary>
    /// Set by the emitter when the graph being emitted was translated against THIS family, so its saved
    /// indices already speak this contract's numbering and the probe-twin shift must not apply.
    /// </summary>
    public bool GraphSpeaksThisContract { get; set; }

    public string FieldNameForGraphIndex(int p_Index) => GraphSpeaksThisContract ? FieldNameFor(p_Index) : Family switch
    {
        ShaderFamily.RigidMeshProbe or ShaderFamily.VegetationProbe or
        ShaderFamily.SimpleSurfaceProbe or ShaderFamily.NormalOnlyProbe => FieldNameFor(p_Index + 4),

        // The character twin keeps its vertex stream at TC0 and slots the SH rows behind it, so only
        // the indices past the stream shift (measured layout, not the generic "+4 ahead").
        ShaderFamily.CharacterProbe => FieldNameFor(p_Index == 0 ? 0 : p_Index + 4),

        // The lightmapped flavour puts ONE row ahead of the rigid five (the per-instance atlas transform),
        // so every stored index is one short. Without this a graph translated from the plain flavour reads
        // its UV out of TangentRow2 and its world position out of the transform — code that compiles, binds
        // and renders garbage, which is exactly how this was found (emitted, then read).
        ShaderFamily.RigidMeshLightmap => FieldNameFor(p_Index + 1),

        _ => FieldNameFor(p_Index),
    };

    public string Describe()
    {
        // The USED mask, not the declared one: interpolators are always declared xyzw, so only "used" carries
        // information about the layout.
        var s_Shape = string.Join(" ", Interpolators.Select(p_I => $"TC{p_I.Index}.{p_I.UsedMaskText.TrimEnd('_')}" +
                                                                  (p_I.IsInteger ? "(int)" : "")));

        return $"{Family}{(Pass.Length > 0 ? " " + Pass : "")} - {RenderTargets} RT(s), " +
               $"{Interpolators.Count()} interpolator(s): {s_Shape}" +
               (SemanticsVerified ? "" : "  [shape measured, MEANING not verified]");
    }

    /// <summary>Which terrain path, when <see cref="Family"/> is Terrain. Empty otherwise.</summary>
    public string Pass { get; init; } = "";

    public static ShaderContract Detect(byte[] p_PixelDxbc)
    {
        var s_Inputs = DxbcSignature.ReadInput(p_PixelDxbc);
        var s_Outputs = DxbcSignature.ReadOutput(p_PixelDxbc);
        var s_Targets = s_Outputs.Count(p_O => p_O.Semantic.StartsWith("SV_Target", StringComparison.OrdinalIgnoreCase));

        var s_Tc = s_Inputs
            .Where(p_I => p_I.Semantic.Equals("TEXCOORD", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p_I => p_I.Index)
            .ToList();

        var s_Pass = "";
        var s_Family = Classify(p_PixelDxbc, s_Tc, ref s_Pass);

        // The probe term has two transports; both are facts of the bytecode. The cbuffer one names its
        // block outright, the interpolator one IS the probe family's layout.
        var s_Probes = Has(p_PixelDxbc, "lightProbeShR") ? ProbeTransport.Cbuffer
            : IsProbeTwin(s_Family) ? ProbeTransport.Interpolators
            : ProbeTransport.None;

        return new ShaderContract
        {
            Family = s_Family, Inputs = s_Inputs, RenderTargets = s_Targets, Pass = s_Pass,
            Probes = s_Probes,
            Resources = DxbcSignature.ReadResources(p_PixelDxbc),
            ConstantFields = DxbcSignature.ReadConstantFields(p_PixelDxbc),
            SamplerPairs = DxbcSignature.ReadSamplerPairs(p_PixelDxbc),
        };
    }

    /// <summary>
    /// Classification is driven FIRST by the resource and cbuffer names DICE compiled into the bytecode, and only
    /// then by the interpolator shape. The names are facts ("texture_heightfieldAtlas" is not a guess), while
    /// counting interpolators is a heuristic that already misread the 3d terrain path - it takes three, not one.
    /// </summary>
    private static ShaderFamily Classify(byte[] p_Dxbc, List<SignatureElement> p_Tc, ref string p_Pass)
    {
        if (p_Tc.Count == 0)
            return ShaderFamily.Unknown;

        // Terrain, by its own named inputs. The three paths are distinguishable the same way.
        if (Has(p_Dxbc, "virtualTextureIndirection"))
        {
            p_Pass = "3d (virtual texture + masks + heightfield + Enlighten; standard GBuffer packing)";
            return ShaderFamily.Terrain;
        }

        if (Has(p_Dxbc, "heightfieldAtlas"))
        {
            p_Pass = "2d (normal from heightfield central differences; OWN GBuffer packing)";
            return ShaderFamily.Terrain;
        }

        if (Has(p_Dxbc, "dynamicMaskUvScale") && Has(p_Dxbc, "clipToWorldSpaceXzTransform"))
        {
            p_Pass = "MaskScale2d (layer-weight pass; writes a scalar and zeroes the rest)";
            return ShaderFamily.Terrain;
        }

        // A flat integer alongside the rigid-mesh five is the submaterial index: it selects into the
        // external_*[8] arrays, and MaxSubMaterialCount is 8.
        if (p_Tc.Any(p_I => p_I.IsInteger))
            return ShaderFamily.RigidMeshSubMaterial;

        // A lightmapped rigid mesh keeps the five and adds a sixth interpolator for the lightmap UV (its
        // transform lives in $Globals as lightMapUvTransform/Translation). Its TEXCOORD4 also carries TWO UV
        // sets packed as xyzw, which is why "this contract has a single UV" was wrong.
        if (p_Tc.Count == 6 && Has(p_Dxbc, "lightMapUvTransform"))
        {
            p_Pass = "lightmapped (TC5 = lightmap UV; TC4.xyzw = two UV sets)";
            return ShaderFamily.RigidMesh;
        }

        // The same lightmapped layout ONE SLOT UP, with a full float4 at TC0: that row is the 2x2 transform
        // into this instance's atlas region, so the shader needs no lightMapUvTransform constant and the
        // rule above cannot see it. Matching on the lightmap TEXTURES instead is what identifies it — they
        // are bound whether the transform is a constant or an interpolator.
        if (p_Tc.Count == 7 && p_Tc[0].UsedComponents == 4 && Has(p_Dxbc, "lightMapIrradianceLuma"))
        {
            p_Pass = "lightmapped, per-instance (TC0 = atlas transform; rigid five at TC1-5; TC6 = lightmap UV)";
            return ShaderFamily.RigidMeshLightmap;
        }

        // Shape, on the USED components - interpolators are always declared xyzw.
        if (p_Tc.Count == 6 && p_Tc[0].UsedComponents == 1)
            return ShaderFamily.Skinned;

        // The probe-lit rigid variant: four full float4 rows (the SH coefficients) ahead of the rigid
        // five, everything else shifted up by four. Measured on the reference exemplar: masks are
        // xyzw,xyzw,xyzw,xyzw then xyz,xyz,xyz,xyz,xy.
        if (p_Tc.Count == 9 && p_Tc.Take(4).All(p_I => p_I.UsedComponents == 4))
        {
            p_Pass = "probe-lit (TC0-3 = light-probe SH rows; rigid five shifted to TC4-8)";
            return ShaderFamily.RigidMeshProbe;
        }

        // The probe twins of the smaller families follow the same shape: four full float4 SH rows ahead
        // of the base layout. The counts cannot collide with each other (base 4/3/2 + 4 = 8/7/6), and the
        // full first row separates them from every 6-TC base layout above — the skinned first row uses one
        // component, the character one three. Measured per family on the preset extracts (2026-08-23).
        if (p_Tc.Count == 8 && p_Tc.Take(4).All(p_I => p_I.UsedComponents == 4))
        {
            p_Pass = "probe-lit (TC0-3 = light-probe SH rows; rows-and-UV shifted to TC4-7)";
            return ShaderFamily.VegetationProbe;
        }

        if (p_Tc.Count == 7 && p_Tc.Take(4).All(p_I => p_I.UsedComponents == 4))
        {
            p_Pass = "probe-lit (TC0-3 = light-probe SH rows; worldpos/normal/UV shifted to TC4-6)";
            return ShaderFamily.SimpleSurfaceProbe;
        }

        if (p_Tc.Count == 6 && p_Tc.Take(4).All(p_I => p_I.UsedComponents == 4))
        {
            p_Pass = "probe-lit (TC0-3 = light-probe SH rows; normal/UV shifted to TC4-5)";
            return ShaderFamily.NormalOnlyProbe;
        }

        // The vegetation-alpha instanced twin: the SH rows are DECLARED but never read (its alpha-coverage
        // pass takes no light at all), so the first four rows show zero used components while the shifted
        // normal and UV keep the base shapes. Same layout as the probe twin, dead rows included — measured
        // on the alpha preset's 4-tap coverage solutions (2026-08-23).
        if (p_Tc.Count == 6 && p_Tc.Take(4).All(p_I => p_I.UsedComponents == 0) &&
            p_Tc[4].UsedComponents == 3 && p_Tc[5].UsedComponents == 2)
        {
            p_Pass = "probe-shaped (TC0-3 declared but unread; normal/UV shifted to TC4-5)";
            return ShaderFamily.NormalOnlyProbe;
        }

        // The character twin keeps its raw vertex stream (three components) at TC0 and rides the SH rows
        // at TC1-4 — the measured exception to "+4 ahead". A one-component first row at this count is the
        // destruction-volume twin (a Skinned layout), which stays unclassified on purpose: Skinned has no
        // measured table, and an unnamed probe layout must keep vanilla bytes rather than read blind.
        if (p_Tc.Count == 10 && p_Tc[0].UsedComponents == 3 &&
            p_Tc.Skip(1).Take(4).All(p_I => p_I.UsedComponents == 4))
        {
            p_Pass = "probe-lit (TC1-4 = light-probe SH rows behind the vertex stream; base shifted to TC5-9)";
            return ShaderFamily.CharacterProbe;
        }

        // Six non-integer TCs with a THREE-component first row is the skinned-character layout (raw vertex
        // stream, world position, three tangent rows, UV) — distinct from Skinned above, whose first TC
        // uses a single component.
        if (p_Tc.Count == 6 && p_Tc[0].UsedComponents == 3)
            return ShaderFamily.Character;

        // The forward transparent pass keeps the rigid five but uses ALL of TC0 (the .w is the distance
        // fade) and names its light source outright — the outdoor-light constants only ride the forward
        // solutions. Both facts together, so a rigid variant that happened to touch TC0.w could never
        // land here by shape alone.
        if (p_Tc.Count == 5 && p_Tc[0].UsedComponents == 4 && Has(p_Dxbc, "outdoorLightHemisphereDir"))
        {
            p_Pass = "forward (single target; TC0.w = distance fade into premultiplied alpha)";
            return ShaderFamily.Forward;
        }

        if (p_Tc.Count == 5)
            return ShaderFamily.RigidMesh;

        // Four TCs = the rows-and-UV layout that vegetation AND vehicle bodies share (measured; the family
        // name is historical).
        if (p_Tc.Count == 4)
            return ShaderFamily.Vegetation;

        if (p_Tc.Count == 3)
            return ShaderFamily.SimpleSurface;

        if (p_Tc.Count == 2)
            return ShaderFamily.NormalOnly;

        return ShaderFamily.Unknown;
    }

    /// <summary>DICE's resource and cbuffer names survive as ASCII in the bytecode, so a byte search is enough.</summary>
    private static bool Has(byte[] p_Dxbc, string p_Marker)
    {
        var s_Needle = System.Text.Encoding.ASCII.GetBytes(p_Marker);
        for (var i = 0; i <= p_Dxbc.Length - s_Needle.Length; i++)
        {
            var s_Match = true;
            for (var j = 0; j < s_Needle.Length; j++)
                if (p_Dxbc[i + j] != s_Needle[j])
                {
                    s_Match = false;
                    break;
                }

            if (s_Match)
                return true;
        }

        return false;
    }
}
