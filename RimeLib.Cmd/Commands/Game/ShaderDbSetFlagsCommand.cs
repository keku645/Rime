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
    [CommandDescription("Sets bits on the Flags byte of EVERY solution of one shader entry (1 = DoubleSided, " +
                        "2 = GammaCorrection). Meant for a shader CLONED with shader_db_clone_entry: turning " +
                        "DoubleSided on for a clone leaves the vanilla entry — and every object wearing it — " +
                        "untouched. Chainable like the other shaderdb commands ([in-file] LAST).")]
    public class ShaderDbSetFlagsCommand : Command
    {
        [CommandArgument(Description = "The shaderdb resource name, e.g. levels/mp_017/mp_017/shaderdb")]
        public string? Name { get; set; }

        [CommandArgument(Description = "EXACT shader asset name whose solutions get the bits, e.g. objects/dust2/de_dust2_preset")]
        public string? Shader { get; set; }

        [CommandArgument(Description = "Bits to OR into Flags (decimal): 1 = DoubleSided, 2 = GammaCorrection.")]
        public int OrMask { get; set; }

        [CommandArgument(Description = "Output shaderdb file path")]
        public FileInfo? Output { get; set; }

        [CommandArgument(Description = "Optional (LAST) INPUT shaderdb file to modify instead of the mounted " +
                                       "resource's bytes, so several modifications can be chained.", Optional = true)]
        public FileInfo? Input { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Shader) || Output == null)
            {
                p_Writer.WriteLine("Usage: shader_db_set_flags <shaderdb> <shader-exact> <or-mask> <out-file> [in-file]");
                return false;
            }

            if (OrMask is < 0 or > 255)
            {
                p_Writer.WriteLine("The mask must fit in the Flags BYTE (0-255).");
                return false;
            }

            var s_Mounter = ((GameContext)p_Context).GetMounter();
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

            // A database re-read FROM A FILE cannot name an entry whose shader-asset partition is not
            // mounted — the reader files it as "__unresolved_0x<key>" (ShaderDatabase.cs). That is exactly
            // the state of a fresh clone while its stub partition still only exists in the mod being built,
            // so match the entry by EITHER its asset name or the key that name hashes to.
            var s_Key = RimeLib.Frostbite.Utils.HashQuickLowerCase(Shader!);
            var s_Unresolved = $"__unresolved_0x{s_Key:x8}";

            var s_Touched = 0;
            foreach (var s_PathKey in s_Databases.Keys)
            {
                var s_Db = s_Databases[s_PathKey];
                var s_Shaders = s_Db?.GetType().GetProperty("Shaders")?.GetValue(s_Db) as IDictionary;
                if (s_Shaders == null) continue;

                foreach (var s_ShKey in s_Shaders.Keys)
                {
                    var s_ThisName = s_ShKey?.ToString() ?? "";
                    if (!s_ThisName.Equals(Shader, StringComparison.OrdinalIgnoreCase) &&
                        !s_ThisName.Equals(s_Unresolved, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var s_Info = s_Shaders[s_ShKey!];
                    var s_Sols = s_Info?.GetType().GetProperty("Solutions")?.GetValue(s_Info) as Array;
                    if (s_Sols == null) continue;

                    var s_Before = "";
                    var s_Count = 0;
                    foreach (var s_Sol in s_Sols)
                    {
                        var s_Prop = s_Sol!.GetType().GetProperty("Flags");
                        if (s_Prop == null) continue;
                        var s_Old = Convert.ToInt32(s_Prop.GetValue(s_Sol));
                        if (s_Count == 0) s_Before = s_Old.ToString();
                        s_Prop.SetValue(s_Sol, (byte)(s_Old | OrMask));
                        s_Count++;
                    }

                    p_Writer.WriteLine($"SETFLAGS: [{s_PathKey}] {s_ShKey} -> {s_Count} solution(s), " +
                                       $"flags {s_Before} |= {OrMask}");
                    s_Touched += s_Count;
                }
            }

            if (s_Touched == 0)
            {
                p_Writer.WriteLine($"SETFLAGS FAILED: no solution of '{Shader}' (nor '{s_Unresolved}') was found; " +
                                   "the asset name must be EXACT.");
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
