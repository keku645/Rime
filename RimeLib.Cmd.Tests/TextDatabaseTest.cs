using System.IO;
using RimeLib.Cmd.UiBuilder;
using Xunit;

namespace RimeLib.Cmd.Tests
{
    /// <summary>
    /// The EA::Localizer lang pack reader: the ID hash pins to values measured against the shipped English
    /// database, and, when the cached chunks of that database are on this machine, known IDs resolve to their
    /// texts (the chunks are game content, not part of the repo, so that half skips elsewhere).
    /// </summary>
    public class TextDatabaseTest
    {
        [Fact]
        public void HashIsDjb2WithSeedMinusOne()
        {
            Assert.Equal(0x400274c8u, TextDatabase.Hash("ID_M_YES"));
            Assert.Equal(0xda712318u, TextDatabase.Hash("ID_P_ANAME_NOCAMO"));
            Assert.Equal(0xac9b3874u, TextDatabase.Hash("ID_M_NO"));
        }

        [Fact]
        public void EnglishDatabaseResolvesKnownIds()
        {
            var s_Dir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "RimeUIEditor", "cache");
            string? s_Binary = null, s_Histogram = null;
            if (Directory.Exists(s_Dir))
                foreach (var s_Game in Directory.GetDirectories(s_Dir))
                {
                    var b = Path.Combine(s_Game, "chunks", "50035522-22d3-b8c3-9aac-ce617deae923.bin");
                    var h = Path.Combine(s_Game, "chunks", "8ba57686-5a81-a229-726b-2d0f7ecb9b37.bin");
                    if (File.Exists(b) && File.Exists(h)) { s_Binary = b; s_Histogram = h; }
                }
            if (s_Binary == null) return; // no cached game on this machine
            var s_Db = TextDatabase.Parse(File.ReadAllBytes(s_Binary), File.ReadAllBytes(s_Histogram!));
            Assert.True(s_Db.Count > 15000);
            Assert.Equal("YES", s_Db.Lookup("ID_M_YES"));
            Assert.Equal("NO", s_Db.Lookup("ID_M_NO"));
            Assert.Equal("No Camo", s_Db.Lookup("ID_P_ANAME_NOCAMO"));
            Assert.Equal("ROUND WILL START IN", s_Db.Lookup("ID_H_PREROUND_COUNTDOWN"));
            Assert.Equal("not an id", s_Db.Resolve("not an id"));
        }
    }
}
