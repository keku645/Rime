using System.IO;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;

namespace RimeLib.Cmd.Commands.Game
{
    [CommandDescription("Prints every mounted variant of an EBX partition: the superbundle and bundle that carry it and whether it is catalog-backed. Tells which bundle of a level brings a shared asset (a LOD group, a texture) and so whether it is loaded before a bundle inserted after the level's own. Args: <partition name>.")]
    public class DumpPartitionVariantsCommand : Command
    {
        [CommandArgument(Description = "The partition name.")]
        public string? Name { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            var s_Mounter = ((GameContext)p_Context).GetMounter();

            if (!s_Mounter.TryGetPartition(Name!, out var s_Partition))
            {
                p_Writer.WriteLine($"PARTVARS: {Name} NOTFOUND");
                return false;
            }

            var s_I = 0;
            p_Writer.WriteLine($"PARTVARS: {Name}");

            foreach (var s_V in s_Partition.Variants)
                p_Writer.WriteLine($"  PARTVAR[{s_I++}] sb={s_V.GetContainedSuperbundle()} bundle={s_V.GetContainedBundle() ?? "-"} cas={s_V.Cas}");

            return true;
        }
    }
}
