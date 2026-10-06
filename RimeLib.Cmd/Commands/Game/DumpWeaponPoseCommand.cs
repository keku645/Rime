using System;
using System.IO;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;

namespace RimeLib.Cmd.Commands.Game;

/// <summary>
/// Writes the pose that assembles a first-person weapon mesh (its idle pose from its own animation package,
/// composed on the shared weapon skeleton) as a pose file for dump_mesh_sections to apply.
/// </summary>
[CommandDescription("Writes a weapon mesh's assembled pose (per bone transform) to a JSON pose file.")]
public class DumpWeaponPoseCommand : Command
{
    [CommandArgument(Description = "The weapon mesh, e.g. weapons/m240/m240_1p_mesh (its folder names the weapon)")]
    public string Mesh { get; set; } = string.Empty;

    [CommandArgument(Description = "The skeleton partition the mesh is skinned to, e.g. animations/skeletons/weapon/weaponske01")]
    public string Skeleton { get; set; } = string.Empty;

    [CommandArgument(Description = "The destination JSON pose file")]
    public FileInfo? Destination { get; set; }

    public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
    {
        try
        {
            var s_Bones = WeaponPose.Dump(((GameContext) p_Context).GetMounter(), Mesh, Skeleton, Destination!, p_Writer);
            p_Writer.WriteLine($"Wrote a pose for {s_Bones} bone(s) of {Mesh} to {Destination?.FullName}");
            return s_Bones > 0;
        }
        catch (Exception s_Exception)
        {
            p_Writer.WriteLine($"Could not dump the weapon pose. Error: {s_Exception.Message}");
            return false;
        }
    }
}
