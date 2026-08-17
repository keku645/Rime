using System;
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

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RimeShaderEditor", "settings.json");

    public static EditorSettings Load()
    {
        try
        {
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

        return this;
    }

    public void Save()
    {
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
