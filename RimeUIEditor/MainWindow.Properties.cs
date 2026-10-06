using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json.Linq;
using RimeLib.Cmd.UiBuilder;
using RimeUIEditor.Model;
using RimeUIEditor.View;

namespace RimeUIEditor
{
    /// <summary>
    /// The properties panel: a property grid in the style of the game's own editor. Every field of the selected
    /// node's type (from the game's type information), grouped by the type that declares it, showing the value the
    /// screen ships — and editable in place: the first edit puts the node into the document, later edits change it
    /// there (bold names mark what the document overrides). Widgets add their stage placement, their properties
    /// (with the names and values the shipped screens use for that widget), their data binding as a whole instance,
    /// their event contract and the wiring. Every edit is one undo step.
    /// </summary>
    public partial class MainWindow
    {
        WidgetCatalog? m_WidgetCatalog;
        bool m_CatalogBuilding;
        /// <summary>What each widget's own code reads (Data/widget_settings.json, measured from the widgets' scripts).</summary>
        WidgetSettings? m_WidgetSettings;
        bool m_WidgetSettingsTried;
        /// <summary>The widget asset of the node whose rows are being built (a binding's DataName suggestions come from it).</summary>
        string? m_RowWidget;

        WidgetSettings? Settings()
        {
            if (!m_WidgetSettingsTried) { m_WidgetSettingsTried = true; m_WidgetSettings = WidgetSettings.Load(); if (m_WidgetSettings == null) Log("Data/widget_settings.json not found: the widgets' own settings are not listed"); }
            return m_WidgetSettings;
        }

        /// <summary>The per-widget knowledge (events, properties in use, binding types), from the cache file or built once in the background.</summary>
        void EnsureWidgetCatalog()
        {
            if (m_WidgetCatalog != null || m_CatalogBuilding) return;
            m_WidgetCatalog = WidgetCatalog.LoadCached(m_Rime.CacheDir);
            if (m_WidgetCatalog != null) { Log($"widget catalogue: {m_WidgetCatalog.Widgets.Count} widget assets measured over {m_WidgetCatalog.Graphs} graphs"); FillTypeFilter(); FillExplorerList(); return; }
            if (!m_Rime.HasCache && !m_Rime.IsMounted) return;
            m_CatalogBuilding = true;
            Status("measuring the widgets of every screen…");
            Task.Run(() =>
            {
                try
                {
                    var s_Catalog = WidgetCatalog.Build(m_Rime);
                    if (m_Rime.CacheDir != null) s_Catalog.Save(m_Rime.CacheDir);
                    Dispatcher.Invoke(() => { m_WidgetCatalog = s_Catalog; m_CatalogBuilding = false; Log($"widget catalogue built: {s_Catalog.Widgets.Count} widget assets over {s_Catalog.Graphs} graphs, {s_Catalog.TotalWires} wires, clip variables of {s_Catalog.Widgets.Values.Count(w => w.ClipVars.Count > 0)} widgets"); FillTypeFilter(); FillExplorerList(); RefreshGraph(); ShowProperties(); WarnClipVars(); });
                }
                catch (Exception s_Ex) { Dispatcher.Invoke(() => { m_CatalogBuilding = false; Log("widget catalogue: " + s_Ex.Message); }); }
            });
        }

        // ------------------------------------------------------------------------------------------ rows

        static PropRow Category(string p_Name, IEnumerable<PropRow> p_Children) => new() { Path = "cat:" + p_Name, Name = p_Name, IsCategory = true, Children = p_Children.ToList() };

        static PropRow Info(string p_Path, string p_Name, string p_Text, string? p_Tip = null) => new() { Path = p_Path, Name = p_Name, Text = p_Text, ReadOnly = true, Tooltip = p_Tip };

        PropRow TextRow(string p_Path, string p_Name, string p_Value, Action<string> p_Set, UiTypeCatalog.Kind p_Kind = UiTypeCatalog.Kind.String, string? p_Tip = null) =>
            new() { Path = p_Path, Name = p_Name, Kind = p_Kind, Value = p_Value, Tooltip = p_Tip, OnChange = v => { PushUndo(); p_Set(v?.ToString() ?? ""); ShowProperties(); } };

        void ShowProperties()
        {
            var s_Rows = new List<PropRow>();
            var s_Footer = "";
            // the document's pictures: each a texture the build ships (UI/Art/<mod>/<name>); a row's - takes it out of the document
            var s_Images = new PropRow { Path = "doc.images", Name = "Images", Kind = UiTypeCatalog.Kind.List, Summary = $"({m_Doc.Images.Count} items)", Modified = m_Doc.Images.Count > 0, Tooltip = "The document's own pictures (Add image… on the Widgets tab). Each ships with the mod as a DXT5 texture of the game named UI/Art/<mod>/<name>: what an ImageManager's StaticUrl or a row's ItemImage can name." };
            for (var i = 0; i < m_Doc.Images.Count; ++i)
            {
                var s_Image = m_Doc.Images[i];
                var s_ImageFile = RimeLib.Cmd.UiBuilder.ImageTextures.ImagePath(s_Image, DocDir);
                s_Images.Children.Add(new PropRow { Path = "doc.images." + i, Name = s_Image.Name, ReadOnly = true, Text = m_Doc.ImageTextureName(s_Image) + " ← " + s_Image.File + (File.Exists(s_ImageFile) ? "" : " (file missing)"), Tooltip = s_ImageFile, Modified = true, OnRemove = () => RemoveImage(s_Image) });
            }
            s_Rows.Add(Category("Document", new[]
            {
                TextRow("doc.name", "Name", m_Doc.Name, v => m_Doc.Name = v.Trim(), p_Tip: "The mod's name (its folder under Mods)."),
                TextRow("doc.sb", "Superbundle", m_Doc.Superbundle, v => m_Doc.Superbundle = v.Trim(), p_Tip: "Superbundle path without Win32/. One name per mod, never shared."),
                TextRow("doc.bundle", "Bundle", m_Doc.Bundle, v => m_Doc.Bundle = v.Trim()),
                Info("doc.file", "File", m_DocPath == "" ? "(unsaved)" : m_DocPath),
                s_Images,
            }));
            if (m_Current == null) { PropertiesTitle.Text = ""; PropertiesGrid.SetRows(s_Rows, "no screen selected"); return; }

            var s_Entry = DocScreen(m_Current.Partition);
            var s_Screen = new List<PropRow>
            {
                Info("scr.part", "Partition", m_Current.Partition),
                Info("scr.type", "Asset type", m_Current.AssetType),
                Info("scr.counts", "Contents", $"{m_Current.Widgets.Count} widgets, {m_Current.OtherNodes.Count} logic nodes, {m_Current.Connections.Count} connections" + (m_Current.Wires.Any(w => w.Dangling) ? $" ({m_Current.Wires.Count(w => w.Dangling)} dangling in the shipped data)" : "")),
            };
            // the asset's own fields (Modal, ProtectScreens, the platform flags…) with the shipped values; an edit goes to the document's screen entry
            var s_AssetInfo = UiTypeCatalog.Describe(m_Current.AssetType);
            var s_AssetJson = m_Current.Instances[m_Current.AssetGuid] as JObject;
            if (s_AssetInfo != null && s_AssetJson != null)
            {
                foreach (var f in s_AssetInfo.Fields)
                {
                    if (f.Name is "Nodes" or "Connections" or "GlobalNode" or "Name" || f.Kind is UiTypeCatalog.Kind.Port or UiTypeCatalog.Kind.PortArray) continue;
                    var s_HasDoc = s_Entry != null && s_Entry.Fields.TryGetValue(f.Name, out var s_DocValue);
                    var s_Root = s_HasDoc ? s_Entry!.Fields[f.Name] : s_AssetJson[f.Name] != null ? ShippedToDoc(f, s_AssetJson[f.Name]!) : DefaultValue(f);
                    var s_FieldName = f.Name;
                    void Mutate(Action p_Change)
                    {
                        PushUndo();
                        var e = EnsureDocScreen(m_Current.Partition);
                        if (!e.Fields.ContainsKey(s_FieldName)) e.Fields[s_FieldName] = s_Root;
                        p_Change();
                        ShowProperties();
                    }
                    s_Screen.Add(ValueRow("scr.f." + f.Name, f.Name, f.Kind, f.TypeName, f.ElementKind, s_Root, s_HasDoc, v => Mutate(() => EnsureDocScreen(m_Current.Partition).Fields[s_FieldName] = v), Mutate,
                        f.Name switch { "Modal" => "A modal screen keeps the screens under it from receiving input.", "ProtectScreens" => "Keeps the screens under it alive (not destroyed) while this one is up.", _ => null }));
                }
            }
            if (m_Current.HasStage)
            {
                s_Screen.Add(Info("scr.movie", "Movie", m_Current.MovieName));
                var s_Frame = m_Current.Movie!.FrameSize;
                s_Screen.Add(Info("scr.size", "Size", $"{s_Frame.Width:0}/{s_Frame.Height:0}", "The movie's frame size (stage px); BF3 screens are authored at 1280/720."));
                var s_Ops = s_Entry?.Stage ?? new List<string>();
                var s_OpsRow = new PropRow { Path = "scr.ops", Name = "Stage ops", Kind = UiTypeCatalog.Kind.List, Summary = $"({s_Ops.Count} ops)", Tooltip = "gfx_stage_edit ops applied to the movie: move/scale/char/remove/import/add. A drag on the stage writes them.", Modified = s_Ops.Count > 0 };
                s_OpsRow.OnClear = () => { if (s_Entry == null) return; PushUndo(); s_Entry.Stage.Clear(); ApplyDocumentToCurrent(); };
                for (var i = 0; i < s_Ops.Count; ++i)
                {
                    var s_Index = i;
                    s_OpsRow.Children.Add(new PropRow
                    {
                        Path = "scr.ops." + i, Name = "[" + i + "]", Value = s_Ops[i], Modified = true,
                        OnChange = v => { PushUndo(); s_Entry!.Stage[s_Index] = v?.ToString() ?? ""; ApplyDocumentToCurrent(); },
                        OnRemove = () => { PushUndo(); s_Entry!.Stage.RemoveAt(s_Index); ApplyDocumentToCurrent(); },
                    });
                }
                s_Screen.Add(s_OpsRow);
            }
            // shipped wires the document disconnected (Alt+click on the graph): x puts one back
            if (s_Entry != null && s_Entry.RemovedConnections.Count > 0)
            {
                var s_RemovedRow = new PropRow { Path = "scr.removed", Name = "Removed connections", Kind = UiTypeCatalog.Kind.List, Summary = $"({s_Entry.RemovedConnections.Count} items)", Tooltip = "Shipped wires the mod erases from this screen at load (Alt+click a wire on the graph to disconnect it). x puts one back.", Modified = true };
                s_RemovedRow.OnClear = () => { PushUndo(); s_Entry.RemovedConnections.Clear(); Log("every disconnected shipped wire put back"); RefreshGraph(); ShowProperties(); };
                for (var i = 0; i < s_Entry.RemovedConnections.Count; ++i)
                {
                    var s_Index = i; var r = s_Entry.RemovedConnections[i];
                    s_RemovedRow.Children.Add(new PropRow
                    {
                        Path = "scr.removed." + i, Name = r.From, Text = "-> " + r.To, ReadOnly = true, Modified = true, Tooltip = "UINodeConnection " + r.Guid + " — erased by the mod at load",
                        OnRemove = () => { PushUndo(); s_Entry.RemovedConnections.RemoveAt(s_Index); Log($"reconnected {r.From} -> {r.To} (the shipped wire stays)"); RefreshGraph(); ShowProperties(); },
                    });
                }
                s_Screen.Add(s_RemovedRow);
            }
            s_Rows.Add(Category(m_Current.IsScreen ? "Screen" : "Flow graph", s_Screen));

            var s_Label = m_SelectedPlacement ?? m_SelectedNode;
            // the panel's title says what is selected (the screen, or the node / placement by name and type): the rows alone do not
            if (s_Label == null) { PropertiesTitle.Text = $" — {m_Current.Partition.Split('/').Last()} ({m_Current.AssetType})"; PropertiesGrid.SetRows(s_Rows, m_Current.AssetType + "  —  " + m_Current.Partition.Split('/').Last()); return; }
            var s_Placement = m_Current.Placements.FirstOrDefault(p => p.Name == s_Label);
            var s_Shipped = (m_SelectedKey != null ? m_Current.AllNodes.FirstOrDefault(n => n.Guid == m_SelectedKey) : null) ?? m_Current.AllNodes.FirstOrDefault(n => n.Label == s_Label);
            var s_Doc = s_Entry?.Nodes.FirstOrDefault(n => n.InstanceName == s_Label);
            var s_Type = s_Doc?.Type ?? s_Shipped?.Type ?? "WidgetNode";
            if (s_Placement != null) s_Rows.Add(Category("Placement", PlacementRows(s_Placement)));
            if (s_Placement != null) s_Rows.Add(Category("Clip variables (set at construct)", ClipVarRows(s_Placement)));
            s_Rows.AddRange(NodeRows(s_Label, s_Type, s_Shipped, s_Doc));
            s_Footer = s_Type + "  —  " + (s_Doc == null ? "shipped (edit any value to put it in the document)" : s_Doc.New ? "document: new node" : "document: edit of the shipped node");
            var s_Widget = s_Doc?.Widget ?? m_Current.Widgets.FirstOrDefault(w => w.InstanceName == s_Label)?.WidgetPartition ?? s_Placement?.ImportUrl;
            PropertiesTitle.Text = $" — {s_Label} ({s_Type}{(s_Type == "WidgetNode" && !string.IsNullOrEmpty(s_Widget) ? ", " + s_Widget.Split('/').Last().Replace(".swf", "") : "")})";
            PropertiesGrid.SetRows(s_Rows, s_Footer);
        }

