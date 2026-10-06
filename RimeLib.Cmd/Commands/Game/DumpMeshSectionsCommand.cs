using System;
using System.IO;
using System.Linq;
using System.Text;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;

namespace RimeLib.Cmd.Commands.Game;

/// <summary>
/// Writes, per LOD-0 subset of a mesh IN THE SAME ORDER the OBJ converter walks them, the material name
/// and the SURFACE SHADER that material resolves to. This is the mapping the OBJ export loses: its
/// `usemtl Material_N` groups are numbered by that same walk, so Material_N pairs with sections[N] here —
/// which is what lets a preview draw each section with the shader that actually wears it.
/// </summary>
[CommandDescription("Dumps a mesh's LOD0 subset -> material -> surface shader mapping to a JSON file.")]
public class DumpMeshSectionsCommand : Command
{
    [CommandArgument(Description = "The path of the mesh")]
    public string Path { get; set; } = string.Empty;

    [CommandArgument(Description = "The destination JSON file")]
    public FileInfo? Destination { get; set; }

    [CommandArgument(Description = "Optional: the level's shaderdb resource name, to mark double-sided " +
                                   "sections (solution Flags bit 1)", Optional = true)]
    public string? ShaderDb { get; set; }

    [CommandArgument(Description = "Optional: a pose file from dump_weapon_pose (*.json) that assembles a skinned " +
                                   "weapon, or a skeleton partition whose raw ModelPose is applied " +
                                   "(default: none — the vertices stay as the game stores them). '-' = none", Optional = true)]
    public string? Skeleton { get; set; }

    [CommandArgument(Description = "Optional: true assembles a COMPOSITE mesh (a vehicle body) with the part " +
                                   "transforms the mesh layout carries — each part is modelled around its own " +
                                   "origin and a vertex names its part in BoneIndices", Optional = true)]
    public bool Parts { get; set; }

    [CommandArgument(Description = "Optional: which UV set is written — 0 (default, the first), 1 (the second) " +
                                   "or 'auto', which takes the second in every section that HAS one and the " +
                                   "first where it is empty. A vehicle body samples its DIFFUSE with the second " +
                                   "set (measured: read as the first, a wheel lands on hull plates) while a " +
                                   "weapon has only one. Both sets are printed (MESHUV) whichever is written", Optional = true)]
    public string? UvSet { get; set; }

    [CommandArgument(Description = "Optional: comma-separated uv-set ORDERS per shader, by FULL resource name " +
                                   "(matched by identity, e.g. vehicles/shaders/vehiclepreset_mud). An entry is " +
                                   "`<shader>` (its sections have their two sets written SWAPPED: the game's " +
                                   "vertex shader does `mov o8.xyzw, v3.zwxy`), `<shader>=a:b:c` (the three " +
                                   "slots written: the pair's .xy, its .zw and a second uv interpolator's set — " +
                                   "the jets' preset hands over 1:2:0) or `<shader>@0x<decl>=a:b:c` for one " +
                                   "declaration of it. A preview draws every section with a single vertex " +
                                   "shader, so this cannot be done downstream", Optional = true)]
    public string? SwapUv { get; set; }

    public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
    {
        try
        {
            var s_Skeleton = Skeleton == "-" || string.IsNullOrWhiteSpace(Skeleton) ? null : Skeleton;
            var s_UvSet = string.Equals(UvSet, "auto", StringComparison.OrdinalIgnoreCase)
                ? -1
                : int.TryParse(UvSet, out var s_Parsed) ? s_Parsed : 0;

            var s_Swap = string.IsNullOrWhiteSpace(SwapUv)
                ? null
                : SwapUv!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var s_Count = ((GameContext) p_Context).DumpMeshSections(Path, Destination!, p_Writer, ShaderDb, s_Skeleton,
                Parts, s_UvSet, s_Swap);
            p_Writer.WriteLine($"Wrote {s_Count} section(s) for {Path} to {Destination?.FullName}");
            return s_Count > 0;
        }
        catch (Exception s_Exception)
        {
            p_Writer.WriteLine($"Could not dump mesh sections. Error: {s_Exception.Message}");
            return false;
        }
    }
}
