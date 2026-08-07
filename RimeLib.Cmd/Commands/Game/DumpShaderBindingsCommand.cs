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
    [CommandDescription("Dumps the full binding structure (pixel ShaderConstant textures/externals/samplers + the " +
                        "ConstantFunctionData + TextureFunctionData with their ShaderConstantFunction->register map) of " +
                        "shaders whose pixel permutation binds a given ShaderConstantFunction. The authoring template " +
                        "for writing a new shader entry that binds engine inputs (SkyEnvmap/DepthBuffer/etc.).")]
    public class DumpShaderBindingsCommand : Command
    {
        [CommandArgument(Description = "The shaderdb resource name, e.g. levels/mp_017/mp_017/shaderdb")]
        public string? Name { get; set; }

        [CommandArgument(Description = "ShaderConstantFunction substring to search for, e.g. SkyEnvmap or DepthBuffer")]
        public string? Function { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Function))
            {
                p_Writer.WriteLine("Usage: dump_shader_bindings <resource> <function-substr>");
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
            if (s_Databases == null) { p_Writer.WriteLine("null databases"); return false; }

            string s_Needle = Function!.ToLowerInvariant();
            int s_Shown = 0;

            foreach (var s_PathKey in s_Databases.Keys)
            {
                var s_Db = s_Databases[s_PathKey];
                var s_Shaders = s_Db?.GetType().GetProperty("Shaders")?.GetValue(s_Db) as IDictionary;
                if (s_Shaders == null) continue;

                foreach (var s_ShKey in s_Shaders.Keys)
                {
                    if (s_Shown >= 2) break;
                    var s_Info = s_Shaders[s_ShKey];
                    var s_Sols = s_Info?.GetType().GetProperty("Solutions")?.GetValue(s_Info) as Array;
                    if (s_Sols == null) continue;

                    foreach (var s_Sol in s_Sols)
                    {
                        var s_Pp = s_Sol?.GetType().GetProperty("PixelPermutation")?.GetValue(s_Sol);
                        if (s_Pp == null) continue;
                        var s_Tf = s_Pp.GetType().GetProperty("TextureFunction")?.GetValue(s_Pp);
                        if (!FunctionListHas(s_Tf, "Textures", s_Needle)) continue;

                        p_Writer.WriteLine($"######## SHADER: [{s_PathKey}] {s_ShKey} ########");
                        DumpConstant(p_Writer, s_Sol.GetType().GetProperty("PixelConstants")?.GetValue(s_Sol), "PixelConstants");
                        DumpTextureFunction(p_Writer, s_Tf);
                        DumpConstantFunction(p_Writer, s_Pp.GetType().GetProperty("ConstantFunction")?.GetValue(s_Pp));
                        s_Shown++;
                        break;
                    }
                }
            }
            if (s_Shown == 0) p_Writer.WriteLine($"No shader pixel-perm binds a texture function matching '{Function}'.");
            return true;
        }

        static bool FunctionListHas(object? p_Obj, string p_ArrProp, string p_Needle)
        {
            if (p_Obj == null) return false;
            if (p_Obj.GetType().GetProperty(p_ArrProp)?.GetValue(p_Obj) is Array s_Arr)
                foreach (var s_It in s_Arr)
                {
                    var s_Fn = s_It?.GetType().GetProperty("Function")?.GetValue(s_It)?.ToString();
                    if (s_Fn != null && s_Fn.ToLowerInvariant().Contains(p_Needle)) return true;
                }
            return false;
        }

        static void DumpTextureFunction(TextWriter p_Writer, object? p_Tf)
        {
            p_Writer.WriteLine("  -- TextureFunction (register <- engine function) --");
            if (p_Tf?.GetType().GetProperty("Textures")?.GetValue(p_Tf) is Array s_Arr)
                foreach (var s_It in s_Arr)
                {
                    object? G(string n) => s_It?.GetType().GetProperty(n)?.GetValue(s_It);
                    p_Writer.WriteLine($"     t? Index={G("Index")} Function={G("Function")} ValueType={G("ValueType")} Parameter={G("Parameter")}");
                }
        }

        static void DumpConstantFunction(TextWriter p_Writer, object? p_Cf)
        {
            p_Writer.WriteLine("  -- ConstantFunction (cbuffer reg <- engine function) --");
            if (p_Cf?.GetType().GetProperty("Constants")?.GetValue(p_Cf) is Array s_Arr)
                foreach (var s_It in s_Arr)
                {
                    object? G(string n) => s_It?.GetType().GetProperty(n)?.GetValue(s_It);
                    p_Writer.WriteLine($"     Function={G("Function")} Index={G("Index")} Parameter={G("Parameter")} ArraySize={G("ArraySize")} VectorCount={G("VectorCount")}");
                }
        }

        static void DumpConstant(TextWriter p_Writer, object? p_Const, string p_Tag)
        {
            if (p_Const == null) { p_Writer.WriteLine($"  -- {p_Tag}: null --"); return; }
            object? P(string n) => p_Const.GetType().GetProperty(n)?.GetValue(p_Const);
            p_Writer.WriteLine($"  -- {p_Tag}: ConstantCount={P("ConstantCount")} ValueConstantsStart={P("ValueConstantsStart")} --");
            if (P("Textures") is Array s_Tex)
                foreach (var s_It in s_Tex)
                {
                    object? G(string n) => s_It?.GetType().GetProperty(n)?.GetValue(s_It);
                    p_Writer.WriteLine($"     TextureConstant Name=\"{G("Name")}\" (streamable texture slot)");
                }
            if (P("Samplers") is Array s_Sm) p_Writer.WriteLine($"     Samplers x{s_Sm.Length}");
            if (P("ValueConstants") is Array s_Vc) p_Writer.WriteLine($"     ValueConstants x{s_Vc.Length}");
            if (P("ExternalValues") is Array s_Ev) p_Writer.WriteLine($"     ExternalValues x{s_Ev.Length}");
            if (P("ExternalTextures") is Array s_Et) p_Writer.WriteLine($"     ExternalTextures x{s_Et.Length}");
        }
    }
}
