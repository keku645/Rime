using System;
using System.Collections;
using System.Collections.Generic;
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
    [CommandDescription("Replaces the VERTEX-shader DXBC of every solution of a shader (by name substring) in a shaderdb " +
                        "with the bytes of a .dxbc file, and writes the whole modified shaderdb container to an output " +
                        "file. The stored InputSignatureBytecode/Elements are NOT touched (the engine builds the input " +
                        "layout from them), so the new VS must declare the same input semantics as the stock one.")]
    public class ReplaceVertexShaderBytecodeCommand : Command
    {
        [CommandArgument(Description = "The shaderdb resource name, e.g. levels/mp_017/mp_017/shaderdb")]
        public string? Name { get; set; }

        [CommandArgument(Description = "Shader name substring to target, e.g. Sea_Shader")]
        public string? Shader { get; set; }

        [CommandArgument(Description = "Path to the new compiled vertex shader (.dxbc)")]
        public FileInfo? Dxbc { get; set; }

        [CommandArgument(Description = "Output shaderdb file path")]
        public FileInfo? Output { get; set; }

        [CommandArgument(Description = "Optional INPUT shaderdb file to modify instead of the mounted resource's bytes " +
                                       "(e.g. a shaderdb .bin that already had a custom PS injected). The mounted game " +
                                       "content is still required: shader entry names resolve against mounted partitions.",
                         Optional = true)]
        public FileInfo? Input { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Shader) || Dxbc == null || Output == null)
            {
                p_Writer.WriteLine("Usage: replace_vertex_shader_bytecode <shaderdb> <shader-substr> <dxbc-file> <out-file> [in-file]");
                return false;
            }
            if (!Dxbc.Exists) { p_Writer.WriteLine($"DXBC file not found: {Dxbc.FullName}"); return false; }

            var s_Mounter = ((GameContext)p_Context).GetMounter();
            var s_Resolver = EngineInterfaceRegistry.Create<IShaderResolver>(s_Mounter.GetEngineType());

            object? s_Container;
            if (Input != null)
            {
                // Parse the container from a FILE (a previously modified shaderdb .bin) — same pattern as
                // shader_db_add_texture, so this command chains after the PS/texture injections.
                if (!Input.Exists) { p_Writer.WriteLine($"Input file not found: {Input.FullName}"); return false; }
                var s_ContainerType = s_Resolver.GetType().GetProperty("ShaderDatabaseContainer")?.PropertyType;
                if (s_ContainerType == null) { p_Writer.WriteLine("Resolver exposes no ShaderDatabaseContainer."); return false; }

                using var s_Ms = new MemoryStream(File.ReadAllBytes(Input.FullName));
                using var s_Rd = new RimeReader(s_Ms, Endianness.LittleEndian, false);
                s_Container = Activator.CreateInstance(s_ContainerType, s_Rd, s_Mounter);
                p_Writer.WriteLine($"Parsed input shaderdb file: {Input.FullName} ({s_Ms.Length} bytes)");
            }
            else
            {
                if (!s_Mounter.TryGetResource(Name!, out var s_Resource) || s_Resource.FirstVariant == null)
                {
                    p_Writer.WriteLine($"Could not find shaderdb resource ({Name}).");
                    return false;
                }
                s_Resolver.Initialize(s_Resource.FirstVariant, s_Mounter);
                s_Container = s_Resolver.GetType().GetProperty("ShaderDatabaseContainer")?.GetValue(s_Resolver);
            }
            if (s_Container == null) { p_Writer.WriteLine("Shaderdb parsed to null."); return false; }
            var s_Databases = s_Container.GetType().GetProperty("Databases")?.GetValue(s_Container) as IDictionary;
            if (s_Databases == null) { p_Writer.WriteLine("No databases."); return false; }

            byte[] s_NewDxbc = File.ReadAllBytes(Dxbc.FullName);
            if (s_NewDxbc.Length < 4 || s_NewDxbc[0] != 0x44 || s_NewDxbc[1] != 0x58 || s_NewDxbc[2] != 0x42 || s_NewDxbc[3] != 0x43)
                p_Writer.WriteLine("WARNING: file does not start with the 'DXBC' magic.");

            string s_Needle = Shader!.ToLowerInvariant();
            // Solutions can share permutation instances (the database stores them by index) — dedup by
            // reference so a shared VertexPermutation is only patched/reported once.
            var s_Seen = new HashSet<object>();
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
                        var s_Vp = s_Sol?.GetType().GetProperty("VertexPermutation")?.GetValue(s_Sol);
                        if (s_Vp == null || !s_Seen.Add(s_Vp)) continue;
                        var s_ByteProp = s_Vp.GetType().GetProperty("ShaderBytecode");
                        if (s_ByteProp == null || !s_ByteProp.CanWrite) continue;
                        s_ByteProp.SetValue(s_Vp, s_NewDxbc);
                        s_Replaced++;
                    }
                    p_Writer.WriteLine($"  [{s_PathKey}] {s_ShKey}: patched vertex permutations");
                }
            }

            if (s_Replaced == 0) { p_Writer.WriteLine($"No vertex permutation matched '{Shader}'."); return false; }

            var s_Serialize = s_Container.GetType().GetMethod("Serialize", new[] { typeof(RimeWriter) });
            if (s_Serialize == null) { p_Writer.WriteLine("Container has no Serialize(RimeWriter)."); return false; }

            if (Output.Directory != null && !Output.Directory.Exists) Output.Directory.Create();
            using (var s_Fs = File.Create(Output.FullName))
            using (var s_W = new RimeWriter(s_Fs, Endianness.LittleEndian, false))
                s_Serialize.Invoke(s_Container, new object[] { s_W });

            p_Writer.WriteLine($"Replaced {s_Replaced} vertex permutation(s) with {s_NewDxbc.Length}-byte DXBC.");
            p_Writer.WriteLine($"Wrote modified shaderdb -> {Output.FullName} ({new FileInfo(Output.FullName).Length} bytes)");
            return true;
        }
    }
}
