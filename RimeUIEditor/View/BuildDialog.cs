using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using RimeLib.Cmd.UiBuilder;

namespace RimeUIEditor.View
{
    /// <summary>What the user chose in <see cref="BuildDialog"/> besides the document's own fields (written straight into it).</summary>
    public sealed class BuildRequest
    {
        public string ModsFolder { get; init; } = "";
        /// <summary>The recorder rides in the mod's screen movies (Record data… on the mod as it ships, nothing else installed).</summary>
        public bool Record { get; init; }
    }

    /// <summary>
    /// Build mod…: the questions before the minutes, the way the shader editor's bake asks — what the mod is (name, version,
    /// authors, description), how it ships (superbundle / bundle names) and where it goes (the Mods folder). Shows the screens
    /// the document touches so the user sees what is about to ship. Built in code in the editor's dialog style. Installing
    /// (ModList.txt, the client's cached copy) is the user's own step: the Output says what is left to do, the tool never
    /// touches the server's or the client's files.
    /// </summary>
    public sealed class BuildDialog : Window
    {
        readonly ScreenDocument m_Doc;
        readonly TextBox m_Name, m_Version, m_Authors, m_Description, m_Superbundle, m_Bundle, m_Mods;
        readonly CheckBox m_Static, m_Record;
        readonly TextBlock m_Warning;
        bool m_SuperbundleTouched, m_BundleTouched;

        public BuildRequest? Request { get; private set; }

        public BuildDialog(Window p_Owner, ScreenDocument p_Doc, EditorSettings p_Settings)
        {
            m_Doc = p_Doc;
            Title = "Build mod";
            if (p_Owner.IsVisible) Owner = p_Owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Background = FolderUi.Panel;
            ShowInTaskbar = false;

            var s_Root = new StackPanel { Margin = new Thickness(14), Width = 560 };

            // ---- what ships
            s_Root.Children.Add(FolderUi.Section("Screens in this mod"));
            var s_List = new StackPanel();
            foreach (var s_Screen in p_Doc.Screens)
                s_List.Children.Add(new TextBlock { Text = "•  " + Summary(s_Screen), Foreground = FolderUi.Text, FontSize = 11, Opacity = 0.85, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 1, 0, 1) });
            if (p_Doc.Screens.Count == 0)
                s_List.Children.Add(new TextBlock { Text = "(nothing: drop a widget, wire a node or make a screen first)", Foreground = FolderUi.Warn, FontSize = 11, Margin = new Thickness(4, 1, 0, 1) });
            s_Root.Children.Add(new ScrollViewer { Content = s_List, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = FolderUi.Field, BorderBrush = FolderUi.Border, BorderThickness = new Thickness(1), Padding = new Thickness(4), Margin = new Thickness(0, 0, 0, 6) });

            // ---- what the mod is
            s_Root.Children.Add(FolderUi.Section("Mod"));
            m_Name = FolderUi.FieldBox(p_Doc.Name);
            s_Root.Children.Add(Labelled("Name", "The folder under Mods and the line in ModList.txt (letters, digits, _).", m_Name));
            var s_Row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            s_Row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            s_Row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            s_Row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            m_Version = FolderUi.FieldBox(string.IsNullOrWhiteSpace(p_Doc.Version) ? "1.0.0" : p_Doc.Version);
            m_Authors = FolderUi.FieldBox(string.Join(", ", p_Doc.Authors));
            var s_VersionCol = Labelled("Version", "major.minor.patch, as mod.json states it.", m_Version, 0);
            var s_AuthorsCol = Labelled("Authors", "Comma-separated, as mod.json lists them.", m_Authors, 0);
            Grid.SetColumn(s_VersionCol, 0); Grid.SetColumn(s_AuthorsCol, 2);
            s_Row.Children.Add(s_VersionCol); s_Row.Children.Add(s_AuthorsCol);
            s_Root.Children.Add(s_Row);
            m_Description = FolderUi.FieldBox(string.IsNullOrWhiteSpace(p_Doc.Description) ? ModEmitter.DescribeDocument(p_Doc) : p_Doc.Description);
            m_Description.AcceptsReturn = true; m_Description.TextWrapping = TextWrapping.Wrap; m_Description.MinHeight = 54; m_Description.MaxHeight = 90;
            m_Description.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            s_Root.Children.Add(Labelled("Description", "What the mod does, as mod.json says it (prefilled from the document — say what the files really do).", m_Description));

