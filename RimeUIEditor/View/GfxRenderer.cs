using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RimeLib.Cmd.Scaleform;
using RimeLib.Cmd.UiBuilder;

namespace RimeUIEditor.View
{
    /// <summary>
    /// Draws what frame 1 of a Scaleform movie looks like, as WPF drawings in stage pixels: shapes with their
    /// solid, gradient and atlas-bitmap fills, sprites (placements in depth order, masks by clip depth, alpha of
    /// the colour transform), imported symbols resolved through the widget movies, and edit text with the real
    /// glyphs of the game's font library (via the FontMap: $Menu_bold → Purista EA Semibold). What the game
    /// fills in at runtime (list rows, labels from data) is not there, so those stay empty — as in Flash.
    /// Not yet drawn: filters (glow/shadow), blend modes, 9-slice scaling, DefineText.
    /// </summary>
    public class GfxRenderer
    {
        readonly Func<string, GfxMovie?> m_LoadMovie;
        readonly Func<string, BitmapSource?> m_LoadTexture;
        readonly Dictionary<string, string> m_FontMap;     // "$menu_bold" (lower) -> "Purista EA Semibold"
        readonly string m_FontLib;                          // ui/static/fontcollection_en_fontlib
        readonly TextDatabase? m_Texts;
        readonly Dictionary<string, MovieCtx?> m_Movies = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, SwfFont?> m_LibFonts = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Warnings { get; } = new();
        public int ShapesDrawn, TextsDrawn, BitmapsDrawn, ImportsResolved, ImportsMissing;
        /// <summary>ID_* texts the text database could not resolve (drawn red).</summary>
        public int UnresolvedIds;
        /// <summary>Placement paths of the edit texts drawn (diagnostics for text overrides).</summary>
        public List<string> TextPaths { get; } = new();

        /// <summary>Optional override for the text of a named edit text ("KitInfoBox_01/label" → text); null = the movie's own.</summary>
        public Func<string, string?>? TextFor { get; set; }

        /// <summary>
        /// The ActionScript class a widget movie registers for its symbol ("ui/assets/textfield" → "Widget.Text.TextField"), when
        /// known. It decides which widgets build their text at runtime the way the game does (see <see cref="RenderTextRow"/>).
        /// </summary>
        public Func<string, string?>? ClassOf { get; set; }

        /// <summary>One edit text as it was drawn: where, what, at which font size and inside which box (stage-independent, local px).</summary>
        public record DrawnText(string Path, string Text, double SizePx, Rect Box, string Font);

        /// <summary>Every text drawn by the last <see cref="RenderMovie"/>, for the seams that compare the editor with the game.</summary>
        public List<DrawnText> DrawnTexts { get; } = new();

        /// <summary>
        /// Widgets whose class attaches "mc_" + m_rowType at construct and then sizes that row's text field to the placed size
        /// (TextField in its constructor + onClipLoad, ScrollingTextField in onClipLoad): the stage draws that row, not the
        /// previewInstance the movie carries for the game's own editor (whose text sits at 32 px whatever the row type).
        /// </summary>
        static readonly HashSet<string> s_RowTypeClasses = new(StringComparer.Ordinal) { "Widget.Text.TextField", "Widget.Text.ScrollingTextField" };

        class MovieCtx
        {
            public string Resource = "";
            public GfxMovie Movie = null!;
            public Dictionary<ushort, GfxTag> Chars = new();
            public Dictionary<ushort, (string Url, string Symbol)> Imports = new();
            public Dictionary<ushort, GfxMovie.SubImage> Subs = new();
            public Dictionary<ushort, SwfScalingGrid> Grids = new();
            public GfxMovie.ExternalImage? Atlas;
            public BitmapSource? AtlasBitmap;
            public bool AtlasTried;
            public Dictionary<ushort, ImageSource?> Crops = new();
            public Dictionary<ushort, Drawing?> Rendered = new();
            public bool? HasPreview;
            public Dictionary<ushort, Geometry?> MaskGeometry = new();
            public Dictionary<ushort, SwfFont> Fonts = new();
        }

        public GfxRenderer(Func<string, GfxMovie?> p_LoadMovie, Func<string, BitmapSource?> p_LoadTexture,
                           IDictionary<string, string> p_FontMap, string p_FontLib, TextDatabase? p_Texts)
        {
            m_LoadMovie = p_LoadMovie;
            m_LoadTexture = p_LoadTexture;
            m_FontMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in p_FontMap) m_FontMap[kv.Key] = kv.Value;
            m_FontLib = p_FontLib;
            m_Texts = p_Texts;
        }

        /// <summary>Forget everything rendered for a movie (its bytes changed).</summary>
        public void Invalidate(string p_Resource) => m_Movies.Remove(p_Resource);

        // ------------------------------------------------------------------------------------------ movies

        MovieCtx? Ctx(string p_Resource, GfxMovie? p_Preloaded = null)
        {
            if (m_Movies.TryGetValue(p_Resource, out var s_Ctx)) return s_Ctx;
            var s_Movie = p_Preloaded ?? m_LoadMovie(p_Resource);
            if (s_Movie == null) { m_Movies[p_Resource] = null; return null; }
            s_Ctx = new MovieCtx { Resource = p_Resource, Movie = s_Movie, Chars = s_Movie.Characters(), Imports = s_Movie.ImportedCharacters() };
            foreach (var s in s_Movie.SubImages()) s_Ctx.Subs[s.CharacterId] = s;
            s_Ctx.Atlas = s_Movie.ExternalImages().FirstOrDefault();
            foreach (var t in s_Movie.Tags.Where(t => t.Code == 78)) { var g = SwfScalingGrid.Decode(t.Body); s_Ctx.Grids[g.CharacterId] = g; }
            foreach (var t in s_Movie.Tags.Where(t => t.Code is 48 or 75)) { try { var f = SwfFont.Decode(t.Code, t.Body); s_Ctx.Fonts[f.Id] = f; } catch { } }
            m_Movies[p_Resource] = s_Ctx;
            return s_Ctx;
        }

