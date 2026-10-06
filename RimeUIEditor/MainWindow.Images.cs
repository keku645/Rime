using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using RimeLib.Cmd.UiBuilder;
using RimeUIEditor.Model;
using RimeUIEditor.View;

namespace RimeUIEditor
{
    /// <summary>
    /// The document's own pictures: a PNG of the user's, kept with the document and shown by the game's picture widget (ImageManager,
    /// the one every icon goes through) — chosen in the widget's own properties ("Custom texture": keku, 2026-09-16: the picture is a
    /// property of the widget, not a button of its own). Its StaticUrl names the picture as the texture the build ships it as
    /// (UI/Art/&lt;mod&gt;/&lt;name&gt;), ResizeToTarget fits it into the widget's box. The stage draws the PNG the way the game will,
    /// the live preview serves it to the player, the build converts it (see ImageTextures).
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>The widget every picture rides on.</summary>
        const string c_ImageWidget = "ui/assets/imagemanager";

        /// <summary>The document's folder, for the pictures' relative files; empty until Save as… has given it one.</summary>
        string DocDir => m_DocPath == "" ? "" : Path.GetDirectoryName(m_DocPath) ?? "";

        /// <summary>
        /// The Custom texture rows of a picture widget: the picture (one of the document's, or a PNG chosen with …), and the widget's
        /// box against the picture's size with a button that makes them equal (the game fits the picture into the box, aspect kept).
        /// </summary>
        IEnumerable<PropRow> CustomTextureRows(string p_Label, NodeInfo? p_Shipped, NodeEntry? p_Doc)
        {
            var s_Url = CurrentProperties(p_Shipped, p_Doc).FirstOrDefault(kv => kv.Key == "StaticUrl").Value ?? "";
            var s_Image = m_Doc.ImageByTexture(s_Url);
            var s_Names = m_Doc.Images.Select(i => i.Name).ToList();
            var s_Picture = new PropRow
            {
                Path = "img.custom", Name = "Custom texture", Kind = UiTypeCatalog.Kind.String, Value = s_Image?.Name ?? "", Choices = s_Names, Modified = s_Image != null,
                Tooltip = "A picture of your own for this widget: … chooses a PNG (transparency kept), copied in with the document and shipped by the build as a texture of the game (UI/Art/" + m_Doc.ImageFolder + "/<name>); the list offers the document's pictures. Sets StaticUrl to the texture and ResizeToTarget on. Empty takes the picture off the widget." + (s_Url != "" && s_Image == null ? $"\nStaticUrl now names the game's own texture {s_Url}." : ""),
                ActionLabel = "…", ActionTip = "choose a PNG file",
                OnChange = v => SetCustomTexture(p_Label, m_Doc.Images.FirstOrDefault(i => string.Equals(i.Name, v?.ToString()?.Trim(), StringComparison.OrdinalIgnoreCase))),
                OnAction = () =>
                {
                    if (EditorSettings.Headless) { Log("Custom texture: no file dialog in a headless run"); return; }
                    var d = new OpenFileDialog { Title = "Custom texture — a PNG (transparency kept) shipped with the mod as a texture of the game", Filter = "PNG picture|*.png" };
                    if (d.ShowDialog() != true) return;
                    SetCustomTextureFile(p_Label, d.FileName);
                },
            };
            yield return s_Picture;
            if (s_Image == null) yield break;
            var s_Bitmap = PictureFor(m_Doc.ImageTextureName(s_Image));
            var s_Box = m_Current?.Placements.FirstOrDefault(p => p.Name == p_Label);
            var (s_NatW, s_NatH) = ImageWidgetNaturalSize();
            var s_BoxW = s_Box != null ? s_NatW * s_Box.SizeX : 0; var s_BoxH = s_Box != null ? s_NatH * s_Box.SizeY : 0;
            var s_Same = s_Bitmap != null && System.Math.Abs(s_BoxW - s_Bitmap.PixelWidth) < 0.5 && System.Math.Abs(s_BoxH - s_Bitmap.PixelHeight) < 0.5;
            yield return new PropRow
            {
                Path = "img.fit", Name = "Box", ReadOnly = true,
                Text = s_Bitmap == null ? "(the picture's file cannot be read)" : $"{s_BoxW:0}x{s_BoxH:0} px, the picture {s_Bitmap.PixelWidth}x{s_Bitmap.PixelHeight}" + (s_Same ? " — 1:1" : " — fitted inside, aspect kept"),
                Tooltip = "The widget's box (its scale × the symbol's size). In the game the picture is scaled to fit inside it; Fit makes the box the picture's own size so it draws pixel for pixel.",
                ActionLabel = "Fit", ActionTip = "make the box the picture's size (1:1)",
                OnAction = s_Bitmap == null ? null : () => FitBoxToPicture(p_Label, s_Bitmap.PixelWidth, s_Bitmap.PixelHeight),
            };
        }

