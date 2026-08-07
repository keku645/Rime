using System;
using System.Collections;
using System.IO;
using System.Reflection;
using RimeLib;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.IO;
using RimeLib.IO.Conversion;
using RimeLib.Shader;

namespace RimeLib.Cmd.Commands.Game
{
    [CommandDescription("Replaces the pixel-shader DXBC of every solution of a shader (by name substring) in a shaderdb " +
                        "with the bytes of a .dxbc file, and writes the whole modified shaderdb container to an output " +
                        "file. Use to inject a custom compiled shader over a level's shader (e.g. MP_017 Sea_Shader).")]
    public class ReplaceShaderBytecodeCommand : Command
    {
        [CommandArgument(Description = "The shaderdb resource name, e.g. levels/mp_017/mp_017/shaderdb")]
        public string? Name { get; set; }

        [CommandArgument(Description = "Shader name substring to target, e.g. Sea_Shader")]
        public string? Shader { get; set; }

        [CommandArgument(Description = "Path to the new compiled pixel shader (.dxbc)")]
        public FileInfo? Dxbc { get; set; }

        [CommandArgument(Description = "Output shaderdb file path")]
        public FileInfo? Output { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Shader) || Dxbc == null || Output == null)
            {
                p_Writer.WriteLine("Usage: replace_shader_bytecode <shaderdb> <shader-substr> <dxbc-file> <out-file>");
                return false;
            }
            if (!Dxbc.Exists) { p_Writer.WriteLine($"DXBC file not found: {Dxbc.FullName}"); return false; }

            var s_Mounter = ((GameContext)p_Context).GetMounter();
            if (!s_Mounter.TryGetResource(Name!, out var s_Resource) || s_Resource.FirstVariant == null)
            {
                p_Writer.WriteLine($"Could not find shaderdb resource ({Name}).");
                return false;
            }

            var s_Resolver = EngineInterfaceRegistry.Create<IShaderResolver>(s_Mounter.GetEngineType());
            s_Resolver.Initialize(s_Resource.FirstVariant, s_Mounter);
            var s_Container = s_Resolver.GetType().GetProperty("ShaderDatabaseContainer")?.GetValue(s_Resolver);
            if (s_Container == null) { p_Writer.WriteLine("Shaderdb parsed to null."); return false; }
            var s_Databases = s_Container.GetType().GetProperty("Databases")?.GetValue(s_Container) as IDictionary;
            if (s_Databases == null) { p_Writer.WriteLine("No databases."); return false; }

            byte[] s_NewDxbc = File.ReadAllBytes(Dxbc.FullName);
            if (s_NewDxbc.Length < 4 || s_NewDxbc[0] != 0x44 || s_NewDxbc[1] != 0x58 || s_NewDxbc[2] != 0x42 || s_NewDxbc[3] != 0x43)
                p_Writer.WriteLine("WARNING: file does not start with the 'DXBC' magic.");

            string s_Needle = Shader!.ToLowerInvariant();
            int s_Replaced = 0;

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

                    foreach (var s_Sol in s_Sols)
                    {
                        var s_Pp = s_Sol?.GetType().GetProperty("PixelPermutation")?.GetValue(s_Sol);
                        if (s_Pp == null) continue;
                        var s_ByteProp = s_Pp.GetType().GetProperty("ShaderBytecode");
                        if (s_ByteProp == null || !s_ByteProp.CanWrite) continue;
                        s_ByteProp.SetValue(s_Pp, s_NewDxbc);
                        s_Replaced++;
                    }
                    p_Writer.WriteLine($"  [{s_PathKey}] {s_ShKey}: patched pixel permutations");
                }
            }

            if (s_Replaced == 0) { p_Writer.WriteLine($"No pixel permutation matched '{Shader}'."); return false; }

            var s_Serialize = s_Container.GetType().GetMethod("Serialize", new[] { typeof(RimeWriter) });
            if (s_Serialize == null) { p_Writer.WriteLine("Container has no Serialize(RimeWriter)."); return false; }

            if (Output.Directory != null && !Output.Directory.Exists) Output.Directory.Create();
            using (var s_Fs = File.Create(Output.FullName))
            using (var s_W = new RimeWriter(s_Fs, Endianness.LittleEndian, false))
                s_Serialize.Invoke(s_Container, new object[] { s_W });

            p_Writer.WriteLine($"Replaced {s_Replaced} pixel permutation(s) with {s_NewDxbc.Length}-byte DXBC.");
            p_Writer.WriteLine($"Wrote modified shaderdb -> {Output.FullName} ({new FileInfo(Output.FullName).Length} bytes)");
            return true;
        }
    }
}
