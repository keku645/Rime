using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimeLib;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.Shader;

namespace RimeLib.Cmd.Commands.Game
{
    [CommandDescription("Extracts the vertex + pixel shader DXBC of a shader (by name substring) from a shaderdb to " +
                        ".dxbc files (one pair per solution). Use to inspect a level shader's VS output signature / " +
                        "interpolants and its compiled bytecode before authoring a replacement.")]
    public class ExtractShaderDxbcCommand : Command
    {
        [CommandArgument(Description = "The shaderdb resource name, e.g. levels/mp_017/mp_017/shaderdb")]
        public string? Name { get; set; }

        [CommandArgument(Description = "Shader name substring, e.g. Sea_Shader")]
        public string? Shader { get; set; }

        [CommandArgument(Description = "Output directory for the .dxbc files")]
        public DirectoryInfo? Output { get; set; }

        // LAST on purpose: an optional inserted before the final positional silently rebinds every existing
        // caller — the 7th-argument lesson from the clone command.
        [CommandArgument(Description = "Optional '1'/'true' = read the LAST-mounted variant of the resource " +
                                       "(a standalone-mounted mod database) instead of the game's.", Optional = true)]
        public bool Last { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Shader) || Output == null)
            {
                p_Writer.WriteLine("Usage: extract_shader_dxbc <shaderdb> <shader-substr> <out-dir>");
                return false;
            }

            var s_Mounter = ((GameContext)p_Context).GetMounter();
            if (!s_Mounter.TryGetResource(Name!, out var s_Resource) || s_Resource.FirstVariant == null)
            {
                p_Writer.WriteLine($"Could not find shaderdb resource ({Name}).");
                return false;
            }

            // Same law as dump_resource: a name shared with a mounted standalone sb answers with the
            // GAME's variant first — extracting from a MOD's database needs the LAST-mounted one.
            var s_Variant = Last ? System.Linq.Enumerable.Last(s_Resource.Variants) : s_Resource.FirstVariant;

            var s_Resolver = EngineInterfaceRegistry.Create<IShaderResolver>(s_Mounter.GetEngineType());
            s_Resolver.Initialize(s_Variant, s_Mounter);
            var s_Container = s_Resolver.GetType().GetProperty("ShaderDatabaseContainer")?.GetValue(s_Resolver);
            var s_Databases = s_Container?.GetType().GetProperty("Databases")?.GetValue(s_Container) as IDictionary;
            if (s_Databases == null) { p_Writer.WriteLine("null databases."); return false; }

            if (!Output.Exists) Output.Create();
            string s_Needle = Shader!.ToLowerInvariant();
            int s_Wrote = 0;

            // The file name carries only the render path and solution index, so several shaders would all write
            // "Dx11_sol0_ps.dxbc" and silently overwrite each other, leaving whichever came last. Count the
            // matches first: one shader keeps the original flat layout (existing command files depend on it),
            // several get a numbered sub-directory each plus an index mapping number -> shader name. Numbered
            // rather than named because shader names are long and the full path hits MAX_PATH.
            var s_Matches = new List<string>();
            foreach (var s_PathKey0 in s_Databases.Keys)
            {
                var s_Db0 = s_Databases[s_PathKey0];
                var s_Shaders0 = s_Db0?.GetType().GetProperty("Shaders")?.GetValue(s_Db0) as IDictionary;
                if (s_Shaders0 == null) continue;

                foreach (var s_ShKey0 in s_Shaders0.Keys)
                {
                    var s_NameText = s_ShKey0?.ToString() ?? "";
                    if (s_NameText.ToLowerInvariant().Contains(s_Needle) && !s_Matches.Contains(s_NameText))
                        s_Matches.Add(s_NameText);
                }
            }

            if (s_Matches.Count == 0)
            {
                p_Writer.WriteLine($"No shader name matched '{Shader}'.");
                return false;
            }

