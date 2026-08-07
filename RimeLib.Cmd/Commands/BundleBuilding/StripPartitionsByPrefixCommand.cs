using System;
using System.IO;
using System.Linq;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;

namespace RimeLib.Cmd.Commands.BundleBuilding
{
    [CommandDescription("Removes from the current bundle every partition whose name starts with the given " +
        "prefix (case-insensitive). Robust replacement for a hand-maintained remove_partition list: the " +
        "resolve_partition_dependencies closure changes whenever the level references new content, so a " +
        "fixed list silently leaves new partitions behind. Used to strip the ALWAYS-MOUNTED-global `sound/` " +
        "closure (which must be referenced, not copied — copying makes the level compartment re-register the " +
        "waves and prime them turbo out-of-window -> vu+0xc1c76). Run AFTER resolve_partition_dependencies.")]
    internal class StripPartitionsByPrefixCommand : Command
    {
        [CommandArgument(Description = "Partition-name prefix to strip, e.g. \"sound/\" (case-insensitive).")]
        public string? Prefix { get; set; }

        [CommandArgument(Description = "Optional comma-separated sub-prefixes EXEMPT from the strip, e.g. \"sound/vo/\". " +
            "A retail level DOES ship these (mp_subway's main bundle carries 1314 sound/ partitions, 292 of them sound/vo/): " +
            "the EBX that defines dialog groups/tracks must be resident because the level's own sound/vo/logic/* partitions " +
            "import them - stripping the imports leaves VoiceOverDialogGroup's asset ref NULL and the engine derefs it " +
            "(+0x24e5f5) the first time a VO line plays. Their WAVE chunks live in the always-mounted loc/* and are NOT " +
            "needed in the bundle.", Optional = true)]
        public string? ExemptPrefixes { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Prefix))
            {
                p_Writer.WriteLine("usage: strip_partitions_by_prefix <prefix>");
                return false;
            }

            var s_BundleContext = (BundleBuildingContext)p_Context;
            var s_Prefix = Prefix.ToLowerInvariant();

            var s_Exempt = string.IsNullOrWhiteSpace(ExemptPrefixes)
                ? System.Array.Empty<string>()
                : ExemptPrefixes!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                 .Select(p_E => p_E.ToLowerInvariant()).ToArray();

            var s_Names = s_BundleContext.GetPartitions().Keys
                .Where(k => k.ToLowerInvariant().StartsWith(s_Prefix)
                            && !s_Exempt.Any(p_E => k.ToLowerInvariant().StartsWith(p_E)))
                .ToList();

            var s_Removed = 0;
            foreach (var s_Name in s_Names)
            {
                try
                {
                    s_BundleContext.RemovePartition(s_Name);
                    s_Removed++;
                }
                catch (Exception s_Ex)
                {
                    p_Writer.WriteLine($"Warning: could not remove '{s_Name}': {s_Ex.Message}");
                }
            }

            p_Writer.WriteLine($"strip_partitions_by_prefix '{Prefix}': removed {s_Removed} partition(s).");
            return true;
        }
    }
}