        /// <summary>Frame 1 of a movie's main timeline, as one drawing in stage pixels.</summary>
        public Drawing? RenderMovie(string p_Resource, GfxMovie? p_Movie = null)
        {
            if (p_Movie != null) m_Movies.Remove(p_Resource);
            PlacementGroups.Clear();
            PlacementBounds.Clear();
            TextPaths.Clear();
            DrawnTexts.Clear();
            DrawnPictures.Clear();
            // sprites and texts drawn while there were no text overrides were cached as drawn: a later render with an override
            // (a static text typed on a widget) must draw them again — only the plain shapes stay cached
            foreach (var s_Ctx0 in m_Movies.Values.Where(c => c != null))
                foreach (var s_Id in s_Ctx0!.Rendered.Keys.Where(id => !(s_Ctx0.Chars.TryGetValue(id, out var t) && t.Code is GfxMovie.TagDefineShape or GfxMovie.TagDefineShape2 or GfxMovie.TagDefineShape3 or GfxMovie.TagDefineShape4)).ToList())
                    s_Ctx0.Rendered.Remove(s_Id);
            m_World.Clear(); m_World.Push(Matrix.Identity);
            var s_Ctx = Ctx(p_Resource, p_Movie);
            if (s_Ctx == null) return null;
            return RenderTimeline(s_Ctx, s_Ctx.Movie.Tags, 0);
        }

        /// <summary>The composed transform of the placements above the one being drawn (stage px ← local px).</summary>
        readonly Stack<Matrix> m_World = new();

        /// <summary>
        /// Per named placement (path from the root, imported widgets' insides included), its drawn bounds on the stage
        /// in px: what the game's editor outlines when every clip is shown, not just the screen's own widgets.
        /// </summary>
        public Dictionary<string, Rect> PlacementBounds { get; } = new();

        public Drawing? RenderCharacter(string p_Resource, ushort p_Id)
        {
            var s_Ctx = Ctx(p_Resource);
            return s_Ctx == null ? null : Render(s_Ctx, p_Id, 0);
        }

        /// <summary>The import url of a movie ("Grid.swf", "../Static/gfxfontlib.swf") as a resource name next to the importer.</summary>
        static string ResolveUrl(string p_Importer, string p_Url)
        {
            var s_Dir = p_Importer.Contains('/') ? p_Importer[..p_Importer.LastIndexOf('/')] : "";
            var s_Parts = new List<string>(s_Dir.Split('/', StringSplitOptions.RemoveEmptyEntries));
            foreach (var s_Seg in p_Url.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (s_Seg == "..") { if (s_Parts.Count > 0) s_Parts.RemoveAt(s_Parts.Count - 1); }
                else if (s_Seg != ".") s_Parts.Add(s_Seg);
            }
            var s_Name = string.Join("/", s_Parts);
            var s_Dot = s_Name.LastIndexOf('.');
            if (s_Dot > s_Name.LastIndexOf('/')) s_Name = s_Name[..s_Dot];
            return s_Name.ToLowerInvariant();
        }

        /// <summary>Placement names from the movie's root down to what is being drawn ("PageHeader_01/heading").</summary>
        readonly List<string> m_Path = new();
        public string CurrentPath => string.Join("/", m_Path);

        Drawing? Render(MovieCtx p_Ctx, ushort p_Id, int p_Depth)
        {
            if (p_Depth > 12) return null;
            // with text overrides in play, sprites and texts depend on where they are placed: only shapes are cached; a runtime text
            // row is sized by its placement, so it is never cached either
            var s_Cacheable = (TextFor == null && m_RowBox == null) || (p_Ctx.Chars.TryGetValue(p_Id, out var s_Tag) && s_Tag.Code is GfxMovie.TagDefineShape or GfxMovie.TagDefineShape2 or GfxMovie.TagDefineShape3 or GfxMovie.TagDefineShape4);
            if (s_Cacheable && p_Ctx.Rendered.TryGetValue(p_Id, out var s_Cached)) return s_Cached;
            if (s_Cacheable) p_Ctx.Rendered[p_Id] = null; // recursion guard
            Drawing? s_Result = null;
            try
            {
                if (p_Ctx.Chars.TryGetValue(p_Id, out var t))
                {
                    switch (t.Code)
                    {
                        case GfxMovie.TagDefineShape: case GfxMovie.TagDefineShape2: case GfxMovie.TagDefineShape3: case GfxMovie.TagDefineShape4:
                            s_Result = RenderShape(p_Ctx, SwfShape.Decode(t.Code, t.Body)); break;
                        case GfxMovie.TagDefineSprite:
                            s_Result = RenderTimeline(p_Ctx, t.Children, p_Depth + 1); break;
                        case GfxMovie.TagDefineEditText:
                            s_Result = RenderEditText(p_Ctx, SwfEditText.Decode(t.Body)); break;
                    }
                }
                else if (p_Ctx.Imports.TryGetValue(p_Id, out var s_Import))
                {
                    if (s_Import.Url.EndsWith("gfxfontlib.swf", StringComparison.OrdinalIgnoreCase)) { /* fonts are not drawables */ }
                    else
                    {
                        var s_Resource = ResolveUrl(p_Ctx.Resource, s_Import.Url);
                        var s_Other = Ctx(s_Resource);
                        var s_Exported = s_Other?.Movie.ExportedCharacter(s_Import.Symbol);
                        if (s_Other != null && s_Exported != null)
                        {
                            ++ImportsResolved;
                            // a widget that builds its text at runtime (TextField attaches its row) exports a symbol with no text field in it;
                            // its movie's main timeline carries the preview DICE authored for their editor (previewInstance, with the text
                            // field the widget will show): that is what the stage draws for it
                            if (HasPreview(s_Other) && !ContainsText(s_Other, s_Exported.Value, 0))
                            {
                                ++m_PreviewDepth;
                                try { s_Result = RenderTimeline(s_Other, s_Other.Movie.Tags, p_Depth + 1); }
                                finally { --m_PreviewDepth; }
                            }
                            else s_Result = Render(s_Other, s_Exported.Value, p_Depth + 1);
                        }
                        else { ++ImportsMissing; Warn($"{p_Ctx.Resource}: import {s_Import.Url}:{s_Import.Symbol} not found"); }
                    }
                }
            }
            catch (Exception s_Ex) { Warn($"{p_Ctx.Resource} #{p_Id}: {s_Ex.Message}"); }
            // shapes are frozen (shared, immutable); sprite groups stay mutable so a placement can be moved live
            if (s_Result != null && s_Result.CanFreeze && p_Ctx.Chars.TryGetValue(p_Id, out var s_Def) && s_Def.Code != GfxMovie.TagDefineSprite) s_Result.Freeze();
            if (s_Cacheable) p_Ctx.Rendered[p_Id] = s_Result;
            return s_Result;
        }

