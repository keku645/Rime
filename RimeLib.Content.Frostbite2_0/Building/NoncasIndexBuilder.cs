using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using RimeLib.Content.Frostbite2_0.Frostbite.Bundles;
using RimeLib.Content.Frostbite2_0.Frostbite.Chunks;
using RimeLib.Frostbite.Core;

namespace RimeLib.Content.Frostbite2_0.Building
{
    /// <summary>
    /// Builds a CAS catalogue that INDEXES the game's existing non-cas content where it already lives,
    /// instead of copying it into cas_NN.cas files.
    ///
    /// The idea: a catalogue entry is sha1 -> (file number, offset, size), and the file number is only
    /// a slot in fb::FileSuperBundleManager's cas-file handle array. Nothing requires that slot to hold
    /// a cas_NN.cas — it can hold a buffer for any file the engine can read, including a superbundle
    /// (.sb) that already ships with the game. So every non-cas blob can be given a content address
    /// without duplicating a single byte of payload.
    ///
    /// This class produces two things:
    ///   * the catalogue itself (cas.cat), whose entries carry a synthetic file number, and
    ///   * the mapping from those file numbers to the .sb files on disk, which the runtime must use to
    ///     populate the corresponding handle slots.
    ///
    /// WHAT THE ENGINE DOES WITH THESE ENTRIES (read off retail bf3.exe, imagebase 0x00400000).
    /// NOTE ON NAMING: the function below is UNNAMED in both the retail and the R38 decompiles, and
    /// "cas file handle array" is OUR description of the vector it indexes, not a recovered symbol.
    /// Treat every name here as a label; the addresses and the behaviour are what was actually read.
    ///   * the lookup ends in a single statement at retail 0x004B3400 whose effective form is
    ///     handleVector.begin[fileNumber - 1]  -- 1-BASED, with no bounds check;
    ///   * fileNumber is read as a single byte, so 1..255 are the addressable slots;
    ///   * the entry's offset and size are u32 and the offset-high field is zeroed on this path,
    ///     so a target byte range must live below 4 GiB inside its file;
    ///   * nothing on that path validates that a slot is a real cas file: it is fetched as an opaque
    ///     buffer, seeked and read, with no header, framing, decryption or bounds check.
    /// The independent evidence that this works is not the decompile but an in-game run: 73 blobs
    /// pointed at raw offsets inside a player's own DLC superbundles loaded correctly with no cas
    /// file open at all.
    /// </summary>
    public class NoncasIndexBuilder
    {
        /// <summary>Where a blob's stored bytes physically live: a contiguous window of one file.</summary>
        public readonly struct PhysicalLocation
        {
            public readonly string SuperbundleFilePath;
            public readonly long OffsetInFile;
            public readonly long StoredByteLength;

            public PhysicalLocation(string p_SuperbundleFilePath, long p_OffsetInFile, long p_StoredByteLength)
            {
                SuperbundleFilePath = p_SuperbundleFilePath;
                OffsetInFile = p_OffsetInFile;
                StoredByteLength = p_StoredByteLength;
            }
        }

        private readonly struct PendingEntry
        {
            public readonly string ObjectKind;
            public readonly string ObjectName;
            public readonly PhysicalLocation Location;
            public readonly object Readable;

            public PendingEntry(string p_ObjectKind, string p_ObjectName, PhysicalLocation p_Location,
                object p_Readable)
            {
                ObjectKind = p_ObjectKind;
                ObjectName = p_ObjectName;
                Location = p_Location;
                Readable = p_Readable;
            }
        }

        /// <summary>Raised when Rime's own view of a blob disagrees with the bytes at the computed offset.</summary>
        public class LocationMismatchException : Exception
        {
            public LocationMismatchException(string p_Message) : base(p_Message) { }
        }

        // A catalogue entry's file number is a single byte read as 1-based, so slots 1..255 exist and
        // nothing above 255 is addressable.
        private const uint c_MaxFileNumber = 255;

        // Cross-checking materialises the blob twice. Above this, the assurance is not worth the RAM.
        private const long c_MaxCrossCheckByteLength = 16L * 1024 * 1024;

