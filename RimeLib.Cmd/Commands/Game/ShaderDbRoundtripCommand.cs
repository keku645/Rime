using System;
using System.IO;
using System.Reflection;
using RimeLib;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.IO;
using RimeLib.IO.Conversion;
using RimeLib.Shader;

namespace RimeLib.Cmd.Commands.Game
{
    [CommandDescription("Round-trips a mounted shaderdb: reads the container via the engine's shader resolver, " +
                        "re-serializes it and reports whether the result is byte-identical to the original resource. " +
                        "Validates the shader-database writer before authoring/injecting a custom shader.")]
    public class ShaderDbRoundtripCommand : Command
    {
        [CommandArgument(Description = "The shaderdb resource name, e.g. levels/mp_017/mp_017/shaderdb")]
        public string? Name { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                p_Writer.WriteLine("Usage: shaderdb_roundtrip <resource-name>");
                return false;
            }

            try
            {
                var s_Mounter = ((GameContext)p_Context).GetMounter();

                if (!s_Mounter.TryGetResource(Name!, out var s_Resource) || s_Resource.FirstVariant == null)
                {
                    p_Writer.WriteLine($"Could not find shaderdb resource ({Name}).");
                    return false;
                }

                var s_Variant = s_Resource.FirstVariant;

                // Original resource bytes.
                byte[] s_Original;
                using (var s_Reader0 = s_Variant.GetReader())
                    s_Original = s_Reader0.ReadBytes((int)s_Reader0.Length);

                // Parse via the engine's shader resolver and reflect the container out, so this command stays
                // engine-agnostic (same pattern as dump_shader_db). Frostbite2_0 exposes ShaderDatabaseContainer,
                // whose Serialize(RimeWriter) mirrors its reader.
                var s_Resolver = EngineInterfaceRegistry.Create<IShaderResolver>(s_Mounter.GetEngineType());
                s_Resolver.Initialize(s_Variant, s_Mounter);

                var s_Container = s_Resolver.GetType().GetProperty("ShaderDatabaseContainer")?.GetValue(s_Resolver);
                if (s_Container == null)
                {
                    p_Writer.WriteLine("Shaderdb parsed to null (unsupported version or not a shaderdb resource).");
                    return false;
                }

                var s_SerializeMethod = s_Container.GetType().GetMethod("Serialize", new[] { typeof(RimeWriter) });
                if (s_SerializeMethod == null)
                {
                    p_Writer.WriteLine("This shaderdb container exposes no Serialize(RimeWriter) (writer not implemented for this engine).");
                    return false;
                }

                byte[] s_Rewritten;
                using (var s_Ms = new MemoryStream())
                {
                    using (var s_Writer = new RimeWriter(s_Ms, Endianness.LittleEndian, false))
                        s_SerializeMethod.Invoke(s_Container, new object[] { s_Writer });

                    s_Rewritten = s_Ms.ToArray();
                }

                // Optional context: render-path count.
                var s_Databases = s_Container.GetType().GetProperty("Databases")?.GetValue(s_Container) as System.Collections.IDictionary;
                var s_DbCount = s_Databases?.Count ?? 0;

                p_Writer.WriteLine($"[shaderdb_roundtrip] {Name}: orig={s_Original.Length}B rewritten={s_Rewritten.Length}B databases={s_DbCount}");

                var s_Len = System.Math.Min(s_Original.Length, s_Rewritten.Length);
                var s_FirstDiff = -1;
                for (var i = 0; i < s_Len; ++i)
                {
                    if (s_Original[i] != s_Rewritten[i]) { s_FirstDiff = i; break; }
                }

                if (s_FirstDiff < 0 && s_Original.Length == s_Rewritten.Length)
                    p_Writer.WriteLine($"  round-trip: BYTE-IDENTICAL over {s_Original.Length} bytes [OK]");
                else if (s_FirstDiff < 0)
                    p_Writer.WriteLine($"  round-trip: first {s_Len} bytes identical but LENGTH differs (orig={s_Original.Length} new={s_Rewritten.Length}) -- diverges at/after 0x{s_Len:X}");
                else
                    p_Writer.WriteLine($"  round-trip: DIFF at byte 0x{s_FirstDiff:X} (orig=0x{s_Original[s_FirstDiff]:X2} new=0x{s_Rewritten[s_FirstDiff]:X2}) of orig={s_Original.Length} new={s_Rewritten.Length}");

                return true;
            }
            catch (TargetInvocationException s_Exception)
            {
                // Unwrap reflection so the real Serialize error surfaces.
                p_Writer.WriteLine($"Could not round-trip shader database. Error: {(s_Exception.InnerException ?? s_Exception).Message}");
                return false;
            }
            catch (Exception s_Exception)
            {
                p_Writer.WriteLine($"Could not round-trip shader database. Error: {s_Exception.Message}");
                return false;
            }
        }
    }
}
