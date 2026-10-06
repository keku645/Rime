using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimeLib;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.Mesh.Frostbite;
using RimeLib.Shader;

namespace RimeLib.Cmd.Commands.Game
{
    [CommandDescription("Dumps the per-SOLUTION state of a shader in a shaderdb: Technique / ColorScale / " +
                        "BoolPermutation / Mode / StateHash for every solution. Use to check WHICH techniques " +
                        "(e.g. camo vs grey) a level's shaderdb actually compiled for a given vehicle shader.")]
    public class DumpShaderSolutionsCommand : Command
    {
        [CommandArgument(Description = "The shaderdb resource name, e.g. levels/mp_017/mp_017/shaderdb")]
        public string? Name { get; set; }

        [CommandArgument(Description = "Shader name (or substring), e.g. Vehicles/M1A2/M1A2_Frame_Main")]
        public string? Shader { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Shader))
            {
                p_Writer.WriteLine("Usage: dump_shader_solutions <resource> <shader-name-or-substring>");
                return false;
            }

            var s_Mounter = ((GameContext)p_Context).GetMounter();
            if (!s_Mounter.TryGetResource(Name!, out var s_Resource) || s_Resource.FirstVariant == null)
            {
                p_Writer.WriteLine($"Could not find shaderdb resource ({Name}).");
                return false;
            }

            var s_Resolver = EngineInterfaceRegistry.Create<IShaderResolver>(s_Mounter.GetEngineType());
            s_Resolver.Initialize(s_Resource.FirstVariant, s_Mounter);
            var s_Container = s_Resolver.GetType().GetProperty("ShaderDatabaseContainer")?.GetValue(s_Resolver);
            var s_Databases = s_Container?.GetType().GetProperty("Databases")?.GetValue(s_Container) as IDictionary;
            if (s_Databases == null)
            {
                p_Writer.WriteLine("Shaderdb parsed to null / no databases.");
                return false;
            }

            var s_Needle = Shader!.ToLowerInvariant();
            int s_Matched = 0;

            foreach (var s_PathKey in s_Databases.Keys)
            {
                var s_Db = s_Databases[s_PathKey];
                var s_Shaders = s_Db?.GetType().GetProperty("Shaders")?.GetValue(s_Db) as IDictionary;
                if (s_Shaders == null) continue;

                foreach (var s_ShKey in s_Shaders.Keys)
                {
                    var s_ShName = s_ShKey?.ToString() ?? "";
                    if (!s_ShName.ToLowerInvariant().Contains(s_Needle)) continue;
                    s_Matched++;

                    var s_Info = s_Shaders[s_ShKey];
                    var s_Sols = s_Info?.GetType().GetProperty("Solutions")?.GetValue(s_Info) as System.Array;
                    var s_Type = s_Info?.GetType().GetProperty("SurfaceShaderType")?.GetValue(s_Info);
                    p_Writer.WriteLine($"SHSOL-SHADER: [{s_PathKey}] {s_ShName}  type={s_Type}  " +
                                       $"solutions={(s_Sols?.Length ?? 0)}");
                    if (s_Sols == null) continue;

                    // The vertex declarations this shader can draw: one line per distinct decl hash with its
                    // elements, so a mesh's MESHDECL (dump_mesh_sections) can be checked against them.
                    var s_Decls = new SortedDictionary<uint, string>();

                    foreach (var s_Sol in s_Sols)
                    {
                        var s_HashObj = s_Sol!.GetType().GetProperty("StateHash")?.GetValue(s_Sol);
                        var s_State = s_Sol.GetType().GetProperty("State")?.GetValue(s_Sol);
                        var s_Flags = s_Sol.GetType().GetProperty("Flags")?.GetValue(s_Sol);
                        object? Get(string p_N) => s_State?.GetType().GetProperty(p_N)?.GetValue(s_State);
                        p_Writer.WriteLine(
                            $"  SHSOL: tech={Get("Technique")} colorScale={Get("ColorScale")} " +
                            $"boolPerm={Get("BoolPermutation")} mode={Get("Mode")} " +
                            $"objLight={Get("ObjectLighting")} decl=0x{Get("GeometryDeclarationHash"):X8} " +
                            $"inst={Get("InstancingMethod")} flags={s_Flags} stateHash=0x{s_HashObj:X}");

                        if (Get("GeometryDeclarationHash") is uint s_DeclHash && !s_Decls.ContainsKey(s_DeclHash))
                        {
                            var s_Desc = Get("GeometryDeclarationDesc") as GeometryDeclarationDesc;

                            // The D3D11 input layout the runtime creates for this solution lives in its VERTEX
                            // permutation (semantic name/index, DXGI format, byte offset), independent of the
                            // declaration: a solution re-labelled to another declaration keeps these offsets.
                            var s_Vertex = s_Sol.GetType().GetProperty("VertexPermutation")?.GetValue(s_Sol);
                            var s_Elements = s_Vertex?.GetType().GetProperty("Elements")?.GetValue(s_Vertex) as System.Array;
                            var s_Layout = s_Elements == null ? "(no vertex permutation)" : string.Join(",", s_Elements.Cast<object>().Select(e =>
                            {
                                // SharpDX's InputElement exposes FIELDS, not properties.
                                var t = e.GetType();
                                object? F(string n) => t.GetField(n)?.GetValue(e) ?? t.GetProperty(n)?.GetValue(e);
                                return $"{F("SemanticName")}{F("SemanticIndex")}:{F("Format")}@{F("AlignedByteOffset")}";
                            }));

                            s_Decls[s_DeclHash] = (s_Desc == null
                                ? "(declaration not in this shaderdb)"
                                : string.Join(",", s_Desc.Elements
                                    .Where(e => e.Usage != fb.VertexElementUsage.VertexElementUsage_Unknown)
                                    .Select(e => $"{e.Usage}:{e.Format}@{e.Offset}")) +
                                  $" stride={string.Join("/", s_Desc.Streams.Select(s => s.Stride))}") +
                                $" | VS input layout: {s_Layout}";
                        }
                    }

                    foreach (var (s_DeclHash, s_Elements) in s_Decls)
                        p_Writer.WriteLine($"  SHSOL-DECL: {s_ShName} decl=0x{s_DeclHash:X8} elements={s_Elements}");
                }
            }

            p_Writer.WriteLine($"SHSOL-DONE: {Name} '{Shader}' matched={s_Matched}");
            return true;
        }
    }
}