        private readonly uint m_FirstFileNumber;
        private readonly Dictionary<string, uint> m_FileNumberBySuperbundlePath =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> m_SuperbundlePathByOrder = new();
        private readonly List<PendingEntry> m_PendingEntries = new();
        private readonly Dictionary<string, int> m_RejectionCountsByReason = new();
        private readonly Dictionary<string, int> m_IndexableCountsByKind = new();

        private uint m_NextFileNumber;

        /// <summary>
        /// Collapses the mixed separators that come out of joining a native directory with a
        /// '/'-separated superbundle name, so one file has exactly one identity.
        /// </summary>
        private static string NormalisePath(string p_Path)
        {
            try { return Path.GetFullPath(p_Path); }
            catch { return p_Path.Replace('/', Path.DirectorySeparatorChar); }
        }

        public NoncasIndexBuilder(uint p_FirstFileNumber)
        {
            m_FirstFileNumber = p_FirstFileNumber;
            m_NextFileNumber = p_FirstFileNumber;
        }

        public int PendingEntryCount => m_PendingEntries.Count;
        public int SuperbundleFileCount => m_SuperbundlePathByOrder.Count;
        public IReadOnlyDictionary<string, int> RejectionCountsByReason => m_RejectionCountsByReason;

        /// <summary>
        /// Resolves where a mounted non-cas readable's stored bytes physically live.
        ///
        /// Returns false — with a reason — for anything that cannot be described as one contiguous
        /// window of one file. The important case is a PATCHED bundle: when a bundle exists in both the
        /// base and the patch superbundle, Rime serves it through RimeMultiplexedReader, which assembles
        /// the bytes from two files. Such a blob has no single (file, offset, length) and MUST NOT be
        /// indexed in place — emitting an entry for it would silently point the engine at the wrong
        /// bytes rather than fail.
        /// </summary>
        public static bool TryResolvePhysicalLocation(object p_Readable, out PhysicalLocation p_Location,
            out string p_RejectionReason)
        {
            p_Location = default;
            p_RejectionReason = "";

            switch (p_Readable)
            {
                case EbxEntry s_Ebx:
                    return TryResolveBundleEntry(s_Ebx.ContainedSuperbundle, s_Ebx.ContainedBundle,
                        s_Ebx.SeekOffsetWithinBundle, s_Ebx.PayloadSize, out p_Location, out p_RejectionReason);

                case ResourceEntry s_Resource:
                    return TryResolveBundleEntry(s_Resource.ContainedSuperbundle, s_Resource.ContainedBundle,
                        s_Resource.SeekOffsetWithinBundle, s_Resource.PayloadSize, out p_Location, out p_RejectionReason);

                case BundleChunkEntry s_BundleChunk:
                    return TryResolveBundleEntry(s_BundleChunk.ContainedSuperbundle, s_BundleChunk.ContainedBundle,
                        s_BundleChunk.SeekOffsetWithinBundle, s_BundleChunk.PayloadSize, out p_Location, out p_RejectionReason);

                case SbChunkEntry s_SuperbundleChunk:
                {
                    // A TOC-level chunk is the simplest case: it carries an absolute offset already and
                    // is never multiplexed.
                    var s_Superbundle = s_SuperbundleChunk.ContainedSuperbundle;
                    var s_FilePath = s_SuperbundleChunk.InPatch
                        ? s_Superbundle.PatchPath + ".sb"
                        : s_Superbundle.Path + ".sb";

                    if (s_SuperbundleChunk.InPatch && s_Superbundle.PatchPath == null)
                    {
                        p_RejectionReason = "toc chunk marked in-patch but the superbundle has no patch path";
                        return false;
                    }

                    p_Location = new PhysicalLocation(s_FilePath, s_SuperbundleChunk.Offset, s_SuperbundleChunk.Size);
                    return true;
                }

                case Mounting.InlineReadable:
                    // An inline (idata) payload is embedded in the bundle manifest itself rather than
                    // stored as its own window of the .sb, so it has no standalone (offset, length) to
                    // point a catalogue entry at. It is still non-cas data physically present in the
                    // file; addressing it would mean tracking the manifest's own offset plus the idata
                    // displacement inside it. Inline payloads are typically small headers, so the
                    // payoff is low — but this is the category to revisit if that changes.
                    p_RejectionReason = "inline (idata) payload: embedded in the bundle manifest, no standalone file window";
                    return false;

                default:
                    p_RejectionReason = $"unsupported readable type {p_Readable.GetType().Name}";
                    return false;
            }
        }

