using System;
using System.Collections;
using System.IO;
using RimeLib;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.IO;
using RimeLib.IO.Conversion;
using RimeLib.Shader;

namespace RimeLib.Cmd.Commands.Game
{
    [CommandDescription("Gives a CLONED shader entry vertex shaders of its own that hand the mesh-space position to the pixel shader " +
                        "(the .w of the world-position and the first two tangent-row outputs = v0.x/y/z; written 0 by the game's), for an " +
                        "emblem projected from the weapon's own space. The source entry keeps the game's vertex shaders. Chainable " +
                        "([in-file] LAST).")]
    public class ShaderDbVertexMeshPosCommand : Command
    {
        [CommandArgument(Description = "The shaderdb resource name, e.g. levels/xp2_skybar/xp2_skybar/shaderdb")]
        public string? Name { get; set; }

        [CommandArgument(Description = "EXACT shader asset name of the clone, e.g. Weapons/Shaders/Camo_EMBLEMTEST")]
        public string? Shader { get; set; }

        [CommandArgument(Description = "Output shaderdb file path")]
        public FileInfo? Output { get; set; }

        [CommandArgument(Description = "Optional (LAST) INPUT shaderdb file to modify instead of the mounted resource's bytes.", Optional = true)]
        public FileInfo? Input { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Shader) || Output == null)
            {
                p_Writer.WriteLine("Usage: shader_db_vertex_mesh_pos <shaderdb> <shader-exact> <out-file> [in-file]");
                return false;
            }

            var s_Mounter = ((GameContext) p_Context).GetMounter();
            var s_Resolver = EngineInterfaceRegistry.Create<IShaderResolver>(s_Mounter.GetEngineType());

            object? s_Container;
            if (Input != null)
            {
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

            var s_Key = RimeLib.Frostbite.Utils.HashQuickLowerCase(Shader!);
            var s_Ok = false;
            foreach (var s_PathKey in s_Databases.Keys)
            {
                var s_Db = s_Databases[s_PathKey];
                var s_Method = s_Db?.GetType().GetMethod("PatchVertexMeshPosition");
                if (s_Method == null) { p_Writer.WriteLine("ShaderDatabase has no PatchVertexMeshPosition (stale plugin?)."); return false; }

                var s_Report = (string) s_Method.Invoke(s_Db, new object[] { Shader!, s_Key })!;
                p_Writer.WriteLine($"VERTEXMESHPOS: [{s_PathKey}] {s_Report}");
                s_Ok |= !s_Report.StartsWith("ERROR", StringComparison.Ordinal);
            }

            if (!s_Ok)
            {
                p_Writer.WriteLine($"VERTEXMESHPOS FAILED: no vertex shader of '{Shader}' took the patch.");
                return false;
            }

            var s_Serialize = s_Container.GetType().GetMethod("Serialize", new[] { typeof(RimeWriter) });
            if (s_Serialize == null) { p_Writer.WriteLine("Container has no Serialize(RimeWriter)."); return false; }

            if (Output.Directory != null && !Output.Directory.Exists) Output.Directory.Create();
            using (var s_Fs = File.Create(Output.FullName))
            using (var s_W = new RimeWriter(s_Fs, Endianness.LittleEndian, false))
                s_Serialize.Invoke(s_Container, new object[] { s_W });

            p_Writer.WriteLine($"Wrote modified shaderdb -> {Output.FullName} ({new FileInfo(Output.FullName).Length} bytes)");
            return true;
        }
    }
}
