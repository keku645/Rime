using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace RimeShaderEditor;

/// <summary>
/// Remembers the handful of paths the editor needs so they are typed at most once. Nothing here is baked
/// into the build: the BF3 location is read from the installer's own registry key at runtime, so the editor
/// works on any machine that has the game.
/// </summary>
public class EditorSettings
{
    public string GamePath { get; set; } = "";
    public string OutputFolder { get; set; } = "";

    /// <summary>Multiplier on the graph canvas wheel-zoom step. 1 = the speed the editor always had.</summary>
    public double CanvasZoomSpeed { get; set; } = 1.0;

    /// <summary>Multiplier on the preview panel's wheel dolly step.</summary>
    public double PreviewZoomSpeed { get; set; } = 1.0;

    /// <summary>Multiplier on the preview panel's orbit/pan drag sensitivity.</summary>
    public double PreviewOrbitSpeed { get; set; } = 1.0;

    /// <summary>
    /// PREVIEW-ONLY: when false (default) the engine-fed thermal parameters (FLIRData/FLIRScale) read zero in
    /// the preview, the way the game renders with thermal optics off. When true they read the probe pattern,
    /// which shows the thermal tint path alive. Baking is untouched either way: these live in the material
    /// constant buffer the ENGINE fills at runtime — they are never part of the compiled shader.
    /// </summary>
    public bool FlirPreview { get; set; }

    /// <summary>
    /// When previewing a real game mesh: draw ONLY the sections worn by the shader being edited, hiding
    /// every other material section (default shows them as neutral grey so the object keeps its shape).
    /// </summary>
    public bool PreviewOnlyEditedShader { get; set; }

    /// <summary>
    /// Author with the UDK-era material vocabulary (its node names, ports and properties) instead of this
    /// engine's own. ⛔ PALETTE ONLY: the bake is identical either way — a UDK-style node emits exactly what
    /// its native twin emits, which is what lets someone lay out a graph the way they already think and
    /// still ship this engine's shader. Off by default; nothing is forced.
    /// </summary>
    public bool UdkNodeStyle { get; set; }

    /// <summary>
    /// When true (default), the Texture panel's Export button writes a PNG regardless of the source format —
    /// the ready-to-edit choice. When false it exports the file as it IS: the raw DDS dump for game art.
    /// </summary>
    public bool ExportTexturesAsPng { get; set; } = true;

    /// <summary>Node-placement shortcut overrides, node kind -> key name ("Multiply" -> "Q"). Empty = defaults.</summary>
    public Dictionary<string, string> NodeShortcuts { get; set; } = new();

    /// <summary>Test seams point this at a scratch file so they never touch the user's real settings.</summary>
    internal static string? PathOverride;

    private static string SettingsPath => PathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RimeShaderEditor", "settings.json");

    public static EditorSettings Load()
    {
        try
        {
            // A hidden test window must behave the same on every machine: the user's real preferences (their
            // zoom speed, their remapped keys) must not leak into a harness run — unless the test itself
            // pointed PathOverride at its own scratch file.
            if (MainWindow.Headless && PathOverride == null)
                return new EditorSettings().WithDefaults();

            if (File.Exists(SettingsPath))
            {
                var s_Loaded = JsonSerializer.Deserialize<EditorSettings>(File.ReadAllText(SettingsPath));
                if (s_Loaded != null)
                    return s_Loaded.WithDefaults();
            }
        }
        catch
        {
            // A corrupt settings file must not stop the editor from opening.
        }

        return new EditorSettings().WithDefaults();
    }

    private EditorSettings WithDefaults()
    {
        if (string.IsNullOrWhiteSpace(GamePath))
            GamePath = DetectGamePath() ?? "";

        if (string.IsNullOrWhiteSpace(OutputFolder))
            OutputFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RimeShaderEditor");

        // A hand-edited or corrupt file must not leave the editor unusable (zoom speed 0 = a frozen canvas).
        CanvasZoomSpeed = Clamp(CanvasZoomSpeed);
        PreviewZoomSpeed = Clamp(PreviewZoomSpeed);
        PreviewOrbitSpeed = Clamp(PreviewOrbitSpeed);
        NodeShortcuts ??= new Dictionary<string, string>();

        return this;

        static double Clamp(double p_Value) =>
            double.IsFinite(p_Value) ? Math.Clamp(p_Value, 0.25, 4.0) : 1.0;
    }

    public void Save()
    {
        // The hidden windows the test seams drive must never write the user's real file.
        if (MainWindow.Headless && PathOverride == null)
            return;

        try
        {
            var s_Directory = Path.GetDirectoryName(SettingsPath);
            if (s_Directory != null)
                Directory.CreateDirectory(s_Directory);

            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
        }
        catch
        {
            // Losing the settings is not worth interrupting the user over.
        }
    }

    /// <summary>Reads the BF3 install directory the retail installer records under EA Games.</summary>
    public static string? DetectGamePath()
    {
        var s_Keys = new[]
        {
            @"SOFTWARE\WOW6432Node\EA Games\Battlefield 3",
            @"SOFTWARE\EA Games\Battlefield 3",
        };

        foreach (var s_Key in s_Keys)
        {
            try
            {
                using var s_RegistryKey = Registry.LocalMachine.OpenSubKey(s_Key);
                if (s_RegistryKey?.GetValue("Install Dir") is not string s_Path)
                    continue;

                s_Path = s_Path.TrimEnd('\\', '/');
                if (Directory.Exists(s_Path))
                    return s_Path;
            }
            catch
            {
                // Unreadable key: fall through to the next candidate.
            }
        }

        return null;
    }
}