        private static bool TryResolveBundleEntry(Frostbite.Sb.SuperbundleEntry p_Superbundle,
            BundleManifest p_Bundle, long p_SeekOffsetWithinBundle, long p_StoredByteLength,
            out PhysicalLocation p_Location, out string p_RejectionReason)
        {
            p_Location = default;
            p_RejectionReason = "";

            // See the class remark: a base bundle that a patch bundle overlays is read through a
            // multiplexer, so its bytes are not contiguous in either file.
            if (p_Bundle.PatchBundle != null && !p_Bundle.InUpdate)
            {
                p_RejectionReason = "patched bundle (bytes multiplexed across base+patch, not contiguous)";
                return false;
            }

            var s_SuperbundleFilePath = p_Superbundle.Path + ".sb";
            if (p_Bundle.InUpdate && p_Superbundle.PatchPath != null)
                s_SuperbundleFilePath = p_Superbundle.PatchPath + ".sb";

            if (p_StoredByteLength <= 0)
            {
                p_RejectionReason = "empty payload";
                return false;
            }

            p_Location = new PhysicalLocation(s_SuperbundleFilePath,
                p_Bundle.ContainedBundle.Offset + p_SeekOffsetWithinBundle, p_StoredByteLength);
            return true;
        }

        /// <summary>
        /// Queues a mounted non-cas readable for indexing. Returns false when it cannot be indexed in
        /// place; the reason is accumulated into <see cref="RejectionCountsByReason"/>.
        /// </summary>
        public bool TryAdd(string p_ObjectKind, string p_ObjectName, object p_Readable)
        {
            if (!TryResolvePhysicalLocation(p_Readable, out var s_Location, out var s_RejectionReason))
            {
                CountRejection(s_RejectionReason);
                return false;
            }

            // Superbundle paths are assembled from a native directory and a '/'-separated superbundle
            // name, so they come out mixed ("D:\Game\Data\Win32/Levels/XP2_Skybar/XP2_Skybar.sb").
            // Normalise before anything groups or emits by path, or the same file reached two ways
            // would be given two different file numbers.
            s_Location = new PhysicalLocation(NormalisePath(s_Location.SuperbundleFilePath),
                s_Location.OffsetInFile, s_Location.StoredByteLength);

            m_IndexableCountsByKind.TryGetValue(p_ObjectKind, out var s_KindCount);
            m_IndexableCountsByKind[p_ObjectKind] = s_KindCount + 1;

            // The catalogue's offset field is u32. A blob past 4 GiB inside its file is unreachable,
            // and silently truncating the offset would point the engine at the wrong bytes.
            if (s_Location.OffsetInFile + s_Location.StoredByteLength > uint.MaxValue)
            {
                CountRejection("beyond the 4 GiB addressable ceiling of a catalogue entry");
                return false;
            }

            m_PendingEntries.Add(new PendingEntry(p_ObjectKind, p_ObjectName, s_Location, p_Readable));
            return true;
        }

        private void CountRejection(string p_Reason)
        {
            m_RejectionCountsByReason.TryGetValue(p_Reason, out var s_Count);
            m_RejectionCountsByReason[p_Reason] = s_Count + 1;
        }

        private bool TryGetOrAssignFileNumber(string p_SuperbundleFilePath, out uint p_FileNumber)
        {
            if (m_FileNumberBySuperbundlePath.TryGetValue(p_SuperbundleFilePath, out p_FileNumber))
                return true;

            if (m_NextFileNumber > c_MaxFileNumber)
            {
                p_FileNumber = 0;
                return false;
            }

            p_FileNumber = m_NextFileNumber++;
            m_FileNumberBySuperbundlePath[p_SuperbundleFilePath] = p_FileNumber;
            m_SuperbundlePathByOrder.Add(p_SuperbundleFilePath);
            return true;
        }

