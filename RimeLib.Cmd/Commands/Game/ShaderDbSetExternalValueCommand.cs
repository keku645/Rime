using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RimeLib;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.IO;
using RimeLib.IO.Conversion;
using RimeLib.Shader;

namespace RimeLib.Cmd.Commands.Game
{
    [CommandDescription("Puts an EXTERNAL VALUE parameter (a float4 the engine fills per draw BY NAME from the entity's " +
                        "shader parameter block - the way ScopeOcc reaches a weapon preset from the weapon's " +
                        "ShaderParameterComponentData) into every pixel ShaderConstant of every shader matching a name " +
                        "substring. The register is a number (a register already bound is RENAMED in place; a new one " +
                        "is appended and the constant count grows) or 'name:<existing parameter>' to rename that one. " +
                        "Prints every record's external values first (register, name, stored handle, the name's " +
                        "hashQuickLowerCase, size, array size, required, default); 'list' as the register prints and " +
                        "writes nothing. 'rel:<existing parameter>:<delta>' puts the new one at THAT record's register of " +
                        "the existing parameter plus delta — per record, so flavours whose tables number the same block " +
                        "differently (a $Globals block before it moves every register by its size) each get the element " +
                        "the bytecode reads; a record without the existing parameter is left alone. A name with a range, " +
                        "'EmblemL{0..79}', puts a run of parameters at consecutive registers from there. " +
                        "Chains through an optional input file like shader_db_add_external_texture.")]
    public class ShaderDbSetExternalValueCommand : Command
    {
        [CommandArgument(Description = "The shaderdb resource name, e.g. levels/mp_017/mp_017/shaderdb")]
        public string? Name { get; set; }

        [CommandArgument(Description = "Shader name substring to target (a fresh-named clone by its full name)")]
        public string? Shader { get; set; }

        [CommandArgument(Description = "The parameter's (new) name, e.g. EmblemTest")]
        public string? Parameter { get; set; }

        [CommandArgument(Description = "Pixel constant register (e.g. 12 for c12), 'name:<existing parameter>', or 'list'")]
        public string? Register { get; set; }

        [CommandArgument(Description = "Output shaderdb file path ('-' with 'list')")]
        public string? Output { get; set; }

        [CommandArgument(Description = "Optional default value 'x,y,z,w' the engine uses when no block names the " +
                                       "parameter ('-' = 0,0,0,0)", Optional = true)]
        public string? Default { get; set; }

        [CommandArgument(Description = "Optional INPUT shaderdb file to modify instead of the mounted resource's bytes " +
                                       "(a chain file). The game must still be mounted.", Optional = true)]
        public FileInfo? Input { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Shader) ||
                string.IsNullOrWhiteSpace(Parameter) || string.IsNullOrWhiteSpace(Register) || string.IsNullOrWhiteSpace(Output))
            {
                p_Writer.WriteLine("Usage: shader_db_set_external_value <shaderdb> <shader-substr> <parameter-name> " +
                                   "<register|name:<existing>|list> <out-file|-> [x,y,z,w|-] [in-file]");
                return false;
            }

            var s_ListOnly = Register!.Equals("list", StringComparison.OrdinalIgnoreCase);
            string? s_ByName = Register.StartsWith("name:", StringComparison.OrdinalIgnoreCase) ? Register.Substring(5) : null;
            string? s_RelativeTo = null;
            var s_RelativeDelta = 0;
            if (Register.StartsWith("rel:", StringComparison.OrdinalIgnoreCase))
            {
                var s_Rel = Register.Substring(4).Split(':');
                if (s_Rel.Length != 2 || s_Rel[0].Length == 0 || !int.TryParse(s_Rel[1], out s_RelativeDelta))
                {
                    p_Writer.WriteLine($"Invalid register '{Register}' (rel:<existing parameter>:<delta>).");
                    return false;
                }

                s_RelativeTo = s_Rel[0];
            }

            ushort s_FixedRegister = 0;
            if (!s_ListOnly && s_ByName == null && s_RelativeTo == null && !ushort.TryParse(Register, out s_FixedRegister))
            {
                p_Writer.WriteLine($"Invalid register '{Register}' (a number, 'name:<existing>', 'rel:<existing>:<delta>' or 'list').");
                return false;
            }

