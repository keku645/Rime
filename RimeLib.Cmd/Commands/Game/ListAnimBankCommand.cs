using System;
using System.IO;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;

namespace RimeLib.Cmd.Commands.Game;

/// <summary>
/// Lists what an animation bank holds: its animations (codec, channel counts, keys, channel map), its rigs
/// (DOF counts) and the rest by type; or, given a rig name, that rig's DOF table.
/// </summary>
[CommandDescription("Lists the animations, rigs and objects of an animation bank (a weapon's package partition or a bundled bank resource).")]
public class ListAnimBankCommand : Command
{
    [CommandArgument(Description = "The bank: a partition with a streamed AntPackageAsset (animations/antanimations/xp1_l85a2) or a bank resource (animations/antanimations/b_menu)")]
    public string Bank { get; set; } = string.Empty;

    [CommandArgument(Optional = true, Description = "Optional: only objects whose name or type contains this text")]
    public string? Filter { get; set; }

    [CommandArgument(Optional = true, Description = "Optional: a rig name; lists that rig's DOF table (the filter then narrows the DOF names) instead of the objects")]
    public string? Rig { get; set; }

    public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
    {
        try
        {
            var s_Bank = AnimBank.Open(((GameContext) p_Context).GetMounter(), Bank, p_Writer);

            if (!string.IsNullOrEmpty(Rig))
            {
                var s_Rig = AnimBank.FindRig(s_Bank, Rig);
                if (s_Rig == null)
                {
                    p_Writer.WriteLine($"No rig named '{Rig}' in '{Bank}' or the static bank.");
                    return false;
                }

                AnimBank.ListRig(s_Rig, Filter, p_Writer);
                return true;
            }

            AnimBank.List(s_Bank, Filter, p_Writer);
            return true;
        }
        catch (Exception s_Exception)
        {
            p_Writer.WriteLine($"Could not list the animation bank. Error: {s_Exception.Message}");
            return false;
        }
    }
}
