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
    private const string c_GamePathPlaceholder = "<SET-YOUR-BF3-PATH>";

    private string? m_GraphPath;
    private readonly Dictionary<string, ImageSource> m_TexturePreviews = new();
    private readonly EditorSettings m_Settings = EditorSettings.Load();

    public MainWindow()
    {
        InitializeComponent();

        GamePathBox.Text = m_Settings.GamePath;
        OutputFolderBox.Text = m_Settings.OutputFolder;

        GamePathBox.LostKeyboardFocus += (_, _) => PersistSettings();
        OutputFolderBox.LostKeyboardFocus += (_, _) => PersistSettings();
        Closing += (_, _) => PersistSettings();

        BuildPalette();

        Canvas.TexturePreviewProvider = p_Register =>
            m_TexturePreviews.TryGetValue(p_Register, out var s_Image) ? s_Image : null;

        Canvas.SelectionChanged += (_, _) => BuildProperties();

        LoadGraph(SampleGraphs.VanillaEquivalent());

        Log("Ready. Right-drag pans, wheel zooms, double-click a node in the list to add it.");
        Log("Drag output square -> input square to wire. ALT + left-click on a wire breaks it.");
        Log("Ctrl+Z undo / Ctrl+Y redo.");

        if (GamePathBox.Text.Length > 0)
            Log($"BF3 found at {GamePathBox.Text}");
        else
            Log("BF3 install not detected — type its folder in the 'BF3 path' box to enable texture previews.");

        // Previews are cached on disk, so only the very first load per shader has to mount the game.
        if (LoadCachedPreviews() == 0)
            Log("Texture thumbnails are empty: press 'Load target textures' to read them from the game.");
    }

    private void PersistSettings()
    {
        m_Settings.GamePath = GamePathBox.Text.Trim();
        m_Settings.OutputFolder = OutputFolderBox.Text.Trim();
        m_Settings.Save();
    }

    private void BuildPalette()
    {
        PaletteTree.Items.Clear();

        foreach (var s_Group in Palette.All.GroupBy(p_D => p_D.Category).OrderBy(p_G => p_G.Key))
        {
            var s_Item = new TreeViewItem { Header = s_Group.Key, IsExpanded = true };
            foreach (var s_Def in s_Group.OrderBy(p_D => p_D.Title))
                s_Item.Items.Add(new TreeViewItem { Header = s_Def.Title, Tag = s_Def.Kind });

            PaletteTree.Items.Add(s_Item);
        }
    }

    private void OnPaletteDoubleClick(object p_Sender, RoutedEventArgs p_Args)
    {
        if (PaletteTree.SelectedItem is TreeViewItem { Tag: string s_Kind })
        {
            Canvas.AddNode(s_Kind);
            Log($"Added {Palette.Get(s_Kind).Title}.");
        }
    }

    private void LoadGraph(ShaderGraph p_Graph)
    {
        Canvas.Graph = p_Graph;
        TargetShaderBox.Text = p_Graph.TargetShader;
        BuildProperties();
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

        PropertiesHeader.Text = $"Properties — {s_Node.Def.Title}";

        if (s_Node.Def.Description.Length > 0)
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = s_Node.Def.Description,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Opacity = 0.85,
                Margin = new Thickness(0, 2, 0, 6),
            });

        foreach (var s_Param in s_Node.Def.Params)
        {
            PropertiesPanel.Children.Add(new TextBlock { Text = s_Param.Name, Margin = new Thickness(0, 8, 0, 2) });
            PropertiesPanel.Children.Add(BuildParamEditor(s_Node, s_Param));
        }

        BuildInputDefaults(s_Node);

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

            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = s_Connected
                    ? $"{s_Port.Name}  (driven by a wire)"
                    : $"{s_Port.Name}  ({s_Components} × float)",
                Margin = new Thickness(0, 6, 0, 2),
                FontSize = 11,
                Opacity = s_Connected ? 0.5 : 1.0,
            });

            var s_Box = new TextBox
            {
                Text = p_Node.GetInputOverride(s_Port.Name) ?? s_Port.DefaultAsText(),
                IsEnabled = !s_Connected,
            };

            var s_Original = s_Box.Text;
            s_Box.TextChanged += (_, _) =>
            {
                p_Node.SetInputOverride(s_Port.Name, s_Box.Text);
                Canvas.Refresh();
            };

            s_Box.GotKeyboardFocus += (_, _) => s_Original = s_Box.Text;
            s_Box.LostKeyboardFocus += (_, _) =>
            {
                if (s_Box.Text == s_Original)
                    return;

                var s_New = s_Box.Text;
                p_Node.SetInputOverride(s_Port.Name, s_Original);
                Canvas.PushUndo();
                p_Node.SetInputOverride(s_Port.Name, s_New);
                s_Original = s_New;

                // Surface a bad value straight away rather than at emit time.
                if (PortTypeUtils.FormatLiteral(s_New, s_Port.Type) == null && s_New.Trim().Length > 0)
                    Log($"'{s_Port.Name}' needs 1 or {s_Components} numbers — '{s_New}' will fall back to the default.");
            };

            PropertiesPanel.Children.Add(s_Box);
        }
    }

    private FrameworkElement BuildParamEditor(GraphNode p_Node, ParamDef p_Param)
    {
        if (p_Param.Kind == ParamKind.Bool)
        {
            var s_Check = new CheckBox
            {
                IsChecked = p_Node.GetParam(p_Param.Name).Equals("true", StringComparison.OrdinalIgnoreCase),
            };

            s_Check.Click += (_, _) =>
            {
                Canvas.PushUndo();
                p_Node.Params[p_Param.Name] = s_Check.IsChecked == true ? "true" : "false";
                Canvas.Refresh();
                BuildProperties();
            };

            return s_Check;
        }

        if (p_Param.Kind == ParamKind.Enum && p_Param.EnumType != null)
        {
            var s_Combo = new ComboBox();
            foreach (var s_Name in Enum.GetNames(p_Param.EnumType))
                s_Combo.Items.Add(s_Name);

            s_Combo.SelectedItem = p_Node.GetParam(p_Param.Name);
            s_Combo.SelectionChanged += (_, _) =>
            {
                if (s_Combo.SelectedItem is not string s_Value || s_Value == p_Node.GetParam(p_Param.Name))
                    return;

                Canvas.PushUndo();
                p_Node.Params[p_Param.Name] = s_Value;
                Canvas.Refresh();

                // Availability can depend on this value (Opacity follows SurfaceShaderType), so redraw the panel.
                BuildProperties();
            };

            return s_Combo;
        }

        var s_Box = new TextBox { Text = p_Node.GetParam(p_Param.Name) };
        var s_Original = s_Box.Text;

        s_Box.TextChanged += (_, _) =>
        {
            p_Node.Params[p_Param.Name] = s_Box.Text;
            Canvas.Refresh();
        };

        // One undo step per editing session rather than per keystroke.
        s_Box.GotKeyboardFocus += (_, _) => s_Original = s_Box.Text;
        s_Box.LostKeyboardFocus += (_, _) =>
        {
            if (s_Box.Text == s_Original)
                return;

            var s_New = s_Box.Text;
            p_Node.Params[p_Param.Name] = s_Original;
            Canvas.PushUndo();
            p_Node.Params[p_Param.Name] = s_New;
            s_Original = s_New;
        };

        return s_Box;
    }

    private void OnNew(object p_Sender, RoutedEventArgs p_Args)
    {
        var s_Graph = new ShaderGraph { Name = "Untitled", TargetShader = TargetShaderBox.Text };
        s_Graph.Nodes.Add(new GraphNode { Kind = "StandardRoot", X = 120, Y = -80 });
        m_GraphPath = null;
        LoadGraph(s_Graph);
        Log("New graph with a StandardRoot.");
    }

    private void OnLoadSample(object p_Sender, RoutedEventArgs p_Args)
    {
        m_GraphPath = null;
        LoadGraph(SampleGraphs.VanillaEquivalent());
        Log("Loaded the oil-drum vanilla-equivalent graph (correctness harness).");
    }

    private void OnOpen(object p_Sender, RoutedEventArgs p_Args)
    {
        var s_Dialog = new OpenFileDialog { Filter = "Shader graph (*.json)|*.json|All files (*.*)|*.*" };
        if (s_Dialog.ShowDialog() != true)
            return;

        try
        {
            LoadGraph(ShaderGraph.FromJson(File.ReadAllText(s_Dialog.FileName)));
            m_GraphPath = s_Dialog.FileName;
            Log($"Opened {s_Dialog.FileName}");
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
            File.WriteAllText(s_Dialog.FileName, Canvas.Graph.ToJson());
            m_GraphPath = s_Dialog.FileName;
            Log($"Saved {s_Dialog.FileName}");
        }
        catch (Exception s_Exception)
        {
            Log($"ERROR saving graph: {s_Exception.Message}");
        }
    }

    private void OnDeleteNode(object p_Sender, RoutedEventArgs p_Args) => Canvas.DeleteSelected();

    private string? EmitToDisk()
    {
        Canvas.Graph.TargetShader = TargetShaderBox.Text;

        var s_Result = new HlslEmitter().Emit(Canvas.Graph);
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

    private void OnEmit(object p_Sender, RoutedEventArgs p_Args) => EmitToDisk();

    private void OnCompile(object p_Sender, RoutedEventArgs p_Args)
    {
        var s_HlslPath = EmitToDisk();
        if (s_HlslPath == null)
            return;

        var s_Fxc = FindFxc();
        if (s_Fxc == null)
        {
            Log("ERROR: fxc.exe not found. Install the Windows SDK or put fxc.exe on PATH.");
            return;
        }

        var s_DxbcPath = Path.ChangeExtension(s_HlslPath, ".dxbc");
        var s_Result = Run(s_Fxc, new[]
        {
            "/nologo", "/T", "ps_5_0", "/E", "main", "/O3", "/Fo", s_DxbcPath, s_HlslPath,
        });

        foreach (var s_Line in s_Result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Log($"  fxc: {s_Line.TrimEnd()}");

        if (s_Result.ExitCode == 0 && File.Exists(s_DxbcPath))
            Log($"Compiled -> {s_DxbcPath} ({new FileInfo(s_DxbcPath).Length} bytes)");
        else
            Log($"fxc failed (exit {s_Result.ExitCode}).");
    }

    private string TexCacheDir => Path.Combine(OutputFolderBox.Text, "texcache");

    internal static string SlotMapFileName(string p_Target) => $"{Sanitize(p_Target)}.slots.json";

    private string SlotMapPath(string p_Target) => Path.Combine(TexCacheDir, SlotMapFileName(p_Target));

    /// <summary>
    /// Repopulates the thumbnails from the on-disk cache. Returns how many were restored, so the caller can
    /// tell the difference between "nothing cached yet" and "cache served everything".
    /// </summary>
    private int LoadCachedPreviews()
    {
        var s_Target = TargetShaderBox.Text.Trim();
        if (s_Target.Length == 0)
            return 0;

        try
        {
            var s_MapPath = SlotMapPath(s_Target);
            if (!File.Exists(s_MapPath))
                return 0;

            var s_Slots = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(s_MapPath));

            if (s_Slots == null)
                return 0;

            var s_Count = 0;
            foreach (var (s_Register, s_Name) in s_Slots)
            {
                var s_Png = Path.Combine(TexCacheDir, $"{Sanitize(s_Name)}.png");
                if (!File.Exists(s_Png))
                    continue;

                var s_Bitmap = new BitmapImage();
                s_Bitmap.BeginInit();
                s_Bitmap.CacheOption = BitmapCacheOption.OnLoad;
                s_Bitmap.UriSource = new Uri(s_Png);
                s_Bitmap.EndInit();
                s_Bitmap.Freeze();

                m_TexturePreviews[s_Register] = s_Bitmap;
                s_Count++;
            }

            if (s_Count > 0)
            {
                Canvas.Refresh();
                Log($"Loaded {s_Count} cached thumbnail(s) from {TexCacheDir}");
            }

            return s_Count;
        }
        catch (Exception s_Exception)
        {
            Log($"Could not read the thumbnail cache: {s_Exception.Message}");
            return 0;
        }
    }

    private async void OnLoadTextures(object p_Sender, RoutedEventArgs p_Args)
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

        var s_ShaderDb = ShaderDbForTarget(s_Target);
        var s_CacheDir = TexCacheDir;
        var s_MapPath = SlotMapPath(s_Target);
        TexturesButton.IsEnabled = false;
        Log($"Mounting BF3 to read '{s_Target}' from {s_ShaderDb} — this takes ~20 s (two mounts), please wait…");

        try
        {
            var s_Log = await Task.Run(() =>
                LoadTexturesCore(s_Repl, s_GamePath, s_ShaderDb, s_Target, s_CacheDir, s_MapPath));

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
            TexturesButton.IsEnabled = true;
        }
    }

    internal static List<string> LoadTexturesCore(string p_Repl, string p_GamePath, string p_ShaderDb,
        string p_Target, string p_CacheDir, string p_MapPath)
    {
        var s_Result = new List<string>();
        Directory.CreateDirectory(p_CacheDir);

        var s_ListScript = Path.Combine(p_CacheDir, "list.rime");
        File.WriteAllText(s_ListScript,
            $"mount_game \"{p_GamePath}\" Frostbite2_0 true\nselect_game 1\n" +
            $"dump_shader_textures {p_ShaderDb} {p_Target}\nexit\n");

        var s_List = Run(p_Repl, new[] { s_ListScript });
        var s_Slots = new Dictionary<string, string>();

        // Textures bound as shader CONSTANTS carry their real sampler register.
        foreach (Match s_Match in Regex.Matches(s_List.Output, @"SHTEX:\s*register=t(\d+)\s+name=(\S+)"))
            s_Slots[s_Match.Groups[1].Value] = s_Match.Groups[2].Value;

        // Mesh shaders normally bind STREAMABLE textures instead, and those do not record a register. The
        // drum's disassembly shows declaration order mapping to t1, t2, ... so that is the assumption here;
        // it is a guess, hence the log line, and the Register parameter on the node stays authoritative.
        var s_Streamables = Regex.Matches(s_List.Output, @"SHTEX-STREAMABLE:\s*index=(\d+)\s+name=(\S+)");
        if (s_Streamables.Count > 0)
        {
            s_Result.Add("Textures are STREAMABLE (no register recorded); assuming declaration order -> t1, t2, …");
            foreach (Match s_Match in s_Streamables)
            {
                var s_Register = (int.Parse(s_Match.Groups[1].Value) + 1).ToString();
                if (!s_Slots.ContainsKey(s_Register))
                    s_Slots[s_Register] = s_Match.Groups[2].Value;
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

        var s_DumpScript = new StringBuilder();
        s_DumpScript.AppendLine($"mount_game \"{p_GamePath}\" Frostbite2_0 true");
        s_DumpScript.AppendLine("select_game 1");

        var s_Files = new Dictionary<string, string>();
        foreach (var (s_Register, s_Name) in s_Slots)
        {
            var s_File = Path.Combine(p_CacheDir, $"t{s_Register}_{Sanitize(s_Name)}.dds");
            s_Files[s_Register] = s_File;
            s_DumpScript.AppendLine($"dump_texture {s_Name} \"{s_File}\"");
        }

        s_DumpScript.AppendLine("exit");

        var s_DumpScriptPath = Path.Combine(p_CacheDir, "dump.rime");
        File.WriteAllText(s_DumpScriptPath, s_DumpScript.ToString());
        Run(p_Repl, new[] { s_DumpScriptPath });

        var s_Saved = new Dictionary<string, string>();

        foreach (var (s_Register, s_File) in s_Files)
        {
            if (!File.Exists(s_File))
            {
                s_Result.Add($"  t{s_Register}: no DDS produced (texture may be a streaming/residency case).");
                continue;
            }

            var s_Image = DdsImage.Load(s_File);
            if (s_Image == null)
            {
                s_Result.Add($"  t{s_Register}: DDS not decodable for preview ({new FileInfo(s_File).Length} B).");
                continue;
            }

            var s_Png = Path.Combine(p_CacheDir, $"{Sanitize(s_Slots[s_Register])}.png");
            var s_Encoder = new PngBitmapEncoder();
            s_Encoder.Frames.Add(BitmapFrame.Create(s_Image));
            using (var s_Stream = File.Create(s_Png))
                s_Encoder.Save(s_Stream);

            s_Saved[s_Register] = s_Slots[s_Register];
            s_Result.Add($"  t{s_Register}: thumbnail {s_Image.PixelWidth}x{s_Image.PixelHeight} -> {Path.GetFileName(s_Png)}");
        }

        File.WriteAllText(p_MapPath, System.Text.Json.JsonSerializer.Serialize(s_Saved));
        return s_Result;
    }

    private void OnWriteBakeScript(object p_Sender, RoutedEventArgs p_Args)
    {
        var s_Target = TargetShaderBox.Text.Trim();
        if (s_Target.Length == 0)
        {
            Log("ERROR: set the target shader first.");
            return;
        }

        var s_ShaderDb = ShaderDbForTarget(s_Target);
        var s_GamePath = GamePathBox.Text.Trim();
        if (s_GamePath.Length == 0)
            s_GamePath = c_GamePathPlaceholder;

        var s_Dxbc = Path.Combine(OutputFolderBox.Text, $"{SafeName()}.dxbc");
        var s_Builder = new StringBuilder();
        s_Builder.AppendLine($"mount_game \"{s_GamePath}\" Frostbite2_0 true");
        s_Builder.AppendLine("select_game 1");
        s_Builder.AppendLine(
            $"replace_shader_bytecode {s_ShaderDb} {s_Target} \"{s_Dxbc}\" " +
            $"\"{Path.Combine(OutputFolderBox.Text, "shaderdb_patched.bin")}\" " +
            "ShaderRenderMode_DeferredShadingGBufferLayout0");
        s_Builder.AppendLine("exit");

        try
        {
            Directory.CreateDirectory(OutputFolderBox.Text);
            var s_Path = Path.Combine(OutputFolderBox.Text, $"{SafeName()}_inject.rime");
            File.WriteAllText(s_Path, s_Builder.ToString());
            Log($"Wrote {s_Path}");
            Log("Run it with: RimeREPL.exe <that file>   (from Rime-src\\bin\\Release)");
            Log("The Mode argument keeps the ZOnly depth-pass permutations untouched.");

            if (s_GamePath == c_GamePathPlaceholder)
                Log($"NOTE: fill in the BF3 path box; the script currently contains {c_GamePathPlaceholder}.");
        }
        catch (Exception s_Exception)
        {
            Log($"ERROR writing bake script: {s_Exception.Message}");
        }
    }

    /// <summary>
    /// Level shaders live in that level's shaderdb; anything else has to be pointed at a level by hand,
    /// because the same shader is compiled into every level that uses it.
    /// </summary>
    internal static string ShaderDbForTarget(string p_Target)
    {
        var s_Parts = p_Target.Split('/');
        if (s_Parts.Length > 2 && s_Parts[0].Equals("Levels", StringComparison.OrdinalIgnoreCase))
            return $"levels/{s_Parts[1].ToLowerInvariant()}/{s_Parts[1].ToLowerInvariant()}/shaderdb";

        return "levels/mp_017/mp_017/shaderdb";
    }

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
