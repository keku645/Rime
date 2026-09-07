using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using RimeShaderEditor.Emit;
using RimeShaderEditor.Graph;
using RimeShaderEditor.View;

namespace RimeShaderEditor;

public partial class MainWindow : Window
{
    private string? m_GraphPath;
    private readonly Dictionary<string, ImageSource> m_TexturePreviews = new();
    private readonly EditorSettings m_Settings = EditorSettings.Load();

    public MainWindow()
    {
        InitializeComponent();

        GamePathBox.Text = m_Settings.GamePath;
        OutputFolderBox.Text = m_Settings.OutputFolder;

        // Settings that live outside this window's own controls, applied before anything reads them (the
        // shortcut help line below prints the LIVE table, so a remapped key shows its real letter).
        View.GridCanvas.ZoomSpeed = m_Settings.CanvasZoomSpeed;
        GraphCanvas.ApplyShortcuts(m_Settings.NodeShortcuts);

        GamePathBox.LostKeyboardFocus += (_, _) => PersistSettings();
        OutputFolderBox.LostKeyboardFocus += (_, _) => PersistSettings();

        // The graph name reaches disk (the emitted .hlsl/.dxbc are named from it) and is the default mod name at
        // bake time, so it is edited here rather than being stuck at "Untitled" with every graph overwriting
        // the last one's artifacts.
        GraphNameBox.TextChanged += (_, _) =>
        {
            Canvas.Graph.Name = GraphNameBox.Text.Trim();
            RefreshTabBar();
        };
        Closing += (_, _) => PersistSettings();

        // Created-variation NAMES used to persist across restarts while their canvas drafts do not — a
        // stale name selected in a fresh session bakes whatever happens to be on screen under it. The
        // session therefore starts CLEAN: every variation list is wiped, and the dropdown repopulates only
        // from what a loaded master file actually embeds (the user's decision, on the user's request).
        try
        {
            if (Directory.Exists(TexCacheDir))
            {
                var s_Stale = Directory.GetFiles(TexCacheDir, "*.variations.json");
                foreach (var s_File in s_Stale)
                    File.Delete(s_File);

                if (s_Stale.Length > 0)
                    Log($"Cleared {s_Stale.Length} persisted variation list(s) — load a saved graph to " +
                        "bring its variations back.");
            }
        }
        catch
        {
            // A locked cache file only costs a stale dropdown entry; it must never stop the window.
        }

        // The instance library registers its palette entries BEFORE the tree is built, so saved fragments
        // are placeable from the first click of the session.
        var s_Instances = Graph.InstanceLibrary.Refresh(Log);
        BuildPalette();
        BuildLevelList();
        if (s_Instances > 0)
            Log($"{s_Instances} instance graph(s) loaded from {Graph.InstanceLibrary.Directory}.");

        Canvas.TexturePreviewProvider = p_Register =>
            m_TexturePreviews.TryGetValue(p_Register, out var s_Image) ? s_Image : null;

        Canvas.SelectionChanged += (_, _) => BuildProperties();
        Canvas.GraphChanged += (_, _) => SchedulePreview();
        Canvas.SearchRequested += OnSearchRequested;

        SetUpPreview();
        Closing += (_, _) => m_Preview.Dispose();

        // Same rule at startup as after a settings change: the fold is a UDK-vocabulary action, and the
        // canvas has to know the vocabulary too or its shortcuts speak the other one.
        View.GraphCanvas.UdkStyle = m_Settings.UdkNodeStyle;
        CollapseButton.Visibility = m_Settings.UdkNodeStyle ? Visibility.Visible : Visibility.Collapsed;

        // The session opens on a canvas holding nothing but its OUTPUT node — the one node every shader ends
        // at, in whichever vocabulary is being authored in. (It used to open completely empty; that turned out
        // to mean starting every graph by hunting for the root.)
        m_Tabs.Add(new EditorTab { Graph = NewGraphWithRoot() });
        ActivateTab(0);

        Log("Canvas: right-drag pans, wheel zooms, drag a node in from the list on the left.");
        Log("Preview: LMB orbit, MMB pan, RMB/wheel zoom, hold L + LMB to turn the light, F to frame.");
        Log("Drag output square -> input square to wire. ALT + left-click on a wire breaks it.");
        Log("Ctrl+Z undo / Ctrl+Y redo. Right-CLICK the canvas for the node search (right-drag still pans).");
        Log("Node shortcuts (hold the key and left-click): " + string.Join(", ", GraphCanvas.Shortcuts
            .Where(p_S => p_S.Key is >= Key.A and <= Key.Z)
            .OrderBy(p_S => p_S.Key.ToString())
            .Select(p_S => $"{p_S.Key}={Palette.PlacedTitle(p_S.Kind, m_Settings.UdkNodeStyle)}")));

        if (GamePathBox.Text.Length > 0)
            Log($"BF3 found at {GamePathBox.Text}");
        else
            Log("BF3 install not detected — type its folder in the 'BF3 path' box to enable texture previews.");

        // Previews are cached on disk, so only the very first load per shader has to mount the game.
        if (LoadCachedPreviews() == 0)
            Log("Texture thumbnails are empty: press 'Load target textures' to read them from the game.");
    }

    private readonly ShaderPreview m_Preview = new();
    private System.Windows.Forms.Panel? m_PreviewPanel;
    private System.Windows.Threading.DispatcherTimer? m_PreviewDebounce;
    private DateTime m_PreviewClock = DateTime.UtcNow;
    private System.Drawing.Point m_PreviewDragOrigin;
    private bool m_PreviewInitialised;

    private void SetUpPreview()
    {
        m_PreviewPanel = new System.Windows.Forms.Panel { BackColor = System.Drawing.Color.Black };
        PreviewHost.Child = m_PreviewPanel;

        m_PreviewPanel.MouseDown += (_, s_Args) =>
        {
            m_PreviewDragOrigin = s_Args.Location;
            m_PreviewPanel.Focus();
        };

        // Unreal's asset-preview mapping: LMB orbits, MMB pans, RMB dollies, wheel zooms, and holding L turns
        // the preview light instead of the camera. F frames the object.
        m_PreviewPanel.MouseMove += (_, s_Args) =>
        {
            var s_Sensitivity = 0.01f * (float) m_Settings.PreviewOrbitSpeed;
            var s_Dx = (s_Args.X - m_PreviewDragOrigin.X) * s_Sensitivity;
            var s_Dy = (s_Args.Y - m_PreviewDragOrigin.Y) * s_Sensitivity;

            var s_Button = s_Args.Button;
            if (s_Button == System.Windows.Forms.MouseButtons.None)
                return;

            var s_LightKey = Keyboard.IsKeyDown(Key.L);

            if (s_Button == System.Windows.Forms.MouseButtons.Left && s_LightKey)
            {
                m_Preview.LightYaw += s_Dx;
                m_Preview.LightPitch = Math.Clamp(m_Preview.LightPitch - s_Dy, -1.45f, 1.45f);
            }
            else if (s_Button == System.Windows.Forms.MouseButtons.Left)
            {
                // Vertical is +dy: dragging down lifts the camera so the top face comes into view.
                m_Preview.Yaw += s_Dx;
                m_Preview.Pitch = Math.Clamp(m_Preview.Pitch + s_Dy, -1.45f, 1.45f);
            }
            else if (s_Button == System.Windows.Forms.MouseButtons.Middle)
            {
                m_Preview.Pan(s_Dx * 0.35f, s_Dy * 0.35f);
            }
            else if (s_Button == System.Windows.Forms.MouseButtons.Right)
            {
                // Vertical drag dollies, matching orbit-mode RMB. Multiplicative, so each step moves a
                // fraction of the remaining distance: the zoom feels unlimited going in and never crosses zero.
                m_Preview.Distance = Math.Clamp(
                    m_Preview.Distance * (float) Math.Exp(s_Dy * 0.75f), 0.05f, 100f);
            }

            m_PreviewDragOrigin = s_Args.Location;
        };

        m_PreviewPanel.MouseWheel += (_, s_Args) =>
            m_Preview.Distance = Math.Clamp(
                m_Preview.Distance * (float) Math.Exp(-s_Args.Delta * 0.0011f *
                    (float) m_Settings.PreviewZoomSpeed), 0.05f, 100f);

        m_PreviewPanel.PreviewKeyDown += (_, s_Args) =>
        {
            if (s_Args.KeyCode == System.Windows.Forms.Keys.F)
                m_Preview.ResetView();
        };

        m_PreviewPanel.Resize += (_, _) =>
        {
            if (m_PreviewInitialised)
                m_Preview.Resize(m_PreviewPanel.Width, m_PreviewPanel.Height);
        };

        // The panel has no handle until it is shown, so initialise on the first WPF frame.
        System.Windows.Media.CompositionTarget.Rendering += OnPreviewFrame;
    }

    private void OnPreviewFrame(object? p_Sender, EventArgs p_Args)
    {
        if (m_PreviewPanel == null || m_PreviewPanel.Width <= 0 || m_PreviewPanel.Height <= 0)
            return;

        if (!m_PreviewInitialised)
        {
            m_PreviewInitialised = true;
            if (!m_Preview.Initialise(m_PreviewPanel.Handle, m_PreviewPanel.Width, m_PreviewPanel.Height))
            {
                PreviewStatus.Text = $"Preview unavailable: {m_Preview.LastError}";
                System.Windows.Media.CompositionTarget.Rendering -= OnPreviewFrame;
                return;
            }

            // Textures decoded before the device existed were dropped on the floor, so re-push them now that
            // there is somewhere to put them.
            PushPreviewTextures();
            SchedulePreview();
        }

        if (!m_Preview.Ready)
            return;

        m_Preview.Time = (float) (DateTime.UtcNow - m_PreviewClock).TotalSeconds;
        m_Preview.Render();
    }

    /// <summary>
    /// Recompiles the graph into the preview, debounced so typing in a field does not trigger a compile per
    /// keystroke.
    /// </summary>
    private void SchedulePreview()
    {
        if (!m_Preview.Ready)
            return;

        m_PreviewDebounce ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };

