using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace RimeLib.Cmd.UiBuilder
{
    /// <summary>
    /// BF3's localized strings: a UITextDatabase asset (Localization/&lt;lang&gt;_loc) is an EA::Localizer lang pack —
    /// a table of (hash, offset) pairs sorted by hash over NUL-terminated "packed" strings, plus a character table
    /// (the "histogram" chunk) that unpacks bytes >= 0x80 into code points. The hash is the engine's
    /// EA::Localizer::LocalizerManager::StringHash: h = 0xFFFFFFFF; h = h * 33 + byte, over the ID's bytes.
    /// Verified against the client (StringHash 0x146D2A0, UnpackString 0x146DFA0) and 436/439 of the IDs the UI
    /// flow uses.
    /// </summary>
    public class TextDatabase
    {
        readonly Dictionary<uint, string> m_Strings = new();

        public int Count => m_Strings.Count;
        public string Language { get; private set; } = "";

        public static uint Hash(string p_Id)
        {
            var h = 0xFFFFFFFFu;
            foreach (var c in Encoding.UTF8.GetBytes(p_Id))
                h = unchecked(h * 33 + c);
            return h;
        }

        /// <summary>The string for an ID (e.g. "ID_M_YES"), or null when the database has no such entry.</summary>
        public string? Lookup(string p_Id) => m_Strings.TryGetValue(Hash(p_Id), out var s) ? s : null;

        /// <summary>IDs look like ID_xxx; anything else is shown as it is.</summary>
        public string Resolve(string p_Text) =>
            p_Text.StartsWith("ID_", StringComparison.Ordinal) ? Lookup(p_Text) ?? p_Text : p_Text;

        public IEnumerable<KeyValuePair<uint, string>> All => m_Strings;

        /// <summary>
        /// Loads a language ("us", "es", ...) through the service (mounted game or cache): the partition
        /// localization/&lt;lang&gt;_loc names the two chunks.
        /// </summary>
        public static TextDatabase Load(RimeUiService p_Rime, string p_Lang)
        {
            var s_Json = p_Rime.PartitionJson("localization/" + p_Lang + "_loc");
            string? s_Binary = null, s_Histogram = null;
            foreach (var p in s_Json.Descendants().OfType<JProperty>())
            {
                if (p.Name == "BinaryChunk") s_Binary = ChunkGuid(p.Value);
                if (p.Name == "HistogramChunk") s_Histogram = ChunkGuid(p.Value);
            }
            if (s_Binary == null || s_Histogram == null) throw new Exception("localization/" + p_Lang + "_loc has no BinaryChunk/HistogramChunk");
            var s_Db = Parse(p_Rime.ChunkBytes(s_Binary), p_Rime.ChunkBytes(s_Histogram));
            s_Db.Language = p_Lang;
            return s_Db;
        }

        static string? ChunkGuid(JToken p_Value)
        {
            if (p_Value.Type == JTokenType.String) return (string?)p_Value;
            if (p_Value is JObject o)
                foreach (var k in new[] { "Guid", "guid", "Id", "ChunkId" })
                    if (o[k]?.Type == JTokenType.String) return (string?)o[k];
            return null;
        }

        public static TextDatabase Parse(byte[] p_Binary, byte[] p_Histogram)
        {
            var s_Db = new TextDatabase();
            if (BitConverter.ToUInt32(p_Histogram, 0) != 0x39001) throw new Exception("histogram chunk: bad magic");
            var s_TableCount = (int)BitConverter.ToUInt32(p_Histogram, 8);
            var s_Table = new ushort[s_TableCount];
            for (var i = 0; i < s_TableCount && 12 + 2 * i + 1 < p_Histogram.Length; ++i)
                s_Table[i] = BitConverter.ToUInt16(p_Histogram, 12 + 2 * i);

            if (BitConverter.ToUInt32(p_Binary, 0) != 0x39000) throw new Exception("text database chunk: bad magic");
            var s_Count = (int)BitConverter.ToUInt32(p_Binary, 8);
            var s_Header = (int)BitConverter.ToUInt32(p_Binary, 12);
            var s_DataOff = (int)BitConverter.ToUInt32(p_Binary, 16) + 8;
            for (var i = 0; i < s_Count; ++i)
            {
                var s_Hash = BitConverter.ToUInt32(p_Binary, s_Header + 8 * i);
                var s_Off = (int)BitConverter.ToUInt32(p_Binary, s_Header + 8 * i + 4);
                var s_Start = s_DataOff + s_Off;
                if (s_Start >= p_Binary.Length) continue;
                var s_End = Array.IndexOf(p_Binary, (byte)0, s_Start);
                if (s_End < 0) s_End = p_Binary.Length;
                s_Db.m_Strings[s_Hash] = Unpack(p_Binary, s_Start, s_End, s_Table);
            }
            return s_Db;
        }

        static string Unpack(byte[] p_Data, int p_Start, int p_End, ushort[] p_Table)
        {
            var s_Sb = new StringBuilder(p_End - p_Start);
            for (var i = p_Start; i < p_End; ++i)
            {
                var c = p_Data[i];
                if (c < 0x80) { s_Sb.Append((char)c); continue; }
                var v = c < p_Table.Length ? p_Table[c] : (ushort)0;
                if (v >= 0x80) { s_Sb.Append((char)v); continue; }
                if (v == 0 || i + 1 >= p_End) { s_Sb.Append('_'); continue; }
                var n = p_Data[++i];
                var s_Index = 128 * v + (n - 0x80);
                s_Sb.Append(s_Index < p_Table.Length && p_Table[s_Index] != 0 ? (char)p_Table[s_Index] : '_');
            }
            return s_Sb.ToString();
        }
    }
}
