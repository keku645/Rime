using System.IO;
using System.Linq;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;
using RimeLib.Content.Mounting;

namespace RimeLib.Cmd.Commands.BundleBuilding
{
    [CommandDescription("Reports whether the current bundle's chunks/resources/partitions are CAS-REFERENCEABLE (Cas=true -> delivered as a SHA1 reference, ~0 bytes in the .sb) or INLINE (Cas=false -> bytes embedded, big). Cas is true only when the mounted variant reads from the CAS catalog (ObjectVariant: CatalogReadable/CasChunkEntry); anything read out of a superbundle's own .sb inlines. NOTE: the totals are ORIGINAL SIZES, so a large CAS-referenceable figure is NOT bloat - only the INLINE figure lands in the .sb. Optional arg top_inline=N lists the N biggest INLINE items (kind, name, MB, source superbundle) - that is what actually explains a fat .sb.")]
    internal class BundleStatsCommand : Command
    {
        [CommandArgument(Description = "Optional: list the N biggest INLINE (non-cas) items with their name and source superbundle. 0/absent = totals only.", Optional = true)]
        public int TopInline { get; set; } = 0;

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            var s_Ctx = p_Context as BundleBuildingContext;
            if (s_Ctx == null) { p_Writer.WriteLine("run inside a bundle-building context."); return false; }

            long s_CasBytes = 0, s_InlineBytes = 0, s_UnknownBytes = 0;
            int s_Cas = 0, s_Inline = 0, s_Unknown = 0;
            var s_InlineItems = new System.Collections.Generic.List<(string Kind, string Name, long Size, string Src)>();

            void Tally(string p_Kind, string p_Name, object p_Obj, long p_Size)
            {
                if (p_Obj is IObjectVariant s_V)
                {
                    if (s_V.Cas) { s_Cas++; s_CasBytes += p_Size; }
                    else
                    {
                        s_Inline++; s_InlineBytes += p_Size;
                        if (TopInline > 0)
                        {
                            var s_Src = "?";
                            try { s_Src = s_V.GetContainedSuperbundle() ?? "?"; } catch { }
                            s_InlineItems.Add((p_Kind, p_Name, p_Size, s_Src));
                        }
                    }
                }
                else { s_Unknown++; s_UnknownBytes += p_Size; }   // file/memory-backed (generated) -> inline
            }

            foreach (var s_Kv in s_Ctx.GetChunks())   Tally("chunk", s_Kv.Key.ToString() ?? "?", s_Kv.Value, s_Kv.Value.GetSize());
            foreach (var s_Kv in s_Ctx.GetResources()) Tally("res", s_Kv.Key.ToString() ?? "?", s_Kv.Value, s_Kv.Value.GetSize());
            foreach (var s_Kv in s_Ctx.GetPartitions())
            {
                long s_Sz = 0; try { s_Sz = s_Kv.Value.GetSize(); } catch { }
                Tally("part", s_Kv.Key.ToString() ?? "?", s_Kv.Value, s_Sz);
            }

            p_Writer.WriteLine($"bundle_stats: CAS-referenceable = {s_Cas} items / {s_CasBytes / 1024 / 1024} MB  |  INLINE(non-cas variant) = {s_Inline} items / {s_InlineBytes / 1024 / 1024} MB  |  generated/file-backed = {s_Unknown} items / {s_UnknownBytes / 1024 / 1024} MB");
            p_Writer.WriteLine($"  -> ONLY the INLINE figure becomes bytes in the .sb; CAS-referenceable ships as SHA1 refs (~0). Use top_inline=N to see what the inline bytes actually are.");

            if (TopInline > 0 && s_InlineItems.Count > 0)
            {
                p_Writer.WriteLine($"  top {TopInline} INLINE items (of {s_InlineItems.Count}):");
                foreach (var s_It in s_InlineItems.OrderByDescending(p_I => p_I.Size).Take(TopInline))
                    p_Writer.WriteLine($"    {s_It.Size / 1024 / 1024,6} MB  [{s_It.Kind}] {s_It.Name}   <- {s_It.Src}");

                var s_BySrc = s_InlineItems.GroupBy(p_I => p_I.Src)
                                           .Select(p_G => new { Src = p_G.Key, Bytes = p_G.Sum(p_I => p_I.Size), Count = p_G.Count() })
                                           .OrderByDescending(p_G => p_G.Bytes);
                p_Writer.WriteLine("  INLINE bytes by SOURCE superbundle:");
                foreach (var s_G in s_BySrc)
                    p_Writer.WriteLine($"    {s_G.Bytes / 1024 / 1024,6} MB  ({s_G.Count} items)  {s_G.Src}");
            }
            return true;
        }
    }
}