            // ---- how it ships
            s_Root.Children.Add(FolderUi.Section("Bundle"));
            m_Superbundle = FolderUi.FieldBox(p_Doc.Superbundle);
            m_Bundle = FolderUi.FieldBox(p_Doc.Bundle);
            s_Root.Children.Add(Labelled("Superbundle",
                "The mod's own superbundle: sb\\Win32\\<superbundle>.sb, declared in mod.json, mounted on every level by ext/Shared. " +
                "The edited screens keep their ORIGINAL resource names inside it, so they replace the game's (nothing of the game is renamed). " +
                "One name per mod, never shared with another mod (follows the name until you change it).", m_Superbundle));
            s_Root.Children.Add(Labelled("Bundle",
                "The bundle inside it. At load, ext/Shared puts it in front of the level's main pass (widget movies) and after the level's " +
                "_UiPlaying pass (screen movies) — the way the game finds the edited screens, measured in game.", m_Bundle));
            m_Static = new CheckBox
            {
                Content = new TextBlock { Text = "Ship the graph edits inside the bundle (static partitions, no runtime edits)", Foreground = FolderUi.Text },
                Foreground = FolderUi.Text, IsChecked = p_Doc.IsStatic, Margin = new Thickness(0, 0, 0, 2),
            };
            s_Root.Children.Add(m_Static);
            s_Root.Children.Add(new TextBlock
            {
                Text = "On: every edited screen ships as a whole partition (the game's, with your nodes, wires and bindings applied) under its own name; " +
                       "ext/Client only logs what loaded ([PROBE]). Off: ext/Client edits each screen's live graph at load — the delivery verified in game so far. " +
                       "The static one is what the next in-game test measures.",
                Foreground = FolderUi.Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 0, 0, 8),
            });