            // A name with a range ("EmblemL{0..79}") is a run of parameters at consecutive registers.
            var s_Names = new List<string> { Parameter! };
            var s_Range = System.Text.RegularExpressions.Regex.Match(Parameter!, @"^(.*)\{(\d+)\.\.(\d+)\}(.*)$");
            if (s_Range.Success)
            {
                var s_First = int.Parse(s_Range.Groups[2].Value, CultureInfo.InvariantCulture);
                var s_Last = int.Parse(s_Range.Groups[3].Value, CultureInfo.InvariantCulture);
                if (s_Last < s_First || s_Last - s_First > 1023)
                {
                    p_Writer.WriteLine($"Invalid range in '{Parameter}'.");
                    return false;
                }

                s_Names.Clear();
                for (var i = s_First; i <= s_Last; i++)
                    s_Names.Add(s_Range.Groups[1].Value + i.ToString(CultureInfo.InvariantCulture) + s_Range.Groups[4].Value);
                if (s_ByName != null)
                {
                    p_Writer.WriteLine("A range of names needs a register number or 'rel:', not 'name:'.");
                    return false;
                }
            }

            var s_Default = new float[4];
            if (!string.IsNullOrWhiteSpace(Default) && Default != "-")
            {
                var s_Parts = Default!.Split(',');
                if (s_Parts.Length != 4)
                {
                    p_Writer.WriteLine($"Invalid default '{Default}' (expected x,y,z,w).");
                    return false;
                }

                for (var i = 0; i < 4; i++)
                    if (!float.TryParse(s_Parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out s_Default[i]))
                    {
                        p_Writer.WriteLine($"Invalid default component '{s_Parts[i]}'.");
                        return false;
                    }
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

            // The engine's id of a parameter name: fb::hashQuickLowerCase (measured for the external textures; the
            // listing prints the stored handle next to the computed one so the same holds for values) — per name, below.
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
                    var s_Sols = s_Info?.GetType().GetProperty("Solutions")?.GetValue(s_Info) as Array;
                    if (s_Sols == null) continue;
                    s_Entries++;

                    foreach (var s_Sol in s_Sols)
                    {
                        var s_Const = s_Sol?.GetType().GetProperty("PixelConstants")?.GetValue(s_Sol);
                        if (s_Const == null || !s_Seen.Add(s_Const)) continue;

                        object? C(string p_N) => s_Const.GetType().GetProperty(p_N)?.GetValue(s_Const);
                        var s_Values = C("ValueConstants") as Array;
                        p_Writer.WriteLine($"[{s_PathKey}] {s_KeyName}: pixel constants={C("ConstantCount")} " +
                                           $"literals={s_Values?.Length ?? 0} from c{C("ValueConstantsStart")}");

                        int? s_Register = s_ByName == null && s_RelativeTo == null ? s_FixedRegister : null;
                        // Where the engine PACKS each external value: one after another in table order, ArraySize registers each,
                        // whatever its Index (fb::DxShaderDispatcher, sub_697E50, measured 2026-09-29) — so an appended value is read
                        // at the packed end, and the shader must declare it there.
                        var s_Packed = 0;
                        int? s_PackedOfReference = null;
                        if (C("ExternalValues") is Array s_Before)
                            foreach (var s_It in s_Before)
                            {
                                object? G(string p_N) => s_It?.GetType().GetProperty(p_N)?.GetValue(s_It);
                                var s_ItName = G("Name")?.ToString() ?? "";
                                if (s_RelativeTo != null && s_ItName.Equals(s_RelativeTo, StringComparison.OrdinalIgnoreCase))
                                    s_PackedOfReference = s_Packed;
                                s_Packed += System.Math.Max(1, Convert.ToInt32(G("ArraySize") ?? 1));
                                var s_Dv = G("DefaultValue");
                                object? D(string p_N) => s_Dv?.GetType().GetProperty(p_N)?.GetValue(s_Dv);
                                p_Writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                    "      external c{0} '{1}' handle={2} hashQuickLowerCase={3} size={4} array={5} " +
                                    "required={6} default={7},{8},{9},{10}", G("Index"), s_ItName, G("Handle"),
                                    RimeLib.Frostbite.Utils.HashQuickLowerCase(s_ItName), G("Size"), G("ArraySize"),
                                    G("Required"), D("x"), D("y"), D("z"), D("w")));
                                if (s_ByName != null && s_ItName.Equals(s_ByName, StringComparison.OrdinalIgnoreCase))
                                    s_Register = Convert.ToInt32(G("Index"));
                                if (s_RelativeTo != null && s_ItName.Equals(s_RelativeTo, StringComparison.OrdinalIgnoreCase))
                                    s_Register = Convert.ToInt32(G("Index")) + s_RelativeDelta;
                            }

                        if (s_ListOnly) continue;

                        if (s_Register == null)
                        {
                            p_Writer.WriteLine($"      (no external '{s_ByName ?? s_RelativeTo}' in this record - left alone)");
                            continue;
                        }

