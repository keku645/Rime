using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RimeUIEditor.View
{
    /// <summary>Shared look and the folder row used by both dialogs (same as the shader editor's).</summary>
    static class FolderUi
    {
        public static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(45, 45, 48));
        public static readonly Brush Field = new SolidColorBrush(Color.FromRgb(30, 30, 30));
        public static readonly Brush Text = new SolidColorBrush(Color.FromRgb(220, 220, 220));
        public static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(150, 150, 150));
        public static readonly Brush Border = new SolidColorBrush(Color.FromRgb(84, 84, 90));
        public static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(255, 192, 96));

        public static TextBox FieldBox(string p_Value) => new()
        {
            Text = p_Value, Background = Field, Foreground = Text, BorderBrush = Border,
            BorderThickness = new Thickness(1), Padding = new Thickness(4, 3, 4, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        public static TextBlock Section(string p_Text) => new()
        {
            Text = p_Text, Foreground = Text, FontWeight = FontWeights.Bold, FontSize = 13,
            Margin = new Thickness(0, 8, 0, 4),
        };

        /// <summary>Label + help + text box + Browse… (a folder picker starting at the current value).</summary>
        public static UIElement Row(Window p_Owner, string p_Label, string p_Help, TextBox p_Box, string p_BrowseTitle)
        {
            var s_Panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            s_Panel.Children.Add(new TextBlock { Text = p_Label, Foreground = Text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) });
            s_Panel.Children.Add(new TextBlock { Text = p_Help, Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
            var s_Line = new DockPanel();
            var s_Browse = new Button { Content = "Browse…", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
            s_Browse.Click += (_, _) =>
            {
                var s_Dialog = new Microsoft.Win32.OpenFolderDialog { Title = p_BrowseTitle };
                if (Directory.Exists(p_Box.Text.Trim()))
                    s_Dialog.InitialDirectory = p_Box.Text.Trim();
                if (s_Dialog.ShowDialog(p_Owner) == true)
                    p_Box.Text = s_Dialog.FolderName;
            };
            DockPanel.SetDock(s_Browse, Dock.Right);
            s_Line.Children.Add(s_Browse);
            s_Line.Children.Add(p_Box);
            s_Panel.Children.Add(s_Line);
            return s_Panel;
        }

        /// <summary>Label + help + text box + Browse… for a single file (an executable).</summary>
        public static UIElement FileRow(Window p_Owner, string p_Label, string p_Help, TextBox p_Box, string p_BrowseTitle, string p_Filter)
        {
            var s_Panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            s_Panel.Children.Add(new TextBlock { Text = p_Label, Foreground = Text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) });
            s_Panel.Children.Add(new TextBlock { Text = p_Help, Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
            var s_Line = new DockPanel();
            var s_Browse = new Button { Content = "Browse…", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
            s_Browse.Click += (_, _) =>
            {
                var s_Dialog = new Microsoft.Win32.OpenFileDialog { Title = p_BrowseTitle, Filter = p_Filter, CheckFileExists = true };
                var s_Dir = Path.GetDirectoryName(p_Box.Text.Trim());
                if (!string.IsNullOrEmpty(s_Dir) && Directory.Exists(s_Dir)) s_Dialog.InitialDirectory = s_Dir;
                if (s_Dialog.ShowDialog(p_Owner) == true) p_Box.Text = s_Dialog.FileName;
            };
            DockPanel.SetDock(s_Browse, Dock.Right);
            s_Line.Children.Add(s_Browse);
            s_Line.Children.Add(p_Box);
            s_Panel.Children.Add(s_Line);
            return s_Panel;
        }

        /// <summary>Label + slider + "×1.00" readout, the shader editor's speed row. Writes through on every move.</summary>
        public static UIElement SpeedRow(string p_Label, Func<double> p_Get, Action<double> p_Set)
        {
            var s_Row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            s_Row.Children.Add(new TextBlock { Text = p_Label, Foreground = Text, Width = 130, VerticalAlignment = VerticalAlignment.Center });
            var s_Value = new TextBlock
            {
                Text = "×" + p_Get().ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                Foreground = Dim, Width = 46, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(s_Value, Dock.Right);
            s_Row.Children.Add(s_Value);
            var s_Slider = new Slider
            {
                Minimum = 0.25, Maximum = 4.0, Value = p_Get(), TickFrequency = 0.05, IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0),
            };
            s_Slider.ValueChanged += (_, s_Args) =>
            {
                p_Set(s_Args.NewValue);
                s_Value.Text = "×" + s_Args.NewValue.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            };
            s_Row.Children.Add(s_Slider);
            return s_Row;
        }

        public static TextBlock Note() => new()
        {
            Foreground = Warn, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, -6, 0, 10), Visibility = Visibility.Collapsed,
        };

        /// <summary>Says what is wrong without blocking: the user may know his machine better than the guess.</summary>
        public static void Revalidate(TextBox p_Game, TextBlock p_Note)
        {
            var s_Path = p_Game.Text.Trim();
            var s_Bad = s_Path.Length > 0 && !EditorSettings.LooksLikeGame(s_Path);
            p_Note.Text = s_Path.Length == 0
                ? "Empty: nothing can be read from the game until this is set."
                : "That folder has no bf3.exe and no Data folder — check it is the install root.";
            p_Note.Visibility = s_Bad || s_Path.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// The two folders the editor cannot work without, asked once, the first time it runs — prefilled with the
    /// guesses (registry for the game, the VU server layout for the mods), one click to accept.
    /// </summary>
    public sealed class FirstRunWindow : Window
    {
        readonly TextBox m_Game, m_Mods;

        public FirstRunWindow(EditorSettings p_Settings)
        {
            Title = "Welcome";
            Background = FolderUi.Panel;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Height;
            Width = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;

            var s_Root = new StackPanel { Margin = new Thickness(16) };
            s_Root.Children.Add(new TextBlock { Text = "Where things live", Foreground = FolderUi.Text, FontWeight = FontWeights.Bold, FontSize = 15, Margin = new Thickness(0, 0, 0, 4) });
            s_Root.Children.Add(new TextBlock
            {
                Text = "Both are filled in with a guess. Change them if the guess is wrong — they can be changed later in Settings.",
                Foreground = FolderUi.Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14),
            });

            m_Game = FolderUi.FieldBox(p_Settings.GamePath);
            var s_Note = FolderUi.Note();
            s_Root.Children.Add(FolderUi.Row(this, "Game folder",
                "The Battlefield 3 install the editor reads screens and widgets out of. Nothing in it is ever written to.",
                m_Game, "Choose the game folder"));
            s_Root.Children.Add(s_Note);

            m_Mods = FolderUi.FieldBox(p_Settings.ModsPath);
            s_Root.Children.Add(FolderUi.Row(this, "Mods folder",
                "Where Build mod writes each mod (one sub-folder per mod, ready for ModList.txt). Normally the VU server's Admin\\Mods.",
                m_Mods, "Choose the mods folder"));

            var s_Ok = new Button { Content = "Continue", Padding = new Thickness(18, 3, 18, 3), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            s_Ok.Click += (_, _) =>
            {
                try { Directory.CreateDirectory(m_Mods.Text.Trim()); } catch { /* reported by the first build */ }
                DialogResult = true;
            };
            s_Root.Children.Add(s_Ok);

            m_Game.TextChanged += (_, _) => FolderUi.Revalidate(m_Game, s_Note);
            FolderUi.Revalidate(m_Game, s_Note);
            Content = s_Root;
        }

        public string GamePath => m_Game.Text.Trim();
        public string ModsPath => m_Mods.Text.Trim();
    }

    /// <summary>Settings…: the same two folders, editable any time. Saved on close.</summary>
    public sealed class SettingsWindow : Window
    {
        public SettingsWindow(Window p_Owner, EditorSettings p_Settings, Action p_Changed)
        {
            Title = "Editor Settings";
            if (p_Owner.IsVisible) Owner = p_Owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Background = FolderUi.Panel;
            ShowInTaskbar = false;

            var s_Root = new StackPanel { Margin = new Thickness(14), Width = 460 };
            s_Root.Children.Add(FolderUi.Section("Folders"));
            var s_Game = FolderUi.FieldBox(p_Settings.GamePath);
            var s_Note = FolderUi.Note();
            s_Root.Children.Add(FolderUi.Row(this, "Game folder", "Read-only: screens and widget movies come out of here. Changing it takes effect on the next mount.", s_Game, "Choose the game folder"));
            s_Root.Children.Add(s_Note);
            var s_Mods = FolderUi.FieldBox(p_Settings.ModsPath);
            s_Root.Children.Add(FolderUi.Row(this, "Mods folder", "Where Build mod writes each mod (one sub-folder per mod).", s_Mods, "Choose the mods folder"));

            var s_Cache = FolderUi.FieldBox(p_Settings.CachePath);
            s_Root.Children.Add(FolderUi.Row(this, "Cache folder",
                "What the first mount saves (screens, widgets, atlases, fonts, texts) so later runs never wait for a mount. One sub-folder per game install; safe to delete.",
                s_Cache, "Choose the cache folder"));
            var s_CacheButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, -6, 0, 10) };
            var s_Open = new Button { Content = "Open folder", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0) };
            s_Open.Click += (_, _) => { try { Directory.CreateDirectory(s_Cache.Text.Trim()); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(s_Cache.Text.Trim()) { UseShellExecute = true }); } catch { } };
            var s_Clear = new Button { Content = "Clear cache", Padding = new Thickness(10, 2, 10, 2) };
            s_Clear.Click += (_, _) =>
            {
                try { if (Directory.Exists(s_Cache.Text.Trim())) Directory.Delete(s_Cache.Text.Trim(), true); s_Clear.Content = "Cleared — mount again to refill"; }
                catch (Exception s_Ex) { s_Clear.Content = "Clear failed: " + s_Ex.Message; }
            };
            s_CacheButtons.Children.Add(s_Open); s_CacheButtons.Children.Add(s_Clear);
            s_Root.Children.Add(s_CacheButtons);

            s_Root.Children.Add(FolderUi.Section("Stage"));
            var s_LangRow = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            s_LangRow.Children.Add(new TextBlock { Text = "Text language", Foreground = FolderUi.Text, Width = 130, VerticalAlignment = VerticalAlignment.Center });
            var s_Lang = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var (s_Code, s_Label) in new[] { ("us", "English (us)"), ("es", "Spanish (es)"), ("ge", "German (ge)"), ("fr", "French (fr)"), ("it", "Italian (it)"), ("ru", "Russian (ru)"), ("pl", "Polish (pl)"), ("cs", "Czech (cs)"), ("jp", "Japanese (jp)"), ("ko", "Korean (ko)"), ("zh", "Chinese (zh)") })
                s_Lang.Items.Add(new ComboBoxItem { Content = s_Label, Tag = s_Code });
            s_Lang.SelectedIndex = System.Math.Max(0, Array.IndexOf(new[] { "us", "es", "ge", "fr", "it", "ru", "pl", "cs", "jp", "ko", "zh" }, p_Settings.Language));
            s_LangRow.Children.Add(s_Lang);
            s_Root.Children.Add(s_LangRow);
            s_Root.Children.Add(new TextBlock
            {
                Text = "Which text database resolves the ID_* strings drawn on the stage. English is the default whatever the machine's locale; only languages installed with the game exist.",
                Foreground = FolderUi.Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            });
            s_Root.Children.Add(FolderUi.SpeedRow("Zoom speed", () => p_Settings.CanvasZoomSpeed, p_V => { p_Settings.CanvasZoomSpeed = p_V; StageViewport.ZoomSpeed = p_V; }));
            s_Root.Children.Add(new TextBlock
            {
                Text = "How far one wheel notch zooms the stage. Wheel zooms toward the cursor; right or middle button drags the view; F frames the stage.",
                Foreground = FolderUi.Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            });

            s_Root.Children.Add(FolderUi.Section("Live preview"));
            var s_Ruffle = FolderUi.FieldBox(p_Settings.RufflePath);
            s_Root.Children.Add(FolderUi.FileRow(this, "Ruffle player",
                "ruffle.exe, the desktop build of the open-source Flash player (ruffle.rs, Windows x86_64). Preview in Ruffle converts the open screen with your edits and plays it with the game's own ActionScript: the widgets build themselves, hover and press animate. Found on its own when it sits next to the editor or in a \"ruffle\" folder beside it.",
                s_Ruffle, "Choose ruffle.exe", "Ruffle (ruffle.exe)|ruffle.exe|Executables|*.exe"));

            s_Root.Children.Add(FolderUi.Section("Pictures"));
            var s_Texconv = FolderUi.FieldBox(p_Settings.TexconvPath);
            s_Root.Children.Add(FolderUi.FileRow(this, "texconv",
                "texconv.exe (DirectXTex). Build mod turns every picture of the document (Add image…) into a DXT5 texture of the game with it. Found on its own next to the editor or in the Rime tools folder (bin\\Release).",
                s_Texconv, "Choose texconv.exe", "texconv (texconv.exe)|texconv.exe|Executables|*.exe"));

            var s_Close = new Button { Content = "Close", Padding = new Thickness(18, 3, 18, 3), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            s_Close.Click += (_, _) => Close();
            s_Root.Children.Add(s_Close);

            s_Game.TextChanged += (_, _) => FolderUi.Revalidate(s_Game, s_Note);
            FolderUi.Revalidate(s_Game, s_Note);
            Closed += (_, _) =>
            {
                p_Settings.GamePath = s_Game.Text.Trim();
                p_Settings.ModsPath = s_Mods.Text.Trim();
                p_Settings.CachePath = s_Cache.Text.Trim();
                p_Settings.Language = (s_Lang.SelectedItem as ComboBoxItem)?.Tag as string ?? "us";
                p_Settings.RufflePath = s_Ruffle.Text.Trim();
                p_Settings.TexconvPath = s_Texconv.Text.Trim();
                RimeLib.Cmd.UiBuilder.ImageTextures.TexconvOverride = p_Settings.TexconvPath;
                p_Settings.Save();
                p_Changed();
            };
            Content = s_Root;
        }
    }
}
