using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Meshwright
{
    /// <summary>
    /// A Valve Pak, read for its directory only.
    ///
    /// Stock game content lives in these rather than as loose files, so a static prop that ships with
    /// Half-Life 2 or Counter-Strike is unreachable without one. The directory is parsed up front - it
    /// is small, a few megabytes at most - and file bodies are read on demand, because the bodies live
    /// in separate numbered archives that can run to gigabytes and almost none of them will be wanted.
    ///
    /// Only what a lookup needs is kept: where each file's bytes are. CRCs, preload data, MD5 sections
    /// and the signature block are all skipped, which is why this is a few hundred lines rather than a
    /// general-purpose VPK library.
    /// </summary>
    public sealed class VpkArchive : IDisposable
    {
        private const uint Signature = 0x55aa1234;

        /// <summary>An archive index meaning "the bytes are in the directory file itself".</summary>
        private const ushort InlineArchive = 0x7fff;

        private readonly record struct Entry(ushort Archive, long Offset, int Length, byte[] Preload);

        private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Path prefix of the numbered archives, e.g. ...\garrysmod\garrysmod (no suffix).</summary>
        private string prefix = string.Empty;

        private string directoryPath = string.Empty;
        private long inlineBase;

        private readonly Dictionary<ushort, FileStream> open = [];

        public int FileCount => entries.Count;

        public static VpkArchive? TryOpen(string dirVpkPath)
        {
            try
            {
                var archive = new VpkArchive();
                return archive.Read(dirVpkPath) ? archive : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (InvalidDataException) { return null; }
        }

        private bool Read(string path)
        {
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream);

            if (stream.Length < 12) return false;
            if (r.ReadUInt32() != Signature) return false;

            uint version = r.ReadUInt32();
            uint treeSize = r.ReadUInt32();

            if (version is not (1 or 2)) return false;

            // Version 2 appends four more counts to the header. They describe sections that sit after
            // the file data and hold checksums and a signature; nothing here needs them, but the header
            // is longer, and the offsets below are measured from its end.
            if (version == 2)
            {
                r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32();
            }

            long headerEnd = stream.Position;
            inlineBase = headerEnd + treeSize;

            directoryPath = path;
            prefix = path.EndsWith("_dir.vpk", StringComparison.OrdinalIgnoreCase)
                ? path[..^"_dir.vpk".Length]
                : path[..^".vpk".Length];

            // The tree is three nested runs of null-terminated strings - extension, then directory, then
            // file - each terminated by an empty string. A path is assembled from all three, which is
            // why nothing in the file ever stores a full path.
            while (true)
            {
                string extension = ReadString(r);
                if (extension.Length == 0) break;

                while (true)
                {
                    string folder = ReadString(r);
                    if (folder.Length == 0) break;

                    while (true)
                    {
                        string name = ReadString(r);
                        if (name.Length == 0) break;

                        r.ReadUInt32();                       // CRC
                        ushort preloadBytes = r.ReadUInt16();
                        ushort archiveIndex = r.ReadUInt16();
                        uint entryOffset = r.ReadUInt32();
                        uint entryLength = r.ReadUInt32();
                        r.ReadUInt16();                       // terminator

                        byte[] preload = preloadBytes > 0 ? r.ReadBytes(preloadBytes) : [];

                        string full = folder == " "
                            ? $"{name}.{extension}"
                            : $"{folder}/{name}.{extension}";

                        entries[full] = new Entry(archiveIndex, entryOffset, (int)entryLength, preload);

                        if (stream.Position > headerEnd + treeSize) return entries.Count > 0;
                    }
                }
            }

            return true;
        }

        private static string ReadString(BinaryReader r)
        {
            var sb = new StringBuilder(48);

            while (true)
            {
                byte b = r.ReadByte();
                if (b == 0) break;
                sb.Append((char)b);
            }

            return sb.ToString();
        }

        public bool Contains(string relativePath) => entries.ContainsKey(Normalise(relativePath));

        public bool TryRead(string relativePath, out byte[] bytes)
        {
            bytes = [];

            if (!entries.TryGetValue(Normalise(relativePath), out var entry)) return false;

            // A small file can live entirely in the directory's preload block, with no body at all.
            if (entry.Length == 0) { bytes = entry.Preload; return entry.Preload.Length > 0; }

            try
            {
                var stream = StreamFor(entry.Archive);
                if (stream is null) return false;

                long at = entry.Archive == InlineArchive ? inlineBase + entry.Offset : entry.Offset;

                if (at + entry.Length > stream.Length) return false;

                var body = new byte[entry.Preload.Length + entry.Length];
                entry.Preload.CopyTo(body, 0);

                stream.Seek(at, SeekOrigin.Begin);
                stream.ReadExactly(body, entry.Preload.Length, entry.Length);

                bytes = body;
                return true;
            }
            catch (IOException) { return false; }
        }

        private FileStream? StreamFor(ushort archive)
        {
            if (open.TryGetValue(archive, out var existing)) return existing;

            string path = archive == InlineArchive ? directoryPath : $"{prefix}_{archive:000}.vpk";

            if (!File.Exists(path)) return null;

            var stream = File.OpenRead(path);
            open[archive] = stream;
            return stream;
        }

        private static string Normalise(string path) => path.Replace('\\', '/').TrimStart('/');

        public void Dispose()
        {
            foreach (var stream in open.Values) stream.Dispose();
            open.Clear();
        }
    }
}
