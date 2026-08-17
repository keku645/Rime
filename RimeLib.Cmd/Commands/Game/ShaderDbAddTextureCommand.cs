using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using RimeLib;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.IO;
using RimeLib.IO.Conversion;
using RimeLib.Shader;

namespace RimeLib.Cmd.Commands.Game
{
    [CommandDescription("Adds a streamable-texture slot (TextureConstant, register <- asset name) to the PIXEL " +
                        "ShaderConstant of every shader matching a name substring in a shaderdb, and writes the whole " +
                        "modified container to an output file. The engine then loads + binds that texture at the given " +
                        "t-register natively, with no runtime hook. Prints the existing texture slots first (Index = " +
                        "t-register), so the mapping can be sanity-checked against the shader's disassembly.")]
    public class ShaderDbAddTextureCommand : Command
    {
        [CommandArgument(Description = "The shaderdb resource name, e.g. levels/mp_017/mp_017/shaderdb")]
        public string? Name { get; set; }

        [CommandArgument(Description = "Shader name substring to target, e.g. Sea_Shader")]
        public string? Shader { get; set; }

        [CommandArgument(Description = "Texture asset name to bind, e.g. Textures/Water/WaterFoam_01_D")]
        public string? Texture { get; set; }

        [CommandArgument(Description = "Pixel-shader texture register to bind it at (e.g. 8 for t8)")]
        public string? Register { get; set; }

        [CommandArgument(Description = "Output shaderdb file path")]
        public FileInfo? Output { get; set; }

        [CommandArgument(Description = "Optional INPUT shaderdb file to modify instead of the mounted resource's bytes " +
                                       "(e.g. a shaderdb .bin that already had a custom PS injected). The mounted game " +
                                       "content is still required: shader entry names resolve against mounted partitions.",
                         Optional = true)]
        public FileInfo? Input { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Shader) ||
                string.IsNullOrWhiteSpace(Texture) || string.IsNullOrWhiteSpace(Register) || Output == null)
            {
                p_Writer.WriteLine("Usage: shader_db_add_texture <shaderdb> <shader-substr> <texture-name> <register> <out-file>");
                return false;
            }

            if (!byte.TryParse(Register, out var s_Register))
            {
                p_Writer.WriteLine($"Invalid register '{Register}' (expected 0-255).");
                return false;
            }

            var s_Mounter = ((GameContext)p_Context).GetMounter();
            var s_Resolver = EngineInterfaceRegistry.Create<IShaderResolver>(s_Mounter.GetEngineType());

            object? s_Container;
            if (Input != null)
            {
                // Parse the container from a FILE (a previously modified shaderdb .bin). The container ctor still
                // resolves shader entry names against the MOUNTED partitions, so the level must be mounted.
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

            string s_Needle = Shader!.ToLowerInvariant();
            // Solutions share ShaderConstant instances (they reference the database's constant array by index),
            // so collect the DISTINCT constants across all matched solutions and patch each exactly once.
            var s_Seen = new HashSet<object>();
            int s_Patched = 0;

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
                        var s_Const = s_Sol?.GetType().GetProperty("PixelConstants")?.GetValue(s_Sol);
                        if (s_Const == null || !s_Seen.Add(s_Const)) continue;

                        p_Writer.WriteLine($"[{s_PathKey}] {s_ShKey}: pixel ShaderConstant existing texture slots:");
                        if (s_Const.GetType().GetProperty("Textures")?.GetValue(s_Const) is Array s_Tex)
                            foreach (var s_It in s_Tex)
                            {
                                object? G(string n) => s_It?.GetType().GetProperty(n)?.GetValue(s_It);
                                p_Writer.WriteLine($"    t{G("Index")} <- \"{G("Name")}\" ({G("TextureType")})");
                            }

                        var s_Add = s_Const.GetType().GetMethod("AddTextureConstant");
                        if (s_Add == null) { p_Writer.WriteLine("ShaderConstant has no AddTextureConstant (stale assembly?)."); return false; }

                        var s_Report = s_Add.Invoke(s_Const, new object[] { s_Register, (byte) 0 /* TextureType_2d */, Texture! }) as string;
                        p_Writer.WriteLine($"    + t{s_Register} <- \"{Texture}\": {s_Report}");
                        if (s_Report == null || s_Report.StartsWith("ERROR")) return false;
                        s_Patched++;
                    }
                }
            }

            if (s_Patched == 0) { p_Writer.WriteLine($"No shader matched '{Shader}'."); return false; }

            var s_Serialize = s_Container.GetType().GetMethod("Serialize", new[] { typeof(RimeWriter) });
            if (s_Serialize == null) { p_Writer.WriteLine("Container has no Serialize(RimeWriter)."); return false; }

            if (Output.Directory != null && !Output.Directory.Exists) Output.Directory.Create();
            using (var s_Fs = File.Create(Output.FullName))
            using (var s_W = new RimeWriter(s_Fs, Endianness.LittleEndian, false))
                s_Serialize.Invoke(s_Container, new object[] { s_W });

            p_Writer.WriteLine($"Patched {s_Patched} pixel ShaderConstant(s).");
            p_Writer.WriteLine($"Wrote modified shaderdb -> {Output.FullName} ({new FileInfo(Output.FullName).Length} bytes)");
            return true;
        }
    }
}
