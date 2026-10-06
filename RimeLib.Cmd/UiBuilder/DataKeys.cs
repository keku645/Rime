using System;
using System.Collections.Generic;
using System.Text;

namespace RimeLib.Cmd.UiBuilder
{
    /// <summary>
    /// The numbers a UIDataSourceInfo.DataKey holds, as the client computes them when it registers a
    /// UIComponentData's DataSources (fb::UIComponentManager::registerComponentDataKeys): the string
    /// "UI_&lt;component file name&gt;_&lt;source name&gt;" upper-cased and hashed with the engine's djb2-xor
    /// (seed 5381, h = (h * 33) ^ c). Verified against 1011 of the 1054 keys the shipped UI graphs bind (the
    /// rest are 0 or name sources the component no longer lists) and the constants the ActionScript passes to
    /// UIDataInterfaceComp.getData.
    /// </summary>
    public static class DataKeys
    {
        /// <summary>The engine's string hash (djb2 with xor), as a signed 32-bit number the EBX stores.</summary>
        public static int FbHash(string p_Text)
        {
            var h = 5381u;
            foreach (var c in Encoding.UTF8.GetBytes(p_Text))
                h = unchecked((h * 33) ^ c);
            return unchecked((int)h);
        }

        /// <summary>"UI/UIComponents/UICustomizationComp" → "UICustomizationComp" (fb::extractFileNameWithoutExt).</summary>
        public static string ComponentShortName(string p_ComponentName)
        {
            var s = p_ComponentName.Replace('\\', '/');
            var s_Slash = s.LastIndexOf('/');
            if (s_Slash >= 0) s = s[(s_Slash + 1)..];
            var s_Dot = s.LastIndexOf('.');
            return s_Dot > 0 ? s[..s_Dot] : s;
        }

        /// <summary>The key of a data source of a component (either the component's full name or its file name).</summary>
        public static int Compute(string p_ComponentName, string p_SourceName) =>
            FbHash(("UI_" + ComponentShortName(p_ComponentName) + "_" + p_SourceName).ToUpperInvariant());

        /// <summary>Key → source name for every source of a component.</summary>
        public static Dictionary<int, string> Table(string p_ComponentName, IEnumerable<string> p_Sources)
        {
            var d = new Dictionary<int, string>();
            foreach (var s in p_Sources) d[Compute(p_ComponentName, s)] = s;
            return d;
        }
    }
}
