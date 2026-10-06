using System;
using System.IO;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;

namespace RimeLib.Cmd.Commands.Game;

/// <summary>
/// Writes where the first-person camera sits in a weapon mesh's own space: the soldier's 1P skeleton posed by the weapon's idle pose,
/// its camera bone taken into the weapon bone's space and through the weapon skeleton's rest pose into the mesh. One JSON for every
/// weapon, keyed by mesh (a run adds or replaces its weapon).
/// </summary>
[CommandDescription("Merges a weapon mesh's first-person eye (position, look, up in the mesh's space) into a JSON file.")]
public class DumpWeaponCameraCommand : Command
{
    [CommandArgument(Description = "The weapon mesh, e.g. weapons/m416/m416_1p_mesh (its folder names the weapon)")]
    public string Mesh { get; set; } = string.Empty;

    [CommandArgument(Description = "The soldier's first-person skeleton, e.g. animations/skeletons/venice1pske01")]
    public string Skeleton { get; set; } = string.Empty;

    [CommandArgument(Description = "The skeleton the weapon mesh is skinned to, e.g. animations/skeletons/weapon/weaponske01")]
    public string WeaponSkeleton { get; set; } = string.Empty;

    [CommandArgument(Description = "The JSON file the weapon's camera is merged into")]
    public FileInfo? Destination { get; set; }

    public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
    {
        try
        {
            WeaponPose.Camera(((GameContext) p_Context).GetMounter(), Mesh, Skeleton, WeaponSkeleton, Destination!, p_Writer);
            p_Writer.WriteLine($"Wrote the first-person camera of {Mesh} into {Destination?.FullName}");
            return true;
        }
        catch (Exception s_Exception)
        {
            p_Writer.WriteLine($"Could not dump the weapon camera of {Mesh}. Error: {s_Exception.Message}");
            return false;
        }
    }
}
