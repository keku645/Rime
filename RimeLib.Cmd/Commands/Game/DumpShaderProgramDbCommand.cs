using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using RimeLib;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.Shader;

namespace RimeLib.Cmd.Commands.Game
{
    [CommandDescription("Lists (and optionally extracts) the engine's OWN shader programs from " +
                        "Systems/ShaderProgramDb — the fullscreen/lighting/post passes, which are NOT surface " +
                        "shaders and so never appear in a level shaderdb. Filter by name substring; give an " +
                        "output directory to write each match's DXBC for disassembly.")]
    public class DumpShaderProgramDbCommand : Command
    {
        [CommandArgument(Description = "The program-db resource name, normally Systems/ShaderProgramDb")]
        public string? Name { get; set; }

        [CommandArgument(Description = "Name substring filter; use * or leave blank-ish to list everything",
                         Optional = true)]
        public string? Filter { get; set; }

        [CommandArgument(Description = "Output directory for the extracted .dxbc files", Optional = true)]
        public DirectoryInfo? Output { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                p_Writer.WriteLine("Usage: dump_shader_program_db <resource> [name-substring] [out-dir]");
                return false;
            }

            var s_Mounter = ((GameContext)p_Context).GetMounter();
            if (!s_Mounter.TryGetResource(Name!, out var s_Resource) || s_Resource.FirstVariant == null)
            {
                p_Writer.WriteLine($"Could not find resource ({Name}).");
                return false;
            }

            // The program database is not exposed through IShaderResolver, so reach its type through the
            // engine-specific shader assembly the resolver already comes from.
            var s_Resolver = EngineInterfaceRegistry.Create<IShaderResolver>(s_Mounter.GetEngineType());
            var s_Assembly = s_Resolver.GetType().Assembly;
            var s_ContainerType = s_Assembly.GetTypes()
                .FirstOrDefault(p_T => p_T.Name == "ShaderProgramDatabaseContainer");
            var s_StageType = s_Assembly.GetTypes().FirstOrDefault(p_T => p_T.Name == "ShaderStageType")
                              ?? AppDomain.CurrentDomain.GetAssemblies()
                                  .SelectMany(p_A =>
                                  {
                                      try { return p_A.GetTypes(); }
                                      catch { return Type.EmptyTypes; }
                                  })
                                  .FirstOrDefault(p_T => p_T.Name == "ShaderStageType");
            var s_PathType = s_Assembly.GetTypes().FirstOrDefault(p_T => p_T.Name == "ShaderRenderPath")
                             ?? AppDomain.CurrentDomain.GetAssemblies()
                                 .SelectMany(p_A =>
                                 {
                                     try { return p_A.GetTypes(); }
                                     catch { return Type.EmptyTypes; }
                                 })
                                 .FirstOrDefault(p_T => p_T.Name == "ShaderRenderPath");

            if (s_ContainerType == null || s_StageType == null || s_PathType == null)
            {
                p_Writer.WriteLine("Could not resolve the program-database types for this engine.");
                return false;
            }

            using var s_Reader = s_Resource.FirstVariant.GetReader();
            var s_Container = Activator.CreateInstance(s_ContainerType, s_Reader);
            if (s_Container == null)
            {
                p_Writer.WriteLine("Program database parsed to null.");
                return false;
            }

            var s_TryGet = s_ContainerType.GetMethod("TryGetDatabase");
            if (s_TryGet == null)
            {
                p_Writer.WriteLine("Container has no TryGetDatabase.");
                return false;
            }

            var s_Needle = (Filter ?? "").Trim();
            if (s_Needle == "*")
                s_Needle = "";
            s_Needle = s_Needle.ToLowerInvariant();

            var s_Total = 0;
            var s_Written = 0;

            foreach (var s_Path in Enum.GetValues(s_PathType))
            {
                var s_Args = new object?[] { s_Path, null };
                if (s_TryGet.Invoke(s_Container, s_Args) is not true || s_Args[1] == null)
                    continue;

                var s_Db = s_Args[1]!;
                var s_GetNames = s_Db.GetType().GetMethod("GetShaderNames");
                var s_TryGetShader = s_Db.GetType().GetMethod("TryGetShader");
                if (s_GetNames == null || s_TryGetShader == null)
                    continue;

                foreach (var s_Stage in Enum.GetValues(s_StageType))
                {
                    if (s_GetNames.Invoke(s_Db, new[] { s_Stage }) is not IEnumerable s_Names)
                        continue;

                    foreach (var s_NameObj in s_Names)
                    {
                        var s_ShaderName = s_NameObj?.ToString() ?? "";
                        if (s_Needle.Length > 0 && !s_ShaderName.ToLowerInvariant().Contains(s_Needle))
                            continue;

                        s_Total++;

                        var s_ShaderArgs = new object?[] { s_Stage, s_ShaderName, null };
                        var s_Got = s_TryGetShader.Invoke(s_Db, s_ShaderArgs) is true;
                        var s_Length = 0;

                        if (s_Got && s_ShaderArgs[2] != null)
                        {
                            var s_Info = s_ShaderArgs[2]!;
                            var s_Data = s_Info.GetType().GetProperty("Data")?.GetValue(s_Info) as byte[];
                            s_Length = s_Data?.Length ?? 0;

                            if (Output != null && s_Data is { Length: > 0 })
                            {
                                if (!Output.Exists) Output.Create();
                                var s_File = Path.Combine(Output.FullName,
                                    $"{s_Path}_{s_Stage}_{Sanitize(s_ShaderName)}.dxbc");
                                File.WriteAllBytes(s_File, s_Data);
                                s_Written++;
                            }
                        }

                        p_Writer.WriteLine($"SPDB: [{s_Path}] {s_Stage} {s_ShaderName} bytes={s_Length}");
                    }
                }
            }

            p_Writer.WriteLine($"SPDB-DONE: matched={s_Total} written={s_Written}");
            return s_Total > 0;
        }

        private static string Sanitize(string p_Value)
        {
            var s_Chars = p_Value.Select(p_C => char.IsLetterOrDigit(p_C) ? p_C : '_').ToArray();
            return new string(s_Chars);
        }
    }
}
