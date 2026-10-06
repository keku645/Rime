using System;
using System.IO;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;

namespace RimeLib.Cmd.Commands.Game;

/// <summary>
/// Bakes, for every weapon, the poses its parts take when a foregrip, a bipod or nothing is mounted (the bipod
/// bones, as a held weapon shows them) into a Lua module for the customization screen's weapon view.
/// </summary>
[CommandDescription("Bakes every weapon's held-part poses (foregrip / bipod / nothing mounted: the Wep_Bipod bones) into a Lua module.")]
public class DumpWeaponPartPosesCommand : Command
{
    [CommandArgument(Description = "The destination Lua file (a module returning the table)")]
    public FileInfo? Destination { get; set; }

    [CommandArgument(Optional = true, Description = "Optional: only weapons whose partition name contains this text")]
    public string? Only { get; set; }

    public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
    {
        try
        {
            var s_Baked = WeaponPartPoses.Bake(((GameContext) p_Context).GetMounter(), Destination!, Only, p_Writer);
            return s_Baked > 0;
        }
        catch (Exception s_Exception)
        {
            p_Writer.WriteLine($"Could not bake the weapon part poses. Error: {s_Exception.Message}");
            return false;
        }
    }
}
