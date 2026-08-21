using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Meshwright
{
    /// <summary>
    /// A Garry's Mod addon archive, read for its file index only.
    ///
    /// Workshop content is the normal way a Garry's Mod map gets its props, and none of it is loose on
    /// disk: subscribing downloads a .gma into <c>garrysmod/cache/workshop</c> and the game mounts it
    /// as if it were installed. A machine with 652 subscriptions has 652 of these and no
    /// <c>models/</c> directory to show for it, so a map built against workshop props resolves nothing
    /// without reading them.
    ///
    /// The format is simple and entirely sequential: a header, then a run of entries naming every file
    /// with its size, then all the file bodies concatenated in the same order. Nothing stores an offset,
    /// so the position of a body is the sum of every size before it - which is why the whole index has
    /// to be walked even to find one file, and why the result is kept.
    /// </summary>
    public sealed class GmaArchive : IDisposable
    {
        /// <summary>'GMAD'.</summary>
        private static ReadOnlySpan<byte> Magic => "GMAD"u8;

        private readonly record struct Entry(long Offset, long Length);

        private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);

        private string path = string.Empty;
        private FileStream? body;

        public int FileCount => entries.Count;

        public static GmaArchive? TryOpen(string gmaPath)
        {
            try
            {
                var archive = new GmaArchive();
                return archive.Read(gmaPath) ? archive : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (InvalidDataException) { return null; }
        }

        private bool Read(string file)
        {
            using var stream = File.OpenRead(file);
            using var r = new BinaryReader(stream);

            if (stream.Length < 32) return false;

            Span<byte> magic = stackalloc byte[4];
            if (r.Read(magic) != 4 || !magic.SequenceEqual(Magic)) return false;

            byte version = r.ReadByte();
            if (version is < 1 or > 3) return false;

            path = file;

            r.ReadUInt64();                 // steam id of the uploader
            r.ReadUInt64();                 // timestamp

            // Version 2 onwards lists the content types the addon needs, as strings ending with an
            // empty one. Skipped, but it has to be walked or everything after it is misread.
            if (version > 1)
                while (ReadString(r).Length > 0) { }

            ReadString(r);                  // name
            ReadString(r);                  // description
            ReadString(r);                  // author
            r.ReadInt32();                  // addon version

            var found = new List<(string Path, long Size)>();

            while (true)
            {
                if (stream.Position + 4 > stream.Length) return false;

                if (r.ReadUInt32() == 0) break;   // a zero index ends the list

                string name = ReadString(r);
                long size = r.ReadInt64();
                r.ReadUInt32();             // crc

                if (size < 0 || found.Count > 200_000) return false;

                found.Add((name.Replace('\\', '/'), size));
            }

            // Bodies start here, concatenated in index order, so an offset is the sum of what precedes.
            long at = stream.Position;

            foreach (var (name, size) in found)
            {
                if (at + size > stream.Length) break;

                entries[name] = new Entry(at, size);
                at += size;
            }

            return entries.Count > 0;
        }

        private static string ReadString(BinaryReader r)
        {
            var sb = new StringBuilder(64);

            while (true)
            {
                byte b = r.ReadByte();
                if (b == 0) break;
                sb.Append((char)b);
            }

            return sb.ToString();
        }

        public bool TryRead(string relativePath, out byte[] bytes)
        {
            bytes = [];

            string key = relativePath.Replace('\\', '/').TrimStart('/');

            if (!entries.TryGetValue(key, out var entry)) return false;

            try
            {
                body ??= File.OpenRead(path);

                if (entry.Offset + entry.Length > body.Length) return false;

                var buffer = new byte[entry.Length];

                body.Seek(entry.Offset, SeekOrigin.Begin);
                body.ReadExactly(buffer, 0, buffer.Length);

                bytes = buffer;
                return true;
            }
            catch (IOException) { return false; }
        }

        public void Dispose()
        {
            body?.Dispose();
            body = null;
        }
    }
}
