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
    [CommandDescription("Clones a shader's shaderdb entry under a FRESH NAME (the engine keys entries by FNV of " +
                        "the lowercased shader-asset name), leaving the vanilla entry untouched — the mechanism " +
                        "behind per-instance custom variations. Only the given MODE's solutions are deep-cloned " +
                        "(solution + pixel permutation + constants, so later surgery on the clone cannot leak " +
                        "into shaders sharing the originals); a .dxbc patches the cloned permutations in the same " +
                        "step. Chain with [in-file] like replace_shader_bytecode.")]
    public class ShaderDbCloneEntryCommand : Command
    {
        [CommandArgument(Description = "The shaderdb resource name, e.g. levels/mp_017/mp_017/shaderdb")]
        public string? Name { get; set; }

        [CommandArgument(Description = "EXACT source shader asset name, e.g. Props/StreetProps/OilDrumBarrel_01/SS_OilDrumBarrel_01")]
        public string? Shader { get; set; }

        [CommandArgument(Description = "The fresh shader asset name for the clone, e.g. custom/mymod/ss_oildrum_glow")]
        public string? NewName { get; set; }

        [CommandArgument(Description = "Output shaderdb file path")]
        public FileInfo? Output { get; set; }

        [CommandArgument(Description = "Optional compiled pixel shader (.dxbc) to patch into the cloned " +
                                       "permutations of the given mode.", Optional = true)]
        public FileInfo? Dxbc { get; set; }

        [CommandArgument(Description = "Optional ShaderRenderMode substring deciding which solutions are " +
                                       "deep-cloned (default DeferredShadingGBufferLayout0).", Optional = true)]
        public string? Mode { get; set; } = "DeferredShadingGBufferLayout0";

        [CommandArgument(Description = "Optional REFERENCE original .dxbc (or '-'): the authored bytecode is " +
                                       "patched ONLY into cloned permutations whose vanilla bytes equal this " +
                                       "reference — the variant the shader was authored against. The other " +
                                       "variants (probe-lit, instanced…) keep their vanilla bytes; without it " +
                                       "every permutation of the mode is patched.", Optional = true)]
        public FileInfo? RefDxbc { get; set; }

        [CommandArgument(Description = "Optional (LAST) INPUT shaderdb file to modify instead of the mounted " +
                                       "resource's bytes, so several modifications can be chained.", Optional = true)]
        public FileInfo? Input { get; set; }

        [CommandArgument(Description = "Optional NEW geometry-declaration hash (hex, e.g. 0xB555855D) to " +
                                       "re-label the cloned solutions with, so the clone becomes the answer " +
                                       "for meshes using THAT vertex layout - the way to make a shader draw " +
                                       "geometry it was never compiled for. Only sound when the target " +
                                       "layout is a SUPERSET of what the vertex shader reads.", Optional = true)]
        public string? NewDecl { get; set; }

        [CommandArgument(Description = "Optional SOURCE geometry-declaration hash (hex): only the solutions " +
                                       "using THAT declaration get re-labelled. Without it every declaration " +
                                       "of the mode is re-labelled and they collide on the runtime's lookup " +
                                       "key, which renders as an arbitrary permutation per pass.", Optional = true)]
        public string? FromDecl { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Shader) ||
                string.IsNullOrWhiteSpace(NewName) || Output == null)
            {
                p_Writer.WriteLine("Usage: shader_db_clone_entry <shaderdb> <shader-exact> <new-name> <out-file> [in-file] [dxbc] [mode]");
                return false;
            }

            // "-" = clone WITHOUT touching the bytecode (the variation keeps the vanilla pixel shader). A
            // positional empty string cannot express that: it fails to bind to FileInfo at parse time.
            byte[]? s_Bytecode = null;
            System.Collections.Generic.List<(byte[], byte[])>? s_Patches = null;
            if (Dxbc != null && Dxbc.Name != "-")
            {
                if (!Dxbc.Exists) { p_Writer.WriteLine($"DXBC file not found: {Dxbc.FullName}"); return false; }

                // A .manifest patches PER VARIANT: each line is "<vanilla-variant.dxbc>|<authored.dxbc>",
                // so every pixel flavour (base, probe-lit, instanced...) receives bytecode compiled against
                // ITS OWN contract instead of one compilation stuffed into all of them.
                if (Dxbc.Extension.Equals(".manifest", StringComparison.OrdinalIgnoreCase))
                {
                    s_Patches = new System.Collections.Generic.List<(byte[], byte[])>();
                    foreach (var s_Line in File.ReadAllLines(Dxbc.FullName))
                    {
                        var s_Parts = s_Line.Split('|');
                        if (s_Parts.Length != 2 || s_Parts[0].Trim().Length == 0)
                            continue;
                        if (!File.Exists(s_Parts[0]) || !File.Exists(s_Parts[1]))
                        {
                            p_Writer.WriteLine($"Manifest entry missing on disk: {s_Line}");
                            return false;
                        }
                        s_Patches.Add((File.ReadAllBytes(s_Parts[0]), File.ReadAllBytes(s_Parts[1])));
                    }
                    p_Writer.WriteLine($"Patch manifest: {s_Patches.Count} variant pair(s).");
                }
                else
                {
                    s_Bytecode = File.ReadAllBytes(Dxbc.FullName);
                }
            }

            var s_Mounter = ((GameContext)p_Context).GetMounter();
            var s_Resolver = EngineInterfaceRegistry.Create<IShaderResolver>(s_Mounter.GetEngineType());

            object? s_Container;
            // "-" means "no input" here as it already does for the reference dxbc, so a later positional
            // argument can be given without inventing a chained file that does not exist.
            if (Input != null && Input.Name != "-")
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

            // The clone's key: FNV of the LOWERCASED asset name, matching how the reader (and the engine)
            // resolve shader-asset partitions against the entry table.
            var s_NewKey = RimeLib.Frostbite.Utils.HashQuickLowerCase(NewName!);
            var s_Cloned = 0;

            foreach (var s_PathKey in s_Databases.Keys)
            {
                var s_Db = s_Databases[s_PathKey];
                var s_Clone = s_Db?.GetType().GetMethod("CloneShaderEntry");
                if (s_Clone == null) { p_Writer.WriteLine("ShaderDatabase has no CloneShaderEntry (stale assembly?)."); return false; }

                byte[]? s_Reference = RefDxbc != null && RefDxbc.Name != "-" && RefDxbc.Exists
                    ? File.ReadAllBytes(RefDxbc.FullName)
                    : null;

                // Reflection fills NO optional parameters: the array must carry every slot the method
                // declares. The trailing null is p_SourceDb (same-database clone) — leaving it out throws
                // TargetParameterCountException, which is exactly what happened when the merge work grew
                // the signature and this call was not audited along.
                uint? s_NewDecl = null;
                if (!string.IsNullOrWhiteSpace(NewDecl))
                {
                    var s_Text = NewDecl!.Trim();
                    if (s_Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s_Text = s_Text[2..];
                    if (!uint.TryParse(s_Text, System.Globalization.NumberStyles.HexNumber, null, out var s_Parsed))
                    { p_Writer.WriteLine($"Invalid NewDecl '{NewDecl}' (expected hex, e.g. 0xB555855D)."); return false; }
                    s_NewDecl = s_Parsed;
                }

                uint? s_FromDecl = null;
                if (!string.IsNullOrWhiteSpace(FromDecl))
                {
                    var s_Txt = FromDecl!.Trim();
                    if (s_Txt.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s_Txt = s_Txt[2..];
                    if (!uint.TryParse(s_Txt, System.Globalization.NumberStyles.HexNumber, null, out var s_P2))
                    { p_Writer.WriteLine($"Invalid FromDecl '{FromDecl}' (expected hex)."); return false; }
                    s_FromDecl = s_P2;
                }

                var s_Report = s_Clone.Invoke(s_Db,
                    new object?[] { Shader!, NewName!, s_NewKey, s_Bytecode, Mode, s_Reference, s_Patches, null, s_NewDecl, s_FromDecl }) as string;

                p_Writer.WriteLine($"[{s_PathKey}] {s_Report}");

                // A render path that simply lacks the source shader is fine (Dx10Plus vs Dx11 coverage can
                // differ); every path failing means the name is wrong, handled after the loop.
                if (s_Report != null && !s_Report.StartsWith("ERROR"))
                    s_Cloned++;
            }

            if (s_Cloned == 0)
            {
                p_Writer.WriteLine($"CLONE FAILED: no render path could clone '{Shader}'.");
                return false;
            }

            var s_Serialize = s_Container.GetType().GetMethod("Serialize", new[] { typeof(RimeWriter) });
            if (s_Serialize == null) { p_Writer.WriteLine("Container has no Serialize(RimeWriter)."); return false; }

            if (Output.Directory != null && !Output.Directory.Exists) Output.Directory.Create();
            using (var s_Fs = File.Create(Output.FullName))
            using (var s_W = new RimeWriter(s_Fs, Endianness.LittleEndian, false))
                s_Serialize.Invoke(s_Container, new object[] { s_W });

            p_Writer.WriteLine($"Cloned into {s_Cloned} render path(s); new key 0x{s_NewKey:x8}.");
            p_Writer.WriteLine($"Wrote modified shaderdb -> {Output.FullName} ({new FileInfo(Output.FullName).Length} bytes)");
            return true;
        }
    }
}
