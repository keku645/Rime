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
    [CommandDescription("Gives a shader entry the SAMPLER STATES of another shader: addressing and filtering are state " +
                        "of the shader kept in each pixel ShaderConstant, not in the bytecode, so a copy that draws " +
                        "another shader's logic on a donor's solutions keeps the DONOR's samplers unless they are " +
                        "copied (a vehicle preset's decals read through s1 = Clamp/Clamp/Wrap, its body through s0 = " +
                        "Wrap). Reads the source shader's samplers from every pixel constant of its solutions (one " +
                        "state per register, or it refuses), and sets each on every pixel constant of the target that " +
                        "already carries samplers: in place where the register exists, appended where it does not. " +
                        "Both shaders are named EXACTLY (a fresh-named clone by its full name). Refuses a target record " +
                        "another shader entry shares. Chains through an optional input file like " +
                        "shader_db_add_external_texture.")]
    public class ShaderDbCopySamplersCommand : Command
    {
        [CommandArgument(Description = "The shaderdb resource holding the source shader, e.g. levels/xp3_desert/xp3_desert/shaderdb")]
        public string? Source { get; set; }

        [CommandArgument(Description = "The source shader's full name, e.g. Vehicles/Shaders/VehiclePreset1UvSet_Mud_Decals")]
        public string? SourceShader { get; set; }

        [CommandArgument(Description = "The shaderdb resource the target lives in (or that the input file was built from)")]
        public string? Name { get; set; }

        [CommandArgument(Description = "The target shader's full name (a fresh-named clone by the name the caller gave it)")]
        public string? Shader { get; set; }

        [CommandArgument(Description = "Output shaderdb file path")]
        public FileInfo? Output { get; set; }

        [CommandArgument(Description = "Optional INPUT shaderdb file to modify instead of the mounted resource's bytes " +
                                       "(a chain file with clones already in it).",
                         Optional = true)]
        public FileInfo? Input { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Source) || string.IsNullOrWhiteSpace(SourceShader) ||
                string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Shader) || Output == null)
            {
                p_Writer.WriteLine("Usage: shader_db_copy_samplers <source-shaderdb> <source-shader> <shaderdb> <shader> <out-file> [in-file]");
                return false;
            }

            var s_Mounter = ((GameContext) p_Context).GetMounter();

            // --- the source: its samplers, one state per register across every pixel constant of its solutions
            var s_SourceContainer = ContainerOf(s_Mounter, Source!, null, p_Writer);
            if (s_SourceContainer == null)
                return false;

            var s_SourceEntries = EntriesNamed(s_SourceContainer, SourceShader!);
            if (s_SourceEntries.Count == 0)
            {
                p_Writer.WriteLine($"No shader matched '{SourceShader}' in {Source} (exact name).");
                return false;
            }

            // register -> (state bytes -> (a state object, how many records hold it))
            var s_States = new SortedDictionary<uint, Dictionary<string, (object State, int Count)>>();
            var s_SourceRecords = 0;
            foreach (var s_Const in PixelConstantsOf(s_SourceEntries.Select(p_E => p_E.Info)))
            {
                s_SourceRecords++;
                if (s_Const.GetType().GetProperty("Samplers")?.GetValue(s_Const) is not Array s_Samplers)
                    continue;

                foreach (var s_Sampler in s_Samplers)
                {
                    if (s_Sampler == null)
                        continue;

                    var s_Index = (uint) (s_Sampler.GetType().GetProperty("Index")?.GetValue(s_Sampler) ?? 0u);
                    var s_Key = Convert.ToHexString(Bytes(s_Const, s_Sampler));
                    if (!s_States.TryGetValue(s_Index, out var s_Per))
                        s_States[s_Index] = s_Per = new Dictionary<string, (object, int)>();

                    s_Per[s_Key] = s_Per.TryGetValue(s_Key, out var s_Had) ? (s_Had.State, s_Had.Count + 1) : (s_Sampler, 1);
                }
            }

            if (s_States.Count == 0)
            {
                p_Writer.WriteLine($"'{SourceShader}' declares no sampler in any of its {s_SourceRecords} pixel constant(s) - nothing to copy.");
                return false;
            }

            var s_Wanted = new List<object>();
            foreach (var (s_Index, s_Per) in s_States)
            {
                if (s_Per.Count > 1)
                {
                    p_Writer.WriteLine($"SAMPLER-CONFLICT: '{SourceShader}' holds {s_Per.Count} different states for s{s_Index}: " +
                                       string.Join(" | ", s_Per.Values.Select(p_V => $"{Describe(s_SourceContainer, p_V.State)} x{p_V.Count}")) +
                                       " - refusing to pick one.");
                    return false;
                }

                var s_Only = s_Per.Values.First();
                p_Writer.WriteLine($"  source s{s_Index}: {Describe(s_SourceContainer, s_Only.State)} ({s_Only.Count} of {s_SourceRecords} record(s))");
                s_Wanted.Add(s_Only.State);
            }

            // --- the target
            var s_Container = ContainerOf(s_Mounter, Name!, Input, p_Writer);
            if (s_Container == null)
                return false;

            var s_Targets = EntriesNamed(s_Container, Shader!);
            if (s_Targets.Count == 0)
            {
                p_Writer.WriteLine($"No shader matched '{Shader}' (exact name, or its key for a fresh-named clone).");
                return false;
            }

            // a record another entry shares is not ours to change
            var s_Theirs = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var (s_OtherName, s_OtherInfo) in AllEntries(s_Container))
                if (!s_Targets.Any(p_T => ReferenceEquals(p_T.Info, s_OtherInfo)))
                    foreach (var s_Const in PixelConstantsOf(new[] { s_OtherInfo }))
                        s_Theirs.Add(s_Const);

            int s_Records = 0, s_Bare = 0;
            foreach (var s_Const in PixelConstantsOf(s_Targets.Select(p_T => p_T.Info)))
            {
                if (s_Theirs.Contains(s_Const))
                {
                    p_Writer.WriteLine($"ERROR: a pixel constant of '{Shader}' is shared with another shader entry - refusing to change it.");
                    return false;
                }

                var s_Has = s_Const.GetType().GetProperty("Samplers")?.GetValue(s_Const) as Array;
                if (s_Has == null || s_Has.Length == 0)
                {
                    s_Bare++; // a pass that samples nothing (depth): no sampler table to complete
                    continue;
                }

                var s_Set = s_Const.GetType().GetMethod("SetSamplerState");
                if (s_Set == null)
                {
                    p_Writer.WriteLine("ShaderConstant has no SetSamplerState (stale assembly?).");
                    return false;
                }

                s_Records++;
                foreach (var s_State in s_Wanted)
                {
                    var s_Report = s_Set.Invoke(s_Const, new[] { s_State }) as string;
                    p_Writer.WriteLine($"    {s_Report}");
                    if (s_Report == null || s_Report.StartsWith("ERROR"))
                        return false;
                }

                // the record as it now stands, read back
                if (s_Const.GetType().GetProperty("Samplers")?.GetValue(s_Const) is Array s_Now)
                    p_Writer.WriteLine("      now " + string.Join(", ", s_Now.Cast<object>().Select(p_S =>
                        $"s{p_S.GetType().GetProperty("Index")?.GetValue(p_S)} {Describe(s_Container, p_S)}")));
            }

            if (s_Records == 0)
            {
                p_Writer.WriteLine($"'{Shader}' matched but none of its pixel constants carries a sampler table ({s_Bare} without one).");
                return false;
            }

            var s_Serialize = s_Container.GetType().GetMethod("Serialize", new[] { typeof(RimeWriter) });
            if (s_Serialize == null)
            {
                p_Writer.WriteLine("Container has no Serialize(RimeWriter).");
                return false;
            }

            if (Output.Directory != null && !Output.Directory.Exists)
                Output.Directory.Create();
            using (var s_Fs = File.Create(Output.FullName))
            using (var s_W = new RimeWriter(s_Fs, Endianness.LittleEndian, false))
                s_Serialize.Invoke(s_Container, new object[] { s_W });

            p_Writer.WriteLine($"SAMPLERS: '{Shader}' <- '{SourceShader}': {string.Join(", ", s_Wanted.Select(p_S => $"s{p_S.GetType().GetProperty("Index")?.GetValue(p_S)} {Describe(s_Container, p_S)}"))} " +
                               $"on {s_Records} pixel constant(s) ({s_Bare} without samplers left as they are)");
            p_Writer.WriteLine($"Wrote modified shaderdb -> {Output.FullName} ({new FileInfo(Output.FullName).Length} bytes)");
            return true;
        }

        /// <summary>The parsed container of a mounted shaderdb resource, or of an input file built from one.</summary>
        private static object? ContainerOf(RimeLib.Content.Mounting.IEngineMounter p_Mounter, string p_Resource, FileInfo? p_Input, TextWriter p_Writer)
        {
            var s_Resolver = EngineInterfaceRegistry.Create<IShaderResolver>(p_Mounter.GetEngineType());
            object? s_Container;
            if (p_Input != null)
            {
                if (!p_Input.Exists)
                {
                    p_Writer.WriteLine($"Input file not found: {p_Input.FullName}");
                    return null;
                }

                var s_ContainerType = s_Resolver.GetType().GetProperty("ShaderDatabaseContainer")?.PropertyType;
                if (s_ContainerType == null)
                {
                    p_Writer.WriteLine("Resolver exposes no ShaderDatabaseContainer.");
                    return null;
                }

                using var s_Ms = new MemoryStream(File.ReadAllBytes(p_Input.FullName));
                using var s_Rd = new RimeReader(s_Ms, Endianness.LittleEndian, false);
                s_Container = Activator.CreateInstance(s_ContainerType, s_Rd, p_Mounter);
                p_Writer.WriteLine($"Parsed input shaderdb file: {p_Input.FullName} ({s_Ms.Length} bytes)");
            }
            else
            {
                if (!p_Mounter.TryGetResource(p_Resource, out var s_Resource) || s_Resource.FirstVariant == null)
                {
                    p_Writer.WriteLine($"Could not find shaderdb resource ({p_Resource}).");
                    return null;
                }

                s_Resolver.Initialize(s_Resource.FirstVariant, p_Mounter);
                s_Container = s_Resolver.GetType().GetProperty("ShaderDatabaseContainer")?.GetValue(s_Resolver);
            }

            if (s_Container == null)
                p_Writer.WriteLine($"Shaderdb {p_Resource} parsed to null.");
            return s_Container;
        }

        private static IEnumerable<(string Name, object Info)> AllEntries(object p_Container)
        {
            if (p_Container.GetType().GetProperty("Databases")?.GetValue(p_Container) is not IDictionary s_Databases)
                yield break;

            foreach (var s_PathKey in s_Databases.Keys)
            {
                var s_Db = s_Databases[s_PathKey];
                if (s_Db?.GetType().GetProperty("Shaders")?.GetValue(s_Db) is not IDictionary s_Shaders)
                    continue;

                foreach (var s_ShKey in s_Shaders.Keys)
                    if (s_Shaders[s_ShKey] is { } s_Info)
                        yield return (s_ShKey?.ToString() ?? "", s_Info);
            }
        }

        /// <summary>
        /// The entries named EXACTLY p_Name — or keyed by it: a fresh-named clone read back from a file has no name
        /// to resolve and shows as its key. ⛔ Never a substring: `VehiclePreset1UvSet_Mud` is a substring of its
        /// `_Decals` and `_AllCamo` siblings.
        /// </summary>
        private static List<(string Name, object Info)> EntriesNamed(object p_Container, string p_Name)
        {
            var s_Synthetic = $"__unresolved_0x{RimeLib.Frostbite.Utils.HashQuickLowerCase(p_Name):x8}";
            return AllEntries(p_Container)
                .Where(p_E => p_E.Name.Equals(p_Name, StringComparison.OrdinalIgnoreCase) ||
                              p_E.Name.Equals(s_Synthetic, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>Every distinct pixel ShaderConstant of these entries' solutions.</summary>
        private static List<object> PixelConstantsOf(IEnumerable<object> p_Infos)
        {
            var s_Seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var s_List = new List<object>();
            foreach (var s_Info in p_Infos)
            {
                if (s_Info.GetType().GetProperty("Solutions")?.GetValue(s_Info) is not Array s_Sols)
                    continue;

                foreach (var s_Sol in s_Sols)
                    if (s_Sol?.GetType().GetProperty("PixelConstants")?.GetValue(s_Sol) is { } s_Const && s_Seen.Add(s_Const))
                        s_List.Add(s_Const);
            }

            return s_List;
        }

        /// <summary>A state as the record stores it (ShaderConstant.SamplerBytes, found on the constant's own type).</summary>
        private static byte[] Bytes(object p_Const, object p_Sampler) =>
            p_Const.GetType().GetMethod("SamplerBytes")?.Invoke(null, new[] { p_Sampler }) as byte[] ?? Array.Empty<byte>();

        private static string Describe(object p_Container, object p_Sampler)
        {
            var s_Type = p_Sampler.GetType().Assembly.GetType("RimeLib.Shader.Frostbite2_0.Frostbite.ShaderConstant");
            return s_Type?.GetMethod("DescribeSampler")?.Invoke(null, new[] { p_Sampler }) as string ?? "?";
        }
    }
}
