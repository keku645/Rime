using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using RimeLib;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.Shader;

namespace RimeLib.Cmd.Commands.Game
{
    [CommandDescription("Lists the texture slots a shader's PIXEL permutations declare: the sampler register " +
                        "(tN) and the texture asset bound to it, plus the external/streamable ones. Use it to " +
                        "learn which assets a shader reads before authoring a replacement pixel shader — " +
                        "dump_shader_bindings searches by ShaderConstantFunction instead of by shader name.")]
    public class DumpShaderTexturesCommand : Command
    {
        [CommandArgument(Description = "The shaderdb resource name, e.g. levels/mp_017/mp_017/shaderdb")]
        public string? Name { get; set; }

        [CommandArgument(Description = "Shader name substring, e.g. OilDrumBarrel")]
        public string? Shader { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Shader))
            {
                p_Writer.WriteLine("Usage: dump_shader_textures <shaderdb> <shader-name-or-substring>");
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
            if (s_Databases == null)
            {
                p_Writer.WriteLine("Shaderdb parsed to null / no databases.");
                return false;
            }

            var s_Needle = Shader!.ToLowerInvariant();
            var s_Seen = new HashSet<string>();
            var s_Matched = 0;

            foreach (var s_PathKey in s_Databases.Keys)
            {
                var s_Db = s_Databases[s_PathKey];
                var s_Shaders = s_Db?.GetType().GetProperty("Shaders")?.GetValue(s_Db) as IDictionary;
                if (s_Shaders == null) continue;

                foreach (var s_ShKey in s_Shaders.Keys)
                {
                    var s_ShName = s_ShKey?.ToString() ?? "";
                    if (!s_ShName.ToLowerInvariant().Contains(s_Needle)) continue;
                    s_Matched++;

                    var s_Info = s_Shaders[s_ShKey];
                    p_Writer.WriteLine($"SHTEX-SHADER: [{s_PathKey}] {s_ShName}");

                    // Most mesh shaders bind their textures as STREAMABLE (resolved by name at draw time)
                    // rather than as ShaderConstant textures, so this list is usually the only one populated.
                    // The index is emitted because the sampler register is not stored here: the drum's
                    // disassembly shows streamable 0 -> t1 and 1 -> t2, i.e. declaration order, but that
                    // mapping is INFERRED from ordering and is not recorded in the database.
                    var s_Streamables = s_Info?.GetType().GetProperty("StreamableTextures")?.GetValue(s_Info) as Array;
                    if (s_Streamables != null)
                        for (var i = 0; i < s_Streamables.Length; i++)
                        {
                            var s_Streamable = s_Streamables.GetValue(i);
                            object? StProp(string p_N) =>
                                s_Streamable?.GetType().GetProperty(p_N)?.GetValue(s_Streamable);

                            p_Writer.WriteLine($"  SHTEX-STREAMABLE: index={i} name={StProp("Name")} " +
                                               $"coord={StProp("CoordType")} usage={StProp("VertexUsage")} " +
                                               $"factor={StProp("Factor")}");
                        }

                    // The second streamable list: textures resolved at draw time from a MATERIAL PARAMETER
                    // rather than baked in by asset name. A shader can declare more samplers than it has named
                    // streamables precisely because these fill the difference - a dump that omits them reports
                    // "2 textures" for a 3-sampler shader and the third register looks unaccounted for.
                    var s_Externals = s_Info?.GetType().GetProperty("StreamableExternalTextures")?.GetValue(s_Info) as Array;
                    if (s_Externals != null)
                        for (var i = 0; i < s_Externals.Length; i++)
                        {
                            var s_External = s_Externals.GetValue(i);
                            object? ExProp(string p_N) =>
                                s_External?.GetType().GetProperty(p_N)?.GetValue(s_External);

                            p_Writer.WriteLine($"  SHTEX-EXTERNAL: index={i} param={ExProp("ParameterName")} " +
                                               $"id={ExProp("ParameterId")} coord={ExProp("CoordType")} " +
                                               $"usage={ExProp("VertexUsage")} factor={ExProp("Factor")}");
                        }

                    var s_Sols = s_Info?.GetType().GetProperty("Solutions")?.GetValue(s_Info) as Array;
                    if (s_Sols == null) continue;

                    foreach (var s_Sol in s_Sols)
                    {
                        var s_Pp = s_Sol?.GetType().GetProperty("PixelPermutation")?.GetValue(s_Sol);
                        var s_Constant = s_Pp?.GetType().GetProperty("Constant")?.GetValue(s_Pp);
                        var s_Textures = s_Constant?.GetType().GetProperty("Textures")?.GetValue(s_Constant) as Array;

                        var s_State = s_Sol!.GetType().GetProperty("State")?.GetValue(s_Sol);
                        var s_Mode = s_State?.GetType().GetProperty("Mode")?.GetValue(s_State)?.ToString() ?? "";

                        if (s_Textures != null)
                            foreach (var s_Texture in s_Textures)
                            {
                                var s_Index = s_Texture?.GetType().GetProperty("Index")?.GetValue(s_Texture);
                                var s_TexName = s_Texture?.GetType().GetProperty("Name")?.GetValue(s_Texture)?.ToString() ?? "";

                                // One line per distinct (register, asset) pair PER SHADER: the same slot repeats
                                // across every solution and the caller only needs the mapping. ⛔ The shader name
                                // is part of the key — a substring match prints SIBLING shaders too, and a global
                                // dedup made the FIRST sibling swallow the rows of every later one (the exact
                                // VehiclePreset_Jet block came out with no slot rows because _Decals printed
                                // first), which a per-shader-scoped consumer reads as "this shader has none".
                                var s_Key = $"{s_ShName}|{s_Index}|{s_TexName}";
                                if (!s_Seen.Add(s_Key)) continue;

                                p_Writer.WriteLine($"  SHTEX: register=t{s_Index} name={s_TexName} mode={s_Mode}");
                            }

                        // The solution-level constants are where the REAL register mapping lives: TextureConstant
                        // entries name each streamable/external slot in the order the compiled permutation binds
                        // them, and the TextureFunction rows are registers the ENGINE feeds (depth, envmaps...) -
                        // registers no asset will ever fill, which a texture loader must know to not wait for one.
                        var s_PixelConstants = s_Sol.GetType().GetProperty("PixelConstants")?.GetValue(s_Sol);
                        if (s_PixelConstants != null)
                        {
                            void DumpList(string p_Prop, string p_Tag)
                            {
                                if (s_PixelConstants.GetType().GetProperty(p_Prop)?.GetValue(s_PixelConstants) is not Array s_List)
                                    return;

                                for (var i = 0; i < s_List.Length; i++)
                                {
                                    var s_Item = s_List.GetValue(i);
                                    var s_ItemName = s_Item?.GetType().GetProperty("Name")?.GetValue(s_Item)?.ToString() ?? "";
                                    var s_Key = $"{p_Tag}|{s_ShName}|{s_Mode}|{i}|{s_ItemName}";
                                    if (!s_Seen.Add(s_Key)) continue;

                                    p_Writer.WriteLine($"  {p_Tag}: mode={s_Mode} slot={i} name={s_ItemName}");
                                }
                            }

                            DumpList("Textures", "SHTEX-SLOT");
                            DumpList("ExternalTextures", "SHTEX-EXTSLOT");

                            // The engine's own DEFAULT for each external VALUE parameter. A consumer that has
                            // to fill a shader's constant buffer without a material instance naming the value
                            // needs these: a made-up "neutral" is somebody's lethal value (an FLIR gate at 1
                            // renders the whole vehicle in thermal mode).
                            if (s_PixelConstants.GetType().GetProperty("ExternalValues")?.GetValue(s_PixelConstants) is Array s_ExtVals)
                                foreach (var s_Val in s_ExtVals)
                                {
                                    object? EvProp(string p_N) => s_Val?.GetType().GetProperty(p_N)?.GetValue(s_Val);
                                    var s_ValName = EvProp("Name")?.ToString() ?? "";
                                    var s_ValKey = $"extval|{s_ShName}|{s_Mode}|{s_ValName}";
                                    if (!s_Seen.Add(s_ValKey)) continue;

                                    var s_Default = EvProp("DefaultValue");
                                    object? DvProp(string p_N) => s_Default?.GetType().GetProperty(p_N)?.GetValue(s_Default);
                                    p_Writer.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                        "  SHTEX-EXTVAL: mode={0} param={1} default={2},{3},{4},{5}",
                                        s_Mode, s_ValName, DvProp("x"), DvProp("y"), DvProp("z"), DvProp("w")));
                                }
                        }

                        var s_Tf = s_Pp?.GetType().GetProperty("TextureFunction")?.GetValue(s_Pp);
                        if (s_Tf?.GetType().GetProperty("Textures")?.GetValue(s_Tf) is Array s_FnTextures)
                            foreach (var s_Fn in s_FnTextures)
                            {
                                object? FnProp(string p_N) => s_Fn?.GetType().GetProperty(p_N)?.GetValue(s_Fn);
                                var s_Key = $"fn|{s_ShName}|{s_Mode}|{FnProp("Index")}|{FnProp("Function")}";
                                if (!s_Seen.Add(s_Key)) continue;

                                p_Writer.WriteLine($"  SHTEX-FUNCTION: mode={s_Mode} register=t{FnProp("Index")} " +
                                                   $"function={FnProp("Function")} parameter={FnProp("Parameter")}");
                            }
                    }
                }
            }

            p_Writer.WriteLine($"SHTEX-DONE: {Name} '{Shader}' matchedShaders={s_Matched} slots={s_Seen.Count}");
            return s_Matched > 0;
        }
    }
}
