using System;
using System.IO;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;

namespace RimeLib.Cmd.Commands.Game;

/// <summary>
/// Reads one frame of an animation by DOF name: the channels of a frame, raw or DCT animation mapped through
/// its channel map onto the named rig's DOF table. A one-frame animation is a pose: each bone's local
/// transform relative to its parent (quaternion x,y,z,w and translation x,y,z). A bank repeats a name across
/// rigs (the same pose on the 1P rig and on the weapon-parts rig) and across weapons, so the shape (quaternion
/// count) and the occurrence pick one; occurrence -1 dumps every match, numbered.
/// </summary>
[CommandDescription("Dumps one frame of an animation as DOF name -> value under a rig (JSON), printing the DOFs that match a filter.")]
public class DumpAnimFrameCommand : Command
{
    [CommandArgument(Description = "The bank (see list_anim_bank)")]
    public string Bank { get; set; } = string.Empty;

    [CommandArgument(Description = "The animation's name (exact, else every animation whose name contains it)")]
    public string Anim { get; set; } = string.Empty;

    [CommandArgument(Description = "The rig whose DOF table names the channels, e.g. 1P_Upperbody_DOF or WeaponParts")]
    public string Rig { get; set; } = string.Empty;

    [CommandArgument(Description = "The destination JSON file (with occurrence -1, a numbered file per match)")]
    public FileInfo? Destination { get; set; }

    [CommandArgument(Optional = true, Description = "Optional: the frame to read (default 0)")]
    public int Frame { get; set; }

    [CommandArgument(Optional = true, Description = "Optional: print only DOFs whose name contains this text (default: all)")]
    public string? Filter { get; set; }

    [CommandArgument(Optional = true, Description = "Optional: only animations with this many quaternion channels (0 = any)")]
    public int Quats { get; set; }

    [CommandArgument(Optional = true, Description = "Optional: which match to dump, 0-based (default 0; -1 = every match)")]
    public int Occurrence { get; set; }

    public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
    {
        try
        {
            var s_Bank = AnimBank.Open(((GameContext) p_Context).GetMounter(), Bank, p_Writer);
            var s_Anims = AnimBank.FindAnims(s_Bank, Anim, Quats);
            if (s_Anims.Count == 0)
            {
                p_Writer.WriteLine($"No animation named '{Anim}'" + (Quats > 0 ? $" with {Quats} quaternion channels" : "") + $" in '{Bank}'.");
                return false;
            }

            var s_Rig = AnimBank.FindRig(s_Bank, Rig);
            if (s_Rig == null)
            {
                p_Writer.WriteLine($"No rig named '{Rig}' in '{Bank}' or the static bank.");
                return false;
            }

            p_Writer.WriteLine($"ANIMBANK: {s_Anims.Count} animation(s) match '{Anim}'" + (Quats > 0 ? $" with q={Quats}" : ""));

            var s_First = Occurrence < 0 ? 0 : Occurrence;
            var s_Last = Occurrence < 0 ? s_Anims.Count - 1 : Occurrence;
            if (s_First >= s_Anims.Count)
            {
                p_Writer.WriteLine($"Occurrence {Occurrence} is out of range: {s_Anims.Count} match(es).");
                return false;
            }

            var s_Named = 0;
            for (var i = s_First; i <= s_Last; i++)
            {
                FileInfo? s_Destination = Destination;
                if (Occurrence < 0 && Destination != null)
                    s_Destination = new FileInfo(Path.Combine(Destination.DirectoryName ?? ".",
                        Path.GetFileNameWithoutExtension(Destination.Name) + $"_{i}" + Destination.Extension));

                p_Writer.WriteLine($"ANIMBANK: ---- match {i} of {s_Anims.Count}: '{s_Anims[i].ObjectName}' ({s_Anims[i].GetType().Name}) ----");
                var s_Frame = AnimBank.DumpFrame(s_Bank, Bank, s_Anims[i], s_Rig, Frame, s_Destination, Filter, p_Writer);
                s_Named += s_Frame.Dofs.Count;
                if (s_Destination != null)
                    p_Writer.WriteLine($"Wrote frame {Frame} of '{s_Anims[i].ObjectName}' ({s_Frame.Dofs.Count} named DOFs) to {s_Destination.FullName}");
            }

            return s_Named > 0;
        }
        catch (Exception s_Exception)
        {
            p_Writer.WriteLine($"Could not dump the animation frame. Error: {s_Exception.Message}");
            return false;
        }
    }
}
