using System;
using System.IO;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;

namespace RimeLib.Cmd.Commands.Game;

[CommandDescription("Round-trips a MeshSet resource (read -> parse MeshSetLayout -> re-serialize the header) and reports whether the re-serialized header is byte-identical to the original. Validates the mesh writer before authoring a mesh from an import.")]
public class MeshRoundtripCommand : Command
{
    [CommandArgument(Description = "The name of the MeshSet resource")]
    public string Path { get; set; } = string.Empty;

    public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
    {
        try
        {
            ((GameContext)p_Context).MeshRoundtrip(Path, p_Writer);
            return true;
        }
        catch (Exception s_Exception)
        {
            p_Writer.WriteLine($"Could not round-trip mesh. Error: {s_Exception.Message}");
            return false;
        }
    }
}