            m_Record = new CheckBox
            {
                Content = new TextBlock { Text = "Record what the game hands these screens (the recorder rides in the mod)", Foreground = FolderUi.Text },
                Foreground = FolderUi.Text, IsChecked = false, Margin = new Thickness(0, 4, 0, 2),
            };
            s_Root.Children.Add(m_Record);
            s_Root.Children.Add(new TextBlock
            {
                Text = "On: the built screen movies carry the editor's recorder and the mod gets the receiver (ext/Client) and the database side (ext/Server): " +
                       "open the screens in the game with THIS mod alone, press F5, then Import recording… — the edited screens' data, new nodes included. " +
                       "A diagnosis build: turn it off for the mod you play with.",
                Foreground = FolderUi.Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 0, 0, 8),
            });

            // ---- where it goes
            s_Root.Children.Add(FolderUi.Section("Where"));
            m_Mods = FolderUi.FieldBox(p_Settings.ModsPath);
            s_Root.Children.Add(FolderUi.Row(this, "Save the mod in", "The Mods folder (one sub-folder per mod). Normally the VU server's Admin\\Mods, so the server serves it straight away; the line in ModList.txt is yours to add.", m_Mods, "Choose the mods folder"));

            m_Warning = new TextBlock { Foreground = FolderUi.Warn, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 0) };
            s_Root.Children.Add(m_Warning);

            var s_Buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var s_Ok = new Button { Content = "Build", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
            var s_Cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };
            s_Ok.Click += (_, _) => { if (Problem() == null) { Apply(); DialogResult = true; } else Revalidate(); };
            s_Buttons.Children.Add(s_Ok); s_Buttons.Children.Add(s_Cancel);
            s_Root.Children.Add(s_Buttons);

            // the name drives the superbundle (and the superbundle the bundle) until the user takes them over: a mismatch nobody notices is a mod that does not load
            m_Superbundle.TextChanged += (_, _) => { if (m_Superbundle.IsFocused) m_SuperbundleTouched = true; if (!m_BundleTouched) m_Bundle.Text = BundleFor(m_Superbundle.Text); Revalidate(); };
            m_Bundle.TextChanged += (_, _) => { if (m_Bundle.IsFocused) m_BundleTouched = true; Revalidate(); };
            m_Name.TextChanged += (_, _) => { if (!m_SuperbundleTouched) m_Superbundle.Text = SuperbundleFor(m_Name.Text); Revalidate(); };
            m_Version.TextChanged += (_, _) => Revalidate();
            m_Mods.TextChanged += (_, _) => Revalidate();
            Revalidate();
            Content = s_Root;
            Loaded += (_, _) => { m_Name.Focus(); m_Name.SelectAll(); };
        }

        /// <summary>Shows the dialog; null when cancelled. The document's fields are written on Build.</summary>
        public static BuildRequest? Ask(Window p_Owner, ScreenDocument p_Doc, EditorSettings p_Settings)
        {
            var s_Dialog = new BuildDialog(p_Owner, p_Doc, p_Settings);
            return s_Dialog.ShowDialog() == true ? s_Dialog.Request : null;
        }

        /// <summary>One line per screen: what the document does to it (the "what ships" list).</summary>
        public static string Summary(ScreenEntry s)
        {
            var s_Parts = new List<string>();
            var s_New = s.Nodes.Count(n => n.New); var s_Edited = s.Nodes.Count - s_New;
            if (s_New > 0) s_Parts.Add($"{s_New} new node(s)");
            if (s_Edited > 0) s_Parts.Add($"{s_Edited} edited node(s)");
            var s_Wires = s.Nodes.Sum(n => n.Connections.Count);
            if (s_Wires > 0) s_Parts.Add($"{s_Wires} wire(s)");
            if (s.RemovedConnections.Count > 0) s_Parts.Add($"{s.RemovedConnections.Count} shipped wire(s) removed");
            if (s.Stage.Count > 0) s_Parts.Add($"{s.Stage.Count} stage op(s)");
            if (s.Fields.Count > 0) s_Parts.Add($"{s.Fields.Count} asset field(s)");
            var s_Name = s.Partition.Split('/').Last() + (s.New ? "  (new screen, from " + s.Template.Split('/').Last() + ")" : "");
            return s_Name + (s_Parts.Count > 0 ? "  —  " + string.Join(", ", s_Parts) : "  —  nothing yet");
        }

        public static string SanitizeName(string p_Text) => new(p_Text.Trim().Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());

        /// <summary>lower-case letters, digits, _ and / — the shape of a bundle path.</summary>
        public static string SanitizeBundle(string p_Text) => new(p_Text.Trim().ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '/').ToArray());

        public static string SuperbundleFor(string p_Name) => SanitizeName(p_Name).ToLowerInvariant() + "/ui";

        public static string BundleFor(string p_Superbundle)
        {
            var s_Root = SanitizeBundle(p_Superbundle).Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            return s_Root + "/uibundle";
        }

        static readonly Regex s_VersionShape = new(@"^\d+\.\d+\.\d+$");

        string? Problem()
        {
            if (m_Doc.Screens.Count == 0) return "Nothing to build: the document has no screens.";
            if (SanitizeName(m_Name.Text).Length == 0) return "The mod needs a name (letters, digits, _).";
            if (!s_VersionShape.IsMatch(m_Version.Text.Trim())) return "The version must look like 1.0.0.";
            if (SanitizeBundle(m_Superbundle.Text).Length == 0 || !SanitizeBundle(m_Superbundle.Text).Contains('/')) return "The superbundle needs a name like mymod/ui.";
            if (SanitizeBundle(m_Bundle.Text).Length == 0) return "The bundle needs a name.";
            if (m_Mods.Text.Trim().Length == 0) return "Pick a folder to save the mod in.";
            return null;
        }

        void Revalidate()
        {
            var s_Problem = Problem();
            if (s_Problem != null) { m_Warning.Text = s_Problem; return; }
            var s_Target = Path.Combine(m_Mods.Text.Trim(), SanitizeName(m_Name.Text));
            m_Warning.Text = $"Writes {s_Target}\\ (mod.json, ext\\, sb\\Win32\\{SanitizeBundle(m_Superbundle.Text)}.sb)." +
                             (Directory.Exists(s_Target) ? " That folder exists: its files are replaced." : "") +
                             " The built movies and the recipe stay in the editor's build folder, not in the mod." +
                             " Building mounts the game when it is not mounted yet (minutes); the Output panel reports each step.";
        }

        /// <summary>The document takes the dialog's fields; the request carries the rest.</summary>
        void Apply()
        {
            m_Doc.Name = SanitizeName(m_Name.Text);
            m_Doc.Version = m_Version.Text.Trim();
            m_Doc.Authors = m_Authors.Text.Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
            if (m_Doc.Authors.Count == 0) m_Doc.Authors.Add("keku");
            m_Doc.Description = m_Description.Text.Trim();
            m_Doc.Superbundle = SanitizeBundle(m_Superbundle.Text);
            m_Doc.Bundle = SanitizeBundle(m_Bundle.Text);
            m_Doc.GraphDelivery = m_Static.IsChecked == true ? "static" : "lua";
            Request = new BuildRequest { ModsFolder = m_Mods.Text.Trim(), Record = m_Record.IsChecked == true };
        }

        static UIElement Labelled(string p_Label, string p_Help, TextBox p_Box, double p_Bottom = 8)
        {
            var s_Panel = new StackPanel { Margin = new Thickness(0, 0, 0, p_Bottom) };
            s_Panel.Children.Add(new TextBlock { Text = p_Label, Foreground = FolderUi.Text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) });
            s_Panel.Children.Add(new TextBlock { Text = p_Help, Foreground = FolderUi.Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3) });
            s_Panel.Children.Add(p_Box);
            return s_Panel;
        }
    }
}
