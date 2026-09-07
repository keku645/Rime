using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Device = SharpDX.Direct3D11.Device;
using Buffer = SharpDX.Direct3D11.Buffer;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace RimeShaderEditor.View;

/// <summary>The stand-in geometry the preview renders the authored shader on.</summary>
public enum PreviewShape
{
    Cube,
    Sphere,
    Cylinder,
    Plane,

    /// <summary>A real game mesh, loaded with <see cref="ShaderPreview.LoadMeshObj"/>. Falls back to the
    /// cube until one is loaded.</summary>
    Mesh,
}

/// <summary>
/// Renders the authored shader on a cube. Two passes, mirroring the engine: the cube writes the four GBuffer
/// targets through the generated pixel shader unmodified, then a resolve pass lights them. The authored shader
/// is therefore executed for real rather than reinterpreted, which is the only way the preview can tell the
/// truth about things like the smoothness response.
/// </summary>
public sealed class ShaderPreview : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector3 Tangent;
        public Vector2 TexCoord;
    }

    private const int c_TargetCount = 4;

    private Device? m_Device;
    private DeviceContext? m_Context;
    private SwapChain? m_SwapChain;
    private RenderTargetView? m_BackBufferView;
    private Texture2D? m_Output;

    private readonly Texture2D?[] m_Targets = new Texture2D?[c_TargetCount];
    private readonly RenderTargetView?[] m_TargetViews = new RenderTargetView?[c_TargetCount];
    private readonly ShaderResourceView?[] m_TargetSrvs = new ShaderResourceView?[c_TargetCount];
    private Texture2D? m_Depth;
    private DepthStencilView? m_DepthView;
    private ShaderResourceView? m_DepthSrv;

    private Buffer? m_CubeVertices;
    private Buffer? m_CubeIndices;
    private Buffer? m_VsConstants;
    private Buffer? m_ViewConstants;

    /// <summary>The target shader's own parameters (cb1). 64 registers covers every mp_017 shader measured.</summary>
    private Buffer? m_ParameterConstants;

    /// <summary>Real material-instance values overriding pattern slots of cb1, as (element, xyzw).</summary>
    private List<(int Element, float[] Value)>? m_ExternalOverrides;

    /// <summary>
    /// Overwrites named external-parameter slots of cb1 with a material instance's REAL values, keeping the
    /// distinctive pattern everywhere else. EDITOR PREVIEW ONLY: the differential harnesses never call this -
    /// both of their sides must keep eating the identical pattern.
    /// </summary>
    public int SetExternalValues(IEnumerable<(int Element, float[] Value)>? p_Values)
    {
        m_ExternalOverrides = p_Values?.ToList();
        PushParameterBuffer();
        return m_ExternalOverrides?.Count ?? 0;
    }

    private void PushParameterBuffer()
    {
        if (m_Device == null || m_ParameterConstants == null)
            return;

        var s_Parameters = new RawVector4[c_ParameterSlots];
        for (var i = 0; i < c_ParameterSlots; i++)
            s_Parameters[i] = new RawVector4(0.10f + i * 0.037f, 0.65f - i * 0.021f,
                0.30f + i * 0.013f, 0.50f + i * 0.007f);

        foreach (var (s_Element, s_Value) in m_ExternalOverrides ?? new List<(int, float[])>())
            if (s_Element is >= 0 and < c_ParameterSlots && s_Value.Length >= 4)
                s_Parameters[s_Element] = new RawVector4(s_Value[0], s_Value[1], s_Value[2], s_Value[3]);

        m_Device.ImmediateContext.UpdateSubresource(s_Parameters, m_ParameterConstants);
    }

    /// <summary>DICE's `$Globals` (cb0): the outdoor light terms a forward-lit shader reads.</summary>
    private Buffer? m_GlobalConstants;

    private const int c_ParameterSlots = 64;
    private Buffer? m_LightConstants;

    private VertexShader? m_CubeVs;
    private InputLayout? m_CubeLayout;
    private VertexShader? m_ResolveVs;
    private PixelShader? m_ResolvePs;
    private PixelShader? m_ForwardResolvePs;
    private PixelShader? m_NeutralGBufferPs;
    private PixelShader? m_NeutralForwardPs;
    private DepthStencilState? m_DepthReadState;
    private BlendState? m_ForwardBlend;
    private RasterizerState? m_RasterNoCull;
    private PixelShader? m_AuthoredPs;

    /// <summary>True when the authored shader writes a single target (a forward/transparent solution):
    /// its output is finished premultiplied colour and takes the compositing resolve, not the lit one.</summary>
    private bool m_ForwardOutput;
    private SamplerState? m_Sampler;
    private RasterizerState? m_Raster;
    private DepthStencilState? m_DepthState;

    /// <summary>
    /// One entry per texture register, indexed BY the register. It used to be two fields, t1 and t2, which was
    /// fine only for the oil drum: a lightmapped shader binds its material from t5, and the shader keku hit
    /// samples t1..t4. Everything above t2 therefore sampled (0,0,0,0), and since albedo and specular are both
    /// products of those samples, the object came out mathematically black - no error, no warning, just a black
    /// cube that reads as "the editor is broken".
    /// </summary>
    private readonly ShaderResourceView?[] m_Textures = new ShaderResourceView?[c_TextureSlots];

    private const int c_TextureSlots = 16;

    private int m_Width;
    private int m_Height;

    public float Yaw { get; set; } = 0.6f;
    public float Pitch { get; set; } = 0.4f;
    public float Distance { get; set; } = 3.2f;
    public float Exposure { get; set; } = 1.0f;
    public float LightYaw { get; set; } = 0.9f;
    public float LightPitch { get; set; } = 0.7f;

    /// <summary>Point the camera orbits and looks at; panning moves this rather than the camera.</summary>
    public Vector3 Target { get; set; } = Vector3.Zero;

    /// <summary>Slides the orbit target across the screen plane, so panning feels attached to the view.</summary>
    public void Pan(float p_ScreenX, float p_ScreenY)
    {
        var s_Forward = Vector3.Normalize(Target - CameraPosition());
        var s_Right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, s_Forward));
        var s_Up = Vector3.Cross(s_Forward, s_Right);

        // Scale with distance so the drag tracks the pointer at any zoom.
        var s_Scale = Distance * 0.5f;
        Target += (-s_Right * p_ScreenX + s_Up * p_ScreenY) * s_Scale;
    }

    public void ResetView()
    {
        Yaw = 0.6f;
        Pitch = 0.4f;
        Distance = 3.2f;
        Target = Vector3.Zero;
    }

    private Vector3 CameraPosition() => Target + new Vector3(
        (float) (Math.Cos(Pitch) * Math.Sin(Yaw)),
        (float) Math.Sin(Pitch),
        (float) (Math.Cos(Pitch) * Math.Cos(Yaw))) * Distance;

    /// <summary>Seconds fed to the shader's time input, so panners and curves animate.</summary>
    public float Time { get; set; }

    /// <summary>
    /// A cube shows tiling and normal maps best, but its faces are flat, so a tight specular highlight never
    /// lands on one and smoothness looks like it does nothing. The sphere is what makes that readable; the
    /// cylinder reads curvature + tiling together, and the plane is the honest view for decals and terrain.
    /// </summary>
    public PreviewShape Shape
    {
        get => m_Shape;
        set
        {
            if (m_Shape == value)
                return;

            m_Shape = value;
            if (m_Device != null)
                BuildGeometry();
        }
    }

    /// <summary>
    /// The historical two-shape switch, kept as a facade because every harness seam (PREVIEW_SPHERE, the
    /// sweep, shaderdiff) speaks it — the guardian renders must keep their exact geometry.
    /// </summary>
    public bool UseSphere
    {
        get => m_Shape == PreviewShape.Sphere;
        set => Shape = value ? PreviewShape.Sphere : PreviewShape.Cube;
    }

    private PreviewShape m_Shape;
    private int m_IndexCount;
    private Format m_IndexFormat = Format.R16_UInt;
    private (Vertex[] Vertices, int[] Indices)? m_MeshData;

    /// <summary>One material section of a loaded game mesh: an index range plus the shader that wears it.
    /// Category is the game's own pass bucket (0 opaque, 1 transparent, 2 transparent decal) — a foreign
    /// TRANSPARENT section is skipped rather than stood in for, because an opaque grey rotor disc or canopy
    /// occludes the whole object. DoubleSided comes from the shader's own solutions (Flags bit 1) and turns
    /// culling off for that section — a canopy is visible from inside, foliage from both sides.</summary>
    public readonly record struct MeshSection(string Shader, string Material, int Category, bool DoubleSided,
        int StartIndex, int IndexCount);

    private readonly List<MeshSection> m_MeshSections = new();

    /// <summary>The shader the editor is editing, for deciding which mesh sections are "ours".</summary>
    public string MeshTargetShader { get; set; } = "";

    /// <summary>When set, mesh sections worn by OTHER shaders are not drawn at all (the Settings option).</summary>
    public bool HideForeignSections { get; set; }

    /// <summary>
    /// Everything one FOREIGN shader needs to draw its sections for real: its own game bytecode, a vertex
    /// shader generated for ITS contract (feeding another family's pixel shader through the active
    /// shader's interpolator layout would hand every input a different meaning), and its own texture set.
    /// </summary>
    private sealed class ForeignShader : IDisposable
    {
        public PixelShader? Ps;
        public VertexShader? Vs;
        public InputLayout? Layout;
        public ShaderResourceView?[] Textures = new ShaderResourceView?[c_TextureSlots];
        public bool Forward;

        /// <summary>Its own external-constants buffer, bound over the authored parameter pattern.</summary>
        public Buffer? Params;

        public int ParamsRegister = 1;

        public void Dispose()
        {
            Ps?.Dispose();
            Vs?.Dispose();
            Layout?.Dispose();
            Params?.Dispose();
            foreach (var s_Texture in Textures)
                s_Texture?.Dispose();
        }
    }

    private readonly Dictionary<string, ForeignShader> m_ForeignShaders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a foreign shader (one worn by mesh sections the editor is NOT editing) so its sections
    /// render with the REAL game pixel shader and art. Returns an error string, or null. Textures arrive
    /// as decoded BGRA pixel blocks keyed by the register the shader's own binding table names.
    /// </summary>
    public string? RegisterForeignShader(string p_Name, byte[] p_Dxbc,
        IEnumerable<(int Slot, uint[] Pixels, int Width, int Height)> p_Textures,
        IReadOnlyDictionary<string, string>? p_MaterialValues = null)
    {
        if (m_Device == null)
            return "no device";

        try
        {
            var s_Contract = Emit.ShaderContract.Detect(p_Dxbc);
            try
            {
                var s_Disassembly = new ShaderBytecode(p_Dxbc).Disassemble();
                s_Contract.ClassifyInterpolators(Translate.DxbcAsm.Parse(s_Disassembly.Split('\n')));
            }
            catch
            {
                // The shape still renders; only the interpolator FEED quality degrades.
            }

            var s_Foreign = new ForeignShader
            {
                Ps = new PixelShader(m_Device, p_Dxbc),
                Forward = s_Contract.RenderTargets == 1,
            };

            var s_VsCode = ShaderBytecode.Compile(
                PreviewShaders.VertexShaderFor(s_Contract.InterpolatorMeanings), "main", "vs_5_0");
            s_Foreign.Vs = new VertexShader(m_Device, s_VsCode);
            s_Foreign.Layout = new InputLayout(m_Device, s_VsCode, new[]
            {
                new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElement("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
                new InputElement("TANGENT", 0, Format.R32G32B32_Float, 24, 0),
                new InputElement("TEXCOORD", 0, Format.R32G32_Float, 36, 0),
            });

            foreach (var (s_Slot, s_Pixels, s_Width, s_Height) in p_Textures)
                if (s_Slot >= 1 && s_Slot < c_TextureSlots)
                    s_Foreign.Textures[s_Slot] = CreateTexture(s_Width, s_Height, s_Pixels);

            BuildForeignConstants(s_Foreign, p_Dxbc, p_MaterialValues);

            if (m_ForeignShaders.TryGetValue(p_Name, out var s_Old))
                s_Old.Dispose();
            m_ForeignShaders[p_Name] = s_Foreign;
            return null;
        }
        catch (Exception s_Exception)
        {
            return s_Exception.Message;
        }
    }

    /// <summary>
    /// Builds a foreign shader's OWN external-constants buffer from its RDEF layout. Without it the foreign
    /// draw read the authored graph's cb1 — a buffer laid out for a DIFFERENT shader and deliberately filled
    /// with a distinctive test pattern — so every camo tint, tiling and wear factor came out as arbitrary
    /// values (the jet's fuselage rendered violet). Fields default to 1 (the multiplicative neutral) and the
    /// level's own material instance values (CamoBrightness, CamoTiling…) overwrite the ones they name.
    /// </summary>
    private void BuildForeignConstants(ForeignShader p_Foreign, byte[] p_Dxbc,
        IReadOnlyDictionary<string, string>? p_MaterialValues)
    {
        var s_Fields = Emit.DxbcSignature.ReadConstantFields(p_Dxbc)
            .Where(p_F => p_F.Name.StartsWith("external_", StringComparison.OrdinalIgnoreCase) &&
                          p_F.Register >= 0 && p_F.Offset >= 0 && p_F.Size > 0)
            .ToList();

        if (s_Fields.Count == 0)
            return;

        var s_Floats = (s_Fields.Max(p_F => p_F.Offset + p_F.Size) + 15) / 16 * 4;
        var s_Data = new float[s_Floats];
        Array.Fill(s_Data, 1f);

        foreach (var s_Field in s_Fields)
        {
            var s_Value = p_MaterialValues?.FirstOrDefault(p_V =>
                s_Field.Name.Equals("external_" + p_V.Key, StringComparison.OrdinalIgnoreCase) ||
                s_Field.Name.Equals(p_V.Key, StringComparison.OrdinalIgnoreCase)).Value;
            if (s_Value == null)
                continue;

            var s_Parts = s_Value.Split(',');
            for (var i = 0; i < s_Parts.Length && i < s_Field.Size / 4; i++)
                if (float.TryParse(s_Parts[i], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var s_Component) &&
                    s_Field.Offset / 4 + i < s_Data.Length)
                    s_Data[s_Field.Offset / 4 + i] = s_Component;
        }

        p_Foreign.ParamsRegister = s_Fields[0].Register;
        p_Foreign.Params = Buffer.Create(m_Device, BindFlags.ConstantBuffer, s_Data);
    }

    public bool HasForeignShader(string p_Name) => m_ForeignShaders.ContainsKey(p_Name);

    /// <summary>The loaded mesh's sections, for the editor to know which foreign shaders to fetch.</summary>
    public IReadOnlyList<MeshSection> MeshSections => m_MeshSections;

    /// <summary>
    /// Loads a sectioned game-mesh dump (the RSM1 file dump_mesh_sections writes): per section, the shader
    /// that wears it plus positions/UVs and RELATIVE indices. Normals are accumulated from faces (the game
    /// packs its frame in formats the dump does not carry), tangents from the UV gradients, and the whole
    /// mesh is recentred and scaled to the primitives' size so the camera framing works unchanged.
    /// Returns an error string, or null on success.
    /// </summary>
    public string? LoadMeshSections(string p_Path)
    {
        try
        {
            using var s_Reader = new System.IO.BinaryReader(System.IO.File.OpenRead(p_Path));
            if (new string(s_Reader.ReadChars(4)) != "RSM3")
                return "not an RSM3 file (delete the cached dump and re-pick the mesh)";

            var s_SectionCount = s_Reader.ReadInt32();
            var s_Vertices = new List<Vertex>();
            var s_IndexList = new List<int>();
            var s_Sections = new List<MeshSection>();

            for (var s_Section = 0; s_Section < s_SectionCount; s_Section++)
            {
                var s_Shader = System.Text.Encoding.UTF8.GetString(s_Reader.ReadBytes(s_Reader.ReadInt32()));
                var s_Material = System.Text.Encoding.UTF8.GetString(s_Reader.ReadBytes(s_Reader.ReadInt32()));
                var s_Category = s_Reader.ReadInt32();
                var s_DoubleSided = s_Reader.ReadInt32() != 0;
                var s_VertexBase = s_Vertices.Count;
                var s_VertexCount = s_Reader.ReadInt32();

                for (var i = 0; i < s_VertexCount; i++)
                    s_Vertices.Add(new Vertex
                    {
                        Position = new Vector3(s_Reader.ReadSingle(), s_Reader.ReadSingle(), s_Reader.ReadSingle()),
                        TexCoord = new Vector2(s_Reader.ReadSingle(), s_Reader.ReadSingle()),
                    });

                var s_IndexCount = s_Reader.ReadInt32();
                var s_Start = s_IndexList.Count;
                for (var i = 0; i < s_IndexCount; i++)
                    s_IndexList.Add(s_VertexBase + s_Reader.ReadInt32());

                s_Sections.Add(new MeshSection(s_Shader.Replace('\\', '/'), s_Material, s_Category, s_DoubleSided,
                    s_Start, s_IndexCount));
            }

            return FinishMesh(s_Vertices, s_IndexList, s_Sections, "the mesh dump has no triangles");
        }
        catch (Exception s_Exception)
        {
            return s_Exception.Message;
        }
    }

    /// <summary>
    /// Reads a Wavefront OBJ into the preview — positions, texture coordinates and triangulated faces. It is
    /// PREVIEW geometry only: no material, no shader, nothing that reaches a bake.
    ///
    /// OBJ indexes each attribute separately (v/vt/vn) while the pipeline wants one vertex per (position,
    /// texcoord) pair, so pairs are interned as they appear. Faces come in as fans: a quad is two triangles,
    /// an n-gon n-2, which is what every exporter's "triangulate" would have produced anyway.
    /// </summary>
    public string? LoadObj(string p_Path)
    {
        try
        {
            var s_Positions = new List<Vector3>();
            var s_TexCoords = new List<Vector2> { Vector2.Zero };
            var s_Vertices = new List<Vertex>();
            var s_Indices = new List<int>();
            var s_Interned = new Dictionary<(int, int), int>();

            static float Number(string p_Text) =>
                float.TryParse(p_Text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var s_Value)
                    ? s_Value
                    : 0f;

            // "12", "12/7", "12//4" and "12/7/4" all name a position and maybe a texcoord; negative indices
            // count back from the end, which is legal OBJ and what several exporters write.
            int Corner(string p_Token)
            {
                var s_Parts = p_Token.Split('/');
                var s_Position = int.TryParse(s_Parts[0], out var s_P) ? s_P : 0;
                var s_TexCoord = s_Parts.Length > 1 && int.TryParse(s_Parts[1], out var s_T) ? s_T : 0;

                s_Position = s_Position < 0 ? s_Positions.Count + s_Position : s_Position - 1;
                s_TexCoord = s_TexCoord < 0 ? s_TexCoords.Count + s_TexCoord : s_TexCoord;

                if (s_Position < 0 || s_Position >= s_Positions.Count)
                    return -1;

                if (s_TexCoord < 0 || s_TexCoord >= s_TexCoords.Count)
                    s_TexCoord = 0;

                if (s_Interned.TryGetValue((s_Position, s_TexCoord), out var s_Existing))
                    return s_Existing;

                s_Vertices.Add(new Vertex
                {
                    Position = s_Positions[s_Position],
                    TexCoord = s_TexCoords[s_TexCoord],
                });

                s_Interned[(s_Position, s_TexCoord)] = s_Vertices.Count - 1;
                return s_Vertices.Count - 1;
            }

            foreach (var s_Line in System.IO.File.ReadLines(p_Path))
            {
                var s_Fields = s_Line.Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries);
                if (s_Fields.Length == 0 || s_Fields[0].StartsWith("#", StringComparison.Ordinal))
                    continue;

                switch (s_Fields[0])
                {
                    case "v" when s_Fields.Length >= 4:
                        s_Positions.Add(new Vector3(Number(s_Fields[1]), Number(s_Fields[2]),
                            Number(s_Fields[3])));
                        break;

                    // OBJ's V axis points up, the sampler's down.
                    case "vt" when s_Fields.Length >= 3:
                        s_TexCoords.Add(new Vector2(Number(s_Fields[1]), 1f - Number(s_Fields[2])));
                        break;

                    case "f" when s_Fields.Length >= 4:
                        var s_First = Corner(s_Fields[1]);
                        for (var i = 2; i + 1 < s_Fields.Length; i++)
                        {
                            var s_B = Corner(s_Fields[i]);
                            var s_C = Corner(s_Fields[i + 1]);
                            if (s_First < 0 || s_B < 0 || s_C < 0)
                                continue;

                            s_Indices.Add(s_First);
                            s_Indices.Add(s_B);
                            s_Indices.Add(s_C);
                        }

                        break;
                }
            }

            var s_Name = System.IO.Path.GetFileName(p_Path);
            return FinishMesh(s_Vertices, s_Indices,
                new List<MeshSection> { new("", s_Name, 0, false, 0, s_Indices.Count) },
                "the OBJ has no triangular faces (only 'v' and 'f' lines are read)");
        }
        catch (Exception s_Exception)
        {
            return s_Exception.Message;
        }
    }

    /// <summary>
    /// Everything a loaded mesh needs after its vertices and triangles are known, shared by every reader so
    /// the geometry is derived ONE way: smooth normals and UV-gradient tangents accumulated per face, then
    /// the whole thing recentred and scaled to the primitives' envelope so the camera framing and the light
    /// read the same whatever was loaded.
    /// </summary>
    private string? FinishMesh(List<Vertex> p_Vertices, List<int> p_IndexList, List<MeshSection> p_Sections,
        string p_EmptyMessage)
    {
        {
            var s_Vertices = p_Vertices;
            var s_IndexList = p_IndexList;
            var s_Sections = p_Sections;

            if (s_IndexList.Count == 0)
                return p_EmptyMessage;

            var s_Data = s_Vertices.ToArray();
            var s_Indices = s_IndexList.ToArray();

            // Accumulate smooth face normals and UV-gradient tangents.
            for (var i = 0; i < s_Indices.Length; i += 3)
            {
                ref var s_A = ref s_Data[s_Indices[i]];
                ref var s_B = ref s_Data[s_Indices[i + 1]];
                ref var s_C = ref s_Data[s_Indices[i + 2]];

                var s_Edge1 = s_B.Position - s_A.Position;
                var s_Edge2 = s_C.Position - s_A.Position;

                var s_FaceNormal = Vector3.Cross(s_Edge1, s_Edge2);
                s_A.Normal += s_FaceNormal;
                s_B.Normal += s_FaceNormal;
                s_C.Normal += s_FaceNormal;

                var s_DeltaUv1 = s_B.TexCoord - s_A.TexCoord;
                var s_DeltaUv2 = s_C.TexCoord - s_A.TexCoord;
                var s_Determinant = s_DeltaUv1.X * s_DeltaUv2.Y - s_DeltaUv2.X * s_DeltaUv1.Y;
                if (Math.Abs(s_Determinant) > 1e-12f)
                {
                    var s_Tangent = (s_Edge1 * s_DeltaUv2.Y - s_Edge2 * s_DeltaUv1.Y) / s_Determinant;
                    s_A.Tangent += s_Tangent;
                    s_B.Tangent += s_Tangent;
                    s_C.Tangent += s_Tangent;
                }
            }

            // Recentre + scale to the primitives' envelope so the camera framing and the light read the same.
            var s_Min = new Vector3(float.MaxValue);
            var s_Max = new Vector3(float.MinValue);
            foreach (var s_Vertex in s_Data)
            {
                s_Min = Vector3.Min(s_Min, s_Vertex.Position);
                s_Max = Vector3.Max(s_Max, s_Vertex.Position);
            }

            var s_Centre = (s_Min + s_Max) * 0.5f;
            var s_Extent = Math.Max(Math.Max(s_Max.X - s_Min.X, s_Max.Y - s_Min.Y), s_Max.Z - s_Min.Z);

            // 1.7, not the primitives' 1.1: a vehicle's envelope is dominated by its thinnest extremities
            // (rotor blades, gun barrels), which left the body tiny in the frame. The camera still orbits
            // and zooms, so slight cropping of an extremity at rest is the better trade.
            var s_Scale = s_Extent > 1e-6f ? 1.7f / s_Extent : 1f;

            for (var i = 0; i < s_Data.Length; i++)
            {
                s_Data[i].Position = (s_Data[i].Position - s_Centre) * s_Scale;
                s_Data[i].Normal = s_Data[i].Normal.LengthSquared() > 1e-12f
                    ? Vector3.Normalize(s_Data[i].Normal)
                    : new Vector3(0, 1, 0);
                s_Data[i].Tangent = s_Data[i].Tangent.LengthSquared() > 1e-12f
                    ? Vector3.Normalize(s_Data[i].Tangent)
                    : new Vector3(1, 0, 0);
            }

            m_MeshData = (s_Data, s_Indices);
            m_MeshSections.Clear();
            m_MeshSections.AddRange(s_Sections);
            if (m_Shape == PreviewShape.Mesh && m_Device != null)
                BuildGeometry();

            return null;
        }
    }

    public bool HasMesh => m_MeshData != null;

    public bool Ready => m_Device != null;
    public string? LastError { get; private set; }

    /// <summary>
    /// Same pipeline without a window, so the preview can be rendered to a file and actually looked at rather
    /// than merely "not crashing".
    /// </summary>
    public bool InitialiseOffscreen(int p_Width, int p_Height)
    {
        try
        {
            m_Width = Math.Max(1, p_Width);
            m_Height = Math.Max(1, p_Height);

            m_Device = new Device(DriverType.Hardware, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 });
            m_Context = m_Device.ImmediateContext;

            BuildStaticResources();
            CreateSizeDependentResources();
            return true;
        }
        catch (Exception s_Exception)
        {
            LastError = s_Exception.Message;
            Dispose();
            return false;
        }
    }

    /// <summary>
    /// Copies one GBuffer target out as RAW RGBA, with no channel swap and no lighting applied. This is the
    /// pixel shader's actual output, which is what an equivalence test between two shaders has to compare -
    /// diffing the lit resolve would hide any difference the lighting pass happens not to read (RT2.xyz).
    /// </summary>
    public byte[]? CaptureTarget(int p_Index)
    {
        if (m_Device == null || m_Context == null || p_Index < 0 || p_Index >= c_TargetCount)
            return null;

        var s_Source = m_Targets[p_Index];
        if (s_Source == null)
            return null;

        using var s_Staging = new Texture2D(m_Device, new Texture2DDescription
        {
            Width = m_Width,
            Height = m_Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CpuAccessFlags = CpuAccessFlags.Read,
        });

        m_Context.CopyResource(s_Source, s_Staging);

        var s_Box = m_Context.MapSubresource(s_Staging, 0, MapMode.Read, MapFlags.None, out _);
        var s_Bytes = new byte[m_Width * m_Height * 4];

        for (var y = 0; y < m_Height; y++)
            Marshal.Copy(IntPtr.Add(s_Box.DataPointer, y * s_Box.RowPitch), s_Bytes, y * m_Width * 4,
                m_Width * 4);

        m_Context.UnmapSubresource(s_Staging, 0);
        return s_Bytes;
    }

    /// <summary>Copies the resolved image out to BGRA bytes for saving.</summary>
    public byte[]? Capture(out int p_Width, out int p_Height)
    {
        p_Width = m_Width;
        p_Height = m_Height;

        if (m_Device == null || m_Context == null || m_Output == null)
            return null;

        using var s_Staging = new Texture2D(m_Device, new Texture2DDescription
        {
            Width = m_Width,
            Height = m_Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CpuAccessFlags = CpuAccessFlags.Read,
        });

        m_Context.CopyResource(m_Output, s_Staging);

        var s_Box = m_Context.MapSubresource(s_Staging, 0, MapMode.Read, MapFlags.None, out _);
        var s_Bytes = new byte[m_Width * m_Height * 4];

        for (var y = 0; y < m_Height; y++)
            Marshal.Copy(IntPtr.Add(s_Box.DataPointer, y * s_Box.RowPitch), s_Bytes, y * m_Width * 4,
                m_Width * 4);

        m_Context.UnmapSubresource(s_Staging, 0);

        // RGBA on the GPU, BGRA for the encoder.
        for (var i = 0; i < s_Bytes.Length; i += 4)
            (s_Bytes[i], s_Bytes[i + 2]) = (s_Bytes[i + 2], s_Bytes[i]);

        return s_Bytes;
    }

    public bool Initialise(IntPtr p_WindowHandle, int p_Width, int p_Height)
    {
        try
        {
            m_Width = Math.Max(1, p_Width);
            m_Height = Math.Max(1, p_Height);

            var s_Description = new SwapChainDescription
            {
                BufferCount = 2,
                ModeDescription = new ModeDescription(m_Width, m_Height, new Rational(60, 1),
                    Format.R8G8B8A8_UNorm),
                IsWindowed = true,
                OutputHandle = p_WindowHandle,
                SampleDescription = new SampleDescription(1, 0),
                SwapEffect = SwapEffect.Discard,
                Usage = Usage.RenderTargetOutput,
            };

            Device.CreateWithSwapChain(DriverType.Hardware, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 }, s_Description,
                out m_Device, out m_SwapChain);

            m_Context = m_Device.ImmediateContext;

            BuildStaticResources();
            CreateSizeDependentResources();
            return true;
        }
        catch (Exception s_Exception)
        {
            LastError = s_Exception.Message;
            Dispose();
            return false;
        }
    }

    /// <summary>
    /// Feeds each TEXCOORD what the TARGET shader treats it as. Null or empty restores the measured rigid
    /// layout - which is also what every guardian is pinned against, so the default path never moves.
    /// </summary>
    public void SetInterpolatorMeanings(IReadOnlyDictionary<int, string>? p_Meanings)
    {
        if (m_Device != null)
            BuildCubeVertexShader(p_Meanings);
    }

    private void BuildCubeVertexShader(IReadOnlyDictionary<int, string>? p_Meanings)
    {
        var s_Device = m_Device!;
        m_CubeVs?.Dispose();
        m_CubeLayout?.Dispose();

        var s_CubeVsCode = ShaderBytecode.Compile(PreviewShaders.VertexShaderFor(p_Meanings), "main", "vs_5_0");
        m_CubeVs = new VertexShader(s_Device, s_CubeVsCode);
        m_CubeLayout = new InputLayout(s_Device, s_CubeVsCode, new[]
        {
            new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElement("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new InputElement("TANGENT", 0, Format.R32G32B32_Float, 24, 0),
            new InputElement("TEXCOORD", 0, Format.R32G32_Float, 36, 0),
        });
    }

    private void BuildStaticResources()
    {
        var s_Device = m_Device!;
        BuildCubeVertexShader(null);

        m_ResolveVs = new VertexShader(s_Device,
            ShaderBytecode.Compile(PreviewShaders.c_ResolveVertexShader, "main", "vs_5_0"));
        m_ResolvePs = new PixelShader(s_Device,
            ShaderBytecode.Compile(PreviewShaders.c_ResolvePixelShader, "main", "ps_5_0"));
        m_ForwardResolvePs = new PixelShader(s_Device,
            ShaderBytecode.Compile(PreviewShaders.c_ForwardResolvePixelShader, "main", "ps_5_0"));
        m_NeutralGBufferPs = new PixelShader(s_Device,
            ShaderBytecode.Compile(PreviewShaders.c_NeutralGBufferPixelShader, "main", "ps_5_0"));
        m_NeutralForwardPs = new PixelShader(s_Device,
            ShaderBytecode.Compile(PreviewShaders.c_NeutralForwardPixelShader, "main", "ps_5_0"));

        BuildGeometry();

        m_VsConstants = new Buffer(s_Device, 128, ResourceUsage.Dynamic, BindFlags.ConstantBuffer,
            CpuAccessFlags.Write, ResourceOptionFlags.None, 0);

        // 21 float4s: the authored shader's cbuffer reaches cameraPos at c20.
        m_ViewConstants = new Buffer(s_Device, 21 * 16, ResourceUsage.Dynamic, BindFlags.ConstantBuffer,
            CpuAccessFlags.Write, ResourceOptionFlags.None, 0);

        m_LightConstants = new Buffer(s_Device, 64 + 5 * 16, ResourceUsage.Dynamic, BindFlags.ConstantBuffer,
            CpuAccessFlags.Write, ResourceOptionFlags.None, 0);

        // ⛔ The material parameter buffer (cb1), filled with a DISTINCTIVE pattern rather than left unbound.
        //
        // Unbound would read as zeros - for the game's shader and for ours alike - so the differential test would
        // still say EQUIVALENT while proving nothing at all about whether a parameter was wired to the right
        // slot. That is validating with the neutral value, the exact trap that already cost a cycle on the
        // texture preview. With a pattern, reading cb1[3] where the original reads cb1[2] changes the picture.
        m_ParameterConstants = new Buffer(s_Device, c_ParameterSlots * 16, ResourceUsage.Default,
            BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

        // Re-applied here because instance values may have been set BEFORE the device existed - everything
        // pushed at a lazily initialised subsystem must be re-pushed right after it initialises.
        PushParameterBuffer();

        // ⛔⛔ cb0 is DICE's `$Globals` - the outdoor light and light-probe terms a forward shader reads. It was
        // never bound in pass 1, and that is not the same as "reads zeros": the RESOLVE pass binds its own light
        // constants at slot 0, so on a fresh device the game's shader rendered with cb0 EMPTY and ours rendered
        // with the previous pass's leftovers still bound. Two halves of a differential test fed different inputs.
        // It only ever showed on shaders that read cb0 - glass, decals, light cones - which is why chasing it
        // through undefined render targets and resolution twice refuted the wrong hypothesis.
        //
        // A DIFFERENT pattern from cb1's on purpose: identical buffers would hide a graph that confused the two.
        var s_Globals = new RawVector4[c_ParameterSlots];
        for (var i = 0; i < c_ParameterSlots; i++)
            s_Globals[i] = new RawVector4(0.72f - i * 0.011f, 0.19f + i * 0.029f,
                0.44f + i * 0.017f, 0.86f - i * 0.005f);

        // c7..c10 are the light-probe SH rows (lightProbeShR/G/B/O). The shader clamps their dp4 against
        // the normal at zero — with the generic pattern the dp4 went NEGATIVE for the placeholder normal,
        // the whole probe term collapsed to zero on BOTH sides of a differential test, and an equivalence
        // over zeros proves nothing (caught by a tint perturbation that changed nothing). These rows keep
        // |xyz| well under w, so the dp4 stays positive for every normal AND still depends on it.
        for (var i = 7; i <= 10; i++)
            s_Globals[i] = new RawVector4(0.20f - i * 0.010f, 0.12f + i * 0.008f,
                -0.08f + i * 0.012f, 0.55f + i * 0.010f);

        m_GlobalConstants = new Buffer(s_Device, c_ParameterSlots * 16, ResourceUsage.Default,
            BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

        s_Device.ImmediateContext.UpdateSubresource(s_Globals, m_GlobalConstants);

        m_Sampler = new SamplerState(s_Device, new SamplerStateDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            ComparisonFunction = Comparison.Never,
            MaximumLod = float.MaxValue,
        });

        m_Raster = new RasterizerState(s_Device, new RasterizerStateDescription
        {
            CullMode = CullMode.Back,
            FillMode = FillMode.Solid,
            IsDepthClipEnabled = true,
        });

        // For sections whose shader's solutions carry the DoubleSided flag (bit 1): both faces drawn, the
        // way the engine rasterises them — a canopy stays visible from inside, foliage from both sides.
        m_RasterNoCull = new RasterizerState(s_Device, new RasterizerStateDescription
        {
            CullMode = CullMode.None,
            FillMode = FillMode.Solid,
            IsDepthClipEnabled = true,
        });

        m_DepthState = new DepthStencilState(s_Device, new DepthStencilStateDescription
        {
            IsDepthEnabled = true,
            DepthWriteMask = DepthWriteMask.All,
            DepthComparison = Comparison.LessEqual,
        });

        // The forward composite pass: tested against pass 1's depth without writing it, blended
        // premultiplied over the lit backbuffer — the transparent pass's own convention.
        m_DepthReadState = new DepthStencilState(s_Device, new DepthStencilStateDescription
        {
            IsDepthEnabled = true,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthComparison = Comparison.LessEqual,
        });

        var s_Blend = new BlendStateDescription();
        s_Blend.RenderTarget[0] = new RenderTargetBlendDescription
        {
            IsBlendEnabled = true,
            SourceBlend = BlendOption.One,
            DestinationBlend = BlendOption.InverseSourceAlpha,
            BlendOperation = BlendOperation.Add,
            SourceAlphaBlend = BlendOption.One,
            DestinationAlphaBlend = BlendOption.InverseSourceAlpha,
            AlphaBlendOperation = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteMaskFlags.All,
        };
        m_ForwardBlend = new BlendState(s_Device, s_Blend);

        // A neutral checker stands in for an unloaded texture so the cube is never blank. t1/t2 keep exactly the
        // defaults they always had, because the drum's output is verified bit-exact against the game's own shader
        // and must not move by an LSB.
        //
        // ⛔ EXCEPT under PREVIEW_ALPHA_GRADIENT=1, an opt-in for the alpha-coverage differential tests: the
        // default checker's alpha is a CONSTANT 1.0, so every coverage tap passes everywhere and the whole
        // SV_Coverage path compares constant-against-constant — a diff that cannot fail proves nothing. The
        // gradient sweeps alpha across the threshold over the surface, which is what makes wrong tap offsets,
        // a wrong tap count and a wrong threshold all show up as pixels kept or discarded differently.
        m_Textures[1] = Environment.GetEnvironmentVariable("PREVIEW_ALPHA_GRADIENT") == "1"
            ? CreateAlphaGradientTexture()
            : CreateCheckerTexture(0xFFB0B0B0, 0xFF808080);
        m_Textures[2] = CreateFlatTexture(0xFFFF8080);

        // ⛔ Everything else is MAGENTA-ish on purpose, never black. A black default is what hid the
        // missing-binding bug: a shader sampling an unbound slot produced a black object, which is
        // indistinguishable from a shader that legitimately shades to black.
        for (var s_Slot = 3; s_Slot < c_TextureSlots; s_Slot++)
            m_Textures[s_Slot] = CreatePlaceholderTexture(s_Slot);
    }

    /// <summary>
    /// The stand-in for a slot nothing was loaded into: still loud enough to read as "not supplied", but with
    /// NO degenerate channel and NO flat area.
    ///
    /// ⛔⛔⛔ IT USED TO BE FLAT MAGENTA, AND MAGENTA HAS GREEN EXACTLY ZERO. That is the neutral-value trap of
    /// this project's own law, sitting inside the verification harness: any shader whose output ran through the
    /// green channel of an unbound slot multiplied by a hard zero and wrote nothing at all, so the sweep filed
    /// it under "wrote nothing on the test mesh" and could say NOTHING about it - 24 terrain mask passes among
    /// them. A placeholder is an INPUT of the differential test, and the law is that every input gets a
    /// distinctive value, never a zero.
    ///
    /// Flat was the second half of the same mistake: with one colour everywhere, a wrong UV samples the same
    /// texel as the right one, so the whole class of coordinate bugs is invisible by construction. Every
    /// channel now varies across the surface AND differs per slot, which is what makes "reads t7 instead of t5"
    /// and "reads at the wrong coordinate" both show up as a difference.
    ///
    /// ⛔⛔ AND THE PATTERN IS A MONOTONIC GRADIENT, NOT A CHECKER - the first varied version used one and it
    /// was wrong in BOTH directions. A checker is periodic, so a coordinate error of exactly one period reads
    /// back identical and the test says "equivalent" about a shader that samples the wrong place; and its edges
    /// are discontinuous, so a SUB-TEXEL error - the kind two mathematically identical programs produce just by
    /// associating their floats differently - jumps a whole step and reads as a failure. Two sky shaders that
    /// work with Earth-radius magnitudes failed at 5/255 for exactly that reason, and the tell was that the one
    /// channel WITHOUT a checker differed by 1 while the two with it differed by 5 and 3.
    /// A gradient makes the response PROPORTIONAL to the coordinate error and non-aliasing: small error, small
    /// difference; wrong texture or wrong coordinate, large difference.
    /// </summary>
    /// <summary>
    /// The colour checker t1 always had, with the ALPHA swept as a fine diagonal gradient instead of the
    /// constant 1.0. Opt-in via PREVIEW_ALPHA_GRADIENT for coverage tests only: alpha crosses the 0.5
    /// threshold many times across the surface, so the supersampled alpha-test taps land on both sides of
    /// it and their exact offsets become visible in which pixels survive. The period is small (8 texels)
    /// so the threshold edge crosses many derivative-sized neighbourhoods.
    /// </summary>
    private ShaderResourceView CreateAlphaGradientTexture()
    {
        const int c_Size = 32;
        var s_Pixels = new uint[c_Size * c_Size];

        for (var y = 0; y < c_Size; y++)
        for (var x = 0; x < c_Size; x++)
        {
            var s_Colour = (x / 4 + y / 4) % 2 == 0 ? 0x00B0B0B0u : 0x00808080u;
            var s_Alpha = (uint) (16 + (x * 5 + y * 3) % 224);
            s_Pixels[y * c_Size + x] = s_Alpha << 24 | s_Colour;
        }

        return CreateTexture(c_Size, c_Size, s_Pixels);
    }

    private ShaderResourceView CreatePlaceholderTexture(int p_Slot)
    {
        const int c_Size = 32;
        var s_Pixels = new uint[c_Size * c_Size];

        // Each channel ramps along a DIFFERENT axis, so a swizzle mix-up moves the picture too, and each slot
        // sits at its own offset so reading t7 where the shader wanted t5 cannot come out the same.
        var s_Offset = p_Slot * 13 % 40;

        for (var y = 0; y < c_Size; y++)
        for (var x = 0; x < c_Size; x++)
        {
            // ⛔⛔ THE RANGE IS ~0.10..0.55 ON PURPOSE, AND BOTH ENDS ARE A MEASURED MISTAKE, NOT TASTE.
            // A 0 is the neutral-value trap that started all this. But a value near 1 is the OTHER end of the
            // same trap: it SATURATES the shader. Five terrain mask passes computed `dp2_sat(a,a, b,b)`, which
            // is saturate(2ab), and with placeholders around 0.8 that clamps to 1 for every texel; the shader
            // then took `1 - 1 = 0`, failed its own `0 < x` test, skipped its whole body and wrote nothing -
            // so the sweep filed them as "wrote nothing" and could say nothing about them. A saturated input
            // destroys information exactly like a zero does; it just does it at the top of the range.
            // Kept clear of 0 and of anything that makes a doubled product reach 1.
            var s_R = 25 + x * 3 + s_Offset / 2;
            var s_G = 30 + y * 3 + s_Offset / 3;
            var s_B = 20 + (x + y) * 2 + s_Offset / 2;
            var s_A = 35 + (c_Size - 1 - x + y) * 3 / 2;

            s_Pixels[y * c_Size + x] =
                ((uint) Math.Clamp(s_A, 1, 254) << 24) | ((uint) Math.Clamp(s_R, 1, 254) << 16) |
                ((uint) Math.Clamp(s_G, 1, 254) << 8) | (uint) Math.Clamp(s_B, 1, 254);
        }

        return CreateTexture(c_Size, c_Size, s_Pixels);
    }

    private void BuildGeometry()
    {
        m_CubeVertices?.Dispose();
        m_CubeIndices?.Dispose();
        m_IndexFormat = Format.R16_UInt;

        switch (m_Shape)
        {
            case PreviewShape.Sphere:
                BuildSphere();
                return;

            case PreviewShape.Cylinder:
                BuildCylinder();
                return;

            case PreviewShape.Plane:
                BuildPlane();
                return;

            // A real mesh easily exceeds the 16-bit index range, so it gets 32-bit indices; the shape
            // falls back to the cube until a mesh has been loaded.
            case PreviewShape.Mesh when m_MeshData is { } s_Mesh:
                m_CubeVertices = Buffer.Create(m_Device, BindFlags.VertexBuffer, s_Mesh.Vertices);
                m_CubeIndices = Buffer.Create(m_Device, BindFlags.IndexBuffer, s_Mesh.Indices);
                m_IndexCount = s_Mesh.Indices.Length;
                m_IndexFormat = Format.R32_UInt;
                return;
        }

        var s_Faces = new[]
        {
            (Normal: new Vector3(0, 0, -1), Tangent: new Vector3(1, 0, 0)),
            (Normal: new Vector3(0, 0, 1), Tangent: new Vector3(-1, 0, 0)),
            (Normal: new Vector3(-1, 0, 0), Tangent: new Vector3(0, 0, -1)),
            (Normal: new Vector3(1, 0, 0), Tangent: new Vector3(0, 0, 1)),
            (Normal: new Vector3(0, -1, 0), Tangent: new Vector3(1, 0, 0)),
            (Normal: new Vector3(0, 1, 0), Tangent: new Vector3(1, 0, 0)),
        };

        var s_Vertices = new Vertex[24];
        var s_Indices = new ushort[36];

        for (var i = 0; i < 6; i++)
        {
            var s_Normal = s_Faces[i].Normal;
            var s_Tangent = s_Faces[i].Tangent;
            var s_Binormal = Vector3.Cross(s_Normal, s_Tangent);

            // Corners of the face, built from its own basis so every face gets sane uvs and a tangent frame.
            var s_Corners = new[]
            {
                -s_Tangent - s_Binormal, s_Tangent - s_Binormal, s_Tangent + s_Binormal, -s_Tangent + s_Binormal,
            };

            // ⛔ V grows DOWNWARD in D3D (row 0 of the image is V=0): the first corner (-tangent -binormal)
            // is the face's TOP-left, so it takes V=0. The old table gave it V=1, which drew every texture
            // upside down — invisible on organic art for months, obvious the day a texture had text on it.
            var s_Uvs = new[]
            {
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1),
            };

            for (var s_Corner = 0; s_Corner < 4; s_Corner++)
                s_Vertices[i * 4 + s_Corner] = new Vertex
                {
                    Position = (s_Normal + s_Corners[s_Corner]) * 0.5f,
                    Normal = s_Normal,
                    Tangent = s_Tangent,
                    TexCoord = s_Uvs[s_Corner],
                };

            var s_Base = i * 4;
            var s_Slot = i * 6;
            s_Indices[s_Slot + 0] = (ushort) (s_Base + 0);
            s_Indices[s_Slot + 1] = (ushort) (s_Base + 1);
            s_Indices[s_Slot + 2] = (ushort) (s_Base + 2);
            s_Indices[s_Slot + 3] = (ushort) (s_Base + 0);
            s_Indices[s_Slot + 4] = (ushort) (s_Base + 2);
            s_Indices[s_Slot + 5] = (ushort) (s_Base + 3);
        }

        m_CubeVertices = Buffer.Create(m_Device, BindFlags.VertexBuffer, s_Vertices);
        m_CubeIndices = Buffer.Create(m_Device, BindFlags.IndexBuffer, s_Indices);
        m_IndexCount = s_Indices.Length;
    }

    private void BuildSphere()
    {
        const int c_Rings = 48;
        const int c_Segments = 96;

        var s_Vertices = new Vertex[(c_Rings + 1) * (c_Segments + 1)];
        var s_Index = 0;

        for (var s_Ring = 0; s_Ring <= c_Rings; s_Ring++)
        {
            var s_V = s_Ring / (float) c_Rings;
            var s_Phi = s_V * MathF.PI;

            for (var s_Segment = 0; s_Segment <= c_Segments; s_Segment++)
            {
                var s_U = s_Segment / (float) c_Segments;
                var s_Theta = s_U * MathF.PI * 2f;

                var s_Normal = new Vector3(
                    MathF.Sin(s_Phi) * MathF.Cos(s_Theta),
                    MathF.Cos(s_Phi),
                    MathF.Sin(s_Phi) * MathF.Sin(s_Theta));

                // Tangent along increasing longitude keeps the frame consistent with the uv direction.
                var s_Tangent = new Vector3(-MathF.Sin(s_Theta), 0f, MathF.Cos(s_Theta));

                s_Vertices[s_Index++] = new Vertex
                {
                    Position = s_Normal * 0.5f,
                    Normal = s_Normal,
                    Tangent = s_Tangent,
                    TexCoord = new Vector2(s_U * 2f, s_V),
                };
            }
        }

        var s_Indices = new ushort[c_Rings * c_Segments * 6];
        var s_Slot = 0;

        for (var s_Ring = 0; s_Ring < c_Rings; s_Ring++)
        for (var s_Segment = 0; s_Segment < c_Segments; s_Segment++)
        {
            var s_A = (ushort) (s_Ring * (c_Segments + 1) + s_Segment);
            var s_B = (ushort) (s_A + 1);
            var s_C = (ushort) (s_A + c_Segments + 1);
            var s_D = (ushort) (s_C + 1);

            // Wound to match the cube, whose triangles are known to face outward: for a quad at the equator
            // this order gives a geometric normal along +X where the surface normal is +X. The reverse order
            // renders the inside of the sphere, which reads as inverted lighting rather than as a hole.
            s_Indices[s_Slot++] = s_A;
            s_Indices[s_Slot++] = s_B;
            s_Indices[s_Slot++] = s_C;
            s_Indices[s_Slot++] = s_B;
            s_Indices[s_Slot++] = s_D;
            s_Indices[s_Slot++] = s_C;
        }

        m_CubeVertices = Buffer.Create(m_Device, BindFlags.VertexBuffer, s_Vertices);
        m_CubeIndices = Buffer.Create(m_Device, BindFlags.IndexBuffer, s_Indices);
        m_IndexCount = s_Indices.Length;
    }

    /// <summary>
    /// A capped cylinder: the side reads curvature and tiling together (the highlight sweeps across it the
    /// way it never can on a flat face), the caps keep it from looking like a tube. Same frame conventions
    /// as the sphere: tangent along increasing longitude, V growing downward.
    /// </summary>
    private void BuildCylinder()
    {
        const int c_Segments = 64;
        const float c_Radius = 0.35f;
        const float c_HalfHeight = 0.5f;

        var s_Vertices = new List<Vertex>();
        var s_Indices = new List<ushort>();

        // Side: two rings of shared vertices, wrapped with one duplicated seam column for clean UVs.
        for (var s_Segment = 0; s_Segment <= c_Segments; s_Segment++)
        {
            var s_U = s_Segment / (float) c_Segments;
            var s_Theta = s_U * MathF.PI * 2f;
            var s_Normal = new Vector3(MathF.Cos(s_Theta), 0f, MathF.Sin(s_Theta));
            var s_Tangent = new Vector3(-MathF.Sin(s_Theta), 0f, MathF.Cos(s_Theta));

            foreach (var (s_Y, s_V) in new[] { (c_HalfHeight, 0f), (-c_HalfHeight, 1f) })
                s_Vertices.Add(new Vertex
                {
                    Position = new Vector3(s_Normal.X * c_Radius, s_Y, s_Normal.Z * c_Radius),
                    Normal = s_Normal,
                    Tangent = s_Tangent,
                    TexCoord = new Vector2(s_U * 2f, s_V),
                });
        }

        for (var s_Segment = 0; s_Segment < c_Segments; s_Segment++)
        {
            var s_Top = (ushort) (s_Segment * 2);
            var s_Bottom = (ushort) (s_Top + 1);
            var s_NextTop = (ushort) (s_Top + 2);
            var s_NextBottom = (ushort) (s_Top + 3);

            s_Indices.AddRange(new[] { s_Top, s_NextTop, s_Bottom, s_NextTop, s_NextBottom, s_Bottom });
        }

        // Caps: a fan around a centre vertex, UVs mapped from the disc.
        foreach (var (s_Y, s_Up) in new[] { (c_HalfHeight, 1f), (-c_HalfHeight, -1f) })
        {
            var s_Normal = new Vector3(0f, s_Up, 0f);
            var s_Centre = (ushort) s_Vertices.Count;
            s_Vertices.Add(new Vertex
            {
                Position = new Vector3(0f, s_Y, 0f), Normal = s_Normal,
                Tangent = new Vector3(1f, 0f, 0f), TexCoord = new Vector2(0.5f, 0.5f),
            });

            for (var s_Segment = 0; s_Segment <= c_Segments; s_Segment++)
            {
                var s_Theta = s_Segment / (float) c_Segments * MathF.PI * 2f;
                var (s_X, s_Z) = (MathF.Cos(s_Theta), MathF.Sin(s_Theta));
                s_Vertices.Add(new Vertex
                {
                    Position = new Vector3(s_X * c_Radius, s_Y, s_Z * c_Radius), Normal = s_Normal,
                    Tangent = new Vector3(1f, 0f, 0f),
                    TexCoord = new Vector2(0.5f + s_X * 0.5f, 0.5f + s_Z * 0.5f * s_Up),
                });
            }

            for (var s_Segment = 0; s_Segment < c_Segments; s_Segment++)
            {
                var s_A = (ushort) (s_Centre + 1 + s_Segment);
                var s_B = (ushort) (s_A + 1);
                s_Indices.AddRange(s_Up > 0
                    ? new[] { s_Centre, s_B, s_A }
                    : new[] { s_Centre, s_A, s_B });
            }
        }

        m_CubeVertices = Buffer.Create(m_Device, BindFlags.VertexBuffer, s_Vertices.ToArray());
        m_CubeIndices = Buffer.Create(m_Device, BindFlags.IndexBuffer, s_Indices.ToArray());
        m_IndexCount = s_Indices.Count;
    }

    /// <summary>
    /// A unit quad, BOTH faces, standing upright facing the camera's home position: the honest view for
    /// decals, terrain layers and anything whose look is really a texture. Two opposed faces rather than a
    /// disabled cull, so orbiting behind it shows a lit surface instead of an inside-out ghost.
    /// </summary>
    private void BuildPlane()
    {
        // The exact per-face construction the cube uses (verified winding + the V-grows-downward law),
        // for the two opposed faces only, scaled to a full unit and flattened to zero thickness.
        var s_Faces = new[]
        {
            (Normal: new Vector3(0, 0, -1), Tangent: new Vector3(1, 0, 0)),
            (Normal: new Vector3(0, 0, 1), Tangent: new Vector3(-1, 0, 0)),
        };

        var s_Vertices = new Vertex[8];
        var s_Indices = new ushort[12];

        for (var i = 0; i < s_Faces.Length; i++)
        {
            var s_Normal = s_Faces[i].Normal;
            var s_Tangent = s_Faces[i].Tangent;
            var s_Binormal = Vector3.Cross(s_Normal, s_Tangent);

            var s_Corners = new[]
            {
                -s_Tangent - s_Binormal, s_Tangent - s_Binormal, s_Tangent + s_Binormal, -s_Tangent + s_Binormal,
            };
            var s_Uvs = new[]
            {
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1),
            };

            for (var s_Corner = 0; s_Corner < 4; s_Corner++)
                s_Vertices[i * 4 + s_Corner] = new Vertex
                {
                    Position = s_Corners[s_Corner] * 0.5f,
                    Normal = s_Normal,
                    Tangent = s_Tangent,
                    TexCoord = s_Uvs[s_Corner],
                };

            var s_Base = i * 4;
            var s_Slot = i * 6;
            s_Indices[s_Slot + 0] = (ushort) (s_Base + 0);
            s_Indices[s_Slot + 1] = (ushort) (s_Base + 1);
            s_Indices[s_Slot + 2] = (ushort) (s_Base + 2);
            s_Indices[s_Slot + 3] = (ushort) (s_Base + 0);
            s_Indices[s_Slot + 4] = (ushort) (s_Base + 2);
            s_Indices[s_Slot + 5] = (ushort) (s_Base + 3);
        }

        m_CubeVertices = Buffer.Create(m_Device, BindFlags.VertexBuffer, s_Vertices);
        m_CubeIndices = Buffer.Create(m_Device, BindFlags.IndexBuffer, s_Indices);
        m_IndexCount = s_Indices.Length;
    }

    private ShaderResourceView CreateFlatTexture(uint p_Colour)
    {
        var s_Pixels = new uint[4];
        for (var i = 0; i < s_Pixels.Length; i++)
            s_Pixels[i] = p_Colour;

        return CreateTexture(2, 2, s_Pixels);
    }

    private ShaderResourceView CreateCheckerTexture(uint p_A, uint p_B)
    {
        const int c_Size = 16;
        var s_Pixels = new uint[c_Size * c_Size];
        for (var y = 0; y < c_Size; y++)
        for (var x = 0; x < c_Size; x++)
            s_Pixels[y * c_Size + x] = ((x / 4 + y / 4) & 1) == 0 ? p_A : p_B;

        return CreateTexture(c_Size, c_Size, s_Pixels);
    }

    private ShaderResourceView CreateTexture(int p_Width, int p_Height, uint[] p_Pixels)
    {
        var s_Handle = GCHandle.Alloc(p_Pixels, GCHandleType.Pinned);
        try
        {
            var s_Texture = new Texture2D(m_Device, new Texture2DDescription
            {
                Width = p_Width,
                Height = p_Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
            }, new DataRectangle(s_Handle.AddrOfPinnedObject(), p_Width * 4));

            var s_View = new ShaderResourceView(m_Device, s_Texture);
            s_Texture.Dispose();
            return s_View;
        }
        finally
        {
            s_Handle.Free();
        }
    }

    /// <summary>Replaces a texture slot with real BF3 art once the thumbnails have been loaded.</summary>
    public void SetTexture(int p_Register, uint[] p_Pixels, int p_Width, int p_Height)
    {
        if (m_Device == null)
            return;

        // ⛔ The old body was `if (register == 2) ... else ...`, which collapsed every other register onto t1: a
        // shader with four textures ended up with three of them writing over each other in slot 1.
        if (p_Register < 0 || p_Register >= c_TextureSlots)
            return;

        var s_View = CreateTexture(p_Width, p_Height, p_Pixels);
        m_Textures[p_Register]?.Dispose();
        m_Textures[p_Register] = s_View;
    }

    /// <summary>Swaps in a freshly compiled authored shader. Returns false and keeps the old one on failure.</summary>
    public bool SetAuthoredShader(byte[] p_Dxbc)
    {
        if (m_Device == null)
            return false;

        try
        {
            var s_Shader = new PixelShader(m_Device, p_Dxbc);
            m_AuthoredPs?.Dispose();
            m_AuthoredPs = s_Shader;

            // One declared target = forward output; the resolve pass composites it instead of lighting it.
            try { m_ForwardOutput = Emit.ShaderContract.Detect(p_Dxbc).RenderTargets == 1; }
            catch { m_ForwardOutput = false; }

            LastError = null;
            return true;
        }
        catch (Exception s_Exception)
        {
            LastError = s_Exception.Message;
            return false;
        }
    }

    public void Resize(int p_Width, int p_Height)
    {
        if (m_Device == null || m_SwapChain == null)
            return;

        p_Width = Math.Max(1, p_Width);
        p_Height = Math.Max(1, p_Height);
        if (p_Width == m_Width && p_Height == m_Height)
            return;

        m_Width = p_Width;
        m_Height = p_Height;

        ReleaseSizeDependentResources();
        m_SwapChain.ResizeBuffers(2, m_Width, m_Height, Format.R8G8B8A8_UNorm, SwapChainFlags.None);
        CreateSizeDependentResources();
    }

    private void CreateSizeDependentResources()
    {
        var s_Device = m_Device!;

        if (m_SwapChain != null)
        {
            using var s_BackBuffer = m_SwapChain.GetBackBuffer<Texture2D>(0);
            m_Output = null;
            m_BackBufferView = new RenderTargetView(s_Device, s_BackBuffer);
        }
        else
        {
            m_Output = new Texture2D(s_Device, new Texture2DDescription
            {
                Width = m_Width,
                Height = m_Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });

            m_BackBufferView = new RenderTargetView(s_Device, m_Output);
        }

        for (var i = 0; i < c_TargetCount; i++)
        {
            m_Targets[i] = new Texture2D(s_Device, new Texture2DDescription
            {
                Width = m_Width,
                Height = m_Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });

            m_TargetViews[i] = new RenderTargetView(s_Device, m_Targets[i]);
            m_TargetSrvs[i] = new ShaderResourceView(s_Device, m_Targets[i]);
        }

        // Typeless so the same surface can be a depth target and a shader resource for the resolve pass.
        m_Depth = new Texture2D(s_Device, new Texture2DDescription
        {
            Width = m_Width,
            Height = m_Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R32_Typeless,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil | BindFlags.ShaderResource,
        });

        m_DepthView = new DepthStencilView(s_Device, m_Depth, new DepthStencilViewDescription
        {
            Format = Format.D32_Float,
            Dimension = DepthStencilViewDimension.Texture2D,
        });

        m_DepthSrv = new ShaderResourceView(s_Device, m_Depth, new ShaderResourceViewDescription
        {
            Format = Format.R32_Float,
            Dimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new ShaderResourceViewDescription.Texture2DResource { MipLevels = 1 },
        });
    }

    private void ReleaseSizeDependentResources()
    {
        m_BackBufferView?.Dispose();
        m_BackBufferView = null;
        m_Output?.Dispose();
        m_Output = null;

        for (var i = 0; i < c_TargetCount; i++)
        {
            m_TargetSrvs[i]?.Dispose();
            m_TargetViews[i]?.Dispose();
            m_Targets[i]?.Dispose();
            m_TargetSrvs[i] = null;
            m_TargetViews[i] = null;
            m_Targets[i] = null;
        }

        m_DepthSrv?.Dispose();
        m_DepthView?.Dispose();
        m_Depth?.Dispose();
        m_DepthSrv = null;
        m_DepthView = null;
        m_Depth = null;
    }

    public void Render()
    {
        if (m_Device == null || m_Context == null || m_BackBufferView == null)
            return;

        var s_Context = m_Context;
        var s_Camera = CameraPosition();
        var s_View = Matrix.LookAtLH(s_Camera, Target, Vector3.UnitY);
        // The near plane follows the dolly so extreme closeups do not clip into the mesh: at 3 metres it sits
        // at the old 0.05, right up close it tightens to millimetres.
        var s_Near = Math.Clamp(Distance * 0.02f, 0.002f, 0.05f);
        var s_Projection = Matrix.PerspectiveFovLH(0.9f, m_Width / (float) m_Height, s_Near, 100f);
        var s_ViewProj = s_View * s_Projection;

        var s_Light = Vector3.Normalize(new Vector3(
            (float) (Math.Cos(LightPitch) * Math.Sin(LightYaw)),
            (float) Math.Sin(LightPitch),
            (float) (Math.Cos(LightPitch) * Math.Cos(LightYaw))));

        WriteVsConstants(Matrix.Identity, s_ViewProj);
        WriteViewConstants(s_Camera);
        WriteLightConstants(s_ViewProj, s_Camera, s_Light);

        s_Context.Rasterizer.SetViewport(new Viewport(0, 0, m_Width, m_Height, 0f, 1f));
        s_Context.Rasterizer.State = m_Raster;
        s_Context.OutputMerger.DepthStencilState = m_DepthState;

        // Pass 1 -- the authored shader fills the four targets.
        var s_Views = new[] { m_TargetViews[0], m_TargetViews[1], m_TargetViews[2], m_TargetViews[3] };
        s_Context.OutputMerger.SetRenderTargets(m_DepthView, s_Views);

        foreach (var s_TargetView in s_Views)
            s_Context.ClearRenderTargetView(s_TargetView, new RawColor4(0, 0, 0, 0));

        s_Context.ClearDepthStencilView(m_DepthView, DepthStencilClearFlags.Depth, 1.0f, 0);

        if (m_AuthoredPs != null)
        {
            s_Context.InputAssembler.InputLayout = m_CubeLayout;
            s_Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            s_Context.InputAssembler.SetVertexBuffers(0,
                new VertexBufferBinding(m_CubeVertices, Utilities.SizeOf<Vertex>(), 0));
            s_Context.InputAssembler.SetIndexBuffer(m_CubeIndices, m_IndexFormat, 0);

            s_Context.VertexShader.Set(m_CubeVs);
            s_Context.VertexShader.SetConstantBuffer(0, m_VsConstants);

            s_Context.PixelShader.Set(m_AuthoredPs);
            s_Context.PixelShader.SetConstantBuffer(0, m_GlobalConstants);
            s_Context.PixelShader.SetConstantBuffer(1, m_ParameterConstants);
            s_Context.PixelShader.SetConstantBuffer(2, m_ViewConstants);
            // ⛔ EVERY sampler slot, not just s0. A shader that fetches through s1 - decals do, and so do several
            // terrain passes - otherwise ran against an UNBOUND sampler in the game's version (D3D11's default
            // state, which clamps) while ours, which only ever declares sampler0, ran against this one, which
            // wraps. Same defect as leaving cb0 unbound: the two halves of the differential test quietly got
            // different inputs, and it surfaced as a 2/255 difference at the edges.
            // ⛔ ALL SIXTEEN, not the first four. The terrain declares five samplers and fetches through s4, so a
            // four-slot loop left that one on D3D11's default state for the game's shader while ours ran on this
            // one - the same asymmetry as the unbound cb0, found the same way, and there is no reason to keep
            // guessing how many a shader might want.
            for (var s_Sampler = 0; s_Sampler < 16; s_Sampler++)
                s_Context.PixelShader.SetSampler(s_Sampler, m_Sampler);

            // Rebound every frame: the resolve pass below steals t0..t4 for the g-buffer and then unbinds them.
            for (var s_Slot = 1; s_Slot < c_TextureSlots; s_Slot++)
                s_Context.PixelShader.SetShaderResource(s_Slot, m_Textures[s_Slot]);

            // A loaded game mesh draws PER MATERIAL SECTION. Sections worn by the edited shader run the
            // authored pixel shader; sections worn by a REGISTERED foreign shader run that shader's own
            // game bytecode with its own art and its own contract-matched vertex feed; anything else falls
            // back to a neutral stand-in (opaque sections only) or is skipped. FORWARD sections — the
            // edited shader's included — are queued and composited AFTER the resolve, the way the engine
            // orders its passes, so a glass canopy blends over the lit body instead of into the GBuffer.
            m_ForwardQueue.Clear();
            if (m_Shape == PreviewShape.Mesh && m_MeshSections.Count > 0 && MeshTargetShader.Length > 0)
            {
                var s_Target = MeshTargetShader.Replace('\\', '/');
                foreach (var s_Section in m_MeshSections)
                {
                    var s_Mine = s_Section.Shader.Equals(s_Target, StringComparison.OrdinalIgnoreCase);
                    var s_Foreign = !s_Mine && m_ForeignShaders.TryGetValue(s_Section.Shader, out var s_Found)
                        ? s_Found
                        : null;

                    if (!s_Mine && HideForeignSections)
                        continue;

                    if (s_Mine ? m_ForwardOutput : s_Foreign?.Forward == true)
                    {
                        m_ForwardQueue.Add(s_Section);
                        continue;
                    }

                    s_Context.Rasterizer.State = s_Section.DoubleSided ? m_RasterNoCull : m_Raster;

                    if (s_Mine)
                    {
                        s_Context.PixelShader.Set(m_AuthoredPs);
                        s_Context.DrawIndexed(s_Section.IndexCount, s_Section.StartIndex, 0);
                    }
                    else if (s_Foreign != null)
                    {
                        DrawSectionWith(s_Context, s_Foreign, s_Section);
                        RestoreAuthoredBindings(s_Context);
                    }
                    else if (s_Section.Category == 0)
                    {
                        // No real shader for it (yet): opaque sections keep the silhouette with the
                        // neutral stand-in; a transparent one is omitted — an opaque grey canopy or
                        // rotor disc OCCLUDES the object it belongs to.
                        s_Context.PixelShader.Set(m_ForwardOutput ? m_NeutralForwardPs : m_NeutralGBufferPs);
                        s_Context.DrawIndexed(s_Section.IndexCount, s_Section.StartIndex, 0);
                    }
                }

                s_Context.Rasterizer.State = m_Raster;
            }
            else
            {
                s_Context.DrawIndexed(m_IndexCount, 0, 0);
            }
        }

        // Pass 2 -- light what was written. In sectioned-mesh mode the resolve is ALWAYS the GBuffer one:
        // forward sections (the edited shader's included) composite over it in pass 3, the engine's order.
        var s_Sectioned = m_Shape == PreviewShape.Mesh && m_MeshSections.Count > 0 && MeshTargetShader.Length > 0;

        s_Context.OutputMerger.SetRenderTargets((DepthStencilView?) null, m_BackBufferView);
        s_Context.ClearRenderTargetView(m_BackBufferView, new RawColor4(0.05f, 0.05f, 0.06f, 1f));

        s_Context.InputAssembler.InputLayout = null;
        s_Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        s_Context.VertexShader.Set(m_ResolveVs);
        s_Context.PixelShader.Set(m_ForwardOutput && !s_Sectioned ? m_ForwardResolvePs : m_ResolvePs);
        s_Context.PixelShader.SetConstantBuffer(0, m_LightConstants);
        s_Context.PixelShader.SetSampler(0, m_Sampler);

        for (var i = 0; i < c_TargetCount; i++)
            s_Context.PixelShader.SetShaderResource(i, m_TargetSrvs[i]);

        s_Context.PixelShader.SetShaderResource(4, m_DepthSrv);
        s_Context.Draw(3, 0);

        // Unbind so the targets are writable again next frame. The whole range, not just the five the resolve
        // pass used: pass 1 now binds up to t15 and a slot left bound is a slot D3D11 refuses to render into.
        for (var i = 0; i < c_TextureSlots; i++)
            s_Context.PixelShader.SetShaderResource(i, null);

        // Pass 3 -- forward sections composite over the lit result: premultiplied blend against the
        // backbuffer, tested against (but not writing) pass 1's depth, exactly the engine's pass order.
        if (m_ForwardQueue.Count > 0)
        {
            var s_Target = MeshTargetShader.Replace('\\', '/');
            s_Context.OutputMerger.SetRenderTargets(m_DepthView, m_BackBufferView);
            s_Context.OutputMerger.DepthStencilState = m_DepthReadState;
            s_Context.OutputMerger.BlendState = m_ForwardBlend;

            s_Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            s_Context.InputAssembler.SetVertexBuffers(0,
                new VertexBufferBinding(m_CubeVertices, Utilities.SizeOf<Vertex>(), 0));
            s_Context.InputAssembler.SetIndexBuffer(m_CubeIndices, m_IndexFormat, 0);

            foreach (var s_Section in m_ForwardQueue)
            {
                s_Context.Rasterizer.State = s_Section.DoubleSided ? m_RasterNoCull : m_Raster;

                if (s_Section.Shader.Equals(s_Target, StringComparison.OrdinalIgnoreCase))
                {
                    RestoreAuthoredBindings(s_Context);
                    s_Context.DrawIndexed(s_Section.IndexCount, s_Section.StartIndex, 0);
                }
                else if (m_ForeignShaders.TryGetValue(s_Section.Shader, out var s_Foreign))
                {
                    DrawSectionWith(s_Context, s_Foreign, s_Section);
                }
            }

            s_Context.Rasterizer.State = m_Raster;
            s_Context.OutputMerger.BlendState = null;
            s_Context.OutputMerger.DepthStencilState = m_DepthState;

            for (var i = 0; i < c_TextureSlots; i++)
                s_Context.PixelShader.SetShaderResource(i, null);
        }

        m_SwapChain?.Present(1, PresentFlags.None);
    }

    private readonly List<MeshSection> m_ForwardQueue = new();

    /// <summary>Full per-draw bindings for one foreign-shader section: its VS, layout, PS and textures.</summary>
    private void DrawSectionWith(DeviceContext p_Context, ForeignShader p_Foreign, MeshSection p_Section)
    {
        p_Context.InputAssembler.InputLayout = p_Foreign.Layout;
        p_Context.VertexShader.Set(p_Foreign.Vs);
        p_Context.VertexShader.SetConstantBuffer(0, m_VsConstants);
        p_Context.PixelShader.Set(p_Foreign.Ps);
        p_Context.PixelShader.SetConstantBuffer(0, m_GlobalConstants);
        p_Context.PixelShader.SetConstantBuffer(1, m_ParameterConstants);
        p_Context.PixelShader.SetConstantBuffer(2, m_ViewConstants);

        // Bound AFTER the defaults so a shader whose externals live in cb1 replaces the authored pattern.
        if (p_Foreign.Params != null)
            p_Context.PixelShader.SetConstantBuffer(p_Foreign.ParamsRegister, p_Foreign.Params);

        for (var s_Slot = 1; s_Slot < c_TextureSlots; s_Slot++)
            p_Context.PixelShader.SetShaderResource(s_Slot,
                p_Foreign.Textures[s_Slot] ?? m_Textures[s_Slot]);

        p_Context.DrawIndexed(p_Section.IndexCount, p_Section.StartIndex, 0);
    }

    /// <summary>Puts the ACTIVE shader's bindings back after a foreign section borrowed the pipeline.</summary>
    private void RestoreAuthoredBindings(DeviceContext p_Context)
    {
        p_Context.InputAssembler.InputLayout = m_CubeLayout;
        p_Context.VertexShader.Set(m_CubeVs);
        p_Context.VertexShader.SetConstantBuffer(0, m_VsConstants);
        p_Context.PixelShader.Set(m_AuthoredPs);
        p_Context.PixelShader.SetConstantBuffer(0, m_GlobalConstants);
        p_Context.PixelShader.SetConstantBuffer(1, m_ParameterConstants);
        p_Context.PixelShader.SetConstantBuffer(2, m_ViewConstants);

        for (var s_Slot = 1; s_Slot < c_TextureSlots; s_Slot++)
            p_Context.PixelShader.SetShaderResource(s_Slot, m_Textures[s_Slot]);
    }

    private void Upload<T>(Buffer p_Buffer, T[] p_Data) where T : struct
    {
        m_Context!.MapSubresource(p_Buffer, MapMode.WriteDiscard, MapFlags.None, out var s_Stream);
        s_Stream.WriteRange(p_Data);
        m_Context.UnmapSubresource(p_Buffer, 0);
    }

    private void WriteVsConstants(Matrix p_World, Matrix p_ViewProj) =>
        Upload(m_VsConstants!, new[] { Matrix.Transpose(p_World), Matrix.Transpose(p_ViewProj) });

    /// <summary>
    /// Fills the authored shader's cbuffer at the offsets measured on the vanilla shader: time at c0.x,
    /// screenSize at c1, viewportZMinMaxKzKw at c19 and cameraPos at c20. An unfilled field here is a silent
    /// no-op inside the shader, so every slot the layout declares is written.
    /// </summary>
    private void WriteViewConstants(Vector3 p_Camera)
    {
        var s_Values = new float[21 * 4];
        s_Values[0] = Time;

        s_Values[4] = m_Width;
        s_Values[5] = m_Height;
        s_Values[6] = 1f / m_Width;
        s_Values[7] = 1f / m_Height;

        s_Values[19 * 4 + 0] = 0.05f;
        s_Values[19 * 4 + 1] = 100f;

        s_Values[20 * 4 + 0] = p_Camera.X;
        s_Values[20 * 4 + 1] = p_Camera.Y;
        s_Values[20 * 4 + 2] = p_Camera.Z;

        Upload(m_ViewConstants!, s_Values);
    }

    private void WriteLightConstants(Matrix p_ViewProj, Vector3 p_Camera, Vector3 p_Light)
    {
        var s_Inverse = Matrix.Invert(p_ViewProj);
        s_Inverse.Transpose();

        var s_Values = new float[16 + 5 * 4];
        for (var i = 0; i < 16; i++)
            s_Values[i] = s_Inverse[i];

        void Put(int p_Slot, float p_X, float p_Y, float p_Z, float p_W)
        {
            var s_Base = 16 + p_Slot * 4;
            s_Values[s_Base + 0] = p_X;
            s_Values[s_Base + 1] = p_Y;
            s_Values[s_Base + 2] = p_Z;
            s_Values[s_Base + 3] = p_W;
        }

        Put(0, p_Light.X, p_Light.Y, p_Light.Z, 0f);
        Put(1, 1.0f, 0.97f, 0.92f, 0f);
        Put(2, 0.38f, 0.45f, 0.58f, 0f);
        Put(3, 0.16f, 0.15f, 0.14f, 0f);
        Put(4, p_Camera.X, p_Camera.Y, p_Camera.Z, Exposure);

        Upload(m_LightConstants!, s_Values);
    }

    public void Dispose()
    {
        ReleaseSizeDependentResources();

        foreach (var s_Texture in m_Textures)
            s_Texture?.Dispose();
        m_AuthoredPs?.Dispose();
        m_ResolvePs?.Dispose();
        m_ForwardResolvePs?.Dispose();
        m_NeutralGBufferPs?.Dispose();
        m_NeutralForwardPs?.Dispose();
        m_DepthReadState?.Dispose();
        m_ForwardBlend?.Dispose();
        m_RasterNoCull?.Dispose();
        foreach (var s_Foreign in m_ForeignShaders.Values)
            s_Foreign.Dispose();
        m_ForeignShaders.Clear();
        m_ResolveVs?.Dispose();
        m_CubeLayout?.Dispose();
        m_CubeVs?.Dispose();
        m_DepthState?.Dispose();
        m_Raster?.Dispose();
        m_Sampler?.Dispose();
        m_LightConstants?.Dispose();
        m_ViewConstants?.Dispose();
        m_ParameterConstants?.Dispose();
        m_GlobalConstants?.Dispose();
        m_VsConstants?.Dispose();
        m_CubeIndices?.Dispose();
        m_CubeVertices?.Dispose();
        m_SwapChain?.Dispose();
        m_Device?.Dispose();

        m_Device = null;
        m_Context = null;
        m_SwapChain = null;
    }
}