        void Warn(string p_Text) { if (Warnings.Count < 200 && !Warnings.Contains(p_Text)) Warnings.Add(p_Text); }

        /// <summary>A drawn picture widget: the placement's path, the texture it shows and the box the picture was fitted into (stage px).</summary>
        public record DrawnPicture(string Path, string Url, Rect Box, Rect Picture);
        /// <summary>The picture widgets drawn by the last RenderMovie.</summary>
        public List<DrawnPicture> DrawnPictures { get; } = new();

        /// <summary>
        /// What a picture widget (Widget.Image.ImageManager) shows, by the placement's name: the picture, the texture path it names
        /// (StaticUrl) and whether the widget fits it into its box (ResizeToTarget). Null = no pictures known.
        /// </summary>
        public Func<string, (BitmapSource? Picture, string Url, bool Fit)>? PictureFor { get; set; }

        const string c_ImageClass = "Widget.Image.ImageManager";

        int m_PreviewDepth;   // > 0 while a widget's editor preview (previewInstance) or its runtime text row is being drawn: its texts are the game's to fill
        Size? m_RowBox;       // while a runtime text row is drawn: the box the game gives its text field (the widget's placed size)
        string? m_RowAlign;   // … and the m_align the widget applies to it
        double? m_RowTextSize; // … and the size the document's textsize op gives its text (null = the row symbol's own)
        Color? m_RowColor;    // … the colour of the text field drawn (the border is tinted with it)

        /// <summary>
        /// Whether a placement is a widget that attaches its text row at runtime (see <see cref="s_RowTypeClasses"/>); gives the
        /// widget's movie and its exported symbol when it is.
        /// </summary>
        bool IsRuntimeTextWidget(MovieCtx p_Ctx, GfxMovie.FramePlacement p, out MovieCtx p_Widget, out ushort p_Exported)
        {
            p_Widget = null!; p_Exported = 0;
            if (ClassOf == null || !p_Ctx.Imports.TryGetValue(p.CharacterId, out var s_Import) || s_Import.Url.EndsWith("gfxfontlib.swf", StringComparison.OrdinalIgnoreCase)) return false;
            var s_Resource = ResolveUrl(p_Ctx.Resource, s_Import.Url);
            var s_Class = ClassOf(s_Resource);
            if (s_Class == null || !s_RowTypeClasses.Contains(s_Class)) return false;
            var s_Other = Ctx(s_Resource);
            var s_Exported = s_Other?.Movie.ExportedCharacter(s_Import.Symbol);
            if (s_Other == null || s_Exported == null) return false;
            p_Widget = s_Other; p_Exported = s_Exported.Value;
            return true;
        }

        /// <summary>
        /// A text widget the way the game builds it (measured in TextField.as / ScrollingTextField.as): the constructor attaches
        /// the movie's "mc_" + m_rowType symbol (the placement's construct-time variable; "_us" appended for the Vehicle rows),
        /// onClipLoad reads the widget's placed size, resets its own scale to 100 % and gives the row's text field that size as
        /// its box (the font keeps the row's own size — bold1 is $Menu_bold 20 px whatever the placement's scale), applies m_align
        /// and blanks the text (a static text arrives through the binding). m_useBorder adds a frame of the text colour.
        /// A placement without m_rowType, or a row type the movie does not export, draws nothing — as in the game.
        /// </summary>
        Drawing? RenderTextRow(MovieCtx p_Widget, ushort p_Exported, GfxMovie.FramePlacement p, int p_Depth)
        {
            var s_RowType = p.ClipVars?.FirstOrDefault(v => v.Name == "m_rowType").Value as string;
            var s_Align = p.ClipVars?.FirstOrDefault(v => v.Name == "m_align").Value as string;
            var s_Border = p.ClipVars?.FirstOrDefault(v => v.Name == "m_useBorder").Value is bool s_B && s_B;
            // the editor's textsize op: the frame action sets the field's format to this size after every text (the row symbol's own size otherwise)
            var s_SizeVar = p.ClipVars?.FirstOrDefault(v => v.Name == "_rueTextSize").Value;
            double? s_TextSize = s_SizeVar is int s_I && s_I > 0 ? s_I : s_SizeVar is double s_D && s_D > 0 ? s_D : s_SizeVar is float s_F && s_F > 0 ? s_F : null;
            if (string.IsNullOrEmpty(s_RowType)) { Warn($"{CurrentPath}: no m_rowType clip variable — the game attaches no text row, so nothing is drawn"); return null; }
            var s_Symbol = "mc_" + s_RowType + (s_RowType.Contains("Vehicle", StringComparison.Ordinal) ? "_us" : "");
            var s_RowId = p_Widget.Movie.ExportedCharacter(s_Symbol);
            if (s_RowId == null) { Warn($"{CurrentPath}: {p_Widget.Resource} exports no '{s_Symbol}' (m_rowType={s_RowType}) — the game attaches no text row, so nothing is drawn"); return null; }
            // the placed size the widget reads at construct: its symbol's natural bounds × the placement's scale
            var s_Natural = p_Widget.Movie.CharacterBounds(p_Exported);
            var (s_Sx, s_Sy) = p.Matrix?.Magnitudes ?? (1.0, 1.0);
            var s_W = s_Natural.Empty ? 0 : (s_Natural.Right - s_Natural.Left) / 20.0 * s_Sx;
            var s_H = s_Natural.Empty ? 0 : (s_Natural.Bottom - s_Natural.Top) / 20.0 * s_Sy;
            var s_Group = new DrawingGroup();
            m_Path.Add(s_RowType);   // attachMovie names the row after its type: instance1/TextField_02/bold1/txtDisplay
            m_RowBox = new Size(s_W, s_H); m_RowAlign = s_Align; m_RowColor = null; m_RowTextSize = s_TextSize; ++m_PreviewDepth;
            try
            {
                var s_Row = Render(p_Widget, s_RowId.Value, p_Depth);
                if (s_Row != null) s_Group.Children.Add(s_Row);
            }
            finally { m_Path.RemoveAt(m_Path.Count - 1); m_RowBox = null; m_RowAlign = null; m_RowTextSize = null; --m_PreviewDepth; }
            if (s_Border && s_W > 0 && s_H > 0)
            {
                // BorderMc, a 9-sliced 10x10 frame stretched to the widget's size and tinted with the text colour: drawn as a 1 px frame
                var s_Pen = new Pen(new SolidColorBrush(m_RowColor ?? Colors.White), 1);
                s_Group.Children.Add(new GeometryDrawing(null, s_Pen, new RectangleGeometry(new Rect(0, 0, s_W, s_H))));
            }
            return s_Group.Children.Count == 0 ? null : s_Group;
        }

