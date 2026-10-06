using System.IO;
using System.Linq;
using RimeLib.Cmd.Scaleform;
using Xunit;

namespace RimeLib.Cmd.Tests
{
    /// <summary>
    /// The stage editor's contract: loading and saving a movie without edits reproduces its payload byte for
    /// byte, and an edit changes exactly the placement it names. Runs on any shipped .gfx dumped next to the
    /// game tooling; skips when none is available (the test data is game content, not part of the repo).
    /// </summary>
    public class GfxStageTest
    {
        static readonly string[] s_Candidates =
        {
            @"F:\Desktop\Venice Unleashed\UI-Swf\customizeaccessoriesscreen.gfx",
            @"F:\Desktop\Venice Unleashed\UI-Swf\spawnscreenpc.gfx",
            @"F:\Desktop\Venice Unleashed\UI-Swf\grid.gfx",
        };

        static byte[] Payload(byte[] p_File) => GfxMovie.Load(p_File).ToPayload();

        [Fact]
        public void NoEditIsByteIdentical()
        {
            foreach (var s_Path in s_Candidates.Where(File.Exists))
            {
                var s_Original = File.ReadAllBytes(s_Path);
                var s_Movie = GfxMovie.Load(s_Original);
                Assert.Equal(Payload(s_Original), s_Movie.ToPayload());
                // and the written file loads back to the same payload
                Assert.Equal(s_Movie.ToPayload(), Payload(s_Movie.ToFile()));
            }
        }

        /// <summary>
        /// The construct-time variables FrostEd bakes into a placement's clip actions (TextField_01 of the accessories screen:
        /// m_useBorder=false, m_align="left", m_rowType="bold1") decode, re-encode byte for byte, change one at a time, and land on
        /// a placement the editor adds — the widget's constructor reads them before onClipLoad (TextField attaches "mc_" + m_rowType).
        /// </summary>
        [Fact]
        public void ClipVariablesDecodeEncodeAndSet()
        {
            var s_Path = s_Candidates[0];
            if (!File.Exists(s_Path))
                return;
            var s_Movie = GfxMovie.Load(s_Path);
            var s_Vars = s_Movie.ClipVarsOf("TextField_01");
            Assert.NotNull(s_Vars);
            Assert.Equal(new[] { "m_useBorder", "m_align", "m_rowType" }, s_Vars!.Select(v => v.Name).ToArray());
            Assert.Equal(false, s_Vars[0].Value);
            Assert.Equal("left", s_Vars[1].Value);
            Assert.Equal("bold1", s_Vars[2].Value);
            // the encoder writes what FrostEd wrote: rebuilding the record from its own variables changes not a byte
            var s_Place = s_Movie.Placements().Single(p => p.Place.Name == "TextField_01").Place;
            var s_TailBefore = s_Place.Tail.ToArray();
            var s_Actions = s_Place.DecodeClipActions()!;
            s_Actions.SetConstructVars(s_Actions.ConstructVars());
            s_Place.SetClipActions(s_Actions);
            Assert.Equal(s_TailBefore, s_Place.Tail);
            Assert.Equal(GfxMovie.Load(s_Path).ToPayload(), s_Movie.ToPayload());
            // one variable changed, the others kept; the KitSelectors say which view they are
            s_Movie.SetClipVars("TextField_01", GfxMovie.ParseClipVars("m_rowType=medium1"));
            var s_After = GfxMovie.Load(s_Movie.ToFile());
            Assert.Equal("m_useBorder=false|m_align=left|m_rowType=medium1", GfxMovie.FormatClipVars(s_After.ClipVarsOf("TextField_01")!));
            Assert.Equal("KitView_1", s_After.ClipVarsOf("KitSelector_01")!.Single(v => v.Name == "m_viewType").Value);
            // a placement the editor adds: born without, given the usual set, typed
            s_After.AddPlacement(10, "TextField_09", 7, 40, 100, 100, 1, 1);
            Assert.Null(s_After.ClipVarsOf("TextField_09"));
            s_After.SetClipVars("TextField_09", GfxMovie.ParseClipVars("m_useBorder=false|m_align=left|m_rowType=bold1|i_index=0.0|n=2"));
            var s_New = GfxMovie.Load(s_After.ToFile()).ClipVarsOf("TextField_09")!;
            Assert.Equal("m_useBorder=false|m_align=left|m_rowType=bold1|i_index=0.0|n=2", GfxMovie.FormatClipVars(s_New));
            Assert.IsType<double>(s_New[3].Value);
            Assert.IsType<int>(s_New[4].Value);
        }