        m_PreviewDebounce.Tick -= OnPreviewDebounce;
        m_PreviewDebounce.Tick += OnPreviewDebounce;
        m_PreviewDebounce.Stop();
        m_PreviewDebounce.Start();
    }

    private void OnPreviewDebounce(object? p_Sender, EventArgs p_Args)
    {
        m_PreviewDebounce?.Stop();
        RebuildPreviewShader();
    }

    private void RebuildPreviewShader()
    {
        if (!m_Preview.Ready)
            return;

        // The detected contract shapes PsIn/PsOut; null falls back to the rigid-mesh layout.
        var s_Result = new HlslEmitter { Contract = m_Contract }.Emit(Canvas.Graph);
        if (!s_Result.Ok)
        {
            PreviewStatus.Text = "Preview paused: " + s_Result.Errors[0];
            return;
        }

        try
        {
            // Compiled in-process rather than by launching fxc, so the loop stays interactive.
            using var s_Compiled = SharpDX.D3DCompiler.ShaderBytecode.Compile(
                s_Result.Hlsl, "main", "ps_5_0", SharpDX.D3DCompiler.ShaderFlags.OptimizationLevel1);

            if (s_Compiled.Bytecode == null)
            {
                PreviewStatus.Text = "Preview paused: shader did not compile.";
                return;
            }

            if (m_Preview.SetAuthoredShader(s_Compiled.Bytecode.Data))
                PreviewStatus.Text = "LMB orbit · MMB pan · RMB or wheel zoom · hold L + LMB turns the light · F frames";
            else
                PreviewStatus.Text = $"Preview paused: {m_Preview.LastError}";
        }
        catch (Exception s_Exception)
        {
            // A compile error is normal while a graph is half-wired; keep the last good shader on screen.
            PreviewStatus.Text = "Preview paused: " + s_Exception.Message.Split('\n')[0].Trim();
        }
    }

    /// <summary>Pushes the decoded BF3 textures into the preview so the cube shows real art.</summary>
    private void PushPreviewTextures()
    {
        if (!m_Preview.Ready)
            return;

        foreach (var (s_Register, s_Image) in m_TexturePreviews)
        {
            if (s_Image is not BitmapSource s_Bitmap)
                continue;

            var s_Converted = new FormatConvertedBitmap(s_Bitmap, PixelFormats.Bgra32, null, 0);
            var s_Pixels = new uint[s_Converted.PixelWidth * s_Converted.PixelHeight];
            s_Converted.CopyPixels(s_Pixels, s_Converted.PixelWidth * 4, 0);

            if (int.TryParse(s_Register, out var s_Slot))
                m_Preview.SetTexture(s_Slot, s_Pixels, s_Converted.PixelWidth, s_Converted.PixelHeight);
        }
    }

    private Point m_SearchWorldPoint;

    private sealed class SearchEntry
    {
        public required string Kind { get; init; }
        public required string Label { get; init; }
        public override string ToString() => Label;
    }

    private void OnSearchRequested(object? p_Sender, Point p_World)
    {
        m_SearchWorldPoint = p_World;

        var s_Screen = Mouse.GetPosition(Canvas);
        var s_Offset = Canvas.TranslatePoint(s_Screen, this);

        SearchPopup.PlacementTarget = this;
        SearchPopup.HorizontalOffset = s_Offset.X;
        SearchPopup.VerticalOffset = s_Offset.Y;

        SearchBox.Text = "";
        FillSearchResults("");
        SearchPopup.IsOpen = true;
        SearchBox.Focus();
    }

    private void FillSearchResults(string p_Filter)
    {
        var s_Needle = p_Filter.Trim().ToLowerInvariant();
        SearchResults.Items.Clear();

        var s_Matches = Palette.VisibleFor(m_Settings.UdkNodeStyle)
            .Where(p_D => s_Needle.Length == 0 ||
                          p_D.Title.ToLowerInvariant().Contains(s_Needle) ||
                          p_D.Kind.ToLowerInvariant().Contains(s_Needle) ||
                          p_D.CategoryFor(m_Settings.UdkNodeStyle).ToLowerInvariant().Contains(s_Needle) ||
                          p_D.Aliases.Any(p_A => p_A.ToLowerInvariant().Contains(s_Needle)))
            // Names that START with what was typed are what you usually meant — measured against the name
            // actually shown, or the ranking answers for a title the user cannot see. The foreign group sinks
            // to the bottom for the same reason it sits last in the palette.
            .OrderByDescending(p_D => s_Needle.Length > 0 &&
                                      p_D.TitleFor(m_Settings.UdkNodeStyle).ToLowerInvariant().StartsWith(s_Needle))
            .ThenBy(p_D => p_D.CategoryFor(m_Settings.UdkNodeStyle) == Palette.c_ForeignCategory ? 1 : 0)
            .ThenBy(p_D => p_D.CategoryFor(m_Settings.UdkNodeStyle))
            .ThenBy(p_D => p_D.TitleFor(m_Settings.UdkNodeStyle));

        foreach (var s_Def in s_Matches)
            SearchResults.Items.Add(new SearchEntry
            {
                Kind = s_Def.Kind,
                Label = $"{s_Def.TitleFor(m_Settings.UdkNodeStyle)}   ({s_Def.CategoryFor(m_Settings.UdkNodeStyle)})",
            });

        // Not a palette node: drops a comment box at the clicked point. Empty Kind is the sentinel.
        if (s_Needle.Length == 0 || "comment".Contains(s_Needle) || "note".Contains(s_Needle))
            SearchResults.Items.Add(new SearchEntry
            {
                Kind = "",
                Label = "Comment   (Canvas)",
            });

        if (SearchResults.Items.Count > 0)
            SearchResults.SelectedIndex = 0;
    }

    private void OnSearchTextChanged(object p_Sender, TextChangedEventArgs p_Args) =>
        FillSearchResults(SearchBox.Text);

    private void OnSearchKeyDown(object p_Sender, KeyEventArgs p_Args)
    {
        switch (p_Args.Key)
        {
            case Key.Escape:
                SearchPopup.IsOpen = false;
                p_Args.Handled = true;
                break;

            case Key.Enter:
                PlaceSearchSelection();
                p_Args.Handled = true;
                break;

            // Arrows move through the list while the caret stays in the box.
            case Key.Down:
                if (SearchResults.Items.Count > 0)
                    SearchResults.SelectedIndex =
                        Math.Min(SearchResults.SelectedIndex + 1, SearchResults.Items.Count - 1);

                p_Args.Handled = true;
                break;

            case Key.Up:
                if (SearchResults.Items.Count > 0)
                    SearchResults.SelectedIndex = Math.Max(SearchResults.SelectedIndex - 1, 0);

                p_Args.Handled = true;
                break;
        }
    }

    private void OnSearchPick(object p_Sender, MouseButtonEventArgs p_Args) => PlaceSearchSelection();

    private void PlaceSearchSelection()
    {
        if (SearchResults.SelectedItem is not SearchEntry s_Entry)
            return;

        SearchPopup.IsOpen = false;

        if (s_Entry.Kind.Length == 0)
        {
            // The comment entry: the box opens its own title editor, so no focus steal afterwards.
            Canvas.AddCommentAt(m_SearchWorldPoint);
            Log("Added comment.");
            return;
        }

        Canvas.AddNodeAt(s_Entry.Kind, m_SearchWorldPoint);
        Canvas.Focus();
        Log($"Added {Palette.Get(s_Entry.Kind).TitleFor(m_Settings.UdkNodeStyle)}.");
    }

    /// <summary>
    /// The shape icons behave as a radio group by hand: WPF's RadioButton can't wear the toggle-chrome
    /// template without re-templating twice, and the group is four buttons.
    /// </summary>
    private void OnShapeIcon(object p_Sender, RoutedEventArgs p_Args)
    {
        if (p_Sender is not System.Windows.Controls.Primitives.ToggleButton s_Button)
            return;

        m_MeshMode = false;
        m_Preview.Shape = (s_Button.Tag as string) switch
        {
            "Sphere" => View.PreviewShape.Sphere,
            "Cylinder" => View.PreviewShape.Cylinder,
            "Plane" => View.PreviewShape.Plane,
            _ => View.PreviewShape.Cube,
        };

        SyncShapeIcons(m_Preview.Shape);
    }

    /// <summary>The mesh the user picked from the linked-objects list, for the mesh-render preview phase.</summary>
    private string? m_PreviewMesh;

    /// <summary>
    /// Lists the DISTINCT meshes that wear the current target shader, from the same cached slot map that
    /// feeds the Variation picker — "which objects is this shader on" is data the editor already has.
    /// </summary>
    private void OnMeshIcon(object p_Sender, RoutedEventArgs p_Args)
    {
        var s_Target = TargetShaderBox.Text.Trim();
        var s_Meshes = s_Target.Length > 0
            ? ReadSlotMapFull(SlotMapPath(s_Target))?.Variations?.Values
                .Select(p_V => p_V.Mesh)
                .Where(p_M => !string.IsNullOrWhiteSpace(p_M))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p_M => p_M, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : null;

        // ⛔ THE LIST USED TO BE A POPUP THAT OPENED ON THIS ICON, and a popup is the wrong home for it:
        // it hides the choice the moment you look away, so the object being previewed is invisible until you
        // click again — and there was nowhere to say what the list even means. It lives in the properties
        // column now, always on screen; this icon only turns the mesh view on.
        MeshListNote.Visibility = s_Meshes is { Count: > 0 } ? Visibility.Collapsed : Visibility.Visible;

        m_MeshListFilling = true;
        MeshList.Items.Clear();
        foreach (var s_Mesh in s_Meshes ?? new List<string?>())
            MeshList.Items.Add(s_Mesh);

        MeshList.SelectedItem = m_PreviewMesh != null && s_Meshes != null && s_Meshes.Contains(m_PreviewMesh)
            ? m_PreviewMesh
            : null;
        m_MeshListFilling = false;

        if (s_Meshes == null || s_Meshes.Count == 0)
            Log("No linked objects known for this shader yet — press 'Load target textures' to build its map.");

        // ⛔ COMING BACK TO THE MESH IS NOT LOADING IT AGAIN. The geometry stays in the preview while the
        // shape is a cube, so switching back is a shape change and nothing else — no dump, no re-pick, no
        // mount. Re-dumping here would charge a game mount for pressing an icon twice.
        m_MeshMode = true;
        if (m_Preview.HasMesh)
            m_Preview.Shape = View.PreviewShape.Mesh;

        SyncShapeIcons(m_Preview.Shape);
    }

    /// <summary>Filling the list raises SelectionChanged; without this it would re-dump the mesh on every fill.</summary>
    private bool m_MeshListFilling;

    /// <summary>
    /// A mesh read from a file instead of from the game, for LOOKING at the shader on it. It is deliberately
    /// not an import: nothing here reaches a bake, a bundle or the game's own data.
    /// </summary>
    private void OnPickCustomMesh(object p_Sender, RoutedEventArgs p_Args)
    {
        var s_Dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Mesh to preview",
            Filter = "Meshes (*.rsm;*.obj)|*.rsm;*.obj|Rime mesh dump (*.rsm)|*.rsm|Wavefront (*.obj)|*.obj|" +
                     "All files (*.*)|*.*",
        };

        if (s_Dialog.ShowDialog(this) != true)
            return;

        LoadCustomMesh(s_Dialog.FileName);
    }

    private void LoadCustomMesh(string p_Path)
    {
        var s_Extension = Path.GetExtension(p_Path).ToLowerInvariant();

        // ⚠ FBX IS NOT READ. Saying so here, by name, beats a parse error from a file the dialog offered:
        // it is a container format (binary or ASCII, versioned) and reading it honestly is a parser, not an
        // afternoon. Every exporter writes OBJ.
        if (s_Extension == ".fbx")
        {
            Log("FBX is not read yet — export it as OBJ (every tool does) or dump a game mesh to .rsm.");
            return;
        }

        var s_Error = s_Extension == ".obj"
            ? m_Preview.LoadObj(p_Path)
            : m_Preview.LoadMeshSections(p_Path);

        if (s_Error != null)
        {
            Log($"Could not preview '{Path.GetFileName(p_Path)}': {s_Error}");
            return;
        }

        m_PreviewMesh = null;
        m_MeshListFilling = true;
        MeshList.SelectedItem = null;
        m_MeshListFilling = false;

        CustomMeshName.Text = Path.GetFileName(p_Path);
        CustomMeshClear.IsEnabled = true;

        m_MeshMode = true;
        m_Preview.Shape = View.PreviewShape.Mesh;
        SyncShapeIcons(View.PreviewShape.Mesh);
        Log($"Previewing '{Path.GetFileName(p_Path)}' — preview only; nothing about it is baked or shipped.");
    }

    private void OnClearCustomMesh(object p_Sender, RoutedEventArgs p_Args)
    {
        CustomMeshName.Text = "(none)";
        CustomMeshClear.IsEnabled = false;
        m_Preview.Shape = View.PreviewShape.Cube;
        SyncShapeIcons(View.PreviewShape.Cube);
        Log("Custom mesh cleared — pick a linked object above to go back to a game mesh.");
    }

    private string MeshCacheDir => Path.Combine(OutputFolderBox.Text, "meshcache");

    /// <summary>
    /// Whether a cached dump is of the CURRENT format — its magic is the version stamp. A dump from an older
    /// one re-dumps automatically: telling the user to go delete a cache file by hand is not an error
    /// message, it is homework.
    /// </summary>
    private static bool IsCurrentRsm(string p_Path)
    {
        try
        {
            using var s_Stream = File.OpenRead(p_Path);
            var s_Magic = new byte[4];
            return s_Stream.Read(s_Magic, 0, 4) == 4 &&
                   System.Text.Encoding.ASCII.GetString(s_Magic) == "RSM3";
        }
        catch
        {
            return false;
        }
    }

    private async void OnMeshPicked(object p_Sender, SelectionChangedEventArgs p_Args)
    {
        if (m_MeshListFilling || MeshList.SelectedItem is not string s_Mesh)
            return;

        m_PreviewMesh = s_Mesh;
        CustomMeshName.Text = "(none)";
        CustomMeshClear.IsEnabled = false;

        var s_File = Path.Combine(MeshCacheDir, $"{Sanitize(s_Mesh)}.rsm");
        if (!File.Exists(s_File) || !IsCurrentRsm(s_File))
        {
            var s_GamePath = GamePathBox.Text.Trim();
            var s_Repl = FindRimeRepl();
            if (s_Repl == null || s_GamePath.Length == 0 || !Directory.Exists(s_GamePath))
            {
                Log("ERROR: mesh dump needs RimeREPL and a valid BF3 path.");
                return;
            }

            Log($"Mounting BF3 to dump '{s_Mesh}' — one mount, ~20 s, please wait…");
            Directory.CreateDirectory(MeshCacheDir);
            var s_LevelDb = ResolveShaderDb(TargetShaderBox.Text.Trim());

            // While the mount runs: the cube stands in, and the mesh icon + list grey out so a second
            // click cannot start a competing mount. The bar walks an ESTIMATE (mounting reports no
            // progress) and completes on arrival.
            ShapeMesh.IsEnabled = false;
            MeshList.IsEnabled = false;
            MeshProgress.Value = 0;
            MeshProgress.Visibility = Visibility.Visible;
            OnShapeIcon(ShapeCube, p_Args);

            var s_Timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150),
            };
            s_Timer.Tick += (_, _) => MeshProgress.Value = Math.Min(95.0, MeshProgress.Value + 95.0 / 150.0);
            s_Timer.Start();

            try
            {
                var s_Script = Path.Combine(MeshCacheDir, "dump_mesh.rime");
                await Task.Run(() =>
                {
                    File.WriteAllText(s_Script,
                        $"mount_game \"{s_GamePath}\" Frostbite2_0 true\nselect_game 1\n" +
                        $"dump_mesh_sections {s_Mesh} \"{s_File}\" {s_LevelDb}\nexit\n");
                    Run(s_Repl, new[] { s_Script });
                });
            }
            finally
            {
                s_Timer.Stop();
                MeshProgress.Value = 100;
                MeshProgress.Visibility = Visibility.Collapsed;
                ShapeMesh.IsEnabled = true;
                MeshList.IsEnabled = true;
            }

            if (!File.Exists(s_File))
            {
                Log($"ERROR: the game did not yield '{s_Mesh}' (level-only or DLC-mounted meshes are a " +
                    "known gap here). The preview keeps the cube.");
                return;
            }
        }

        var s_Error = m_Preview.LoadMeshSections(s_File);
        if (s_Error != null)
        {
            Log($"ERROR: could not load the dumped mesh ({s_Error}).");
            return;
        }

        m_MeshMode = true;
        m_Preview.MeshTargetShader = TargetShaderBox.Text.Trim();
        m_Preview.HideForeignSections = m_Settings.PreviewOnlyEditedShader;
        m_Preview.Shape = View.PreviewShape.Mesh;
        SyncShapeIcons(View.PreviewShape.Mesh);
        Log($"Preview mesh: '{s_Mesh}'.");

        if (!m_Settings.PreviewOnlyEditedShader)
            await RegisterForeignShadersAsync();
    }

    /// <summary>
    /// Fetches the REAL shaders for the loaded mesh's foreign sections — each one's own game bytecode
    /// (chosen permutation) plus its texture set — so the whole object previews the way the game draws
    /// it. Everything lands in the caches (meshcache\shaders + the shared texcache), so only the first
    /// object that needs a given shader pays its mounts.
    /// </summary>
    private async Task RegisterForeignShadersAsync()
    {
        var s_Target = TargetShaderBox.Text.Trim().Replace('\\', '/');
        var s_Foreign = m_Preview.MeshSections
            .Select(p_S => p_S.Shader)
            .Where(p_S => p_S.Length > 0 && !p_S.Equals(s_Target, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p_S => !m_Preview.HasForeignShader(p_S))
            .ToList();

        if (s_Foreign.Count == 0)
            return;

        var s_GamePath = GamePathBox.Text.Trim();
        var s_Repl = FindRimeRepl();
        if (s_Repl == null || s_GamePath.Length == 0)
        {
            Log("Foreign sections stay neutral: RimeREPL or the BF3 path is missing.");
            return;
        }

        var s_ShaderFolder = Path.Combine(MeshCacheDir, "shaders");
        Directory.CreateDirectory(s_ShaderFolder);
        ShapeMesh.IsEnabled = false;

        try
        {
            for (var i = 0; i < s_Foreign.Count; i++)
            {
                var s_Shader = s_Foreign[i];
                var s_Dxbc = Path.Combine(s_ShaderFolder, $"{Sanitize(s_Shader)}.dxbc");
                var s_MapPath = SlotMapPath(s_Shader);
                var s_NeedsMounts = !File.Exists(s_Dxbc) || !File.Exists(s_MapPath);

                if (s_NeedsMounts)
                {
                    Log($"Fetching section shader {i + 1}/{s_Foreign.Count}: '{s_Shader}' " +
                        "(mounting the game; first time only)…");
                    MeshProgress.Value = 0;
                    MeshProgress.Visibility = Visibility.Visible;
                }

                var s_Timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(200),
                };
                s_Timer.Tick += (_, _) => MeshProgress.Value = Math.Min(95.0, MeshProgress.Value + 95.0 / 300.0);
                if (s_NeedsMounts)
                    s_Timer.Start();

                try
                {
                    var s_ShaderDb = ResolveShaderDb(s_Shader);
                    var s_Error = await Task.Run(() => FetchForeignShaderCore(
                        s_Repl, s_GamePath, s_ShaderDb, s_Shader, s_Dxbc, s_MapPath));

                    if (s_Error != null)
                    {
                        Log($"  '{s_Shader}': {s_Error} — its sections stay neutral.");
                        continue;
                    }
                }
                finally
                {
                    s_Timer.Stop();
                    MeshProgress.Visibility = Visibility.Collapsed;
                }

                var s_Register = RegisterForeignFromCache(s_Shader, s_Dxbc, s_MapPath);
                Log(s_Register == null
                    ? $"  '{s_Shader}': real shader + art loaded for its sections."
                    : $"  '{s_Shader}': {s_Register} — its sections stay neutral.");
            }
        }
        finally
        {
            ShapeMesh.IsEnabled = true;
            SchedulePreview();
        }
    }

    /// <summary>The mount-side half: the chosen permutation's bytecode plus the texture cache, idempotent.</summary>
    internal static string? FetchForeignShaderCore(string p_Repl, string p_GamePath, string p_ShaderDb,
        string p_Shader, string p_DxbcPath, string p_MapPath)
    {
        if (!File.Exists(p_DxbcPath))
        {
            var s_WorkDir = Path.Combine(Path.GetDirectoryName(p_DxbcPath)!, Sanitize(p_Shader) + "_extract");
            var s_Extract = Path.Combine(s_WorkDir, "perms");
            Directory.CreateDirectory(s_WorkDir);
            if (Directory.Exists(s_Extract))
                try { Directory.Delete(s_Extract, true); } catch { }

            var s_Script = Path.Combine(s_WorkDir, "extract.rime");
            File.WriteAllText(s_Script,
                $"mount_game \"{p_GamePath}\" Frostbite2_0 true\nselect_game 1\n" +
                $"extract_shader_dxbc {p_ShaderDb} \"{p_Shader}\" \"{s_Extract}\"\nexit\n");
            Run(p_Repl, new[] { s_Script });

            var s_Folder = s_Extract;
            var s_IndexPath = Path.Combine(s_Extract, "index.txt");
            if (File.Exists(s_IndexPath))
            {
                var s_Row = File.ReadAllLines(s_IndexPath)
                    .Select(p_L => p_L.Split('\t'))
                    .Where(p_P => p_P.Length >= 2)
                    .FirstOrDefault(p_P => p_P[1].Equals(p_Shader, StringComparison.OrdinalIgnoreCase));
                if (s_Row == null)
                    return "the game yielded no exact match for this shader";

                s_Folder = Path.Combine(s_Extract, s_Row[0]);
            }

            var s_Candidates = Directory.Exists(s_Folder)
                ? new DirectoryInfo(s_Folder).GetFiles("*_ps.dxbc").OrderByDescending(p_F => p_F.Length).ToList()
                : new List<FileInfo>();
            if (s_Candidates.Count == 0)
                return "no pixel shader came out of the game";

            var s_Chosen = ShaderIndex.ChooseGBufferPermutation(s_Candidates, new List<string>());
            File.Copy(s_Chosen.FullName, p_DxbcPath, true);
            try { Directory.Delete(s_WorkDir, true); } catch { }
        }

        if (!File.Exists(p_MapPath))
        {
            Emit.ShaderContract? s_Contract = null;
            try { s_Contract = Emit.ShaderContract.Detect(File.ReadAllBytes(p_DxbcPath)); }
            catch { }

            var s_CacheDir = Path.GetDirectoryName(p_MapPath)!;
            LoadTexturesCore(p_Repl, p_GamePath, p_ShaderDb, p_Shader, s_CacheDir, p_MapPath, s_Contract);
            if (!File.Exists(p_MapPath))
                return "its texture map could not be built";
        }

        return null;
    }

    /// <summary>
    /// The cached art of one shader's slot map, decoded to pixel blocks the preview can take. A SHARED
    /// shader's sets are keyed by (mesh, variation) — the default set belongs to whichever mesh sorted
    /// first, so the set of the MESH BEING PREVIEWED is preferred when one exists (the jet preset's
    /// default set was another helicopter's art entirely).
    /// </summary>
    internal static List<(int Slot, uint[] Pixels, int Width, int Height)> LoadForeignTexturesFromCache(
        string p_MapPath, string p_TexCacheDir, string? p_PreferMesh = null)
    {
        var s_Textures = new List<(int Slot, uint[] Pixels, int Width, int Height)>();
        var s_Slots = ReadSlotMap(p_MapPath);

        if (p_PreferMesh is { Length: > 0 } &&
            ReadSlotMapFull(p_MapPath)?.Variations is { } s_Sets)
        {
            var s_MeshSet = s_Sets
                .Where(p_S => p_S.Key.StartsWith(p_PreferMesh + "|", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(p_S.Value.Mesh, p_PreferMesh, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p_S => p_S.Key.EndsWith("|0") || p_S.Value.Name == "(base)" ? 0 : 1)
                .Select(p_S => p_S.Value.Slots)
                .FirstOrDefault(p_S => p_S is { Count: > 0 });

            if (s_MeshSet != null)
                s_Slots = s_MeshSet;
        }

        foreach (var (s_Register, s_Name) in s_Slots ?? new Dictionary<string, string>())
        {
            var s_Png = Path.Combine(p_TexCacheDir, $"{Sanitize(s_Name)}.png");
            if (!int.TryParse(s_Register, out var s_Slot) || !File.Exists(s_Png))
                continue;

            var s_Bitmap = new BitmapImage();
            s_Bitmap.BeginInit();
            s_Bitmap.CacheOption = BitmapCacheOption.OnLoad;
            s_Bitmap.UriSource = new Uri(s_Png);
            s_Bitmap.EndInit();

            var s_Converted = new FormatConvertedBitmap(s_Bitmap, PixelFormats.Bgra32, null, 0);
            var s_Pixels = new uint[s_Converted.PixelWidth * s_Converted.PixelHeight];
            s_Converted.CopyPixels(s_Pixels, s_Converted.PixelWidth * 4, 0);
            s_Textures.Add((s_Slot, s_Pixels, s_Converted.PixelWidth, s_Converted.PixelHeight));
        }

        return s_Textures;
    }

    /// <summary>
    /// The material-instance VECTOR values (camo tint, tiling, wear…) of the set the mesh being previewed
    /// uses, so a foreign shader's external constants carry the level's own numbers instead of defaults.
    /// Same set preference as <see cref="LoadForeignTexturesFromCache"/>; the map's first set (the default
    /// one) stands in when the mesh has no set of its own.
    /// </summary>
    internal static Dictionary<string, string>? LoadForeignMaterialValues(
        string p_MapPath, string? p_PreferMesh = null)
    {
        if (ReadSlotMapFull(p_MapPath) is not { } s_Map)
            return null;

        // The engine's layering: the shaderdb default for every value parameter, overwritten by whatever the
        // level's material instance names for the set actually being previewed.
        var s_Result = s_Map.ExternalDefaults is { Count: > 0 } s_Defaults
            ? new Dictionary<string, string>(s_Defaults, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (s_Map.Variations is { Count: > 0 } s_Sets)
        {
            var s_Chosen = p_PreferMesh is { Length: > 0 }
                ? s_Sets
                    .Where(p_S => p_S.Key.StartsWith(p_PreferMesh + "|", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(p_S.Value.Mesh, p_PreferMesh, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p_S => p_S.Key.EndsWith("|0") || p_S.Value.Name == "(base)" ? 0 : 1)
                    .Select(p_S => p_S.Value)
                    .FirstOrDefault(p_S => p_S.Values is { Count: > 0 })
                : null;

            foreach (var (s_Param, s_Value) in (s_Chosen ?? s_Sets.Values.First()).Values ??
                                               new Dictionary<string, string>())
                s_Result[s_Param] = s_Value;
        }

        return s_Result.Count > 0 ? s_Result : null;
    }

    /// <summary>The in-process half: decode the cached art and hand shader + textures to the preview.</summary>
    private string? RegisterForeignFromCache(string p_Shader, string p_DxbcPath, string p_MapPath)
    {
        try
        {
            return m_Preview.RegisterForeignShader(p_Shader, File.ReadAllBytes(p_DxbcPath),
                LoadForeignTexturesFromCache(p_MapPath, TexCacheDir, m_PreviewMesh),
                LoadForeignMaterialValues(p_MapPath, m_PreviewMesh));
        }
        catch (Exception s_Exception)
        {
            return s_Exception.Message;
        }
    }

    /// <summary>
    /// Folds every texture-and-mask pair into the single node that vocabulary uses. Offered only while
    /// authoring in that style, because in this engine's own vocabulary the two nodes are the right shape.
    /// </summary>
    /// <summary>
    /// Rewrites the open graph into the vocabulary being authored in. A shader translated out of the game
    /// arrives as this engine's own nodes, so without this, authoring in the other one means working on a
    /// graph made of the wrong words. Every rewrite is one the emitted shader cannot tell apart; whatever
    /// cannot be rewritten that way is LEFT and reported by name.
    /// </summary>
    private void OnConvertToUdk(object p_Sender, RoutedEventArgs p_Args)
    {
        Canvas.PushUndo();
        var s_Report = Graph.UdkConvert.Run(Canvas.Graph);

        if (s_Report.Converted == 0 && s_Report.Folded == 0 && s_Report.Left == 0)
        {
            Log("Nothing to convert: this graph is already in that vocabulary.");
            return;
        }

        Log($"Converted {s_Report.Converted} node(s) and folded {s_Report.Folded} texture/mask pair(s)" +
            (s_Report.Left > 0 ? $"; left {s_Report.Left} alone." : "."));
        foreach (var s_Note in s_Report.Notes)
            Log($"  {s_Note}");

        TouchGraph();
        BuildProperties();
    }

    /// <summary>Redraw plus a debounced preview recompile — every edit path goes through here.</summary>
    private void TouchGraph()
    {
        Canvas.Refresh();
        SchedulePreview();
    }

    /// <summary>
    /// True while a CLI test mode drives a hidden window. Set by App.RunHeadless before any window exists.
    /// </summary>
    internal static bool Headless;

    private void OnOpenSettings(object p_Sender, RoutedEventArgs p_Args) =>
        new SettingsWindow(this, m_Settings, ApplySettingsChanged).ShowDialog();

    /// <summary>
    /// Everything a Settings change must touch, so the dialog applies LIVE: table-backed state re-derives,
    /// the preview constants re-feed (that is what makes the FLIR toggle visible without reselecting the
    /// target), and the file persists through the same headless-guarded path as the rest.
    /// </summary>
    internal void ApplySettingsChanged()
    {
        View.GridCanvas.ZoomSpeed = m_Settings.CanvasZoomSpeed;
        GraphCanvas.ApplyShortcuts(m_Settings.NodeShortcuts);
        m_Preview.HideForeignSections = m_Settings.PreviewOnlyEditedShader;

        // The vocabulary switch takes effect at once: a palette still listing the other style's nodes after
        // the user chose is the setting appearing not to work.
        BuildPalette();
        View.GraphCanvas.UdkStyle = m_Settings.UdkNodeStyle;
        CollapseButton.Visibility = m_Settings.UdkNodeStyle ? Visibility.Visible : Visibility.Collapsed;

        if (LoadCachedPreviews() == 0)
            PushInstanceValues(null);

        m_Settings.Save();
    }

    private void PersistSettings()
    {
        // The hidden windows the test seams build get their Closing raised at app shutdown like any other
        // window, and saving THEIR boxes overwrote the user's real settings with a test's scratch directory.
        if (Headless)
            return;

        m_Settings.GamePath = GamePathBox.Text.Trim();
        m_Settings.OutputFolder = OutputFolderBox.Text.Trim();
        m_Settings.Save();
    }

    // The level's catalogue, held so the filter box can rebuild the tree without going back to Rime.
    private List<ShaderIndex.Entry> m_ShaderIndex = new();

    /// <summary>
    /// The contract of the shader currently targeted, detected from its own bytecode. Drives the PsIn/PsOut the
    /// emitter writes and which interpolators the input nodes reach. Null = the rigid-mesh default.
    /// </summary>
    private ShaderContract? m_Contract;

    /// <summary>
    /// The level whose shader index is currently in the browser. It decides which shaderdb a browsed shader is
    /// read from: object shaders (Objects/..., XP2/...) are compiled into EVERY level that uses them, so their
    /// name alone cannot say which database to open - the level the user browsed them in can.
    /// </summary>
    private string? m_LoadedLevel;

    /// <summary>Fills the level dropdown from the install, so it is never a list this editor made up.</summary>
    private void BuildLevelList()
    {
        LevelBox.Items.Clear();
        foreach (var s_Level in LevelScanner.Scan(GamePathBox.Text.Trim()))
            LevelBox.Items.Add(s_Level);

        // Preselect the level the current target shader belongs to, if it names one.
        var s_Target = TargetShaderBox.Text.Trim();
        var s_Parts = s_Target.Split('/');
        if (s_Parts.Length > 2 && s_Parts[0].Equals("Levels", StringComparison.OrdinalIgnoreCase))
            LevelBox.SelectedItem = LevelBox.Items.Cast<string>()
                .FirstOrDefault(p_L => p_L.Equals(s_Parts[1], StringComparison.OrdinalIgnoreCase));

        if (LevelBox.SelectedItem == null && LevelBox.Items.Count > 0)
            LevelBox.SelectedItem = LevelBox.Items.Cast<string>()
                .FirstOrDefault(p_L => p_L.Equals("MP_017", StringComparison.OrdinalIgnoreCase))
                ?? LevelBox.Items[0];
    }

    private async void OnLoadShaderIndex(object p_Sender, RoutedEventArgs p_Args)
    {
        if (LevelBox.SelectedItem is not string s_Level)
        {
            Log("Pick a level first. If the list is empty, check the BF3 path - it is scanned from the install.");
            return;
        }

        var s_Cache = ShaderIndex.PathFor(OutputFolderBox.Text.Trim(), s_Level);
        if (File.Exists(s_Cache))
        {
            LoadIndexFromCache(s_Cache, s_Level);
            return;
        }

        var s_Repl = FindRimeRepl();
        if (s_Repl == null)
        {
            Log("ERROR: RimeREPL.exe not found (expected under Rime-src\\bin\\Release).");
            return;
        }

        var s_GamePath = GamePathBox.Text.Trim();
        if (s_GamePath.Length == 0 || !Directory.Exists(s_GamePath))
        {
            Log("ERROR: the BF3 path is empty or does not exist.");
            return;
        }

        LoadIndexButton.IsEnabled = false;
        Log($"Reading {s_Level}'s shader list - this mounts the game, so a few minutes. Cached afterwards.");

        try
        {
            var s_Db = LevelScanner.ShaderDbFor(s_Level);
            var s_Work = Path.Combine(OutputFolderBox.Text.Trim(), "shaderindex");
            Directory.CreateDirectory(s_Work);

            var s_Extract = Path.Combine(s_Work, "dxbc_" + s_Level.ToLowerInvariant());

            var s_Entries = await Task.Run(() =>
            {
                // Both steps in ONE mount: the name list and every shader's bytecode. Detecting a contract needs
                // the bytecode, and mounting per double-click would cost minutes each time.
                var s_ScriptPath = Path.Combine(s_Work, $"list_{s_Level.ToLowerInvariant()}.rime");
                File.WriteAllText(s_ScriptPath,
                    $"mount_game \"{s_GamePath}\" Frostbite2_0 true\nselect_game 1\n" +
                    $"dump_shader_db {s_Db}\n" +
                    $"extract_shader_dxbc {s_Db} / \"{s_Extract}\"\nexit\n");

                var s_Run = Run(s_Repl, new[] { s_ScriptPath });
                var s_Parsed = ShaderIndex.ParseDump(s_Run.Output.Split('\n'));
                var s_Contracts = ShaderIndex.DetectAll(s_Extract);

                return s_Parsed.Select(p_P =>
                {
                    s_Contracts.TryGetValue(p_P.Name, out var s_Contract);
                    return new ShaderIndex.Entry
                    {
                        Name = p_P.Name,
                        Textures = p_P.Textures,
                        Family = s_Contract?.Family.ToString() ?? "",
                        RenderTargets = s_Contract?.RenderTargets ?? 0,
                        Shape = s_Contract == null ? "" : string.Join(" ", s_Contract.Interpolators
                            .Select(p_I => $"TC{p_I.Index}.{p_I.UsedMaskText.TrimEnd('_')}" +
                                           (p_I.IsInteger ? "(int)" : ""))),
                        Registers = s_Contract == null ? "" : string.Join(",",
                            s_Contract.MaterialTextures.Select(p_T => p_T.Register)),
                    };
                }).ToList();
            });

            // The extracted bytecode was only needed to classify; ~100 MB per level is not worth keeping.
            try { if (Directory.Exists(s_Extract)) Directory.Delete(s_Extract, true); } catch { }

            if (s_Entries.Count == 0)
            {
                Log($"No shaders came back for {s_Level}. Nothing was cached; see shaderindex\\list_*.rime output.");
                return;
            }

            ShaderIndex.Save(s_Cache, s_Entries.Select(p_E => p_E.ToLine()));
            m_ShaderIndex = s_Entries;
            m_LoadedLevel = s_Level;
            ApplyShaderFilter();

            var s_Families = s_Entries.Where(p_E => p_E.HasContract).GroupBy(p_E => p_E.Family)
                .OrderByDescending(p_G => p_G.Count())
                .Select(p_G => $"{p_G.Key} {p_G.Count()}");

            Log($"{s_Entries.Count} shaders in {s_Level} ({s_Entries.Sum(p_E => p_E.Textures)} texture slots). " +
                $"Contracts: {string.Join(", ", s_Families)}");
        }
        catch (Exception s_Exception)
        {
            Log($"ERROR reading the shader list: {s_Exception.Message}");
        }
        finally
        {
            LoadIndexButton.IsEnabled = true;
        }
    }

    private void LoadIndexFromCache(string p_Path, string p_Level)
    {
        m_ShaderIndex = ShaderIndex.Load(p_Path).Select(ShaderIndex.Entry.FromLine).ToList();
        m_LoadedLevel = p_Level;
        ApplyShaderFilter();

        var s_WithContract = m_ShaderIndex.Count(p_E => p_E.HasContract);
        Log($"{m_ShaderIndex.Count} shaders in {p_Level} (from cache, {s_WithContract} with a detected contract)." +
            (s_WithContract == 0 ? " Delete the cache file and press Load again to add contracts." : ""));
    }

    private void OnShaderFilterChanged(object p_Sender, TextChangedEventArgs p_Args) => ApplyShaderFilter();

    /// <summary>
    /// Renders the whole window off-screen with a real shader list loaded, so the browser can be looked at
    /// without opening it. Catches what a logic test cannot: eaten underscores, clipped columns, a tree that
    /// renders empty. DesiredSize is not used here because the window has a fixed design size.
    /// </summary>
    internal static System.Windows.Media.Imaging.BitmapSource Snapshot(
        List<ShaderIndex.Entry> p_Index, string p_Filter)
    {
        var s_Window = new MainWindow();
        s_Window.m_ShaderIndex = p_Index;
        s_Window.ShaderFilterBox.Text = p_Filter;
        s_Window.ApplyShaderFilter();

        var s_Content = (FrameworkElement) s_Window.Content;
        var s_Size = new Size(s_Window.Width, s_Window.Height);
        s_Content.Measure(s_Size);
        s_Content.Arrange(new Rect(new Point(0, 0), s_Size));
        s_Content.UpdateLayout();

        var s_Target = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int) Math.Ceiling(s_Size.Width), (int) Math.Ceiling(s_Size.Height), 96, 96,
            PixelFormats.Pbgra32);

        s_Target.Render(s_Content);
        return s_Target;
    }

    /// <summary>
    /// Renders the window with the variation picker filled from an existing texture cache (no game mount), so
    /// the picker's colours can be LOOKED at - readability is a question only a picture answers.
    /// </summary>
    internal static int VariationShot(string p_WorkDir, string p_Target, string p_OutputPath)
    {
        var s_Window = new MainWindow();
        s_Window.OutputFolderBox.Text = p_WorkDir;
        s_Window.TargetShaderBox.Text = p_Target;

        var s_Count = s_Window.LoadCachedPreviews();
        Console.Out.WriteLine($"{s_Count} thumbnail(s) from cache; picker holds " +
                              $"{s_Window.VariationBox.Items.Count} item(s), {s_Window.VariationBox.Visibility}");

        var s_Content = (FrameworkElement) s_Window.Content;
        var s_Size = new Size(s_Window.Width, s_Window.Height);
        s_Content.Measure(s_Size);
        s_Content.Arrange(new Rect(new Point(0, 0), s_Size));
        s_Content.UpdateLayout();

        var s_Bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int) Math.Ceiling(s_Size.Width), (int) Math.Ceiling(s_Size.Height), 96, 96, PixelFormats.Pbgra32);
        s_Bitmap.Render(s_Content);

        var s_Encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        s_Encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(s_Bitmap));
        using (var s_Stream = File.Create(p_OutputPath))
            s_Encoder.Save(s_Stream);

        Console.Out.WriteLine($"rendered -> {p_OutputPath}");
        return s_Window.VariationBox.Items.Count > 1 ? 0 : 1;
    }

    private void ApplyShaderFilter()
    {
        var s_Query = ShaderFilterBox.Text;
        var s_Matching = m_ShaderIndex
            .Where(p_E => s_Query.Trim().Length == 0 ||
                          p_E.Name.Contains(s_Query.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Built defensively rather than with ToDictionary: a duplicate name would throw and take the whole
        // browser down, which is exactly what happened when the dump turned out to list each shader once per
        // render-path database.
        var s_Textures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s_Entry in m_ShaderIndex)
            s_Textures[s_Entry.Name] = Math.Max(s_Entry.Textures,
                s_Textures.TryGetValue(s_Entry.Name, out var s_Old) ? s_Old : 0);

        var s_Tree = ShaderIndex.Build(s_Matching.Select(p_E => p_E.Name));
        ShaderTree.Items.Clear();

        // Expanded when filtered, collapsed otherwise: a filter is only useful if you can see what it matched.
        var s_Filtered = s_Query.Trim().Length > 0;
        foreach (var s_Child in s_Tree.Children)
            ShaderTree.Items.Add(BuildShaderTreeItem(s_Child, s_Textures, s_Filtered));
    }

    private static TreeViewItem BuildShaderTreeItem(ShaderTreeNode p_Node,
        IReadOnlyDictionary<string, int> p_Textures, bool p_Expand)
    {
        // TextBlock rather than a string: a string Content is parsed for access keys and eats the underscores
        // that almost every shader name has.
        var s_Label = p_Node.IsShader
            ? $"{p_Node.Name}  ({(p_Textures.TryGetValue(p_Node.ShaderName!, out var s_T) ? s_T : 0)} tex)"
            : $"{p_Node.Name}  ({p_Node.ShaderCount})";

        var s_Item = new TreeViewItem
        {
            Header = new TextBlock { Text = s_Label },
            Tag = p_Node.ShaderName,
            IsExpanded = p_Expand && !p_Node.IsShader,
        };

        foreach (var s_Child in p_Node.Children)
            s_Item.Items.Add(BuildShaderTreeItem(s_Child, p_Textures, p_Expand));

        return s_Item;
    }

    private void OnShaderTreeSelected(object p_Sender, RoutedPropertyChangedEventArgs<object> p_Args)
    {
        if (p_Args.NewValue is TreeViewItem { Tag: string s_Shader })
            Log($"Selected {s_Shader} - double-click to make it the target shader.");
    }

    /// <summary>
    /// Makes the clicked shader the target AND shows it: translates its bytecode to a graph and fills its
    /// texture slots, without asking for anything else.
    ///
    /// ⛔ keku, after using it: *"al seleccionar el shader este ya debe tener hecho translate target to graph
    /// y load target textures, si no estamos añadiendo pasos inútilmente"*. He is right and the reason is that
    /// there is no other thing you would want here: picking a target IS the request to see it, so making the
    /// user press two more buttons to get the only useful outcome is ceremony, not choice. The buttons stay,
    /// but as a RETRY (after Ctrl+Z, or when a step failed), not as steps.
    ///
    /// It hangs off the double click rather than mere selection on purpose: each of these mounts the game for
    /// about twenty seconds, so browsing the tree must stay free.
    /// </summary>
    private async void OnShaderTreeDoubleClick(object p_Sender, MouseButtonEventArgs p_Args)
    {
        if (ShaderTree.SelectedItem is not TreeViewItem { Tag: string s_Shader })
            return;

        p_Args.Handled = true;
        await SelectTargetAsync(s_Shader);
    }

    /// <summary>Signals the headless seam that a whole selection - both steps - has finished.</summary>
    internal static TaskCompletionSource<int>? SelectFinished;

    /// <summary>
    /// Everything picking a target does, as one awaitable unit so it can be driven headlessly. The seam enters
    /// HERE and not at the individual buttons on purpose: what changed is the CHAINING of the two steps, and a
    /// test that presses each button separately would pass whether or not they are chained at all.
    /// </summary>
    internal async Task SelectTargetAsync(string s_Shader)
    {
      try
      {
        // A shader open in ANOTHER tab just gets focus — a second double click must not pay the mounts again
        // nor silently replace a graph being edited elsewhere. Selecting the ACTIVE tab's own target falls
        // through instead: that is the retry ("Reload from game", or double-clicking what you are looking at).
        var s_AlreadyOpen = FindTabByTarget(s_Shader);
        if (s_AlreadyOpen >= 0 && s_AlreadyOpen != m_ActiveTab)
        {
            SaveActiveTab();
            ActivateTab(s_AlreadyOpen);
            Log($"'{s_Shader}' is already open in a tab — switched to it. Select it again (or press " +
                "'Reload from game') for a fresh read.");
            SelectFinished?.TrySetResult(Canvas.Graph.Nodes.Count);
            return;
        }

        // A NEW shader opens as its own document, leaving the current tab exactly as it was.
        if (s_AlreadyOpen < 0)
            OpenInNewTab(new ShaderGraph { TargetShader = s_Shader }, null);

        TargetShaderBox.Text = s_Shader;
        Canvas.Graph.TargetShader = s_Shader;

        // The contract was detected when the level was catalogued, so this costs nothing here.
        var s_Entry = m_ShaderIndex.FirstOrDefault(
            p_E => p_E.Name.Equals(s_Shader, StringComparison.OrdinalIgnoreCase));

        m_Contract = null;
        if (s_Entry is { HasContract: true })
        {
            m_Contract = ContractFromEntry(s_Entry);
            Log($"Target = {s_Shader}");
            Log($"  contract: {s_Entry.Family}, {s_Entry.RenderTargets} render target(s), " +
                $"interpolators {s_Entry.Shape}");

            if (s_Entry.Family is not ("RigidMesh" or "RigidMeshSubMaterial"))
                Log("  NOTE: only the rigid-mesh interpolator meanings are measured. For this family the " +
                    "editor exposes them raw (Inputs > Interpolator), it does not claim to know what each one is.");
        }
        else
        {
            Log($"Target = {s_Shader} (no contract in the cache; assuming the rigid-mesh layout). " +
                "Delete the level's shaderindex file and press Load again to detect it.");
        }

        // Whatever the previous target taught the vertex shader no longer applies; the translation step will
        // teach it this shader's meanings in a moment. Without the reset, a rigid mesh selected after an
        // exotic one previewed with the exotic inputs.
        m_Preview.SetInterpolatorMeanings(m_Contract?.InterpolatorMeanings);

        // Same for the material constants: the previous target's instance values must not leak, and the new
        // contract's engine-fed parameters get their Settings-decided state from the first frame.
        PushInstanceValues(null);

        // Both steps, in this order and not the other: the translation adopts the contract of the permutation it
        // actually read and drops the stale thumbnails, and the texture step needs that contract to know which
        // register each texture belongs in.
        //
        // ⛔ And the machine has to LOOK busy for the whole thing. The first mount is ~50 silent seconds, and
        // with nothing moving keku concluded the double-click had done nothing and pressed the buttons himself
        // - the exact ceremony this chain exists to remove. A busy cursor plus both buttons greyed is the
        // difference between "it is working" and "it ignored me".
        Log($"double-click: translating '{s_Shader}' and loading its textures - two game mounts, ~2 min the " +
            "first time. The buttons stay greyed until it is done.");
        ReloadButton.IsEnabled = false;
        ReloadButton.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.AppStarting;

        var s_Translated = await RunTranslateAsync();

        // The inner step re-enables its own button on the way out; during the texture mount both stay off.
        ReloadButton.IsEnabled = false;

        if (!s_Translated)
        {
            // Only when the real thing could not be produced. The scaffold used to be what you always got here,
            // and it is strictly worse: it is a guess at the inputs, where the translation is the shader.
            // ⚠ The cached slot map behind it predates the contract, so its registers may be the old
            // declaration-order guess - which is exactly why it is the fallback and not the default.
            Canvas.PushUndo();
            var s_Slots = CachedSlots(s_Shader);
            LoadGraph(SampleGraphs.ScaffoldFor(s_Shader, s_Slots));

            var s_Registers = string.Join(", ",
                s_Slots.Select(p_S => $"t{p_S.Register}={p_S.Name.Split('/').Last()}"));

            Log(s_Slots.Count == 0
                ? "  the translation did not run, so the canvas holds an EMPTY scaffold. Ctrl+Z restores the " +
                  "previous graph."
                : $"  the translation did not run, so the canvas holds a SCAFFOLD from the cached slot map " +
                  $"({s_Registers}) - a guess at the inputs, not this shader. Ctrl+Z restores the previous graph.");

            if (m_Contract == null)
                Log("  FIX: press Load on the level (one game mount) so its contract is detected, then " +
                    "double-click the shader again.");

            return;
        }

        await RunLoadTexturesAsync();
      }
      finally
      {
        ReloadButton.IsEnabled = true;
        ReloadButton.IsEnabled = true;
        Mouse.OverrideCursor = null;
        SelectFinished?.TrySetResult(Canvas.Graph.Nodes.Count);
      }
    }

    /// <summary>
    /// The target's textures from the thumbnail cache as (register, name). The map is keyed by the REAL
    /// register, which is what the scaffold has to put on each node.
    /// </summary>
    /// <summary>The slot map on disk, version-checked.</summary>
    /// <remarks>
    /// ⛔ The map's SCHEMA carries a version because the PAIRING RULE is part of the data: the old maps were
    /// built with the identity rule (texture_TextureN = Nth streamable), which put a normal map into a colour
    /// slot on 89 of the shaders measured against their own disassembly. A cached wrong map would keep being
    /// served forever - the loader returns early on cache hits - so old maps must READ as absent, not as valid.
    /// </remarks>
    internal sealed class SlotMap
    {
        public int V { get; set; }
        public Dictionary<string, string> Slots { get; set; } = new();

        /// <summary>
        /// v3: per-variation slot maps, keyed by the variation hash ("0" = the unvaried master). A shader
        /// whose textures are material parameters can carry several complete texture sets - one per painting,
        /// per camo... - all sharing the master's bytecode, so the editor can swap them without a remount.
        /// </summary>
        public Dictionary<string, VariationSlots>? Variations { get; set; }

        /// <summary>
        /// v5: the shaderdb's own DEFAULT for each external VALUE parameter ("name" -> "x,y,z,w"). This is
        /// what the engine feeds when no material instance names the value — a foreign shader's constant
        /// buffer starts from these, and the set's Values overwrite the ones the level does name. A made-up
        /// neutral is somebody's lethal value: 1 in the FLIR gate rendered the whole jet in thermal mode.
        /// </summary>
        public Dictionary<string, string>? ExternalDefaults { get; set; }

        /// <summary>
        /// v6: how many different meshes in the level wear this shader, and a few of their names.
        ///
        /// ⛔ IT IS THE ONE FACT THAT DECIDES HOW A BAKE MUST BE AIMED, and nothing in the editor showed it.
        /// A replacement answers for the shader's NAME: worn by one mesh it changes that object, worn by
        /// seventy-five it repaints the map. Null in maps cached before this existed — the UI then says the
        /// count is unknown rather than inventing a reassuring one, and the bake measures it anyway.
        /// </summary>
        public int? MeshUsers { get; set; }

        public List<string>? MeshUserSample { get; set; }

        /// <summary>
        /// WHICH MAP that count is from, and it has to travel with it.
        ///
        /// ⛔ A shader is not worn by the same objects everywhere: a preset dresses different meshes in every
        /// map, and one that only one object wears here can be shared in the next. The scope also is not the
        /// author's choice — a shader whose name does not carry a level falls back to a default one — so a
        /// bare number reads as "in the game" when it means "in that map". Named here, the label can say so,
        /// and the bake measures the maps actually being built for.
        /// </summary>
        public string? MeshUsersScope { get; set; }
    }

    internal sealed class VariationSlots
    {
        public string Name { get; set; } = "";

        /// <summary>Mesh partition of the database entry this set came from — what a variation bake copies.
        /// Null in maps cached before variation support; the bake asks for a re-load instead of guessing.</summary>
        public string? Mesh { get; set; }

        /// <summary>Full variation asset path ("(base)" for the unvaried master); the short label above is
        /// ambiguous across folders, and new sibling names are derived from this one.</summary>
        public string? AssetName { get; set; }

        public Dictionary<string, string> Slots { get; set; } = new();

        /// <summary>
        /// The material instance's REAL parameter values ("name" -> "x,y,z,w"), where the level's data names
        /// any - the preview feeds them into cb1 so the object shades as ITS instance, not as the probe
        /// pattern. Parameter names come without the bytecode's external_ prefix.
        /// </summary>
        public Dictionary<string, string>? Values { get; set; }
    }

    internal static SlotMap? ReadSlotMapFull(string p_Path)
    {
        try
        {
            if (!File.Exists(p_Path))
                return null;

            var s_Map = System.Text.Json.JsonSerializer.Deserialize<SlotMap>(File.ReadAllText(p_Path));

            // The pairing rule is PART of the data: a map written under an older rule must read as ABSENT or
            // the cache serves the old error forever. v4 = exact-shader scoping + named-register externals +
            // level-wide database scan; v3 maps (substring-polluted slot lists on shared roots) pair wrongly.
            // v5: external slots are the EXTSLOT∪EXTERNAL union and spaced texture names parse whole — v4
            // maps built for vehicles miss their Diffuse/Camo/Specular entirely, so they must refetch.
            return s_Map is { V: >= 5 } ? s_Map : null;
        }
        catch
        {
            // A v1 map is a plain dictionary and fails to bind to SlotMap the same way garbage does: absent.
            return null;
        }
    }

    internal static Dictionary<string, string>? ReadSlotMap(string p_Path) => ReadSlotMapFull(p_Path)?.Slots;

    private List<(int Register, string Name)> CachedSlots(string p_Target)
    {
        var s_Slots = ReadSlotMap(SlotMapPath(p_Target));
        if (s_Slots == null)
            return new List<(int, string)>();

        return s_Slots
            .Select(p_S => (Register: int.TryParse(p_S.Key, out var s_N) ? s_N : 0, Name: p_S.Value))
            .Where(p_S => p_S.Register > 0)
            .OrderBy(p_S => p_S.Register)
            .ToList();
    }

    /// <summary>
    /// Rebuilds a contract from the cached line. Only the shape is cached, which is all the emitter needs: the
    /// family decides the field names and the interpolator list decides the PsIn.
    /// </summary>
    private static ShaderContract ContractFromEntry(ShaderIndex.Entry p_Entry)
    {
        var s_Inputs = new List<SignatureElement>();
        foreach (var s_Token in p_Entry.Shape.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // "TC3.xyz" or "TC5.x(int)"
            var s_IsInteger = s_Token.EndsWith("(int)", StringComparison.Ordinal);
            var s_Clean = s_Token.Replace("(int)", "");
            var s_Split = s_Clean.Split('.');
            if (s_Split.Length != 2 || !int.TryParse(s_Split[0].TrimStart('T', 'C'), out var s_Index))
                continue;

            byte s_Used = 0;
            foreach (var s_Component in s_Split[1])
                s_Used |= s_Component switch
                {
                    'x' => (byte) 1, 'y' => (byte) 2, 'z' => (byte) 4, 'w' => (byte) 8, _ => (byte) 0,
                };

            s_Inputs.Add(new SignatureElement
            {
                Semantic = "TEXCOORD", Index = s_Index, Register = s_Index + 1,
                Mask = 0xF, UsedMask = s_Used, ComponentType = s_IsInteger ? 1 : 3,
            });
        }

        // The cached registers rebuild just enough of the binding table for RegisterForStreamable: the Nth entry
        // is the Nth material texture in streamable order, which is what the loader and the scaffold need.
        var s_Resources = p_Entry.Registers.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select((p_R, p_I) => new ResourceBinding
            {
                Name = "texture_Texture" + (p_I == 0 ? "" : (p_I + 1).ToString()),
                Register = int.TryParse(p_R, out var s_Reg) ? s_Reg : 0,
                InputType = 2,
            })
            .ToList();

        return new ShaderContract
        {
            Family = Enum.TryParse<ShaderFamily>(p_Entry.Family, out var s_Family) ? s_Family : ShaderFamily.Unknown,
            RenderTargets = p_Entry.RenderTargets,
            Inputs = s_Inputs,
            Resources = s_Resources,
        };
    }

    private void BuildPalette()
    {
        PaletteTree.Items.Clear();

        // The foreign group sorts LAST and starts closed: it is the overflow a conversion can leave behind,
        // not part of the vocabulary being authored in, so it must not be the first thing the eye lands on.
        foreach (var s_Group in Palette.VisibleFor(m_Settings.UdkNodeStyle)
                     .GroupBy(p_D => p_D.CategoryFor(m_Settings.UdkNodeStyle))
                     .OrderBy(p_G => p_G.Key == Palette.c_ForeignCategory ? 1 : 0)
                     .ThenBy(p_G => p_G.Key))
        {
            var s_Foreign = s_Group.Key == Palette.c_ForeignCategory;
            var s_Item = new TreeViewItem { Header = s_Group.Key, IsExpanded = !s_Foreign };
            if (s_Foreign)
                s_Item.ToolTip = "Nodes this engine has and that vocabulary does not. Converting a graph can " +
                                 "leave them behind, so they are placeable here under their real names — " +
                                 "some have no counterpart because that catalog simply has no such node.";

            foreach (var s_Def in s_Group.OrderBy(p_D => p_D.TitleFor(m_Settings.UdkNodeStyle)))
                s_Item.Items.Add(new TreeViewItem
                {
                    Header = s_Def.TitleFor(m_Settings.UdkNodeStyle), Tag = s_Def.Kind,
                });

            PaletteTree.Items.Add(s_Item);
        }
    }

    private void OnPaletteDoubleClick(object p_Sender, RoutedEventArgs p_Args)
    {
        if (PaletteTree.SelectedItem is TreeViewItem { Tag: string s_Kind })
        {
            Canvas.AddNode(s_Kind);
            Log($"Added {Palette.Get(s_Kind).TitleFor(m_Settings.UdkNodeStyle)}.");
        }
    }

    private Point m_PaletteDragOrigin;

    private void OnPaletteMouseDown(object p_Sender, MouseButtonEventArgs p_Args) =>
        m_PaletteDragOrigin = p_Args.GetPosition(null);

    /// <summary>
    /// Starts a drag once the pointer has actually travelled, so a plain click still selects the entry in the
    /// tree instead of being swallowed as a drag.
    /// </summary>
    private void OnPaletteMouseMove(object p_Sender, MouseEventArgs p_Args)
    {
        if (p_Args.LeftButton != MouseButtonState.Pressed)
            return;

        var s_Travelled = p_Args.GetPosition(null) - m_PaletteDragOrigin;
        if (Math.Abs(s_Travelled.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(s_Travelled.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (FindKindUnderMouse(p_Args.OriginalSource as DependencyObject) is not string s_Kind)
            return;

        DragDrop.DoDragDrop(PaletteTree, s_Kind, DragDropEffects.Copy);
    }

    /// <summary>Walks up from whatever was hit to the TreeViewItem that carries a node kind.</summary>
    private static string? FindKindUnderMouse(DependencyObject? p_Source)
    {
        while (p_Source != null)
        {
            if (p_Source is TreeViewItem { Tag: string s_Kind })
                return s_Kind;

            p_Source = System.Windows.Media.VisualTreeHelper.GetParent(p_Source);
        }

        return null;
    }

    private void OnCopy(object p_Sender, ExecutedRoutedEventArgs p_Args)
    {
        if (Canvas.CopySelection())
            Log($"Copied {Canvas.SelectedCount} node(s).");
        else
            Log("Nothing selected to copy.");
    }

    private void OnPaste(object p_Sender, ExecutedRoutedEventArgs p_Args)
    {
        if (!Canvas.PasteSelection())
        {
            Log("Clipboard holds no nodes copied from this editor.");
            return;
        }

        Log($"Pasted {Canvas.SelectedCount} node(s).");
        if (Canvas.SkippedRoots > 0)
            Log($"Skipped {Canvas.SkippedRoots} root node(s): the graph already has one, and two roots would " +
                "make the render targets ambiguous.");
    }

    private void LoadGraph(ShaderGraph p_Graph)
    {
        Canvas.Graph = p_Graph;
        TargetShaderBox.Text = p_Graph.TargetShader;
        GraphNameBox.Text = p_Graph.Name;
        BuildProperties();
        SchedulePreview();
        RefreshTabBar();
    }

    /// <summary>
    /// One open document. The canvas and the window fields hold the ACTIVE one; a tab is where the others
    /// wait — graph, save path, contract, texture-set choice and undo history all travel together, because
    /// a tab that came back without any one of them would silently wear the previous document's state.
    /// </summary>
    private sealed class EditorTab
    {
        public ShaderGraph Graph = new();
        public string? GraphPath;
        public ShaderContract? Contract;
        public string Variation = "0";

        // What this tab was previewing. The mesh is remembered even while a primitive is showing, so
        // going back to the mesh icon does not ask for the object again.
        public View.PreviewShape Shape = View.PreviewShape.Cube;
        public string? PreviewMesh;
        public string[] Undo = Array.Empty<string>();
        public string[] Redo = Array.Empty<string>();
    }

    private readonly List<EditorTab> m_Tabs = new();
    private int m_ActiveTab = -1;

    /// <summary>Parks the window's live state into the active tab before another one takes the canvas.</summary>
    private void SaveActiveTab()
    {
        if (m_ActiveTab < 0 || m_ActiveTab >= m_Tabs.Count)
            return;

        var s_Tab = m_Tabs[m_ActiveTab];
        s_Tab.Graph = Canvas.Graph;
        (s_Tab.Undo, s_Tab.Redo) = Canvas.SnapshotUndo();
        s_Tab.Contract = m_Contract;
        s_Tab.Variation = m_Variation;
        s_Tab.GraphPath = m_GraphPath;
        s_Tab.Shape = m_Preview.Shape;
        s_Tab.PreviewMesh = m_PreviewMesh;
    }

    private void ActivateTab(int p_Index)
    {
        m_ActiveTab = p_Index;
        var s_Tab = m_Tabs[p_Index];

        Canvas.Graph = s_Tab.Graph;
        Canvas.RestoreUndo(s_Tab.Undo, s_Tab.Redo);
        m_Contract = s_Tab.Contract;
        m_Variation = s_Tab.Variation;
        m_GraphPath = s_Tab.GraphPath;

        TargetShaderBox.Text = s_Tab.Graph.TargetShader;
        GraphNameBox.Text = s_Tab.Graph.Name;
        m_Preview.SetInterpolatorMeanings(m_Contract?.InterpolatorMeanings);

        // Thumbnails and instance values re-derive from the disk cache (their single source); a target with
        // no cache yet just gets the Settings-decided engine parameters. Custom textures ride on top either
        // way — a tab with no cache still shows the user's own files.
        m_TexturePreviews.Clear();
        if (LoadCachedPreviews() == 0)
        {
            PushInstanceValues(null);
            ApplyCustomTextureOverrides();
        }

        BuildProperties();
        RestoreTabPreview(s_Tab);
        SchedulePreview();
        RefreshTabBar();
    }

    /// <summary>
    /// Puts back the shape — and the mesh — this tab was last previewing.
    ///
    /// ⛔ CACHE-ONLY on purpose: switching tabs must never mount the game. A mesh whose dump is missing or
    /// of an older format falls back to the primitive and FORGETS itself, rather than leaving the mesh icon
    /// lit over an object that is not loaded. The name is restored either way, so the list still shows
    /// which object this tab belongs to.
    /// </summary>
    private async void RestoreTabPreview(EditorTab p_Tab)
    {
        m_PreviewMesh = p_Tab.PreviewMesh;

        if (p_Tab.Shape == View.PreviewShape.Mesh && p_Tab.PreviewMesh is { Length: > 0 } s_Mesh)
        {
            var s_File = Path.Combine(MeshCacheDir, $"{Sanitize(s_Mesh)}.rsm");
            if (File.Exists(s_File) && IsCurrentRsm(s_File) && m_Preview.LoadMeshSections(s_File) == null)
            {
                // The target of THIS tab, not the one the mesh was first loaded under: the same object can
                // be open in two tabs for two different shaders of it.
                m_Preview.MeshTargetShader = p_Tab.Graph.TargetShader;
                m_Preview.HideForeignSections = m_Settings.PreviewOnlyEditedShader;
                m_Preview.Shape = View.PreviewShape.Mesh;
                SyncShapeIcons(View.PreviewShape.Mesh);

                if (!m_Settings.PreviewOnlyEditedShader)
                    await RegisterForeignShadersAsync();

                return;
            }

            p_Tab.Shape = View.PreviewShape.Cube;
        }

        m_Preview.Shape = p_Tab.Shape;
        SyncShapeIcons(p_Tab.Shape);
    }

    /// <summary>Lights the one icon that matches the shape actually being rendered.</summary>
    /// <summary>
    /// Whether the mesh section is on screen. It is the mesh ICON's state rather than the preview's shape,
    /// because the two differ for one real moment: the icon pressed with nothing loaded yet, where the panel
    /// has to be visible for a mesh to be picked at all while the preview still shows the primitive.
    /// </summary>
    private bool m_MeshMode;

    /// <summary>
    /// The ONE place the shape toolbar and the mesh section are set from. Three copies of "check this icon,
    /// uncheck the rest" had grown across the handlers, which is how a toolbar ends up disagreeing with what
    /// the preview is actually drawing.
    /// </summary>
    private void SyncShapeIcons(View.PreviewShape p_Shape)
    {
        ShapeSphere.IsChecked = p_Shape == View.PreviewShape.Sphere;
        ShapeCube.IsChecked = p_Shape == View.PreviewShape.Cube;
        ShapeCylinder.IsChecked = p_Shape == View.PreviewShape.Cylinder;
        ShapePlane.IsChecked = p_Shape == View.PreviewShape.Plane;
        ShapeMesh.IsChecked = p_Shape == View.PreviewShape.Mesh || m_MeshMode;
        MeshPanel.Visibility = m_MeshMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenInNewTab(ShaderGraph p_Graph, string? p_Path)
    {
        SaveActiveTab();
        m_Tabs.Add(new EditorTab { Graph = p_Graph, GraphPath = p_Path });
        ActivateTab(m_Tabs.Count - 1);
    }

    private int FindTabByTarget(string p_Target)
    {
        for (var i = 0; i < m_Tabs.Count; i++)
        {
            var s_Graph = i == m_ActiveTab ? Canvas.Graph : m_Tabs[i].Graph;
            if (s_Graph.TargetShader.Equals(p_Target, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private void CloseTab(int p_Index)
    {
        if (p_Index < 0 || p_Index >= m_Tabs.Count)
            return;

        // Edits are what the undo history records, so an untouched tab closes silently and a worked-on one
        // asks. Headless never closes tabs interactively, but the guard keeps a seam from hanging on a box.
        var (s_Undo, _) = p_Index == m_ActiveTab ? Canvas.SnapshotUndo() : (m_Tabs[p_Index].Undo, m_Tabs[p_Index].Redo);
        if (!Headless && s_Undo.Length > 0 &&
            MessageBox.Show(this, "This tab has edits. Close it and lose them?", "Close tab",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        m_Tabs.RemoveAt(p_Index);

        if (m_Tabs.Count == 0)
        {
            // The window always shows a document; closing the last tab starts a fresh one.
            m_Tabs.Add(new EditorTab { Graph = NewGraphWithRoot() });
            m_ActiveTab = -1;
            ActivateTab(0);
            return;
        }

        if (p_Index == m_ActiveTab)
        {
            m_ActiveTab = -1;
            ActivateTab(Math.Min(p_Index, m_Tabs.Count - 1));
        }
        else
        {
            if (p_Index < m_ActiveTab)
                m_ActiveTab--;
            RefreshTabBar();
        }
    }

    private string TabTitleOf(int p_Index)
    {
        var s_Graph = p_Index == m_ActiveTab ? Canvas.Graph : m_Tabs[p_Index].Graph;
        if (!string.IsNullOrWhiteSpace(s_Graph.Name) && s_Graph.Name != "Untitled")
            return s_Graph.Name;

        var s_Target = s_Graph.TargetShader;
        return string.IsNullOrWhiteSpace(s_Target) ? "Untitled" : s_Target.Split('/').Last();
    }

    private void RefreshTabBar()
    {
        if (TabStrip == null)
            return;

        TabStrip.Children.Clear();

        for (var i = 0; i < m_Tabs.Count; i++)
        {
            var s_Index = i;
            var s_Active = i == m_ActiveTab;

            var s_Title = new TextBlock
            {
                Text = TabTitleOf(i),
                Foreground = new SolidColorBrush(s_Active ? Color.FromRgb(240, 240, 240) : Color.FromRgb(160, 160, 160)),
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 180,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var s_Close = new TextBlock
            {
                Text = "✕",
                Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)),
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 10,
                ToolTip = "Close tab (middle-click also closes)",
            };
            s_Close.MouseLeftButtonDown += (_, s_Args) =>
            {
                s_Args.Handled = true;
                CloseTab(s_Index);
            };

            var s_Content = new StackPanel { Orientation = Orientation.Horizontal };
            s_Content.Children.Add(s_Title);
            s_Content.Children.Add(s_Close);

            var s_Tab = new Border
            {
                Background = new SolidColorBrush(s_Active ? Color.FromRgb(50, 50, 58) : Color.FromRgb(38, 38, 42)),
                BorderBrush = new SolidColorBrush(s_Active ? Color.FromRgb(150, 210, 150) : Color.FromRgb(60, 60, 64)),
                BorderThickness = new Thickness(1, 1, 1, s_Active ? 0 : 1),
                CornerRadius = new CornerRadius(3, 3, 0, 0),
                Padding = new Thickness(10, 4, 8, 4),
                Margin = new Thickness(0, 0, 2, 0),
                Child = s_Content,
                ToolTip = (i == m_ActiveTab ? Canvas.Graph : m_Tabs[i].Graph).TargetShader is { Length: > 0 } s_Target
                    ? s_Target
                    : "No target shader",
            };
            s_Tab.MouseLeftButtonDown += (_, _) =>
            {
                if (s_Index == m_ActiveTab)
                    return;

                SaveActiveTab();
                ActivateTab(s_Index);
            };
            s_Tab.MouseDown += (_, s_Args) =>
            {
                if (s_Args.ChangedButton != MouseButton.Middle)
                    return;

                s_Args.Handled = true;
                CloseTab(s_Index);
            };

            TabStrip.Children.Add(s_Tab);
        }
    }

    private void OnUndo(object p_Sender, ExecutedRoutedEventArgs p_Args) => DoUndo();
    private void OnRedo(object p_Sender, ExecutedRoutedEventArgs p_Args) => DoRedo();
    private void OnUndoClick(object p_Sender, RoutedEventArgs p_Args) => DoUndo();
    private void OnRedoClick(object p_Sender, RoutedEventArgs p_Args) => DoRedo();

    private void DoUndo()
    {
        if (!Canvas.Undo())
            Log("Nothing left to undo.");
    }

    private void DoRedo()
    {
        if (!Canvas.Redo())
            Log("Nothing left to redo.");
    }

    private void BuildProperties()
    {
        PropertiesPanel.Children.Clear();
        var s_Node = Canvas.Selected;

        if (s_Node == null)
        {
            PropertiesHeader.Text = "Properties";
            PropertiesPanel.Children.Add(new TextBlock { Text = "No node selected.", Margin = new Thickness(0, 4, 0, 0) });
            return;
        }

        // The header names the node the way the canvas draws it: a panel headed with the other vocabulary's
        // label reads as if it belonged to some other node than the one that is selected.
        var s_Selected = s_Node.Def.TitleFor(m_Settings.UdkNodeStyle);
        PropertiesHeader.Text = Canvas.SelectedCount > 1
            ? $"Properties — {s_Selected} ({Canvas.SelectedCount} selected)"
            : $"Properties — {s_Selected}";

        if (Canvas.SelectedCount > 1)
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = $"{Canvas.SelectedCount} nodes selected; editing the last one clicked. " +
                       "Delete removes all of them.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 10,
                Opacity = 0.75,
                Margin = new Thickness(0, 2, 0, 6),
            });

        if (s_Node.Def.Description.Length > 0)
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = s_Node.Def.Description,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Opacity = 0.85,
                Margin = new Thickness(0, 2, 0, 6),
            });

        // Which CLASS of knob this node is - the difference the game enforces: a baked value is compiled into
        // the shader and only reaches the game through a re-bake, an external one is instance data the game
        // feeds at runtime and varies with no rebake at all.
        var s_IsExternal = s_Node.Kind == "ExternalConstant";
        if (s_IsExternal || s_Node.Def.Params.Count > 0 || s_Node.Def.Inputs.Any(p_P => p_P.IsEditable))
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = s_IsExternal
                    ? "EXTERNAL — the game feeds this value per material instance/variation at runtime; " +
                      "varying it needs no re-bake."
                    : "BAKED — values here are compiled into the shader; the game only sees changes after " +
                      "Bake. The preview updates live.",
                Foreground = new SolidColorBrush(s_IsExternal
                    ? Color.FromRgb(150, 210, 150)
                    : Color.FromRgb(224, 192, 130)),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
            });

        // For an external parameter: what THIS instance actually feeds it, when the level's data says.
        if (s_Node.Kind == "ExternalConstant")
        {
            var s_Bare = s_Node.GetParam("Name") is var s_Full &&
                         s_Full.StartsWith("external_", StringComparison.Ordinal)
                ? s_Full["external_".Length..]
                : s_Node.GetParam("Name");

            var s_Instance = ReadSlotMapFull(SlotMapPath(TargetShaderBox.Text.Trim()))?.Variations is { } s_Vars &&
                             s_Vars.TryGetValue(m_Variation, out var s_Var) &&
                             s_Var.Values?.TryGetValue(s_Bare, out var s_Value) == true
                ? s_Value
                : null;

            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = s_Instance != null
                    ? $"Instance value: {s_Instance}"
                    : "No instance value in this level's data; the game feeds this at runtime.",
                FontSize = 10,
                Opacity = 0.8,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 4),
            });
        }

        foreach (var s_Param in s_Node.Def.Params)
        {
            PropertiesPanel.Children.Add(new TextBlock { Text = s_Param.Name, Margin = new Thickness(0, 8, 0, 2) });
            PropertiesPanel.Children.Add(BuildParamEditor(s_Node, s_Param));
        }

        BuildInputDefaults(s_Node);
        BuildCustomTexturePicker(s_Node);
        BuildVariationPicker(s_Node);

        var s_Unavailable = s_Node.Def.Inputs
            .Select(p_P => (Port: p_P, Reason: p_P.WhyUnavailable(s_Node)))
            .Where(p_P => p_P.Reason != null)
            .ToList();

        if (s_Unavailable.Count > 0)
        {
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = "Inputs currently ignored", Margin = new Thickness(0, 14, 0, 2), FontWeight = FontWeights.Bold,
            });

            foreach (var (s_Port, s_Reason) in s_Unavailable)
                PropertiesPanel.Children.Add(new TextBlock
                {
                    Text = $"{s_Port.Name}: {s_Reason}",
                    FontSize = 10,
                    Opacity = 0.75,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 3),
                });
        }

        if (s_Node.Def.Params.Count == 0 && s_Unavailable.Count == 0)
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = "This node has no parameters.", Margin = new Thickness(0, 4, 0, 0),
            });

        BuildDescriptionEditor(s_Node);
    }

    /// <summary>The node kinds that sample a texture register (single source of truth: the palette).</summary>
    internal static string[] TextureNodeKinds => Graph.Palette.TextureKinds;

    /// <summary>
    /// The variation section on the root node: pick an existing variation of the object to overwrite, or name
    /// a brand-new one — the bake then delivers the graph as that variation (nothing vanilla is replaced)
    /// instead of swapping the target shader's bytecode.
    /// </summary>
    private void BuildVariationPicker(GraphNode p_Node)
    {
        // ANY root carries the variation section — gating on specific kinds silently locked variations out
        // of every shader whose translation picked another lighting model (Metallic, Skin, DynamicEnvmap…),
        // which read as "only works for some objects" when the delivery mechanism is the same for all.
        if (!p_Node.Def.IsRoot)
            return;

        var s_Graph = Canvas.Graph;
        var s_Target = TargetShaderBox.Text.Trim();

        PropertiesPanel.Children.Add(new TextBlock
        {
            Text = "Variation", Margin = new Thickness(0, 14, 0, 2), FontWeight = FontWeights.Bold,
        });

        var s_Map = s_Target.Length > 0 ? ReadSlotMapFull(SlotMapPath(s_Target)) : null;
        var s_Existing = s_Map?.Variations?.Values
            .Select(p_V => p_V.AssetName)
            .Where(p_N => p_N is { Length: > 0 } && p_N != "(base)")
            .Cast<string>()
            .ToList() ?? new List<string>();

        // Variations the user has CREATED here accumulate per target, across graphs and sessions — the
        // dropdown is the one place they all live, and deleting one is an explicit act, not a side effect.
        var s_Custom = ReadCustomVariations(s_Target);
        var s_All = s_Existing.Concat(s_Custom)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p_N => p_N, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var s_Status = new TextBlock
        {
            FontSize = 10, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4),
        };

        // Anything that has to follow the chosen variation hangs off here, so a control added later cannot
        // be left behind by one of the several paths that change the selection.
        Action? s_AfterStatus = null;

        void ShowStatus()
        {
            s_Status.Text = s_Graph.BakeVariation == null
                ? "Bake replaces the target shader (no variation)."
                : $"Bake creates/overwrites the variation:\n{s_Graph.BakeVariation}";

            s_AfterStatus?.Invoke();
        }

        const string c_None = "(none — bake replaces the target)";
        const string c_New = "New variation…";

        var s_Box = new ComboBox { Margin = new Thickness(0, 2, 0, 2) };
        var s_Filling = false;
        var s_Delete = new Button
        {
            Content = "Delete variation", Margin = new Thickness(0, 2, 0, 2),
            Visibility = Visibility.Collapsed,
            ToolTip = "Removes the selected variation from this list (game-defined variations stay).",
        };

        // The delete button tracks the SELECTED item, not the click path: the programmatic fills (panel
        // build, Enter-commit) skip the SelectionChanged handler on purpose, so they must refresh it here.
        void UpdateDelete() => s_Delete.Visibility =
            s_Box.SelectedItem is string s_Sel && s_Custom.Contains(s_Sel, StringComparer.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;

        void FillBox(string? p_Select)
        {
            s_Filling = true;
            s_Box.Items.Clear();
            s_Box.Items.Add(c_None);
            foreach (var s_Name in s_All)
                s_Box.Items.Add(s_Name);
            s_Box.Items.Add(c_New);
            s_Box.SelectedItem = p_Select != null && s_Box.Items.Contains(p_Select) ? p_Select : c_None;
            s_Filling = false;
            UpdateDelete();
        }

        string SuggestName()
        {
            var s_Slash = s_Target.LastIndexOf('/');
            var s_Stem = s_Slash >= 0 ? s_Target[(s_Slash + 1)..] : s_Target;
            if (s_Stem.StartsWith("SS_", StringComparison.OrdinalIgnoreCase))
                s_Stem = s_Stem[3..];
            return s_Slash >= 0 ? $"{s_Target[..s_Slash]}/{s_Stem}_NEW" : $"{s_Stem}_NEW";
        }

        var s_NameBox = new TextBox
        {
            Margin = new Thickness(0, 2, 0, 2),
            Visibility = Visibility.Collapsed,
            ToolTip = "Full sibling path for the new variation — press Enter to add it to the list.",
        };

        s_Box.SelectionChanged += (_, _) =>
        {
            if (s_Filling)
                return;

            switch (s_Box.SelectedItem as string)
            {
                case c_None:
                    SwitchVariationDraft(null);
                    s_NameBox.Visibility = Visibility.Collapsed;
                    s_Delete.Visibility = Visibility.Collapsed;
                    break;
                case c_New:
                    s_NameBox.Visibility = Visibility.Visible;
                    s_Delete.Visibility = Visibility.Collapsed;
                    if (s_NameBox.Text.Trim().Length == 0)
                        s_NameBox.Text = SuggestName();
                    s_NameBox.Focus();
                    s_NameBox.SelectAll();
                    break;
                case { } s_Picked:
                    SwitchVariationDraft(s_Picked);
                    s_NameBox.Visibility = Visibility.Collapsed;
                    UpdateDelete();
                    break;
            }

            ShowStatus();
        };

        // Enter commits the typed name: it joins the list, gets selected, and stays for every future graph
        // on this target — "new" is how the list GROWS, not a transient text field.
        s_NameBox.KeyDown += (_, p_Args) =>
        {
            if (p_Args.Key != System.Windows.Input.Key.Enter || s_NameBox.Text.Trim().Length == 0)
                return;

            var s_Name = s_NameBox.Text.Trim();
            if (!s_Custom.Contains(s_Name, StringComparer.OrdinalIgnoreCase))
            {
                s_Custom.Add(s_Name);
                SaveCustomVariations(s_Target, s_Custom);
            }

            if (!s_All.Contains(s_Name, StringComparer.OrdinalIgnoreCase))
                s_All.Add(s_Name);

            s_All.Sort(StringComparer.OrdinalIgnoreCase);
            SwitchVariationDraft(s_Name);
            s_NameBox.Visibility = Visibility.Collapsed;
            FillBox(s_Name);
            ShowStatus();
        };

        s_Delete.Click += (_, _) =>
        {
            if (s_Box.SelectedItem is not string s_Victim || !s_Custom.Remove(s_Victim))
                return;

            SaveCustomVariations(s_Target, s_Custom);
            s_All.RemoveAll(p_N => p_N.Equals(s_Victim, StringComparison.OrdinalIgnoreCase));
            if (s_Graph.BakeVariation?.Equals(s_Victim, StringComparison.OrdinalIgnoreCase) == true)
                s_Graph.BakeVariation = null;

            s_Delete.Visibility = Visibility.Collapsed;
            FillBox(null);
            ShowStatus();
        };

        // Sits under the dropdown because it only means anything once a variation is picked: a variation
        // nothing points at leaves the map looking untouched, and that is the surprise this box removes.
        var s_Force = new CheckBox
        {
            Content = new TextBlock
            {
                Text = "Use this on the objects already in the level",
                TextWrapping = TextWrapping.Wrap,
            },
            Margin = new Thickness(0, 6, 0, 0),
            IsChecked = s_Graph.ApplyVariationToLevel,
            ToolTip = "The mod repoints the entry those copies ALREADY use at this shader, so every one " +
                      "of them in the map renders with it. Nothing vanilla is replaced: the original shader " +
                      "stays in its own database under its own key and the mod only ships its own small one, " +
                      "so removing the mod puts the map back exactly as it was. Untick it for a variation you " +
                      "mean to place by hand instead — then it ships as an extra variation and the map is " +
                      "left alone.",
        };

        var s_Syncing = false;
        s_Force.Checked += (_, _) =>
        {
            if (!s_Syncing) { s_Graph.ApplyVariationToLevel = true; TouchGraph(); }
        };
        s_Force.Unchecked += (_, _) =>
        {
            if (!s_Syncing) { s_Graph.ApplyVariationToLevel = false; TouchGraph(); }
        };

        // The state has to follow the dropdown, so the box is never armed on a bake that has no variation
        // to point anything at — and says why instead of just going grey.
        //
        // ⚠ It also has to come out UNTICKED there: a greyed box still showing a tick reads as "this will
        // happen anyway". The tick is cleared for the eye only — the graph keeps the user's choice, so
        // picking a variation again brings it back instead of silently resetting to the default.
        void RefreshForce()
        {
            var s_Live = s_Graph.BakeVariation != null;
            s_Force.IsEnabled = s_Live;

            s_Syncing = true;
            s_Force.IsChecked = s_Live && s_Graph.ApplyVariationToLevel;
            s_Syncing = false;

            // ⛔ WHETHER THIS BOX IS NEEDED IS NOT A MATTER OF TASTE, AND THE ANSWER WAS NOWHERE ON SCREEN.
            // It comes down to one measured number: how many meshes in the level wear the target shader. Worn
            // by ONE, a plain replacement changes that object and nothing else, and this box is beside the
            // point. Worn by many, a replacement repaints every one of them — the only way to change a single
            // object is a variation, which is keyed by the MESH, with this ticked. The old caption said "a
            // replacement already changes what those objects render", which is true and, on a shared preset,
            // exactly the sentence that leads to repainting half a map.
            var s_Users = s_Map?.MeshUsers;

            // ⛔ THE MAP THE NUMBER CAME FROM IS PART OF THE NUMBER. A shader dresses different objects in
            // every map, and the one measured here is whichever map the target names — or a default when its
            // name carries none. Printed bare it reads as a fact about the game; named, it is a fact about a
            // map, and the bake is what settles it for the maps actually being built.
            var s_Where = s_Map?.MeshUsersScope is { Length: > 0 } s_Scope
                ? s_Scope.Split('/').Last().ToUpperInvariant()
                : "that map";

            var s_Hint = s_Users switch
            {
                null => "  (how many objects share this shader has not been measured yet — press \"load " +
                        "target textures\"; the bake checks it for the maps you build)",
                <= 1 => $"  (in {s_Where} only this object wears the shader, so a plain replacement already " +
                        "changes it — other maps may differ, and the bake checks the ones you build)",
                _ => $"  ({s_Users} different meshes wear this shader in {s_Where} — a replacement would " +
                     "repaint ALL of them, so aiming at one object needs a variation with this ticked)",
            };

            ((TextBlock) s_Force.Content).Text = (s_Live
                ? "Use this on the objects already in the level"
                : "Use on the level's objects — needs a variation") + s_Hint;
        }

        s_AfterStatus = RefreshForce;

        FillBox(s_Graph.BakeVariation);
        PropertiesPanel.Children.Add(s_Box);
        PropertiesPanel.Children.Add(s_NameBox);
        PropertiesPanel.Children.Add(s_Delete);
        ShowStatus();
        PropertiesPanel.Children.Add(s_Status);
        PropertiesPanel.Children.Add(s_Force);
    }

    /// <summary>
    /// Per-variation WORK IN PROGRESS: switching the dropdown must not lose edits, so the canvas is
    /// snapshotted under the variation it was showing and restored when that variation is picked again.
    /// Keyed by target|variation ("" = the no-variation/base document). Save writes every draft out.
    /// </summary>
    private readonly Dictionary<string, string> m_VariationDrafts = new(StringComparer.OrdinalIgnoreCase);

    private string DraftKey(string p_Target, string? p_Variation) => $"{p_Target}|{p_Variation ?? ""}";

    private void SnapshotVariationDraft()
    {
        var s_Target = TargetShaderBox.Text.Trim();
        if (s_Target.Length > 0)
            m_VariationDrafts[DraftKey(s_Target, Canvas.Graph.BakeVariation)] = Canvas.Graph.ToJson();
    }

    /// <summary>Swaps the canvas to the chosen variation's draft; a variation touched for the FIRST time
    /// inherits a copy of what is on screen (the new variation starts from the current look).</summary>
    private void SwitchVariationDraft(string? p_Variation)
    {
        // Same variation = nothing to do. Without this, the combo's PROGRAMMATIC fill re-enters here,
        // and each round-trip through the panel rebuild kept stacking scaffold roots onto the canvas.
        if (string.Equals(Canvas.Graph.BakeVariation ?? "", p_Variation ?? "", StringComparison.OrdinalIgnoreCase))
            return;

        var s_Target = TargetShaderBox.Text.Trim();
        SnapshotVariationDraft();

        if (m_VariationDrafts.TryGetValue(DraftKey(s_Target, p_Variation), out var s_Json))
        {
            // The tab-switch pattern on purpose: swap the canvas document directly. LoadGraph also rewrites
            // the target box, and THAT path can answer with a scaffold graph — the infinite-roots bug.
            var s_Graph = ShaderGraph.FromJson(s_Json);
            s_Graph.BakeVariation = p_Variation;
            Canvas.Graph = s_Graph;
            SchedulePreview();
        }
        else
        {
            Canvas.Graph.BakeVariation = p_Variation;
        }
    }

    /// <summary>Every OTHER draft of the current target as a full graph, ready to embed or to bake.</summary>
    private List<ShaderGraph>? CollectVariationDrafts(bool p_ExcludeActive)
    {
        var s_Target = TargetShaderBox.Text.Trim();
        var s_Result = new List<ShaderGraph>();

        foreach (var (s_Key, s_Json) in m_VariationDrafts)
        {
            var s_Cut = s_Key.LastIndexOf('|');
            var s_KeyVariation = s_Key[(s_Cut + 1)..];

            if (s_KeyVariation.Length == 0 ||
                !s_Key[..s_Cut].Equals(s_Target, StringComparison.OrdinalIgnoreCase) ||
                (p_ExcludeActive &&
                 s_KeyVariation.Equals(Canvas.Graph.BakeVariation ?? "", StringComparison.OrdinalIgnoreCase)))
                continue;

            var s_Draft = ShaderGraph.FromJson(s_Json);
            s_Draft.BakeVariation = s_KeyVariation;
            s_Draft.Variations = null;
            s_Result.Add(s_Draft);
        }

        return s_Result.Count > 0 ? s_Result : null;
    }

    /// <summary>
    /// Moves a master file's embedded variations into working drafts (and their names into the dropdown's
    /// persisted list), returning the graph stripped — the canvas edits one look at a time.
    /// </summary>
    private void AbsorbEmbeddedVariations(ShaderGraph p_Graph)
    {
        if (p_Graph.Variations is not { Count: > 0 } s_Embedded)
            return;

        var s_Target = p_Graph.TargetShader.Trim();
        var s_Custom = ReadCustomVariations(s_Target);
        var s_Added = 0;

        foreach (var s_Variation in s_Embedded)
        {
            if (s_Variation.BakeVariation is not { Length: > 0 } s_Name)
                continue;

            s_Variation.Variations = null;
            m_VariationDrafts[DraftKey(s_Target, s_Name)] = s_Variation.ToJson();
            if (!s_Custom.Contains(s_Name, StringComparer.OrdinalIgnoreCase))
            {
                s_Custom.Add(s_Name);
                s_Added++;
            }
        }

        if (s_Added > 0)
            SaveCustomVariations(s_Target, s_Custom);

        Log($"{s_Embedded.Count} embedded variation(s) unpacked from the file.");
        p_Graph.Variations = null;
    }

    /// <summary>User-created variation names, per target, surviving restarts and shared by every graph.</summary>
    private string CustomVariationsPath(string p_Target) =>
        Path.Combine(TexCacheDir, $"{Sanitize(p_Target)}.variations.json");

    private List<string> ReadCustomVariations(string p_Target)
    {
        try
        {
            if (p_Target.Length > 0 && File.Exists(CustomVariationsPath(p_Target)))
                return System.Text.Json.JsonSerializer.Deserialize<List<string>>(
                    File.ReadAllText(CustomVariationsPath(p_Target))) ?? new List<string>();
        }
        catch
        {
            // An unreadable list only costs re-typing a name; it must never take the panel down.
        }

        return new List<string>();
    }

    private void SaveCustomVariations(string p_Target, List<string> p_Names)
    {
        try
        {
            Directory.CreateDirectory(TexCacheDir);
            File.WriteAllText(CustomVariationsPath(p_Target),
                System.Text.Json.JsonSerializer.Serialize(p_Names));
        }
        catch
        {
            // Losing the persisted list is recoverable; failing the click is not.
        }
    }

    /// <summary>
    /// The per-node texture picker (the Unreal-style "choose the texture" affordance). The chosen file shows
    /// LIVE in the preview, and Bake converts it to the engine's format and ships it in the mod's bundle as a
    /// same-name override of whatever game texture this register binds.
    /// </summary>
    private void BuildCustomTexturePicker(GraphNode p_Node)
    {
        if (!TextureNodeKinds.Contains(p_Node.Kind))
            return;

        var s_Current = p_Node.Params.TryGetValue("CustomTexture", out var s_Path) ? s_Path : "";

        // The name BESIDE the header, resolved live: the chosen file's name, else the game texture this
        // register binds in the set being previewed (the slot map names it), else nothing is known yet.
        // GetParam, never the raw Params dict — a fresh node's register is the palette default.
        var s_NameRegister = p_Node.GetParam("Register");
        var s_GameName = !string.IsNullOrWhiteSpace(s_NameRegister) &&
                         ReadSlotMapFull(SlotMapPath(TargetShaderBox.Text.Trim())) is { } s_NameMap
            ? (s_NameMap.Variations != null &&
               s_NameMap.Variations.TryGetValue(m_Variation, out var s_NameSet) &&
               s_NameSet.Slots.TryGetValue(s_NameRegister, out var s_VariationName)
                  ? s_VariationName
                  : s_NameMap.Slots.GetValueOrDefault(s_NameRegister))
            : null;

        var s_ShownName = s_Current.Length > 0
            ? Path.GetFileNameWithoutExtension(s_Current)
            : s_GameName?.Split('/').Last();

        // Read-only borderless TextBoxes, not TextBlocks: the name and the path are exactly the strings a
        // user copies (to search the dump, to name an override), and a TextBlock cannot be selected.
        static TextBox SelectableText(string p_Text, double p_FontSize, double p_Opacity, object? p_Tip) => new()
        {
            Text = p_Text,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            FontSize = p_FontSize,
            Opacity = p_Opacity,
            TextWrapping = TextWrapping.Wrap,
            ToolTip = p_Tip,
            Cursor = Cursors.IBeam,
        };

        var s_Header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 2) };
        s_Header.Children.Add(new TextBlock { Text = "Texture", FontWeight = FontWeights.Bold });
        if (s_ShownName != null)
        {
            var s_NameBox = SelectableText(s_ShownName, 11, 0.7, s_Current.Length > 0 ? s_Current : s_GameName);
            s_NameBox.Margin = new Thickness(8, 1, 0, 0);
            s_Header.Children.Add(s_NameBox);
        }
        PropertiesPanel.Children.Add(s_Header);

        // Whether the loaded slot map knows this register at all decides the honest message: an unbound
        // register previews fine, but a same-name override has no name to override at bake time.
        var s_MapLoaded = ReadSlotMapFull(SlotMapPath(TargetShaderBox.Text.Trim())) != null;

        var s_PathBox = SelectableText(
            s_Current.Length > 0
                ? "custom: " + Path.GetFileName(s_Current) + (File.Exists(s_Current) ? "" : "  (FILE MISSING)")
                : s_GameName ?? (s_MapLoaded
                    ? "(new slot — the game material does not bind this register; a custom texture here is " +
                      "shipped as a brand-new texture and the shader's constants gain the slot at bake)"
                    : "(no game texture named for this register yet — press 'Reload from game')"),
            10, 0.7, s_Current.Length > 0 ? s_Current : s_GameName);
        s_PathBox.Margin = new Thickness(0, 0, 0, 3);
        PropertiesPanel.Children.Add(s_PathBox);

        // The thumbnail sits BESIDE the buttons and always shows what the register currently wears: the
        // chosen file when there is one, else the game's own art — one glance answers "which texture is this".
        var s_ThumbSource = s_Current.Length > 0
            ? LoadCustomTextureImage(s_Current)
            : !string.IsNullOrWhiteSpace(s_NameRegister) &&
              m_TexturePreviews.TryGetValue(s_NameRegister, out var s_GameThumb)
                ? s_GameThumb
                : null;

        var s_Buttons = new StackPanel { Orientation = Orientation.Horizontal };

        if (s_ThumbSource != null)
            s_Buttons.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(84, 84, 90)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 8, 0),
                Child = new Image
                {
                    Source = s_ThumbSource,
                    Width = 48,
                    Height = 48,
                    Stretch = Stretch.Uniform,
                },
                ToolTip = s_Current.Length > 0 ? s_Current : "The game texture bound to this register",
            });

        // Where Browse opens: the CURRENT texture's own folder, custom or vanilla alike — the vanilla art
        // lives as the decoded PNG in the texture cache, so "grab the original and tweak it" starts with the
        // file already under the cursor. Only with nothing at all does the dialog fall back to its default.
        var s_BrowseFrom = s_Current.Length > 0 && File.Exists(s_Current)
            ? s_Current
            : (s_ThumbSource as BitmapImage)?.UriSource is { IsFile: true } s_CacheUri &&
              File.Exists(s_CacheUri.LocalPath)
                ? s_CacheUri.LocalPath
                : null;

        var s_Browse = new Button
        {
            Content = "Browse…",
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        s_Browse.Click += (_, _) =>
        {
            var s_Dialog = new OpenFileDialog
            {
                Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.dds)|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.dds|" +
                         "All files (*.*)|*.*",
            };

            if (s_BrowseFrom != null)
            {
                s_Dialog.InitialDirectory = Path.GetDirectoryName(s_BrowseFrom);
                s_Dialog.FileName = Path.GetFileName(s_BrowseFrom);
            }

            if (s_Dialog.ShowDialog() != true)
                return;

            Canvas.PushUndo();
            p_Node.Params["CustomTexture"] = s_Dialog.FileName;
            ApplyCustomTextureOverrides();
            BuildProperties();
        };
        s_Buttons.Children.Add(s_Browse);

        // Export writes the texture the register currently WEARS wherever the user points — the raw DDS for a
        // vanilla dump, or converted to PNG when Settings say so (the ready-to-edit lane). Grayed with a
        // reason when there is nothing on disk to export yet.
        var s_Export = new Button
        {
            Content = "Export…",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = s_BrowseFrom != null,
            ToolTip = s_BrowseFrom != null
                ? "Save this texture to a folder of your choice" +
                  (m_Settings.ExportTexturesAsPng ? " (as PNG — see Settings > Export)" : " (as-is — see Settings > Export)")
                : "Nothing to export yet — load the target's textures first.",
        };
        s_Export.Click += (_, _) => ExportTexture(s_BrowseFrom!, s_ShownName ?? "texture");
        s_Buttons.Children.Add(s_Export);

        if (s_Current.Length > 0)
        {
            var s_Clear = new Button
            {
                Content = "Clear",
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(6, 0, 0, 0),
            };
            s_Clear.Click += (_, _) =>
            {
                Canvas.PushUndo();
                p_Node.Params.Remove("CustomTexture");

                // The vanilla art comes back from the disk cache; with no cache yet, the register simply
                // returns to the placeholder on the next texture load.
                if (LoadCachedPreviews() == 0 && p_Node.Params.TryGetValue("Register", out var s_Register))
                    m_TexturePreviews.Remove(s_Register);

                ApplyCustomTextureOverrides();
                BuildProperties();
            };
            s_Buttons.Children.Add(s_Clear);
        }

        PropertiesPanel.Children.Add(s_Buttons);

        PropertiesPanel.Children.Add(new TextBlock
        {
            Text = "Shows live in the preview. On Bake it is converted to the engine's texture format and " +
                   "shipped in the mod bundle, overriding the game texture of the same name.",
            FontSize = 10,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
        });
    }

    /// <summary>
    /// Saves the texture a register currently wears wherever the user points. Settings decide the format:
    /// PNG (converted if needed — the ready-to-edit lane) or the file exactly as it is, which for game art
    /// means the raw DDS dump sitting next to the cache's preview PNG.
    /// </summary>
    private void ExportTexture(string p_SourcePath, string p_Name)
    {
        var s_AsPng = m_Settings.ExportTexturesAsPng;

        var s_Source = p_SourcePath;
        if (!s_AsPng && s_Source.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            // The cache keeps the decoded PNG for previews AND the faithful DDS dump beside it.
            var s_Dds = Path.ChangeExtension(s_Source, ".dds");
            if (File.Exists(s_Dds))
                s_Source = s_Dds;
        }

        var s_Extension = s_AsPng ? ".png" : Path.GetExtension(s_Source);
        var s_Dialog = new SaveFileDialog
        {
            FileName = p_Name + s_Extension,
            Filter = s_AsPng
                ? "PNG image (*.png)|*.png"
                : $"Texture (*{s_Extension})|*{s_Extension}|All files (*.*)|*.*",
        };

        if (s_Dialog.ShowDialog() != true)
            return;

        try
        {
            if (s_AsPng && !s_Source.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                if (LoadCustomTextureImage(s_Source) is not BitmapSource s_Image)
                {
                    Log($"ERROR: '{Path.GetFileName(s_Source)}' could not be decoded for PNG export.");
                    return;
                }

                var s_Encoder = new PngBitmapEncoder();
                s_Encoder.Frames.Add(BitmapFrame.Create(s_Image));
                using var s_Stream = File.Create(s_Dialog.FileName);
                s_Encoder.Save(s_Stream);
            }
            else
            {
                File.Copy(s_Source, s_Dialog.FileName, true);
            }

            Log($"Exported '{p_Name}' -> {s_Dialog.FileName}");
        }
        catch (Exception s_Exception)
        {
            Log($"ERROR exporting texture: {s_Exception.Message}");
        }
    }

    /// <summary>Decodes a user image for the preview: WPF codecs for the common formats, our decoder for DDS.</summary>
    private static ImageSource? LoadCustomTextureImage(string p_Path)
    {
        try
        {
            if (!File.Exists(p_Path))
                return null;

            if (p_Path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                return DdsImage.Load(p_Path);

            var s_Bitmap = new BitmapImage();
            s_Bitmap.BeginInit();
            // ⛔ WPF caches decoded bitmaps PER URI for the process lifetime: without IgnoreImageCache, a file
            // the user re-saved (or re-picked) kept showing its FIRST version until the editor restarted.
            s_Bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            s_Bitmap.CacheOption = BitmapCacheOption.OnLoad;
            s_Bitmap.UriSource = new Uri(p_Path);
            s_Bitmap.EndInit();
            s_Bitmap.Freeze();
            return s_Bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Feeds every texture node's chosen custom file into the thumbnail dictionary and the live preview,
    /// AFTER the cache-derived art so the user's choice wins. Idempotent — every path that repopulates the
    /// thumbnails ends by calling this.
    /// </summary>
    private void ApplyCustomTextureOverrides()
    {
        foreach (var s_Node in Canvas.Graph.Nodes)
        {
            if (!TextureNodeKinds.Contains(s_Node.Kind) ||
                !s_Node.Params.TryGetValue("CustomTexture", out var s_Path) || s_Path.Length == 0)
                continue;

            // ⛔ GetParam, not the raw dictionary: a FRESHLY PLACED node has an EMPTY Params dict and its
            // register lives in the palette DEFAULT — the raw read skipped new nodes in silence, which is
            // exactly "I chose a custom texture and nothing happened".
            var s_Register = s_Node.GetParam("Register");
            if (string.IsNullOrWhiteSpace(s_Register))
                continue;

            if (LoadCustomTextureImage(s_Path) is not BitmapSource s_Image)
            {
                Log($"Custom texture '{Path.GetFileName(s_Path)}' could not be read; register t{s_Register} " +
                    "keeps the game art.");
                continue;
            }

            m_TexturePreviews[s_Register] = s_Image;

            if (m_Preview.Ready && int.TryParse(s_Register, out var s_Slot))
            {
                var s_Converted = new FormatConvertedBitmap(s_Image, PixelFormats.Bgra32, null, 0);
                var s_Pixels = new uint[s_Converted.PixelWidth * s_Converted.PixelHeight];
                s_Converted.CopyPixels(s_Pixels, s_Converted.PixelWidth * 4, 0);
                m_Preview.SetTexture(s_Slot, s_Pixels, s_Converted.PixelWidth, s_Converted.PixelHeight);
            }
        }

        Canvas.InvalidateVisual();
    }

    /// <summary>
    /// The free-text note on a node, shown as a bubble above it on the canvas. Saved with the graph; it never
    /// reaches the emitted shader.
    /// </summary>
    private void BuildDescriptionEditor(GraphNode p_Node)
    {
        PropertiesPanel.Children.Add(new TextBlock
        {
            Text = "Description",
            Margin = new Thickness(0, 16, 0, 2),
            FontWeight = FontWeights.Bold,
        });

        var s_Box = new TextBox
        {
            Text = p_Node.Comment ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinLines = 2,
            Tag = "node:Description",
        };

        static string? AsComment(string p_Text) => p_Text.Trim().Length > 0 ? p_Text : null;

        var s_Original = s_Box.Text;
        s_Box.TextChanged += (_, _) =>
        {
            Live(p_Node).Comment = AsComment(s_Box.Text);
            TouchGraph();
        };

        s_Box.GotKeyboardFocus += (_, _) => s_Original = s_Box.Text;
        s_Box.LostKeyboardFocus += (_, _) =>
        {
            if (s_Box.Text == s_Original)
                return;

            // Same undo dance as the input-default editors: one undo step per edit session, not per keystroke.
            var s_New = s_Box.Text;
            Live(p_Node).Comment = AsComment(s_Original);
            Canvas.PushUndo();
            Live(p_Node).Comment = AsComment(s_New);
            s_Original = s_New;
        };

        PropertiesPanel.Children.Add(s_Box);
    }

    /// <summary>
    /// Editors for the constants that feed unconnected inputs. Without these the literals baked into each node
    /// are unreachable — you would have to wire a Scalar node just to change a multiplier.
    /// </summary>
    private void BuildInputDefaults(GraphNode p_Node)
    {
        var s_Editable = p_Node.Def.Inputs.Where(p_P => p_P.IsEditable).ToList();
        if (s_Editable.Count == 0)
            return;

        PropertiesPanel.Children.Add(new TextBlock
        {
            Text = "Input values (used while unconnected)",
            Margin = new Thickness(0, 16, 0, 2),
            FontWeight = FontWeights.Bold,
        });

        foreach (var s_Port in s_Editable)
        {
            var s_Connected = Canvas.Graph.ConnectionInto(p_Node.Id, s_Port.Name) != null;
            var s_Components = s_Port.Type.ComponentCount();

            var s_Label = s_Connected
                ? $"{s_Port.Name}  (driven by a wire)"
                : s_Port.DefaultIsExpression
                    ? $"{s_Port.Name}  ({s_Components} × float, default {s_Port.Default})"
                    : $"{s_Port.Name}  ({s_Components} × float)";

            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = s_Label,
                Margin = new Thickness(0, 6, 0, 2),
                FontSize = 11,
                Opacity = s_Connected ? 0.5 : 1.0,
            });

            var s_Box = SelectAllOnFocus(new TextBox
            {
                // Expression defaults start blank: prefilling them would feed the expression text back as a
                // value on the first TextChanged. Blank means "keep the default".
                Text = Live(p_Node).GetInputOverride(s_Port.Name)
                       ?? (s_Port.DefaultIsExpression ? "" : s_Port.DefaultAsText()),
                IsEnabled = !s_Connected,
            });

            var s_Original = s_Box.Text;
            s_Box.TextChanged += (_, _) =>
            {
                Live(p_Node).SetInputOverride(s_Port.Name, s_Box.Text);
                TouchGraph();
            };

            s_Box.GotKeyboardFocus += (_, _) => s_Original = s_Box.Text;
            s_Box.LostKeyboardFocus += (_, _) =>
            {
                if (s_Box.Text == s_Original)
                    return;

                var s_New = s_Box.Text;
                Live(p_Node).SetInputOverride(s_Port.Name, s_Original);
                Canvas.PushUndo();
                Live(p_Node).SetInputOverride(s_Port.Name, s_New);
                s_Original = s_New;

                // Surface a bad value straight away rather than at emit time.
                if (PortTypeUtils.FormatLiteral(s_New, s_Port.Type) == null && s_New.Trim().Length > 0)
                    Log($"'{s_Port.Name}' needs 1 or {s_Components} numbers — '{s_New}' will fall back to the default.");
            };

            PropertiesPanel.Children.Add(s_Box);
        }
    }

    /// <summary>
    /// Swatch plus four numeric fields. The dialog is there for picking a colour by eye, the fields because a
    /// shader value is not limited to 0..1 — an emissive colour above 1 is legitimate and the dialog would
    /// clamp it away.
    /// </summary>
    /// <summary>
    /// Resolves a node by id against the LIVE graph. Property editors capture a node object, but undo, redo and
    /// loading a graph rebuild every node from JSON, so a captured reference can end up writing into an orphan
    /// that nothing renders or emits.
    /// </summary>
    private GraphNode Live(GraphNode p_Node) => Canvas.Graph.FindNode(p_Node.Id) ?? p_Node;

    private FrameworkElement BuildColorEditor(GraphNode p_Captured, ParamDef p_Param)
    {
        var s_Root = new StackPanel();
        var s_Row = new StackPanel { Orientation = Orientation.Horizontal };
        var s_Swatch = new System.Windows.Shapes.Rectangle
        {
            Width = 34, Height = 20, Stroke = Brushes.Black, StrokeThickness = 1,
            Margin = new Thickness(0, 0, 6, 0),
        };

        var s_Fields = new TextBox[4];
        var s_Names = new[] { "R", "G", "B", "A" };

        float[] Read()
        {
            var s_Parts = Live(p_Captured).GetParam(p_Param.Name)
                .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var s_Values = new[] { 1f, 1f, 1f, 1f };
            for (var i = 0; i < 4 && i < s_Parts.Length; i++)
                if (float.TryParse(s_Parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var s_V))
                    s_Values[i] = s_V;

            return s_Values;
        }

        void Paint()
        {
            var s_Values = Read();
            byte Channel(float p_V) => (byte) Math.Clamp(p_V * 255f, 0f, 255f);
            s_Swatch.Fill = new SolidColorBrush(Color.FromRgb(
                Channel(s_Values[0]), Channel(s_Values[1]), Channel(s_Values[2])));
        }

        var s_Suppress = false;

        void Write(float[] p_Values, bool p_Undo)
        {
            if (p_Undo)
                Canvas.PushUndo();

            Live(p_Captured).Params[p_Param.Name] = string.Join(",",
                p_Values.Select(p_V => p_V.ToString("0.0######", CultureInfo.InvariantCulture)));

            // The fields are written back too, and that must not re-enter through their own change handler.
            s_Suppress = true;
            for (var i = 0; i < 4; i++)
                if (s_Fields[i] != null)
                    s_Fields[i].Text = p_Values[i].ToString("0.0######", CultureInfo.InvariantCulture);

            s_Suppress = false;

            Paint();
            TouchGraph();
        }

        // Tagged so the self-test can find this exact button in the panel instead of guessing at an index.
        var s_Pick = new Button
        {
            Content = "Pick…", Padding = new Thickness(6, 1, 6, 1), Tag = "pick:" + p_Param.Name,
        };

        s_Pick.Click += (_, _) =>
        {
            var s_Picked = AskForColour(Read());
            if (s_Picked == null)
            {
                Log("Colour picker cancelled; nothing changed.");
                return;
            }

            Write(s_Picked, true);

            // Reported so a value that does not land is visible instead of being guessed at.
            Log($"Colour picked -> {p_Param.Name} = {Live(p_Captured).GetParam(p_Param.Name)}");
        };

        s_Row.Children.Add(s_Swatch);
        s_Row.Children.Add(s_Pick);
        s_Root.Children.Add(s_Row);

        var s_Grid = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        for (var i = 0; i < 4; i++)
        {
            var s_Index = i;
            var s_Box = SelectAllOnFocus(new TextBox { Width = 46, Margin = new Thickness(0, 0, 4, 0) });
            s_Fields[i] = s_Box;

            // Commit as it is typed, so both paths into the value behave the same. Committing only on focus
            // loss made the two routes asymmetric, which is what made the dialog look broken.
            s_Box.TextChanged += (_, _) =>
            {
                if (s_Suppress)
                    return;

                if (!float.TryParse(s_Box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var s_V))
                    return;

                var s_Values = Read();
                s_Values[s_Index] = s_V;

                // No undo snapshot per keystroke; that is taken when the field is entered.
                Write(s_Values, false);
            };

            s_Box.GotKeyboardFocus += (_, _) => Canvas.PushUndo();

            s_Grid.Children.Add(new TextBlock
            {
                Text = s_Names[i], Width = 12, VerticalAlignment = VerticalAlignment.Center, FontSize = 10,
            });
            s_Grid.Children.Add(s_Box);
        }

        s_Root.Children.Add(s_Grid);
        s_Root.Children.Add(new TextBlock
        {
            Text = "Values may exceed 1 (e.g. emissive).", FontSize = 10, Opacity = 0.7,
            Margin = new Thickness(0, 2, 0, 0),
        });

        Write(Read(), false);
        return s_Root;
    }

    /// <summary>
    /// Drives the Description field the way a user does: select a node, type into the panel's real TextBox, and
    /// read the model back. Typed text must land on the node, the graph file must keep it, blank must remove it.
    /// </summary>
    internal static int DescriptionSelfTest()
    {
        var s_Window = new MainWindow();
        s_Window.Canvas.Graph = new ShaderGraph();
        s_Window.Canvas.AddNodeAt("Color", new Point(0, 0));

        var s_Node = s_Window.Canvas.Selected;
        if (s_Node == null)
        {
            Console.Error.WriteLine("DESCTEST: adding a node did not leave it selected.");
            return 1;
        }

        var s_Box = Descendants<TextBox>(s_Window.PropertiesPanel)
            .FirstOrDefault(p_B => p_B.Tag as string == "node:Description");

        if (s_Box == null)
        {
            Console.Error.WriteLine("DESCTEST: no Description box in the properties panel.");
            return 1;
        }

        s_Box.Text = "Comment Test";
        var s_Typed = s_Window.Canvas.Graph.FindNode(s_Node.Id)?.Comment;
        Console.Out.WriteLine($"after typing: Comment = '{s_Typed}'");

        var s_Reloaded = ShaderGraph.FromJson(s_Window.Canvas.Graph.ToJson()).FindNode(s_Node.Id)?.Comment;
        Console.Out.WriteLine($"round-trip:   Comment = '{s_Reloaded}'");

        s_Box.Text = "  ";
        var s_Cleared = s_Window.Canvas.Graph.FindNode(s_Node.Id)?.Comment;
        Console.Out.WriteLine($"after clear:  Comment = {(s_Cleared == null ? "null" : $"'{s_Cleared}'")}");

        // The knob-class line: a Color's values are BAKED, an ExternalConstant is runtime instance data.
        var s_Baked = Descendants<TextBlock>(s_Window.PropertiesPanel)
            .Any(p_T => p_T.Text.StartsWith("BAKED", StringComparison.Ordinal));

        s_Window.Canvas.AddNodeAt("ExternalConstant", new Point(220, 0));
        var s_External = Descendants<TextBlock>(s_Window.PropertiesPanel)
            .Any(p_T => p_T.Text.StartsWith("EXTERNAL", StringComparison.Ordinal));

        Console.Out.WriteLine($"knob class lines (BAKED on Color, EXTERNAL on ExternalConstant): " +
                              $"{(s_Baked && s_External ? "OK" : "FAIL")}");

        var s_Ok = s_Typed == "Comment Test" && s_Reloaded == "Comment Test" && s_Cleared == null &&
                   s_Baked && s_External;
        Console.Out.WriteLine(s_Ok
            ? "DESCTEST: PASS - typed text reached the model, the file keeps it, blank removes it."
            : "DESCTEST: FAIL");
        return s_Ok ? 0 : 1;
    }

    /// <summary>
    /// The modal picker is the only step of the Pick path that cannot be driven from the command line, so it sits
    /// behind this hook. In a normal run the hook is null and the real picker opens; the self-test swaps it and
    /// everything else — Read, PushUndo, the write, the field write-back and TouchGraph — runs for real.
    /// Returns null for "cancelled".
    /// </summary>
    internal static Func<float[], float[]?>? ColourPickerHook;

    private float[]? AskForColour(float[] p_Seed) =>
        ColourPickerHook != null ? ColourPickerHook(p_Seed) : ColourPicker.Pick(this, p_Seed);

    /// <summary>
    /// Drives the whole "Pick a colour" path on a node created exactly the way the palette creates one, with no
    /// window shown. keku reported the dialog not applying on a FRESH node while working after one manual edit;
    /// the only way to separate "the write never happens" from "the write happens and is not shown" is to run the
    /// real path and read the model AND the fields back.
    /// </summary>
    internal static int ColourPickSelfTest(bool p_EditFirst)
    {
        var s_Window = new MainWindow();
        s_Window.Canvas.Graph = new ShaderGraph();

        // The same entry point the palette drag, the search popup and the letter shortcuts all use.
        s_Window.Canvas.AddNodeAt("Color", new Point(0, 0));

        var s_Node = s_Window.Canvas.Selected;
        if (s_Node == null || s_Node.Kind != "Color")
        {
            Console.Error.WriteLine("COLORTEST: adding a Color node did not leave it selected.");
            return 1;
        }

        Console.Out.WriteLine($"after add:    Value = {s_Node.GetParam("Value")}");

        var s_Fields = Descendants<TextBox>(s_Window.PropertiesPanel).ToList();
        if (s_Fields.Count < 4)
        {
            Console.Error.WriteLine($"COLORTEST: expected 4 RGBA fields in the panel, found {s_Fields.Count}.");
            return 1;
        }

        if (p_EditFirst)
        {
            // The manual edit keku says "unlocks" the dialog.
            s_Fields[0].Text = "0.25";
            Console.Out.WriteLine($"after typing: Value = {s_Window.Canvas.Selected!.GetParam("Value")}");
        }

        var s_Button = Descendants<Button>(s_Window.PropertiesPanel)
            .FirstOrDefault(p_B => p_B.Tag as string == "pick:Value");

        if (s_Button == null)
        {
            Console.Error.WriteLine("COLORTEST: no Pick button found in the properties panel.");
            return 1;
        }

        var s_Seeded = new[] { 0f, 0f, 0f, 0f };
        ColourPickerHook = p_Seed =>
        {
            s_Seeded = p_Seed;

            // Pure red at half alpha: unmistakable against the 1,1,1,1 default, and asymmetric enough that a
            // swizzle or a dropped alpha would show up rather than passing.
            return new[] { 1f, 0f, 0f, 0.5f };
        };

        try
        {
            s_Button.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }
        catch (Exception s_Exception)
        {
            Console.Error.WriteLine($"COLORTEST: the Pick handler THREW: {s_Exception}");
            return 1;
        }
        finally
        {
            ColourPickerHook = null;
        }

        var s_Value = s_Window.Canvas.Graph.FindNode(s_Node.Id)?.GetParam("Value") ?? "(node not in graph)";
        var s_Shown = string.Join(",", Descendants<TextBox>(s_Window.PropertiesPanel)
            .Take(4).Select(p_F => p_F.Text));

        Console.Out.WriteLine("picker seeded: " + string.Join(",",
            s_Seeded.Select(p_V => p_V.ToString("0.0###", CultureInfo.InvariantCulture))));
        Console.Out.WriteLine($"after pick:   Value = {s_Value}");
        Console.Out.WriteLine($"fields show:  {s_Shown}");

        // Landing in the dictionary is not the same as the shader getting it, so wire the node into a root and
        // check the literal that actually reaches the HLSL.
        var s_Root = new GraphNode { Kind = "StandardRoot", X = 300, Y = 0 };
        s_Window.Canvas.Graph.Nodes.Add(s_Root);
        s_Window.Canvas.Graph.Connect(s_Node.Id, "Out", s_Root.Id, "Diffuse");

        var s_Emitted = new HlslEmitter().Emit(s_Window.Canvas.Graph);
        var s_Wanted = "float4(1.0, 0.0, 0.0, 0.5)";
        var s_Literal = s_Emitted.Hlsl.Split('\n')
            .FirstOrDefault(p_L => p_L.Contains(s_Wanted, StringComparison.Ordinal));

        Console.Out.WriteLine($"emitted:      {s_Literal?.Trim() ?? $"NOT FOUND - no '{s_Wanted}' in the HLSL"}");

        // Full equality, alpha included: a picker that drops the fourth component would otherwise pass.
        var s_Ok = s_Value == "1.0,0.0,0.0,0.5" && s_Shown == "1.0,0.0,0.0,0.5" && s_Literal != null;
        Console.Out.WriteLine(s_Ok
            ? "COLORTEST: PASS - the picked colour reached the model, the fields and the HLSL."
            : "COLORTEST: FAIL - expected 1.0,0.0,0.0,0.5 in the model, the fields and the HLSL.");

        return s_Ok ? 0 : 1;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject p_Root) where T : DependencyObject
    {
        var s_Count = VisualTreeHelper.GetChildrenCount(p_Root);
        for (var i = 0; i < s_Count; i++)
        {
            var s_Child = VisualTreeHelper.GetChild(p_Root, i);
            if (s_Child is T s_Match)
                yield return s_Match;

            foreach (var s_Deep in Descendants<T>(s_Child))
                yield return s_Deep;
        }
    }

    private FrameworkElement BuildParamEditor(GraphNode p_Node, ParamDef p_Param)
    {
        if (p_Param.Kind == ParamKind.Color)
            return BuildColorEditor(p_Node, p_Param);

        if (p_Param.Kind == ParamKind.Choice)
        {
            var s_Choice = new ComboBox();
            foreach (var s_Option in p_Param.Choices)
                s_Choice.Items.Add(s_Option);

            s_Choice.SelectedItem = Live(p_Node).GetParam(p_Param.Name);
            s_Choice.SelectionChanged += (_, _) =>
            {
                if (s_Choice.SelectedItem is not string s_Value || s_Value == Live(p_Node).GetParam(p_Param.Name))
                    return;

                Canvas.PushUndo();
                Live(p_Node).Params[p_Param.Name] = s_Value;
                TouchGraph();

                // A discrete value can decide which PINS are live (a blend mode turning the cutout input on),
                // and that state is drawn when the panel is built — so it has to be rebuilt. Only for
                // discrete values: doing it while someone types in a text box would steal the caret.
                BuildProperties();
            };

            return s_Choice;
        }

        if (p_Param.Kind == ParamKind.Bool)
        {
            var s_Check = new CheckBox
            {
                IsChecked = Live(p_Node).GetParam(p_Param.Name).Equals("true", StringComparison.OrdinalIgnoreCase),
            };

            s_Check.Click += (_, _) =>
            {
                Canvas.PushUndo();
                Live(p_Node).Params[p_Param.Name] = s_Check.IsChecked == true ? "true" : "false";
                TouchGraph();
                BuildProperties();
            };

            return s_Check;
        }

        if (p_Param.Kind == ParamKind.Enum && p_Param.EnumType != null)
        {
            var s_Combo = new ComboBox();
            foreach (var s_Name in Enum.GetNames(p_Param.EnumType))
                s_Combo.Items.Add(s_Name);

            s_Combo.SelectedItem = Live(p_Node).GetParam(p_Param.Name);
            s_Combo.SelectionChanged += (_, _) =>
            {
                if (s_Combo.SelectedItem is not string s_Value || s_Value == Live(p_Node).GetParam(p_Param.Name))
                    return;

                Canvas.PushUndo();
                Live(p_Node).Params[p_Param.Name] = s_Value;
                TouchGraph();

                // Availability can depend on this value (Opacity follows SurfaceShaderType), so redraw the panel.
                BuildProperties();
            };

            return s_Combo;
        }

        var s_Box = SelectAllOnFocus(new TextBox { Text = Live(p_Node).GetParam(p_Param.Name) });
        var s_Original = s_Box.Text;

        s_Box.TextChanged += (_, _) =>
        {
            Live(p_Node).Params[p_Param.Name] = s_Box.Text;
            TouchGraph();
        };

        // One undo step per editing session rather than per keystroke.
        s_Box.GotKeyboardFocus += (_, _) => s_Original = s_Box.Text;
        s_Box.LostKeyboardFocus += (_, _) =>
        {
            if (s_Box.Text == s_Original)
                return;

            var s_New = s_Box.Text;
            Live(p_Node).Params[p_Param.Name] = s_Original;
            Canvas.PushUndo();
            Live(p_Node).Params[p_Param.Name] = s_New;
            s_Original = s_New;
        };

        return s_Box;
    }

    private void OnNew(object p_Sender, RoutedEventArgs p_Args)
    {
        var s_Graph = NewGraphWithRoot();
        OpenInNewTab(s_Graph, null);
        Log($"New graph with a {s_Graph.Nodes[0].Def.TitleFor(m_Settings.UdkNodeStyle)}, in its own tab.");
    }

    /// <summary>
    /// Makes a properties field select its whole value on the FIRST click, so typing replaces it.
    ///
    /// These boxes hold one short value that people come to overwrite, not to edit in place — having to
    /// sweep the old number first is friction on the most repeated action in the panel. The mouse handler
    /// is needed as well as the focus one: WPF places the caret on click, which would undo the selection.
    /// </summary>
    private static TextBox SelectAllOnFocus(TextBox p_Box)
    {
        p_Box.GotKeyboardFocus += (_, _) => p_Box.SelectAll();
        p_Box.PreviewMouseLeftButtonDown += (_, p_Args) =>
        {
            if (p_Box.IsKeyboardFocusWithin)
                return;

            // Focus it ourselves and swallow the click, or the caret lands and clears the selection.
            p_Box.Focus();
            p_Args.Handled = true;
        };

        return p_Box;
    }

    /// <summary>
    /// A fresh graph already carrying its output node — every shader needs one, and starting empty just
    /// made it the first thing anybody had to go and find. The root is the one of the vocabulary being
    /// authored in: Material while in UDK style, StandardRoot otherwise.
    /// </summary>
    private ShaderGraph NewGraphWithRoot()
    {
        var s_Graph = new ShaderGraph { Name = "Untitled" };
        s_Graph.Nodes.Add(new GraphNode
        {
            Kind = m_Settings.UdkNodeStyle ? "UdkMaterial" : "StandardRoot",
            X = 120,
            Y = -80,
        });

        return s_Graph;
    }

    private void OnOpen(object p_Sender, RoutedEventArgs p_Args)
    {
        var s_Dialog = new OpenFileDialog { Filter = "Shader graph (*.json)|*.json|All files (*.*)|*.*" };
        if (s_Dialog.ShowDialog() != true)
            return;

        try
        {
            var s_Opened = ShaderGraph.FromJson(File.ReadAllText(s_Dialog.FileName));
            AbsorbEmbeddedVariations(s_Opened);
            OpenInNewTab(s_Opened, s_Dialog.FileName);
            Log($"Opened {s_Dialog.FileName} in its own tab");
        }
        catch (Exception s_Exception)
        {
            Log($"ERROR opening graph: {s_Exception.Message}");
        }
    }

    private void OnSave(object p_Sender, RoutedEventArgs p_Args)
    {
        var s_Dialog = new SaveFileDialog
        {
            Filter = "Shader graph (*.json)|*.json",
            FileName = m_GraphPath ?? $"{Canvas.Graph.Name}.json",
        };

        if (s_Dialog.ShowDialog() != true)
            return;

        Canvas.Graph.TargetShader = TargetShaderBox.Text;

        try
        {
            // ONE master file: the on-screen document plus every other variation draft of this target
            // embedded inside it. Open unpacks them back into drafts; the bake ships all of them.
            SnapshotVariationDraft();
            Canvas.Graph.Variations = CollectVariationDrafts(true);
            File.WriteAllText(s_Dialog.FileName, Canvas.Graph.ToJson());
            Canvas.Graph.Variations = null;
            m_GraphPath = s_Dialog.FileName;
            Log($"Saved {s_Dialog.FileName}" +
                (m_VariationDrafts.Count > 0 ? " (variations embedded in the one file)" : ""));

            // A save into the instance library IS publication: the palette entry (and every host graph
            // that places it) follows this file from now on.
            if (Graph.InstanceLibrary.Contains(s_Dialog.FileName))
            {
                var s_Count = Graph.InstanceLibrary.Refresh(Log);
                BuildPalette();
                Log($"Instance library refreshed: {s_Count} fragment(s) available under 'Instances'.");
            }
        }
        catch (Exception s_Exception)
        {
            Log($"ERROR saving graph: {s_Exception.Message}");
        }
    }

    private async void OnReloadFromGame(object p_Sender, RoutedEventArgs p_Args)
    {
        var s_Target = TargetShaderBox.Text.Trim();
        if (s_Target.Length == 0)
        {
            Log("Set the target shader first (double-click one in the browser, or type its name).");
            return;
        }

        await SelectTargetAsync(s_Target);
    }

    private string? EmitToDisk()
    {
        Canvas.Graph.TargetShader = TargetShaderBox.Text;

        // The detected contract shapes PsIn/PsOut; null falls back to the rigid-mesh layout.
        var s_Result = new HlslEmitter { Contract = m_Contract }.Emit(Canvas.Graph);
        foreach (var s_Error in s_Result.Errors)
            Log($"ERROR: {s_Error}");

        if (!s_Result.Ok)
            return null;

        try
        {
            Directory.CreateDirectory(OutputFolderBox.Text);
            var s_Path = Path.Combine(OutputFolderBox.Text, $"{SafeName()}.hlsl");
            File.WriteAllText(s_Path, s_Result.Hlsl);
            Log($"Wrote {s_Path}");
            return s_Path;
        }
        catch (Exception s_Exception)
        {
            Log($"ERROR writing HLSL: {s_Exception.Message}");
            return null;
        }
    }

    // CompileToDxbc emits the HLSL itself first, so one button covers the old Emit + Compile pair.
    private void OnExportHlsl(object p_Sender, RoutedEventArgs p_Args) => CompileToDxbc();

    /// <summary>Emits and compiles the graph. Returns the .dxbc path, or null with the reason already logged.</summary>
    private string? CompileToDxbc()
    {
        var s_HlslPath = EmitToDisk();
        if (s_HlslPath == null)
            return null;

        var s_Fxc = FindFxc();
        if (s_Fxc == null)
        {
            Log("ERROR: fxc.exe not found. Install the Windows SDK or put fxc.exe on PATH.");
            return null;
        }

        var s_DxbcPath = Path.ChangeExtension(s_HlslPath, ".dxbc");
        var s_Result = Run(s_Fxc, new[]
        {
            "/nologo", "/T", "ps_5_0", "/E", "main", "/O3", "/Fo", s_DxbcPath, s_HlslPath,
        });

        foreach (var s_Line in s_Result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Log($"  fxc: {s_Line.TrimEnd()}");

        if (s_Result.ExitCode != 0 || !File.Exists(s_DxbcPath))
        {
            Log($"fxc failed (exit {s_Result.ExitCode}).");
            return null;
        }

        Log($"Compiled -> {s_DxbcPath} ({new FileInfo(s_DxbcPath).Length} bytes)");
        return s_DxbcPath;
    }

    /// <summary>
    /// Bakes the graph into an installable mod: pick the maps, name the mod and bundle, choose where it goes.
    /// Everything the pipeline can get wrong is checked by the baker itself and reported per map.
    /// </summary>
    private async void OnBakeShader(object p_Sender, RoutedEventArgs p_Args)
    {
        var s_Target = TargetShaderBox.Text.Trim();
        if (s_Target.Length == 0)
        {
            Log("ERROR: set the target shader first - the bake replaces that shader's bytecode.");
            return;
        }

        var s_GamePath = GamePathBox.Text.Trim();
        if (s_GamePath.Length == 0 || !Directory.Exists(s_GamePath))
        {
            Log("ERROR: the BF3 path is empty or does not exist - fill in the 'BF3 path' box.");
            return;
        }

        var s_Repl = FindRimeRepl();
        if (s_Repl == null)
        {
            Log("ERROR: RimeREPL.exe not found (expected under Rime-src\\bin\\Release).");
            return;
        }

        // Compiled before asking anything: a graph that does not compile has nothing to bake, and finding that
        // out after the user has filled in the dialog wastes their time.
        var s_Dxbc = CompileToDxbc();
        if (s_Dxbc == null)
            return;

        var s_Levels = LevelScanner.Scan(s_GamePath);
        Log($"Found {s_Levels.Count} level(s) in the install.");

        var s_Request = BakeDialog.Ask(this, s_Levels, SafeName(), OutputFolderBox.Text.Trim(), s_Target,
            Canvas.Graph.BakeVariation);
        if (s_Request == null)
            return;

        BakeButton.IsEnabled = false;
        var s_WorkDir = Path.Combine(OutputFolderBox.Text.Trim(), "bake");

        // Custom textures convert BEFORE the mounts start: a bad file or a missing slot name should cost
        // seconds, not surface after minutes of bundle building. The names come from the texture set the
        // user is LOOKING at (their active variation), falling back to the base set.
        var s_Map = ReadSlotMapFull(SlotMapPath(s_Target));
        var s_BakeSlots = s_Map?.Variations != null && s_Map.Variations.TryGetValue(m_Variation, out var s_Set)
            ? s_Set.Slots
            : s_Map?.Slots;

        // ⛔ EVERY open document goes down the saved-graph lane, variation or not — it is the only one with
        // PER-VARIANT compilation, and that is not a refinement: a shader name owns several pixel flavours
        // with different constant/texture contracts, so the direct lane's single compilation written into
        // all of them renders the object BLACK. Measured: the map's barriers came out black through the
        // direct lane and correct through this one, from the same graph.
        //
        // This used to delegate only variations, which is why the difference stayed hidden — replacements
        // were the case nobody could deliver to the level's own objects until the database order was fixed,
        // so nothing had ever exercised the direct lane on them.
        var s_DelegatePrimary = true;
        var s_CustomTextures = new List<CustomBakeTexture>();

        if (s_DelegatePrimary)
        {
            Directory.CreateDirectory(s_WorkDir);
            var s_Self = ShaderGraph.FromJson(Canvas.Graph.ToJson());
            s_Self.Variations = null;

            // The variation rides the mesh of the (mesh, variation) set SELECTED in the preview — the same
            // picker the user already chose their object with. Baking "whichever mesh the database lists
            // first" put a glass variation on a destruction mesh once, which is not resident when the level
            // bundle loads and crashed the end of the loading screen registering against a null mesh.
            if (s_Map?.Variations != null && s_Map.Variations.TryGetValue(m_Variation, out var s_Chosen) &&
                s_Chosen.Mesh is { Length: > 0 })
                s_Self.BakeMesh = s_Chosen.Mesh;

            var s_SelfPath = Path.Combine(s_WorkDir, "current_variation.json");
            File.WriteAllText(s_SelfPath, s_Self.ToJson());
            s_Request.ExtraGraphs.Insert(0, s_SelfPath);
            Log(Canvas.Graph.BakeVariation is { Length: > 0 } s_Named
                ? $"Variation bake: '{s_Named}' — the open document joins the per-variant lane together " +
                  "with its drafts."
                : "The open document joins the per-variant lane: every pixel flavour of this shader gets " +
                  "the graph compiled against ITS OWN contract.");
        }
        else
        {
            var (s_Prepared, s_TextureError) = PrepareCustomTextures(
                Canvas.Graph, s_BakeSlots, Path.Combine(s_WorkDir, "textures"), Log);

            if (s_TextureError != null)
            {
                Log($"BAKE ABORTED: {s_TextureError}");
                BakeButton.IsEnabled = true;
                return;
            }

            s_CustomTextures = s_Prepared;
        }

        // Every OTHER variation draft of this target joins the bake by itself — nobody lists files by
        // hand for looks that already live in this document.
        SnapshotVariationDraft();
        var s_AutoDrafts = CollectVariationDrafts(true) ?? new List<ShaderGraph>();
        if (s_AutoDrafts.Count > 0)
        {
            var s_AutoDir = Path.Combine(s_WorkDir, "auto_variations");
            Directory.CreateDirectory(s_AutoDir);
            foreach (var s_Draft in s_AutoDrafts)
            {
                var s_AutoPath = Path.Combine(s_AutoDir, $"{Sanitize(s_Draft.BakeVariation!.Split('/')[^1])}.json");
                File.WriteAllText(s_AutoPath, s_Draft.ToJson());
                if (!s_Request.ExtraGraphs.Contains(s_AutoPath, StringComparer.OrdinalIgnoreCase))
                    s_Request.ExtraGraphs.Add(s_AutoPath);
            }

            Log($"{s_AutoDrafts.Count} variation draft(s) of this object join the bake automatically.");
        }

        // Saved graphs join the bake as full documents: each compiles against ITS OWN target's contract and
        // brings its own custom textures. Their preparation mounts the game once, so it runs off the UI thread.
        var s_Shaders = new List<ShaderBaker.BakeShader>();
        if (!s_DelegatePrimary)
            s_Shaders.Add(new(s_Target, s_Dxbc, null));
        if (s_Request.ExtraGraphs.Count > 0)
        {
            var s_Fxc = FindFxc();
            if (s_Fxc == null)
            {
                Log("BAKE ABORTED: fxc.exe not found, and the saved graphs need it to compile.");
                BakeButton.IsEnabled = true;
                return;
            }

            var s_ExtraProgress = new Progress<string>(Log);
            var s_TexCache = TexCacheDir;
            var (s_Extra, s_ExtraTextures, s_ExtraError) = await Task.Run(() => PrepareAdditionalGraphs(
                s_Request.ExtraGraphs, s_Repl, s_Fxc, s_GamePath, s_Request.Levels[0], s_TexCache,
                Path.Combine(s_WorkDir, "extra"), p_Line => ((IProgress<string>) s_ExtraProgress).Report(p_Line)));

            if (s_ExtraError != null)
            {
                Log($"BAKE ABORTED: {s_ExtraError}");
                BakeButton.IsEnabled = true;
                return;
            }

            s_Shaders.AddRange(s_Extra);

            // Two graphs overriding the same texture NAME with different files is a fight nobody notices
            // in the build output — refused here instead.
            foreach (var s_Extra2 in s_ExtraTextures)
            {
                var s_Clash = s_CustomTextures.FirstOrDefault(p_T =>
                    p_T.Name.Equals(s_Extra2.Name, StringComparison.OrdinalIgnoreCase) &&
                    !p_T.DdsPath.Equals(s_Extra2.DdsPath, StringComparison.OrdinalIgnoreCase));

                if (s_Clash != null)
                {
                    Log($"BAKE ABORTED: two graphs override the game texture '{s_Extra2.Name}' with " +
                        "different files.");
                    BakeButton.IsEnabled = true;
                    return;
                }

                if (s_CustomTextures.All(p_T => !p_T.Name.Equals(s_Extra2.Name, StringComparison.OrdinalIgnoreCase)))
                    s_CustomTextures.Add(s_Extra2);
            }
        }

        if (s_CustomTextures.Count > 0)
            Log($"{s_CustomTextures.Count} custom texture(s) will ride in the bundle as same-name overrides.");

        try
        {
            var s_Progress = new Progress<string>(Log);
            var s_Result = await Task.Run(() => ShaderBaker.Bake(s_Request, s_Shaders, s_GamePath,
                s_Repl, s_WorkDir, p_Line => ((IProgress<string>) s_Progress).Report(p_Line), s_CustomTextures));

            var s_Good = s_Result.Levels.Count(p_L => p_L.Ok);
            var s_Bad = s_Result.Levels.Count - s_Good;

            if (s_Good == 0)
            {
                Log("BAKE FAILED: no map produced a usable superbundle. Nothing was written.");
            }
            else
            {
                Log($"BAKE DONE: {s_Good} map(s) baked{(s_Bad > 0 ? $", {s_Bad} skipped or failed" : "")} -> {s_Result.ModFolder}");
                Log($"Install: copy '{s_Request.ModName}' into your VU Mods folder and add a line " +
                    $"'{s_Request.ModName}' to ModList.txt (no '#', or it stays disabled).");
            }

            foreach (var s_Level in s_Result.Levels.Where(p_L => !p_L.Ok))
                Log($"  {s_Level.Level}: {s_Level.Failure}");
        }
        catch (Exception s_Exception)
        {
            Log($"BAKE ERROR: {s_Exception.Message}");
        }
        finally
        {
            BakeButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Rewrites a bake's custom textures for a VARIATION: instead of overriding the vanilla texture by name
    /// (which would retexture every vanilla instance on the map), each ships under a sibling name and is
    /// bound in the CLONE's own constants at the register the vanilla art occupied. The vanilla texture
    /// becomes the group donor — a fresh name has no mounted original to copy the texture group from.
    /// </summary>
    internal static string? RetargetTexturesForVariation(List<CustomBakeTexture> p_Textures,
        VariationSpec p_Spec, Dictionary<string, string>? p_Slots)
    {
        for (var i = 0; i < p_Textures.Count; i++)
        {
            var s_Texture = p_Textures[i];

            if (s_Texture.SlotTarget == null)
            {
                // Same-name override lane: the register comes from the slot map row the name was taken from.
                var s_Register = p_Slots?.FirstOrDefault(p_S =>
                    p_S.Value.Equals(s_Texture.Name, StringComparison.OrdinalIgnoreCase)).Key;

                if (s_Register == null)
                    return $"no register maps to '{s_Texture.Name}' — reload the target textures and retry.";

                p_Textures[i] = s_Texture with
                {
                    Name = Emit.VariationPlan.SiblingTextureName(p_Spec.VariationAsset, s_Texture.Name),
                    SlotTarget = p_Spec.CloneShader,
                    SlotRegister = s_Register,
                    GroupDonor = s_Texture.Name,
                };
            }
            else
            {
                // New-slot lane: the slot surgery moves onto the clone; the name becomes a sibling.
                p_Textures[i] = s_Texture with
                {
                    Name = $"{p_Spec.VariationAsset}_t{s_Texture.SlotRegister}",
                    SlotTarget = p_Spec.CloneShader,
                };
            }
        }

        return null;
    }

    private string TexCacheDir => Path.Combine(OutputFolderBox.Text, "texcache");

    internal static string SlotMapFileName(string p_Target) => $"{Sanitize(p_Target)}.slots.json";

    private string SlotMapPath(string p_Target) => Path.Combine(TexCacheDir, SlotMapFileName(p_Target));

    /// <summary>
    /// Repopulates the thumbnails from the on-disk cache. Returns how many were restored, so the caller can
    /// tell the difference between "nothing cached yet" and "cache served everything".
    /// </summary>
    /// <summary>
    /// Feeds the current variation's REAL parameter values into the preview's cb1 (pattern slots stay for
    /// everything unnamed), so the object shades as its instance. Null resets to the pure pattern - which
    /// must happen on target switches, or the previous shader's values would leak into the next preview.
    /// </summary>
    /// <summary>
    /// The engine-fed thermal parameters. In the game they are zero unless thermal optics are active; the
    /// preview's probe pattern is non-zero by design, which showed up as a magenta tint on character shaders.
    /// The Settings toggle decides which of the two the preview gets — the BAKE is untouched either way, since
    /// these live in the runtime constant buffer, never in the compiled shader.
    /// </summary>
    private static readonly string[] s_ThermalParameters = { "FLIRData", "FLIRScale" };

    private void PushInstanceValues(Dictionary<string, string>? p_Values)
    {
        var s_Resolved = new List<(int Element, float[] Value)>();

        if (m_Contract != null && p_Values != null)
            foreach (var (s_Name, s_Text) in p_Values)
            {
                var (s_Register, s_Element) = m_Contract.ExternalFieldOf(s_Name);
                if (s_Register != 1 || s_Element < 0)
                    continue;

                var s_Parts = s_Text.Split(',');
                var s_Value = new float[4];
                for (var i = 0; i < 4 && i < s_Parts.Length; i++)
                    float.TryParse(s_Parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out s_Value[i]);

                s_Resolved.Add((s_Element, s_Value));
            }

        var s_InstanceCount = s_Resolved.Count;

        if (m_Contract != null && !m_Settings.FlirPreview)
            foreach (var s_Name in s_ThermalParameters)
            {
                var (s_Register, s_Element) = m_Contract.ExternalFieldOf(s_Name);
                if (s_Register == 1 && s_Element >= 0 && s_Resolved.All(p_R => p_R.Element != s_Element))
                    s_Resolved.Add((s_Element, new float[4]));
            }

        m_Preview.SetExternalValues(s_Resolved.Count > 0 ? s_Resolved : null);

        if (s_InstanceCount > 0)
            Log($"{s_InstanceCount} instance value(s) wired into the preview's material parameters.");

        if (s_Resolved.Count > s_InstanceCount)
            Log("Thermal parameters (FLIR) read zero in the preview — enable them in Settings if you want " +
                "to see the thermal tint path.");
    }

    /// <summary>The variation whose texture set is being previewed; "0" is the unvaried master.</summary>
    private string m_Variation = "0";

    private bool m_VariationBoxFilling;

    private sealed class VariationChoice
    {
        public required string Hash { get; init; }
        public required string Name { get; init; }
        public override string ToString() => Name;
    }

    private void OnVariationChanged(object p_Sender, SelectionChangedEventArgs p_Args)
    {
        if (m_VariationBoxFilling || VariationBox.SelectedItem is not VariationChoice s_Choice)
            return;

        m_Variation = s_Choice.Hash;

        // ⛔ THE PICKED SET IS THE OBJECT THIS GRAPH IS AIMED AT, SO IT BELONGS IN THE DOCUMENT. It used to be
        // stamped only onto the throwaway copy the bake makes, which meant a SAVED graph could never carry it:
        // the file went out with no object, and a bake that reads it either aims at whatever mesh the cached
        // map lists first or — once that guess became a refusal — cannot be baked at all. Written here, the
        // choice survives a save, travels with the file and is what "use this on the objects already in the
        // level" is aimed by.
        if (ReadSlotMapFull(SlotMapPath(TargetShaderBox.Text.Trim()))?.Variations is { } s_Sets &&
            s_Sets.TryGetValue(s_Choice.Hash, out var s_Set) && s_Set.Mesh is { Length: > 0 } &&
            !string.Equals(Canvas.Graph.BakeMesh, s_Set.Mesh, StringComparison.OrdinalIgnoreCase))
        {
            Canvas.Graph.BakeMesh = s_Set.Mesh;
            TouchGraph();
            Log($"This graph is now aimed at '{s_Set.Mesh}' (save it to keep that).");
        }

        m_TexturePreviews.Clear();
        if (LoadCachedPreviews() == 0)
            Log("No cached thumbnails for that variation; press 'Load target textures'.");
    }

    /// <summary>
    /// Shows the variation picker when the cached map carries more than one texture set. Filling the combo
    /// fires SelectionChanged, so a guard flag keeps that from re-entering the preview load.
    /// </summary>
    private void FillVariationBox(SlotMap p_Map)
    {
        var s_Show = p_Map.Variations is { Count: > 1 };
        VariationLabel.Visibility = s_Show ? Visibility.Visible : Visibility.Collapsed;
        VariationBox.Visibility = s_Show ? Visibility.Visible : Visibility.Collapsed;

        m_VariationBoxFilling = true;
        VariationBox.Items.Clear();

        if (s_Show)
        {
            foreach (var (s_Hash, s_Variation) in p_Map.Variations!
                         .OrderBy(p_V => p_V.Key == "0" ? "" : p_V.Value.Name, StringComparer.OrdinalIgnoreCase))
                VariationBox.Items.Add(new VariationChoice
                {
                    Hash = s_Hash,

                    // Variation assets carry long partition paths; the last segment is the readable part.
                    Name = s_Variation.Name.Contains('/')
                        ? s_Variation.Name.Split('/').Last()
                        : s_Variation.Name,
                });

            // ⛔ THE DOCUMENT'S OWN OBJECT COMES BACK FIRST. A graph aimed at one mesh (the set picked here,
            // saved with it) reopened on "whatever this map lists first" shows another object's textures and
            // silently asks to be aimed again, every single time — the manual step this stamp exists to
            // remove. Matched by MESH, not by the set key, because the key of a shared shader carries the
            // mesh path and a re-scan can renumber it while the mesh stays the same.
            var s_Aimed = Canvas.Graph.BakeMesh is { Length: > 0 } s_Mesh
                ? p_Map.Variations!.FirstOrDefault(p_V =>
                    string.Equals(p_V.Value.Mesh, s_Mesh, StringComparison.OrdinalIgnoreCase)).Key
                : null;

            if (s_Aimed is { Length: > 0 })
                m_Variation = s_Aimed;

            // A shared shader's sets are keyed by (mesh, variation), so "0" may not exist: fall back to the
            // first listed set rather than to a key nobody wrote.
            else if (p_Map.Variations!.ContainsKey(m_Variation) == false)
                m_Variation = VariationBox.Items.Cast<VariationChoice>().FirstOrDefault()?.Hash ?? "0";

            VariationBox.SelectedItem = VariationBox.Items.Cast<VariationChoice>()
                .FirstOrDefault(p_C => p_C.Hash == m_Variation);
        }
        else
        {
            m_Variation = "0";
        }

        m_VariationBoxFilling = false;
    }

    private int LoadCachedPreviews()
    {
        var s_Target = TargetShaderBox.Text.Trim();
        if (s_Target.Length == 0)
            return 0;

        try
        {
            var s_Map = ReadSlotMapFull(SlotMapPath(s_Target));
            if (s_Map == null)
                return 0;

            FillVariationBox(s_Map);
            var s_Chosen = s_Map.Variations != null &&
                           s_Map.Variations.TryGetValue(m_Variation, out var s_Variation)
                ? s_Variation
                : null;
            var s_Slots = s_Chosen?.Slots ?? s_Map.Slots;
            PushInstanceValues(s_Chosen?.Values);

            var s_Count = 0;
            foreach (var (s_Register, s_Name) in s_Slots)
            {
                var s_Png = Path.Combine(TexCacheDir, $"{Sanitize(s_Name)}.png");
                if (!File.Exists(s_Png))
                    continue;

                var s_Bitmap = new BitmapImage();
                s_Bitmap.BeginInit();
                // Same URI-cache trap as the custom loader: the cache PNGs get REWRITTEN on a re-dump, and
                // without this the thumbnails kept the pre-rewrite pixels until the editor restarted.
                s_Bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                s_Bitmap.CacheOption = BitmapCacheOption.OnLoad;
                s_Bitmap.UriSource = new Uri(s_Png);
                s_Bitmap.EndInit();
                s_Bitmap.Freeze();

                m_TexturePreviews[s_Register] = s_Bitmap;
                s_Count++;
            }

            if (s_Count > 0)
            {
                TouchGraph();
                PushPreviewTextures();
                Log($"Loaded {s_Count} cached thumbnail(s) from {TexCacheDir}");
            }

            // The user's chosen files ALWAYS win over the cache-derived art, on every repopulation path.
            ApplyCustomTextureOverrides();

            return s_Count;
        }
        catch (Exception s_Exception)
        {
            Log($"Could not read the thumbnail cache: {s_Exception.Message}");
            return 0;
        }
    }

    private async void OnLoadTextures(object p_Sender, RoutedEventArgs p_Args) => await RunLoadTexturesAsync();

    /// <summary>The texture step itself, awaitable so picking a target can chain it after the translation.</summary>
    private async Task RunLoadTexturesAsync()
    {
        var s_Target = TargetShaderBox.Text.Trim();
        var s_GamePath = GamePathBox.Text.Trim();

        if (s_Target.Length == 0)
        {
            Log("ERROR: set the target shader first.");
            return;
        }

        if (LoadCachedPreviews() > 0)
        {
            Log($"Served from cache. Delete {TexCacheDir} and press again to re-read from the game.");
            return;
        }

        if (s_GamePath.Length == 0)
        {
            Log("ERROR: the BF3 path is empty and could not be auto-detected — fill in the 'BF3 path' box.");
            return;
        }

        if (!Directory.Exists(s_GamePath))
        {
            Log($"ERROR: '{s_GamePath}' does not exist.");
            return;
        }

        var s_Repl = FindRimeRepl();
        if (s_Repl == null)
        {
            Log("ERROR: RimeREPL.exe not found (expected under Rime-src\\bin\\Release).");
            return;
        }

        var s_ShaderDb = ResolveShaderDb(s_Target);
        var s_CacheDir = TexCacheDir;
        var s_MapPath = SlotMapPath(s_Target);
        ReloadButton.IsEnabled = false;
        Log($"Mounting BF3 to read '{s_Target}' from {s_ShaderDb} — this takes ~20 s (two mounts), please wait…");

        try
        {
            var s_Contract = m_Contract;
            var s_Log = await Task.Run(() =>
                LoadTexturesCore(s_Repl, s_GamePath, s_ShaderDb, s_Target, s_CacheDir, s_MapPath, s_Contract));

            foreach (var s_Line in s_Log)
                Log(s_Line);

            // The decoded PNGs are the single source for thumbnails, so reload through the cache path.
            if (LoadCachedPreviews() == 0)
                Log("No thumbnail could be produced. The lines above say which step failed.");
        }
        catch (Exception s_Exception)
        {
            Log($"ERROR loading textures: {s_Exception.Message}");
        }
        finally
        {
            ReloadButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Keeps only the SHTEX-SHADER blocks whose header names exactly the given shader (any render path),
    /// plus whatever lies outside any block. A substring-matched dump prints one block per SIBLING shader,
    /// and slot lists merged across siblings invent externals the target does not have.
    /// </summary>
    internal static string ScopeToShaderBlocks(string p_Output, string p_Target)
    {
        var s_Kept = new StringBuilder();
        bool? s_InTarget = null;

        foreach (var s_Line in p_Output.Split('\n'))
        {
            var s_Header = Regex.Match(s_Line, @"SHTEX-SHADER:\s*\[[^\]]*\]\s*(\S+)");
            if (s_Header.Success)
            {
                var s_Name = s_Header.Groups[1].Value;
                s_InTarget = string.Equals(s_Name, p_Target, StringComparison.OrdinalIgnoreCase) ||
                             s_Name.EndsWith("/" + p_Target, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (s_InTarget != false)
                s_Kept.Append(s_Line).Append('\n');
        }

        return s_Kept.ToString();
    }

    internal static List<string> LoadTexturesCore(string p_Repl, string p_GamePath, string p_ShaderDb,
        string p_Target, string p_CacheDir, string p_MapPath, ShaderContract? p_Contract = null)
    {
        var s_Result = new List<string>();
        Directory.CreateDirectory(p_CacheDir);

        // Besides the shaderdb's own lists, ask the mesh variation databases for material-bound textures: a
        // shader can take part (or all) of its textures per MATERIAL INSTANCE, and those names exist nowhere
        // in the shaderdb entry. The scope is the LEVEL ROOT (levels/<name>), not the shaderdb's own sublevel:
        // the base sublevel database only knows static props, while soldier gear (CharacterRoot and friends)
        // is recorded per GAMEMODE sublevel — a shader can have zero materials in one database and its whole
        // wardrobe in a sibling. The mesh filter (the shader's folder) keeps that from scanning every mesh of
        // the level. Same mount for everything.
        string? s_MvdbScope = null;
        if (p_ShaderDb.EndsWith("/shaderdb", StringComparison.OrdinalIgnoreCase))
        {
            var s_Sublevel = p_ShaderDb[..^"/shaderdb".Length];
            var s_LevelCut = s_Sublevel.LastIndexOf('/');
            s_MvdbScope = s_LevelCut > 0 ? s_Sublevel[..s_LevelCut] : s_Sublevel;
        }

        var s_FolderCut = p_Target.LastIndexOf('/');
        var s_MeshFilter = s_FolderCut > 0 ? p_Target[..s_FolderCut] : p_Target;

        var s_ListScript = Path.Combine(p_CacheDir, "list.rime");
        File.WriteAllText(s_ListScript,
            $"mount_game \"{p_GamePath}\" Frostbite2_0 true\nselect_game 1\n" +
            $"dump_shader_textures {p_ShaderDb} {p_Target}\n" +
            (s_MvdbScope == null ? "" : $"dump_shader_material_textures {s_MvdbScope} {p_Target} {s_MeshFilter}\n") +
            // ⛔ AND THE SAME SCAN WITHOUT THE FOLDER FILTER, BECAUSE THE COUNT IS A DECISION. How many meshes
            // wear this shader is what says whether replacing it changes one object or repaints the map, and
            // the author cannot see it from inside the editor. The filtered pass above cannot answer it: it
            // only looks inside the shader's own folder, so a shader worn from elsewhere comes back as "one
            // mesh" — an undercount, which is the dangerous direction. Measured cost on a mounted game:
            // seconds, once, and the answer is cached with the slot map.
            (s_MvdbScope == null ? "" : $"dump_shader_material_textures {s_MvdbScope} {p_Target}\n") +
            "exit\n");

        var s_List = Run(p_Repl, new[] { s_ListScript });
        var s_Slots = new Dictionary<string, string>();

        // The shaderdb dump matches by SUBSTRING too, so a shared-root family prints one SHTEX-SHADER block
        // per SIBLING ("Root/CharacterRoot" also prints _skin, _1p, _lod…) and an unscoped parse merges their
        // slot lists — the editor then reports externals this shader does not even have. Every SHTEX* regex
        // below reads only the blocks whose header names the exact target.
        var s_ShaderScope = ScopeToShaderBlocks(s_List.Output, p_Target);

        // Textures bound as shader CONSTANTS carry their real sampler register.
        foreach (Match s_Match in Regex.Matches(s_ShaderScope, @"SHTEX:\s*register=t(\d+)\s+name=(\S+)"))
            s_Slots[s_Match.Groups[1].Value] = s_Match.Groups[2].Value;

        // Mesh shaders normally bind STREAMABLE textures instead, and the shaderdb does not record their
        // register. The register comes from the compiled shader's own binding table (RDEF), passed in as the
        // contract. ⛔ It is NOT declaration order: that held for the oil drum by luck, but a lightmapped shader
        // has the engine in t1..t4 and its material from t5, and the parachute's are fully shuffled
        // (texture_Texture7 sits in t1). Without a contract the slots are left unassigned rather than guessed.
        // Three places a material texture can be named, merged into ONE list for the pairing:
        //   1. SHTEX-SLOT - the solution-level slot list. COMPLETE and ordered: it includes shared assets
        //      (e.g. a generic default normal map) that the per-shader streamable list omits entirely.
        //   2. SHTEX-STREAMABLE - the per-shader list; carries the tiling factor, and is the fallback when a
        //      permutation has no slot list.
        //   3. SHTEX-EXTSLOT + SHMATTEX - external parameter slots, whose VALUES live per material instance in
        //      the mesh variation database (base variation preferred).
        // ⛔ Texture asset names can contain SPACES ("…/ah-1z viper_exterior_d"), so no name/texture field is
        // ever matched with \S+ — each one is delimited by the NEXT literal key (or end of line) instead. A
        // \S+ match here silently truncated every ah1z texture to "…/ah-1z" and the dump then found nothing.
        var s_StreamableRows = Regex
            .Matches(s_ShaderScope, @"SHTEX-STREAMABLE:\s*index=(\d+)\s+name=(.+?)\s+coord=.*?factor=([\d.,\-]+)")
            .Select(p_M => (Index: int.Parse(p_M.Groups[1].Value), Name: p_M.Groups[2].Value,
                Factor: float.TryParse(p_M.Groups[3].Value.Replace(',', '.'), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var s_Factor)
                    ? s_Factor
                    : 0f))
            .GroupBy(p_S => p_S.Index)
            .Select(p_G => p_G.First())
            .OrderBy(p_S => p_S.Index)
            .ToList();

        // GBuffer solutions first - that is the permutation the editor translates and previews; a shader
        // without one (sky, forward) falls back to whatever mode it does have.
        List<(int Slot, string Name)> SlotRows(string p_Tag)
        {
            var s_All = Regex
                .Matches(s_ShaderScope, p_Tag + @":\s*mode=(\S+)\s+slot=(\d+)\s+name=(.+?)\r?$",
                    RegexOptions.Multiline)
                .Select(p_M => (Mode: p_M.Groups[1].Value, Slot: int.Parse(p_M.Groups[2].Value),
                    Name: p_M.Groups[3].Value))
                .ToList();

            var s_Preferred = s_All.Where(p_R => p_R.Mode.Contains("GBuffer")).ToList();
            return (s_Preferred.Count > 0 ? s_Preferred : s_All)
                .GroupBy(p_R => p_R.Slot)
                .Select(p_G => (p_G.Key, p_G.First().Name))
                .OrderBy(p_R => p_R.Key)
                .ToList();
        }

        var s_SlotList = SlotRows("SHTEX-SLOT");
        var s_ExternalSlots = SlotRows("SHTEX-EXTSLOT");

        // The per-shader external list: parameter names in declaration order, with their tiling factor
        // (CamoTile repeats ×10 across a soldier, and that lives here, not in the slot list).
        var s_ExternalRows = Regex
            .Matches(s_ShaderScope, @"SHTEX-EXTERNAL:\s*index=(\d+)\s+param=(\S+).*?factor=([\d.,\-]+)")
            .Select(p_M => (Index: int.Parse(p_M.Groups[1].Value), Param: p_M.Groups[2].Value,
                Factor: float.TryParse(p_M.Groups[3].Value.Replace(',', '.'), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var s_Factor)
                    ? s_Factor
                    : 1f))
            .GroupBy(p_R => p_R.Index)
            .Select(p_G => p_G.First())
            .OrderBy(p_R => p_R.Index)
            .ToList();

        // The slot list and the per-shader external list are BOTH partial, in different ways: a shared root
        // (CharacterRoot and family) carries no solution-level slot lists at all, and a vehicle preset's
        // chosen permutation lists ONE of its six externals (the sibling render path's solutions carry the
        // rest). Preferring whichever list is non-empty therefore dropped a vehicle's Diffuse/Camo/Specular
        // on the floor — the sound rule is the UNION by parameter name, slot rows first (their slot number
        // is the solution's own), per-shader rows filling in what no slot row named.
        foreach (var s_Row in s_ExternalRows)
            if (!s_ExternalSlots.Any(p_S => p_S.Name.Equals(s_Row.Param, StringComparison.OrdinalIgnoreCase)))
                s_ExternalSlots.Add((s_Row.Index, s_Row.Param));

        var s_ExternalFactors = s_ExternalRows
            .GroupBy(p_R => p_R.Param, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p_G => p_G.Key, p_G => p_G.First().Factor, StringComparer.OrdinalIgnoreCase);

        // The shaderdb's own defaults for the external VALUE parameters (invariant-culture floats), kept in
        // the map so a foreign shader's cb starts from what the engine would feed, not from a guess.
        var s_ExternalDefaults = Regex
            .Matches(s_ShaderScope, @"SHTEX-EXTVAL:\s*mode=\S+\s+param=(\S+)\s+default=(\S+)")
            .GroupBy(p_M => p_M.Groups[1].Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p_G => p_G.Key, p_G => p_G.First().Groups[2].Value, StringComparer.OrdinalIgnoreCase);

        // Every material texture row, kept PER TEXTURE SET. The set key is (mesh, variation): a shader owned
        // by one mesh varies by variation only (each painting, each camo), but a SHARED shader is used by
        // MANY meshes - every character head feeds its own art into shaders/Root/CharacterRoot - and pooling
        // those would shuffle heads together. All sets are collected in this one mount, swappable from cache.
        // The dump command matches the shader by SUBSTRING, which drags in siblings ("Root/CharacterRoot"
        // also matches characterroot_skin, _lod, _eye…) whose parameters share names but carry different
        // art. Rows are therefore filtered to the exact target here — merging a sibling's Diffuse into this
        // shader's sets would silently paint the wrong texture.
        // Exact name, or the row's tail when the target was given without its folder prefix — a tail match
        // still cannot admit a sibling, because "…/characterroot_skin" does not END in "/characterroot".
        static bool IsTargetRow(string p_RowShader, string p_TargetName) =>
            string.Equals(p_RowShader, p_TargetName, StringComparison.OrdinalIgnoreCase) ||
            p_RowShader.EndsWith("/" + p_TargetName, StringComparison.OrdinalIgnoreCase);

        var s_TextureRows = Regex
            .Matches(s_List.Output,
                @"SHMATTEX:\s*shader=(\S+)\s+mesh=(\S+)\s+variation=(\d+)\s+variationName=(.+?)\s+source=\S+\s+param=(\S+)\s+texture=(.+?)\r?$",
                RegexOptions.Multiline)
            .Select(p_M => (Shader: p_M.Groups[1].Value, Mesh: p_M.Groups[2].Value, Hash: p_M.Groups[3].Value,
                Name: p_M.Groups[4].Value, Param: p_M.Groups[5].Value, Texture: p_M.Groups[6].Value))
            .Where(p_R => p_R.Texture != "(unresolved)" && IsTargetRow(p_R.Shader, p_Target))
            .ToList();

        // SHMATMESH rows name the (mesh, variation) pairs THEMSELVES, one per database entry material —
        // emitted even when the material binds no texture parameters, which is exactly what a
        // streamable-textured prop looks like. They seed the set list so a variation bake knows the mesh
        // and the existing variation names for ANY target, not just externally-textured ones.
        var s_MeshRows = Regex
            .Matches(s_List.Output,
                @"SHMATMESH:\s*shader=(\S+)\s+mesh=(\S+)\s+variation=(\d+)\s+variationName=(.+?)\r?$",
                RegexOptions.Multiline)
            .Select(p_M => (Shader: p_M.Groups[1].Value, Mesh: p_M.Groups[2].Value, Hash: p_M.Groups[3].Value,
                Name: p_M.Groups[4].Value))
            .Where(p_R => IsTargetRow(p_R.Shader, p_Target))
            .ToList();

        // Every mesh in the level that wears THIS shader (exact name — the scan matches by substring, and a
        // preset's siblings, _alphatest/_metal/_nospec…, would otherwise be counted as users of it).
        var s_MeshUsers = s_TextureRows.Select(p_R => p_R.Mesh)
            .Concat(s_MeshRows.Select(p_R => p_R.Mesh))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p_M => p_M, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var s_MultiMesh = s_MeshUsers.Count > 1;

        static string ShortName(string p_Path)
        {
            var s_Last = p_Path.Split('/').Last();
            return s_Last.EndsWith("_mesh", StringComparison.OrdinalIgnoreCase) ? s_Last[..^5] : s_Last;
        }

        string SetKeyOf(string p_Mesh, string p_Hash) => s_MultiMesh ? $"{p_Mesh}|{p_Hash}" : p_Hash;

        string SetLabelOf(string p_Mesh, string p_Hash, string p_VariationName) => s_MultiMesh
            ? p_Hash == "0" ? ShortName(p_Mesh) : $"{ShortName(p_Mesh)} · {ShortName(p_VariationName)}"
            : p_VariationName == "(base)" ? "(base)" : ShortName(p_VariationName);

        var s_VariationSets = s_TextureRows
            .GroupBy(p_R => SetKeyOf(p_R.Mesh, p_R.Hash))
            .ToDictionary(p_G => p_G.Key, p_G => (
                Name: SetLabelOf(p_G.First().Mesh, p_G.First().Hash, p_G.First().Name),
                Mesh: p_G.First().Mesh,
                Hash: p_G.First().Hash,
                Asset: p_G.First().Name,
                Values: p_G.GroupBy(p_R => p_R.Param)
                    .ToDictionary(p_P => p_P.Key, p_P => p_P.First().Texture)));

        // Pairs that carried no texture rows still become sets (empty texture dict): a streamable prop's
        // base entry is one, and it is the seed a "create new variation" bake starts from.
        foreach (var s_MeshRow in s_MeshRows)
        {
            var s_SeedKey = SetKeyOf(s_MeshRow.Mesh, s_MeshRow.Hash);
            if (!s_VariationSets.ContainsKey(s_SeedKey))
                s_VariationSets[s_SeedKey] = (
                    Name: SetLabelOf(s_MeshRow.Mesh, s_MeshRow.Hash, s_MeshRow.Name),
                    Mesh: s_MeshRow.Mesh,
                    Hash: s_MeshRow.Hash,
                    Asset: s_MeshRow.Name,
                    Values: new Dictionary<string, string>());
        }

        // The default set: the first base (hash 0) one, else simply the first.
        var s_DefaultSet = s_VariationSets.Keys
            .OrderBy(p_K => s_VariationSets[p_K].Hash == "0" ? 0 : 1)
            .ThenBy(p_K => p_K, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        // The material instance's VECTOR parameters (cb1 values), per set: a mesh's material-level rows are
        // its base, its variation ASSET's rows override them for that variation.
        var s_VectorRows = Regex
            .Matches(s_List.Output,
                @"SHMATVEC:\s*shader=(\S+)\s+mesh=(\S+)\s+variation=(\d+)\s+variationName=.+?\s+source=(\S+)\s+param=(\S+)\s+value=(\S+)")
            .Select(p_M => (Shader: p_M.Groups[1].Value, Mesh: p_M.Groups[2].Value, Hash: p_M.Groups[3].Value,
                Source: p_M.Groups[4].Value, Param: p_M.Groups[5].Value, Value: p_M.Groups[6].Value))
            .Where(p_R => IsTargetRow(p_R.Shader, p_Target))
            .ToList();

        Dictionary<string, string>? VectorValuesFor(string p_SetKey)
        {
            if (!s_VariationSets.TryGetValue(p_SetKey, out var s_Set))
                return null;

            var s_Result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s_Row in s_VectorRows.Where(p_R => p_R.Mesh == s_Set.Mesh &&
                                                            p_R.Source == "material"))
                s_Result[s_Row.Param] = s_Row.Value;

            foreach (var s_Row in s_VectorRows.Where(p_R => p_R.Mesh == s_Set.Mesh &&
                                                            p_R.Hash == s_Set.Hash &&
                                                            p_R.Source == "variationAsset"))
                s_Result[s_Row.Param] = s_Row.Value;

            return s_Result.Count > 0 ? s_Result : null;
        }

        // The DEFAULT set's textures feed the base pairing; any other set resolves what it leaves unset.
        var s_MaterialValues = s_DefaultSet != null
            ? new Dictionary<string, string>(s_VariationSets[s_DefaultSet].Values)
            : new Dictionary<string, string>();

        foreach (var s_Set in s_VariationSets.Values)
            foreach (var (s_Param, s_Texture) in s_Set.Values)
                if (!s_MaterialValues.ContainsKey(s_Param))
                    s_MaterialValues[s_Param] = s_Texture;

        var s_Merged = new List<(int Index, string Name, float Factor)>();

        // The slot list supersedes the streamable list when present (it is the same textures plus the shared
        // ones); the factor still comes from the streamable row of the same name.
        if (s_SlotList.Count > 0)
            foreach (var (s_Slot, s_Name) in s_SlotList)
                s_Merged.Add((s_Slot, s_Name,
                    s_StreamableRows.FirstOrDefault(p_S => p_S.Name == s_Name).Factor is var s_F && s_F != 0f
                        ? s_F
                        : 1f));
        else
            s_Merged.AddRange(s_StreamableRows);

        // The stream part is shared by every variation; only the external values change between them.
        var s_StreamPart = s_Merged.ToList();

        // An external whose register the bytecode NAMES (texture_<param>) is bound directly — the class
        // pairing below only competes for the registers that stayed anonymous (texture_TextureN).
        var s_DirectRegisters = new List<int>();

        foreach (var (s_Slot, s_Param) in s_ExternalSlots)
        {
            if (!s_MaterialValues.TryGetValue(s_Param, out var s_Texture))
            {
                s_Result.Add($"  external '{s_Param}': no material in the mesh variation database names a " +
                             "texture for it; that register stays on the placeholder.");
                continue;
            }

            var s_Named = p_Contract?.RegisterForExternalTexture(s_Param) ?? -1;
            if (s_Named >= 0)
            {
                s_Slots[s_Named.ToString()] = s_Texture;
                s_DirectRegisters.Add(s_Named);
            }
            else
            {
                s_Merged.Add((1000 + s_Slot, s_Texture,
                    s_ExternalFactors.TryGetValue(s_Param, out var s_ExtFactor) ? s_ExtFactor : 1f));
            }
        }

        if (s_Merged.Count > 0)
        {
            if (p_Contract == null)
            {
                s_Result.Add($"{s_Merged.Count} texture(s) named for this shader, but no contract for it yet, " +
                             "so their registers are unknown. Press Load on the level to detect contracts, then " +
                             "load the textures again.");
            }
            else
            {
                s_Result.Add($"{s_Merged.Count} texture(s) named for this shader " +
                             $"({s_SlotList.Count} slot, {s_ExternalSlots.Count} external, " +
                             $"{s_DirectRegisters.Count} bound by the register's own name); the rest paired " +
                             "by how the shader USES each slot (normal-map decode vs colour), in list order " +
                             "within each class.");

                foreach (var (s_Register, s_Name) in p_Contract.PairStreamables(s_Merged, s_DirectRegisters))
                    s_Slots[s_Register.ToString()] = s_Name;

                foreach (var s_Entry in s_Merged.Where(p_S => !s_Slots.ContainsValue(p_S.Name)))
                    s_Result.Add($"  [{s_Entry.Index}] {s_Entry.Name}: more textures than material slots in " +
                                 "this permutation; not loaded.");
            }
        }

        if (s_Slots.Count == 0)
        {
            s_Result.Add($"No texture slots reported for '{p_Target}'. Check the shader name and the shaderdb path.");
            s_Result.Add($"  (shaderdb used: {p_ShaderDb})");
            s_Result.Add("  RimeREPL said:");
            foreach (var s_Line in s_List.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(6))
                s_Result.Add($"    {s_Line.TrimEnd()}");

            return s_Result;
        }

        s_Result.Add($"Found {s_Slots.Count} texture slot(s): " +
                     string.Join(", ", s_Slots.Select(p_S => $"t{p_S.Key}={p_S.Value}")));

        // Pair each remaining texture set the same way the default was paired: shared stream part + that
        // set's external values (default values fill what a set does not override).
        var s_VariationMaps = new Dictionary<string, VariationSlots>();
        if (p_Contract != null)
            foreach (var (s_Key, s_Set) in s_VariationSets)
            {
                if (s_Key == s_DefaultSet)
                    continue;

                var s_VariationSlots = new VariationSlots
                {
                    Name = s_Set.Name, Mesh = s_Set.Mesh, AssetName = s_Set.Asset,
                    Values = VectorValuesFor(s_Key),
                };
                var s_VariationMerged = s_StreamPart.ToList();
                var s_VariationDirect = new List<int>();

                foreach (var (s_Slot, s_Param) in s_ExternalSlots)
                {
                    if (!s_Set.Values.TryGetValue(s_Param, out var s_Texture) &&
                        !s_MaterialValues.TryGetValue(s_Param, out s_Texture))
                        continue;

                    var s_Named = p_Contract.RegisterForExternalTexture(s_Param);
                    if (s_Named >= 0)
                    {
                        s_VariationSlots.Slots[s_Named.ToString()] = s_Texture;
                        s_VariationDirect.Add(s_Named);
                    }
                    else
                    {
                        s_VariationMerged.Add((1000 + s_Slot, s_Texture,
                            s_ExternalFactors.TryGetValue(s_Param, out var s_ExtFactor) ? s_ExtFactor : 1f));
                    }
                }

                foreach (var (s_Register, s_Name) in p_Contract.PairStreamables(s_VariationMerged, s_VariationDirect))
                    s_VariationSlots.Slots[s_Register.ToString()] = s_Name;

                s_VariationMaps[s_Key] = s_VariationSlots;
            }

        if (s_VariationMaps.Count > 0)
            s_Result.Add($"{s_VariationMaps.Count + 1} texture set(s) for this shader: " +
                         $"{(s_DefaultSet != null ? s_VariationSets[s_DefaultSet].Name : "(base)")}, " +
                         string.Join(", ", s_VariationMaps.Values.Select(p_V => p_V.Name)));

        // One dump per distinct texture NAME across every set, all inside this same mount - switching
        // variation later is then a pure cache read, no remount.
        var s_AllTextures = new HashSet<string>(s_Slots.Values, StringComparer.OrdinalIgnoreCase);
        foreach (var s_Variation in s_VariationMaps.Values)
            s_AllTextures.UnionWith(s_Variation.Slots.Values);

        var s_DumpScript = new StringBuilder();
        s_DumpScript.AppendLine($"mount_game \"{p_GamePath}\" Frostbite2_0 true");
        s_DumpScript.AppendLine("select_game 1");

        var s_Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s_Name in s_AllTextures)
        {
            var s_File = Path.Combine(p_CacheDir, $"{Sanitize(s_Name)}.dds");
            s_Files[s_Name] = s_File;
            s_DumpScript.AppendLine($"dump_texture \"{s_Name}\" \"{s_File}\"");
        }

        s_DumpScript.AppendLine("exit");

        var s_DumpScriptPath = Path.Combine(p_CacheDir, "dump.rime");
        File.WriteAllText(s_DumpScriptPath, s_DumpScript.ToString());
        Run(p_Repl, new[] { s_DumpScriptPath });

        var s_Decoded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (s_Name, s_File) in s_Files)
        {
            if (!File.Exists(s_File))
            {
                s_Result.Add($"  {s_Name}: no DDS produced (texture may be a streaming/residency case).");
                continue;
            }

            var s_Image = DdsImage.Load(s_File);
            if (s_Image == null)
            {
                s_Result.Add($"  {s_Name}: DDS not decodable for preview ({new FileInfo(s_File).Length} B).");
                continue;
            }

            var s_Png = Path.Combine(p_CacheDir, $"{Sanitize(s_Name)}.png");
            var s_Encoder = new PngBitmapEncoder();
            s_Encoder.Frames.Add(BitmapFrame.Create(s_Image));
            using (var s_Stream = File.Create(s_Png))
                s_Encoder.Save(s_Stream);

            s_Decoded.Add(s_Name);
        }

        var s_Saved = new Dictionary<string, string>();
        foreach (var (s_Register, s_Name) in s_Slots)
        {
            if (!s_Decoded.Contains(s_Name))
                continue;

            s_Saved[s_Register] = s_Name;
            s_Result.Add($"  t{s_Register}: thumbnail -> {Sanitize(s_Name)}.png");
        }

        var s_BaseValues = s_DefaultSet != null ? VectorValuesFor(s_DefaultSet) : null;
        if (s_BaseValues != null)
            s_Result.Add($"{s_BaseValues.Count} instance value(s) from the level's material data: " +
                         string.Join(", ", s_BaseValues.Select(p_V => $"{p_V.Key}={p_V.Value}")));

        var s_VariationsOut = new Dictionary<string, VariationSlots>
        {
            [s_DefaultSet ?? "0"] = new()
            {
                Name = s_DefaultSet != null ? s_VariationSets[s_DefaultSet].Name : "(base)",
                Mesh = s_DefaultSet != null ? s_VariationSets[s_DefaultSet].Mesh : null,
                AssetName = s_DefaultSet != null ? s_VariationSets[s_DefaultSet].Asset : null,
                Slots = s_Saved,
                Values = s_BaseValues,
            },
        };

        foreach (var (s_Hash, s_Variation) in s_VariationMaps)
            s_VariationsOut[s_Hash] = s_Variation;

        File.WriteAllText(p_MapPath, System.Text.Json.JsonSerializer.Serialize(
            new SlotMap
            {
                V = 5, Slots = s_Saved, Variations = s_VariationsOut,
                ExternalDefaults = s_ExternalDefaults.Count > 0 ? s_ExternalDefaults : null,
                MeshUsers = s_MeshUsers.Count,
                MeshUserSample = s_MeshUsers.Take(3).ToList(),
                MeshUsersScope = s_MvdbScope,
            }));
        return s_Result;
    }

    /// <summary>What one press of "Translate target to graph" produced, so the caller can report all of it.</summary>
    internal sealed class TranslateResult
    {
        public ShaderGraph? Graph { get; set; }

        /// <summary>The exact bytecode that was translated, kept so --shaderdiff can be run against THIS file.</summary>
        public string? OriginalDxbc { get; set; }

        public string? SourceFile { get; set; }

        /// <summary>
        /// The contract of the permutation that was ACTUALLY translated. It has to travel with the graph: the
        /// texture registers a shader uses differ between its own permutations (the Barrack's plain pass holds
        /// its material at t1..t4, its lightmapped one at t5..t8), so a slot map built from a different
        /// permutation puts the art in registers the graph never samples - which renders as a blank object.
        /// </summary>
        public ShaderContract? Contract { get; set; }

        public int Instructions { get; set; }
        public List<string> Warnings { get; } = new();

        /// <summary>Opcodes with no node, and cbuffer fields with no node. Either one makes the graph inexact.</summary>
        public List<string> Untranslated { get; } = new();

        public List<string> Unmodelled { get; } = new();
        public List<string> Log { get; } = new();
    }

    /// <summary>
    /// The whole "see this shader as a graph" cycle: pull the target's compiled bytecode out of the game,
    /// disassemble it with fxc, and translate it into nodes.
    ///
    /// It does NOT recover the graph DICE authored - that is destroyed at bake time, and many graphs compile to
    /// the same bytecode, so there is no unique original to recover. It builds ONE graph that computes the same
    /// thing, which is a claim --shaderdiff can actually prove.
    ///
    /// Static and synchronous so the headless seam runs the identical code the button runs.
    /// </summary>
    internal static TranslateResult TranslateCore(string p_Repl, string p_Fxc, string p_GamePath,
        string p_ShaderDb, string p_Target, string p_WorkDir, ShaderContract? p_Contract)
    {
        var s_Result = new TranslateResult();
        Directory.CreateDirectory(p_WorkDir);

        var s_Extract = Path.Combine(p_WorkDir, Sanitize(p_Target));
        if (Directory.Exists(s_Extract))
            try { Directory.Delete(s_Extract, true); } catch { }

        var s_Script = Path.Combine(p_WorkDir, "extract.rime");
        File.WriteAllText(s_Script,
            $"mount_game \"{p_GamePath}\" Frostbite2_0 true\nselect_game 1\n" +
            $"extract_shader_dxbc {p_ShaderDb} \"{p_Target}\" \"{s_Extract}\"\nexit\n");

        var s_Run = Run(p_Repl, new[] { s_Script });

        // The extractor writes straight into the output folder for a single match, and numbered subfolders plus
        // an index.txt when the name matched several shaders. Both shapes have to be handled: picking the wrong
        // folder would silently translate a different shader.
        var s_Folder = s_Extract;
        var s_IndexPath = Path.Combine(s_Extract, "index.txt");
        if (File.Exists(s_IndexPath))
        {
            var s_Rows = File.ReadAllLines(s_IndexPath)
                .Select(p_L => p_L.Split('\t'))
                .Where(p_P => p_P.Length >= 2)
                .ToList();

            var s_Row = s_Rows.FirstOrDefault(
                            p_P => p_P[1].Equals(p_Target, StringComparison.OrdinalIgnoreCase))
                        ?? s_Rows.FirstOrDefault();

            if (s_Row == null)
            {
                s_Result.Log.Add($"'{p_Target}' matched several shaders but none of them is that exact name.");
                return s_Result;
            }

            s_Folder = Path.Combine(s_Extract, s_Row[0]);
            if (s_Rows.Count > 1)
                s_Result.Log.Add($"The name matched {s_Rows.Count} shaders; translating the exact match '{s_Row[1]}'.");
        }

        var s_Candidates = Directory.Exists(s_Folder)
            ? new DirectoryInfo(s_Folder).GetFiles("*_ps.dxbc").OrderByDescending(p_F => p_F.Length).ToList()
            : new List<FileInfo>();

        if (s_Candidates.Count == 0)
        {
            s_Result.Log.Add($"No pixel shader came out of the game for '{p_Target}'.");
            s_Result.Log.Add($"  (shaderdb used: {p_ShaderDb})");
            s_Result.Log.Add("  RimeREPL said:");
            foreach (var s_Line in s_Run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(6))
                s_Result.Log.Add($"    {s_Line.TrimEnd()}");

            return s_Result;
        }

        var s_Chosen = ShaderIndex.ChooseGBufferPermutation(s_Candidates, s_Result.Log);
        s_Result.SourceFile = s_Chosen.Name;

        // ⚠ Copied to a short name first: fxc /dumpbin fails on long paths with nothing but "compilation failed;
        // no code produced", and the extracted names are long.
        var s_Short = Path.Combine(p_WorkDir, "target.dxbc");
        File.Copy(s_Chosen.FullName, s_Short, true);
        s_Result.OriginalDxbc = s_Short;

        // Detected from the very bytes about to be translated, so the registers the caller loads textures into
        // are by construction the registers the graph samples.
        try { s_Result.Contract = ShaderContract.Detect(File.ReadAllBytes(s_Short)); }
        catch { }

        var s_Asm = Path.Combine(p_WorkDir, "target.asm");
        if (File.Exists(s_Asm))
            File.Delete(s_Asm);

        var s_Dump = Run(p_Fxc, new[] { "/nologo", "/dumpbin", s_Short, "/Fc", s_Asm });
        if (!File.Exists(s_Asm))
        {
            s_Result.Log.Add($"fxc could not disassemble it (exit {s_Dump.ExitCode}).");
            foreach (var s_Line in s_Dump.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(4))
                s_Result.Log.Add($"    {s_Line.TrimEnd()}");

            return s_Result;
        }

        var s_Instructions = Translate.DxbcAsm.Parse(File.ReadAllLines(s_Asm));

        // Classified BEFORE the build: the builder folds UV interpolators into TexCoord nodes, and the
        // classification is what says which interpolators ARE UVs. The sweep runs in this order already.
        s_Result.Contract?.ClassifyInterpolators(s_Instructions);

        // ⛔ The builder reads cbuffer FIELD NAMES through the contract, so it must get the one detected from
        // the very bytes being translated - the caller's contract belongs to whatever the browser showed LAST
        // (or is null in a fresh window), and with it externalConstants fields resolve to nothing and every
        // external silently becomes a literal 0. Caught on XP2: spec/smoothness alphas off while RGB was exact.
        var s_Builder = new Translate.GraphBuilder(s_Result.Contract ?? p_Contract);
        var s_Name = p_Target.Split('/').Last();

        s_Result.Graph = s_Builder.Build(s_Instructions, p_Target, s_Name);
        s_Result.Instructions = s_Instructions.Count;

        // The authored surface facts the bytecode cannot carry, stamped from the shaderdb's own record
        // (the extractor writes them next to the permutations): the real SurfaceShaderType — a bush is
        // OpaqueAlphaTest, not the Opaque default — and whether its solutions render double-sided.
        var s_InfoPath = Path.Combine(s_Folder, "shaderinfo.txt");
        if (s_Result.Graph?.Root is { } s_TranslatedRoot && File.Exists(s_InfoPath))
            foreach (var s_InfoLine in File.ReadAllLines(s_InfoPath))
            {
                var s_Pair = s_InfoLine.Split('=', 2);
                if (s_Pair.Length != 2)
                    continue;

                if (s_Pair[0] == "type" && s_TranslatedRoot.Def.Params.Any(p_P => p_P.Name == "SurfaceShaderType"))
                    s_TranslatedRoot.Params["SurfaceShaderType"] = s_Pair[1].Trim();
                if (s_Pair[0] == "doubleSided" && s_TranslatedRoot.Def.Params.Any(p_P => p_P.Name == "DoubleSided"))
                    s_TranslatedRoot.Params["DoubleSided"] = s_Pair[1].Trim() == "1" ? "true" : "false";
            }

        // Which registers this permutation decodes as normal maps, read from the graph the translation just
        // built (its NormalMap nodes carry the register). The texture loader pairs streamables BY that usage,
        // which is the rule that survived measurement - see ShaderContract.PairStreamables.
        if (s_Result.Contract != null && s_Result.Graph != null)
            s_Result.Contract.NormalTextureRegisters = s_Result.Graph.Nodes
                .Where(p_N => p_N.Kind == "NormalMap")
                .Select(p_N => int.TryParse(p_N.GetParam("Register"), out var s_Register) ? s_Register : -1)
                .Where(p_R => p_R > 0)
                .Distinct()
                .ToList();

        // And what each texture scale IS, by the same principle: the shader's own instructions name them.
        // (Interpolators were classified before the build - the builder needs them.)
        s_Result.Contract?.ClassifyTextureScales(s_Instructions);

        // The AS-TRANSLATED fingerprint: what this graph's HLSL hashes to before the user touches anything.
        // A later variation bake re-emits and compares — equal means "only textures changed" (the translated
        // graph is equivalence-verified, so same emission = vanilla logic by construction); different means
        // the shader was reworked and needs its own cloned entry. Errs SAFE: a spurious mismatch only costs
        // an unnecessary clone, never a missing one.
        if (s_Result.Graph != null)
            s_Result.Graph.TranslatedHlslHash = HlslFingerprint(s_Result.Graph, s_Result.Contract);

        // Whether the graph came out as the artist's handful of nodes or as the literal instruction-by-instruction
        // translation - and if the latter, WHY. Without this the only thing a long graph tells you is that it is
        // long, and there is no way to tell "this shader is genuinely complicated" from "the matcher gave up".
        s_Result.Log.Add(s_Builder.FrameRecognised
            ? "Recognised as a standard surface shader: the g-buffer frame is the root node, so what you see is " +
              "the material itself."
            : $"NOT the standard frame ({s_Builder.FrameRejection}), so this is the literal translation - one " +
              "node per operation. Every node is needed for it to compute the same thing; there is just no " +
              "shorter way to say it yet.");
        s_Result.Warnings.AddRange(s_Builder.Warnings.Distinct());
        s_Result.Untranslated.AddRange(s_Builder.UntranslatedOpcodes.OrderBy(p_O => p_O, StringComparer.Ordinal));
        s_Result.Unmodelled.AddRange(s_Builder.UnmodelledConstants.OrderBy(p_C => p_C, StringComparer.Ordinal));
        return s_Result;
    }

    /// <summary>Completed by the click handler so the headless seam can wait for the async work to finish.</summary>
    internal static TaskCompletionSource<int>? TranslateFinished;

    private async void OnTranslateShader(object p_Sender, RoutedEventArgs p_Args) => await RunTranslateAsync();

    /// <summary>
    /// The translate step itself. Split out from the button handler so picking a target can run it directly:
    /// an `async void` handler cannot be awaited, and chaining these two steps needs to know when the first
    /// one finished before the second reads the contract it produced.
    /// </summary>
    /// <returns>True when a graph actually landed on the canvas.</returns>
    private async Task<bool> RunTranslateAsync()
    {
        var s_Target = TargetShaderBox.Text.Trim();
        if (s_Target.Length == 0)
        {
            Log("ERROR: pick a target shader first - the translation reads THAT shader's bytecode.");
            TranslateFinished?.TrySetResult(0);
            return false;
        }

        var s_GamePath = GamePathBox.Text.Trim();
        if (s_GamePath.Length == 0 || !Directory.Exists(s_GamePath))
        {
            Log("ERROR: the BF3 path is empty or does not exist - fill in the 'BF3 path' box.");
            TranslateFinished?.TrySetResult(0);
            return false;
        }

        var s_Repl = FindRimeRepl();
        var s_Fxc = FindFxc();
        if (s_Repl == null || s_Fxc == null)
        {
            Log(s_Repl == null
                ? "ERROR: RimeREPL.exe not found (expected under Rime-src\\bin\\Release)."
                : "ERROR: fxc.exe not found. Install the Windows SDK or put fxc.exe on PATH.");

            TranslateFinished?.TrySetResult(0);
            return false;
        }

        ReloadButton.IsEnabled = false;
        Log($"Reading '{s_Target}' out of the game and translating it - one mount, so ~20 s…");

        try
        {
            var s_Db = ResolveShaderDb(s_Target);
            var s_Work = Path.Combine(OutputFolderBox.Text.Trim(), "translate");
            var s_Contract = m_Contract;

            var s_Result = await Task.Run(() =>
                TranslateCore(s_Repl, s_Fxc, s_GamePath, s_Db, s_Target, s_Work, s_Contract));

            foreach (var s_Line in s_Result.Log)
                Log(s_Line);

            if (s_Result.Graph == null)
            {
                Log("Nothing was translated; the graph on the canvas is untouched.");
                return false;
            }

            // Retargeting replaces the canvas, so it goes on the undo stack first: leaving the previous graph up
            // is what made the editor look like it had loaded something when it had not.
            Canvas.PushUndo();
            LoadGraph(s_Result.Graph);

            // The generated graph lands wherever the layout put it, which is rarely where the view is looking.
            Canvas.FrameGraph();

            // ⛔ Adopt the translated permutation's contract, and DROP the stale thumbnails: the cached slot map
            // was built for whatever permutation the level catalogue had picked, and keeping it is how the art
            // ends up in registers this graph never samples - which draws as a blank object with no error.
            if (s_Result.Contract != null)
            {
                m_Contract = s_Result.Contract;
                m_TexturePreviews.Clear();

                // The preview's vertex shader now speaks THIS shader's interpolator language: without it an
                // "Unknown" family got worldpos/tangent-rows where its own bytecode says normal/uv, and drew a
                // flat grey cube while every differential number stayed green.
                m_Preview.SetInterpolatorMeanings(s_Result.Contract.InterpolatorMeanings);
                PushInstanceValues(null);

                var s_Slots = string.Join(", ", s_Result.Contract.MaterialTextures.Select(p_T => "t" + p_T.Register));
                Log($"  contract taken from the translated permutation; its material sits in {s_Slots}.");
            }

            Log($"{s_Result.Instructions} instruction(s) -> {s_Result.Graph.Nodes.Count} nodes, " +
                $"{s_Result.Graph.Connections.Count} connections. Ctrl+Z restores the previous graph.");

            foreach (var s_Warning in s_Result.Warnings)
                Log("  note: " + s_Warning);

            // ⚠ Two different ways to be inexact, and both have to be said out loud. A missing opcode leaves a
            // register at its old value; an unmodelled cbuffer field shades with a zero where the game has a real
            // constant. Neither raises an error anywhere: the graph compiles and renders either way.
            if (s_Result.Untranslated.Count > 0)
                Log($"  ⚠ NOT EQUIVALENT: no node yet for {string.Join(", ", s_Result.Untranslated)}, so those " +
                    "instructions were skipped and their results are wrong. Everything else is faithful.");

            if (s_Result.Unmodelled.Count > 0)
                Log($"  ⚠ NOT EQUIVALENT: {string.Join(", ", s_Result.Unmodelled)} came through as 0 - the " +
                    "editor has no node for those constants yet. The shape of the graph is right, those values " +
                    "are not.");

            if (s_Result.Untranslated.Count == 0 && s_Result.Unmodelled.Count == 0)
                Log("  every instruction and every constant had a node. Prove it: --shaderdiff <saved graph> " +
                    $"\"{s_Result.OriginalDxbc}\" \"{TexCacheDir}\"");

            Log("  the graph is GENERATED from the bytecode, not the original authored one - that is stripped " +
                "at bake time. It computes the same thing, which is the part that can be proven.");

            return true;
        }
        catch (Exception s_Exception)
        {
            Log($"TRANSLATE ERROR: {s_Exception.Message}");
            return false;
        }
        finally
        {
            ReloadButton.IsEnabled = true;
            TranslateFinished?.TrySetResult(Canvas.Graph.Nodes.Count);
        }
    }

    /// <summary>
    /// Presses the Translate button on a window that is never shown, and waits for the async handler to finish.
    /// The point is that it goes in where keku goes in - the real click, the real handler, the real game mount -
    /// rather than calling TranslateCore directly, which would skip exactly the wiring that tends to break.
    /// </summary>
    /// <summary>
    /// Drives a whole target SELECTION headlessly and checks the two things keku expects to be true afterwards
    /// without touching anything else: a graph on the canvas, and its textures in their slots.
    ///
    /// ⚠ What it does not cover, said plainly: pulling the shader name out of the clicked TreeViewItem. That is
    /// one line and it is the part that was already working; everything downstream of it is exercised here.
    /// </summary>
    internal static int SelectSelfTest(string p_Target, string p_WorkDir, string? p_GamePath,
        string? p_Level = null)
    {
        var s_Window = new MainWindow();
        s_Window.OutputFolderBox.Text = p_WorkDir;

        // Simulates "the browser has this level loaded", which routes a non-Levels target to that level's
        // shaderdb - the double click this test mirrors always happens with a level loaded.
        if (!string.IsNullOrWhiteSpace(p_Level))
            s_Window.m_LoadedLevel = p_Level;

        if (!string.IsNullOrWhiteSpace(p_GamePath))
            s_Window.GamePathBox.Text = p_GamePath;

        var s_Finished = new TaskCompletionSource<int>();
        SelectFinished = s_Finished;

        // Fire and forget exactly as the double click does, then pump: the handler is async, so without a
        // message loop its continuation after the first await would never run.
        _ = s_Window.SelectTargetAsync(p_Target);

        var s_Frame = new System.Windows.Threading.DispatcherFrame();
        var s_Timeout = Task.Delay(TimeSpan.FromMinutes(10));
        Task.WhenAny(s_Finished.Task, s_Timeout).ContinueWith(_ => s_Frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(s_Frame);
        SelectFinished = null;

        Console.Out.WriteLine(s_Window.LogBox.Text.TrimEnd());

        if (!s_Finished.Task.IsCompleted)
        {
            Console.Error.WriteLine("SELECTTEST: FAIL - the selection never finished (10 min timeout).");
            return 1;
        }

        var s_Nodes = s_Finished.Task.Result;
        var s_Textures = s_Window.m_TexturePreviews.Count;
        Console.Out.WriteLine($"after ONE selection: {s_Nodes} node(s) on the canvas, " +
                              $"{s_Textures} texture(s) loaded, target '{s_Window.Canvas.Graph.TargetShader}'");

        if (s_Nodes == 0 || s_Textures == 0)
        {
            Console.Error.WriteLine(s_Nodes == 0
                ? "SELECTTEST: FAIL - selecting the shader left the canvas empty."
                : "SELECTTEST: FAIL - selecting the shader translated it but loaded no textures.");

            return 1;
        }

        // When the map carries several texture sets, drive the picker the way a user does and prove the
        // thumbnails actually swap - from cache, no remount.
        var s_Full = ReadSlotMapFull(s_Window.SlotMapPath(p_Target));
        if (s_Full?.Variations is { Count: > 1 } s_Variations)
        {
            Console.Out.WriteLine($"{s_Variations.Count} texture set(s): " +
                                  string.Join(", ", s_Variations.Values.Select(p_V => p_V.Name)));

            var s_PreviousSources = s_Window.m_TexturePreviews
                .ToDictionary(p_P => p_P.Key, p_P => (p_P.Value as BitmapImage)?.UriSource?.ToString() ?? "");

            // The set to switch to must carry DIFFERENT art than the one showing: on a shared shader the
            // keys are (mesh, variation), so "any other key" can land on a sibling base set with identical
            // textures and the swap check would fail against a working picker.
            var s_Showing = s_Variations.TryGetValue(s_Window.m_Variation, out var s_Selected)
                ? s_Selected.Slots
                : s_Full.Slots;

            var s_Other = s_Window.VariationBox.Items.Cast<VariationChoice>()
                .FirstOrDefault(p_C => p_C.Hash != s_Window.m_Variation &&
                                       s_Variations.TryGetValue(p_C.Hash, out var s_Candidate) &&
                                       s_Candidate.Slots.Any(p_S =>
                                           !s_Showing.TryGetValue(p_S.Key, out var s_Old) ||
                                           !string.Equals(s_Old, p_S.Value, StringComparison.OrdinalIgnoreCase)));

            if (s_Other == null)
            {
                Console.Error.WriteLine("SELECTTEST: FAIL - variations in the map but none carries different art.");
                return 1;
            }

            s_Window.VariationBox.SelectedItem = s_Other;

            var s_Swapped = s_Window.m_TexturePreviews.Count > 0 && s_Window.m_TexturePreviews.Any(p_P =>
                !s_PreviousSources.TryGetValue(p_P.Key, out var s_Old) ||
                s_Old != ((p_P.Value as BitmapImage)?.UriSource?.ToString() ?? ""));

            Console.Out.WriteLine($"picking '{s_Other.Name}' swapped the thumbnails from cache: " +
                                  $"{(s_Swapped ? "OK" : "FAIL")}");

            if (!s_Swapped)
            {
                Console.Error.WriteLine("SELECTTEST: FAIL - switching variation did not change any thumbnail.");
                return 1;
            }
        }

        Console.Out.WriteLine("SELECTTEST: PASS - one selection translated the shader AND filled its textures.");
        return 0;
    }

    /// <summary>
    /// ⛔ THE SURFACE SWEEP. An authoring style is not a palette filter: it is every surface a node name comes
    /// out of. Six separate rounds of "the mode is showing through" were each found by hand, one surface at a
    /// time — palette, search box, shortcuts, drawn caption, the settings list, the startup log — so this walks
    /// them ALL and fails on any that speaks the other vocabulary. It runs in BOTH styles, and the Frostbite
    /// pass is the negative control: there the native names MUST appear, which is what makes a pass mean
    /// something rather than the checks quietly matching nothing.
    /// </summary>
    internal static int UdkSurfaceSelfTest(string p_WorkDir)
    {
        Directory.CreateDirectory(p_WorkDir);
        var s_Failures = new List<string>();

        // Every node the two catalogs name differently: in UDK style our own label is the forbidden word, and
        // in Frostbite style it is the one that has to be there.
        var s_Renamed = Palette.All
            .Where(p_D => p_D.TitleFor(true) != p_D.Title)
            .ToDictionary(p_D => p_D.Kind, p_D => p_D.Title);

        // The alias table is applied by NAME, so an entry for a node that is not registered yet vanishes
        // without a word — which is how TextureSample lost its search names. Nothing may be missed.
        foreach (var s_Miss in Palette.UdkAliasMisses)
            s_Failures.Add($"the UDK alias table names '{s_Miss}', which the palette does not have");

        foreach (var s_Miss in Palette.UdkReachableMisses)
            s_Failures.Add($"the reachable-node list names '{s_Miss}', which the palette does not have");

        try
        {
            foreach (var s_Udk in new[] { true, false })
            {
                var s_Mode = s_Udk ? "udk" : "frostbite";
                EditorSettings.PathOverride = Path.Combine(p_WorkDir, $"settings_{s_Mode}.json");
                new EditorSettings { UdkNodeStyle = s_Udk }.Save();

                var s_Window = new MainWindow();

                // 1) The canvas draws captions from this flag, so it has to agree with the setting before any
                // of the checks below mean anything.
                if (GraphCanvas.UdkStyle != s_Udk)
                    s_Failures.Add($"[{s_Mode}] the canvas vocabulary flag did not follow the setting");

                // 2) The palette: nothing in it may be labelled with the other catalog's word, and no two
                // entries may read the same (a duplicate name is a node the user cannot tell apart).
                var s_Listed = Palette.VisibleFor(s_Udk).Select(p_D => p_D.TitleFor(s_Udk)).ToList();
                foreach (var s_Duplicate in s_Listed.GroupBy(p_T => p_T).Where(p_G => p_G.Count() > 1))
                    s_Failures.Add($"[{s_Mode}] the palette lists '{s_Duplicate.Key}' {s_Duplicate.Count()} times");

                if (s_Udk)
                    foreach (var s_Bad in s_Listed.Where(p_T => s_Renamed.ContainsValue(p_T)))
                        s_Failures.Add($"[{s_Mode}] the palette still lists '{s_Bad}'");

                // 2b) The foreign group is an OVERFLOW, not part of the vocabulary: in the UDK style it must
                // be the LAST heading in the tree and start closed, and in the other style it must not exist
                // at all (there, those nodes are simply the palette).
                var s_Headings = s_Window.PaletteTree.Items.OfType<TreeViewItem>().ToList();
                var s_Foreign = s_Headings.FindIndex(p_I => (p_I.Header as string) == Palette.c_ForeignCategory);

                if (s_Udk)
                {
                    if (s_Foreign < 0)
                        s_Failures.Add($"[{s_Mode}] the palette has no '{Palette.c_ForeignCategory}' heading");
                    else if (s_Foreign != s_Headings.Count - 1)
                        s_Failures.Add($"[{s_Mode}] '{Palette.c_ForeignCategory}' is heading " +
                                       $"{s_Foreign + 1} of {s_Headings.Count}, not the last one");
                    else if (s_Headings[s_Foreign].IsExpanded)
                        s_Failures.Add($"[{s_Mode}] '{Palette.c_ForeignCategory}' starts open");
                }
                else if (s_Foreign >= 0)
                {
                    s_Failures.Add($"[{s_Mode}] '{Palette.c_ForeignCategory}' should not exist in this style");
                }

                // 3) The shortcuts, on both of the surfaces that ADVERTISE them — the startup log and the
                // settings list — checked against what the canvas would actually place.
                var s_Log = s_Window.LogBox.Text;
                var s_DialogText = new List<string>();
                var s_Dialog = new SettingsWindow(s_Window, s_Window.m_Settings, () => { });
                CollectText(s_Dialog.Content, s_DialogText);

                foreach (var (s_Key, s_Kind) in GraphCanvas.Shortcuts.Where(p_S => p_S.Key is >= Key.A and <= Key.Z))
                {
                    var s_Placed = Palette.PlacedTitle(s_Kind, s_Udk);

                    if (!s_Log.Contains($"{s_Key}={s_Placed}"))
                        s_Failures.Add($"[{s_Mode}] the startup log does not announce {s_Key} as '{s_Placed}'");

                    if (!s_DialogText.Contains(s_Placed))
                        s_Failures.Add($"[{s_Mode}] the settings shortcut list has no row reading '{s_Placed}'");

                    // The row must not carry the OTHER vocabulary's word for the very node it binds.
                    if (s_Udk && s_Renamed.TryGetValue(s_Kind, out var s_Native) && s_Placed != s_Native &&
                        s_DialogText.Contains(s_Native))
                        s_Failures.Add($"[{s_Mode}] the settings shortcut list still reads '{s_Native}'");
                }

                s_Dialog.Close();

                // 4) The properties header, for every node the palette offers: place it (which selects it and
                // runs the real BuildProperties) and read the heading the user would see.
                foreach (var s_Def in Palette.VisibleFor(s_Udk))
                {
                    s_Window.Canvas.AddNode(s_Def.Kind);

                    var s_Expected = $"Properties — {s_Def.TitleFor(s_Udk)}";
                    if (s_Window.PropertiesHeader.Text != s_Expected)
                        s_Failures.Add($"[{s_Mode}] properties for '{s_Def.Kind}' headed " +
                                       $"'{s_Window.PropertiesHeader.Text}', expected '{s_Expected}'");
                }

                Console.Out.WriteLine($"  {s_Mode,-9} palette {s_Listed.Count,3} node(s), " +
                                      $"{GraphCanvas.Shortcuts.Count(p_S => p_S.Key is >= Key.A and <= Key.Z)} shortcut(s), " +
                                      $"properties heading checked on every one");
            }
        }
        finally
        {
            EditorSettings.PathOverride = null;
            GraphCanvas.UdkStyle = false;
        }

        foreach (var s_Failure in s_Failures)
            Console.Error.WriteLine("  " + s_Failure);

        if (s_Failures.Count > 0)
        {
            Console.Error.WriteLine($"UDKSURFACETEST: FAIL - {s_Failures.Count} surface(s) speak the wrong vocabulary.");
            return 1;
        }

        Console.Out.WriteLine("UDKSURFACETEST: PASS - every surface that names a node speaks the style in use.");
        return 0;
    }

    /// <summary>Every piece of text a dialog would show, walked through its logical tree.</summary>
    private static void CollectText(object? p_Node, List<string> p_Into)
    {
        switch (p_Node)
        {
            case TextBlock s_Text:
                p_Into.Add(s_Text.Text ?? "");
                break;

            case ContentControl { Content: string s_Content }:
                p_Into.Add(s_Content);
                break;
        }

        if (p_Node is DependencyObject s_Object)
            foreach (var s_Child in LogicalTreeHelper.GetChildren(s_Object))
                CollectText(s_Child, p_Into);
    }

    /// <summary>
    /// Drives the Settings machinery the way the dialog does: file round-trip (against its own scratch file,
    /// never the user's), corrupt-value clamping, shortcut remap reaching the live canvas table, and the FLIR
    /// toggle reaching the preview constants through the real PushInstanceValues path.
    /// </summary>
    internal static int SettingsSelfTest(string p_WorkDir)
    {
        Directory.CreateDirectory(p_WorkDir);
        EditorSettings.PathOverride = Path.Combine(p_WorkDir, "settings_test.json");

        try
        {
            // 1) Round-trip: every new field survives save/load.
            var s_Saved = new EditorSettings
            {
                CanvasZoomSpeed = 2.5,
                PreviewZoomSpeed = 0.5,
                PreviewOrbitSpeed = 1.75,
                FlirPreview = true,
                ExportTexturesAsPng = false,
                NodeShortcuts = { ["Multiply"] = "Q" },
            };
            s_Saved.Save();

            var s_Loaded = EditorSettings.Load();
            if (Math.Abs(s_Loaded.CanvasZoomSpeed - 2.5) > 0.001 ||
                Math.Abs(s_Loaded.PreviewZoomSpeed - 0.5) > 0.001 ||
                Math.Abs(s_Loaded.PreviewOrbitSpeed - 1.75) > 0.001 ||
                !s_Loaded.FlirPreview ||
                s_Loaded.ExportTexturesAsPng ||
                !s_Loaded.NodeShortcuts.TryGetValue("Multiply", out var s_Q) || s_Q != "Q")
            {
                Console.Error.WriteLine("SETTINGSTEST: FAIL - the file round-trip lost a value.");
                return 1;
            }

            // 2) A corrupt value must clamp, not freeze the canvas.
            File.WriteAllText(EditorSettings.PathOverride,
                "{\"CanvasZoomSpeed\": 99, \"PreviewZoomSpeed\": 0}");
            var s_Clamped = EditorSettings.Load();
            if (Math.Abs(s_Clamped.CanvasZoomSpeed - 4.0) > 0.001 ||
                Math.Abs(s_Clamped.PreviewZoomSpeed - 0.25) > 0.001)
            {
                Console.Error.WriteLine("SETTINGSTEST: FAIL - out-of-range values did not clamp.");
                return 1;
            }

            // 3) A remap reaches the LIVE placement table: Q places Multiply, M no longer does.
            GraphCanvas.ApplyShortcuts(new Dictionary<string, string> { ["Multiply"] = "Q" });
            var s_Table = GraphCanvas.Shortcuts.ToList();
            var s_Remapped = s_Table.Any(p_S => p_S.Key == Key.Q && p_S.Kind == "Multiply") &&
                             s_Table.All(p_S => p_S.Key != Key.M || p_S.Kind != "Multiply");
            GraphCanvas.ApplyShortcuts(null);
            if (!s_Remapped)
            {
                Console.Error.WriteLine("SETTINGSTEST: FAIL - the shortcut remap did not reach the canvas table.");
                return 1;
            }

            // 4) The FLIR toggle, through the real preview-constant path. A fabricated contract carries the
            // two thermal fields; toggling the setting must change what PushInstanceValues feeds.
            var s_Window = new MainWindow();
            s_Window.m_Contract = new ShaderContract
            {
                ConstantFields = new List<Emit.DxbcSignature.ConstantField>
                {
                    new() { Name = "external_FLIRData", Register = 1, Offset = 0 },
                    new() { Name = "external_FLIRScale", Register = 1, Offset = 16 },
                },
            };

            s_Window.m_Settings.FlirPreview = false;
            s_Window.PushInstanceValues(null);
            var s_ZeroLogged = s_Window.LogBox.Text.Contains("Thermal parameters (FLIR) read zero");

            var s_Before = s_Window.LogBox.Text.Length;
            s_Window.m_Settings.FlirPreview = true;
            s_Window.PushInstanceValues(null);
            var s_PatternSilent = !s_Window.LogBox.Text[s_Before..].Contains("Thermal parameters");

            if (!s_ZeroLogged || !s_PatternSilent)
            {
                Console.Error.WriteLine("SETTINGSTEST: FAIL - the FLIR toggle did not change the preview feed " +
                                        $"(zeroLogged={s_ZeroLogged}, patternSilent={s_PatternSilent}).");
                return 1;
            }

            Console.Out.WriteLine("SETTINGSTEST: PASS - round-trip, clamping, live remap and the FLIR toggle " +
                                  "all reach their targets.");
            return 0;
        }
        finally
        {
            EditorSettings.PathOverride = null;
        }
    }

    /// <summary>
    /// Drives the tab machinery end to end: a second document opens in its own tab, switching restores the
    /// other document's graph/target/undo history untouched, re-selecting an open target focuses instead of
    /// re-reading, closing falls back to the neighbour — and the strip itself is photographed and probed,
    /// because a tab bar is judged by what it draws.
    /// </summary>
    /// <summary>
    /// Drives the custom-texture machinery: a chosen file overrides the thumbnail/preview for its register,
    /// and PrepareCustomTextures converts through the REAL texconv into the in-game-proven DDS lanes (DXT1
    /// opaque, DXT5 alpha, DXT1+flag normal maps, never DX10) with the right game-name mapping — including
    /// the refusals (unnamed register, missing file), which are what keep a half-shipped bake from existing.
    /// </summary>
    internal static int CustomTextureSelfTest(string p_WorkDir)
    {
        Directory.CreateDirectory(p_WorkDir);

        // Two source images rendered here so the test owns its inputs: one opaque, one with real alpha.
        var s_OpaquePng = Path.Combine(p_WorkDir, "custom_opaque.png");
        var s_AlphaPng = Path.Combine(p_WorkDir, "custom_alpha.png");
        WriteTestPng(s_OpaquePng, 64, 64, p_WithAlpha: false);
        WriteTestPng(s_AlphaPng, 64, 64, p_WithAlpha: true);

        // 1) The preview override: the register's thumbnail becomes OUR file. The startup canvas is empty
        // by design, so the test seeds the node it needs (Register living in Params, the touched shape).
        var s_Window = new MainWindow();
        var s_TextureNode = new GraphNode { Kind = "Texture", X = 100, Y = 100 };
        s_TextureNode.Params["Register"] = "1";
        s_Window.Canvas.Graph.Nodes.Add(s_TextureNode);

        var s_Register = s_TextureNode.Params["Register"];
        s_TextureNode.Params["CustomTexture"] = s_OpaquePng;
        s_Window.ApplyCustomTextureOverrides();

        var s_Overridden = s_Window.m_TexturePreviews.TryGetValue(s_Register, out var s_Thumb) &&
                           (s_Thumb as BitmapImage)?.UriSource?.LocalPath.EndsWith("custom_opaque.png",
                               StringComparison.OrdinalIgnoreCase) == true;

        if (!s_Overridden)
        {
            Console.Error.WriteLine($"CUSTOMTEXTEST: FAIL - t{s_Register} did not take the custom thumbnail.");
            return 1;
        }

        // A FRESHLY PLACED node: empty Params dict, register living only in the palette default. This is the
        // exact shape that silently skipped the override (keku placed a node, chose a file, nothing happened).
        var s_FreshNode = new GraphNode { Kind = "Texture", X = 500, Y = 500 };
        s_FreshNode.Params["CustomTexture"] = s_AlphaPng;
        s_Window.Canvas.Graph.Nodes.Add(s_FreshNode);
        s_Window.ApplyCustomTextureOverrides();

        var s_FreshRegister = s_FreshNode.GetParam("Register");
        var s_FreshTaken = !string.IsNullOrWhiteSpace(s_FreshRegister) &&
                           s_Window.m_TexturePreviews.TryGetValue(s_FreshRegister, out var s_FreshThumb) &&
                           (s_FreshThumb as BitmapImage)?.UriSource?.LocalPath.EndsWith("custom_alpha.png",
                               StringComparison.OrdinalIgnoreCase) == true;

        if (!s_FreshTaken)
        {
            Console.Error.WriteLine("CUSTOMTEXTEST: FAIL - a freshly placed node (default register " +
                                    $"'{s_FreshRegister}') did not take its custom texture.");
            return 1;
        }

        // Independence: nodes placed through the real placement path get their own FREE register each —
        // sharing register 1 with the existing node was how a new node's texture replaced the original's.
        s_Window.Canvas.AddNodeAt("Texture", new Point(600, 600));
        var s_PlacedA = s_Window.Canvas.Graph.Nodes[^1].GetParam("Register");
        s_Window.Canvas.AddNodeAt("Texture", new Point(700, 600));
        var s_PlacedB = s_Window.Canvas.Graph.Nodes[^1].GetParam("Register");

        var s_UsedBefore = s_Window.Canvas.Graph.Nodes[..^2]
            .Where(p_N => TextureNodeKinds.Contains(p_N.Kind))
            .Select(p_N => p_N.GetParam("Register"))
            .ToHashSet();

        if (s_PlacedA == s_PlacedB || s_UsedBefore.Contains(s_PlacedA) || s_UsedBefore.Contains(s_PlacedB))
        {
            Console.Error.WriteLine($"CUSTOMTEXTEST: FAIL - placed nodes were not independent " +
                                    $"(registers '{s_PlacedA}' and '{s_PlacedB}', used: {string.Join(",", s_UsedBefore)}).");
            return 1;
        }

        // 2) The conversion, through the real texconv. One graph with the three lanes.
        var s_Graph = new ShaderGraph();
        s_Graph.Nodes.Add(new GraphNode
        {
            Kind = "Texture",
            Params = { ["Register"] = "1", ["CustomTexture"] = s_OpaquePng },
        });
        s_Graph.Nodes.Add(new GraphNode
        {
            Kind = "Texture",
            Params = { ["Register"] = "2", ["CustomTexture"] = s_AlphaPng },
        });
        s_Graph.Nodes.Add(new GraphNode
        {
            Kind = "NormalMap",
            Params = { ["Register"] = "3", ["CustomTexture"] = s_OpaquePng },
        });

        var s_Slots = new Dictionary<string, string>
        {
            ["1"] = "test/diffuse_name",
            ["2"] = "test/decal_name",
            ["3"] = "test/normal_name",
        };

        var (s_Textures, s_Error) = PrepareCustomTextures(s_Graph, s_Slots,
            Path.Combine(p_WorkDir, "conv"), Console.Out.WriteLine);

        if (s_Error != null)
        {
            Console.Error.WriteLine($"CUSTOMTEXTEST: FAIL - conversion refused: {s_Error}");
            return 1;
        }

        static string FourCcOf(string p_Dds)
        {
            var s_Header = new byte[96];
            using var s_Stream = File.OpenRead(p_Dds);
            _ = s_Stream.Read(s_Header, 0, s_Header.Length);
            return System.Text.Encoding.ASCII.GetString(s_Header, 84, 4);
        }

        var s_ByName = s_Textures.ToDictionary(p_T => p_T.Name);
        var s_Lanes =
            s_Textures.Count == 3 &&
            s_ByName["test/diffuse_name"] is { Srgb: true, Normal: false } s_Diffuse &&
            FourCcOf(s_Diffuse.DdsPath) == "DXT1" &&
            s_ByName["test/decal_name"] is { Srgb: true, Normal: false } s_Decal &&
            FourCcOf(s_Decal.DdsPath) == "DXT5" &&
            s_ByName["test/normal_name"] is { Srgb: false, Normal: true } s_Normal &&
            FourCcOf(s_Normal.DdsPath) == "DXT1";

        Console.Out.WriteLine($"lanes: {string.Join("; ", s_Textures.Select(p_T => $"{p_T.Name} -> " +
            $"{FourCcOf(p_T.DdsPath)} srgb={p_T.Srgb} normal={p_T.Normal}"))}");

        if (!s_Lanes)
        {
            Console.Error.WriteLine("CUSTOMTEXTEST: FAIL - a converted texture landed in the wrong lane.");
            return 1;
        }

        // 3a) A register the material does NOT bind is the NEW-SLOT lane: a fresh name under custom/, the
        // slot surgery marked with the graph's target, and the pool group borrowed from a bound sibling.
        var (s_SlotTextures, s_SlotError) = PrepareCustomTextures(s_Graph,
            new Dictionary<string, string> { ["1"] = "test/diffuse_name" },
            Path.Combine(p_WorkDir, "conv2"), Console.Out.WriteLine);

        var s_NewSlot = s_SlotTextures.FirstOrDefault(p_T => p_T.SlotRegister == "2");
        var s_SlotLane = s_SlotError == null && s_NewSlot != null &&
                         s_NewSlot.SlotTarget == s_Graph.TargetShader &&
                         s_NewSlot.Name.StartsWith("custom/", StringComparison.Ordinal) &&
                         s_NewSlot.GroupDonor == "test/diffuse_name" &&
                         s_SlotTextures.Any(p_T => p_T.SlotTarget == null && p_T.Name == "test/diffuse_name");

        if (!s_SlotLane)
        {
            Console.Error.WriteLine($"CUSTOMTEXTEST: FAIL - the new-slot lane misfired (error={s_SlotError}, " +
                                    $"slot={s_NewSlot?.Name}/{s_NewSlot?.SlotTarget}/{s_NewSlot?.GroupDonor}).");
            return 1;
        }

        // 3b) A vanished file must still ABORT — never half-ship.
        s_Graph.Nodes[0].Params["CustomTexture"] = Path.Combine(p_WorkDir, "gone.png");
        var (_, s_NoFile) = PrepareCustomTextures(s_Graph, s_Slots,
            Path.Combine(p_WorkDir, "conv3"), Console.Out.WriteLine);

        if (s_NoFile == null)
        {
            Console.Error.WriteLine("CUSTOMTEXTEST: FAIL - a vanished file was accepted instead of refused.");
            return 1;
        }

        Console.Out.WriteLine("CUSTOMTEXTEST: PASS - preview override, all three DDS lanes, the new-slot lane " +
                              "and the missing-file refusal.");
        return 0;
    }

    private static void WriteTestPng(string p_Path, int p_Width, int p_Height, bool p_WithAlpha)
    {
        var s_Pixels = new byte[p_Width * p_Height * 4];
        for (var s_Y = 0; s_Y < p_Height; s_Y++)
            for (var s_X = 0; s_X < p_Width; s_X++)
            {
                var i = (s_Y * p_Width + s_X) * 4;
                s_Pixels[i + 0] = (byte) (s_X * 255 / p_Width);
                s_Pixels[i + 1] = (byte) (s_Y * 255 / p_Height);
                s_Pixels[i + 2] = 200;
                s_Pixels[i + 3] = p_WithAlpha ? (byte) (s_X * 255 / p_Width) : (byte) 255;
            }

        var s_Bitmap = BitmapSource.Create(p_Width, p_Height, 96, 96, PixelFormats.Bgra32, null,
            s_Pixels, p_Width * 4);
        var s_Encoder = new PngBitmapEncoder();
        s_Encoder.Frames.Add(BitmapFrame.Create(s_Bitmap));
        using var s_Stream = File.Create(p_Path);
        s_Encoder.Save(s_Stream);
    }

    internal static int TabSelfTest(string p_WorkDir)
    {
        Directory.CreateDirectory(p_WorkDir);
        var s_Window = new MainWindow();

        if (s_Window.m_Tabs.Count != 1)
        {
            Console.Error.WriteLine($"TABTEST: FAIL - expected 1 startup tab, found {s_Window.m_Tabs.Count}.");
            return 1;
        }

        var s_SampleNodes = s_Window.Canvas.Graph.Nodes.Count;
        var s_SampleTarget = s_Window.Canvas.Graph.TargetShader;

        // A second document in its own tab, then an EDIT in it (the edit is what must not leak or vanish).
        var s_Second = new ShaderGraph { Name = "second", TargetShader = "shaders/Test/Second" };
        s_Second.Nodes.Add(new GraphNode { Kind = "StandardRoot", X = 0, Y = 0 });
        s_Window.OpenInNewTab(s_Second, null);

        s_Window.Canvas.PushUndo();
        s_Window.Canvas.Graph.Nodes.Add(new GraphNode { Kind = "Scalar", X = 10, Y = 10 });

        if (s_Window.m_Tabs.Count != 2 || s_Window.m_ActiveTab != 1 ||
            s_Window.TargetShaderBox.Text != "shaders/Test/Second" || !s_Window.Canvas.CanUndo)
        {
            Console.Error.WriteLine("TABTEST: FAIL - opening a second tab did not take the window over.");
            return 1;
        }

        // Switch away: the first document comes back exactly as it was, including an EMPTY undo history.
        s_Window.SaveActiveTab();
        s_Window.ActivateTab(0);
        if (s_Window.Canvas.Graph.Nodes.Count != s_SampleNodes || s_Window.Canvas.CanUndo ||
            s_Window.TargetShaderBox.Text != s_SampleTarget)
        {
            Console.Error.WriteLine("TABTEST: FAIL - switching back did not restore the first document.");
            return 1;
        }

        // Switch forward: the edit AND its undo history are still there.
        s_Window.SaveActiveTab();
        s_Window.ActivateTab(1);
        if (s_Window.Canvas.Graph.Nodes.Count != 2 || !s_Window.Canvas.CanUndo)
        {
            Console.Error.WriteLine("TABTEST: FAIL - the second tab lost its edit or its undo history.");
            return 1;
        }

        // Re-selecting an OPEN target focuses its tab — no new tab, no re-read.
        s_Window.SaveActiveTab();
        s_Window.ActivateTab(0);
        var s_Finished = new TaskCompletionSource<int>();
        SelectFinished = s_Finished;
        _ = s_Window.SelectTargetAsync("shaders/Test/Second");
        SelectFinished = null;

        if (!s_Finished.Task.IsCompleted || s_Window.m_Tabs.Count != 2 || s_Window.m_ActiveTab != 1)
        {
            Console.Error.WriteLine("TABTEST: FAIL - selecting an already-open target did not focus its tab.");
            return 1;
        }

        // ── Per-tab preview: each document keeps the shape it was last showing ───────────────────────
        // Tab 1 is active here. Give the two tabs DIFFERENT shapes and check each comes back with its own.
        s_Window.m_Preview.Shape = View.PreviewShape.Cylinder;
        s_Window.SaveActiveTab();
        s_Window.ActivateTab(0);
        s_Window.m_Preview.Shape = View.PreviewShape.Sphere;
        s_Window.SaveActiveTab();
        s_Window.ActivateTab(1);

        if (s_Window.m_Preview.Shape != View.PreviewShape.Cylinder || s_Window.ShapeCylinder.IsChecked != true)
        {
            Console.Error.WriteLine("TABTEST: FAIL - the tab did not come back with its own preview shape.");
            return 1;
        }

        s_Window.SaveActiveTab();
        s_Window.ActivateTab(0);
        if (s_Window.m_Preview.Shape != View.PreviewShape.Sphere || s_Window.ShapeSphere.IsChecked != true)
        {
            Console.Error.WriteLine("TABTEST: FAIL - the other tab's shape leaked across the switch.");
            return 1;
        }

        // A mesh whose dump is NOT cached must not leave the mesh icon lit over an object that never
        // loaded: the shape falls back, while the NAME is kept so the list still shows the object.
        s_Window.m_PreviewMesh = "props/test/never_dumped_mesh";
        s_Window.m_Preview.Shape = View.PreviewShape.Mesh;
        s_Window.SaveActiveTab();
        s_Window.ActivateTab(1);
        s_Window.SaveActiveTab();
        s_Window.ActivateTab(0);

        if (s_Window.m_Preview.Shape == View.PreviewShape.Mesh || s_Window.ShapeMesh.IsChecked == true ||
            s_Window.m_PreviewMesh != "props/test/never_dumped_mesh")
        {
            Console.Error.WriteLine("TABTEST: FAIL - an uncached mesh was restored as if it had loaded.");
            return 1;
        }

        // The photograph, before closing anything: two tabs, the active one wearing the accent border.
        var s_Strip = s_Window.TabStrip;
        s_Strip.Measure(new Size(1200, 40));
        s_Strip.Arrange(new Rect(0, 0, 1200, s_Strip.DesiredSize.Height));
        s_Strip.UpdateLayout();

        var s_Bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            1200, Math.Max(1, (int) Math.Ceiling(s_Strip.DesiredSize.Height)), 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        s_Bitmap.Render(s_Strip);

        var s_Encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        s_Encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(s_Bitmap));
        var s_ShotPath = Path.Combine(p_WorkDir, "tabstrip.png");
        using (var s_Stream = File.Create(s_ShotPath))
            s_Encoder.Save(s_Stream);

        // The probe wants the EXPECTED colour, not "something differs": the active tab's accent border
        // (150,210,150) must exist somewhere in the strip's pixels.
        var s_Pixels = new byte[s_Bitmap.PixelWidth * s_Bitmap.PixelHeight * 4];
        s_Bitmap.CopyPixels(s_Pixels, s_Bitmap.PixelWidth * 4, 0);
        var s_AccentSeen = false;
        for (var i = 0; i < s_Pixels.Length; i += 4)
            if (s_Pixels[i + 2] == 150 && s_Pixels[i + 1] == 210 && s_Pixels[i] == 150)
            {
                s_AccentSeen = true;
                break;
            }

        Console.Out.WriteLine($"tab strip rendered -> {s_ShotPath}; active-tab accent present: " +
                              $"{(s_AccentSeen ? "OK" : "FAIL")}");
        if (!s_AccentSeen)
        {
            Console.Error.WriteLine("TABTEST: FAIL - the strip drew, but no pixel wears the active-tab accent.");
            return 1;
        }

        // Closing the active tab (edits present, headless skips the confirm box) falls back to the sample.
        s_Window.CloseTab(1);
        if (s_Window.m_Tabs.Count != 1 || s_Window.Canvas.Graph.Nodes.Count != s_SampleNodes)
        {
            Console.Error.WriteLine("TABTEST: FAIL - closing the second tab did not fall back to the first.");
            return 1;
        }

        // ⛔ THE OBJECT A GRAPH IS AIMED AT HAS TO COME BACK WITH IT. Saved on the document but not restored,
        // reopening lands on whatever set the map lists first — another object's textures in the preview and
        // the aim silently asking to be redone every time, which is the manual step the stamp exists to
        // remove. The fixture names the aimed set LAST alphabetically on purpose: the old behaviour (first
        // listed) picks the other one, so this cannot pass by accident.
        s_Window.Canvas.Graph.BakeMesh = "props/test/aimed_mesh";
        s_Window.FillVariationBox(new SlotMap
        {
            V = 5,
            Variations = new Dictionary<string, VariationSlots>
            {
                ["aa|0"] = new() { Name = "aa_other", Mesh = "props/test/other_mesh" },
                ["zz|0"] = new() { Name = "zz_aimed", Mesh = "props/test/aimed_mesh" },
            },
        });

        // ⛔ THE MESH SECTION BELONGS TO MESH MODE, AND COMING BACK TO IT MUST NOT COST A RELOAD. Both are
        // one state: the panel shows while the mesh icon is the chosen one, and switching to a primitive and
        // back is a shape change — the geometry never left the preview. Asserted through the single place
        // that sets the toolbar, because it used to be set from three.
        s_Window.m_MeshMode = true;
        s_Window.SyncShapeIcons(View.PreviewShape.Mesh);

        if (s_Window.MeshPanel.Visibility != Visibility.Visible || s_Window.ShapeMesh.IsChecked != true)
        {
            Console.Error.WriteLine("TABTEST: FAIL - mesh mode does not show the mesh section.");
            return 1;
        }

        s_Window.m_MeshMode = false;
        s_Window.SyncShapeIcons(View.PreviewShape.Cube);

        if (s_Window.MeshPanel.Visibility != Visibility.Collapsed || s_Window.ShapeCube.IsChecked != true ||
            s_Window.ShapeMesh.IsChecked != false)
        {
            Console.Error.WriteLine("TABTEST: FAIL - leaving mesh mode leaves the mesh section on screen, or " +
                                    "the toolbar disagreeing with the shape being drawn.");
            return 1;
        }

        if (s_Window.m_Variation != "zz|0")
        {
            Console.Error.WriteLine("TABTEST: FAIL - a graph aimed at an object reopened on set " +
                                    $"'{s_Window.m_Variation}' instead of the one carrying that mesh; the " +
                                    "user would have to aim it again on every open.");
            return 1;
        }

        Console.Out.WriteLine("TABTEST: PASS - open/switch/focus/close all keep each document intact, and the " +
                              "strip draws its state.");
        return 0;
    }

    internal static int TranslateSelfTest(string p_Target, string p_WorkDir, string? p_GamePath,
        string? p_Level = null)
    {
        var s_Window = new MainWindow();
        s_Window.OutputFolderBox.Text = p_WorkDir;
        s_Window.TargetShaderBox.Text = p_Target;

        // Simulates "the browser has this level loaded", which is what routes a non-Levels target to the right
        // shaderdb. Without it the test runs the old typed-by-hand path (mp_017 fallback).
        if (!string.IsNullOrWhiteSpace(p_Level))
            s_Window.m_LoadedLevel = p_Level;

        if (!string.IsNullOrWhiteSpace(p_GamePath))
            s_Window.GamePathBox.Text = p_GamePath;

        var s_Button = Descendants<Button>((DependencyObject) s_Window.Content)
            .FirstOrDefault(p_B => p_B.Tag as string == "translate");

        if (s_Button == null)
        {
            Console.Error.WriteLine("TRANSLATETEST: no button tagged 'translate' in the window.");
            return 1;
        }

        var s_Finished = new TaskCompletionSource<int>();
        TranslateFinished = s_Finished;

        s_Button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

        // The handler is async, so the click returns at its first await. Pump the dispatcher until it completes,
        // which is also what keeps the continuation running - without a message loop it would never resume.
        var s_Frame = new System.Windows.Threading.DispatcherFrame();
        var s_Timeout = Task.Delay(TimeSpan.FromMinutes(10));
        Task.WhenAny(s_Finished.Task, s_Timeout).ContinueWith(_ => s_Frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(s_Frame);
        TranslateFinished = null;

        Console.Out.WriteLine(s_Window.LogBox.Text.TrimEnd());

        if (!s_Finished.Task.IsCompleted)
        {
            Console.Error.WriteLine("TRANSLATETEST: FAIL - the click never finished (10 min timeout).");
            return 1;
        }

        var s_Nodes = s_Finished.Task.Result;
        var s_Graph = s_Window.Canvas.Graph;
        Console.Out.WriteLine($"canvas now holds {s_Nodes} node(s), {s_Graph.Connections.Count} connection(s), " +
                              $"target '{s_Graph.TargetShader}'");

        // Written out so --shaderdiff can be pointed at the graph THIS CLICK produced, against the bytecode THIS
        // CLICK extracted. Reading the node count only proves something was built, not that it is the same shader.
        try
        {
            var s_Saved = Path.Combine(p_WorkDir, "translated.json");
            File.WriteAllText(s_Saved, s_Graph.ToJson());
            Console.Out.WriteLine($"graph written to {s_Saved}");
        }
        catch (Exception s_Exception)
        {
            Console.Error.WriteLine($"could not save the graph: {s_Exception.Message}");
        }

        // A canvas with a couple of nodes is what a FAILED translation leaves behind (the scaffold, or an empty
        // graph), so the bar is a real graph with wiring - not merely "the handler did not throw".
        var s_Ok = s_Nodes > 8 && s_Graph.Connections.Count > 8 &&
                   s_Graph.TargetShader.Equals(p_Target, StringComparison.OrdinalIgnoreCase);

        Console.Out.WriteLine(s_Ok
            ? "TRANSLATETEST: PASS - the button pulled the shader out of the game and put a graph on the canvas."
            : "TRANSLATETEST: FAIL - the click left no usable graph on the canvas.");

        return s_Ok ? 0 : 1;
    }

    /// <summary>
    /// The shaderdb a target is read from. A `Levels/&lt;name&gt;/...` target names its own level; every other
    /// shader (Objects/..., XP2/..., Vehicles/...) is compiled into every level that uses it, so the level whose
    /// index is loaded in the browser decides - that is where the user found the name. Only with no browser
    /// context at all does the old mp_017 fallback remain, for targets typed by hand.
    /// </summary>
    internal string ResolveShaderDb(string p_Target)
    {
        var s_ByName = ShaderDbNamedBy(p_Target);
        if (s_ByName != null)
            return s_ByName;

        if (m_LoadedLevel != null)
            return LevelScanner.ShaderDbFor(m_LoadedLevel);

        return "levels/mp_017/mp_017/shaderdb";
    }

    private static string? ShaderDbNamedBy(string p_Target)
    {
        var s_Parts = p_Target.Split('/');
        if (s_Parts.Length > 2 && s_Parts[0].Equals("Levels", StringComparison.OrdinalIgnoreCase))
            return $"levels/{s_Parts[1].ToLowerInvariant()}/{s_Parts[1].ToLowerInvariant()}/shaderdb";

        return null;
    }

    /// <summary>The CLI has no browser, so name-or-mp_017 is all it can do; the window itself uses ResolveShaderDb.</summary>
    internal static string ShaderDbForTarget(string p_Target) =>
        ShaderDbNamedBy(p_Target) ?? "levels/mp_017/mp_017/shaderdb";

    private string SafeName() => Sanitize(Canvas.Graph.Name) is { Length: > 0 } s_Name ? s_Name : "graph";

    private static string Sanitize(string p_Text)
    {
        var s_Builder = new StringBuilder();
        foreach (var s_Char in p_Text)
            s_Builder.Append(char.IsLetterOrDigit(s_Char) ? s_Char : '_');

        return s_Builder.ToString().Trim('_');
    }

    private sealed class ProcessResult
    {
        public int ExitCode { get; init; }
        public string Output { get; init; } = "";
    }

    private static ProcessResult Run(string p_Executable, IEnumerable<string> p_Arguments)
    {
        var s_Info = new ProcessStartInfo(p_Executable)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(p_Executable) ?? Environment.CurrentDirectory,
        };

        foreach (var s_Argument in p_Arguments)
            s_Info.ArgumentList.Add(s_Argument);

        using var s_Process = Process.Start(s_Info);
        if (s_Process == null)
            return new ProcessResult { ExitCode = -1, Output = "could not start process" };

        var s_StdOut = s_Process.StandardOutput.ReadToEnd();
        var s_StdErr = s_Process.StandardError.ReadToEnd();
        s_Process.WaitForExit();

        return new ProcessResult { ExitCode = s_Process.ExitCode, Output = s_StdOut + s_StdErr };
    }

    /// <summary>Walks up from the editor's own folder looking for the release RimeREPL next to the repo root.</summary>
    /// <summary>
    /// One custom texture ready for the bundle. Two delivery lanes: with <see cref="SlotTarget"/> null the
    /// texture OVERRIDES the vanilla game texture of the same Name; with it set, the texture is BRAND-NEW —
    /// the bake adds a texture slot (register &lt;- Name) to the target shader's constants so the engine binds
    /// it natively, and <see cref="GroupDonor"/> lends the pool TextureGroup a fresh name cannot look up.
    /// </summary>
    internal sealed record CustomBakeTexture(string Name, string DdsPath, bool Srgb, bool Normal,
        string? SlotTarget = null, string SlotRegister = "", string? GroupDonor = null);

    /// <summary>
    /// Prepares SAVED graphs for a multi-shader bake: each one is compiled against the contract detected from
    /// ITS OWN target's bytecode — emitting a saved CharacterRoot graph under the rigid default would produce
    /// a silently wrong shader, so the targets' reference permutations are extracted first (one mount for all
    /// of them) and each graph's HLSL is emitted with what its own bytes say.
    /// </summary>
    internal static (List<Emit.ShaderBaker.BakeShader> Shaders,
        List<CustomBakeTexture> Textures, string? Error) PrepareAdditionalGraphs(
            IReadOnlyList<string> p_GraphPaths, string p_Repl, string p_Fxc, string p_GamePath,
            string p_FirstLevel, string p_TexCacheDir, string p_WorkDir, Action<string> p_Log)
    {
        var s_Shaders = new List<Emit.ShaderBaker.BakeShader>();
        var s_Textures = new List<CustomBakeTexture>();
        if (p_GraphPaths.Count == 0)
            return (s_Shaders, s_Textures, null);

        Directory.CreateDirectory(p_WorkDir);

        var s_Graphs = new List<(string Path, ShaderGraph Graph)>();
        foreach (var s_GraphPath in p_GraphPaths)
        {
            if (!File.Exists(s_GraphPath))
                return (s_Shaders, s_Textures, $"saved graph not found: {s_GraphPath}");

            ShaderGraph s_Graph;
            try { s_Graph = ShaderGraph.FromJson(File.ReadAllText(s_GraphPath)); }
            catch (Exception s_Exception)
            {
                return (s_Shaders, s_Textures, $"'{Path.GetFileName(s_GraphPath)}' is not a graph: {s_Exception.Message}");
            }

            if (string.IsNullOrWhiteSpace(s_Graph.TargetShader))
                return (s_Shaders, s_Textures,
                    $"'{Path.GetFileName(s_GraphPath)}' has no target shader — open it and set one first.");

            s_Graphs.Add((s_GraphPath, s_Graph));

            // A MASTER file carries its variations inside; each embedded one is a bake lane of its own.
            if (s_Graph.Variations is { Count: > 0 } s_EmbeddedLanes)
            {
                foreach (var s_Lane in s_EmbeddedLanes)
                {
                    if (s_Lane.BakeVariation is not { Length: > 0 })
                        continue;

                    if (string.IsNullOrWhiteSpace(s_Lane.TargetShader))
                        s_Lane.TargetShader = s_Graph.TargetShader;

                    s_Lane.Variations = null;
                    s_Graphs.Add(($"{s_GraphPath}#{s_Lane.BakeVariation.Split('/')[^1]}", s_Lane));
                }

                s_Graph.Variations = null;
                p_Log($"  '{Path.GetFileName(s_GraphPath)}': {s_EmbeddedLanes.Count} embedded variation(s) join the bake.");
            }
        }

        // One mount extracts every target's reference permutation.
        var s_Db = Emit.LevelScanner.ShaderDbFor(p_FirstLevel);
        var s_Script = new StringBuilder();
        s_Script.AppendLine($"mount_game \"{p_GamePath}\" Frostbite2_0 true");
        s_Script.AppendLine("select_game 1");
        for (var i = 0; i < s_Graphs.Count; i++)
        {
            // ⛔⛔ THE FOLDER IS EMPTIED FIRST, AND SKIPPING THAT ABORTED A BAKE. The extractor writes numbered
            // subfolders plus an index.txt when the name matches several shaders, and the loose files straight
            // into the folder when it matches ONE — so a single-match shader extracted into a folder left over
            // from a MULTI-match one lands next to that one's index.txt, and the reader below, finding an
            // index, looks for its own name among ANOTHER shader's rows and gives up. Slot number i means a
            // different shader from one bake to the next; only clearing makes it mean this one.
            var s_Out = Path.Combine(p_WorkDir, $"extract_{i}");
            if (Directory.Exists(s_Out))
                try { Directory.Delete(s_Out, true); } catch { /* a locked leftover fails loudly below */ }

            s_Script.AppendLine(
                $"extract_shader_dxbc {s_Db} \"{s_Graphs[i].Graph.TargetShader}\" \"{s_Out}\"");
        }

        s_Script.AppendLine("exit");

        var s_ScriptPath = Path.Combine(p_WorkDir, "contracts.rime");
        File.WriteAllText(s_ScriptPath, s_Script.ToString());
        p_Log($"Reading {s_Graphs.Count} saved shader(s) out of {p_FirstLevel} for their contracts (one mount)…");
        Run(p_Repl, new[] { s_ScriptPath });

        for (var i = 0; i < s_Graphs.Count; i++)
        {
            var (s_GraphPath, s_Graph) = s_Graphs[i];
            var s_Target = s_Graph.TargetShader;
            var s_Extract = Path.Combine(p_WorkDir, $"extract_{i}");

            // The extractor writes numbered subfolders + index.txt when the name matched several shaders.
            var s_Folder = s_Extract;
            var s_IndexPath = Path.Combine(s_Extract, "index.txt");
            if (File.Exists(s_IndexPath))
            {
                var s_Row = File.ReadAllLines(s_IndexPath)
                    .Select(p_L => p_L.Split('\t'))
                    .Where(p_P => p_P.Length >= 2)
                    .FirstOrDefault(p_P => p_P[1].Equals(s_Target, StringComparison.OrdinalIgnoreCase));

                if (s_Row == null)
                    return (s_Shaders, s_Textures,
                        $"'{s_Target}' matched several shaders in {p_FirstLevel} but none exactly.");

                s_Folder = Path.Combine(s_Extract, s_Row[0]);
            }

            var s_Candidates = Directory.Exists(s_Folder)
                ? new DirectoryInfo(s_Folder).GetFiles("*_ps.dxbc").OrderByDescending(p_F => p_F.Length).ToList()
                : new List<FileInfo>();

            if (s_Candidates.Count == 0)
                return (s_Shaders, s_Textures,
                    $"'{s_Target}' produced no pixel shader from {p_FirstLevel} — is it in that map?");

            var s_Log = new List<string>();
            var s_Chosen = ShaderIndex.ChooseGBufferPermutation(s_Candidates, s_Log);

            ShaderContract? s_Contract = null;
            try
            {
                var s_Bytes = File.ReadAllBytes(s_Chosen.FullName);
                s_Contract = ShaderContract.Detect(s_Bytes);
                var s_Disasm = new SharpDX.D3DCompiler.ShaderBytecode(s_Bytes).Disassemble();
                s_Contract.ClassifyInterpolators(Translate.DxbcAsm.Parse(s_Disasm.Split('\n')));
            }
            catch (Exception s_Exception)
            {
                return (s_Shaders, s_Textures, $"could not read '{s_Target}' contract: {s_Exception.Message}");
            }

            var s_EmitResult = new HlslEmitter { Contract = s_Contract }.Emit(s_Graph);
            if (!s_EmitResult.Ok)
                return (s_Shaders, s_Textures,
                    $"'{Path.GetFileName(s_GraphPath)}' does not emit: {string.Join("; ", s_EmitResult.Errors)}");

            var s_Hlsl = Path.Combine(p_WorkDir, $"extra_{i}.hlsl");
            var s_Dxbc = Path.ChangeExtension(s_Hlsl, ".dxbc");
            File.WriteAllText(s_Hlsl, s_EmitResult.Hlsl);
            if (File.Exists(s_Dxbc))
                File.Delete(s_Dxbc);

            var s_Compile = Run(p_Fxc,
                new[] { "/nologo", "/T", "ps_5_0", "/E", "main", "/O3", "/Fo", s_Dxbc, s_Hlsl });

            if (!File.Exists(s_Dxbc))
                return (s_Shaders, s_Textures,
                    $"fxc rejected '{Path.GetFileName(s_GraphPath)}': " +
                    string.Join(" ", s_Compile.Output.Split('\n').TakeLast(3)).Trim());

            var s_FullMap = ReadSlotMapFull(Path.Combine(p_TexCacheDir, SlotMapFileName(s_Target)));
            var s_Slots = s_FullMap?.Slots;
            var (s_GraphTextures, s_TexError) = PrepareCustomTextures(s_Graph, s_Slots,
                Path.Combine(p_WorkDir, $"textures_{i}"), p_Log);

            if (s_TexError != null)
                return (s_Shaders, s_Textures, $"'{Path.GetFileName(s_GraphPath)}': {s_TexError}");

            // A saved graph carrying a variation delivers as one, exactly like the active graph would.
            VariationSpec? s_GraphVariation = null;
            // ⛔ Whether the graph reproduces the vanilla logic decides whether ANY bytecode is
            // patched, and that is true of a replacement as much as of a variation — so it is
            // measured out here, where both can see it.
            var s_SameLogic = s_Graph.TranslatedHlslHash != null &&
                              HlslFingerprint(s_Graph, s_Contract) == s_Graph.TranslatedHlslHash;

            string? s_ManifestForBake = null;

            if (s_Graph.BakeVariation is { Length: > 0 } s_VariationName)
            {
                // On a shared shader "any mesh that uses it" is not good enough: the first one listed can
                // be a destruction mesh, which is not resident when the level bundle loads and crashes the
                // end of the loading screen registering the variation against a null mesh. The graph's own
                // stamp (the preview's selected set) wins; without one, prefer a mesh that does not look
                // like destruction debris.
                static string? PickMesh(IEnumerable<string?>? p_Meshes) => p_Meshes?
                    .Where(p_M => p_M is { Length: > 0 })
                    .OrderBy(p_M => p_M!.Contains("destruction", StringComparison.OrdinalIgnoreCase) ||
                                    p_M.Contains("_wreck", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .FirstOrDefault();

                var s_Mesh = s_Graph.BakeMesh is { Length: > 0 } s_Stamped
                    ? s_Stamped
                    : PickMesh(s_FullMap?.Variations?.Values.Select(p_V => p_V.Mesh));

                // A pre-variation cached map lacks the mesh name; refresh it here rather than bounce the
                // user to a refresh affordance that no longer exists.
                if (s_Mesh == null)
                {
                    p_Log($"  '{s_Target}': cached texture map predates variation support — refreshing...");
                    var s_MapPath = Path.Combine(p_TexCacheDir, SlotMapFileName(s_Target));
                    LoadTexturesCore(p_Repl, p_GamePath, ShaderDbForTarget(s_Target), s_Target,
                        p_TexCacheDir, s_MapPath, s_Contract);
                    s_Mesh = PickMesh(ReadSlotMapFull(s_MapPath)?.Variations?.Values.Select(p_V => p_V.Mesh));
                }

                if (s_Mesh == null)
                    return (s_Shaders, s_Textures,
                        $"'{Path.GetFileName(s_GraphPath)}': no database entry names a mesh for this " +
                        "target even after a refresh — a variation cannot be delivered without one.");

                // ⛔ TAKING OVER WHAT IS ALREADY IN THE MAP AIMS AT ONE MESH, SO THE MESH CANNOT BE A GUESS.
                // A preset shader is worn by dozens of different meshes in a single map, and the fallback
                // above picks whichever one the cached map happens to list first. Aimed at the wrong one, the
                // bake installs, reports success and changes nothing — the failure mode that only shows up
                // after a boot.
                //
                // ⚠ BUT ONLY WHEN THERE IS SOMETHING TO GET WRONG. A shader worn by ONE mesh has no ambiguity
                // to resolve and no picker to choose from (the editor hides it), so demanding a stamp there
                // would refuse the very case that has always worked — a guard that fires where no mistake is
                // possible is just a wall.
                var s_MeshChoices = s_FullMap?.Variations?.Values
                    .Select(p_V => p_V.Mesh)
                    .Where(p_M => p_M is { Length: > 0 })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() ?? 0;

                if (s_Graph.ApplyVariationToLevel && s_Graph.BakeMesh is not { Length: > 0 } &&
                    s_MeshChoices > 1)
                    return (s_Shaders, s_Textures,
                        $"'{Path.GetFileName(s_GraphPath)}': this bake is meant to change the copies already " +
                        $"in the level, but no object is stamped on this graph — '{s_Target}' is worn by more " +
                        "than one mesh and the take-over would be aimed at a guess. Open it, pick the " +
                        "object's set in the preview (that aims the graph) and SAVE it, then bake. A graph " +
                        "saved before this editor recorded the choice carries no object even if it was baked " +
                        "correctly at the time.");

                p_Log($"  variation rides mesh '{s_Mesh}'" +
                      (s_Graph.BakeMesh is { Length: > 0 } ? " (the preview's selected set)." : "."));


                s_GraphVariation = Emit.VariationPlan.Build(s_Target, s_VariationName, s_Mesh,
                    Path.Combine(p_WorkDir, $"variation_{i}"), s_SameLogic ? null : s_Dxbc,
                    s_Graph.ApplyVariationToLevel);

                p_Log(s_Graph.ApplyVariationToLevel
                    ? "  the level's own copies of this object will render with this shader."
                    : "  ships as an extra variation; the level's own copies are left alone.");

                var s_RetargetError = RetargetTexturesForVariation(s_GraphTextures, s_GraphVariation,
                    s_Slots != null ? new Dictionary<string, string>(s_Slots) : null);

                if (s_RetargetError != null)
                    return (s_Shaders, s_Textures, $"'{Path.GetFileName(s_GraphPath)}': {s_RetargetError}");

                p_Log($"  variation: '{s_VariationName}' (hash {s_GraphVariation.Hash}) -> clone " +
                      $"'{s_GraphVariation.CloneShader}', " +
                      $"{(s_SameLogic ? "vanilla bytecode" : "authored bytecode")}");

            }

            // Per-VARIANT compilation: the mode ships several pixel flavours (base, probe-lit via
            // interpolators, probe-lit via cbuffer...) and one compilation stuffed into all of them is
            // how probe-lit objects lost their ambient and instanced batches shaded garbage. Each
            // distinct vanilla flavour gets the graph compiled against ITS OWN detected contract; a
            // flavour the emitter cannot honour keeps its vanilla bytes, which renders correct (just
            // not customised) instead of broken.
            if (!s_SameLogic)
            {
                var s_Manifest = new List<string>();
                var s_ChosenBytes = File.ReadAllBytes(s_Chosen.FullName);
                var s_Distinct = new List<byte[]>();
                var s_VariantIndex = 0;

                // Whether this bake targets the GBuffer or the forward (Default) pass — the variants
                // worth compiling are the ones from the SAME pass as the chosen reference. A forward
                // entry has only single-target flavours; gating on ">= 3 targets" here would silently
                // skip every one of them. ⛔ And "forward" is decided by the measured discriminator
                // (the outdoor-light constants ride ONLY the forward solutions), not by target count:
                // the vegetation-alpha preset writes a REDUCED two-target GBuffer that a count-based
                // rule misfiled as forward, which sent its variants down the keeps-vanilla branch.
                var s_ChosenContract = ShaderContract.Detect(s_ChosenBytes);
                var s_ChosenIsForward = s_ChosenContract.ConstantFields.Any(p_F =>
                    p_F.Name.Equals("outdoorLightHemisphereDir", StringComparison.OrdinalIgnoreCase));

                // Same pass as the chosen reference: the full-GBuffer and forward cases keep the
                // ">= 3 on both sides" split they were verified with; a REDUCED GBuffer reference
                // (fewer than three targets, not forward) matches by exact target count, which is
                // what keeps its alpha-tested ZOnly flavours (one target) out of the patch set.
                bool SamePass(Emit.ShaderContract p_Variant) =>
                    s_ChosenIsForward || s_ChosenContract.RenderTargets >= 3
                        ? (p_Variant.RenderTargets >= 3) == (s_ChosenContract.RenderTargets >= 3)
                        : p_Variant.RenderTargets == s_ChosenContract.RenderTargets;

                foreach (var s_PermFile in new DirectoryInfo(s_Folder).GetFiles("*_ps.dxbc"))
                {
                    var s_PermBytes = File.ReadAllBytes(s_PermFile.FullName);
                    if (s_Distinct.Any(p_D => p_D.AsSpan().SequenceEqual(s_PermBytes)))
                        continue;

                    s_Distinct.Add(s_PermBytes);

                    if (s_PermBytes.AsSpan().SequenceEqual(s_ChosenBytes))
                    {
                        s_Manifest.Add($"{s_PermFile.FullName}|{s_Dxbc}");
                        continue;
                    }

                    try
                    {
                        var s_VarContract = ShaderContract.Detect(s_PermBytes);
                        if (!SamePass(s_VarContract))
                            continue;

                        // A flavour whose interpolator layout no family explains cannot be patched
                        // safely — the emitted code would read positions blind. It keeps vanilla
                        // bytes, the same fallback as a flavour the emitter declines.
                        if (s_VarContract.Family == Emit.ShaderFamily.Unknown)
                        {
                            p_Log($"    variant {s_PermFile.Name}: unrecognized interpolator layout " +
                                  "— keeps vanilla bytes.");
                            continue;
                        }

                        // Forward flavours carry their light terms IN THE BODY (probe SH rows, the
                        // lightmap set) — a GBuffer root re-emits those blocks per flavour, but a
                        // forward graph's body IS the authored code and has no such terms. The
                        // probe-CBUFFER twin's delta is measured and mechanical, so it is SYNTHESIZED
                        // onto a clone of the graph (spawned single objects are lit exactly this way);
                        // the interpolator-probe and lightmap flavours still keep vanilla bytes —
                        // correct, just not customised — rather than losing their light.
                        var s_VariantGraph = s_Graph;
                        var s_HasLightmap = s_VarContract.ConstantFields.Any(
                            p_F => p_F.Name.StartsWith("lightMap", StringComparison.OrdinalIgnoreCase));

                        if (s_ChosenIsForward &&
                            (s_VarContract.Probes != Emit.ProbeTransport.None || s_HasLightmap))
                        {
                            if (s_VarContract.Probes == Emit.ProbeTransport.Cbuffer && !s_HasLightmap)
                            {
                                var s_Probeized = Graph.ShaderGraph.FromJson(s_Graph.ToJson());
                                if (Emit.ForwardProbe.TryProbeize(s_Probeized, out var s_Why))
                                {
                                    s_VariantGraph = s_Probeized;
                                    p_Log($"    variant {s_PermFile.Name}: probe-lit twin synthesized " +
                                          "(ambient x ShO + SH, reflection x ShO).");
                                }
                                else
                                {
                                    p_Log($"    variant {s_PermFile.Name}: probe twin not derivable " +
                                          $"({s_Why}) — keeps vanilla bytes.");
                                    continue;
                                }
                            }
                            else
                            {
                                p_Log($"    variant {s_PermFile.Name}: adds its own light terms " +
                                      "(probe-interpolator/lightmap) — keeps vanilla bytes.");
                                continue;
                            }
                        }

                        var s_VarDisasm = new SharpDX.D3DCompiler.ShaderBytecode(s_PermBytes).Disassemble();
                        var s_VarInstructions = Translate.DxbcAsm.Parse(s_VarDisasm.Split('\n'));
                        s_VarContract.ClassifyInterpolators(s_VarInstructions);

                        // The alpha-coverage tap count is a fact of EACH flavour's own bytecode: the
                        // plain vegetation-alpha solutions dither two taps, the instanced ones four.
                        // A graph translated from one flavour carries that flavour's count, so the
                        // node is re-stamped with what THIS variant actually does — measured from its
                        // instructions, not generalised from the family.
                        var s_VanillaTaps = CountAlphaCoverageTaps(s_VarInstructions);
                        if (s_VanillaTaps is 2 or 4 &&
                            s_VariantGraph.Nodes.Any(p_N => p_N.Kind == "AlphaCoverage" &&
                                p_N.GetParam("Taps") != s_VanillaTaps.ToString()))
                        {
                            var s_Retapped = Graph.ShaderGraph.FromJson(s_VariantGraph.ToJson());
                            foreach (var s_CoverageNode in s_Retapped.Nodes
                                         .Where(p_N => p_N.Kind == "AlphaCoverage"))
                                s_CoverageNode.Params["Taps"] = s_VanillaTaps.ToString();

                            s_VariantGraph = s_Retapped;
                            p_Log($"    variant {s_PermFile.Name}: alpha-coverage re-stamped to " +
                                  $"{s_VanillaTaps} taps (this flavour's own pattern).");
                        }

                        // ⛔ THE GRAPH'S TEXTURE REGISTERS BELONG TO THE FLAVOUR IT WAS TRANSLATED FROM.
                        // Emitting it verbatim against another one samples whatever that flavour keeps at
                        // those registers — on a lightmapped variant t1..t4 are the engine's lightmap, so
                        // the albedo would be read out of the irradiance atlas. Matched by material slot.
                        int NewRegisterFor(Graph.GraphNode p_TexNode) =>
                            int.TryParse(p_TexNode.GetParam("Register"), out var s_Old)
                                ? s_VarContract.RemapRegisterFrom(s_ChosenContract, s_Old) is var s_New &&
                                  s_New >= 0 && s_New != s_Old
                                    ? s_New
                                    : -1
                                : -1;

                        var s_Remapped = new List<string>();
                        if (s_VariantGraph.Nodes.Any(p_N => NewRegisterFor(p_N) >= 0))
                        {
                            // Never in place: the graph may still be the user's own document.
                            if (ReferenceEquals(s_VariantGraph, s_Graph))
                                s_VariantGraph = Graph.ShaderGraph.FromJson(s_Graph.ToJson());

                            foreach (var s_TexNode in s_VariantGraph.Nodes)
                            {
                                var s_NewReg = NewRegisterFor(s_TexNode);
                                if (s_NewReg < 0)
                                    continue;

                                s_Remapped.Add($"t{s_TexNode.GetParam("Register")}->t{s_NewReg}");
                                s_TexNode.Params["Register"] = s_NewReg.ToString(CultureInfo.InvariantCulture);
                            }
                        }

                        if (s_Remapped.Count > 0)
                            p_Log($"    variant {s_PermFile.Name}: texture registers remapped for this " +
                                  $"flavour ({string.Join(", ", s_Remapped)}).");

                        var s_VarEmit = new HlslEmitter { Contract = s_VarContract }.Emit(s_VariantGraph);
                        if (!s_VarEmit.Ok)
                        {
                            p_Log($"    variant {s_PermFile.Name}: not emittable " +
                                  $"({string.Join("; ", s_VarEmit.Errors.Take(1))}) — keeps vanilla bytes.");
                            continue;
                        }

                        var s_VarHlsl = Path.Combine(p_WorkDir, $"variant_{i}_{s_VariantIndex}.hlsl");
                        var s_VarDxbc = Path.ChangeExtension(s_VarHlsl, ".dxbc");
                        File.WriteAllText(s_VarHlsl, s_VarEmit.Hlsl);
                        if (File.Exists(s_VarDxbc))
                            File.Delete(s_VarDxbc);

                        Run(p_Fxc, new[]
                        {
                            "/nologo", "/T", "ps_5_0", "/E", "main", "/O3", "/Fo", s_VarDxbc, s_VarHlsl,
                        });

                        if (File.Exists(s_VarDxbc))
                        {
                            s_Manifest.Add($"{s_PermFile.FullName}|{s_VarDxbc}");
                            p_Log($"    variant {s_PermFile.Name}: {s_VarContract.Pass} -> compiled " +
                                  $"({new FileInfo(s_VarDxbc).Length} B).");
                        }
                        else
                        {
                            p_Log($"    variant {s_PermFile.Name}: fxc rejected it — keeps vanilla bytes.");
                        }
                    }
                    catch (Exception s_VariantException)
                    {
                        p_Log($"    variant {s_PermFile.Name}: {s_VariantException.Message} — keeps vanilla bytes.");
                    }

                    s_VariantIndex++;
                }

                if (s_Manifest.Count > 1)
                {
                    var s_ManifestPath = Path.Combine(p_WorkDir, $"variation_{i}", "patch.manifest");
                    Directory.CreateDirectory(Path.GetDirectoryName(s_ManifestPath)!);
                    File.WriteAllLines(s_ManifestPath, s_Manifest);
                    s_ManifestForBake = s_ManifestPath;
                    if (s_GraphVariation != null)
                        s_GraphVariation.ManifestPath = s_ManifestPath;
                    p_Log($"    {s_Manifest.Count} variant(s) patched via manifest.");
                }
            }

            s_Shaders.Add(new Emit.ShaderBaker.BakeShader(s_Target, s_Dxbc, s_GraphVariation,
                s_ManifestForBake));
            s_Textures.AddRange(s_GraphTextures);
            p_Log($"  saved shader ready: '{s_Target}' ({s_EmitResult.Hlsl.Length} chars HLSL, " +
                  $"{new FileInfo(s_Dxbc).Length} B DXBC, {s_GraphTextures.Count} custom texture(s))");
        }

        return (s_Shaders, s_Textures, null);
    }

    /// <summary>
    /// Converts every texture node's chosen file into an engine-ready DDS and names the game texture each one
    /// overrides. All-or-nothing on purpose: a bake that silently ships HALF the chosen textures looks like a
    /// working mod with wrong art, which is the worst failure to diagnose in-game.
    ///
    /// Format lanes are the ones proven in-game: classic-header DXT1/DXT5 only (a DX10-header DDS builds fine
    /// and then hangs the client reading the chunk), normal maps as DXT1 with the normal-map flag, sRGB as a
    /// resource FLAG (never in the DDS header, which would force DX10).
    /// </summary>
    internal static (List<CustomBakeTexture> Textures, string? Error) PrepareCustomTextures(
        ShaderGraph p_Graph, IReadOnlyDictionary<string, string>? p_Slots, string p_WorkDir,
        Action<string> p_Log)
    {
        // GetParam for the register: a node whose register was never TOUCHED carries it as a palette default,
        // not as a dictionary entry — the raw read shipped "" and the bake refused a perfectly good node.
        var s_Chosen = p_Graph.Nodes
            .Where(p_N => TextureNodeKinds.Contains(p_N.Kind) &&
                          p_N.Params.TryGetValue("CustomTexture", out var s_File) && s_File.Length > 0)
            .Select(p_N => (Node: p_N,
                File: p_N.Params["CustomTexture"],
                Register: p_N.GetParam("Register")))
            .ToList();

        var s_Result = new List<CustomBakeTexture>();
        if (s_Chosen.Count == 0)
            return (s_Result, null);

        var s_Texconv = FindTexconv();
        if (s_Texconv == null)
            return (s_Result, "texconv.exe not found (expected next to RimeREPL.exe or the editor). It is " +
                              "needed to convert custom textures to the engine's format.");

        if (p_Slots == null || p_Slots.Count == 0)
            return (s_Result, "no texture slot map for this shader — press 'Reload from game' first so the " +
                              "editor knows WHICH game texture each register binds (that name is what the " +
                              "custom texture overrides).");

        Directory.CreateDirectory(p_WorkDir);

        foreach (var (s_Node, s_File, s_Register) in s_Chosen)
        {
            if (!File.Exists(s_File))
                return (s_Result, $"custom texture '{s_File}' (register t{s_Register}) no longer exists.");

            // Two lanes. A register the game material BINDS -> override that texture by name. A register it
            // does NOT bind (an independent node the user added) -> a BRAND-NEW texture under a name of our
            // own, plus a shader-constant slot added at bake so the engine binds it there natively — the
            // mechanism the water-palette work proved in-game. The new name needs a GROUP DONOR: a fresh
            // name has no original to copy the pool group from, and 'Default' draws black.
            string s_GameName;
            string? s_SlotTarget = null;
            string? s_GroupDonor = null;

            if (p_Slots.TryGetValue(s_Register, out var s_BoundName))
            {
                s_GameName = s_BoundName;
            }
            else
            {
                s_GameName = $"custom/{Sanitize(p_Graph.TargetShader).ToLowerInvariant()}/t{s_Register}";
                s_SlotTarget = p_Graph.TargetShader;
                s_GroupDonor = p_Slots.Values.FirstOrDefault();

                if (s_GroupDonor == null)
                    return (s_Result, $"register t{s_Register} is a new slot, but the material binds no " +
                                      "texture at all to donate a pool group from.");
            }

            var s_Normal = s_Node.Kind == "NormalMap";
            var s_Srgb = !s_Normal;
            var s_Format = s_Normal ? "BC1_UNORM" : ImageHasAlpha(s_File) ? "BC3_UNORM" : "BC1_UNORM";

            var s_OutName = Sanitize(s_GameName) + ".dds";
            var s_OutPath = Path.Combine(p_WorkDir, s_OutName);
            if (File.Exists(s_OutPath))
                File.Delete(s_OutPath);

            // texconv names its output after the input; convert into a scratch folder then claim the file.
            var s_ConvDir = Path.Combine(p_WorkDir, "conv_" + Sanitize(s_Register));
            Directory.CreateDirectory(s_ConvDir);

            var s_Process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(s_Texconv)
            {
                Arguments = $"-nologo -y -m 0 -pow2 -f {s_Format} -o \"{s_ConvDir}\" \"{s_File}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            var s_ToolOutput = s_Process == null
                ? ""
                : s_Process.StandardOutput.ReadToEnd() + s_Process.StandardError.ReadToEnd();
            s_Process?.WaitForExit();

            var s_Produced = Path.Combine(s_ConvDir,
                Path.GetFileNameWithoutExtension(s_File) + ".dds");

            if (!File.Exists(s_Produced))
                return (s_Result, $"texconv produced nothing for '{Path.GetFileName(s_File)}': " +
                                  $"{s_ToolOutput.Trim()}");

            File.Move(s_Produced, s_OutPath, true);

            // The in-game-proven lane is a CLASSIC DDS header. A DX10-header one builds a bundle that hangs
            // the client reading the chunk — caught here, where it costs seconds instead of a game boot.
            var s_Header = new byte[96];
            using (var s_Stream = File.OpenRead(s_OutPath))
                _ = s_Stream.Read(s_Header, 0, s_Header.Length);

            var s_FourCc = System.Text.Encoding.ASCII.GetString(s_Header, 84, 4);
            if (s_FourCc == "DX10")
                return (s_Result, $"the converted '{Path.GetFileName(s_File)}' came out with a DX10 header, " +
                                  "which the game cannot read from a mod bundle. Re-save the source as PNG " +
                                  "and try again.");

            s_Result.Add(new CustomBakeTexture(s_GameName, s_OutPath, s_Srgb, s_Normal,
                s_SlotTarget, s_SlotTarget != null ? s_Register : "", s_GroupDonor));
            p_Log($"  custom texture: t{s_Register} '{Path.GetFileName(s_File)}' -> {s_FourCc} " +
                  (s_SlotTarget != null
                      ? $"NEW slot '{s_GameName}' (group from '{s_GroupDonor}')"
                      : $"overriding '{s_GameName}'") +
                  (s_Normal ? " (normal map)" : ""));
        }

        return (s_Result, null);
    }

    /// <summary>True when any pixel is meaningfully transparent — that decides DXT5 over DXT1.</summary>
    private static bool ImageHasAlpha(string p_Path)
    {
        try
        {
            if (LoadCustomTextureImage(p_Path) is not BitmapSource s_Image)
                return false;

            var s_Converted = new FormatConvertedBitmap(s_Image, PixelFormats.Bgra32, null, 0);
            var s_Pixels = new byte[s_Converted.PixelWidth * s_Converted.PixelHeight * 4];
            s_Converted.CopyPixels(s_Pixels, s_Converted.PixelWidth * 4, 0);

            for (var i = 3; i < s_Pixels.Length; i += 4)
                if (s_Pixels[i] < 250)
                    return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// How many alpha-coverage taps a pixel shader's own instructions perform: the single-lane samples
    /// whose resource swizzle hands over the texture's ALPHA (the .w channel at the written lane). The
    /// plain vegetation-alpha solutions take two, the instanced ones four; anything else returns whatever
    /// it counts and the caller ignores counts that are not a measured tap pattern.
    /// </summary>
    private static int CountAlphaCoverageTaps(List<Translate.AsmInstruction> p_Instructions) =>
        p_Instructions.Count(p_I =>
            p_I.Opcode.StartsWith("sample", StringComparison.Ordinal) &&
            p_I.Destination is { Kind: Translate.OperandKind.Temp, WriteMask.Length: 1 } s_Dest &&
            p_I.Sources.FirstOrDefault(p_S => p_S.Kind == Translate.OperandKind.Resource) is { } s_Resource &&
            s_Resource.Swizzle.ElementAtOrDefault(s_Dest.WriteMask[0]) == 3);

    /// <summary>
    /// FNV-1a of the graph's emitted HLSL under the given contract, or null when it does not emit. The same
    /// deterministic emitter both stamps (at translate) and compares (at bake), so the hash answers exactly
    /// one question: did the shader's LOGIC change since it came out of the game?
    /// </summary>
    internal static string? HlslFingerprint(ShaderGraph p_Graph, ShaderContract? p_Contract)
    {
        try
        {
            var s_Emit = new HlslEmitter { Contract = p_Contract }.Emit(p_Graph);
            if (!s_Emit.Ok)
                return null;

            var s_Hash = 2166136261u;
            foreach (var s_Byte in System.Text.Encoding.UTF8.GetBytes(s_Emit.Hlsl))
            {
                s_Hash ^= s_Byte;
                s_Hash *= 16777619u;
            }

            return s_Hash.ToString("x8");
        }
        catch
        {
            return null;
        }
    }

    internal static string? FindTexconv()
    {
        var s_Candidates = new List<string>();

        if (FindRimeRepl() is { } s_Repl)
            s_Candidates.Add(Path.Combine(Path.GetDirectoryName(s_Repl)!, "texconv.exe"));

        s_Candidates.Add(Path.Combine(AppContext.BaseDirectory, "texconv.exe"));

        var s_Directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && s_Directory != null; i++, s_Directory = s_Directory.Parent)
            s_Candidates.Add(Path.Combine(s_Directory.FullName, "texconv.exe"));

        return s_Candidates.FirstOrDefault(File.Exists);
    }

    internal static string? FindRimeRepl()
    {
        var s_Directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && s_Directory != null; i++, s_Directory = s_Directory.Parent)
        {
            var s_Candidate = Path.Combine(s_Directory.FullName, "bin", "Release", "RimeREPL.exe");
            if (File.Exists(s_Candidate))
                return s_Candidate;
        }

        return null;
    }

    internal static string? FindFxc()
    {
        var s_Roots = new[]
        {
            @"C:\Program Files (x86)\Windows Kits\10\bin",
            @"C:\Program Files\Windows Kits\10\bin",
        };

        var s_Candidates = new List<string>();
        foreach (var s_Root in s_Roots)
        {
            if (!Directory.Exists(s_Root))
                continue;

            foreach (var s_Version in Directory.GetDirectories(s_Root))
            {
                var s_Path = Path.Combine(s_Version, "x64", "fxc.exe");
                if (File.Exists(s_Path))
                    s_Candidates.Add(s_Path);
            }
        }

        s_Candidates.Sort(StringComparer.OrdinalIgnoreCase);
        return s_Candidates.Count > 0 ? s_Candidates[^1] : null;
    }

    private void Log(string p_Message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {p_Message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }
}
