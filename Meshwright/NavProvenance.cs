using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Meshwright
{
    /// <summary>
    /// Remembers the .nav files this tool has written, so a later run can tell when the mesh it is
    /// about to build on is its own earlier output rather than something somebody authored.
    ///
    /// A run treats whatever it is handed as authored. Areas below the first id it creates are never
    /// clipped, connections are only ever added, and a ladder is only rebuilt while its brush is still
    /// there. Compile Pal finishes the mesh in place, so with "Generate the mesh" ticked the next
    /// compile is handed the last one's output, and once the map has changed in between, everything
    /// that compile made is carried forward unchecked: areas floating over a floor that was lowered,
    /// links through a wall that was added, a ladder to a roof that is gone. Dropping that output
    /// automatically is not the answer, because a mesh touched up in game since is somebody's work and
    /// looks exactly the same to the pipeline. Recognising the untouched case is what lets the run say
    /// so, and only then.
    ///
    /// Recognised by content rather than by size and time. Compile Pal re-stamps the BSP size recorded
    /// in the .nav once packing has changed the map, patching those four bytes in place, which changes
    /// the file's time and its hash without changing the mesh. So the hash is taken with that field
    /// zeroed. Any other change - a nav_save in game, a regenerate, another mesh copied over it -
    /// changes the hash, and the file counts as authored again.
    ///
    /// Kept in a small per-user store rather than beside the map, so nothing extra lands in the maps
    /// folder or ships with it: one short text file per mesh path ever written, named by a hash of
    /// that path.
    /// </summary>
    public static class NavProvenance
    {
        /// <summary>
        /// Where records are kept unless a caller says otherwise: <c>%LOCALAPPDATA%\Meshwright\written</c>.
        /// Null when the platform has no such folder, in which case nothing is recorded or recognised
        /// - falling back to a relative path would scatter records into whatever directory the run
        /// happened to start in.
        /// </summary>
        public static string? DefaultStore
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return local.Length == 0 ? null : Path.Combine(local, "Meshwright", "written");
            }
        }

        /// <summary>
        /// Notes that this tool has just written the mesh at <paramref name="navPath"/>.
        ///
        /// Best effort. A record that cannot be written costs only the warning on the next run, which
        /// is not worth failing a finished build over, so this reports failure rather than throwing.
        /// </summary>
        /// <param name="store">The directory records are kept in; null for <see cref="DefaultStore"/>.</param>
        public static bool Record(string navPath, DateTime writtenUtc, string? store = null)
        {
            store ??= DefaultStore;
            if (store is null)
                return false;

            string? temporary = null;

            try
            {
                string full = Path.GetFullPath(navPath);

                if (ContentHash(full) is not { } hash)
                    return false;

                Directory.CreateDirectory(store);

                // Through a temporary file, so two compiles finishing at once cannot leave a record
                // that is half one and half the other.
                string entry = EntryPath(store, full);
                temporary = $"{entry}.{Guid.NewGuid():N}.tmp";

                File.WriteAllText(temporary,
                    $"path={full}\n" +
                    $"hash={hash}\n" +
                    $"written={writtenUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)}\n");

                File.Move(temporary, entry, overwrite: true);
                temporary = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
            finally
            {
                if (temporary is not null)
                {
                    try { File.Delete(temporary); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }

        /// <summary>
        /// Whether the mesh at <paramref name="navPath"/> is exactly what this tool last wrote there,
        /// apart from a re-stamped BSP size, and if so when it was written.
        ///
        /// False whenever there is any doubt - no record, a record for another file, a mesh changed
        /// since, a store that cannot be read - because the cost of a false yes is a warning about
        /// somebody's own work, and the cost of a false no is only the warning not appearing.
        /// </summary>
        /// <param name="store">The directory records are kept in; null for <see cref="DefaultStore"/>.</param>
        public static bool TryRecognise(string navPath, out DateTime writtenUtc, string? store = null)
        {
            writtenUtc = default;

            store ??= DefaultStore;
            if (store is null)
                return false;

            try
            {
                string full = Path.GetFullPath(navPath);
                string entry = EntryPath(store, full);

                if (!File.Exists(entry) || !File.Exists(full))
                    return false;

                string? path = null, hash = null, written = null;

                foreach (string line in File.ReadAllLines(entry))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;

                    string value = line[(eq + 1)..];

                    switch (line[..eq])
                    {
                        case "path": path = value; break;
                        case "hash": hash = value; break;
                        case "written": written = value; break;
                    }
                }

                // The file name is a hash of the path, so the path inside it is what actually proves
                // this record is about this mesh.
                if (path is null || !string.Equals(path, full, PathComparison) || hash is null ||
                    !DateTime.TryParse(written, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                        out var when))
                {
                    return false;
                }

                if (!string.Equals(ContentHash(full), hash, StringComparison.Ordinal))
                    return false;

                writtenUtc = when.ToUniversalTime();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// A recorded time as the person reading the log would write it: their local time, to the
        /// minute, in a form no culture rearranges. A custom format's ':' is the culture's time
        /// separator unless the culture is pinned, hence the invariant one.
        /// </summary>
        public static string Describe(DateTime writtenUtc)
            => writtenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        /// <summary>
        /// Paths are compared the way the file system compares them. On Windows, and under Wine, which
        /// reports itself as Windows, "Maps\x.nav" and "maps\X.NAV" are the same mesh.
        /// </summary>
        private static StringComparison PathComparison =>
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private static string EntryPath(string store, string fullPath)
        {
            string key = OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return Path.Combine(store, Convert.ToHexString(digest, 0, 16) + ".txt");
        }

        /// <summary>
        /// A hash of the mesh's bytes with the stored BSP size zeroed, or null if the file is not a
        /// mesh at all.
        ///
        /// The size follows the magic and version, and the subversion from version 10 on - the order
        /// <see cref="NavFile.Read"/> reads them in - so it sits at offset 12 in any file this tool
        /// writes and at offset 8 in an older one. Compile Pal only patches it from version 10 on, but
        /// masking it in both places costs nothing and keeps the two layouts alike.
        /// </summary>
        private static string? ContentHash(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);

            if (bytes.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != NavFile.Magic)
                return null;

            uint version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
            int sizeOffset = version >= 10 ? 12 : 8;

            if (bytes.Length < sizeOffset + 4)
                return null;

            bytes.AsSpan(sizeOffset, 4).Clear();
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
    }
}