        [Fact]
        public void ScaleChangesOnlyTheNamedPlacement()
        {
            var s_Path = s_Candidates[0];
            if (!File.Exists(s_Path))
                return;
            var s_Movie = GfxMovie.Load(s_Path);
            var s_Before = s_Movie.Placements().ToDictionary(p => p.Place.Name!, p => (p.Place.Matrix!.ScaleXf, p.Place.Matrix.TranslateX));
            s_Movie.Scale("KitSelector_05", 7.622, 2.2581);
            var s_After = GfxMovie.Load(s_Movie.ToFile()).Placements().ToDictionary(p => p.Place.Name!, p => (p.Place.Matrix!.ScaleXf, p.Place.Matrix.TranslateX));
            Assert.Equal(7.622, s_After["KitSelector_05"].ScaleXf, 3);
            Assert.Equal(s_Before["KitSelector_05"].TranslateX, s_After["KitSelector_05"].TranslateX);
            foreach (var s_Name in s_Before.Keys.Where(n => n != "KitSelector_05"))
                Assert.Equal(s_Before[s_Name], s_After[s_Name]);
        }

        /// <summary>
        /// A rotation keeps the scale along the placement's own axes and a later scale keeps the angle: the matrix
        /// is [sx·cos −sy·sin; sx·sin sy·cos] (RotateSkew0 = sx·sin, positive = clockwise on screen), and 0° drops
        /// the rotate terms again so an un-rotated placement encodes as before.
        /// </summary>
        [Fact]
        public void RotateKeepsTheScaleAndScaleKeepsTheAngle()
        {
            var s_Path = s_Candidates[0];
            if (!File.Exists(s_Path))
                return;
            var s_Movie = GfxMovie.Load(s_Path);
            var s_Before = s_Movie.Placements().Single(p => p.Place.Name == "KitSelector_05").Place.Matrix!;
            var (s_Sx0, s_Sy0) = s_Before.Magnitudes;
            s_Movie.Rotate("KitSelector_05", 30);
            var m = GfxMovie.Load(s_Movie.ToFile()).Placements().Single(p => p.Place.Name == "KitSelector_05").Place.Matrix!;
            Assert.True(m.HasRotate);
            Assert.Equal(30.0, m.Angle * 180 / System.Math.PI, 2);
            Assert.Equal(s_Sx0, m.Magnitudes.Sx, 3);
            Assert.Equal(s_Sy0, m.Magnitudes.Sy, 3);
            Assert.Equal(s_Sx0 * System.Math.Cos(System.Math.PI / 6), m.ScaleXf, 3);
            Assert.True(m.RotateSkew0 > 0 && m.RotateSkew1 < 0, "RotateSkew0 = sx·sin (positive for a clockwise turn), RotateSkew1 = −sy·sin");
            Assert.Equal(s_Before.TranslateX, m.TranslateX);
            // scaling the rotated placement keeps its 30°
            s_Movie.Scale("KitSelector_05", 2, 3);
            m = GfxMovie.Load(s_Movie.ToFile()).Placements().Single(p => p.Place.Name == "KitSelector_05").Place.Matrix!;
            Assert.Equal(30.0, m.Angle * 180 / System.Math.PI, 2);
            Assert.Equal(2.0, m.Magnitudes.Sx, 3);
            Assert.Equal(3.0, m.Magnitudes.Sy, 3);
            // back to 0°: no rotate terms, the plain scale as any shipped placement has it
            s_Movie.Rotate("KitSelector_05", 0);
            m = GfxMovie.Load(s_Movie.ToFile()).Placements().Single(p => p.Place.Name == "KitSelector_05").Place.Matrix!;
            Assert.False(m.HasRotate);
            Assert.Equal(2.0, m.ScaleXf, 3);
            Assert.Equal(3.0, m.ScaleYf, 3);
        }

