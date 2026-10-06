using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;

namespace RimeShaderEditor.Graph;

/// <summary>
/// ⭐ THE EMBLEM SLOT (keku 2026-09-29: "se bakean todos los camos con el hueco nuestro del emblema… invisible, y sólo si el
/// usuario quiere… aparece"; the place automatic, up to 40 layers): a square on each side of the weapon's receiver where the
/// weapon's own shader draws its owner's BF4 emblem. It rides the sticker machinery end to end — a placement projected onto
/// the body as a decal, composed per body mesh into the coordinate map (x, y of the square) and the side map (which half of a
/// mirrored unwrap it sits on) — and the shader's EmblemLayers node draws the layers the engine feeds per weapon there. With
/// every layer at zero (a player without an emblem, a weapon nobody wrote) the slot draws nothing.
///
/// BF4 put no emblem on weapons (its weapon meshes bind one neutral Logo texture and carry no player-logo component; census of
/// all 2612 BF4 mesh-variation databases, 2026-09-29), so there is no place to copy: the slot is found on the body itself —
/// the flattest spot facing out of each side, in the upper middle of the weapon, the emblem kept upright.
/// </summary>
public static class EmblemSlot
{
    /// <summary>The picture name an emblem placement carries (it is not a file: its picture is the square's coordinate picture).</summary>
    public const string Image = "#emblem";

    /// <summary>The slot's side in metres when nothing says otherwise.</summary>
    public const double DefaultSizeMetres = 0.04;

    /// <summary>Layers a slot draws (BF4 premium's cap; emblemsbf codes fit whole) — one packed constant each.</summary>
    public const int Layers = 40;

    /// <summary>
    /// The registers the engine gives a shader's external values, all together (fb::DxShaderDispatcher sizes their buffer from a
    /// render setting: 64 in keku's game, measured 2026-09-29 — 92 overflowed it and killed the client). A preset's own constants
    /// plus the slot's layers must fit.
    /// </summary>
    public const int EngineExternalRegisters = 64;

    public static bool IsEmblem(StickerPlacement p_Placement) => p_Placement.Image == Image;

    /// <summary>The emblem data the studio ships next to its executable (Data/emblem): the shape atlas, its cell table, sample emblems.</summary>
    public static string DataFolder => System.IO.Path.Combine(AppContext.BaseDirectory, "Data", "emblem");

    /// <summary>The shape atlas as the bake converts it (TGA: coverage in alpha, white elsewhere).</summary>
    public static string AtlasTga => System.IO.Path.Combine(DataFolder, "emblem_atlas.tga");

    /// <summary>How the shape atlas is laid out: cells per side, a cell's size and its shape's interior in texels, the atlas side, the mip cap.</summary>
    public readonly record struct AtlasLayoutInfo(int Grid, int Cell, int Inner, int Side, int MaxLod);

    private static AtlasLayoutInfo? s_Layout;

    /// <summary>
    /// The shape atlas's layout as emblem_atlas.json gives it (keku 2026-09-30, the full library: 716 BF4 shapes, 32 × 32 cells of 64 px
    /// in 2048²) — the EmblemLayers node takes it (StickerGraph.EnsureEmblem), so the shader reads the atlas the package ships. A table
    /// without it is the first atlas: 8 × 8 cells of 128 px (112 inside) in 1024², mips up to 3.
    /// </summary>
    public static AtlasLayoutInfo AtlasLayout
    {
        get
        {
            if (s_Layout is { } s_Known)
                return s_Known;

            var s_Found = new AtlasLayoutInfo(8, 128, 112, 1024, 3);
            try
            {
                var s_Table = JsonNode.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(DataFolder, "emblem_atlas.json")));
                int Read(string p_Name, int p_Default) =>
                    s_Table?[p_Name] is JsonValue s_V && s_V.TryGetValue<int>(out var s_Number) && s_Number > 0 ? s_Number : p_Default;
                s_Found = new AtlasLayoutInfo(Read("grid", 8), Read("cell", 128), Read("inner", 112), Read("side", 1024),
                    s_Table?["maxLod"] is JsonValue s_M && s_M.TryGetValue<int>(out var s_Lod) ? s_Lod : 3);
            }
            catch (Exception)
            {
                // no table: the first atlas's layout
            }

