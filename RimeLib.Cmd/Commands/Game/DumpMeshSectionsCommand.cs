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

    public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
    {
        try
        {
            var s_Count = ((GameContext) p_Context).DumpMeshSections(Path, Destination!, p_Writer, ShaderDb);
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
