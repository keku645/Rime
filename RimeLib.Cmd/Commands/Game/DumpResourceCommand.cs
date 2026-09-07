using System;
using System.IO;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;

namespace RimeLib.Cmd.Commands.Game
{
    [CommandDescription("Dumps the specified resource to a file.")]
    public class DumpResourceCommand : Command
    {
        [CommandArgument(Description = "The name of the resource to dump.")]
        public string? Name { get; set; }

        [CommandArgument(Description = "The destination file name where the resource will be dumped to.")]
        public FileInfo? Destination { get; set; }

        [CommandArgument(Description = "Optional: true dumps the LAST-mounted variant of the name instead of " +
                                       "the first — how a standalone superbundle's copy of a colliding name " +
                                       "(a level's shader database) is read instead of the game's own.",
            Optional = true)]
        public bool Last { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            try
            {
                ((GameContext) p_Context).DumpResource(Name!, Destination!, Last);
                p_Writer.WriteLine("Resource successfully dumped.");
                return true;
            }
            catch (Exception s_Exception)
            {
                p_Writer.WriteLine("Failed to dump resource with error:");
                p_Writer.WriteLine(s_Exception.Message);
                return false;
            }
        }
    }
}