        /// <summary>Whether a placement is the game's picture widget (its class), with the widget's movie and exported symbol when it is.</summary>
        bool IsPictureWidget(MovieCtx p_Ctx, GfxMovie.FramePlacement p, out MovieCtx p_Widget, out ushort p_Exported)
        {
            p_Widget = null!; p_Exported = 0;
            if (ClassOf == null || PictureFor == null || p.Name == null || !p_Ctx.Imports.TryGetValue(p.CharacterId, out var s_Import)) return false;
            var s_Resource = ResolveUrl(p_Ctx.Resource, s_Import.Url);
            if (ClassOf(s_Resource) != c_ImageClass) return false;
            var s_Other = Ctx(s_Resource);
            var s_Exported = s_Other?.Movie.ExportedCharacter(s_Import.Symbol);
            if (s_Other == null || s_Exported == null) return false;
            p_Widget = s_Other; p_Exported = s_Exported.Value;
            return true;
        }

        /// <summary>
        /// A picture widget the way the game builds it (measured in ImageManager.as): onClipLoad reads the widget's placed size
        /// (startW/startH = the symbol's bounds × the placement's scale), resets its own scale to 100 % and, once the texture named by
        /// StaticUrl has loaded, puts the picture at its pixel size at the widget's origin — scaled to fit inside the placed box when
        /// ResizeToTarget is on. A picture the editor cannot get (no such texture) leaves the box empty, as in the game.
        /// </summary>
        Drawing? RenderPictureWidget(MovieCtx p_Widget, ushort p_Exported, GfxMovie.FramePlacement p)
        {
            var (s_Picture, s_Url, s_Fit) = PictureFor!(p.Name!);
            if (s_Picture == null) return null;
            var s_Natural = p_Widget.Movie.CharacterBounds(p_Exported);
            var (s_Sx, s_Sy) = p.Matrix?.Magnitudes ?? (1.0, 1.0);
            var s_W = s_Natural.Empty ? 0 : (s_Natural.Right - s_Natural.Left) / 20.0 * s_Sx;
            var s_H = s_Natural.Empty ? 0 : (s_Natural.Bottom - s_Natural.Top) / 20.0 * s_Sy;
            double s_PicW = s_Picture.PixelWidth, s_PicH = s_Picture.PixelHeight;
            if (s_Fit && s_W > 0 && s_H > 0 && s_PicW > 0 && s_PicH > 0)
            {
                var s_Scale = System.Math.Min(s_W / s_PicW, s_H / s_PicH);
                s_PicW = System.Math.Round(s_PicW * s_Scale); s_PicH = System.Math.Round(s_PicH * s_Scale);
            }
            var s_Rect = new Rect(0, 0, s_PicW, s_PicH);
            ++BitmapsDrawn;
            var s_Box = new Rect(0, 0, s_W, s_H);
            DrawnPictures.Add(new DrawnPicture(CurrentPath, s_Url, Rect.Transform(s_Box, m_World.Count > 0 ? m_World.Peek() : Matrix.Identity), Rect.Transform(s_Rect, m_World.Count > 0 ? m_World.Peek() : Matrix.Identity)));
            return new ImageDrawing(s_Picture, s_Rect);
        }

