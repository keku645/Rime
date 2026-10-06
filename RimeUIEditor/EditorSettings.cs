using System;
using System.IO;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace RimeUIEditor
{
    /// <summary>
    /// The paths the editor cannot work without, typed at most once. Same shape as the shader editor's
    /// settings: the game folder is guessed from the installer's registry key, the mods folder from the VU
    /// server layout under Documents; the first run shows both and asks, Settings… lets them be changed.
    /// </summary>
    public class EditorSettings
    {
        /// <summary>The BF3 install. Read-only: screens and widget movies come out of here.</summary>
        public string GamePath { get; set; } = "";

        /// <summary>Where a built mod is written (the VU server's Admin\Mods folder, one sub-folder per mod).</summary>
        public string ModsPath { get; set; } = "";

        public string LastDocument { get; set; } = "";

        /// <summary>The screens and flow graphs that were open when the editor last ran (its tabs), reopened on the next start.</summary>
        public System.Collections.Generic.List<string> OpenScreens { get; set; } = new();

        /// <summary>The one that was on the stage.</summary>
        public string CurrentScreen { get; set; } = "";

        /// <summary>
        /// Root of the UI cache (one sub-folder per game install): partition dumps, movies, atlas textures,
        /// fonts and text databases, written on the first mount so later runs never mount just to edit.
        /// </summary>
        public string CachePath { get; set; } = "";

        /// <summary>
        /// Text database language for the ID_* strings the stage shows: the game's own codes ("us" English, "es",
        /// "ge", "fr", "it", "ru", "jp", "cs", "ko", "pl", "zh"); only installed languages are mounted. English
        /// by default, whatever the machine's locale.
        /// </summary>
        public string Language { get; set; } = "us";

        /// <summary>Multiplier on the stage's wheel-zoom step. 1 = the shader editor's speed.</summary>
        public double CanvasZoomSpeed { get; set; } = 1.0;

        /// <summary>The Flash player that runs the live preview (ruffle.exe, the desktop build). Empty = not installed; Preview in Ruffle then says where to get it.</summary>
        public string RufflePath { get; set; } = "";

        /// <summary>texconv.exe (DirectXTex), which turns the document's pictures into the game's DXT5 textures at build time. Empty = looked for beside the tools.</summary>
        public string TexconvPath { get; set; } = "";

        /// <summary>
        /// The editor's window when it last closed — its normal (un-maximised) rectangle in screen pixels and whether it was maximised —
        /// so it opens on the same monitor, at the same place, the next time (the user moves the editor to the monitor he wants it on and
        /// expects it to stay there). Absent until the first close; ignored when that monitor is gone.
        /// </summary>
        public WindowPlacement? Window { get; set; }

        public class WindowPlacement
        {
            public int Left { get; set; }
            public int Top { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public bool Maximized { get; set; }
            public override string ToString() => $"({Left},{Top}) {Width}x{Height}{(Maximized ? " maximised" : "")}";
        }

        /// <summary>
        /// True when no settings file existed: the first time the editor runs on this machine.
        /// Not serialised — it is a fact about the load, not a preference.
        /// </summary>
        [JsonIgnore]
        public bool IsFirstRun { get; private set; }

        /// <summary>
        /// Test seams point this at a scratch file (or set RUE_SETTINGS) so they never touch the user's file.
        /// </summary>
        public static string? PathOverride;

        /// <summary>True while a CLI mode drives a hidden window: no dialogs, and never write the real file.</summary>
        public static bool Headless;

        static string SettingsPath =>
            PathOverride ?? Environment.GetEnvironmentVariable("RUE_SETTINGS") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimeUIEditor", "settings.json");

        public static EditorSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var s_Loaded = JsonConvert.DeserializeObject<EditorSettings>(File.ReadAllText(SettingsPath));
                    if (s_Loaded != null)
                        return s_Loaded.WithDefaults();
                }
                // No file = first run. A corrupt file does NOT count: its paths may still be right.
                return new EditorSettings { IsFirstRun = true }.WithDefaults();
            }
            catch
            {
                return new EditorSettings().WithDefaults();
            }
        }

        EditorSettings WithDefaults()
        {
            if (string.IsNullOrWhiteSpace(GamePath))
                GamePath = DetectGamePath() ?? "";
            if (string.IsNullOrWhiteSpace(ModsPath))
                ModsPath = DetectModsPath();
            if (string.IsNullOrWhiteSpace(CachePath))
                CachePath = DefaultCachePath();
            if (string.IsNullOrWhiteSpace(Language))
                Language = "us";
            if (string.IsNullOrWhiteSpace(RufflePath))
                RufflePath = DetectRufflePath();
            if (string.IsNullOrWhiteSpace(TexconvPath))
                TexconvPath = RimeLib.Cmd.UiBuilder.ImageTextures.FindTexconv() ?? "";
            // a hand-edited or corrupt file must not leave the stage frozen (zoom speed 0) or wild
            CanvasZoomSpeed = double.IsFinite(CanvasZoomSpeed) ? System.Math.Clamp(CanvasZoomSpeed, 0.25, 4.0) : 1.0;
            return this;
        }

        public void Save()
        {
            if (Headless && PathOverride == null && Environment.GetEnvironmentVariable("RUE_SETTINGS") == null)
                return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch
            {
                // losing the settings is not worth interrupting the user over
            }
        }

        /// <summary>ONE predicate for "is this the game?", shared by the first-run window, Settings and the mount.</summary>
        public static bool LooksLikeGame(string? p_Folder) =>
            !string.IsNullOrWhiteSpace(p_Folder) && Directory.Exists(p_Folder) &&
            (File.Exists(Path.Combine(p_Folder, "bf3.exe")) || Directory.Exists(Path.Combine(p_Folder, "Data")));

        /// <summary>A mods folder is any existing folder; the VU server layout is recognised to help the guess.</summary>
        public static bool LooksLikeModsFolder(string? p_Folder) =>
            !string.IsNullOrWhiteSpace(p_Folder) && Directory.Exists(p_Folder);

        /// <summary>Reads the BF3 install directory the retail installer records under EA Games.</summary>
        public static string? DetectGamePath()
        {
            foreach (var s_Key in new[] { @"SOFTWARE\WOW6432Node\EA Games\Battlefield 3", @"SOFTWARE\EA Games\Battlefield 3" })
            {
                try
                {
                    using var s_Reg = Registry.LocalMachine.OpenSubKey(s_Key);
                    if (s_Reg?.GetValue("Install Dir") is string s_Path)
                    {
                        s_Path = s_Path.TrimEnd('\\', '/');
                        if (Directory.Exists(s_Path))
                            return s_Path;
                    }
                }
                catch { /* unreadable key: next candidate */ }
            }
            return null;
        }

        /// <summary>Next to the settings unless a scratch settings file is in use (then next to that file).</summary>
        public static string DefaultCachePath()
        {
            var s_Override = PathOverride ?? Environment.GetEnvironmentVariable("RUE_SETTINGS");
            if (s_Override != null)
                return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(s_Override))!, "cache");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimeUIEditor", "cache");
        }

        /// <summary>Where a build keeps its intermediates (built movies, new partitions, the recipe): next to the cache, one sub-folder per mod. Never inside the mod.</summary>
        public static string DefaultBuildRoot()
        {
            var s_Override = PathOverride ?? Environment.GetEnvironmentVariable("RUE_SETTINGS");
            if (s_Override != null)
                return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(s_Override))!, "build");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimeUIEditor", "build");
        }

        /// <summary>Where the live preview's converted movies go: next to the cache. Rewritten on every preview.</summary>
        public static string DefaultPreviewRoot()
        {
            var s_Override = PathOverride ?? Environment.GetEnvironmentVariable("RUE_SETTINGS");
            if (s_Override != null)
                return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(s_Override))!, "preview");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimeUIEditor", "preview");
        }

        /// <summary>ruffle.exe next to the editor, or in a "ruffle" folder beside it or beside the Rime tree — the places a user drops it; empty when none.</summary>
        public static string DetectRufflePath()
        {
            var s_Base = AppContext.BaseDirectory.TrimEnd('\\', '/');
            foreach (var s_Candidate in new[] { Path.Combine(s_Base, "ruffle.exe"), Path.Combine(s_Base, "ruffle", "ruffle.exe"), Path.Combine(s_Base, "..", "..", "..", "..", "..", "ruffle", "ruffle.exe") })
            {
                try { if (File.Exists(s_Candidate)) return Path.GetFullPath(s_Candidate); } catch { }
            }
            return "";
        }

        /// <summary>The VU server's Admin\Mods under Documents when it exists, else a folder of our own there.</summary>
        public static string DetectModsPath()
        {
            var s_Docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var s_Vu = Path.Combine(s_Docs, "Battlefield 3", "Server", "Admin", "Mods");
            return Directory.Exists(s_Vu) ? s_Vu : Path.Combine(s_Docs, "RimeUIEditor", "Mods");
        }
    }
}
