using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;

namespace RimeCamoStudio.Catalog;

/// <summary>
/// Where the first-person eye sits on each weapon (keku 2026-09-29: "simular cómo se vería el soldado en 1ª persona con el arma en
/// las manos"): Data/firstperson.json, written by Rime's dump_weapon_camera — the soldier's 1P skeleton posed by the weapon's idle
/// pose, its camera bone (CameraJoint) in the weapon mesh's own space. The eye sits on the weapon's +x, its left, the side the
/// first-person view shows.
/// </summary>
public static class FirstPersonCameras
{
    public readonly record struct Eye(Vector3 Position, Vector3 Look, Vector3 Up);

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "Data", "firstperson.json");

    private static Dictionary<string, Eye>? s_Eyes;

    /// <summary>The eye of a weapon mesh (its first-person body mesh), or null when none was measured.</summary>
    public static Eye? Of(string? p_Mesh)
    {
        if (p_Mesh == null)
            return null;

        s_Eyes ??= Load(DefaultPath);
        return s_Eyes.TryGetValue(p_Mesh, out var s_Eye) ? s_Eye : null;
    }

    private static Dictionary<string, Eye> Load(string p_Path)
    {
        var s_Eyes = new Dictionary<string, Eye>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(p_Path))
            return s_Eyes;

        try
        {
            using var s_Document = JsonDocument.Parse(File.ReadAllText(p_Path));
            if (!s_Document.RootElement.TryGetProperty("Meshes", out var s_Meshes))
                return s_Eyes;

            static Vector3 Triple(JsonElement p_Array) =>
                new(p_Array[0].GetSingle(), p_Array[1].GetSingle(), p_Array[2].GetSingle());

            foreach (var s_Mesh in s_Meshes.EnumerateObject())
                s_Eyes[s_Mesh.Name] = new Eye(Triple(s_Mesh.Value.GetProperty("Eye")), Triple(s_Mesh.Value.GetProperty("Look")),
                    Triple(s_Mesh.Value.GetProperty("Up")));
        }
        catch (Exception)
        {
            // a damaged file is no camera: the button says so
        }

        return s_Eyes;
    }
}
