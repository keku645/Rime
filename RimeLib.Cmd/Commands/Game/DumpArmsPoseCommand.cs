using System;
using System.IO;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;

namespace RimeLib.Cmd.Commands.Game;

/// <summary>
/// Writes the pose file that puts the soldier's first-person arms where they hold a weapon, in that weapon mesh's own space: the 1P
/// skeleton posed by the weapon's idle pose, every bone's rest undone and taken into the weapon bone's space. dump_mesh_sections applies
/// it to an arms mesh (skinned to the same skeleton) so the arms can be drawn with the weapon.
/// </summary>
[CommandDescription("Writes the pose file that places the first-person arms around a weapon mesh (in the weapon's space).")]
public class DumpArmsPoseCommand : Command
{
    [CommandArgument(Description = "The weapon mesh, e.g. weapons/m416/m416_1p_mesh (its folder names the weapon)")]
    public string Mesh { get; set; } = string.Empty;

    [CommandArgument(Description = "The soldier's first-person skeleton the arms are skinned to, e.g. animations/skeletons/venice1pske01")]
    public string Skeleton { get; set; } = string.Empty;

    [CommandArgument(Description = "The destination JSON pose file")]
    public FileInfo? Destination { get; set; }

    public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
    {
        try
        {
            var s_Bones = WeaponPose.ArmsPose(((GameContext) p_Context).GetMounter(), Mesh, Skeleton, Destination!, p_Writer);
            p_Writer.WriteLine($"Wrote an arms pose for {s_Bones} bone(s) around {Mesh} to {Destination?.FullName}");
            return s_Bones > 0;
        }
        catch (Exception s_Exception)
        {
            p_Writer.WriteLine($"Could not dump the arms pose of {Mesh}. Error: {s_Exception.Message}");
            return false;
        }
    }
}
