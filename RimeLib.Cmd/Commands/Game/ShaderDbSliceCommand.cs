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
    [CommandDescription("Writes a NEW shader database holding only the named shaders, with the shared tables " +
                        "trimmed to what those entries reach. Use it to ship a mod's own additions as a small " +
                        "additive database instead of a regenerated copy of the level's whole one. The source " +
                        "is a database FILE when the path exists, otherwise a mounted resource name.")]
    public class ShaderDbSliceCommand : Command
    {
        [CommandArgument(Description = "Source: a shaderdb .bin file, or a mounted resource name " +
                                       "(e.g. levels/mp_017/mp_017/shaderdb).")]
        public string? Source { get; set; }

        [CommandArgument(Description = "Output shaderdb file path.")]
        public FileInfo? Output { get; set; }

        [CommandArgument(Description = "Shader names to keep, comma-separated (exact names, case-insensitive).")]
        public string? Shaders { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Source) || Output == null || string.IsNullOrWhiteSpace(Shaders))
            {
                p_Writer.WriteLine("Usage: shader_db_slice <shaderdb-file-or-resource> <out.bin> <name[,name...]>");
                return false;
            }

            var s_Names = Shaders!.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p_N => p_N.Trim())
                .Where(p_N => p_N.Length > 0)
                .ToList();

            if (s_Names.Count == 0)
            {
                p_Writer.WriteLine("No shader names given.");
                return false;
            }

            var s_Mounter = ((GameContext) p_Context).GetMounter();
            var s_Resolver = EngineInterfaceRegistry.Create<IShaderResolver>(s_Mounter.GetEngineType());
            var s_ContainerType = s_Resolver.GetType().GetProperty("ShaderDatabaseContainer")?.PropertyType;
            if (s_ContainerType == null)
            {
                p_Writer.WriteLine("Resolver exposes no ShaderDatabaseContainer.");
                return false;
            }

            // A file when one exists at that path, otherwise the mounted resource of that name — the same
            // database reaches this command both ways (freshly patched on disk, or straight from the game).
            object? s_Container;
            if (File.Exists(Source))
            {
                using var s_Stream = new MemoryStream(File.ReadAllBytes(Source!));
                using var s_Reader = new RimeReader(s_Stream, Endianness.LittleEndian, false);
                s_Container = Activator.CreateInstance(s_ContainerType, s_Reader, s_Mounter);
                p_Writer.WriteLine($"Parsed {Source} ({s_Stream.Length} bytes)");
            }
            else
            {
                if (!s_Mounter.TryGetResource(Source!, out var s_Resource) || s_Resource.FirstVariant == null)
                {
                    p_Writer.WriteLine($"Could not find shaderdb resource ({Source}), and no file of that name.");
                    return false;
                }

                s_Resolver.Initialize(s_Resource.FirstVariant, s_Mounter);
                s_Container = s_Resolver.GetType().GetProperty("ShaderDatabaseContainer")?.GetValue(s_Resolver);
            }

            var s_Databases = s_Container?.GetType().GetProperty("Databases")?.GetValue(s_Container) as IDictionary;
            if (s_Container == null || s_Databases == null)
            {
                p_Writer.WriteLine("The source parsed to null / no databases.");
                return false;
            }

            var s_Sliced = Activator.CreateInstance(s_ContainerType);
            var s_SlicedDbs = s_Sliced?.GetType().GetProperty("Databases")?.GetValue(s_Sliced) as IDictionary;
            if (s_Sliced == null || s_SlicedDbs == null)
            {
                p_Writer.WriteLine("Could not create an empty container.");
                return false;
            }

            // ⛔ Every render path is sliced, not just the one that happens to be verified: a database whose
            // Dx11 carries the addition and whose Dx10Plus does not is the half-artifact that works on one
            // machine and not the next.
            var s_TotalKept = 0;
            foreach (var s_PathKey in s_Databases.Keys)
            {
                var s_Db = s_Databases[s_PathKey];
                var s_Slice = s_Db?.GetType().GetMethod("Slice");
                if (s_Slice == null)
                {
                    p_Writer.WriteLine("ShaderDatabase has no Slice (stale assembly?).");
                    return false;
                }

                var s_Args = new object?[] { s_Names, null };
                var s_Result = s_Slice.Invoke(s_Db, s_Args);
                var s_Missing = s_Args[1] as List<string> ?? new List<string>();
                if (s_Result == null)
                {
                    p_Writer.WriteLine($"[{s_PathKey}] slice returned null.");
                    return false;
                }

                var s_Kept = (s_Result.GetType().GetProperty("Shaders")?.GetValue(s_Result) as IDictionary)?.Count ?? 0;
                var s_Solutions = (s_Result.GetType().GetProperty("Solutions")?.GetValue(s_Result) as Array)?.Length ?? 0;
                var s_Pixels = (s_Result.GetType().GetProperty("PixelPermutations")?.GetValue(s_Result) as Array)?.Length ?? 0;
                p_Writer.WriteLine($"[{s_PathKey}] SLICE: {s_Kept} shader(s), {s_Solutions} solution(s), " +
                                   $"{s_Pixels} pixel permutation(s)" +
                                   (s_Missing.Count > 0 ? $" — ABSENT here: {string.Join(", ", s_Missing)}" : ""));

                s_TotalKept += s_Kept;
                s_SlicedDbs[s_PathKey] = s_Result;
            }

            if (s_TotalKept == 0)
            {
                p_Writer.WriteLine("ERROR: not one of those shaders is in the source; nothing was written.");
                return false;
            }

            var s_Serialize = s_Sliced.GetType().GetMethod("Serialize", new[] { typeof(RimeWriter) });
            if (s_Serialize == null)
            {
                p_Writer.WriteLine("Container has no Serialize(RimeWriter).");
                return false;
            }

            if (Output.Directory != null && !Output.Directory.Exists)
                Output.Directory.Create();

            using (var s_Fs = File.Create(Output.FullName))
            using (var s_Writer = new RimeWriter(s_Fs, Endianness.LittleEndian, false))
                s_Serialize.Invoke(s_Sliced, new object[] { s_Writer });

            p_Writer.WriteLine($"Wrote sliced shaderdb -> {Output.FullName} " +
                               $"({new FileInfo(Output.FullName).Length:N0} bytes)");
            return true;
        }
    }
}
