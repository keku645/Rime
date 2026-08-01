using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using RimeLib.Content.Frostbite2_0.Frostbite.Cas;
using RimeLib.Frostbite.Core;
using RimeLib.Frostbite.Fs;
using RimeLib.IO;
using RimeLib.IO.Conversion;

namespace RimeLib.Content.Frostbite2_0.Building
{
    /// <summary>
    /// Writes a Frostbite content-addressable-storage catalogue (cas.cat) and its data
    /// files (cas_NN.cas). Blobs are content-addressed by the SHA1 of the stored bytes and
    /// de-duplicated. This is what was missing for writing NEW data referenced by a (patch)
    /// bundle's sha1 entries — the read side is RimeLib.Content.Frostbite2_0.Frostbite.Cas.Catalog.
    ///
    /// The caller decides whether a blob is stored compressed (Frostbite zlib-block) or raw;
    /// this just stores the exact bytes and hashes them. The catalogue is written with the
    /// FileObfuscation header (magic 0x01CED100, XOR disabled) that BF3's loader expects.
    ///
    /// Retail parity: inside a cas_NN.cas every blob is preceded by a 32-byte header
    /// [magic FA CE 0F F0][sha1 (20 bytes)][size (u64 LE)] and the catalogue entry's offset points
    /// PAST that header. Verified against BF3 retail: on both the base and the patch catalogue every
    /// consecutive entry pair satisfies next.offset == prev.offset + prev.size + 32
    /// (109182/109182 and 31038/31038), with the header's sha1 and size matching the entry.
    /// </summary>
    public class CasCatalogWriter
    {
        // Frostbite caps a single cas file at 1 GiB.
        private const long c_MaxCasSize = 1073741824;

        // Per-blob header written before each stored blob (see the retail-parity note above).
        private const int c_BlobHeaderSize = 32;
        private static readonly byte[] c_BlobMagic = { 0xFA, 0xCE, 0x0F, 0xF0 };

        private readonly uint m_StartIndex;
        private readonly List<MemoryStream> m_CasStreams = new();
        private readonly Dictionary<Sha1, CatalogEntry> m_Entries = new();
        private readonly Catalog m_Catalog = new();

        /// <param name="p_StartIndex">First cas file number (cas_NN.cas). Game uses 01..; a
        /// mod patch should start past the base files (or wherever VU expects).</param>
        public CasCatalogWriter(uint p_StartIndex = 1)
        {
            m_StartIndex = p_StartIndex;
        }

        public IReadOnlyDictionary<Sha1, CatalogEntry> Entries => m_Entries;

        /// <summary>
        /// Stores a blob (exact bytes) and returns its content address (SHA1). De-dups: if the
        /// same bytes were already added, returns the existing hash without storing again.
        /// </summary>
        public Sha1 Add(byte[] p_Data)
        {
            var s_Hash = Sha1.FromData(p_Data);

            if (m_Entries.ContainsKey(s_Hash))
                return s_Hash;

            var s_Stream = FindStreamWithSpace(c_BlobHeaderSize + p_Data.Length, out var s_Index);

            // Header first; the catalogue offset then points at the payload, exactly like retail.
            var s_Header = new byte[c_BlobHeaderSize];
            Buffer.BlockCopy(c_BlobMagic, 0, s_Header, 0, c_BlobMagic.Length);
            Buffer.BlockCopy(s_Hash.Hash, 0, s_Header, 4, 20);
            BinaryPrimitives.WriteUInt64LittleEndian(s_Header.AsSpan(24, 8), (ulong)p_Data.Length);
            s_Stream.Write(s_Header, 0, s_Header.Length);

            m_Entries[s_Hash] = new CatalogEntry(m_Catalog)
            {
                Hash = s_Hash,
                FileNumber = m_StartIndex + s_Index,
                FileOffset = (uint)s_Stream.Position,
                FileSize = (uint)p_Data.Length,
            };

            s_Stream.Write(p_Data, 0, p_Data.Length);

            return s_Hash;
        }

