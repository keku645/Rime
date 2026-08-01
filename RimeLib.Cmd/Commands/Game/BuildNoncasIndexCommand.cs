using System.IO;
using RimeLib.Cmd.Attributes;
using RimeLib.Cmd.Contexts;

namespace RimeLib.Cmd.Commands.Game
{
    [CommandDescription(
        "Indexes ALL non-cas game content where it already lives: emits a cas.cat whose entries point " +
        "straight into the .sb files on disk, plus casfilemap.txt mapping each synthetic cas file " +
        "number to its .sb. No payload is copied — content the game ships as non-cas becomes " +
        "content-addressable for ~32 bytes of catalogue per blob. The runtime must open each mapped " +
        ".sb and put its buffer in the cas-file handle slot at index fileNumber - 1 (1-BASED). " +
        "Args: <out_dir> [first_file_number=0 means auto] [cross_check_samples_per_file=32] [sb_prefix].")]
    public class BuildNoncasIndexCommand : Command
    {
        [CommandArgument(Description = "Output directory for cas.cat + casfilemap.txt.")]
        public DirectoryInfo? OutDir { get; set; }

        [CommandArgument(Optional = true, Description =
            "First synthetic cas file number. 0 (the default) DERIVES it from the mounted catalogues: " +
            "max(fileNumber already in use) + 1, so it cannot collide. Pass a value only to override " +
            "that; it must be 1..255, since the engine reads the field as a 1-based byte.")]
        public int FirstFileNumber { get; set; } = 0;

        [CommandArgument(Optional = true, Description =
            "Per .sb file, how many blobs to re-read through Rime's mounted accessor and compare " +
            "byte-for-byte against the computed window. Catches a systematic offset error, which " +
            "hashing alone cannot. 0 disables. Default 32.")]
        public int CrossCheckSamples { get; set; } = 32;

        [CommandArgument(Optional = true, Description =
            "Only index content whose superbundle name starts with this prefix (case-insensitive). " +
            "Omit to index everything mounted.")]
        public string? SbPrefix { get; set; }

        public override bool Execute(ref ExecutionContext p_Context, TextWriter p_Writer)
        {
            var s_Ctx = (GameContext)p_Context;
            var s_Mounter = s_Ctx.GetMounter() as RimeLib.Content.Frostbite2_0.Mounting.EngineMounter;
            if (s_Mounter == null)
            {
                p_Writer.WriteLine("build_noncas_index: no Frostbite2_0 mounter.");
                return false;
            }

            if (FirstFileNumber != 0 && (FirstFileNumber < 1 || FirstFileNumber > 255))
            {
                p_Writer.WriteLine(
                    $"build_noncas_index: first_file_number must be 1..255, or 0 to derive it " +
                    $"(got {FirstFileNumber}). The engine reads a catalogue entry's file number as a " +
                    "single 1-based byte.");
                return false;
            }

            p_Writer.WriteLine(s_Mounter.BuildNoncasIndex(
                OutDir!.FullName,
                (uint)FirstFileNumber,
                CrossCheckSamples,
                string.IsNullOrWhiteSpace(SbPrefix) ? null : SbPrefix,
                true,
                p_Writer));

            return true;
        }
    }
}
