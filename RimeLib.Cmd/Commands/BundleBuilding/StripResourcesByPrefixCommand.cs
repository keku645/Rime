using System;
using System.IO;
using System.Linq;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;

namespace RimeLib.Cmd.Commands.BundleBuilding
{
    [CommandDescription("Removes from the current bundle every RESOURCE whose name starts with the given " +
        "prefix (case-insensitive). Companion to strip_partitions_by_prefix (which only removes partitions). " +
        "Used to drop GLOBAL shared assets that resolve_partition_dependencies wrongly COPIED into the level " +
        "bundle — e.g. the deploy/end-of-round backdrop diorama (objects/ui_characterbackdrop, objects/" +
        "deliveryvan, objects/spotlight, ...) which the game keeps ALWAYS LOADED below the map. A copied " +
        "mesh resource whose streaming chunk is not resident makes the client mesh proxy bind NULL and crash " +
        "(fb::MeshProxyEntity::dataUpdate -> movzx [NULL+0Ch]). Stripping the copy lets the ResourceManager " +
        "resolve the resource from the always-mounted global bundle instead. Run AFTER the resolves.")]
    internal class StripResourcesByPrefixCommand : Command
    {
        [CommandArgument(Description = "Resource-name prefix to strip, e.g. \"objects/\" (case-insensitive).")]
        public string? Prefix { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            if (string.IsNullOrWhiteSpace(Prefix))
            {
                p_Writer.WriteLine("usage: strip_resources_by_prefix <prefix>");
                return false;
            }

            var s_BundleContext = (BundleBuildingContext)p_Context;
            var s_Prefix = Prefix.ToLowerInvariant();

            var s_Names = s_BundleContext.GetResources().Keys
                .Where(k => k.ToLowerInvariant().StartsWith(s_Prefix))
                .ToList();

            var s_Removed = 0;
            foreach (var s_Name in s_Names)
            {
                try
                {
                    s_BundleContext.RemoveResource(s_Name);
                    s_Removed++;
                }
                catch (Exception s_Ex)
                {
                    p_Writer.WriteLine($"Warning: could not remove '{s_Name}': {s_Ex.Message}");
                }
            }

            p_Writer.WriteLine($"strip_resources_by_prefix '{Prefix}': removed {s_Removed} resource(s).");
            return true;
        }
    }
}