                        var s_Set = s_Const.GetType().GetMethod("SetExternalValueConstant");
                        if (s_Set == null) { p_Writer.WriteLine("ShaderConstant has no SetExternalValueConstant (stale plugin?)."); return false; }

                        // rel: the shader reads the first new value at the reference's element + delta; the engine will put it at
                        // the packed end. They must agree, and the whole must fit the engine's 64 external value registers.
                        if (s_RelativeTo != null && s_PackedOfReference != null)
                        {
                            var s_ReadAt = s_PackedOfReference.Value + s_RelativeDelta;
                            p_Writer.WriteLine($"      packed: '{s_Names[0]}' will be read at c{s_ReadAt} and packs at c{s_Packed}; " +
                                               $"{s_Packed + s_Names.Count} external value register(s) in all");
                            if (s_ReadAt != s_Packed)
                            {
                                p_Writer.WriteLine($"ERROR: the shader reads '{s_Names[0]}' at c{s_ReadAt} but the engine packs it at c{s_Packed} " +
                                                   "(external values pack in table order, one after another).");
                                return false;
                            }

                            if (s_Packed + s_Names.Count > 64)
                            {
                                p_Writer.WriteLine($"ERROR: {s_Packed + s_Names.Count} external value registers pass the 64 the engine's buffer holds " +
                                                   "(fb::DxShaderDispatcher; 92 crashed the client, 2026-09-29).");
                                return false;
                            }
                        }

                        for (var k = 0; k < s_Names.Count; k++)
                        {
                            var s_At = s_Register.Value + k;
                            if (s_At < 0 || s_At > ushort.MaxValue)
                            {
                                p_Writer.WriteLine($"ERROR: register c{s_At} for '{s_Names[k]}' is out of range.");
                                return false;
                            }

                            var s_NameHandle = RimeLib.Frostbite.Utils.HashQuickLowerCase(s_Names[k]);
                            var s_Report = s_Set.Invoke(s_Const, new object[]
                            {
                                (ushort) s_At, s_Names[k], s_NameHandle, s_Default[0], s_Default[1], s_Default[2], s_Default[3],
                            }) as string;
                            if (s_Names.Count <= 4 || k == 0 || k == s_Names.Count - 1)
                                p_Writer.WriteLine($"    = external c{s_At} <- '{s_Names[k]}' (id {s_NameHandle}): {s_Report}");
                            if (s_Report == null || s_Report.StartsWith("ERROR")) return false;
                        }

                        if (s_Names.Count > 4)
                            p_Writer.WriteLine($"    = {s_Names.Count} externals c{s_Register}..c{s_Register + s_Names.Count - 1}");
                        s_Patched++;

                        p_Writer.WriteLine($"      now: pixel constants={C("ConstantCount")}");
                        if (C("ExternalValues") is Array s_After)
                            foreach (var s_It in s_After)
                            {
                                object? G(string p_N) => s_It?.GetType().GetProperty(p_N)?.GetValue(s_It);
                                p_Writer.WriteLine($"      external c{G("Index")} '{G("Name")}' handle={G("Handle")} " +
                                                   $"size={G("Size")} array={G("ArraySize")}");
                            }
                    }
                }
            }

            if (s_Entries == 0) { p_Writer.WriteLine($"No shader matched '{Shader}'."); return false; }
            if (s_ListOnly) { p_Writer.WriteLine($"Listed {s_Seen.Count} pixel ShaderConstant(s) of {s_Entries} entr(ies)."); return true; }
            if (s_Patched == 0) { p_Writer.WriteLine($"'{Shader}' matched but no pixel constant was patched."); return false; }

            var s_Serialize = s_Container.GetType().GetMethod("Serialize", new[] { typeof(RimeWriter) });
            if (s_Serialize == null) { p_Writer.WriteLine("Container has no Serialize(RimeWriter)."); return false; }

            var s_Out = new FileInfo(Output!);
            if (s_Out.Directory != null && !s_Out.Directory.Exists) s_Out.Directory.Create();
            using (var s_Fs = File.Create(s_Out.FullName))
            using (var s_W = new RimeWriter(s_Fs, Endianness.LittleEndian, false))
                s_Serialize.Invoke(s_Container, new object[] { s_W });

            p_Writer.WriteLine($"Patched {s_Patched} pixel ShaderConstant(s) in {s_Entries} shader entr(ies) with external value '{Parameter}'.");
            p_Writer.WriteLine($"Wrote modified shaderdb -> {s_Out.FullName} ({new FileInfo(s_Out.FullName).Length} bytes)");
            return true;
        }
    }
}