        /// <summary>The matrix with its scale taken out (columns normalised): what _xscale = _yscale = 100 leaves of a placement.</summary>
        static Matrix UnitScale(Matrix m)
        {
            var l1 = System.Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12); var l2 = System.Math.Sqrt(m.M21 * m.M21 + m.M22 * m.M22);
            if (l1 < 1e-9 || l2 < 1e-9) return new Matrix(1, 0, 0, 1, m.OffsetX, m.OffsetY);
            return new Matrix(m.M11 / l1, m.M12 / l1, m.M21 / l2, m.M22 / l2, m.OffsetX, m.OffsetY);
        }

        /// <summary>Whether a character draws a text field anywhere inside it (a sprite's children, recursively).</summary>
        static bool ContainsText(MovieCtx p_Ctx, ushort p_Id, int p_Depth)
        {
            if (p_Depth > 12 || !p_Ctx.Chars.TryGetValue(p_Id, out var t)) return false;
            if (t.Code == GfxMovie.TagDefineEditText) return true;
            if (t.Code != GfxMovie.TagDefineSprite) return false;
            foreach (var c in t.Children.Where(c => GfxMovie.IsPlaceTag(c.Code)))
            {
                try { var p = GfxMovie.DecodePlace(c); if (p.HasCharacter && ContainsText(p_Ctx, p.CharacterId, p_Depth + 1)) return true; }
                catch { /* an undecodable placement draws nothing */ }
            }
            return false;
        }

        /// <summary>Whether a widget movie's main timeline places the editor preview DICE authored ("previewInstance").</summary>
        static bool HasPreview(MovieCtx p_Ctx)
        {
            p_Ctx.HasPreview ??= p_Ctx.Movie.Tags.Where(t => GfxMovie.IsPlaceTag(t.Code)).Any(t => { try { return GfxMovie.DecodePlace(t).Name == "previewInstance"; } catch { return false; } });
            return p_Ctx.HasPreview.Value;
        }

        // ------------------------------------------------------------------------------------------ timelines

        static Matrix ToMatrix(SwfMatrix? m)
        {
            if (m == null) return Matrix.Identity;
            return new Matrix(m.ScaleXf, m.HasRotate ? m.RotateSkew0 / 65536.0 : 0, m.HasRotate ? m.RotateSkew1 / 65536.0 : 0, m.ScaleYf, m.TranslateX / 20.0, m.TranslateY / 20.0);
        }

        /// <summary>Gradient/bitmap fill matrices map fill space (twips or image px) to shape twips; our shapes are in px.</summary>
        static Matrix FillMatrix(SwfMatrix? m)
        {
            if (m == null) return new Matrix(1 / 20.0, 0, 0, 1 / 20.0, 0, 0);
            return new Matrix(m.ScaleXf / 20.0, (m.HasRotate ? m.RotateSkew0 / 65536.0 : 0) / 20.0, (m.HasRotate ? m.RotateSkew1 / 65536.0 : 0) / 20.0, m.ScaleYf / 20.0, m.TranslateX / 20.0, m.TranslateY / 20.0);
        }

        Drawing? RenderTimeline(MovieCtx p_Ctx, IEnumerable<GfxTag> p_Tags, int p_Depth)
        {
            var s_Placements = GfxMovie.FrameOnePlacements(p_Tags);
            if (s_Placements.Count == 0) return null;
            var s_Group = new DrawingGroup();
            DrawingGroup? s_Masked = null;
            ushort s_MaskUntil = 0;
            foreach (var p in s_Placements)
            {
                if (s_Masked != null && p.Depth > s_MaskUntil) { s_Masked = null; }
                if (p.HasClipDepth)
                {
                    // a mask: everything up to ClipDepth is clipped to its filled area
                    var s_Geom = MaskGeometry(p_Ctx, p.CharacterId, p_Depth + 1);
                    s_Masked = new DrawingGroup();
                    if (s_Geom != null)
                    {
                        var s_Clip = s_Geom.Clone();
                        s_Clip.Transform = new MatrixTransform(ToMatrix(p.Matrix));
                        s_Masked.ClipGeometry = s_Clip;
                    }
                    s_MaskUntil = p.ClipDepth;
                    s_Group.Children.Add(s_Masked);
                    continue;
                }
                if (p.Name != null) m_Path.Add(p.Name);
                var s_Path = CurrentPath;
                var s_Local = ToMatrix(p.Matrix);
                // a widget that attaches its text row at runtime: the game resets its scale to 100 % (the row keeps its font size);
                // the picture widget does the same and fits its picture into the placed box
                var s_RuntimeText = IsRuntimeTextWidget(p_Ctx, p, out var s_Widget, out var s_Exported);
                var s_PictureWidget = !s_RuntimeText && IsPictureWidget(p_Ctx, p, out s_Widget, out s_Exported);
                if (s_RuntimeText || s_PictureWidget) s_Local = UnitScale(s_Local);
                var s_World = s_Local * (m_World.Count > 0 ? m_World.Peek() : Matrix.Identity);
                Drawing? s_Child;
                m_World.Push(s_World);
                try { s_Child = s_RuntimeText ? RenderTextRow(s_Widget, s_Exported, p, p_Depth + 1) : s_PictureWidget ? RenderPictureWidget(s_Widget, s_Exported, p) : Render(p_Ctx, p.CharacterId, p_Depth + 1); }
                finally { m_World.Pop(); if (p.Name != null) m_Path.RemoveAt(m_Path.Count - 1); }
                if (s_Child == null) continue;
                var s_Placed = new DrawingGroup { Transform = new MatrixTransform(s_Local) };
                if (p.ColorTransform != null && !p.ColorTransform.IsIdentity) s_Placed.Opacity = p.ColorTransform.AlphaFactor;
                s_Placed.Children.Add(s_Child);
                (s_Masked ?? s_Group).Children.Add(s_Placed);
                // the first group drawn for a named placement is the one a drag on the stage moves live
                if (p.Name != null && !PlacementGroups.ContainsKey(s_Path)) PlacementGroups[s_Path] = s_Placed;
                if (p.Name != null && !PlacementBounds.ContainsKey(s_Path))
                {
                    var s_Bounds = s_Child.Bounds;
                    if (!s_Bounds.IsEmpty) PlacementBounds[s_Path] = Rect.Transform(s_Bounds, s_World);
                }
            }
            return s_Group.Children.Count == 0 ? null : s_Group;
        }

        /// <summary>Per named placement (path from the root), the group carrying its matrix — mutable, for live drags.</summary>
        public Dictionary<string, DrawingGroup> PlacementGroups { get; } = new();

        /// <summary>The filled area of a character (a mask), in its own px space.</summary>
        Geometry? MaskGeometry(MovieCtx p_Ctx, ushort p_Id, int p_Depth)
        {
            if (p_Depth > 12) return null;
            if (p_Ctx.MaskGeometry.TryGetValue(p_Id, out var s_Cached)) return s_Cached;
            Geometry? s_Result = null;
            if (p_Ctx.Chars.TryGetValue(p_Id, out var t))
            {
                if (t.Code is GfxMovie.TagDefineShape or GfxMovie.TagDefineShape2 or GfxMovie.TagDefineShape3 or GfxMovie.TagDefineShape4)
                {
                    var s_Shape = SwfShape.Decode(t.Code, t.Body);
                    var s_All = new GeometryGroup { FillRule = FillRule.Nonzero };
                    for (var i = 1; i <= s_Shape.Fills.Count; ++i)
                    {
                        var g = FillGeometry(s_Shape, i);
                        if (g != null) s_All.Children.Add(g);
                    }
                    if (s_All.Children.Count == 0)
                        s_All.Children.Add(new RectangleGeometry(new Rect(s_Shape.Bounds.Left / 20.0, s_Shape.Bounds.Top / 20.0, (s_Shape.Bounds.Right - s_Shape.Bounds.Left) / 20.0, (s_Shape.Bounds.Bottom - s_Shape.Bounds.Top) / 20.0)));
                    s_Result = s_All;
                }
                else if (t.Code == GfxMovie.TagDefineSprite)
                {
                    var s_All = new GeometryGroup { FillRule = FillRule.Nonzero };
                    foreach (var p in GfxMovie.FrameOnePlacements(t.Children))
                    {
                        var g = MaskGeometry(p_Ctx, p.CharacterId, p_Depth + 1);
                        if (g == null) continue;
                        var c = g.Clone(); c.Transform = new MatrixTransform(ToMatrix(p.Matrix));
                        s_All.Children.Add(c);
                    }
                    s_Result = s_All.Children.Count > 0 ? s_All : null;
                }
            }
            else if (p_Ctx.Imports.TryGetValue(p_Id, out var s_Import) && !s_Import.Url.EndsWith("gfxfontlib.swf", StringComparison.OrdinalIgnoreCase))
            {
                var s_Other = Ctx(ResolveUrl(p_Ctx.Resource, s_Import.Url));
                var s_Exported = s_Other?.Movie.ExportedCharacter(s_Import.Symbol);
                if (s_Other != null && s_Exported != null) s_Result = MaskGeometry(s_Other, s_Exported.Value, p_Depth + 1);
            }
            if (s_Result != null && s_Result.CanFreeze) s_Result.Freeze();
            p_Ctx.MaskGeometry[p_Id] = s_Result;
            return s_Result;
        }

        // ------------------------------------------------------------------------------------------ shapes

        Drawing? RenderShape(MovieCtx p_Ctx, SwfShape p_Shape)
        {
            var s_Group = new DrawingGroup();
            for (var i = 1; i <= p_Shape.Fills.Count; ++i)
            {
                var s_Geom = FillGeometry(p_Shape, i);
                if (s_Geom == null) continue;
                var s_Brush = FillBrush(p_Ctx, p_Shape.Fills[i - 1]);
                if (s_Brush == null) continue;
                s_Group.Children.Add(new GeometryDrawing(s_Brush, null, s_Geom));
            }
            for (var i = 1; i <= p_Shape.Lines.Count; ++i)
            {
                var s_Geom = LineGeometry(p_Shape, i);
                if (s_Geom == null) continue;
                var l = p_Shape.Lines[i - 1];
                var s_Pen = new Pen(new SolidColorBrush(Color.FromArgb(l.Color.A, l.Color.R, l.Color.G, l.Color.B)), System.Math.Max(l.Width / 20.0, 1.0))
                {
                    LineJoin = l.Join == 1 ? PenLineJoin.Bevel : l.Join == 2 ? PenLineJoin.Miter : PenLineJoin.Round,
                    StartLineCap = l.StartCap == 0 ? PenLineCap.Round : l.StartCap == 1 ? PenLineCap.Flat : PenLineCap.Square,
                    EndLineCap = l.EndCap == 0 ? PenLineCap.Round : l.EndCap == 1 ? PenLineCap.Flat : PenLineCap.Square,
                };
                s_Group.Children.Add(new GeometryDrawing(null, s_Pen, s_Geom));
            }
            if (s_Group.Children.Count == 0) return null;
            ++ShapesDrawn;
            return s_Group;
        }

        /// <summary>Closed contours of one fill style, chained from the edges that have it on either side.</summary>
        internal static PathGeometry? FillGeometry(SwfShape p_Shape, int p_Fill)
        {
            var s_Segments = new List<ShapeEdge>();
            foreach (var e in p_Shape.Edges)
            {
                if (e.Fill1 == p_Fill) s_Segments.Add(e);
                if (e.Fill0 == p_Fill) s_Segments.Add(new ShapeEdge { X0 = e.X1, Y0 = e.Y1, X1 = e.X0, Y1 = e.Y0, CX = e.CX, CY = e.CY, IsCurve = e.IsCurve });
            }
            if (s_Segments.Count == 0) return null;
            var s_Geometry = new PathGeometry { FillRule = p_Shape.UsesNonZeroWinding ? FillRule.Nonzero : FillRule.EvenOdd };
            foreach (var s_Figure in Chain(s_Segments, true)) s_Geometry.Figures.Add(s_Figure);
            return s_Geometry.Figures.Count == 0 ? null : s_Geometry;
        }

        static PathGeometry? LineGeometry(SwfShape p_Shape, int p_Line)
        {
            var s_Segments = p_Shape.Edges.Where(e => e.Line == p_Line).ToList();
            if (s_Segments.Count == 0) return null;
            var s_Geometry = new PathGeometry();
            foreach (var s_Figure in Chain(s_Segments, false)) s_Geometry.Figures.Add(s_Figure);
            return s_Geometry;
        }

        /// <summary>Greedy chaining of segments into figures (closed when the walk returns to its start).</summary>
        static IEnumerable<PathFigure> Chain(List<ShapeEdge> p_Segments, bool p_Fill)
        {
            var s_ByStart = new Dictionary<(int, int), List<int>>();
            for (var i = 0; i < p_Segments.Count; ++i)
            {
                var k = (p_Segments[i].X0, p_Segments[i].Y0);
                if (!s_ByStart.TryGetValue(k, out var l)) s_ByStart[k] = l = new List<int>();
                l.Add(i);
            }
            var s_Used = new bool[p_Segments.Count];
            for (var s_Start = 0; s_Start < p_Segments.Count; ++s_Start)
            {
                if (s_Used[s_Start]) continue;
                var s_Figure = new PathFigure { IsFilled = p_Fill, StartPoint = new Point(p_Segments[s_Start].X0 / 20.0, p_Segments[s_Start].Y0 / 20.0) };
                var s_Cur = s_Start;
                var s_First = (p_Segments[s_Start].X0, p_Segments[s_Start].Y0);
                while (true)
                {
                    s_Used[s_Cur] = true;
                    var e = p_Segments[s_Cur];
                    if (e.IsCurve) s_Figure.Segments.Add(new QuadraticBezierSegment(new Point(e.CX / 20.0, e.CY / 20.0), new Point(e.X1 / 20.0, e.Y1 / 20.0), true));
                    else s_Figure.Segments.Add(new LineSegment(new Point(e.X1 / 20.0, e.Y1 / 20.0), true));
                    var s_End = (e.X1, e.Y1);
                    if (s_End == s_First) { s_Figure.IsClosed = true; break; }
                    if (!s_ByStart.TryGetValue(s_End, out var s_Next)) break;
                    var s_Pick = s_Next.FirstOrDefault(i => !s_Used[i], -1);
                    if (s_Pick < 0) break;
                    s_Cur = s_Pick;
                }
                if (p_Fill) s_Figure.IsClosed = true;
                yield return s_Figure;
            }
        }

        Brush? FillBrush(MovieCtx p_Ctx, SwfFillStyle f)
        {
            if (f.IsSolid)
                return new SolidColorBrush(Color.FromArgb(f.Color.A, f.Color.R, f.Color.G, f.Color.B));
            if (f.IsGradient && f.Gradient != null)
            {
                var s_Stops = new GradientStopCollection();
                foreach (var (s_Ratio, s_Color) in f.Gradient.Records)
                    s_Stops.Add(new GradientStop(Color.FromArgb(s_Color.A, s_Color.R, s_Color.G, s_Color.B), s_Ratio / 255.0));
                var s_Spread = f.Gradient.Spread == 1 ? GradientSpreadMethod.Reflect : f.Gradient.Spread == 2 ? GradientSpreadMethod.Repeat : GradientSpreadMethod.Pad;
                GradientBrush s_Brush;
                if (f.Type == 0x10)
                    s_Brush = new LinearGradientBrush(s_Stops, new Point(-16384, 0), new Point(16384, 0)) { MappingMode = BrushMappingMode.Absolute, SpreadMethod = s_Spread };
                else
                    s_Brush = new RadialGradientBrush(s_Stops)
                    {
                        MappingMode = BrushMappingMode.Absolute, Center = new Point(0, 0), RadiusX = 16384, RadiusY = 16384,
                        GradientOrigin = new Point(16384 * f.Gradient.FocalPoint, 0), SpreadMethod = s_Spread,
                    };
                s_Brush.Transform = new MatrixTransform(FillMatrix(f.Matrix));
                return s_Brush;
            }
            if (f.IsBitmap)
            {
                if (f.BitmapId == 0xFFFF) return null;
                var s_Image = Crop(p_Ctx, f.BitmapId);
                if (s_Image == null) return null;
                ++BitmapsDrawn;
                var s_Brush = new ImageBrush(s_Image)
                {
                    ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, s_Image.Width, s_Image.Height),
                    Stretch = Stretch.Fill, TileMode = f.BitmapClipped ? TileMode.None : TileMode.Tile,
                    Transform = new MatrixTransform(FillMatrix(f.Matrix)),
                };
                RenderOptions.SetBitmapScalingMode(s_Brush, f.BitmapSmoothed ? BitmapScalingMode.Linear : BitmapScalingMode.NearestNeighbor);
                return s_Brush;
            }
            return null;
        }

        /// <summary>A bitmap character = a rectangle of the movie's atlas texture.</summary>
        ImageSource? Crop(MovieCtx p_Ctx, ushort p_BitmapId)
        {
            if (p_Ctx.Crops.TryGetValue(p_BitmapId, out var s_Cached)) return s_Cached;
            ImageSource? s_Result = null;
            if (p_Ctx.Subs.TryGetValue(p_BitmapId, out var s_Sub) && p_Ctx.Atlas != null)
            {
                if (!p_Ctx.AtlasTried)
                {
                    p_Ctx.AtlasTried = true;
                    var s_TexName = RimeUiService.AtlasResourceName(p_Ctx.Resource, p_Ctx.Atlas.FileName);
                    p_Ctx.AtlasBitmap = m_LoadTexture(s_TexName);
                    if (p_Ctx.AtlasBitmap == null) Warn($"{p_Ctx.Resource}: atlas texture {s_TexName} not available");
                }
                var s_Atlas = p_Ctx.AtlasBitmap;
                if (s_Atlas != null)
                {
                    var l = System.Math.Clamp((int)s_Sub.Left, 0, s_Atlas.PixelWidth); var t = System.Math.Clamp((int)s_Sub.Top, 0, s_Atlas.PixelHeight);
                    var r = System.Math.Clamp((int)s_Sub.Right, l, s_Atlas.PixelWidth); var b = System.Math.Clamp((int)s_Sub.Bottom, t, s_Atlas.PixelHeight);
                    if (r > l && b > t)
                    {
                        var s_Crop = new CroppedBitmap(s_Atlas, new Int32Rect(l, t, r - l, b - t));
                        s_Crop.Freeze();
                        s_Result = s_Crop;
                    }
                }
            }
            else Warn($"{p_Ctx.Resource}: bitmap #{p_BitmapId} has no sub-image");
            p_Ctx.Crops[p_BitmapId] = s_Result;
            return s_Result;
        }

        // ------------------------------------------------------------------------------------------ text

        static readonly Typeface s_Fallback = new("Segoe UI");

        SwfFont? ResolveFont(MovieCtx p_Ctx, ushort p_FontId)
        {
            if (p_Ctx.Fonts.TryGetValue(p_FontId, out var s_Local) && s_Local.Glyphs.Count > 0) return s_Local;
            string? s_LongName = null;
            if (p_Ctx.Imports.TryGetValue(p_FontId, out var s_Import))
                s_LongName = m_FontMap.TryGetValue(s_Import.Symbol, out var s_Mapped) ? s_Mapped : s_Import.Symbol;
            else if (s_Local != null)
                s_LongName = m_FontMap.TryGetValue(s_Local.Name, out var s_Mapped) ? s_Mapped : s_Local.Name;
            if (s_LongName == null) return null;
            if (m_LibFonts.TryGetValue(s_LongName, out var s_Lib)) return s_Lib;
            var s_LibCtx = Ctx(m_FontLib);
            s_Lib = s_LibCtx?.Fonts.Values.FirstOrDefault(f => string.Equals(f.Name, s_LongName, StringComparison.OrdinalIgnoreCase) && f.Glyphs.Count > 0);
            m_LibFonts[s_LongName] = s_Lib;
            if (s_Lib == null) Warn($"font '{s_LongName}' not in {m_FontLib}");
            return s_Lib;
        }

        Drawing? RenderEditText(MovieCtx p_Ctx, SwfEditText t)
        {
            if (TextPaths.Count < 500) TextPaths.Add(CurrentPath + " = " + t.InitialText);
            var s_Override = TextFor?.Invoke(CurrentPath);
            // inside an editor preview (a widget that fills its text at runtime) a placeholder like "baseText0" is not the game's text: blank unless a static text is set
            if (s_Override == null && m_PreviewDepth > 0) return null;
            var s_Text = s_Override ?? t.InitialText;
            if (m_Texts != null && s_Text.StartsWith("ID_", StringComparison.Ordinal)) s_Text = m_Texts.Resolve(s_Text);
            if (t.Html) s_Text = StripHtml(s_Text);
            if (string.IsNullOrWhiteSpace(s_Text)) return null;
            var s_Font = t.HasFont ? ResolveFont(p_Ctx, t.FontId) : null;
            var s_HeightPx = m_RowTextSize ?? (t.HasFont && t.FontHeight > 0 ? t.FontHeight : 240) / 20.0;
            var s_Color = Color.FromArgb(t.TextColor.A, t.TextColor.R, t.TextColor.G, t.TextColor.B);
            // an ID the text database does not know stays an ID and is painted red, the way the game's own editor flags it
            if (s_Text.StartsWith("ID_", StringComparison.Ordinal)) { s_Color = Color.FromArgb(255, 220, 60, 60); UnresolvedIds++; }
            var s_Box = new Rect(t.Bounds.Left / 20.0, t.Bounds.Top / 20.0, System.Math.Max(0, (t.Bounds.Right - t.Bounds.Left) / 20.0), System.Math.Max(0, (t.Bounds.Bottom - t.Bounds.Top) / 20.0));
            // a runtime text row: the game sets the field's _width/_height to the widget's placed size (the box moves, the font does not)
            if (m_RowBox is { } s_Fixed) s_Box = new Rect(s_Box.Left, s_Box.Top, s_Fixed.Width, s_Fixed.Height);
            var s_AlignCode = t.HasLayout ? t.Align : 0;
            if (m_RowAlign != null) s_AlignCode = m_RowAlign.Equals("center", StringComparison.OrdinalIgnoreCase) ? 2 : m_RowAlign.Equals("right", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            if (m_RowBox != null) m_RowColor = s_Color;
            const double c_Gutter = 2;
            var s_Left = s_Box.Left + c_Gutter + (t.HasLayout ? t.LeftMargin / 20.0 : 0);
            var s_Right = s_Box.Right - c_Gutter - (t.HasLayout ? t.RightMargin / 20.0 : 0);
            var s_Width = System.Math.Max(0, s_Right - s_Left);
            var s_Group = new DrawingGroup();
            ++TextsDrawn;
            DrawnTexts.Add(new DrawnText(CurrentPath, s_Text, s_HeightPx, s_Box, s_Font?.Name ?? (t.HasFont && p_Ctx.Imports.TryGetValue(t.FontId, out var s_FontImport) ? s_FontImport.Symbol : "(device)")));

            if (s_Font == null)
            {
                // no glyphs (a device font such as Arial, or a font the library lacks): the system's text
                var s_Formatted = new FormattedText(s_Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, s_Fallback, s_HeightPx, new SolidColorBrush(s_Color), 1.0)
                { MaxTextWidth = t.WordWrap && s_Width > 0 ? s_Width : 10000, Trimming = TextTrimming.None };
                s_Formatted.TextAlignment = s_AlignCode == 2 ? TextAlignment.Center : s_AlignCode == 1 ? TextAlignment.Right : TextAlignment.Left;
                var s_Geom = s_Formatted.BuildGeometry(new Point(s_Left, s_Box.Top + c_Gutter));
                s_Group.Children.Add(new GeometryDrawing(new SolidColorBrush(s_Color), null, s_Geom));
                return s_Group;
            }

            var s_Scale = s_HeightPx / s_Font.UnitsPerEm;
            var s_Ascent = (s_Font.HasLayout && s_Font.Ascent > 0 ? s_Font.Ascent : s_Font.UnitsPerEm * 0.8) * s_Scale;
            var s_Descent = (s_Font.HasLayout ? s_Font.Descent : s_Font.UnitsPerEm * 0.2) * s_Scale;
            var s_LineHeight = s_Ascent + s_Descent + (t.HasLayout ? t.Leading / 20.0 : 0);
            var s_Lines = Layout(s_Text, s_Font, s_Scale, t.Multiline && t.WordWrap ? s_Width : double.PositiveInfinity, t.Multiline);
            var s_Brush = new SolidColorBrush(s_Color);
            var y = s_Box.Top + c_Gutter + s_Ascent;
            foreach (var s_Line in s_Lines)
            {
                var s_LineWidth = s_Line.Sum(c => s_Font.Advance(c) * s_Scale);
                var x = s_Left + (t.HasLayout ? t.Indent / 20.0 : 0);
                if (s_AlignCode == 2) x = s_Left + (s_Width - s_LineWidth) / 2;
                else if (s_AlignCode == 1) x = s_Right - s_LineWidth;
                foreach (var ch in s_Line)
                {
                    if (s_Font.CodeToGlyph.TryGetValue(ch, out var gi) && gi < s_Font.Glyphs.Count)
                    {
                        var s_Glyph = GlyphGeometry(s_Font.Glyphs[gi]);
                        if (s_Glyph != null)
                        {
                            var g = s_Glyph.Clone();
                            g.Transform = new MatrixTransform(s_Scale * 20, 0, 0, s_Scale * 20, x, y);
                            s_Group.Children.Add(new GeometryDrawing(s_Brush, null, g));
                        }
                    }
                    x += s_Font.Advance(ch) * s_Scale;
                }
                y += s_LineHeight;
            }
            return s_Group.Children.Count == 0 ? null : s_Group;
        }

        readonly Dictionary<SwfShape, PathGeometry?> m_GlyphCache = new();

        PathGeometry? GlyphGeometry(SwfShape p_Glyph)
        {
            if (m_GlyphCache.TryGetValue(p_Glyph, out var s_Cached)) return s_Cached;
            // glyph edges carry fill 1 as ink; the geometry builder works in px (edge/20), so the scale above multiplies by 20 again
            var s_Geom = FillGeometry(p_Glyph, 1);
            if (s_Geom != null) { s_Geom.FillRule = FillRule.Nonzero; s_Geom.Freeze(); }
            m_GlyphCache[p_Glyph] = s_Geom;
            return s_Geom;
        }

        static List<string> Layout(string p_Text, SwfFont p_Font, double p_Scale, double p_MaxWidth, bool p_Multiline)
        {
            var s_Lines = new List<string>();
            foreach (var s_Para in (p_Multiline ? p_Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n') : new[] { p_Text.Replace("\r", "").Replace("\n", " ") }))
            {
                if (double.IsInfinity(p_MaxWidth) || p_MaxWidth <= 0) { s_Lines.Add(s_Para); continue; }
                var s_Line = ""; var s_LineWidth = 0.0;
                foreach (var s_Word in s_Para.Split(' '))
                {
                    var s_Piece = s_Line.Length == 0 ? s_Word : " " + s_Word;
                    var s_PieceWidth = s_Piece.Sum(c => p_Font.Advance(c) * p_Scale);
                    if (s_Line.Length > 0 && s_LineWidth + s_PieceWidth > p_MaxWidth) { s_Lines.Add(s_Line); s_Line = s_Word; s_LineWidth = s_Word.Sum(c => p_Font.Advance(c) * p_Scale); }
                    else { s_Line += s_Piece; s_LineWidth += s_PieceWidth; }
                }
                s_Lines.Add(s_Line);
            }
            return s_Lines;
        }

        static string StripHtml(string p_Text)
        {
            var s_Sb = new System.Text.StringBuilder();
            var s_In = false;
            foreach (var c in p_Text)
            {
                if (c == '<') { s_In = true; continue; }
                if (c == '>') { s_In = false; continue; }
                if (!s_In) s_Sb.Append(c);
            }
            return s_Sb.ToString().Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">");
        }
    }
}
