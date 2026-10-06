using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace RimeLib.Cmd.UiBuilder
{
    /// <summary>
    /// What each widget's own code reads at initialise — measured from the widgets' scripts once and shipped as
    /// Data/widget_settings.json: the class the movie registers for its symbol, the WidgetProperties it reads
    /// (initData.&lt;Name&gt;) with their declared defaults and how they are parsed (bool / number / flag / string),
    /// and the data channels it accepts (its update&lt;Name&gt;Data methods = the DataName a binding can address).
    /// </summary>
    public class WidgetSettings
    {
        public class Setting
        {
            [JsonProperty("default")] public string? Default;
            /// <summary>bool ("true"/"false"), number, flag (0/1), string.</summary>
            [JsonProperty("kind")] public string Kind = "string";
            /// <summary>The class that reads it (a base class for inherited settings).</summary>
            [JsonProperty("from")] public string From = "";
        }

        public class Widget
        {
            [JsonProperty("symbol")] public string Symbol = "";
            [JsonProperty("class")] public string Class = "";
            [JsonProperty("chain")] public List<string> Chain = new();
            [JsonProperty("settings")] public Dictionary<string, Setting> Settings = new();
            [JsonProperty("dataNames")] public List<string> DataNames = new();
        }

        [JsonProperty("source")] public string Source = "";
        [JsonProperty("widgets")] public Dictionary<string, Widget> Widgets = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>By movie name ("grid") or asset partition ("ui/assets/grid").</summary>
        public Widget? Get(string p_Asset)
        {
            var s_Name = p_Asset.Replace('\\', '/');
            if (s_Name.StartsWith("ui/assets/", StringComparison.OrdinalIgnoreCase)) s_Name = s_Name["ui/assets/".Length..];
            return Widgets.TryGetValue(s_Name, out var w) ? w : null;
        }

        public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "Data", "widget_settings.json");

        public static WidgetSettings? Load(string? p_Path = null)
        {
            var s_Path = p_Path ?? DefaultPath;
            if (!File.Exists(s_Path)) return null;
            try { return JsonConvert.DeserializeObject<WidgetSettings>(File.ReadAllText(s_Path)); }
            catch { return null; }
        }
    }
}
