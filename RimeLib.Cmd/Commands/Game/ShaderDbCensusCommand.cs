using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimeLib;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.Shader;

namespace RimeLib.Cmd.Commands.Game
{
    [CommandDescription("Censuses every shader in a shaderdb to CSV: surface type, flags, render modes, and for " +
                        "each pixel permutation its instruction count, bytecode hash and binding shape " +
                        "(textures/externals/samplers + interpolator-independent counts). Use it to find how many " +
                        "DISTINCT shader families and binding contracts a level actually contains, instead of " +
                        "disassembling tens of thousands of permutations one by one.")]
    public class ShaderDbCensusCommand : Command
    {
        [CommandArgument(Description = "The shaderdb resource name, e.g. levels/mp_017/mp_017/shaderdb")]
        public string? Name { get; set; }

        [CommandArgument(Description = "Output CSV file path")]
        public FileInfo? Output { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Name) || Output == null)
            {
                p_Writer.WriteLine("Usage: shader_db_census <shaderdb> <out.csv>");
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

            var s_Rows = new List<string>
            {
                "path,shader,surfaceType,flags,solutions,mode,objectLighting,psInstr,psBytes,psHash," +
                "texCount,extValCount,extTexCount,samplerCount,streamableCount,constantFunctions",
            };

            var s_DistinctPs = new HashSet<string>();
            var s_DistinctBindingShape = new HashSet<string>();
            var s_Shaders_Total = 0;

            foreach (var s_PathKey in s_Databases.Keys)
            {
                var s_Db = s_Databases[s_PathKey];
                var s_Shaders = s_Db?.GetType().GetProperty("Shaders")?.GetValue(s_Db) as IDictionary;
                if (s_Shaders == null) continue;

                foreach (var s_ShKey in s_Shaders.Keys)
                {
                    var s_ShName = s_ShKey?.ToString() ?? "";
                    var s_Info = s_Shaders[s_ShKey];
                    if (s_Info == null) continue;

                    s_Shaders_Total++;

                    object? Prop(object? p_Obj, string p_Name) =>
                        p_Obj?.GetType().GetProperty(p_Name)?.GetValue(p_Obj);

                    var s_SurfaceType = Prop(s_Info, "SurfaceShaderType")?.ToString() ?? "";
                    var s_Flags = Prop(s_Info, "Flags")?.ToString() ?? "";
                    var s_Streamables = Prop(s_Info, "StreamableTextures") as Array;
                    var s_Sols = Prop(s_Info, "Solutions") as Array;
                    var s_SolCount = s_Sols?.Length ?? 0;

                    if (s_Sols == null) continue;

                    // One row per DISTINCT pixel permutation of the shader; solutions share instances heavily.
                    var s_SeenHere = new HashSet<object>(ReferenceEqualityComparer.Instance);

                    foreach (var s_Sol in s_Sols)
                    {
                        var s_Pp = Prop(s_Sol, "PixelPermutation");
                        if (s_Pp == null || !s_SeenHere.Add(s_Pp)) continue;

                        var s_State = Prop(s_Sol, "State");
                        var s_Mode = Prop(s_State, "Mode")?.ToString() ?? "";
                        var s_ObjLight = Prop(s_State, "ObjectLighting")?.ToString() ?? "";

                        var s_Bytecode = Prop(s_Pp, "ShaderBytecode") as byte[] ?? Array.Empty<byte>();
                        var s_Instr = Prop(s_Pp, "InstructionCount")?.ToString() ?? "";
                        var s_Hash = Convert.ToHexString(SHA256.HashData(s_Bytecode)).Substring(0, 16);
                        s_DistinctPs.Add(s_Hash);

                        var s_Constant = Prop(s_Pp, "Constant");
                        var s_TexCount = (Prop(s_Constant, "Textures") as Array)?.Length ?? 0;
                        var s_ExtValCount = (Prop(s_Constant, "ExternalValues") as Array)?.Length ?? 0;
                        var s_ExtTexCount = (Prop(s_Constant, "ExternalTextures") as Array)?.Length ?? 0;
                        var s_SamplerCount = (Prop(s_Constant, "Samplers") as Array)?.Length ?? 0;

                        // The set of engine-provided inputs this permutation asks for: the closest thing the
                        // database has to "which input nodes were wired into this graph".
                        // The entry arrays are named differently on the two holders, so probe both rather than
                        // silently emitting an empty column when the guess is wrong.
                        var s_Functions = new SortedSet<string>();
                        foreach (var s_Holder in new[] { Prop(s_Pp, "ConstantFunction"), Prop(s_Pp, "TextureFunction") })
                        {
                            if (s_Holder == null) continue;

                            Array? s_FunctionArray = null;
                            foreach (var s_Candidate in new[] { "Constants", "Textures", "Functions", "Entries" })
                            {
                                if (Prop(s_Holder, s_Candidate) is Array s_Found)
                                {
                                    s_FunctionArray = s_Found;
                                    break;
                                }
                            }

                            if (s_FunctionArray == null)
                            {
                                s_Functions.Add($"<unread:{s_Holder.GetType().Name}>");
                                continue;
                            }

                            foreach (var s_Function in s_FunctionArray)
                            {
                                var s_Value = Prop(s_Function, "Function")?.ToString();
                                if (!string.IsNullOrEmpty(s_Value))
                                    s_Functions.Add(s_Value!);
                            }
                        }

                        var s_Shape = $"{s_TexCount}/{s_ExtValCount}/{s_ExtTexCount}/{s_SamplerCount}/{s_Mode}";
                        s_DistinctBindingShape.Add(s_Shape);

                        s_Rows.Add(string.Join(",",
                            s_PathKey, Csv(s_ShName), s_SurfaceType, s_Flags, s_SolCount.ToString(),
                            s_Mode, s_ObjLight, s_Instr, s_Bytecode.Length.ToString(), s_Hash,
                            s_TexCount.ToString(), s_ExtValCount.ToString(), s_ExtTexCount.ToString(),
                            s_SamplerCount.ToString(), (s_Streamables?.Length ?? 0).ToString(),
                            Csv(string.Join("|", s_Functions))));
                    }
                }
            }

            if (Output.Directory != null && !Output.Directory.Exists)
                Output.Directory.Create();

            File.WriteAllLines(Output.FullName, s_Rows);

            p_Writer.WriteLine($"CENSUS: shaders={s_Shaders_Total} rows={s_Rows.Count - 1} " +
                               $"distinctPixelBytecodes={s_DistinctPs.Count} " +
                               $"distinctBindingShapes={s_DistinctBindingShape.Count}");
            p_Writer.WriteLine($"Wrote {Output.FullName}");
            return true;
        }

        private static string Csv(string p_Value) =>
            p_Value.Contains(',') || p_Value.Contains('"')
                ? $"\"{p_Value.Replace("\"", "\"\"")}\""
                : p_Value;
    }
}