            bool s_Split = s_Matches.Count > 1;
            if (s_Split)
            {
                var s_IndexLines = new List<string>();
                for (int i = 0; i < s_Matches.Count; i++)
                    s_IndexLines.Add(i.ToString("D4") + "\t" + s_Matches[i]);

                File.WriteAllLines(Path.Combine(Output.FullName, "index.txt"), s_IndexLines);
                p_Writer.WriteLine($"{s_Matches.Count} shaders matched; writing one sub-directory each " +
                                   "(index.txt maps the number to the shader name).");
            }

            foreach (var s_PathKey in s_Databases.Keys)
            {
                var s_Db = s_Databases[s_PathKey];
                var s_Shaders = s_Db?.GetType().GetProperty("Shaders")?.GetValue(s_Db) as IDictionary;
                if (s_Shaders == null) continue;

                foreach (var s_ShKey in s_Shaders.Keys)
                {
                    var s_NameText = s_ShKey?.ToString() ?? "";
                    if (!s_NameText.ToLowerInvariant().Contains(s_Needle)) continue;
                    var s_Info = s_Shaders[s_ShKey];
                    var s_Sols = s_Info?.GetType().GetProperty("Solutions")?.GetValue(s_Info) as Array;
                    if (s_Sols == null) continue;

                    var s_Dir = Output;
                    if (s_Split)
                    {
                        s_Dir = new DirectoryInfo(Path.Combine(Output.FullName,
                            s_Matches.IndexOf(s_NameText).ToString("D4")));

                        if (!s_Dir.Exists) s_Dir.Create();
                    }

                    // The authored SURFACE facts the bytecode alone cannot carry: the shaderdb's own
                    // SurfaceShaderType and whether any solution renders double-sided (Flags bit 1). A
                    // translated graph's root gets stamped from this, instead of defaulting to Opaque.
                    var s_Type = s_Info?.GetType().GetProperty("SurfaceShaderType")?.GetValue(s_Info);
                    var s_AnyDoubleSided = s_Sols.Cast<object?>().Any(p_S =>
                        p_S?.GetType().GetProperty("Flags")?.GetValue(p_S) is byte s_Flags && (s_Flags & 1) != 0);
                    File.WriteAllText(Path.Combine(s_Dir.FullName, "shaderinfo.txt"),
                        $"type={s_Type}\ndoubleSided={(s_AnyDoubleSided ? 1 : 0)}\n");

                    int s_Idx = 0;
                    foreach (var s_Sol in s_Sols)
                    {
                        string s_Tag = s_PathKey + "_sol" + s_Idx;
                        Dump(s_Sol, "VertexPermutation", s_Dir, s_Tag + "_vs.dxbc", p_Writer, ref s_Wrote);
                        Dump(s_Sol, "PixelPermutation", s_Dir, s_Tag + "_ps.dxbc", p_Writer, ref s_Wrote);
                        s_Idx++;
                    }
                }
            }
            p_Writer.WriteLine($"Wrote {s_Wrote} .dxbc file(s) from {s_Matches.Count} shader(s) to {Output.FullName}");
            return s_Wrote > 0;
        }

        static void Dump(object? p_Sol, string p_PermProp, DirectoryInfo p_Dir, string p_File, TextWriter p_Writer, ref int p_Count)
        {
            var s_Perm = p_Sol?.GetType().GetProperty(p_PermProp)?.GetValue(p_Sol);
            if (s_Perm == null) return;
            if (s_Perm.GetType().GetProperty("ShaderBytecode")?.GetValue(s_Perm) is byte[] s_Bytes && s_Bytes.Length > 0)
            {
                var s_Path = Path.Combine(p_Dir.FullName, p_File);
                File.WriteAllBytes(s_Path, s_Bytes);
                p_Writer.WriteLine($"  {p_File}: {s_Bytes.Length} bytes");
                p_Count++;
            }
        }
    }
}
