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
    [CommandDescription("Adds an EXTERNAL texture parameter (a material parameter bound per material instance by " +
                        "NAME, the way Diffuse/Camo/Specular reach a weapon preset) to every shader matching a name " +
                        "substring in a shaderdb: an ExternalTextureConstant at the given pixel register in every " +
                        "pixel ShaderConstant, plus the entry's own StreamableExternalTextures list, both keyed by " +
                        "the engine's id of the name (hashQuickLowerCase). A material variation then binds a " +
                        "texture to it with mvdb_add_entry's SetTextureParams 'Name>part:inst'. Writes the whole " +
                        "modified container to an output file; chains through an optional input file like " +
                        "shader_db_add_texture, and addresses fresh-named clones by the name the caller knows.")]
    public class ShaderDbAddExternalTextureCommand : Command
    {
        [CommandArgument(Description = "The shaderdb resource name, e.g. levels/xp2_skybar/xp2_skybar/shaderdb")]
        public string? Name { get; set; }

        [CommandArgument(Description = "Shader name substring to target (a fresh-named clone by its full name)")]
        public string? Shader { get; set; }

        [CommandArgument(Description = "The material parameter's name, e.g. Sticker")]
        public string? Parameter { get; set; }

        [CommandArgument(Description = "Pixel-shader texture register the parameter is sampled from (e.g. 6 for t6)")]
        public string? Register { get; set; }

        [CommandArgument(Description = "Output shaderdb file path")]
        public FileInfo? Output { get; set; }

        [CommandArgument(Description = "Optional INPUT shaderdb file to modify instead of the mounted resource's bytes " +
                                       "(a chain file with clones already in it). The game must still be mounted: " +
                                       "shader entry names resolve against mounted partitions.",
                         Optional = true)]
        public FileInfo? Input { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Shader) ||
                string.IsNullOrWhiteSpace(Parameter) || string.IsNullOrWhiteSpace(Register) || Output == null)
            {
                p_Writer.WriteLine("Usage: shader_db_add_external_texture <shaderdb> <shader-substr> <parameter-name> <register> <out-file> [in-file]");
                return false;
            }

            if (!ushort.TryParse(Register, out var s_Register))
            {
                p_Writer.WriteLine($"Invalid register '{Register}' (expected 0-65535).");
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

            // The engine's id of a parameter name: fb::hashQuickLowerCase (measured on the weapon presets:
            // Diffuse 2798056075, Camo 2087764101, Specular 137093834).
            var s_Handle = RimeLib.Frostbite.Utils.HashQuickLowerCase(Parameter!);
            var s_Needle = Shader!.ToLowerInvariant();
            var s_Synthetic = $"__unresolved_0x{RimeLib.Frostbite.Utils.HashQuickLowerCase(Shader!):x8}";

            var s_Seen = new HashSet<object>();
            var s_Patched = 0;
            var s_Entries = 0;

            foreach (var s_PathKey in s_Databases.Keys)
            {
                var s_Db = s_Databases[s_PathKey];
                var s_Shaders = s_Db?.GetType().GetProperty("Shaders")?.GetValue(s_Db) as IDictionary;
                if (s_Shaders == null) continue;

                foreach (var s_ShKey in s_Shaders.Keys)
                {
                    var s_KeyName = s_ShKey?.ToString() ?? "";
                    if (!s_KeyName.ToLowerInvariant().Contains(s_Needle) &&
                        !s_KeyName.Equals(s_Synthetic, StringComparison.OrdinalIgnoreCase)) continue;

                    var s_Info = s_Shaders[s_ShKey];
                    if (s_Info == null) continue;

                    // Entry level: the shader's own list of external texture parameters. A new array, never
                    // the source's mutated in place - a clone shares the array with the entry it was cloned from.
                    var s_ListProperty = s_Info.GetType().GetProperty("StreamableExternalTextures");
                    if (s_ListProperty?.GetValue(s_Info) is not Array s_List)
                    {
                        p_Writer.WriteLine($"[{s_PathKey}] {s_KeyName}: entry has no StreamableExternalTextures list.");
                        return false;
                    }

                    var s_ElementType = s_List.GetType().GetElementType()!;
                    object? s_Template = null;
                    var s_Duplicate = false;
                    foreach (var s_Item in s_List)
                    {
                        var s_ItemName = s_ElementType.GetProperty("ParameterName")?.GetValue(s_Item)?.ToString() ?? "";
                        p_Writer.WriteLine($"[{s_PathKey}] {s_KeyName}: entry external '{s_ItemName}' id={s_ElementType.GetProperty("ParameterId")?.GetValue(s_Item)}");
                        s_Template ??= s_Item;
                        if (s_ItemName.Equals(Parameter, StringComparison.OrdinalIgnoreCase))
                            s_Duplicate = true;
                    }

                    if (s_Duplicate)
                        p_Writer.WriteLine($"[{s_PathKey}] {s_KeyName}: entry already lists '{Parameter}' - list kept.");
                    else
                    {
                        var s_Added = Activator.CreateInstance(s_ElementType)!;
                        s_ElementType.GetProperty("ParameterName")!.SetValue(s_Added, Parameter);
                        s_ElementType.GetProperty("ParameterId")!.SetValue(s_Added, s_Handle);
                        if (s_Template != null)
                        {
                            foreach (var s_Copied in new[] { "CoordType", "VertexUsage", "Factor" })
                            {
                                var s_Property = s_ElementType.GetProperty(s_Copied);
                                s_Property?.SetValue(s_Added, s_Property.GetValue(s_Template));
                            }
                        }
                        // Factor 1: sampled at the material's own UV, like the diffuse (Camo carries 0.25 = its tiling).
                        s_ElementType.GetProperty("Factor")?.SetValue(s_Added, 1.0f);

                        var s_Grown = Array.CreateInstance(s_ElementType, s_List.Length + 1);
                        Array.Copy(s_List, s_Grown, s_List.Length);
                        s_Grown.SetValue(s_Added, s_List.Length);
                        s_ListProperty.SetValue(s_Info, s_Grown);
                        p_Writer.WriteLine($"[{s_PathKey}] {s_KeyName}: entry external '{Parameter}' id={s_Handle} appended ({s_List.Length}+1).");
                    }

                    s_Entries++;

                    // Solution level: the constant record of every pixel permutation, each distinct object once.
                    var s_Sols = s_Info.GetType().GetProperty("Solutions")?.GetValue(s_Info) as Array;
                    if (s_Sols == null) continue;

                    foreach (var s_Sol in s_Sols)
                    {
                        var s_Const = s_Sol?.GetType().GetProperty("PixelConstants")?.GetValue(s_Sol);
                        if (s_Const == null || !s_Seen.Add(s_Const)) continue;

                        var s_Add = s_Const.GetType().GetMethod("AddExternalTextureConstant");
                        if (s_Add == null) { p_Writer.WriteLine("ShaderConstant has no AddExternalTextureConstant (stale assembly?)."); return false; }

                        var s_Report = s_Add.Invoke(s_Const, new object[] { s_Register, (byte) 0 /* TextureType_2d */, Parameter!, s_Handle, false }) as string;
                        p_Writer.WriteLine($"    + external t{s_Register} <- '{Parameter}' (id {s_Handle}): {s_Report}");
                        if (s_Report == null || s_Report.StartsWith("ERROR")) return false;
                        s_Patched++;

                        // The record as it now stands, so the surgery is verified by reading it back, not by trusting it.
                        if (s_Const.GetType().GetProperty("ExternalTextures")?.GetValue(s_Const) is Array s_Now)
                            foreach (var s_It in s_Now)
                            {
                                object? G(string n) => s_It?.GetType().GetProperty(n)?.GetValue(s_It);
                                p_Writer.WriteLine($"      external t{G("Index")} '{G("Name")}' handle={G("Handle")} type={G("TextureType")} required={G("Required")}");
                            }
                    }
                }
            }

            if (s_Entries == 0) { p_Writer.WriteLine($"No shader matched '{Shader}'."); return false; }
            if (s_Patched == 0) { p_Writer.WriteLine($"'{Shader}' matched but no pixel constant was patched."); return false; }

            var s_Serialize = s_Container.GetType().GetMethod("Serialize", new[] { typeof(RimeWriter) });
            if (s_Serialize == null) { p_Writer.WriteLine("Container has no Serialize(RimeWriter)."); return false; }

            if (Output.Directory != null && !Output.Directory.Exists) Output.Directory.Create();
            using (var s_Fs = File.Create(Output.FullName))
            using (var s_W = new RimeWriter(s_Fs, Endianness.LittleEndian, false))
                s_Serialize.Invoke(s_Container, new object[] { s_W });

            p_Writer.WriteLine($"Patched {s_Patched} pixel ShaderConstant(s) and {s_Entries} shader entr(ies) with external '{Parameter}' at t{s_Register}.");
            p_Writer.WriteLine($"Wrote modified shaderdb -> {Output.FullName} ({new FileInfo(Output.FullName).Length} bytes)");
            return true;
        }
    }
}