        /// <summary>
        /// Records an entry whose bytes are NOT stored by this writer: they already exist, verbatim,
        /// somewhere the engine can reach. The caller supplies the file number, and is responsible for
        /// having populated that slot in fb::FileSuperBundleManager's cas-file handle array with a
        /// buffer for the corresponding file on disk.
        ///
        /// This is what makes an "index in place" catalogue possible: a superbundle (.sb) that already
        /// ships with the game holds the blob at a known offset, so the catalogue can address it there
        /// instead of duplicating it into a cas_NN.cas.
        ///
        /// IMPORTANT — no blob header. Blobs written by <see cref="Add"/> are preceded by the retail
        /// 32-byte [magic|sha1|size] header and the entry's offset points PAST it. A blob living inside
        /// a .sb has no such header, so <paramref name="p_FileOffset"/> must be the offset of the
        /// payload's FIRST byte. The read path never reads that header (verified in-game: entries
        /// pointing straight at raw .sb offsets load correctly), it exists only for cas files.
        ///
        /// Returns false if a different location was already recorded for the same hash (first wins,
        /// matching the engine's own duplicate handling), true if this call recorded the entry.
        /// </summary>
        public bool AddExternalReference(Sha1 p_Hash, uint p_FileNumber, long p_FileOffset, long p_ByteLength)
        {
            if (m_Entries.ContainsKey(p_Hash))
                return false;

            // The catalogue entry's offset and size fields are both u32: the engine reads the offset
            // into a 32-bit field whose high 24 bits are hard-zeroed on this path, so nothing beyond
            // 4 GiB-1 inside the target file is addressable.
            if (p_FileOffset < 0 || p_FileOffset > uint.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(p_FileOffset),
                    $"offset {p_FileOffset} exceeds the 4 GiB ceiling of a catalogue entry");
            if (p_ByteLength < 0 || p_ByteLength > uint.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(p_ByteLength),
                    $"length {p_ByteLength} exceeds the 4 GiB ceiling of a catalogue entry");
            if (p_FileNumber == 0 || p_FileNumber > 255)
                throw new ArgumentOutOfRangeException(nameof(p_FileNumber),
                    $"file number {p_FileNumber} is outside 1..255 (the engine reads it as a 1-based byte)");

            m_Entries[p_Hash] = new CatalogEntry(m_Catalog)
            {
                Hash = p_Hash,
                FileNumber = p_FileNumber,
                FileOffset = (uint)p_FileOffset,
                FileSize = (uint)p_ByteLength,
            };

            return true;
        }

        /// <summary>
        /// Writes cas_NN.cas files and the obfuscated cas.cat into <paramref name="p_Directory"/>.
        /// Entries recorded through <see cref="AddExternalReference"/> contribute no cas file: if every
        /// entry is external, only cas.cat is written.
        /// </summary>
        public void Write(string p_Directory, string p_CatalogName = "cas.cat")
        {
            Directory.CreateDirectory(p_Directory);

            // Write the data files.
            for (var s_I = 0; s_I < m_CasStreams.Count; ++s_I)
            {
                var s_Path = Path.Combine(p_Directory, $"cas_{m_StartIndex + s_I:D2}.cas");
                using var s_File = new FileStream(s_Path, FileMode.Create, FileAccess.Write);
                m_CasStreams[s_I].WriteTo(s_File);
            }

            // Build the catalogue body: NyanNyan x2 + entries.
            byte[] s_Body;
            using (var s_BodyStream = new MemoryStream())
            {
                using var s_BodyWriter = new RimeWriter(s_BodyStream, Endianness.LittleEndian, false);
                s_BodyWriter.Write(Catalog.c_Nyan);
                s_BodyWriter.Write(Catalog.c_Nyan);

                foreach (var s_Entry in m_Entries.Values)
                    s_Entry.Serialize(s_BodyWriter);

                s_BodyWriter.Flush();
                s_Body = s_BodyStream.ToArray();
            }

            // Wrap with the FileObfuscation header (XOR disabled -> body stored as-is) and write.
            var s_CatalogPath = Path.Combine(p_Directory, p_CatalogName);
            using var s_CatFile = new FileStream(s_CatalogPath, FileMode.Create, FileAccess.Write);
            using var s_CatWriter = new RimeWriter(s_CatFile, Endianness.LittleEndian, false);
            FileObfuscation.Serialize(s_CatWriter, s_Body);
        }

        private MemoryStream FindStreamWithSpace(long p_Length, out uint p_Index)
        {
            for (var s_I = 0u; s_I < m_CasStreams.Count; ++s_I)
            {
                if (m_CasStreams[(int)s_I].Length + p_Length <= c_MaxCasSize)
                {
                    p_Index = s_I;
                    return m_CasStreams[(int)s_I];
                }
            }

            var s_New = new MemoryStream();
            m_CasStreams.Add(s_New);
            p_Index = (uint)(m_CasStreams.Count - 1);
            return s_New;
        }
    }
}
