using System;
using System.Collections.Generic;
using System.Linq;
using RimeLib.Cmd.Scaleform;

namespace RimeLib.Cmd.UiBuilder
{
    /// <summary>
    /// The frame actions the stage ops put on a screen's timeline, compiled (External/keku/compile_as.py over the .as next to the editor's
    /// Data folder) and carried here as base64 so the mod build needs no file beside it. AddFrameScript keeps one script per name.
    /// </summary>
    public static class FrameScripts
    {
        /// <summary>Data/TextSize.as: gives a text widget the size its `_rueTextSize` construct variable asks for (see the op textsize).</summary>
        public const string TextSizeBase64 = "iAQBHABydWVSb290Q2xpcAB0aGlzAF9ydWVSb290AHJ1ZVRleHRGaWVsZABtX3RleHRGaWVsZABtX3Jvd1R5cGUAdHh0RGlzcGxheQB0eHQAcnVlQXBwbHlUZXh0U2l6ZQBfcnVlVGV4dFNpemUAaXNOYU4AVGV4dEZvcm1hdABzaXplAHNldE5ld1RleHRGb3JtYXQAc2V0VGV4dEZvcm1hdABtX2FsaWduAGF1dG9TaXplAGNlbnRlcgByaWdodABsZWZ0AHJ1ZU1ha2VUZXh0V3JhcABvcmlnAGNhbGwAawB3AG1vdmllY2xpcAB1cGRhdGVUZXh0RGF0YQBmdW5jdGlvbgCWAgAIAJYCAAgBHJYCAAgCHEtOPJYCAAgDjgsAAAEABCoAAXcAggCWAgAEAZYCAAgETocBAAIXlgIABAKWAQADSRKdAgAXAJYCAAQBlgIABAGWAgAIBU5LTocBAAIXlgIABAKWAQADSRKdAgAFAJYBAAM+lgIABAKWAgAIBk6HAQADF5YCAAQDlgEAA0kSnQIAEACWAgAEApYCAAgHTocBAAMXlgIABAM+PJYCAAgIjgsAAAEAByoBAncAIwGWAgAEApYCAAgJTkqHAQADF5YCAAQDlgUABwEAAACWAgAEAZYCAAgKUkydAgAQABeWAgAEA5YFAAcAAAAAZxISnQIABQCWAQADPpYCAAQClgUABwEAAACWAgAIAz2HAQAEF5YCAAQElgEAA0kSnQIABQCWAQADPpYFAAcAAAAAlgIACAtAhwEABReWAgAEBZYCAAgMlgIABANPlgIABAWWBQAHAQAAAJYCAAQElgIACA1SF5YCAAQFlgUABwEAAACWAgAEBJYCAAgOUheWAgAEApYCAAgPTkuHAQAGF5YCAAQElgIACBCWAgAEBpYCAAgRSRKdAgAKAJYCAAgRmQIAIACWAgAEBpYCAAgSSRKdAgAKAJYCAAgSmQIABQCWAgAIE088lgIACBSOEQAAAgAEKgABdwAAb3JpZwBPAI4OAAABAAQpAAJkYXRhAD0AlgIABAKWAgAEAZYFAAcCAAAAlgIACBUclgIACBZShwEAAxeWAgAEAZYFAAcBAAAAlgIACAg9F5YCAAQDPj48lgIACAAclgEAA0kSEp0CAMEAlgIACAAcVYcBAACWAQACSZ0CAKwAlgIACBeWAgAEADyWAgAIGJYCAAgAHJYCAAgXHE48lgIACBgcRJYCAAgZSUwSnQIAEwAXlgIACBgclgIACAlOlgEAA0kSTBKdAgAUABeWAgAIGByWAgAIGk5ElgIACBtJEp0CAEEAlgIACBgclgIACBqWAgAIGByWAgAIGk6WAgAIGByWBQAHAgAAAJYCAAgUPU+WAgAIGByWBQAHAQAAAJYCAAgIPReZAgBG/wA=";

        public static byte[] TextSize() => Convert.FromBase64String(TextSizeBase64);

        /// <summary>The screen's root clip on the main timeline (instance1 on every shipped screen): what the scripts reach the widgets through.</summary>
        public static string RootClipName(GfxMovie p_Movie) => GfxMovie.FrameOnePlacements(p_Movie.Tags).FirstOrDefault(p => p.Name != null)?.Name ?? "instance1";

        /// <summary>
        /// textsize:&lt;clip&gt;:&lt;px&gt; — the clip's `_rueTextSize` construct variable (0 = the row symbol's own size again) and, once per movie,
        /// the TextSize frame action that applies it after every text the engine hands the widget. The game's text widgets know one size
        /// per row type (bold1 20 px, baseText0 32 px…); this is the editor's way past it, with the game's own vector font.
        /// </summary>
        public static void ApplyTextSize(GfxMovie p_Movie, string p_Clip, string p_Px)
        {
            var s_Px = double.Parse(p_Px, System.Globalization.CultureInfo.InvariantCulture);
            p_Movie.SetClipVars(p_Clip, new List<(string, object?)> { ("_rueTextSize", s_Px <= 0 ? 0 : (object)(int)System.Math.Round(s_Px)) });
            if (!p_Movie.FrameScripts().Any(s => s == "textsize"))
                p_Movie.AddFrameScript("textsize", new[] { ("_rueRoot", RootClipName(p_Movie)) }, TextSize());
        }
    }
}
