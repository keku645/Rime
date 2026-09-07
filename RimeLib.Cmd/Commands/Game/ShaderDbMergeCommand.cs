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
    [CommandDescription("Merges two mods' shader databases of the SAME level into one, using the mounted " +
                        "vanilla database to tell what each mod actually changed: fresh-named additions " +
                        "(variation clones) transplant whole, replaced vanilla bytecode carries over spot by " +
                        "spot, and any place both mods changed differently is refused loudly. Chain the " +
                        "output as the next merge's base to fold N mods into one database.")]
    public class ShaderDbMergeCommand : Command
    {
        [CommandArgument(Description = "The vanilla shaderdb resource name, e.g. levels/mp_017/mp_017/shaderdb")]
        public string? Name { get; set; }

        [CommandArgument(Description = "BASE database file (a mod's full-level shaderdb .bin); the merge folds " +
                                       "the other file's changes into this one.")]
        public FileInfo? Base { get; set; }

        [CommandArgument(Description = "OTHER database file whose changes (vs vanilla) are folded into the base.")]
        public FileInfo? Other { get; set; }

        [CommandArgument(Description = "Output shaderdb file path.")]
        public FileInfo? Output { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Name) || Base == null || Other == null || Output == null)
            {
                p_Writer.WriteLine("Usage: shader_db_merge <vanilla-shaderdb> <base.bin> <other.bin> <out.bin>");
                return false;
            }

            if (!Base.Exists) { p_Writer.WriteLine($"Base file not found: {Base.FullName}"); return false; }
            if (!Other.Exists) { p_Writer.WriteLine($"Other file not found: {Other.FullName}"); return false; }

            var s_Mounter = ((GameContext) p_Context).GetMounter();
            var s_Resolver = EngineInterfaceRegistry.Create<IShaderResolver>(s_Mounter.GetEngineType());
            var s_ContainerType = s_Resolver.GetType().GetProperty("ShaderDatabaseContainer")?.PropertyType;
            if (s_ContainerType == null)
            {
                p_Writer.WriteLine("Resolver exposes no ShaderDatabaseContainer.");
                return false;
            }

            object? ParseFile(FileInfo p_File)
            {
                using var s_Ms = new MemoryStream(File.ReadAllBytes(p_File.FullName));
                using var s_Rd = new RimeReader(s_Ms, Endianness.LittleEndian, false);
                var s_Parsed = Activator.CreateInstance(s_ContainerType, s_Rd, s_Mounter);
                p_Writer.WriteLine($"Parsed {p_File.Name} ({s_Ms.Length} bytes)");
                return s_Parsed;
            }

            if (!s_Mounter.TryGetResource(Name!, out var s_Resource) || s_Resource.FirstVariant == null)
            {
                p_Writer.WriteLine($"Could not find the vanilla shaderdb resource ({Name}).");
                return false;
            }

            s_Resolver.Initialize(s_Resource.FirstVariant, s_Mounter);
            var s_VanillaContainer = s_Resolver.GetType().GetProperty("ShaderDatabaseContainer")?.GetValue(s_Resolver);
            var s_BaseContainer = ParseFile(Base);
            var s_OtherContainer = ParseFile(Other);
            if (s_VanillaContainer == null || s_BaseContainer == null || s_OtherContainer == null)
            {
                p_Writer.WriteLine("A database parsed to null.");
                return false;
            }

            IDictionary? DatabasesOf(object p_Container) =>
                p_Container.GetType().GetProperty("Databases")?.GetValue(p_Container) as IDictionary;

            var s_BaseDbs = DatabasesOf(s_BaseContainer);
            var s_OtherDbs = DatabasesOf(s_OtherContainer);
            var s_VanillaDbs = DatabasesOf(s_VanillaContainer);
            if (s_BaseDbs == null || s_OtherDbs == null || s_VanillaDbs == null)
            {
                p_Writer.WriteLine("No databases.");
                return false;
            }

            // Every render path must merge or none does: a database whose Dx11 carries a mod that its
            // Dx10Plus lost is exactly the kind of half-artifact that looks fine on one machine.
            foreach (var s_PathKey in s_BaseDbs.Keys)
            {
                if (!s_OtherDbs.Contains(s_PathKey) || !s_VanillaDbs.Contains(s_PathKey))
                {
                    p_Writer.WriteLine($"ERROR: render path '{s_PathKey}' is not present in all three databases.");
                    return false;
                }

                var s_Db = s_BaseDbs[s_PathKey];
                var s_Merge = s_Db?.GetType().GetMethod("MergeFrom");
                if (s_Merge == null)
                {
                    p_Writer.WriteLine("ShaderDatabase has no MergeFrom (stale assembly?).");
                    return false;
                }

                var s_Report = s_Merge.Invoke(s_Db,
                    new[] { s_OtherDbs[s_PathKey], s_VanillaDbs[s_PathKey], p_Writer }) as string;
                p_Writer.WriteLine($"[{s_PathKey}] {s_Report}");

                if (s_Report == null || s_Report.StartsWith("ERROR", StringComparison.Ordinal))
                {
                    p_Writer.WriteLine("MERGE FAILED: nothing was written.");
                    return false;
                }
            }

            var s_Serialize = s_BaseContainer.GetType().GetMethod("Serialize", new[] { typeof(RimeWriter) });
            if (s_Serialize == null)
            {
                p_Writer.WriteLine("Container has no Serialize(RimeWriter).");
                return false;
            }

            if (Output.Directory != null && !Output.Directory.Exists)
                Output.Directory.Create();

            using (var s_Fs = File.Create(Output.FullName))
            using (var s_W = new RimeWriter(s_Fs, Endianness.LittleEndian, false))
                s_Serialize.Invoke(s_BaseContainer, new object[] { s_W });

            p_Writer.WriteLine($"Wrote merged shaderdb -> {Output.FullName} ({new FileInfo(Output.FullName).Length} bytes)");
            return true;
        }
    }
}