        [Fact]
        public void AddImportAndPlacementRoundTrips()
        {
            var s_Path = s_Candidates[0];
            if (!File.Exists(s_Path))
                return;
            var s_Movie = GfxMovie.Load(s_Path);
            s_Movie.AddImport("Grid.swf", 11, "Grid");
            s_Movie.AddPlacement(10, "CamoGrid_01", 11, 11, 740, 260, 4.1463, 4.8387);
            var s_Again = GfxMovie.Load(s_Movie.ToFile());
            Assert.Contains(s_Again.Imports(), i => i.Url == "Grid.swf" && i.Characters.Any(c => c.Id == 11 && c.Name == "Grid"));
            var s_Added = s_Again.Placements().Single(p => p.Place.Name == "CamoGrid_01");
            Assert.Equal(11, s_Added.Place.CharacterId);
            Assert.Equal(740 * 20, s_Added.Place.Matrix!.TranslateX);
            Assert.Equal(4.1463, s_Added.Place.Matrix.ScaleXf, 3);
            Assert.Throws<System.Exception>(() => s_Again.AddPlacement(10, "Dup", 11, 11, 0, 0, 1, 1)); // depth taken
        }

        /// <summary>
        /// The com-rose screen places all 13 of its widgets with PlaceObject3 (filters + clip actions, 271-byte
        /// tags). They must be on the stage like any PlaceObject2 placement, a re-encode must be byte-identical,
        /// and a move must change nothing but the matrix — the filters and clip actions travel verbatim.
        /// </summary>
        [Fact]
        public void PlaceObject3PlacementsAreOnTheStageAndEditable()
        {
            var s_Path = @"F:\Desktop\Venice Unleashed\UI-Swf\ui\assets\commrosescreen.gfx";
            if (!File.Exists(s_Path))
                return;
            var s_Movie = GfxMovie.Load(s_Path);
            var s_Stage = s_Movie.StagePlacements();
            Assert.Equal(13, s_Stage.Count(p => p.Tag.Code == GfxMovie.TagPlaceObject3));
            Assert.Contains(s_Stage, p => p.Place.Name == "Button_01" && p.Place.IsPlaceObject3 && p.Place.HasClipActions);
            Assert.Contains(s_Stage, p => p.Place.Name == "ComRose_01");
            // decode + encode + decode of a PlaceObject3 body keeps every field (the matrix may pack with fewer bits than Flash used:
            // same values, so an untouched tag keeps its raw bytes and only an edited one is re-encoded)
            foreach (var p in s_Stage.Where(p => p.Tag.Code == GfxMovie.TagPlaceObject3))
            {
                var q = PlaceObject2.Decode(p.Place.Encode(), true);
                Assert.Equal(p.Place.Name, q.Name); Assert.Equal(p.Place.Depth, q.Depth); Assert.Equal(p.Place.CharacterId, q.CharacterId);
                Assert.Equal(p.Place.Flags2, q.Flags2); Assert.Equal(p.Place.ClassNameRaw, q.ClassNameRaw);
                Assert.Equal(p.Place.Matrix!.TranslateX, q.Matrix!.TranslateX); Assert.Equal(p.Place.Matrix.ScaleX, q.Matrix.ScaleX);
                Assert.Equal(p.Place.ColorTransformRaw, q.ColorTransformRaw); Assert.Equal(p.Place.Tail, q.Tail);
                if (p.Place.HasClipActions) Assert.True(p.Place.Tail.Length > 100, "the clip actions travel in the tail");
            }
            Assert.Equal(GfxMovie.Load(s_Path).ToPayload(), s_Movie.ToPayload());
            // a move rewrites the matrix and nothing else
            var s_Before = s_Stage.Single(p => p.Place.Name == "Button_07");
            s_Movie.Move("Button_07", 123, -45);
            var s_After = GfxMovie.Load(s_Movie.ToFile()).StagePlacements().Single(p => p.Place.Name == "Button_07");
            Assert.Equal(123 * 20, s_After.Place.Matrix!.TranslateX);
            Assert.Equal(-45 * 20, s_After.Place.Matrix.TranslateY);
            Assert.Equal(s_Before.Place.Tail, s_After.Place.Tail);
            Assert.Equal(s_Before.Place.ColorTransformRaw, s_After.Place.ColorTransformRaw);
            Assert.Equal(s_Before.Place.Flags2, s_After.Place.Flags2);
            Assert.Equal(s_Before.Place.Matrix!.ScaleXf, s_After.Place.Matrix.ScaleXf, 4);
        }
    }
}