        // ------------------------------------------------------------------------------------------ placement

        IEnumerable<PropRow> PlacementRows(PlacementInfo p)
        {
            var s_Name = p.Name;
            string F(double v) => StageCanvas.F(v);
            yield return new PropRow { Path = "pl.x", Name = "X (local)", Kind = UiTypeCatalog.Kind.Float, Value = System.Math.Round(p.LocalX, 2), Tooltip = "Position in the parent sprite's space (px). A move op.", Modified = HasOp("move", s_Name), OnChange = v => OnPlacementMoved(s_Name, (double)v!, p.LocalY) };
            yield return new PropRow { Path = "pl.y", Name = "Y (local)", Kind = UiTypeCatalog.Kind.Float, Value = System.Math.Round(p.LocalY, 2), Modified = HasOp("move", s_Name), OnChange = v => OnPlacementMoved(s_Name, p.LocalX, (double)v!) };
            yield return new PropRow { Path = "pl.sx", Name = "Scale X", Kind = UiTypeCatalog.Kind.Float, Value = System.Math.Round(p.SizeX, 4), Tooltip = "Scale along the placement's own x axis (a widget takes its area from it: Grid gridWidth = _width). Drag the handles on the stage (Shift = keep proportions) or type it. A scale op.", Modified = HasOp("scale", s_Name), OnChange = v => OnPlacementScaled(s_Name, (double)v!, p.SizeY) };
            yield return new PropRow { Path = "pl.sy", Name = "Scale Y", Kind = UiTypeCatalog.Kind.Float, Value = System.Math.Round(p.SizeY, 4), Modified = HasOp("scale", s_Name), OnChange = v => OnPlacementScaled(s_Name, p.SizeX, (double)v!) };
            yield return new PropRow { Path = "pl.rot", Name = "Rotation", Kind = UiTypeCatalog.Kind.Float, Value = System.Math.Round(p.Rotation, 2), Tooltip = "Degrees, positive = clockwise on screen, about the placement's origin (the orange dot). Drag the knob above the box (Shift snaps to 15°) or type it. A rotate op; the scale stays." + (System.Math.Abs(p.RotateSkew1 + p.RotateSkew0) > 1e-6 && System.Math.Abs(System.Math.Abs(p.RotateSkew1) - System.Math.Abs(p.RotateSkew0) * p.SizeY / System.Math.Max(1e-9, p.SizeX)) > 1e-3 ? $"  (the shipped matrix also skews: {p.RotateSkew0.ToString("0.####", CultureInfo.InvariantCulture)} / {p.RotateSkew1.ToString("0.####", CultureInfo.InvariantCulture)})" : ""), Modified = HasOp("rotate", s_Name), OnChange = v => OnPlacementRotated(s_Name, (double)v!) };
            yield return Info("pl.stage", "On the stage", $"({F(p.X)}, {F(p.Y)}) px" + (p.Bounds.Empty ? "  — bounds at runtime" : $"  bounds {p.Bounds}"));
            // the drawing order among the sprite's clips: a higher depth draws in front (the Layers list shows it topmost first); typing a
            // depth another clip holds swaps the two
            yield return new PropRow { Path = "pl.depth", Name = "Depth", Kind = UiTypeCatalog.Kind.Int, Value = (long)p.Depth, Tooltip = "Drawing order among the clips of its sprite: a higher depth draws in FRONT (Layers lists them topmost first). Type a depth another clip holds and the two swap; ▲ ▼ over the Layers list move one step. A depth op." + (p.Added ? "  (added by the document)" : ""), Modified = HasOp("depth", s_Name), OnChange = v => OnPlacementDepth(s_Name, (int)(long)v!) };
            yield return Info("pl.symbol", "Symbol", $"#{p.CharacterId}  {p.ImportUrl ?? "(local sprite)"}", "The character the placement instantiates: an imported widget symbol or a sprite of the screen's own movie.");
            yield return Info("pl.path", "Path", p.Path, "Names from the root sprite; the renderer's placement path.");
        }

        /// <summary>
        /// The construct-time variables of the placement (the per-instance widget parameters FrostEd bakes into the clip actions:
        /// TextField m_rowType, Button buttonType, KitSelector m_viewType…): each editable (a vars op on the clip), the shipped values of
        /// the widget's other placements offered, and the usual ones the clip lacks addable in one go. The widget's constructor reads
        /// them before onClipLoad — a placement without them runs blind (a TextField attaches no text row without m_rowType).
        /// </summary>
        IEnumerable<PropRow> ClipVarRows(PlacementInfo p)
        {
            var s_Name = p.Name;
            var s_Vars = p.ClipVars ?? new List<(string Name, object? Value)>();
            var s_Asset = p.ImportUrl != null ? "ui/assets/" + p.ImportUrl.Split('/').Last().ToLowerInvariant().Replace(".swf", "") : null;
            var s_Info = s_Asset != null && m_WidgetCatalog != null && m_WidgetCatalog.Widgets.TryGetValue(s_Asset, out var w) ? w : null;
            // a text widget's size: the game's text widgets draw at the size their row symbol carries (bold1 = 20 px, baseText0 = 32 px…) and
            // scaling the placement only grows the box; this row (a textsize op) gives the widget the size asked, through a frame action that
            // sets the field's text format after every text the engine hands it — the game's own vector font at that size
            if (s_Vars.Any(v => v.Name == "m_rowType") || (s_Asset != null && (s_Asset.EndsWith("/textfield") || s_Asset.EndsWith("/scrollingtextfield"))))
            {
                var s_SizeOp = DocScreen(m_Current!.Partition)?.Stage.FirstOrDefault(o => o.StartsWith("textsize:" + s_Name + ":"));
                var s_SizePx = s_SizeOp?[(s_SizeOp.LastIndexOf(':') + 1)..] ?? "";
                if (s_SizePx == "0") s_SizePx = "";
                var s_RowType = s_Vars.FirstOrDefault(v => v.Name == "m_rowType").Value as string;
                yield return new PropRow
                {
                    Path = "tx.size", Name = "Text size (px)", Kind = UiTypeCatalog.Kind.String, Value = s_SizePx, Modified = s_SizeOp != null,
                    Choices = new List<string> { "", "16", "20", "24", "28", "32", "40", "48", "64" },
                    Tooltip = "The size of the text in px; empty = the row type's own (" + (s_RowType ?? "m_rowType") + ": bold1/medium1 20 px, baseText0/1 32 px, multilineText0 24 px, multilineText1 18 px). The game's widget knows no other size and scaling the placement only grows the box, so the editor adds a frame action to the screen that sets the field's text format after every text it receives (the game's own font, vector) and lets the field grow to fit. A textsize op.",
                    OnChange = v => SetTextSize(s_Name, v?.ToString() ?? ""),
                };
            }
            foreach (var (s_Var, s_Value) in s_Vars)
            {
                if (s_Var == "_rueTextSize") continue;   // the textsize op's own variable: edited through the Text size row
                var s_Stats = s_Info != null && s_Info.ClipVars.TryGetValue(s_Var, out var st) ? st : null;
                var s_VarName = s_Var;
                yield return new PropRow
                {
                    Path = "cv." + s_Var, Name = s_Var, Kind = UiTypeCatalog.Kind.String, Value = RimeLib.Cmd.Scaleform.ClipActions.FormatValue(s_Value), Modified = HasOp("vars", s_Name),
                    Choices = s_Stats?.Values.Keys.ToList(),
                    Tooltip = "Set on the clip when it is constructed; the widget's code reads it before onClipLoad. true/false and numbers are typed, anything else is a string." +
                              (s_Stats != null ? $" Shipped placements of this widget: {string.Join(", ", s_Stats.Values.Select(kv => kv.Key + " ×" + kv.Value))}." : ""),
                    OnChange = v => SetClipVars(s_Name, new List<(string, object?)> { (s_VarName, RimeLib.Cmd.Scaleform.ClipActions.ParseValue(v?.ToString() ?? "")) }),
                };
            }
            var s_Usual = s_Info?.UsualClipVars() ?? new List<(string Name, object? Value)>();
            var s_Missing = s_Usual.Where(u => s_Vars.All(v => v.Name != u.Name)).ToList();
            if (s_Missing.Count > 0)
            {
                yield return new PropRow
                {
                    Path = "cv.add", Name = "Usual variables", Kind = UiTypeCatalog.Kind.List, Modified = true,
                    Summary = "missing: " + string.Join(", ", s_Missing.Select(m => m.Name + "=" + RimeLib.Cmd.Scaleform.ClipActions.FormatValue(m.Value))),
                    Tooltip = $"What the widget's {s_Info!.Placements} shipped placements set on themselves at construction; without them its code runs blind (a TextField attaches no text row without m_rowType). + adds them to this clip.",
                    OnAdd = () => SetClipVars(s_Name, s_Missing),
                };
            }
            else if (s_Vars.Count == 0)
                yield return Info("cv.none", "(none)", s_Info == null ? "no widget catalogue yet" : "this widget's shipped placements set none either");
        }

        /// <summary>A text widget's size in px (empty or 0 = the row type's own): the clip's textsize op is set or removed, one undo step, the movie rebuilt.</summary>
        void SetTextSize(string p_Name, string p_Px)
        {
            if (m_Current == null) return;
            var s_Text = p_Px.Trim();
            if (s_Text != "" && (!double.TryParse(s_Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var s_Px) || s_Px < 0 || s_Px > 500)) { Log($"text size: '{p_Px}' is not a size in px (0..500)"); return; }
            PushUndo();
            var s_Entry = EnsureDocScreen(m_Current.Partition);
            s_Entry.Stage.RemoveAll(o => o.StartsWith("textsize:" + p_Name + ":"));
            if (s_Text != "" && s_Text != "0") { var s_Op = $"textsize:{p_Name}:{s_Text}"; s_Entry.Stage.Add(s_Op); Log($"stage: {s_Op}"); }
            else Log($"stage: {p_Name} draws its text at the row type's own size again (textsize op removed)");
            ApplyDocumentToCurrent();
        }

        /// <summary>Sets construct-time variables on a placement: the clip's vars op is updated (or made), one undo step, the movie rebuilt.</summary>
        void SetClipVars(string p_Name, List<(string Name, object? Value)> p_Vars)
        {
            if (m_Current == null) return;
            PushUndo();
            var s_Entry = EnsureDocScreen(m_Current.Partition);
            var s_Index = s_Entry.Stage.FindIndex(o => o.StartsWith("vars:" + p_Name + ":"));
            var s_Merged = s_Index >= 0 ? RimeLib.Cmd.Scaleform.GfxMovie.ParseClipVars(s_Entry.Stage[s_Index][(s_Entry.Stage[s_Index].IndexOf(':', 5 + p_Name.Length) + 1)..]) : new List<(string, object?)>();
            foreach (var (s_Var, s_Value) in p_Vars)
            {
                var i = s_Merged.FindIndex(x => x.Item1 == s_Var);
                if (i >= 0) s_Merged[i] = (s_Var, s_Value); else s_Merged.Add((s_Var, s_Value));
            }
            var s_Op = $"vars:{p_Name}:{RimeLib.Cmd.Scaleform.GfxMovie.FormatClipVars(s_Merged)}";
            if (s_Index >= 0) s_Entry.Stage[s_Index] = s_Op; else s_Entry.Stage.Add(s_Op);
            Log($"stage: {s_Op}");
            ApplyDocumentToCurrent();
        }

        /// <summary>
        /// A depth typed in the grid (or asked by ▲ ▼): a depth op appended to the document — appended, never merged, because each one
        /// swaps with whoever holds the depth at that moment and the replay must swap the same clips. Higher draws in front.
        /// </summary>
        void OnPlacementDepth(string p_Name, int p_Depth)
        {
            if (m_Current == null) return;
            var s_Placement = m_Current.Placements.FirstOrDefault(p => p.Name == p_Name);
            if (s_Placement == null) { Log($"no placement {p_Name}"); return; }
            if (p_Depth < 1 || p_Depth > 65535) { Log("a depth is 1..65535"); return; }
            if (p_Depth == s_Placement.Depth) return;
            var s_Other = m_Current.Placements.FirstOrDefault(p => p.SpriteId == s_Placement.SpriteId && p.Depth == p_Depth);
            PushUndo();
            EnsureDocScreen(m_Current.Partition).Stage.Add($"depth:{p_Name}:{p_Depth}");
            Log($"stage: {p_Name} depth {s_Placement.Depth} → {p_Depth}" + (s_Other != null ? $", {s_Other.Name} takes {s_Placement.Depth}" : "") + " (higher draws in front)");
            ApplyDocumentToCurrent();
            m_SelectedPlacement = p_Name; m_SelectedNode = p_Name; m_Stage.Selected = p_Name; SyncLayerSelection();
            ShowProperties();
        }

        /// <summary>▲ / ▼ over the Layers list: the selected clip swaps depths with its next sibling above (+1) or below (−1) in its sprite.</summary>
        void MoveLayer(int p_Step)
        {
            if (m_Current == null || m_SelectedPlacement == null) { Log("select a clip in Layers or on the stage first"); return; }
            var p = m_Current.Placements.FirstOrDefault(x => x.Name == m_SelectedPlacement);
            if (p == null) return;
            var s_Siblings = m_Current.Placements.Where(x => x.SpriteId == p.SpriteId).OrderBy(x => x.Depth).ToList();
            var s_Next = p_Step > 0 ? s_Siblings.FirstOrDefault(x => x.Depth > p.Depth) : s_Siblings.LastOrDefault(x => x.Depth < p.Depth);
            if (s_Next == null) { Log($"{p.Name} is already at the {(p_Step > 0 ? "front" : "back")} of its sprite"); return; }
            OnPlacementDepth(p.Name, s_Next.Depth);
        }

        void OnLayerForward(object p_Sender, RoutedEventArgs e) => MoveLayer(+1);
        void OnLayerBackward(object p_Sender, RoutedEventArgs e) => MoveLayer(-1);

        bool HasOp(string p_Op, string p_Name) => DocScreen(m_Current!.Partition)?.Stage.Any(o => o.StartsWith(p_Op + ":" + p_Name + ":")) == true
                                                 || (p_Op == "move" && DocScreen(m_Current!.Partition)?.Stage.Any(o => o.StartsWith("add:") && o.Split(':')[2] == p_Name) == true);

        /// <summary>A scale typed in the grid becomes a scale op (or the add op's own scale for a placement the document added).</summary>
        void OnPlacementScaled(string p_Name, double p_Sx, double p_Sy)
        {
            if (m_Current == null) return;
            PushUndo();
            var s_Entry = EnsureDocScreen(m_Current.Partition);
            var s_AddIndex = s_Entry.Stage.FindIndex(o => o.StartsWith("add:") && o.Split(':')[2] == p_Name);
            if (s_AddIndex >= 0)
            {
                var f = s_Entry.Stage[s_AddIndex].Split(':');
                f[7] = StageCanvas.F(p_Sx); f[8] = StageCanvas.F(p_Sy);
                s_Entry.Stage[s_AddIndex] = string.Join(":", f);
            }
            else
            {
                s_Entry.Stage.RemoveAll(o => o.StartsWith("scale:" + p_Name + ":"));
                s_Entry.Stage.Add($"scale:{p_Name}:{StageCanvas.F(p_Sx)}:{StageCanvas.F(p_Sy)}");
            }
            Log($"stage: {p_Name} scale {StageCanvas.F(p_Sx)}/{StageCanvas.F(p_Sy)}");
            ApplyDocumentToCurrent();
        }

        // ------------------------------------------------------------------------------------------ node

        /// <summary>The document entry the next edit writes to: the existing one (an undo step first) or a fresh edit of the shipped node.</summary>
        NodeEntry DocNodeForEdit(string p_Label)
        {
            var s_Existing = DocScreen(m_Current!.Partition)?.Nodes.FirstOrDefault(n => n.InstanceName == p_Label);
            if (s_Existing != null) { PushUndo(); return s_Existing; }
            return EditShippedNode(p_Label);
        }

        IEnumerable<PropRow> NodeRows(string p_Label, string p_Type, NodeInfo? p_Shipped, NodeEntry? p_Doc)
        {
            var s_Info = UiTypeCatalog.Describe(p_Type);
            var s_IsWidget = p_Type == "WidgetNode";
            m_RowWidget = s_IsWidget ? p_Doc?.Widget ?? (p_Shipped != null ? m_Current!.Widgets.FirstOrDefault(w => w.Guid == p_Shipped.Guid)?.WidgetPartition : null) : null;
            if (s_Info == null)
            {
                yield return Category(p_Type, new[] { Info("node.unknown", "Type", p_Type + "  (unknown to the game's type information)") });
                yield break;
            }
            // one category per declaring type, base first (UINodeData, then WidgetNode / StateNode / DialogNode…)
            foreach (var s_Group in s_Info.Fields.GroupBy(f => f.DeclaringType))
            {
                var s_Rows = new List<PropRow>();
                foreach (var f in s_Group)
                {
                    if (f.Kind == UiTypeCatalog.Kind.PortArray || (f.Kind == UiTypeCatalog.Kind.Port && !UiTypeCatalog.IsPortReference(p_Type, f.Name))) continue;
                    var s_Row = FieldRow(p_Label, p_Type, f, p_Shipped, p_Doc);
                    // a field may bring a helper row along (the static text next to a text widget's binding)
                    if (m_PendingRows != null) { s_Rows.AddRange(m_PendingRows); m_PendingRows = null; }
                    if (s_Row != null) s_Rows.Add(s_Row);
                }
                if (s_Rows.Count > 0) yield return Category(s_Group.Key, s_Rows);
            }
            if (s_IsWidget)
            {
                var s_Asset = p_Doc?.Widget ?? (p_Shipped != null ? m_Current!.Widgets.FirstOrDefault(w => w.Guid == p_Shipped.Guid)?.WidgetPartition : null);
                // the game's picture widget: a picture of the user's own as its texture, chosen here (see MainWindow.Images)
                if (string.Equals(s_Asset, c_ImageWidget, StringComparison.OrdinalIgnoreCase))
                    yield return Category("Custom texture", CustomTextureRows(p_Label, p_Shipped, p_Doc));
                var s_Known = s_Asset != null ? m_WidgetCatalog?.Get(s_Asset) : null;
                var s_Code = s_Asset != null ? Settings()?.Get(s_Asset) : null;
                var s_Set = CurrentProperties(p_Shipped, p_Doc).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                // the settings the widget's own code reads at initialise (name, default, how it parses it), with what the shipped screens set;
                // the ones already on the node are in WidgetProperties above — the rest can be added with + (default, or the most common value)
                var s_Settings = new List<PropRow>();
                void AddSetting(string p_Name, string p_Value) { var e = DocNodeForEdit(p_Label); if (e.Properties.Count == 0 && p_Shipped != null) foreach (var (k, v) in CurrentProperties(p_Shipped, null)) e.Properties[k] = v; e.Properties[p_Name] = p_Value; ShowProperties(); }
                if (s_Code != null)
                    foreach (var (s_Name, s_Setting) in s_Code.Settings)
                    {
                        if (s_Set.Contains(s_Name)) continue;
                        var s_Stats = s_Known != null && s_Known.Properties.TryGetValue(s_Name, out var st) ? st : null;
                        var s_Common = s_Stats?.Values.OrderByDescending(x => x.Value).FirstOrDefault().Key;
                        var s_Default = s_Setting.Default?.Trim('"') ?? (s_Setting.Kind == "bool" ? "false" : s_Setting.Kind is "number" or "flag" ? "0" : "");
                        var s_Text = $"{s_Setting.Kind}" + (s_Setting.Default != null ? $", default {s_Setting.Default}" : "") + (s_Stats != null ? $"  — set on {s_Stats.Uses} of {s_Known!.Uses} nodes: {string.Join(", ", s_Stats.Values.Take(6).Select(x => $"{(x.Key == "" ? "\"\"" : x.Key)} ×{x.Value}"))}" : "  — no screen sets it");
                        var s_Value = s_Common ?? s_Default;
                        s_Settings.Add(new PropRow { Path = "known." + s_Name, Name = s_Name, ReadOnly = true, Text = s_Text, Tooltip = $"read by {s_Setting.From} from the node's WidgetProperties (initData.{s_Name}); + adds it with {(s_Common != null ? "the most common value" : "its default")}", OnAdd = () => AddSetting(s_Name, s_Value) });
                    }
                if (s_Known != null)
                    foreach (var kv in s_Known.Properties.Where(kv => !s_Set.Contains(kv.Key) && (s_Code == null || !s_Code.Settings.ContainsKey(kv.Key))).OrderByDescending(kv => kv.Value.Uses))
                    {
                        var s_Name = kv.Key; var s_Common = kv.Value.Values.OrderByDescending(x => x.Value).FirstOrDefault().Key ?? "";
                        s_Settings.Add(new PropRow { Path = "known." + s_Name, Name = s_Name, ReadOnly = true, Text = $"set on {kv.Value.Uses} of {s_Known.Uses} nodes: {string.Join(", ", kv.Value.Values.Take(6).Select(x => $"{(x.Key == "" ? "\"\"" : x.Key)} ×{x.Value}"))}" + (s_Code != null ? "  — not read by the widget's code" : ""), OnAdd = () => AddSetting(s_Name, s_Common) });
                    }
                if (s_Settings.Count > 0 || s_Code != null)
                    yield return Category($"Widget settings ({(s_Code != null ? s_Code.Class : s_Asset?.Split('/').Last())}; + adds one)", s_Settings.Count > 0 ? s_Settings : new[] { Info("known.none", "(all set)", "every setting the widget reads is on the node") });
                if (s_Code != null && s_Code.DataNames.Count > 0)
                    yield return Category("Data channels (update<Name>Data)", s_Code.DataNames.Select(n => Info("chan." + n, n, "a binding's DataName the widget's code accepts", "The widget's update" + n + "Data(data) is called with the bound data.")));
                if (s_Known != null)
                    yield return Category("Widget events (contract)", s_Known.Events.Select(e =>
                    {
                        var s_HasPort = p_Shipped?.Ports.Any(p => p.Query == e.Query && p.IsInput != e.IsOutput) == true || p_Doc?.Ports.Any(p => p.Event == e.Query) == true || p_Doc?.Connections.Any(c => c.Event == e.Query) == true;
                        return Info("ev." + e.Name + e.Query, (e.IsOutput ? "▶ " : "◀ ") + e.Query, e.Name + (s_HasPort ? "  — port on this node" : ""), e.IsOutput ? "fired by the widget (wire it from the graph)" : "received by the widget");
                    }));
                else if (s_Asset != null && (m_WidgetCatalog == null || m_CatalogBuilding))
                    yield return Category("Widget", new[] { Info("known.wait", "Catalogue", "measuring the widgets of every screen… (a moment)") });
            }
            if (p_Shipped != null)
            {
                yield return Category("Ports", p_Shipped.Ports.Select(p => Info("port." + p.Guid, (p.IsInput ? "◀ " : "▶ ") + p.Name, p.Field + (p.Query != "" ? "  event " + p.Query : "") + (p.InputEvent != "" ? "  action " + p.InputEvent : "") + (p.Dangling ? "  — DANGLING (named by a connection, in no slot)" : ""))));
                yield return Category("Connections", WiringOf(p_Shipped).Select((w, i) => Info("wire." + i, w.Contains("->") ? w.Split(" -> ")[0] : w, w.Contains("->") ? "-> " + w.Split(" -> ")[1] : "")));
            }
            if (p_Doc != null)
            {
                var s_DocRows = new List<PropRow>();
                var s_PortsRow = new PropRow { Path = "doc.ports", Name = "Named ports", Kind = UiTypeCatalog.Kind.List, Summary = $"({p_Doc.Ports.Count} items)", Tooltip = "Array ports to create: Field (Outputs/Inputs), name, optional widget event or controller action.", Modified = p_Doc.Ports.Count > 0 };
                s_PortsRow.OnAdd = () => { PushUndo(); p_Doc.Ports.Add(new PortEntry { Field = "Outputs", Name = "NewPort" }); ShowProperties(); };
                s_PortsRow.OnClear = () => { PushUndo(); p_Doc.Ports.Clear(); RefreshGraph(); ShowProperties(); };
                for (var i = 0; i < p_Doc.Ports.Count; ++i)
                {
                    var s_Port = p_Doc.Ports[i]; var s_Index = i;
                    var s_Row = new PropRow { Path = "doc.ports." + i, Name = "[" + i + "]", Kind = UiTypeCatalog.Kind.Struct, Summary = $"{s_Port.Field}:{s_Port.Name}" + (s_Port.Event != null ? "@" + s_Port.Event : "") + (s_Port.InputEvent != null ? "^" + s_Port.InputEvent : ""), Modified = true, OnRemove = () => { PushUndo(); p_Doc.Ports.RemoveAt(s_Index); RefreshGraph(); ShowProperties(); } };
                    s_Row.Children.Add(new PropRow { Path = s_Row.Path + ".field", Name = "Field", Kind = UiTypeCatalog.Kind.Enum, Choices = new[] { "Outputs", "Inputs" }, Value = s_Port.Field, OnChange = v => { PushUndo(); s_Port.Field = v?.ToString() ?? "Outputs"; RefreshGraph(); ShowProperties(); } });
                    s_Row.Children.Add(TextRow(s_Row.Path + ".name", "Name", s_Port.Name, v => { s_Port.Name = v; RefreshGraph(); }));
                    s_Row.Children.Add(new PropRow { Path = s_Row.Path + ".event", Name = "Event", Kind = UiTypeCatalog.Kind.Enum, Choices = new[] { "" }.Concat(UiTypeCatalog.EnumMembers("UIWidgetEventID").Select(e => e.Replace("UIWidgetEventID_", ""))).ToList(), Value = s_Port.Event ?? "", OnChange = v => { PushUndo(); s_Port.Event = string.IsNullOrEmpty(v?.ToString()) ? null : v!.ToString(); ShowProperties(); } });
                    s_Row.Children.Add(new PropRow { Path = s_Row.Path + ".action", Name = "Input action", Kind = UiTypeCatalog.Kind.Enum, Choices = new[] { "" }.Concat(UiTypeCatalog.EnumMembers("UIInputAction").Select(e => e.Replace("UIInputAction_", ""))).ToList(), Value = s_Port.InputEvent ?? "", Tooltip = "A UIInputAction makes the port a UIInputEventNodePort (a StateNode output reacting to Back/Deactivate/Menu…).", OnChange = v => { PushUndo(); s_Port.InputEvent = string.IsNullOrEmpty(v?.ToString()) ? null : v!.ToString(); ShowProperties(); } });
                    s_PortsRow.Children.Add(s_Row);
                }
                s_DocRows.Add(s_PortsRow);
                // every wire this node may fire, by the rules the shipped connections follow (the game's most-wired pairs first): the
                // rows offer them as choices, + adds the first one
                var s_Offers = CompatibleTargets(p_Label);
                var s_ConnRow = new PropRow { Path = "doc.conn", Name = "Connections", Kind = UiTypeCatalog.Kind.List, Summary = $"({p_Doc.Connections.Count} items; {s_Offers.Count} compatible targets)", Tooltip = "Wires this node fires: drag them on the graph, pick one of the compatible targets from a row's list, or type 'port -> Node.port' (widget events by name, array ports as Outputs:Name). + adds the most-wired compatible one.", Modified = p_Doc.Connections.Count > 0 };
                s_ConnRow.OnClear = () => { PushUndo(); p_Doc.Connections.Clear(); RefreshGraph(); ShowProperties(); };
                ConnectionEntry ParseConnection(string p_Text)
                {
                    var s = p_Text.Split("->", 2);
                    var t = s.Length == 2 ? s[1].Trim() : ""; var s_Dot = t.IndexOf('.');
                    return MakeConnection(p_Doc, s[0].Trim(), s_Dot < 0 ? t : t[..s_Dot].Trim(), s_Dot < 0 ? "In" : t[(s_Dot + 1)..].Trim());
                }
                if (s_Offers.Count > 0)
                    s_ConnRow.OnAdd = () => { PushUndo(); p_Doc.Connections.Add(ParseConnection(s_Offers[0])); Log($"{p_Label}: connection added: {s_Offers[0]} (pick another from the row's list)"); RefreshGraph(); ShowProperties(); };
                for (var i = 0; i < p_Doc.Connections.Count; ++i)
                {
                    var c = p_Doc.Connections[i]; var s_Index = i;
                    var s_Text = $"{c.Event ?? c.FromPort ?? "Out"} -> {c.ToNode}.{c.ToEvent ?? c.ToPort ?? c.ToField ?? "In"}";
                    s_ConnRow.Children.Add(new PropRow
                    {
                        Path = "doc.conn." + i, Name = "[" + i + "]", Value = s_Text, Modified = true, Choices = s_Offers.Count > 0 ? s_Offers : null,
                        Tooltip = s_Offers.Count > 0 ? $"{s_Offers.Count} compatible targets in the list (the game's most-wired pairs first)" : null,
                        OnChange = v => { var s_New = v?.ToString() ?? ""; if (!s_New.Contains("->")) return; PushUndo(); p_Doc.Connections[s_Index] = ParseConnection(s_New); RefreshGraph(); ShowProperties(); },
                        OnRemove = () => { PushUndo(); p_Doc.Connections.RemoveAt(s_Index); RefreshGraph(); ShowProperties(); },
                    });
                }
                s_DocRows.Add(s_ConnRow);
                if (s_IsWidget)
                {
                    var s_Replace = new PropRow { Path = "doc.replace", Name = "Replace shipped properties", Kind = UiTypeCatalog.Kind.Bool, Value = p_Doc.ReplaceProperties, Tooltip = "true: the node ends up with exactly the properties listed; false: listed ones are rewritten/added, the rest stay.", OnChange = v => { PushUndo(); p_Doc.ReplaceProperties = v?.Type == JTokenType.Boolean && (bool)v; ShowProperties(); } };
                    s_DocRows.Add(s_Replace);
                }
                s_DocRows.Add(new PropRow { Path = "doc.remove", Name = "Remove from document", ReadOnly = true, Text = p_Doc.New ? "drops the new node" : "forgets every edit of the shipped node", OnRemove = () => { PushUndo(); DocScreen(m_Current!.Partition)!.Nodes.Remove(p_Doc); RefreshGraph(); ShowProperties(); } });
                yield return Category("Document", s_DocRows);
            }
        }

        /// <summary>The widget properties as the panel shows them: the document's when it has any, else the shipped ones.</summary>
        static IEnumerable<KeyValuePair<string, string>> CurrentProperties(NodeInfo? p_Shipped, NodeEntry? p_Doc)
        {
            if (p_Doc != null && (p_Doc.Properties.Count > 0 || p_Doc.New)) return p_Doc.Properties;
            if (p_Shipped != null)
                return (p_Shipped.Json["WidgetProperties"] as JArray ?? new JArray()).OfType<JObject>().Select(p => new KeyValuePair<string, string>((string?)p["Name"] ?? "", (string?)p["Value"] ?? ""));
            return Enumerable.Empty<KeyValuePair<string, string>>();
        }

        /// <summary>One field of the node, with the document's value when it overrides the shipped one; edits go to the document.</summary>
        PropRow? FieldRow(string p_Label, string p_Type, UiTypeCatalog.FieldInfo f, NodeInfo? p_Shipped, NodeEntry? p_Doc)
        {
            var s_IsWidget = p_Type == "WidgetNode";
            var s_Path = "f." + f.Name;
            // widget shorthand fields live on the entry itself
            if (s_IsWidget)
            {
                switch (f.Name)
                {
                    case "InstanceName":
                        return Info(s_Path, "InstanceName", p_Label, "The stage clip's name; the node's identity on this screen.");
                    case "WidgetAsset":
                    {
                        var s_Native = p_Shipped != null ? m_Current!.Widgets.FirstOrDefault(w => w.Guid == p_Shipped.Guid)?.WidgetPartition : null;
                        var s_Value = p_Doc?.Widget ?? s_Native ?? "";
                        return new PropRow { Path = s_Path, Name = f.Name, Kind = UiTypeCatalog.Kind.Ref, TypeName = "UIWidgetAsset", Value = s_Value, Modified = p_Doc?.Widget != null && p_Doc.Widget != s_Native, Choices = m_AllWidgets, Tooltip = "The UIWidgetAsset partition (ui/assets/…) whose movie the stage clip instantiates.", OnChange = v => { var e = DocNodeForEdit(p_Label); e.Widget = string.IsNullOrEmpty(v?.ToString()) ? null : v!.ToString(); ShowProperties(); } };
                    }
                    case "FocusIndex":
                    {
                        var s_Native = (int?)p_Shipped?.Json["FocusIndex"];
                        return new PropRow { Path = s_Path, Name = f.Name, Kind = UiTypeCatalog.Kind.Int, Value = p_Doc?.FocusIndex ?? s_Native ?? -1, Modified = p_Doc != null && p_Doc.FocusIndex != (s_Native ?? -1), OnChange = v => { var e = DocNodeForEdit(p_Label); e.FocusIndex = (int)(long)v!; ShowProperties(); } };
                    }
                    case "ZDepthLevel":
                    {
                        var s_Native = (int?)p_Shipped?.Json["ZDepthLevel"];
                        return new PropRow { Path = s_Path, Name = f.Name, Kind = UiTypeCatalog.Kind.Int, Value = p_Doc?.ZDepthLevel ?? s_Native ?? 0, Modified = p_Doc != null && p_Doc.ZDepthLevel != (s_Native ?? 0), OnChange = v => { var e = DocNodeForEdit(p_Label); e.ZDepthLevel = (int)(long)v!; ShowProperties(); } };
                    }
                    case "WidgetProperties":
                        return PropertiesRow(p_Label, p_Shipped, p_Doc);
                    case "DataBinding":
                        return BindingRow(p_Label, f, p_Shipped, p_Doc);
                }
            }
            if (s_IsWidget && f.Name == "AlwaysInFocus")
            {
                // right after the binding: the plain way to put a text on a text widget (DICE does it in the binding, never in WidgetProperties)
                var s_Text = StaticTextRow(p_Label, p_Shipped, p_Doc);
                if (s_Text != null) { m_PendingRows ??= new List<PropRow>(); m_PendingRows.Add(s_Text); }
            }
            if (f.Name is "ParentGraph")
                return Info(s_Path, f.Name, m_Current!.Partition, "The graph the node belongs to (set by the engine when the node is added).");
            if (f.Name is "IsRootNode" or "ParentIsScreen")
                return Info(s_Path, f.Name, p_Shipped != null ? Compact(p_Shipped.Json[f.Name] ?? "") : (f.Name == "ParentIsScreen" ? (m_Current!.IsScreen ? "true" : "false") : "false"), "Set by the builder when the node is created.");
            if (f.Name is "Name" && p_Shipped != null && p_Doc == null)
                return Info(s_Path, "Name", (string?)p_Shipped.Json["Name"] ?? "", "The node's label.");

            // generic typed field: document value > shipped value (converted to the document's spelling) > default
            var s_HasDoc = p_Doc != null && p_Doc.Fields.TryGetValue(f.Name, out var s_DocValue);
            JToken s_Root;
            if (s_HasDoc) s_Root = p_Doc!.Fields[f.Name];
            else if (p_Shipped != null && p_Shipped.Json[f.Name] != null && !UiTypeCatalog.IsPortReference(p_Type, f.Name)) s_Root = ShippedToDoc(f, p_Shipped.Json[f.Name]!);
            else if (UiTypeCatalog.IsPortReference(p_Type, f.Name)) { var r = p_Shipped?.PortRefs.FirstOrDefault(x => x.Field == f.Name); s_Root = r?.Node != null ? r.Value.Node + "." + r.Value.Port : ""; }
            else if (f.Name == "Name" && p_Doc != null) s_Root = p_Doc.InstanceName;
            else s_Root = DefaultValue(f);
            var s_Modified = s_HasDoc;
            // a leaf edit puts the whole value into the document (created from the shipped one on the first edit) and rebuilds the panel
            void Mutate(Action p_Change)
            {
                var e = DocNodeForEdit(p_Label);
                if (!e.Fields.ContainsKey(f.Name)) e.Fields[f.Name] = s_Root;
                p_Change();
                ShowProperties();
            }
            if (UiTypeCatalog.IsPortReference(p_Type, f.Name))
                return new PropRow { Path = s_Path, Name = f.Name, Kind = UiTypeCatalog.Kind.String, Value = s_Root, Modified = s_Modified, Tooltip = "A reference to another node's port: Node.port (Confirm.In, Pick.Outputs:0).", OnChange = v => Mutate(() => DocNodeForEdit(p_Label).Fields[f.Name] = v?.ToString() ?? "") };
            return ValueRow(s_Path, f.Name, f.Kind, f.TypeName, f.ElementKind, s_Root, s_Modified, p_NewRoot => Mutate(() => DocNodeForEdit(p_Label).Fields[f.Name] = p_NewRoot), Mutate, f.Name == "Name" ? "The node's label (its identity on the graph)." : null);
        }

        /// <summary>A value of any kind as a row (with children for structs, lists and inline instances). p_Replace swaps the whole value; p_Mutate wraps an in-place change.</summary>
        PropRow ValueRow(string p_Path, string p_Name, UiTypeCatalog.Kind p_Kind, string p_TypeName, UiTypeCatalog.Kind p_ElementKind, JToken p_Value, bool p_Modified, Action<JToken> p_Replace, Action<Action> p_Mutate, string? p_Tip = null)
        {
            var s_Row = new PropRow { Path = p_Path, Name = p_Name, Kind = p_Kind, TypeName = p_TypeName, Modified = p_Modified, Tooltip = p_Tip };
            switch (p_Kind)
            {
                case UiTypeCatalog.Kind.Bool:
                case UiTypeCatalog.Kind.Int:
                case UiTypeCatalog.Kind.Float:
                case UiTypeCatalog.Kind.String:
                    s_Row.Value = p_Value; s_Row.OnChange = v => p_Replace(v ?? "");
                    break;
                case UiTypeCatalog.Kind.Enum:
                    s_Row.Value = p_Value; s_Row.Choices = UiTypeCatalog.EnumMembers(p_TypeName); s_Row.OnChange = v => p_Replace(v ?? "");
                    break;
                case UiTypeCatalog.Kind.Ref:
                    if (p_Value is JObject s_Inst && s_Inst["$type"] != null) return InstanceRow(p_Path, p_Name, p_TypeName, s_Inst, p_Modified, p_Replace, p_Mutate, p_Tip);
                    s_Row.Value = p_Value; s_Row.Choices = RefCandidates(p_TypeName); s_Row.OnChange = v => p_Replace(v ?? "");
                    s_Row.Tooltip = p_Tip ?? $"A {p_TypeName} partition by name.";
                    break;
                case UiTypeCatalog.Kind.Struct:
                {
                    var o = p_Value as JObject ?? new JObject();
                    var s_Struct = UiTypeCatalog.Describe(p_TypeName);
                    var s_Parts = new List<string>();
                    foreach (var sf in s_Struct?.Fields ?? new List<UiTypeCatalog.FieldInfo>())
                    {
                        if (sf.Kind is UiTypeCatalog.Kind.Port or UiTypeCatalog.Kind.PortArray) continue;
                        if (o[sf.Name] == null) o[sf.Name] = DefaultValue(sf);
                        var s_Child = o[sf.Name]!;
                        s_Parts.Add(Compact(s_Child));
                        var s_Name = sf.Name;
                        var s_ChildRow = ValueRow(p_Path + "." + sf.Name, sf.Name, sf.Kind, sf.TypeName, sf.ElementKind, s_Child, p_Modified, v => p_Mutate(() => o[s_Name] = v), p_Mutate);
                        if (p_TypeName == "UIDataSourceInfo") DataSourceRow(s_ChildRow, o, sf, p_Mutate);
                        s_Row.Children.Add(s_ChildRow);
                    }
                    if (p_TypeName == "UIDataSourceInfo")
                    {
                        // the summary names the source, not the number
                        var s_Comp = m_WidgetCatalog?.Component((string?)o["DataCategory"]);
                        var s_Key = (int?)o["DataKey"] ?? 0;
                        var s_Source = s_Comp?.SourceOf(s_Key);
                        s_Row.Summary = $"{(string?)o["DataName"]}  ←  {(s_Comp != null ? DataKeys.ComponentShortName(s_Comp.Name) : "?")}.{s_Source ?? (s_Key == 0 ? "(none)" : "(unknown key " + s_Key + ")")}";
                    }
                    else
                        s_Row.Summary = string.Join("/", s_Parts.Select(x => x.Length > 24 ? x[..24] + "…" : x));
                    break;
                }
                case UiTypeCatalog.Kind.List:
                {
                    var a = p_Value as JArray ?? new JArray();
                    s_Row.Summary = $"({a.Count} items)";
                    var s_Element = new UiTypeCatalog.FieldInfo { Name = p_Name, Kind = p_ElementKind, TypeName = p_TypeName };
                    s_Row.OnAdd = () => p_Mutate(() => a.Add(p_ElementKind == UiTypeCatalog.Kind.Ref && UiTypeCatalog.Describe(p_TypeName)?.IsBinding == true ? DefaultInstance(p_TypeName) : DefaultValue(s_Element)));
                    s_Row.OnClear = () => p_Mutate(() => a.Clear());
                    for (var i = 0; i < a.Count; ++i)
                    {
                        var s_Index = i;
                        var s_Child = ValueRow(p_Path + "[" + i + "]", "[" + i + "]", p_ElementKind, p_TypeName, UiTypeCatalog.Kind.Unknown, a[i], p_Modified, v => p_Mutate(() => a[s_Index] = v), p_Mutate);
                        s_Child.OnRemove = () => p_Mutate(() => a.RemoveAt(s_Index));
                        s_Row.Children.Add(s_Child);
                    }
                    break;
                }
                default:
                    s_Row.Text = Compact(p_Value); s_Row.ReadOnly = true;
                    break;
            }
            return s_Row;
        }

        /// <summary>
        /// A UIDataSourceInfo's rows the way the game's editor shows them: the DataCategory picks among the data
        /// components, the DataKey is chosen as one of that component's sources by NAME (the number is the fb hash of
        /// "UI_&lt;component&gt;_&lt;source&gt;" upper-cased, computed here), and the DataName offers the channels the
        /// widget's code accepts (its update&lt;Name&gt;Data methods).
        /// </summary>
        void DataSourceRow(PropRow p_Row, JObject p_Source, UiTypeCatalog.FieldInfo p_Field, Action<Action> p_Mutate)
        {
            switch (p_Field.Name)
            {
                case "DataCategory":
                    p_Row.Choices = m_WidgetCatalog != null && m_WidgetCatalog.Components.Count > 0 ? m_WidgetCatalog.Components.Keys.OrderBy(k => k).ToList() : RefCandidates("UIComponentData");
                    p_Row.Tooltip = "The data component (ui/uicomponents/…) that serves the source.";
                    break;
                case "DataKey":
                {
                    var s_Comp = m_WidgetCatalog?.Component((string?)p_Source["DataCategory"]);
                    var s_Key = (int?)p_Source["DataKey"] ?? 0;
                    if (s_Comp == null)
                    {
                        p_Row.Tooltip = "fb hash of UI_<COMPONENT>_<SOURCE> (upper-cased). Pick a DataCategory the catalogue knows to choose the source by name.";
                        break;
                    }
                    var s_Source = s_Comp.SourceOf(s_Key);
                    var s_Choices = new List<string> { "(none)" };
                    s_Choices.AddRange(s_Comp.Sources);
                    var s_Unknown = s_Key != 0 && s_Source == null;
                    if (s_Unknown) s_Choices.Insert(1, $"(unknown key {s_Key})");
                    p_Row.Kind = UiTypeCatalog.Kind.Enum;
                    p_Row.Choices = s_Choices;
                    p_Row.Value = s_Source ?? (s_Key == 0 ? "(none)" : $"(unknown key {s_Key})");
                    p_Row.Name = "DataKey (source)";
                    p_Row.Tooltip = s_Unknown
                        ? $"The key {s_Key} names no source of {DataKeys.ComponentShortName(s_Comp.Name)} — the game ships bindings like this; they receive nothing. Pick a source to fix it."
                        : $"The source of {DataKeys.ComponentShortName(s_Comp.Name)} this binding reads; the number the game stores is the fb hash of UI_{DataKeys.ComponentShortName(s_Comp.Name).ToUpperInvariant()}_<SOURCE> ({(s_Source != null ? s_Key.ToString(CultureInfo.InvariantCulture) : "0")}).";
                    p_Row.OnChange = v =>
                    {
                        var s_Pick = v?.ToString() ?? "(none)";
                        var s_NewKey = s_Pick == "(none)" ? 0 : s_Comp.Keys.TryGetValue(s_Pick, out var k) ? k : s_Key;
                        p_Mutate(() => p_Source["DataKey"] = s_NewKey);
                    };
                    break;
                }
                case "DataName":
                {
                    var s_Code = m_RowWidget != null ? Settings()?.Get(m_RowWidget) : null;
                    if (s_Code != null && s_Code.DataNames.Count > 0) p_Row.Choices = s_Code.DataNames;
                    p_Row.Tooltip = "The widget-side channel: its update<DataName>Data method receives the source's value." + (s_Code != null && s_Code.DataNames.Count > 0 ? $" {s_Code.Class} accepts: {string.Join(", ", s_Code.DataNames)}." : "");
                    break;
                }
            }
        }

        /// <summary>An instance described inline (a data binding): its type (switchable) and every field of that type.</summary>
        PropRow InstanceRow(string p_Path, string p_Name, string p_BaseType, JObject p_Inst, bool p_Modified, Action<JToken> p_Replace, Action<Action> p_Mutate, string? p_Tip)
        {
            var s_Type = (string?)p_Inst["$type"] ?? p_BaseType;
            var s_Row = new PropRow { Path = p_Path, Name = p_Name, Kind = UiTypeCatalog.Kind.Ref, TypeName = s_Type, Modified = p_Modified, Tooltip = p_Tip ?? "An instance the node owns (edited in place in the game). Switching the type replaces it." };
            s_Row.Choices = DerivedTypes(p_BaseType);
            s_Row.OnChange = v => { var t = v?.ToString() ?? s_Type; if (t != s_Type) p_Replace(DefaultInstance(t)); };
            var s_Info = UiTypeCatalog.Describe(s_Type);
            foreach (var sf in s_Info?.Fields ?? new List<UiTypeCatalog.FieldInfo>())
            {
                if (sf.Kind is UiTypeCatalog.Kind.Port or UiTypeCatalog.Kind.PortArray) continue;
                if (p_Inst[sf.Name] == null) p_Inst[sf.Name] = DefaultValue(sf);
                var s_Name = sf.Name;
                s_Row.Children.Add(ValueRow(p_Path + "." + sf.Name, sf.Name, sf.Kind, sf.TypeName, sf.ElementKind, p_Inst[sf.Name]!, p_Modified, v => p_Mutate(() => p_Inst[s_Name] = v), p_Mutate));
            }
            s_Row.Summary = s_Type;
            return s_Row;
        }

        static readonly Dictionary<string, List<string>> s_DerivedCache = new();

        /// <summary>The concrete types a reference of a base type may hold (UIDataBinding → the 12 binding types), the base itself first when concrete.</summary>
        static List<string> DerivedTypes(string p_BaseType)
        {
            if (s_DerivedCache.TryGetValue(p_BaseType, out var s_Cached)) return s_Cached;
            var s_List = new List<string>();
            if (UiTypeCatalog.Describe(p_BaseType) is { } s_Base && s_Base.IsBinding && p_BaseType == "UIDataBinding") s_List.AddRange(UiTypeCatalog.BindingTypes());
            else
            {
                var s_All = UiTypeCatalog.NodeTypes().Concat(UiTypeCatalog.BindingTypes()).ToList();
                s_List.Add(p_BaseType);
                foreach (var t in s_All) { var i = UiTypeCatalog.Describe(t); for (var b = i; b != null && b.Name != ""; b = b.BaseName == "" ? null : UiTypeCatalog.Describe(b.BaseName)) if (b.Name == p_BaseType && t != p_BaseType) { s_List.Add(t); break; } }
            }
            s_DerivedCache[p_BaseType] = s_List.Distinct().ToList();
            return s_DerivedCache[p_BaseType];
        }

        /// <summary>A fresh instance of a type with every field at its default, as the document spells it.</summary>
        static JObject DefaultInstance(string p_Type)
        {
            var o = new JObject { ["$type"] = p_Type };
            foreach (var sf in UiTypeCatalog.Describe(p_Type)?.Fields ?? new List<UiTypeCatalog.FieldInfo>())
                if (sf.Kind is not (UiTypeCatalog.Kind.Port or UiTypeCatalog.Kind.PortArray)) o[sf.Name] = DefaultValue(sf);
            return o;
        }

        /// <summary>Partition names a reference of that type can point at (from the cache/mount): widgets, components, screens…</summary>
        List<string> RefCandidates(string p_TypeName)
        {
            var s_Prefix = p_TypeName switch
            {
                "UIWidgetAsset" => "ui/assets/",
                "UIComponentData" => "ui/uicomponents/",
                "UIScreenAsset" => "ui/flow/screen/",
                "UIGraphAsset" => "ui/flow/",
                "UIStateAsset" => "ui/flow/state/",
                "UIActionData" => "ui/flow/action/",
                _ => "ui/",
            };
            try
            {
                var s_List = m_Rime.Partitions(s_Prefix).ToList();
                // the screens the document makes are UIScreenAssets a StateNode / DialogNode may show
                if (p_TypeName is "UIScreenAsset" or "UIGraphAsset") s_List.AddRange(m_Doc.Screens.Where(s => s.New && !s_List.Contains(s.Partition)).Select(s => s.Partition));
                return s_List;
            }
            catch { return new List<string>(); }
        }

        /// <summary>The widget's properties as a list of Name/Value pairs (the values the shipped screen sets; the document's once edited).</summary>
        PropRow PropertiesRow(string p_Label, NodeInfo? p_Shipped, NodeEntry? p_Doc)
        {
            var s_Props = CurrentProperties(p_Shipped, p_Doc).ToList();
            var s_Modified = p_Doc != null && p_Doc.Properties.Count > 0;
            var s_Row = new PropRow { Path = "f.WidgetProperties", Name = "WidgetProperties", Kind = UiTypeCatalog.Kind.List, Summary = $"({s_Props.Count} items)", Modified = s_Modified, Tooltip = "The p_* / setup properties the widget's ActionScript reads at initialise. The names the game uses for this widget are listed below." };
            // an edit rewrites the document's list from the pairs shown, so the shipped ones travel along on the first edit
            void Commit(List<KeyValuePair<string, string>> p_Pairs)
            {
                var e = DocNodeForEdit(p_Label);
                e.Properties = new Dictionary<string, string>();
                foreach (var kv in p_Pairs) e.Properties[kv.Key] = kv.Value;
                ShowProperties();
            }
            s_Row.OnAdd = () => { var l = s_Props.ToList(); l.Add(new KeyValuePair<string, string>("NewProperty" + (l.Count + 1), "")); Commit(l); };
            s_Row.OnClear = () => Commit(new List<KeyValuePair<string, string>>());
            for (var i = 0; i < s_Props.Count; ++i)
            {
                var s_Index = i; var kv = s_Props[i];
                var s_Elem = new PropRow { Path = "f.WidgetProperties[" + i + "]", Name = "[" + i + "]", Kind = UiTypeCatalog.Kind.Struct, TypeName = "UIWidgetProperty", Summary = $"{kv.Key} = {kv.Value}", Modified = s_Modified, OnRemove = () => { var l = s_Props.ToList(); l.RemoveAt(s_Index); Commit(l); } };
                // the names the widget's code reads and the values the shipped screens use, as suggestions
                var s_Code = m_RowWidget != null ? Settings()?.Get(m_RowWidget) : null;
                var s_Known = m_RowWidget != null ? m_WidgetCatalog?.Get(m_RowWidget) : null;
                var s_Names = (s_Code?.Settings.Keys ?? Enumerable.Empty<string>()).Concat(s_Known?.Properties.Keys ?? Enumerable.Empty<string>()).Distinct().OrderBy(n => n).ToList();
                var s_Values = s_Known != null && s_Known.Properties.TryGetValue(kv.Key, out var st) ? st.Values.OrderByDescending(x => x.Value).Select(x => x.Key).ToList() : new List<string>();
                var s_Setting = s_Code != null && s_Code.Settings.TryGetValue(kv.Key, out var sd) ? sd : null;
                if (s_Setting != null && s_Setting.Kind == "bool" && !s_Values.Contains("true")) { s_Values.Add("true"); s_Values.Add("false"); }
                s_Elem.Children.Add(new PropRow { Path = s_Elem.Path + ".Name", Name = "Name", Value = kv.Key, Modified = s_Modified, Choices = s_Names.Count > 0 ? s_Names : null, Tooltip = s_Setting != null ? $"read by {s_Setting.From} as {s_Setting.Kind}" + (s_Setting.Default != null ? $", default {s_Setting.Default}" : "") : s_Code != null ? "not read by the widget's code" : null, OnChange = v => { var l = s_Props.ToList(); l[s_Index] = new KeyValuePair<string, string>(v?.ToString() ?? "", kv.Value); Commit(l); } });
                s_Elem.Children.Add(new PropRow { Path = s_Elem.Path + ".Value", Name = "Value", Value = kv.Value, Modified = s_Modified, Choices = s_Values.Count > 0 ? s_Values : null, Tooltip = s_Values.Count > 0 ? "values the shipped screens use: " + string.Join(", ", s_Values.Take(8).Select(x => x == "" ? "\"\"" : x)) : null, OnChange = v => { var l = s_Props.ToList(); l[s_Index] = new KeyValuePair<string, string>(kv.Key, v?.ToString() ?? ""); Commit(l); } });
                s_Row.Children.Add(s_Elem);
            }
            return s_Row;
        }

        List<PropRow>? m_PendingRows;

        /// <summary>
        /// "Static text" for a widget whose code takes a Text channel (TextField, buttons, headers…): the text DICE puts
        /// in a UITextDataBinding's StaticText — a literal ("READY") or an ID_ string the game localises. Typing it
        /// creates that binding on the document node (TextData / Visibility sources empty, Refresh, OverrideDirectAccess,
        /// as every shipped one) or changes the StaticText of the one already there. WidgetProperties never carry text.
        /// </summary>
        PropRow? StaticTextRow(string p_Label, NodeInfo? p_Shipped, NodeEntry? p_Doc)
        {
            var s_Code = m_RowWidget != null ? Settings()?.Get(m_RowWidget) : null;
            if (s_Code == null || !s_Code.DataNames.Contains("Text")) return null;
            JObject? s_Inline = p_Doc != null && p_Doc.Fields.TryGetValue("DataBinding", out var t) && t is JObject o && (string?)o["$type"] == "UITextDataBinding" ? o : null;
            var s_ShippedText = p_Shipped?.Json["DataBinding"] is JObject r && (string?)r["InstanceGuid"] is { } g && m_Current!.Instances[g] is JObject b && (string?)b["$type"] == "UITextDataBinding" ? (string?)b["StaticText"] ?? "" : "";
            var s_Value = s_Inline != null ? (string?)s_Inline["StaticText"] ?? "" : s_ShippedText;
            return new PropRow
            {
                Path = "f.StaticText", Name = "Static text", Kind = UiTypeCatalog.Kind.String, Value = s_Value, Modified = s_Inline != null,
                Tooltip = "The text the widget shows: a literal (\"I hate burgers\") or an ID_ string the game localises. It travels in a UITextDataBinding's StaticText (the way every shipped text widget carries it) — a TextData WidgetProperty does nothing, the widget's code never reads one.",
                OnChange = v =>
                {
                    var s_Text = v?.ToString() ?? "";
                    var e = DocNodeForEdit(p_Label);
                    if (e.Fields.TryGetValue("DataBinding", out var s_Cur) && s_Cur is JObject s_Obj && (string?)s_Obj["$type"] == "UITextDataBinding") s_Obj["StaticText"] = s_Text;
                    else
                    {
                        JObject Source() => new() { ["DataName"] = "", ["DataCategory"] = "", ["DataKey"] = 0, ["UseDirectAccess"] = false, ["UpdateOnInitialize"] = true };
                        e.Fields["DataBinding"] = new JObject { ["$type"] = "UITextDataBinding", ["StaticText"] = s_Text, ["TextData"] = Source(), ["Visibility"] = Source(), ["Refresh"] = true, ["OverrideDirectAccess"] = true };
                        e.Binding = null;
                    }
                    Log($"{p_Label}: static text \"{s_Text}\" (UITextDataBinding.StaticText)");
                    ApplyDocumentToCurrent();
                },
            };
        }

        /// <summary>The widget's data binding as a whole instance (any of the 12 binding types), shipped values first; edited as Fields["DataBinding"].</summary>
        PropRow BindingRow(string p_Label, UiTypeCatalog.FieldInfo f, NodeInfo? p_Shipped, NodeEntry? p_Doc)
        {
            JToken s_Root; var s_Modified = false;
            if (p_Doc != null && p_Doc.Fields.TryGetValue("DataBinding", out var s_DocBinding)) { s_Root = s_DocBinding; s_Modified = true; }
            else if (p_Doc?.Binding != null)
            {
                // the shorthand binding of older documents, shown as the instance it makes
                var b = p_Doc.Binding;
                var s_Category = b.Category ?? (b.CategoryFromNode != null ? m_Current!.Widgets.FirstOrDefault(w => w.InstanceName == b.CategoryFromNode)?.Bindings.FirstOrDefault().Category : null) ?? "";
                s_Root = new JObject { ["$type"] = "UIDynamicDataBinding", ["Refresh"] = true, ["Bindings"] = new JArray(new JObject { ["DataName"] = b.DataName, ["DataCategory"] = s_Category, ["DataKey"] = b.DataKey, ["UseDirectAccess"] = b.UseDirectAccess, ["UpdateOnInitialize"] = b.UpdateOnInitialize }) };
                s_Modified = true;
            }
            else if (p_Shipped != null && p_Shipped.Json["DataBinding"] is JObject s_Ref && ShippedToDoc(f, s_Ref) is JObject s_Conv && s_Conv["$type"] != null) s_Root = s_Conv;
            else s_Root = "";
            if (s_Root is not JObject s_Inst)
            {
                // no binding yet: pick a type to create one
                return new PropRow { Path = "f.DataBinding", Name = "DataBinding", Kind = UiTypeCatalog.Kind.Ref, TypeName = "(none)", Summary = "(none)", Choices = new[] { "(none)" }.Concat(UiTypeCatalog.BindingTypes()).ToList(), Tooltip = "The instance that feeds the widget: pick a binding type to create one.",
                    OnChange = v => { var t = v?.ToString(); if (string.IsNullOrEmpty(t) || t == "(none)") return; var e = DocNodeForEdit(p_Label); e.Fields["DataBinding"] = DefaultInstance(t); e.Binding = null; ShowProperties(); } };
            }
            void Mutate(Action p_Change)
            {
                var e = DocNodeForEdit(p_Label);
                if (!e.Fields.ContainsKey("DataBinding")) { e.Fields["DataBinding"] = s_Inst; e.Binding = null; }
                p_Change();
                // the binding's static texts (StaticText, StaticHeader…) show on the stage: redraw it, not just the rows —
                // a StaticText typed here used to change the document and leave the stage as it was
                ApplyDocumentToCurrent();
            }
            var s_Row = InstanceRow("f.DataBinding", "DataBinding", "UIDataBinding", s_Inst, s_Modified, v => Mutate(() => DocNodeForEdit(p_Label).Fields["DataBinding"] = v), Mutate, "The instance that feeds the widget (UIDynamicDataBinding: component data keys; UIText/UIPageHeader/UIList…: static texts and items). Edited in place in the game; switching the type makes a new one.");
            return s_Row;
        }

        /// <summary>A shipped value in the document's spelling; an in-partition instance (a binding) becomes an inline object with $type.</summary>
        JToken ShippedToDoc(UiTypeCatalog.FieldInfo f, JToken v)
        {
            switch (f.Kind)
            {
                case UiTypeCatalog.Kind.Ref:
                {
                    if (v is not JObject r) return "";
                    var s_Part = (string?)r["PartitionGuid"]; var s_Guid = (string?)r["InstanceGuid"];
                    if (s_Guid != null && (s_Part == null || string.Equals(s_Part, m_Current?.PartitionGuid, StringComparison.OrdinalIgnoreCase)) && m_Current?.Instances[s_Guid] is JObject s_Inline && (string?)s_Inline["$type"] is { } s_Type)
                    {
                        var o = new JObject { ["$type"] = s_Type };
                        var s_Info = UiTypeCatalog.Describe(s_Type);
                        foreach (var p in s_Inline.Properties().Where(p => p.Name != "$type"))
                        {
                            var sf = s_Info?.Field(p.Name);
                            if (sf != null && sf.Kind is UiTypeCatalog.Kind.Port or UiTypeCatalog.Kind.PortArray) continue;
                            o[p.Name] = sf != null ? ShippedToDoc(sf, p.Value) : p.Value;
                        }
                        return o;
                    }
                    return s_Part != null ? (m_Rime.PartitionNameByGuid(s_Part) ?? "") : "";
                }
                case UiTypeCatalog.Kind.Struct:
                {
                    var o = new JObject(); var s = UiTypeCatalog.Describe(f.TypeName);
                    if (v is JObject src) foreach (var p in src.Properties()) { var sf = s?.Field(p.Name); o[p.Name] = sf != null ? ShippedToDoc(sf, p.Value) : p.Value; }
                    return o;
                }
                case UiTypeCatalog.Kind.List:
                {
                    var a = new JArray(); var e = new UiTypeCatalog.FieldInfo { Name = f.Name, Kind = f.ElementKind, TypeName = f.TypeName };
                    if (v is JArray src) foreach (var item in src) a.Add(ShippedToDoc(e, item));
                    return a;
                }
                default: return v;
            }
        }
    }
}
