using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using RimeLib;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.Shader;

namespace RimeLib.Cmd.Commands.Game
{
    [CommandDescription("Tallies every ShaderConstantFunction used by a shaderdb's ConstantFunctions/TextureFunctions " +
                        "(with counts). Use to check whether a level provides engine inputs like SceneTexture(46), " +
                        "DepthBufferTexture(24) or OutdoorLightSkyEnvmap(38) before authoring a shader that binds them.")]
    public class ShaderDbFunctionsCommand : Command
    {
        [CommandArgument(Description = "The shaderdb resource name, e.g. levels/mp_017/mp_017/shaderdb")]
        public string? Name { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                p_Writer.WriteLine("Usage: shader_db_functions <resource-name>");
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
            if (s_Databases == null) { p_Writer.WriteLine("Shaderdb parsed to null / no databases."); return false; }

            var s_TextureFns = new SortedDictionary<string, int>();
            var s_ConstantFns = new SortedDictionary<string, int>();

            foreach (var s_Key in s_Databases.Keys)
            {
                var s_Db = s_Databases[s_Key];

                // TextureFunctions[].Textures[].Function  (this is where SceneTexture/DepthBuffer/SkyEnvmap live)
                if (s_Db!.GetType().GetProperty("TextureFunctions")?.GetValue(s_Db) is Array s_Tfs)
                    foreach (var s_Tf in s_Tfs)
                        if (s_Tf?.GetType().GetProperty("Textures")?.GetValue(s_Tf) is Array s_Texs)
                            foreach (var s_Tex in s_Texs)
                                Tally(s_TextureFns, s_Tex?.GetType().GetProperty("Function")?.GetValue(s_Tex)?.ToString());

                // ConstantFunctions[].Constants[].Function
                if (s_Db.GetType().GetProperty("ConstantFunctions")?.GetValue(s_Db) is Array s_Cfs)
                    foreach (var s_Cf in s_Cfs)
                        if (s_Cf?.GetType().GetProperty("Constants")?.GetValue(s_Cf) is Array s_Cs)
                            foreach (var s_C in s_Cs)
                                Tally(s_ConstantFns, s_C?.GetType().GetProperty("Function")?.GetValue(s_C)?.ToString());
            }

            p_Writer.WriteLine($"=== TEXTURE functions (register bindings) in {Name} ===");
            foreach (var s_Kv in s_TextureFns)
                p_Writer.WriteLine($"  {s_Kv.Key} x{s_Kv.Value}" + Flag(s_Kv.Key));

            p_Writer.WriteLine($"=== CONSTANT functions in {Name} ===");
            foreach (var s_Kv in s_ConstantFns)
                p_Writer.WriteLine($"  {s_Kv.Key} x{s_Kv.Value}" + Flag(s_Kv.Key));

            return true;
        }

        static void Tally(SortedDictionary<string, int> p_Map, string? p_Name)
        {
            if (string.IsNullOrEmpty(p_Name)) return;
            p_Map[p_Name!] = p_Map.TryGetValue(p_Name!, out var s_V) ? s_V + 1 : 1;
        }

        static string Flag(string p_Name)
        {
            var s_L = p_Name.ToLowerInvariant();
            if (s_L.Contains("scenetexture")) return "   <-- REFRACTION source (46)";
            if (s_L.Contains("depthbuffer")) return "   <-- DEPTH (24)";
            if (s_L.Contains("skyenvmap") || s_L.Contains("dynamicenvmap")) return "   <-- REFLECTION envmap (38/39)";
            return "";
        }
    }
}
