using System;
using System.Collections;
using System.IO;
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

            var s_Resolver = EngineInterfaceRegistry.Create<IShaderResolver>(s_Mounter.GetEngineType());
            s_Resolver.Initialize(s_Resource.FirstVariant, s_Mounter);
            var s_Container = s_Resolver.GetType().GetProperty("ShaderDatabaseContainer")?.GetValue(s_Resolver);
            var s_Databases = s_Container?.GetType().GetProperty("Databases")?.GetValue(s_Container) as IDictionary;
            if (s_Databases == null) { p_Writer.WriteLine("null databases."); return false; }

            if (!Output.Exists) Output.Create();
            string s_Needle = Shader!.ToLowerInvariant();
            int s_Wrote = 0;

            foreach (var s_PathKey in s_Databases.Keys)
            {
                var s_Db = s_Databases[s_PathKey];
                var s_Shaders = s_Db?.GetType().GetProperty("Shaders")?.GetValue(s_Db) as IDictionary;
                if (s_Shaders == null) continue;

                foreach (var s_ShKey in s_Shaders.Keys)
                {
                    if (!(s_ShKey?.ToString() ?? "").ToLowerInvariant().Contains(s_Needle)) continue;
                    var s_Info = s_Shaders[s_ShKey];
                    var s_Sols = s_Info?.GetType().GetProperty("Solutions")?.GetValue(s_Info) as Array;
                    if (s_Sols == null) continue;

                    int s_Idx = 0;
                    foreach (var s_Sol in s_Sols)
                    {
                        string s_Tag = s_PathKey + "_sol" + s_Idx;
                        Dump(s_Sol, "VertexPermutation", Output, s_Tag + "_vs.dxbc", p_Writer, ref s_Wrote);
                        Dump(s_Sol, "PixelPermutation", Output, s_Tag + "_ps.dxbc", p_Writer, ref s_Wrote);
                        s_Idx++;
                    }
                }
            }
            p_Writer.WriteLine($"Wrote {s_Wrote} .dxbc file(s) to {Output.FullName}");
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