        /// <summary>The picture widget symbol's own size in stage px (what scale 1 shows), from its movie; (0,0) without the cache.</summary>
        (double W, double H) ImageWidgetNaturalSize()
        {
            var s_Movie = WidgetMovie(c_ImageWidget);
            var s_Symbol = s_Movie?.Exports().SelectMany(x => x.Characters).Select(c => c.Name).FirstOrDefault(n => string.Equals(n, "ImageManager", StringComparison.OrdinalIgnoreCase));
            var s_Id = s_Symbol != null ? s_Movie!.ExportedCharacter(s_Symbol) : null;
            if (s_Movie == null || s_Id == null) return (0, 0);
            var s_Bounds = s_Movie.CharacterBounds(s_Id.Value);
            return s_Bounds.Empty ? (0, 0) : ((s_Bounds.Right - s_Bounds.Left) / 20.0, (s_Bounds.Bottom - s_Bounds.Top) / 20.0);
        }

        /// <summary>
        /// A PNG file chosen for a picture widget: read (a PNG the build can convert, or it is refused here rather than at build time),
        /// named after its file (unique in the document), copied under the document's folder when it has one, listed, and set on the
        /// widget. Returns the document's entry, or null.
        /// </summary>
        public ImageEntry? SetCustomTextureFile(string p_Label, string p_File)
        {
            PngImage s_Png;
            try { s_Png = PngImage.Load(p_File); }
            catch (Exception s_Ex) { Log($"Custom texture: {Path.GetFileName(p_File)} cannot be read as a PNG the build converts ({s_Ex.Message})"); return null; }
            var s_Base = ImageTextures.NameFromFile(p_File);
            var s_Name = s_Base;
            for (var i = 2; m_Doc.Images.Any(x => string.Equals(x.Name, s_Name, StringComparison.OrdinalIgnoreCase)); ++i) s_Name = s_Base + i;
            var s_Entry = new ImageEntry { Name = s_Name, File = p_File };
            // the picture lives with the document when the document lives somewhere; else its own path, moved in at the next save
            if (DocDir != "") s_Entry.File = KeepImageWithDocument(s_Entry, p_File);
            PushUndo();
            m_Doc.Images.Add(s_Entry);
            Log($"image {s_Name}: {Path.GetFileName(p_File)} {s_Png.Width}x{s_Png.Height} → {m_Doc.ImageTextureName(s_Entry)} (shipped by the build as a DXT5 texture)" + (DocDir == "" ? " — Save as… moves the file in with the document" : ""));
            SetCustomTexture(p_Label, s_Entry, false);
            return s_Entry;
        }

        /// <summary>
        /// The picture a widget shows: StaticUrl = the picture's texture name and ResizeToTarget = true on the node (the shipped node's
        /// other properties kept when it is one); null takes the picture off (StaticUrl empty). The box is the user's: Fit sizes it.
        /// </summary>
        public void SetCustomTexture(string p_Label, ImageEntry? p_Image, bool p_Undo = true)
        {
            if (m_Current == null) return;
            if (p_Undo) PushUndo();
            var s_Shipped = m_Current.AllNodes.FirstOrDefault(n => n.Label == p_Label);
            var s_Existing = DocScreen(m_Current.Partition)?.Nodes.FirstOrDefault(n => n.InstanceName == p_Label);
            var e = s_Existing ?? EditShippedNode(p_Label);
            if (e.Properties.Count == 0 && s_Shipped != null) foreach (var (k, v) in CurrentProperties(s_Shipped, null)) e.Properties[k] = v;
            if (p_Image != null)
            {
                e.Properties["StaticUrl"] = m_Doc.ImageTextureName(p_Image);
                e.Properties["ResizeToTarget"] = "true";
                Log($"{p_Label}: custom texture {p_Image.Name} (StaticUrl {e.Properties["StaticUrl"]}, fitted into its box — Fit makes the box the picture's size)");
            }
            else
            {
                e.Properties["StaticUrl"] = "";
                Log($"{p_Label}: custom texture taken off (StaticUrl empty)");
            }
            ApplyDocumentToCurrent();
            ShowProperties();
        }

        /// <summary>The widget's box made the picture's pixel size: its scale = picture / symbol size (the add op's or a scale op).</summary>
        public void FitBoxToPicture(string p_Label, int p_Width, int p_Height)
        {
            var (s_NatW, s_NatH) = ImageWidgetNaturalSize();
            if (s_NatW < 1 || s_NatH < 1) { Log("Fit: the picture widget's symbol has no size to scale from (mount the game once)"); return; }
            OnPlacementScaled(p_Label, p_Width / s_NatW, p_Height / s_NatH);
            ShowProperties();
        }

