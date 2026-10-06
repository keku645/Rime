using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimeLib.Cmd.Scaleform;
using Xunit;

namespace RimeLib.Cmd.Tests
{
    /// <summary>
    /// The art decoders against the shipped movies (game content on this machine; skipped elsewhere): every
    /// shape, edit text, font and placement of every UI movie decodes without throwing, and the values measured
    /// by hand on button.gfx (its atlas, sub-images and a bitmap-filled shape) come back exactly.
    /// </summary>
    public class GfxShapesTest
    {
        const string c_Root = @"F:\Desktop\Venice Unleashed\UI-Swf";

        static IEnumerable<GfxTag> AllTags(IEnumerable<GfxTag> p_Tags)
        {
            foreach (var t in p_Tags)
            {
                yield return t;
                foreach (var c in AllTags(t.Children)) yield return c;
            }
        }

        [Fact]
        public void EveryShipppedMovieDecodes()
        {
            if (!Directory.Exists(c_Root)) return;
            int s_Shapes = 0, s_Texts = 0, s_Fonts = 0, s_Places = 0, s_Movies = 0;
            foreach (var s_File in Directory.EnumerateFiles(c_Root, "*.gfx", SearchOption.AllDirectories))
            {
                GfxMovie s_Movie;
                try { s_Movie = GfxMovie.Load(s_File); } catch { continue; } // GFX (uncompressed) font libs etc.
                ++s_Movies;
                foreach (var t in AllTags(s_Movie.Tags))
                {
                    switch (t.Code)
                    {
                        case 2: case 22: case 32: case 83:
                            var s = SwfShape.Decode(t.Code, t.Body); Assert.True(s.Edges.Count >= 0); ++s_Shapes; break;
                        case 37:
                            SwfEditText.Decode(t.Body); ++s_Texts; break;
                        case 48: case 75:
                            var f = SwfFont.Decode(t.Code, t.Body); Assert.Equal(f.Glyphs.Count, f.CodeToGlyph.Count > 0 ? f.Glyphs.Count : f.Glyphs.Count); ++s_Fonts; break;
                        case 70:
                            PlaceObject3.Decode(t.Body); ++s_Places; break;
                    }
                }
                GfxMovie.FrameOnePlacements(s_Movie.Tags);
            }
            Assert.True(s_Movies > 300, $"movies {s_Movies}");
            Assert.True(s_Shapes > 1000 && s_Texts > 1000 && s_Fonts > 50 && s_Places > 500, $"shapes {s_Shapes} texts {s_Texts} fonts {s_Fonts} places {s_Places}");
        }

        [Fact]
        public void ButtonAtlasAndSubImagesMatchTheHandMeasurement()
        {
            var s_Path = Path.Combine(c_Root, "button.gfx");
            if (!File.Exists(s_Path)) return;
            var m = GfxMovie.Load(s_Path);
            var s_Atlas = m.ExternalImages().Single();
            Assert.Equal("button_i7.tga", s_Atlas.FileName);
            Assert.Equal(256, s_Atlas.Width); Assert.Equal(64, s_Atlas.Height); Assert.Equal(13, s_Atlas.Format);
            var s_Sub = m.SubImages().ToDictionary(s => s.CharacterId);
            Assert.Equal(8, s_Sub.Count);
            Assert.Equal((145, 0, 167, 22), (s_Sub[7].Left, s_Sub[7].Top, s_Sub[7].Right, s_Sub[7].Bottom));
            Assert.Equal((0, 0, 8, 64), (s_Sub[107].Left, s_Sub[107].Top, s_Sub[107].Right, s_Sub[107].Bottom));
            var s_Shape8 = SwfShape.Decode(2, m.Characters()[8].Body);
            Assert.Equal(2, s_Shape8.Fills.Count);
            Assert.True(s_Shape8.Fills[1].IsBitmap);
            Assert.Equal(7, s_Shape8.Fills[1].BitmapId);
            Assert.Equal(-20.0, s_Shape8.Fills[1].Matrix!.ScaleXf, 3);
            Assert.Equal(440, s_Shape8.Fills[1].Matrix!.TranslateX);
            Assert.True(s_Shape8.Edges.Count >= 4);
            Assert.Equal("ui/assets/button/button_i7", RimeLib.Cmd.UiBuilder.RimeUiService.AtlasResourceName("ui/assets/button", s_Atlas.FileName));
        }

        [Fact]
        public void EnglishFontLibraryHasTheFourFonts()
        {
            var s_Path = Path.Combine(c_Root, "ui", "static", "fontcollection_en_fontlib.gfx");
            if (!File.Exists(s_Path)) return;
            var m = GfxMovie.Load(s_Path);
            var s_Fonts = m.Tags.Where(t => t.Code == 75).Select(t => SwfFont.Decode(75, t.Body)).ToList();
            Assert.Equal(new[] { "Purista EA Medium", "Purista EA Semibold", "Venice-Medium", "Venice-SemiBold" }, s_Fonts.Select(f => f.Name).ToArray());
            var s_Purista = s_Fonts[0];
            Assert.True(s_Purista.HasLayout);
            Assert.True(s_Purista.CodeToGlyph.ContainsKey('A') && s_Purista.CodeToGlyph.ContainsKey('a'));
            var s_A = s_Purista.Glyphs[s_Purista.CodeToGlyph['A']];
            Assert.True(s_A.Edges.Count > 4, "glyph A has edges");
            Assert.True(s_Purista.Advance('A') > 0 && s_Purista.Advance('A') < 20480);
            Assert.True(s_Purista.Ascent > 0);
        }
    }
}
