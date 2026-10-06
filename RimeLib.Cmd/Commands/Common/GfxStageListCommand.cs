using System;
using System.IO;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Scaleform;

namespace RimeLib.Cmd.Commands.Common
{
    [CommandDescription("Lists the stage of a Scaleform .gfx movie file: its imports (widget symbols it pulls from other movies), its exports, and every placed clip (sprite, depth, character id, instance name, scale and position in pixels). Works on a file on disk (dump_resource first), no game needed.")]
    public class GfxStageListCommand : Command
    {
        [CommandArgument(Description = "The .gfx file to read.")]
        public FileInfo? Source { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            try
            {
                var s_Movie = GfxMovie.Load(Source!.FullName);
                p_Writer.Write(s_Movie.Describe());

                // Bounds of every placement, resolving imports against sibling files named like the import url
                // (Grid.swf -> grid.gfx in the same folder), which is how dump_resource lays them out.
                var s_Dir = Source.DirectoryName ?? ".";
                var s_Cache = new System.Collections.Generic.Dictionary<string, GfxMovie?>();
                (GfxMovie?, ushort) Resolve(string p_Url, string p_Symbol)
                {
                    var s_Key = System.IO.Path.GetFileNameWithoutExtension(p_Url).ToLowerInvariant();
                    if (!s_Cache.TryGetValue(s_Key, out var s_M))
                    {
                        var s_File = System.IO.Path.Combine(s_Dir, s_Key + ".gfx");
                        s_M = File.Exists(s_File) ? GfxMovie.Load(s_File) : null;
                        s_Cache[s_Key] = s_M;
                    }
                    var s_Id = s_M?.ExportedCharacter(p_Symbol);
                    return (s_Id == null ? null : s_M, s_Id ?? 0);
                }
                // stage = through the sprites above the placement (a screen's root sprite is often placed at the centre)
                p_Writer.WriteLine("bounds (px, in the stage):");
                foreach (var s_Stage in s_Movie.StagePlacements())
                {
                    var s_Place = s_Stage.Place;
                    if (!s_Place.HasCharacter || s_Place.Name == null) continue;
                    var s_Bounds = s_Movie.CharacterBounds(s_Place.CharacterId, Resolve).Transform(s_Stage.World);
                    var (s_X, s_Y) = s_Stage.Parent.Apply(s_Place.Matrix?.TranslateX ?? 0, s_Place.Matrix?.TranslateY ?? 0);
                    p_Writer.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "  {0,-24} origin=({1:0.#},{2:0.#}) {3}", s_Stage.Path, s_X / 20.0, s_Y / 20.0,
                        s_Bounds.Empty ? "(unknown: symbol not resolvable from sibling files)" : s_Bounds.ToString()));
                }
                return true;
            }
            catch (Exception s_Exception)
            {
                p_Writer.WriteLine("Failed to read movie: " + s_Exception.Message);
                return false;
            }
        }
    }
}