        /// <summary>
        /// Hashes every queued blob straight out of its file, writes cas.cat next to a plain-text map of
        /// file number -> path, and returns a human-readable report.
        ///
        /// The hash is taken from the FILE, not from the mounted reader, on purpose: those are exactly
        /// the bytes the engine will fetch through the entry, so a wrong offset produces a wrong hash
        /// and the entry simply never resolves — it fails closed rather than serving a wrong payload.
        ///
        /// Two checks sit on top of that, and they are NOT of equal strength:
        ///   * against DICE's own manifest sha1, where the entry is uncompressed and therefore carries
        ///     one. This is an INDEPENDENT oracle — it validates the location against the game's data.
        ///     A mismatch means the blob is not what the manifest says, and it is not indexed.
        ///   * against Rime's mounted accessor, for <paramref name="p_CrossCheckSamplesPerFile"/> blobs
        ///     per file. This is a differential check of a re-derivation, not independent proof: it
        ///     establishes that this class's arithmetic agrees with Rime's long-standing reader, which
        ///     catches the transcription errors new code actually makes (dropping the bundle offset,
        ///     using OriginalSize instead of PayloadSize, the wrong base-vs-patch rule). It cannot
        ///     validate Rime's model itself.
        /// </summary>
        public string Build(string p_OutputDirectory, Func<object, byte[]>? p_MountedStoredBytesAccessor,
            int p_CrossCheckSamplesPerFile, Func<Sha1, bool>? p_AlreadyInGameCatalog, TextWriter? p_Log)
        {
            Directory.CreateDirectory(p_OutputDirectory);

            var s_Writer = new CasCatalogWriter(m_FirstFileNumber);
            var s_Stopwatch = System.Diagnostics.Stopwatch.StartNew();

            long s_IndexedEntries = 0, s_IndexedBytes = 0, s_DuplicateHashes = 0;
            long s_SkippedAlreadyInGameCatalog = 0, s_ReadErrors = 0, s_OutOfFileNumbers = 0;
            long s_CrossChecksPassed = 0, s_CrossChecksSkippedForSize = 0;
            long s_DiceHashMatches = 0, s_DiceHashMismatches = 0;
            var s_DiceMismatchSamples = new List<string>();

            // Group by file and walk each file once in ascending offset order: the pass is then a
            // sequential read of every superbundle instead of millions of seeks.
            var s_EntriesByFile = m_PendingEntries
                .GroupBy(p_E => p_E.Location.SuperbundleFilePath, StringComparer.OrdinalIgnoreCase)
                .OrderBy(p_G => p_G.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var s_FileGroup in s_EntriesByFile)
            {
                if (!File.Exists(s_FileGroup.Key))
                {
                    CountRejection("superbundle file missing on disk");
                    continue;
                }

                if (!TryGetOrAssignFileNumber(s_FileGroup.Key, out var s_FileNumber))
                {
                    s_OutOfFileNumbers += s_FileGroup.Count();
                    continue;
                }

                using var s_FileStream = new FileStream(s_FileGroup.Key, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                using var s_Sha1 = SHA1.Create();

                var s_CrossChecksRemaining = p_CrossCheckSamplesPerFile;

                foreach (var s_Entry in s_FileGroup.OrderBy(p_E => p_E.Location.OffsetInFile))
                {
                    byte[] s_StoredBytes;
                    try
                    {
                        s_StoredBytes = ReadWindow(s_FileStream, s_Entry.Location.OffsetInFile,
                            (int)s_Entry.Location.StoredByteLength);
                    }
                    catch
                    {
                        s_ReadErrors++;
                        continue;
                    }

                    var s_Hash = new Sha1(s_Sha1.ComputeHash(s_StoredBytes));

                    if (p_AlreadyInGameCatalog != null && p_AlreadyInGameCatalog(s_Hash))
                    {
                        // Already content-addressed by the game's own catalogue: indexing it again
                        // would add an entry the engine will never consult.
                        s_SkippedAlreadyInGameCatalog++;
                        continue;
                    }

                    // INDEPENDENT ORACLE, and the strongest check here. For an UNCOMPRESSED entry the
                    // manifest carries DICE's own sha1 of those bytes, so comparing it against the hash
                    // we just computed from the file window validates our location against the game's
                    // data rather than against Rime's reader. (GetSha1 returns null when the entry is
                    // compressed, because then the manifest hash covers the decompressed content.)
                    if (s_Entry.Readable is RimeLib.Content.Mounting.IReadableObjectWithHash s_WithHash)
                    {
                        Sha1? s_DiceHash = null;
                        try { s_DiceHash = s_WithHash.GetSha1(); } catch { }
                        if (s_DiceHash != null)
                        {
                            if (s_DiceHash.Equals(s_Hash)) s_DiceHashMatches++;
                            else
                            {
                                s_DiceHashMismatches++;
                                if (s_DiceMismatchSamples.Count < 8)
                                    s_DiceMismatchSamples.Add(
                                        $"{s_Entry.ObjectKind} '{s_Entry.ObjectName}' @{s_Entry.Location.OffsetInFile}" +
                                        $"+{s_Entry.Location.StoredByteLength} in {Path.GetFileName(s_FileGroup.Key)}");
                                continue;   // never index a blob DICE says is something else
                            }
                        }
                    }

                    // Re-reading a blob through the mounted accessor materialises it in full, so a
                    // multi-hundred-MB chunk would cost that much memory for no extra assurance. Cap it.
                    if (s_CrossChecksRemaining > 0 && p_MountedStoredBytesAccessor != null &&
                        s_Entry.Location.StoredByteLength > c_MaxCrossCheckByteLength)
                    {
                        s_CrossChecksSkippedForSize++;
                    }
                    else if (s_CrossChecksRemaining > 0 && p_MountedStoredBytesAccessor != null)
                    {
                        s_CrossChecksRemaining--;
                        var s_MountedBytes = p_MountedStoredBytesAccessor(s_Entry.Readable);

                        // Deliberately fatal. Every hash in this pass is self-consistent by
                        // construction (we hash whatever is at the offset we computed), so a
                        // SYSTEMATIC offset error would produce a catalogue that looks perfect and
                        // resolves to garbage. Comparing against Rime's own mounted view is the only
                        // thing that catches it, and a mismatch means the whole index is wrong — the
                        // caller must see that, not receive a plausible-looking cas.cat.
                        if (s_MountedBytes.Length != s_StoredBytes.Length ||
                            !s_MountedBytes.AsSpan().SequenceEqual(s_StoredBytes))
                        {
                            throw new LocationMismatchException(
                                $"stored bytes for {s_Entry.ObjectKind} '{s_Entry.ObjectName}' do not match " +
                                $"the window at offset {s_Entry.Location.OffsetInFile} " +
                                $"({s_Entry.Location.StoredByteLength} bytes) of {s_FileGroup.Key}: " +
                                $"mounted={s_MountedBytes.Length} bytes, window={s_StoredBytes.Length} bytes. " +
                                "This class's location arithmetic disagrees with Rime's own accessor for " +
                                "this entry type; refusing to emit an index built on it.");
                        }

                        s_CrossChecksPassed++;
                    }

                    if (!s_Writer.AddExternalReference(s_Hash, s_FileNumber, s_Entry.Location.OffsetInFile,
                            s_Entry.Location.StoredByteLength))
                    {
                        s_DuplicateHashes++;
                        continue;
                    }

                    s_IndexedEntries++;
                    s_IndexedBytes += s_Entry.Location.StoredByteLength;
                }
            }

            s_Writer.Write(p_OutputDirectory);
            WriteFileNumberMap(p_OutputDirectory);

            var s_Report = new System.Text.StringBuilder();
            s_Report.AppendLine($"build_noncas_index: indexed={s_IndexedEntries} entries over " +
                                $"{m_SuperbundlePathByOrder.Count} superbundle files, " +
                                $"{s_IndexedBytes / 1024 / 1024} MB addressed WITHOUT copying any payload.");
            s_Report.AppendLine($"  catalogue        : {Path.Combine(p_OutputDirectory, "cas.cat")}");
            s_Report.AppendLine($"  file number map  : {Path.Combine(p_OutputDirectory, "casfilemap.txt")}");
            s_Report.AppendLine($"  duplicate hashes : {s_DuplicateHashes} (first location wins, as the engine does)");
            s_Report.AppendLine($"  already in game cat: {s_SkippedAlreadyInGameCatalog}");
            s_Report.AppendLine($"  vs DICE's own manifest sha1 (independent): {s_DiceHashMatches} match, " +
                                $"{s_DiceHashMismatches} MISMATCH (not indexed)");
            foreach (var s_Sample in s_DiceMismatchSamples)
                s_Report.AppendLine($"      mismatch: {s_Sample}");
            s_Report.AppendLine($"  vs Rime's mounted accessor (agreement of this re-derivation): " +
                                $"{s_CrossChecksPassed} blobs byte-for-byte, {s_CrossChecksSkippedForSize} skipped as too large");
            if (m_IndexableCountsByKind.Count > 0)
                s_Report.AppendLine("  queued by kind   : " + string.Join(", ",
                    m_IndexableCountsByKind.OrderByDescending(p_K => p_K.Value)
                        .Select(p_K => $"{p_K.Key}={p_K.Value}")));
            if (s_ReadErrors > 0) s_Report.AppendLine($"  READ ERRORS      : {s_ReadErrors}");
            if (s_OutOfFileNumbers > 0)
                s_Report.AppendLine($"  DROPPED (no file numbers left, ceiling is {c_MaxFileNumber}): {s_OutOfFileNumbers}");
            foreach (var s_Rejection in m_RejectionCountsByReason.OrderByDescending(p_R => p_R.Value))
                s_Report.AppendLine($"  not indexable    : {s_Rejection.Value,8}  {s_Rejection.Key}");
            s_Report.AppendLine($"  elapsed          : {s_Stopwatch.Elapsed.TotalSeconds:F0}s");
            s_Report.AppendLine();
            s_Report.AppendLine("  fileNumber -> file on disk   (the runtime must open each of these and put");
            s_Report.AppendLine("  the buffer in the cas-file handle slot at index fileNumber - 1: 1-BASED)");
            foreach (var s_Path in m_SuperbundlePathByOrder)
                s_Report.AppendLine($"    {m_FileNumberBySuperbundlePath[s_Path],4}  {s_Path}");

            var s_Text = s_Report.ToString();
            p_Log?.Write(s_Text);
            return s_Text;
        }

        private void WriteFileNumberMap(string p_OutputDirectory)
        {
            var s_MapPath = Path.Combine(p_OutputDirectory, "casfilemap.txt");
            using var s_MapWriter = new StreamWriter(s_MapPath, false, System.Text.Encoding.UTF8);
            s_MapWriter.WriteLine("# fileNumber<TAB>path");
            s_MapWriter.WriteLine("# The engine indexes its cas-file handle vector at [fileNumber - 1]:");
            s_MapWriter.WriteLine("# 1-BASED, read as a single byte, so 1..255 are the addressable slots.");
            s_MapWriter.WriteLine("# Open each file read-only and place its buffer in the matching slot.");
            foreach (var s_Path in m_SuperbundlePathByOrder)
                s_MapWriter.WriteLine($"{m_FileNumberBySuperbundlePath[s_Path]}\t{s_Path}");
        }

        private static byte[] ReadWindow(FileStream p_Stream, long p_Offset, int p_Length)
        {
            p_Stream.Seek(p_Offset, SeekOrigin.Begin);
            var s_Buffer = new byte[p_Length];
            var s_Read = 0;
            while (s_Read < p_Length)
            {
                var s_Chunk = p_Stream.Read(s_Buffer, s_Read, p_Length - s_Read);
                if (s_Chunk <= 0)
                    throw new EndOfStreamException(
                        $"wanted {p_Length} bytes at {p_Offset} but the file ended after {s_Read}");
                s_Read += s_Chunk;
            }
            return s_Buffer;
        }
    }
}
