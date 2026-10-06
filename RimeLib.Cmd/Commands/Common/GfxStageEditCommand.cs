using System;
using System.Globalization;
using System.IO;
using System.Linq;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Scaleform;

namespace RimeLib.Cmd.Commands.Common
{
    [CommandDescription("Edits the stage of a Scaleform .gfx movie by byte surgery and writes a new file. Ops (separate several with ';'): " +
                        "move:<clip>:<xPx>:<yPx> | scale:<clip>:<sx>:<sy> | rotate:<clip>:<degrees> | char:<clip>:<characterId> | remove:<clip> | " +
                        "import:<url>:<characterId>:<symbol> | add:<spriteId>:<clip>:<characterId>:<depth>:<xPx>:<yPx>:<sx>:<sy> | " +
                        "vars:<clip>:<name>=<value>|<name>=<value> (the construct-time variables FrostEd bakes into a placement; true/false, numbers, strings) | " +
                        "clone:<clip>:<newName>:<depth>:<xPx>:<yPx> (the same widget placed again in the clip's sprite). " +
                        "A clip is its name, or <sprite id or exported symbol>/<name> when several sprites place one of that name. " +
                        "Untouched tags are copied byte for byte; the result is re-read from disk and listed.")]
    public class GfxStageEditCommand : Command
    {
        [CommandArgument(Description = "The .gfx file to read.")]
        public FileInfo? Source { get; set; }

        [CommandArgument(Description = "The .gfx file to write.")]
        public FileInfo? Destination { get; set; }

        [CommandArgument(Description = "The edit operations, ';'-separated (quote the whole list).")]
        public string? Ops { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            try
            {
                var s_Movie = GfxMovie.Load(Source!.FullName);
                p_Writer.WriteLine("BEFORE:");
                p_Writer.Write(s_Movie.Describe());

                var s_Applied = 0;
                foreach (var s_Op in (Ops ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(o => o.Trim()))
                {
                    var s_F = s_Op.Split(':');
                    var s_Inv = CultureInfo.InvariantCulture;
                    switch (s_F[0])
                    {
                        case "move":
                            s_Movie.Move(s_F[1], double.Parse(s_F[2], s_Inv), double.Parse(s_F[3], s_Inv));
                            break;
                        case "scale":
                            s_Movie.Scale(s_F[1], double.Parse(s_F[2], s_Inv), double.Parse(s_F[3], s_Inv));
                            break;
                        case "rotate":
                            s_Movie.Rotate(s_F[1], double.Parse(s_F[2], s_Inv));
                            break;
                        case "char":
                            s_Movie.SetCharacter(s_F[1], ushort.Parse(s_F[2], s_Inv));
                            break;
                        case "remove":
                            s_Movie.Remove(s_F[1]);
                            break;
                        case "import":
                            s_Movie.AddImport(s_F[1], ushort.Parse(s_F[2], s_Inv), s_F[3]);
                            break;
                        case "add":
                            s_Movie.AddPlacement(ushort.Parse(s_F[1], s_Inv), s_F[2], ushort.Parse(s_F[3], s_Inv), ushort.Parse(s_F[4], s_Inv),
                                double.Parse(s_F[5], s_Inv), double.Parse(s_F[6], s_Inv), double.Parse(s_F[7], s_Inv), double.Parse(s_F[8], s_Inv));
                            break;
                        case "vars":
                            // everything after the second ':' is the variable list (a value may hold ':')
                            s_Movie.SetClipVars(s_F[1], GfxMovie.ParseClipVars(s_Op[(s_Op.IndexOf(':', s_Op.IndexOf(':') + 1) + 1)..]));
                            break;
                        case "clone":
                            s_Movie.ClonePlacement(s_F[1], s_F[2], ushort.Parse(s_F[3], s_Inv), double.Parse(s_F[4], s_Inv), double.Parse(s_F[5], s_Inv));
                            break;
                        default:
                            throw new Exception($"unknown op '{s_F[0]}'");
                    }
                    ++s_Applied;
                }

                if (Destination!.Directory != null && !Destination.Directory.Exists)
                    Destination.Directory.Create();
                s_Movie.Save(Destination.FullName);

                var s_Check = GfxMovie.Load(Destination.FullName);
                p_Writer.WriteLine($"AFTER ({s_Applied} op(s), re-read from {Destination.FullName}, payload {s_Check.ToPayload().Length} bytes):");
                p_Writer.Write(s_Check.Describe());
                return true;
            }
            catch (Exception s_Exception)
            {
                p_Writer.WriteLine("Failed to edit movie: " + s_Exception.Message);
                return false;
            }
        }
    }
}