            s_Layout = s_Found;
            return s_Found;
        }
    }

    private static Dictionary<string, int>? s_Cells;

    /// <summary>The atlas cell of a Battlelog shape name, or null when the atlas does not carry it.</summary>
    public static int? CellOf(string p_Asset)
    {
        if (s_Cells == null)
        {
            s_Cells = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var s_Table = JsonNode.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(DataFolder, "emblem_atlas.json")))?["cells"]?.AsObject();
                if (s_Table != null)
                    foreach (var (s_Name, s_Cell) in s_Table)
                        if (s_Cell is JsonValue s_V && s_V.TryGetValue<int>(out var s_Index))
                            s_Cells[s_Name] = s_Index;
            }
            catch (Exception)
            {
                // no table: every shape unknown, every layer left out (and said, by Encode)
            }
        }

        return s_Cells.TryGetValue(p_Asset, out var s_Found) ? s_Found : null;
    }

    private static BitmapSource? s_Atlas;
    private static bool s_AtlasTried;

    /// <summary>The shape atlas as the preview shows it (the PNG beside the TGA); null when it is not there.</summary>
    public static BitmapSource? AtlasPicture()
    {
        if (s_AtlasTried)
            return s_Atlas;

        s_AtlasTried = true;
        var s_Path = System.IO.Path.Combine(DataFolder, "emblem_atlas.png");
        if (!System.IO.File.Exists(s_Path))
            return null;

        var s_Image = new BitmapImage();
        s_Image.BeginInit();
        s_Image.CacheOption = BitmapCacheOption.OnLoad;
        s_Image.UriSource = new Uri(s_Path);
        s_Image.EndInit();
        s_Image.Freeze();
        s_Atlas = s_Image;
        return s_Atlas;
    }

    private static float[][]? s_Preview;

    /// <summary>Puts another emblem in the preview's slot (its constants, as <see cref="Encode"/> writes them); all zero = an empty slot.</summary>
    public static void SetPreview(float[][] p_Values) => s_Preview = p_Values;

    /// <summary>
    /// The emblem the studio's preview draws in the slot, as the slot's constants: the sample named by CAMO_EMBLEM_PREVIEW (a file, or a
    /// sample's name in Data/emblem), "test" otherwise; null when none can be read. In the game each weapon draws its owner's.
    /// </summary>
    public static float[][]? PreviewValues()
    {
        if (s_Preview != null)
            return s_Preview;

        var s_Name = Environment.GetEnvironmentVariable("CAMO_EMBLEM_PREVIEW") is { Length: > 0 } s_Chosen ? s_Chosen : "test";
        var s_Path = System.IO.File.Exists(s_Name) ? s_Name : System.IO.Path.Combine(DataFolder, s_Name + ".json");
        if (!System.IO.File.Exists(s_Path))
            return null;

        s_Preview = Encode(System.IO.File.ReadAllText(s_Path), CellOf);
        return s_Preview;
    }

    /// <summary>Whether a graph draws the emblem slot.</summary>
    public static bool IsOn(ShaderGraph p_Graph) => p_Graph.Nodes.Any(p_N => p_N.Kind == "EmblemLayers");

    /// <summary>Whether the studio gives camos the emblem slot (CAMO_EMBLEM_SLOT=0 turns it off: the way back).</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable("CAMO_EMBLEM_SLOT") != "0";

    /// <summary>
    /// Whether the slot is PROJECTED from the weapon's own space instead of read from a coordinate map in the texture's unwrap (keku
    /// 2026-09-29: "que pueda poner el sticker donde quiera y que sea calculado acorde para luego el emblema" — BF4's weapons carry a
    /// second, unique unwrap for it; a BF3 mesh does not, so the unique coordinate is computed from the mesh position an emblem clone's
    /// patched vertex shader hands over). ON by default since the game showed it (keku 2026-09-30: the tube's square whole in first and
    /// third person, and "las dos con su emblema, cada una en su sitio" — two weapons, each its own slot out of one shader);
    /// CAMO_EMBLEM_PROJECT=0 is the way back to the coordinate map.
    /// </summary>
    public static bool Projected => Environment.GetEnvironmentVariable("CAMO_EMBLEM_PROJECT") != "0";

    /// <summary>The name every projected slot's frame constant carries, without the compiler's external_ prefix: EmblemF0, EmblemF1…</summary>
    public const string FramePrefix = "EmblemF";

    /// <summary>A projected square as constants: (centre, reach), (x axis, the normal's sign), (y axis, 0) — three registers.</summary>
    public const int FrameRegisters = 3;

    /// <summary>The most squares a projected slot holds on one weapon mesh (their constants sit after the layers, within the 64).</summary>
    public const int ProjectedSquares = 4;

    /// <summary>
    /// A weapon's projected slot as the constants its shader reads (keku 2026-09-29, the emblem "donde quiera" on EVERY weapon of a
    /// camo: the frames ride in each weapon's own variation, not in the shader): <see cref="FrameRegisters"/> vectors per square from
    /// <paramref name="p_Slots"/> (<see cref="ProjectionSlots"/>), <paramref name="p_Squares"/> squares, the ones not given all zero (a
    /// reach of 0 holds no point). The outward normal is carried as the sign that turns normalize(cross(x axis, y axis)) into it.
    /// </summary>
    public static float[][] FramesOf(string? p_Slots, int p_Squares = ProjectedSquares)
    {
        var s_Out = Enumerable.Range(0, p_Squares * FrameRegisters).Select(_ => new float[4]).ToArray();
        var s_Culture = CultureInfo.InvariantCulture;
        var s_Squares = (p_Slots ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p_S => p_S.Split(',').Select(p_V => float.TryParse(p_V, NumberStyles.Float, s_Culture, out var s_V) ? s_V : float.NaN).ToArray())
            .Where(p_V => p_V.Length == 13 && p_V.All(float.IsFinite))
            .Take(p_Squares)
            .ToList();
        for (var k = 0; k < s_Squares.Count; k++)
        {
            var s_V = s_Squares[k];
            var s_A = new Vector3(s_V[3], s_V[4], s_V[5]);
            var s_B = new Vector3(s_V[6], s_V[7], s_V[8]);
            var s_N = new Vector3(s_V[9], s_V[10], s_V[11]);
            var s_Sign = Vector3.Dot(Vector3.Cross(s_A, s_B), s_N) < 0f ? -1f : 1f;
            s_Out[k * FrameRegisters] = new[] { s_V[0], s_V[1], s_V[2], s_V[12] };
            s_Out[k * FrameRegisters + 1] = new[] { s_A.X, s_A.Y, s_A.Z, s_Sign };
            s_Out[k * FrameRegisters + 2] = new[] { s_B.X, s_B.Y, s_B.Z, 0f };
        }

        return s_Out;
    }

    private static string s_PreviewSlots = "";

    /// <summary>The projected slot the preview draws now (<see cref="ProjectionSlots"/> of the weapon on screen), fed as its frame constants.</summary>
    public static void SetPreviewSlots(string p_Slots) => s_PreviewSlots = p_Slots ?? "";

    /// <summary>The projected slot the preview draws (as <see cref="ProjectionSlots"/> writes it).</summary>
    public static string PreviewSlots => s_PreviewSlots;

    /// <summary>
    /// A weapon mesh's slot as the EmblemProjection node reads it: per placement "ox,oy,oz,ax,ay,az,bx,by,bz,nx,ny,nz,reach" in the
    /// mesh's own units (the dump's, the space the patched vertex shader hands over) — its centre, its two axes each divided by the
    /// square's size (so 0.5 + dot(p − centre, axis) is the coordinate across it, x right, y down as the coordinate map has it), the
    /// surface's OUTWARD normal there (⛔ not cross(a, b): with y down that points INTO the body — measured on the M416's buffer tube,
    /// where it rejected every pixel), and how far from its plane a point may lie. The frames are the ones the coordinate map is
    /// composed with (StickerSurface.FrameOf), so both ways draw the same square. Empty when none resolves.
    /// </summary>
    public static string ProjectionSlots(IEnumerable<StickerPlacement> p_Slots, StickerSurface p_Surface)
    {
        var s_Culture = CultureInfo.InvariantCulture;
        var s_Out = new List<string>();
        foreach (var s_Slot in p_Slots)
        {
            if (p_Surface.FrameOf(s_Slot, 1.0) is not { } s_Frame || s_Frame.HalfWidth <= 0f || s_Frame.HalfHeight <= 0f)
                continue;

            var s_Centre = p_Surface.ToRaw(s_Frame.Origin);
            var s_A = s_Frame.AxisX * (p_Surface.RawScale / (2f * s_Frame.HalfWidth));
            var s_B = s_Frame.AxisY * (p_Surface.RawScale / (2f * s_Frame.HalfHeight));
            // within half the square's larger side of its plane: a curved part (a tube's top) keeps the whole square, the far side
            // of the body never gets it (the node's facing test)
            var s_Reach = Math.Max(s_Frame.HalfWidth, s_Frame.HalfHeight) / p_Surface.RawScale;
            var s_N = Vector3.Normalize(s_Frame.Normal);
            s_Out.Add(string.Join(",", new[] { s_Centre.X, s_Centre.Y, s_Centre.Z, s_A.X, s_A.Y, s_A.Z, s_B.X, s_B.Y, s_B.Z, s_N.X, s_N.Y, s_N.Z, s_Reach }
                .Select(p_V => p_V.ToString("0.#######", s_Culture))));
        }

        return string.Join(";", s_Out);
    }

    /// <summary>The picture an emblem placement is composed with: the square's coordinate picture.</summary>
    public static BitmapSource Picture => StickerLayer.EmblemCoordinatePicture();

    /// <summary>A picture resolver that knows the emblem's pseudo-picture and asks <paramref name="p_Inner"/> for the rest.</summary>
    public static Func<string, BitmapSource?> ImageOf(Func<string, BitmapSource?> p_Inner) =>
        p_Name => p_Name == Image ? Picture : p_Inner(p_Name);

    // --- the slot placed by hand -----------------------------------------------------------------------------------------------
    //
    // ⭐ keku 2026-09-29: "iré en cada arma individualmente dentro de camo studio en modo sticker para señalarte la posición donde irán
    // los stickers… le dé a save, genere un archivo… y lo guarde en una carpeta". A weapon's slot, moved, turned and sized by hand in
    // sticker mode, is saved as one file per weapon mesh; the preview and the bake take it over the automatic spot. A weapon with no
    // file keeps the automatic one.

    /// <summary>
    /// Where hand-placed slots are saved: CAMO_EMBLEM_SLOTS when set (a seam's scratch folder — a seam never writes into the user's), the
    /// user's own folder otherwise (%LOCALAPPDATA%\RimeCamoStudio\emblem_slots).
    /// </summary>
    public static string SlotsFolder => Environment.GetEnvironmentVariable("CAMO_EMBLEM_SLOTS") is { Length: > 0 } s_Override
        ? s_Override
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimeCamoStudio", "emblem_slots");

    /// <summary>Slots the studio ships (Data/emblem/slots) — read after the user's own.</summary>
    public static string ShippedSlotsFolder => System.IO.Path.Combine(DataFolder, "slots");

    /// <summary>The file of a weapon mesh's slot: its last path segment (m416_1p_mesh.json).</summary>
    public static string SlotFileName(string p_Mesh) => p_Mesh.Split('/', '\\')[^1].ToLowerInvariant() + ".json";

    private sealed class SavedSlot
    {
        public string Mesh { get; set; } = "";
        public string Weapon { get; set; } = "";
        public string Saved { get; set; } = "";
        public List<StickerPlacement> Placements { get; set; } = new();
    }

    private static readonly System.Text.Json.JsonSerializerOptions s_SlotJson = new() { WriteIndented = true };

    /// <summary>The slot saved for a weapon mesh (the user's, then the studio's), or null when none is; <paramref name="p_From"/> names the file.</summary>
    public static List<StickerPlacement>? SavedPlacements(string p_Mesh, out string? p_From)
    {
        p_From = null;
        foreach (var s_Folder in new[] { SlotsFolder, ShippedSlotsFolder })
        {
            var s_Path = System.IO.Path.Combine(s_Folder, SlotFileName(p_Mesh));
            if (!System.IO.File.Exists(s_Path))
                continue;

            try
            {
                var s_Saved = System.Text.Json.JsonSerializer.Deserialize<SavedSlot>(System.IO.File.ReadAllText(s_Path), s_SlotJson);
                if (s_Saved == null || !s_Saved.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase))
                    continue;

                p_From = s_Path;
                return s_Saved.Placements.Select(p_P => { p_P.Mesh = p_Mesh; p_P.Image = Image; return p_P; }).ToList();
            }
            catch (Exception)
            {
                // an unreadable file is no slot: the automatic one stands (and the caller says where it came from)
            }
        }

        return null;
    }

    /// <summary>Saves a weapon mesh's slot (every placement on it, left and right) into <see cref="SlotsFolder"/>; the file's path.</summary>
    public static string SavePlacements(string p_Mesh, string p_Weapon, IEnumerable<StickerPlacement> p_Placements)
    {
        System.IO.Directory.CreateDirectory(SlotsFolder);
        var s_Path = System.IO.Path.Combine(SlotsFolder, SlotFileName(p_Mesh));
        var s_Saved = new SavedSlot
        {
            Mesh = p_Mesh,
            Weapon = p_Weapon,
            Saved = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            Placements = p_Placements.Select(p_P => new StickerPlacement
            {
                Mesh = p_Mesh, Image = Image, U = p_P.U, V = p_P.V, Width = p_P.Width, Height = p_P.Height, Rotation = p_P.Rotation,
                Mirror = p_P.Mirror, Reach = p_P.Reach, Anchor = p_P.Anchor,
            }).ToList(),
        };
        System.IO.File.WriteAllText(s_Path, System.Text.Json.JsonSerializer.Serialize(s_Saved, s_SlotJson), new System.Text.UTF8Encoding(false));
        return s_Path;
    }

    /// <summary>Forgets a weapon mesh's hand-placed slot (the user's file): the automatic spot comes back. Whether there was one.</summary>
    public static bool ForgetPlacements(string p_Mesh)
    {
        var s_Path = System.IO.Path.Combine(SlotsFolder, SlotFileName(p_Mesh));
        if (!System.IO.File.Exists(s_Path))
            return false;

        System.IO.File.Delete(s_Path);
        return true;
    }

    /// <summary>
    /// The slot of a weapon body: the one saved for its mesh when there is one, the automatic one otherwise — what the preview and the
    /// bake both use. <paramref name="p_Log"/> says which.
    /// </summary>
    public static List<StickerPlacement> PlacementsFor(StickerSurface p_Body, string p_Mesh, double p_SizeMetres = DefaultSizeMetres,
        Action<string>? p_Log = null)
    {
        if (SavedPlacements(p_Mesh, out var s_From) is { Count: > 0 } s_Saved)
        {
            p_Log?.Invoke($"emblem slot: '{p_Mesh.Split('/')[^1]}' placed by hand ({s_Saved.Count} placement(s), {s_From}).");
            return s_Saved;
        }

        return AutoPlacements(p_Body, p_Mesh, p_SizeMetres, p_Log);
    }

    /// <summary>
    /// The slot's placements on a weapon body, one per side of the receiver that has a flat enough spot: rays cast inwards from
    /// each side over the upper middle of the body (the barrel runs along z, up is +y — the weapons' own convention: origin at the
    /// stock, barrel +z), each spot scored by how squarely it faces out and how flat the square around it is; the best one per
    /// side becomes a square placement <paramref name="p_SizeMetres"/> across (in the mesh's units, metres), turned so the
    /// emblem stands upright. <paramref name="p_Log"/> says where each went, or why a side got none.
    /// </summary>
    public static List<StickerPlacement> AutoPlacements(StickerSurface p_Body, string p_Mesh, double p_SizeMetres = DefaultSizeMetres,
        Action<string>? p_Log = null)
    {
        var s_Result = new List<StickerPlacement>();
        var (s_Min, s_Max) = p_Body.Bounds();
        var s_Size = (float) (p_SizeMetres * p_Body.RawScale);
        var s_Half = s_Size / 2f;
        var s_Length = s_Max.Z - s_Min.Z;
        var s_Height = s_Max.Y - s_Min.Y;
        if (s_Length <= 0f || s_Height <= 0f)
            return s_Result;

        foreach (var s_SideSign in new[] { 1f, -1f })
        {
            var s_Out = new Vector3(s_SideSign, 0f, 0f);
            var s_Start = s_SideSign > 0 ? s_Max.X + s_Size : s_Min.X - s_Size;
            var s_Reach = s_Max.X - s_Min.X + 2f * s_Size;

            (Vector3 Position, Vector3 Normal, int Triangle)? s_Best = null;
            var s_BestScore = float.MinValue;
            for (var s_Zf = 0.30f; s_Zf <= 0.70f + 1e-4f; s_Zf += 0.025f)
            for (var s_Yf = 0.40f; s_Yf <= 0.90f + 1e-4f; s_Yf += 0.025f)
            {
                var s_Y = s_Min.Y + s_Yf * s_Height;
                var s_Z = s_Min.Z + s_Zf * s_Length;
                if (Probe(s_Y, s_Z) is not { } s_Centre || !InTile(s_Centre))
                    continue;

                // The square around the spot: its four edge midpoints and corners must land on the same flat face — a
                // spot on a rib or at the edge of a panel is refused, a slightly curved one scored down — and on the unwrap's
                // first tile, the one the coordinate map covers (a weapon lays parts of its body in the tiles beside it: the
                // M416's receiver has parts of its +x side, the left, at v < 0, measured 2026-09-29).
                var s_Score = Vector3.Dot(s_Centre.Normal, s_Out) * 4f;
                var s_Flat = true;
                foreach (var (s_Dy, s_Dz) in new[] { (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f), (1f, 1f), (1f, -1f), (-1f, 1f), (-1f, -1f) })
                {
                    if (Probe(s_Y + s_Dy * s_Half, s_Z + s_Dz * s_Half) is not { } s_Edge || !InTile(s_Edge) ||
                        Vector3.Dot(s_Edge.Normal, s_Out) < 0.85f ||
                        Math.Abs((s_Edge.Position.X - s_Centre.Position.X) * s_SideSign) > s_Half * 0.25f)
                    {
                        s_Flat = false;
                        break;
                    }

                    s_Score += Vector3.Dot(s_Edge.Normal, s_Out) - Math.Abs(s_Edge.Position.X - s_Centre.Position.X) / s_Half;
                }

                if (!s_Flat)
                    continue;

                // Nearer the middle of the receiver (half way along, the upper part) wins a tie.
                s_Score -= Math.Abs(s_Zf - 0.5f) * 0.5f + Math.Abs(s_Yf - 0.7f) * 0.25f;
                if (s_Score > s_BestScore)
                {
                    s_BestScore = s_Score;
                    s_Best = s_Centre;
                }
            }

            if (s_Best is not { } s_Spot)
            {
                p_Log?.Invoke($"emblem slot: no flat spot {s_Size * 100 / p_Body.RawScale:0.#} cm across on the {(s_SideSign > 0 ? "+x" : "-x")} side of '{p_Mesh.Split('/')[^1]}'.");
                continue;
            }

            var s_Uv = p_Body.UvAt(s_Spot.Triangle, s_Spot.Position);
            var s_Raw = p_Body.ToRaw(s_Spot.Position);
            var s_Placement = new StickerPlacement
            {
                Mesh = p_Mesh, Image = Image, U = s_Uv.X, V = s_Uv.Y, Width = 0.1, Height = 0, Rotation = 0,
                Anchor = new double[] { s_Raw.X, s_Raw.Y, s_Raw.Z },
            };

            // Size and upright turn from the frame the placement resolves to unturned: its x runs along the unwrap's u, its y
            // "down" the picture; the emblem's down goes to the body's down (−y) laid into the surface.
            if (p_Body.FrameOf(s_Placement, 1.0) is not { } s_Frame)
            {
                p_Log?.Invoke($"emblem slot: the spot found on '{p_Mesh.Split('/')[^1]}' does not resolve to a frame (uv {s_Uv.X:0.###}, {s_Uv.Y:0.###}).");
                continue;
            }

            s_Placement.Width = s_Size / s_Frame.Scale;
            var s_Down = -Vector3.UnitY - s_Frame.Normal * Vector3.Dot(-Vector3.UnitY, s_Frame.Normal);
            if (s_Down.LengthSquared() > 1e-12f)
            {
                s_Down = Vector3.Normalize(s_Down);
                s_Placement.Rotation = Math.Atan2(Vector3.Dot(s_Down, s_Frame.AxisX), Vector3.Dot(s_Down, s_Frame.AxisY)) * 180.0 / Math.PI;
            }

            s_Result.Add(s_Placement);
            p_Log?.Invoke(string.Create(CultureInfo.InvariantCulture,
                $"emblem slot: '{p_Mesh.Split('/')[^1]}' {(s_SideSign > 0 ? "+x" : "-x")} side at ({s_Raw.X:0.###}, {s_Raw.Y:0.###}, {s_Raw.Z:0.###}) m, uv ({s_Uv.X:0.####}, {s_Uv.Y:0.####}), width {s_Placement.Width:0.####} of the texture, turned {s_Placement.Rotation:0.#}°."));

            // (a local function of the loop body; the rest of the loop body is above)
            // ⛔ flatness is the FACE's normal, not the smooth one: the smooth normals are welded across the unwrap's seams for the
            // lighting (StickerSurface.WeldNormals), which bends them near a panel's edge — the Remington 870 lost its only left spot
            // to that (corpus 56/59, 2026-09-29); the face is what the square has to lie on
            (Vector3 Position, Vector3 Normal, int Triangle)? Probe(float p_Y, float p_Z)
            {
                var s_Hit = p_Body.RayHit(new Vector3(s_Start, p_Y, p_Z), -s_Out, s_Reach, s_Out);
                return s_Hit is { } s_H ? (s_H.Position, p_Body.FaceNormalOf(s_H.Triangle), s_H.Triangle) : null;
            }

            bool InTile((Vector3 Position, Vector3 Normal, int Triangle) p_Hit)
            {
                var s_At = p_Body.UvAt(p_Hit.Triangle, p_Hit.Position);
                return s_At.X > 0.002f && s_At.X < 0.998f && s_At.Y > 0.002f && s_At.Y < 0.998f;
            }
        }

        // ⭐ The LEFT side is the one the first-person view shows (keku 2026-09-29: "si las pones en el derecho no las podré ver"), and
        // the left is +x of the mesh: the M416's bolt catch and fire selector are on +x, its ejection port, dust cover and magazine
        // release on −x (where the "HK 416 D" marking reads MIRRORED — the texture's copy); the first-person camera, CameraJoint in the
        // weapon root's space, sits on +x (measured 2026-09-29 with --fpviewshot). ⛔ This said −x until then, read off that mirrored
        // marking. A weapon whose unwrap lays both sides over ONE patch of texture (the F2000: both slots at uv 0.82, 0.65) can show
        // only one of them — the side map keeps a texel's one side — so there the right one goes and the left one stays.
        if (s_Result.Count == 2)
        {
            var s_A = new Vector2((float) s_Result[0].U, (float) s_Result[0].V);
            var s_B = new Vector2((float) s_Result[1].U, (float) s_Result[1].V);
            if (Vector2.Distance(s_A, s_B) < (s_Result[0].Width + s_Result[1].Width) / 2)
            {
                var s_Right = s_Result.FirstOrDefault(p_P => p_P.Anchor is { Length: 3 } s_R && !IsLeft(s_R));
                if (s_Right != null)
                {
                    s_Result.Remove(s_Right);
                    p_Log?.Invoke($"emblem slot: '{p_Mesh.Split('/')[^1]}' lays both sides over one patch of its texture — the slot stays on the left (+x), the side the first-person view shows.");
                }
            }
        }

        return s_Result;
    }

    /// <summary>Whether a point of a weapon mesh (its raw anchor) is on the weapon's LEFT side, the one the first-person view shows: +x.</summary>
    public static bool IsLeft(double[] p_Anchor) => p_Anchor is { Length: 3 } && p_Anchor[0] > 0;

    /// <summary>
    /// One layer as the ONE constant the slot reads (Palette's EmblemLayers): four integers below 2^24, exact in a float —
    /// x = centre x · 4096 + centre y (12 bits each, the unit square from −0.5 to 1.5); y = half width · 16384 + half height · 16 +
    /// opacity (10, 10 and 4 bits; half sizes 0..2 of the square); z = angle · 4096 + flips · 1024 + atlas cell (12 bits for a clockwise
    /// turn, flip x = 1 and flip y = 2, 10 bits of cell); w = 0xRRGGBB. A layer with any size and any opacity never packs to an empty one.
    /// </summary>
    public static float[] Pack(double p_CentreX, double p_CentreY, double p_HalfWidth, double p_HalfHeight, double p_AngleDegrees,
        bool p_FlipX, bool p_FlipY, int p_Cell, int p_Rgb, double p_Opacity)
    {
        static int Quantise(double p_Value, double p_Low, double p_High, int p_Max) =>
            (int) Math.Clamp(Math.Round((p_Value - p_Low) / (p_High - p_Low) * p_Max), 0, p_Max);

        var s_Cx = Quantise(p_CentreX, -0.5, 1.5, 4095);
        var s_Cy = Quantise(p_CentreY, -0.5, 1.5, 4095);
        var s_Hw = Math.Max(p_HalfWidth > 0 ? 1 : 0, Quantise(p_HalfWidth, 0, 2, 1023));
        var s_Hh = Math.Max(p_HalfHeight > 0 ? 1 : 0, Quantise(p_HalfHeight, 0, 2, 1023));
        var s_Opacity = Math.Max(p_Opacity > 0 ? 1 : 0, Quantise(p_Opacity, 0, 1, 15));
        var s_Turn = ((p_AngleDegrees % 360) + 360) % 360;
        var s_Angle = (int) Math.Round(s_Turn / 360.0 * 4096) % 4096;
        var s_Flips = (p_FlipX ? 1 : 0) + (p_FlipY ? 2 : 0);
        return new float[]
        {
            s_Cx * 4096 + s_Cy,
            s_Hw * 16384 + s_Hh * 16 + s_Opacity,
            s_Angle * 4096 + s_Flips * 1024 + Math.Clamp(p_Cell, 0, 1023),
            p_Rgb & 0xFFFFFF,
        };
    }

    /// <summary>
    /// A BF4 emblem in Battlelog's layer format — {"objects":[{asset, fill, opacity, angle, flipX, flipY, left, top, width, height}]}
    /// on the 320-unit canvas, left/top the layer's CENTRE, layer 0 at the bottom — as the constants the slot reads, one per layer
    /// (<see cref="Pack"/>). <paramref name="p_CellOf"/> names the atlas cell of a shape (null = a shape the atlas does not carry: that
    /// layer is left out, and said). Layers past <see cref="Layers"/> are dropped. Every slot not filled is zero (no layer).
    /// </summary>
    public static float[][] Encode(string p_Json, Func<string, int?> p_CellOf, Action<string>? p_Log = null)
    {
        var s_Vectors = Enumerable.Range(0, Layers).Select(_ => new float[4]).ToArray();
        var s_Objects = JsonNode.Parse(p_Json)?["objects"]?.AsArray();
        if (s_Objects == null)
            return s_Vectors;

        var s_Slot = 0;
        foreach (var s_Object in s_Objects)
        {
            if (s_Object == null)
                continue;
            if (s_Slot >= Layers)
            {
                p_Log?.Invoke($"emblem: more than {Layers} layers — the rest are left out.");
                break;
            }

            var s_Asset = s_Object["asset"]?.GetValue<string>() ?? "";
            if (p_CellOf(s_Asset) is not { } s_Cell)
            {
                p_Log?.Invoke($"emblem: shape '{s_Asset}' is not in the atlas — layer left out.");
                continue;
            }

            double Number(string p_Name, double p_Default) =>
                s_Object[p_Name] is JsonValue s_V && s_V.TryGetValue<double>(out var s_D) ? s_D : p_Default;
            bool Flag(string p_Name) => s_Object[p_Name] is JsonValue s_V && s_V.TryGetValue<bool>(out var s_B) && s_B;

            var s_Fill = (s_Object["fill"]?.GetValue<string>() ?? "#FFFFFF").TrimStart('#');
            var s_Rgb = s_Fill.Length >= 6 && int.TryParse(s_Fill[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var s_Hex) ? s_Hex : 0xFFFFFF;
            s_Vectors[s_Slot] = Pack(Number("left", 160) / 320.0, Number("top", 160) / 320.0, Number("width", 0) / 640.0,
                Number("height", 0) / 640.0, Number("angle", 0), Flag("flipX"), Flag("flipY"), s_Cell, s_Rgb, Number("opacity", 1));
            s_Slot++;
        }

        return s_Vectors;
    }
}
