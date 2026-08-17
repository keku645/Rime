using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimeLib;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.IO;
using RimeLib.IO.Conversion;
using RimeLib.Shader;

namespace RimeLib.Cmd.Commands.Game
{
    [CommandDescription("Replaces the pixel-shader DXBC of a shader's solutions (by name substring) in a shaderdb " +
                        "with the bytes of a .dxbc file, and writes the whole modified shaderdb container to an output " +
                        "file. Use to inject a custom compiled shader over a level's shader (e.g. MP_017 Sea_Shader). " +
                        "Pass a render MODE to patch only the solutions of that pass — a shader normally carries both " +
                        "its GBuffer permutations and its ZOnly depth-pass ones, and overwriting the latter with a " +
                        "GBuffer shader breaks the depth/shadow pass.")]
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

        [CommandArgument(Description = "Optional ShaderRenderMode substring; only solutions whose mode matches are " +
                                       "patched (e.g. DeferredShadingGBufferLayout0). Omit to patch every solution, " +
                                       "which is what this command has always done.",
                         Optional = true)]
        public string? Mode { get; set; }

        [CommandArgument(Description = "Optional INPUT shaderdb file to modify instead of the mounted resource's bytes " +
                                       "(a shaderdb .bin that already had another injection applied), so several " +
                                       "modifications can be chained.",
                         Optional = true)]
        public FileInfo? Input { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Shader) || Dxbc == null || Output == null)
            {
                p_Writer.WriteLine("Usage: replace_shader_bytecode <shaderdb> <shader-substr> <dxbc-file> <out-file> [mode] [in-file]");
                return false;
            }
            if (!Dxbc.Exists) { p_Writer.WriteLine($"DXBC file not found: {Dxbc.FullName}"); return false; }

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

            byte[] s_NewDxbc = File.ReadAllBytes(Dxbc.FullName);
            if (s_NewDxbc.Length < 4 || s_NewDxbc[0] != 0x44 || s_NewDxbc[1] != 0x58 || s_NewDxbc[2] != 0x42 || s_NewDxbc[3] != 0x43)
                p_Writer.WriteLine("WARNING: file does not start with the 'DXBC' magic.");

            string s_Needle = Shader!.ToLowerInvariant();
            bool s_Filtering = !string.IsNullOrWhiteSpace(Mode);
            string s_ModeNeedle = (Mode ?? "").ToLowerInvariant();

            // Solutions reference permutations by index, so one PixelShaderPermutation instance can be shared by
            // several solutions. Collect the modes each instance is reachable from BEFORE writing anything: an
            // instance shared between a matching and a non-matching mode cannot be patched without silently
            // altering the pass we were asked to leave alone.
            var s_Modes = new Dictionary<object, HashSet<string>>(ReferenceEqualityComparer.Instance);
            var s_Owners = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
            int s_MatchedShaders = 0;

            foreach (var s_PathKey in s_Databases.Keys)
            {
                var s_Db = s_Databases[s_PathKey];
                var s_Shaders = s_Db?.GetType().GetProperty("Shaders")?.GetValue(s_Db) as IDictionary;
                if (s_Shaders == null) continue;

                foreach (var s_ShKey in s_Shaders.Keys)
                {
                    var s_ShName = s_ShKey?.ToString() ?? "";
                    if (!s_ShName.ToLowerInvariant().Contains(s_Needle)) continue;
                    s_MatchedShaders++;

                    var s_Info = s_Shaders[s_ShKey];
                    var s_Sols = s_Info?.GetType().GetProperty("Solutions")?.GetValue(s_Info) as Array;
                    if (s_Sols == null) continue;

                    foreach (var s_Sol in s_Sols)
                    {
                        var s_Pp = s_Sol?.GetType().GetProperty("PixelPermutation")?.GetValue(s_Sol);
                        if (s_Pp == null) continue;

                        var s_State = s_Sol!.GetType().GetProperty("State")?.GetValue(s_Sol);
                        var s_SolMode = s_State?.GetType().GetProperty("Mode")?.GetValue(s_State)?.ToString() ?? "";

                        if (!s_Modes.TryGetValue(s_Pp, out var s_Set))
                        {
                            s_Set = new HashSet<string>();
                            s_Modes[s_Pp] = s_Set;
                            s_Owners[s_Pp] = $"[{s_PathKey}] {s_ShName}";
                        }

                        s_Set.Add(s_SolMode);
                    }
                }
            }

            if (s_MatchedShaders == 0)
            {
                p_Writer.WriteLine($"No shader name matched '{Shader}'.");
                return false;
            }

            int s_Replaced = 0, s_SkippedMode = 0, s_SkippedShared = 0;

            foreach (var s_Pair in s_Modes)
            {
                var s_Pp = s_Pair.Key;
                var s_PermModes = s_Pair.Value;

                if (s_Filtering)
                {
                    bool s_AnyMatch = s_PermModes.Any(p_M => p_M.ToLowerInvariant().Contains(s_ModeNeedle));
                    bool s_AllMatch = s_PermModes.All(p_M => p_M.ToLowerInvariant().Contains(s_ModeNeedle));

                    if (!s_AnyMatch)
                    {
                        s_SkippedMode++;
                        continue;
                    }

                    if (!s_AllMatch)
                    {
                        p_Writer.WriteLine($"  SKIP {s_Owners[s_Pp]}: pixel permutation is shared by modes " +
                                           $"[{string.Join(", ", s_PermModes)}] — patching it would also change a " +
                                           "pass outside the requested mode.");
                        s_SkippedShared++;
                        continue;
                    }
                }

                var s_ByteProp = s_Pp.GetType().GetProperty("ShaderBytecode");
                if (s_ByteProp == null || !s_ByteProp.CanWrite) continue;

                s_ByteProp.SetValue(s_Pp, s_NewDxbc);
                s_Replaced++;
                p_Writer.WriteLine($"  PATCH {s_Owners[s_Pp]}: modes [{string.Join(", ", s_PermModes)}]");
            }

            if (s_Replaced == 0)
            {
                p_Writer.WriteLine($"No pixel permutation was patched for '{Shader}'" +
                                   (s_Filtering ? $" with mode '{Mode}'." : ".") +
                                   $" (skipped {s_SkippedMode} by mode, {s_SkippedShared} as shared)");
                return false;
            }

            var s_Serialize = s_Container.GetType().GetMethod("Serialize", new[] { typeof(RimeWriter) });
            if (s_Serialize == null) { p_Writer.WriteLine("Container has no Serialize(RimeWriter)."); return false; }

            if (Output.Directory != null && !Output.Directory.Exists) Output.Directory.Create();
            using (var s_Fs = File.Create(Output.FullName))
            using (var s_W = new RimeWriter(s_Fs, Endianness.LittleEndian, false))
                s_Serialize.Invoke(s_Container, new object[] { s_W });

            p_Writer.WriteLine($"Replaced {s_Replaced} pixel permutation(s) with {s_NewDxbc.Length}-byte DXBC" +
                               (s_Filtering ? $" (mode filter '{Mode}': left {s_SkippedMode} untouched)" : "") + ".");
            p_Writer.WriteLine($"Wrote modified shaderdb -> {Output.FullName} ({new FileInfo(Output.FullName).Length} bytes)");
            return true;
        }
    }
}