        /// <summary>The picture's file copied under &lt;document folder&gt;/images (when it is not there already); the relative path the entry keeps.</summary>
        string KeepImageWithDocument(ImageEntry p_Entry, string p_Source)
        {
            var s_Dir = Path.Combine(DocDir, "images");
            Directory.CreateDirectory(s_Dir);
            var s_Target = Path.Combine(s_Dir, p_Entry.Name + ".png");
            if (!string.Equals(Path.GetFullPath(p_Source), Path.GetFullPath(s_Target), StringComparison.OrdinalIgnoreCase)) File.Copy(p_Source, s_Target, true);
            return "images/" + p_Entry.Name + ".png";
        }

        /// <summary>Pictures added before the document had a folder (absolute files) move in with it when it is saved.</summary>
        void RelocateImages()
        {
            if (DocDir == "") return;
            foreach (var s_Image in m_Doc.Images.Where(i => Path.IsPathRooted(i.File)))
            {
                try { if (File.Exists(s_Image.File)) { s_Image.File = KeepImageWithDocument(s_Image, s_Image.File); Log($"image {s_Image.Name}: copied in with the document ({s_Image.File})"); } }
                catch (Exception s_Ex) { Log($"image {s_Image.Name}: not copied in with the document ({s_Ex.Message}); it stays at {s_Image.File}"); }
            }
        }

        /// <summary>Takes a picture out of the document (its widgets stay: a StaticUrl the game has no texture for shows nothing).</summary>
        void RemoveImage(ImageEntry p_Image)
        {
            PushUndo();
            m_Doc.Images.Remove(p_Image);
            m_ImageBitmaps.Clear();
            Log($"image {p_Image.Name} removed from the document (its file is kept; widgets naming {m_Doc.ImageTextureName(p_Image)} show nothing now)");
            ApplyDocumentToCurrent();
            ShowProperties();
        }

        /// <summary>Decoded document pictures by file and write time (the preview's server asks from its own thread).</summary>
        readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapSource?> m_ImageBitmaps = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The picture a texture path names: a document picture's PNG, else the game's texture from the cache; null when neither.</summary>
        BitmapSource? PictureFor(string p_Url)
        {
            if (string.IsNullOrWhiteSpace(p_Url)) return null;
            var s_Image = m_Doc.ImageByTexture(p_Url);
            if (s_Image == null) return LoadTexture(p_Url.Replace('\\', '/').Trim('/').ToLowerInvariant());
            var s_Path = ImageTextures.ImagePath(s_Image, DocDir);
            var s_Key = s_Path + "|" + (File.Exists(s_Path) ? File.GetLastWriteTimeUtc(s_Path).Ticks : 0);
            return m_ImageBitmaps.GetOrAdd(s_Key, _ =>
            {
                try
                {
                    // decoded by the same code the build uses, so the stage shows the pixels the texture will carry (colour profile untouched)
                    var s_Png = PngImage.Load(s_Path);
                    var s_Bitmap = BitmapSource.Create(s_Png.Width, s_Png.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, s_Png.Bgra, s_Png.Width * 4);
                    s_Bitmap.Freeze();
                    return s_Bitmap;
                }
                catch (Exception s_Ex) { Dispatcher.BeginInvoke(() => Log($"image {s_Image.Name}: {s_Ex.Message}")); return null; }
            });
        }

        /// <summary>
        /// What each picture widget of the current screen shows: placement name → (StaticUrl, ResizeToTarget), from the document's nodes
        /// (a node's own properties, or the shipped node's when it edits one and keeps them) and the shipped widgets.
        /// </summary>
        Dictionary<string, (string Url, bool Fit)> PictureUrls()
        {
            var s_Out = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);
            if (m_Current == null) return s_Out;
            foreach (var w in m_Current.Widgets)
            {
                var s_Url = w.Properties.FirstOrDefault(p => p.Name == "StaticUrl").Value;
                var s_Fit = string.Equals(w.Properties.FirstOrDefault(p => p.Name == "ResizeToTarget").Value, "true", StringComparison.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(s_Url)) s_Out[w.InstanceName] = (s_Url, s_Fit);
            }
            foreach (var n in DocScreen(m_Current.Partition)?.Nodes ?? new List<NodeEntry>())
            {
                if (n.Properties.TryGetValue("StaticUrl", out var s_Url) && !string.IsNullOrEmpty(s_Url))
                    s_Out[n.InstanceName] = (s_Url, n.Properties.TryGetValue("ResizeToTarget", out var s_Fit) && string.Equals(s_Fit, "true", StringComparison.OrdinalIgnoreCase));
                else if (n.Properties.ContainsKey("StaticUrl")) s_Out.Remove(n.InstanceName);
            }
            return s_Out;
        }
    }
}
