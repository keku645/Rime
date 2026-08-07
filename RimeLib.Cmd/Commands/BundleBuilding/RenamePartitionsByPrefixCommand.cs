using System.IO;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;

namespace RimeLib.Cmd.Commands.BundleBuilding
{
    [CommandDescription("Re-keys every partition in the current bundle whose name starts with <old_prefix> " +
        "so it starts with <new_prefix> instead (case-insensitive), keeping its data. Companion to " +
        "clone_bundle, which copies partitions under their donor OriginalName (e.g. levels/mp_subway/" +
        "teamdeathmatch). The engine looks up a sublevel's root DataContainer BY NAME " +
        "(fb::ResourceManager::lookupDataContainer(compartment, name), reached from the level-load sublevel " +
        "recursion sub_11A1170); when a SubWorldReference's BundleName is levels/<newlevel>/... but the " +
        "cloned partition is still named levels/<donor>/..., the lookup returns NULL and the server/client " +
        "crash on load (sub_11A1170, read from NULL+0x20). Inter-partition references are by GUID (imports), " +
        "so renaming names does NOT break them. Run AFTER clone_bundle (and its resolves), BEFORE build. " +
        "Example: rename_partitions_by_prefix levels/mp_subway/teamdeathmatch levels/realitymod/teamdeathmatch")]
    internal class RenamePartitionsByPrefixCommand : Command
    {
        [CommandArgument(Description = "Donor partition-name prefix to match, e.g. \"levels/mp_subway/teamdeathmatch\".")]
        public string? OldPrefix { get; set; }

        [CommandArgument(Description = "Replacement prefix, e.g. \"levels/realitymod/teamdeathmatch\".")]
        public string? NewPrefix { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(OldPrefix) || string.IsNullOrWhiteSpace(NewPrefix))
            {
                p_Writer.WriteLine("usage: rename_partitions_by_prefix <old_prefix> <new_prefix>");
                return false;
            }

            var s_BundleContext = (BundleBuildingContext)p_Context;
            var s_Renamed = s_BundleContext.RenamePartitionsByPrefix(OldPrefix, NewPrefix);

            p_Writer.WriteLine($"rename_partitions_by_prefix '{OldPrefix}' -> '{NewPrefix}': renamed {s_Renamed} partition(s).");
            return true;
        }
    }
}
