using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Meshwright
{
    /// <summary>
    /// Finds a game file the way the engine would, so a model named by the map can be opened.
    ///
    /// A static prop names its model as a path relative to the mod directory, and that path can resolve
    /// to four different places. In descending priority:
    ///
    /// 1. The map's own embedded pakfile. A mapper who ships custom models packs them into the .bsp, and
    ///    those override everything - they exist precisely because the content is not installed.
    /// 2. Loose files under the mod directory, and under any addon folder beneath it.
    /// 3. Loose files under the base game directories a mod inherits from.
    /// 4. VPKs, where stock game content lives.
    ///
    /// The order matters and is not arbitrary. Getting it backwards means a map that ships a modified
    /// version of a stock prop silently collides against the stock one.
    ///
    /// **This is best-effort by design.** A missing model is reported and skipped, never fatal. Meshwright
    /// runs on a build machine that may have nothing installed but the map, and a mesh built without
    /// some props is worth more than no mesh at all - so long as it says so, which is what
    /// <see cref="Missing"/> is for.
    /// </summary>
    public sealed class GameFiles : IDisposable
    {
        private const int LumpPakfile = 40;

        private readonly List<string> roots = [];
        private readonly List<VpkArchive> vpks = [];
        private ZipArchive? pak;
        private readonly Dictionary<string, ZipArchiveEntry> pakEntries = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Paths asked for and not found anywhere, so the gap can be reported rather than guessed at.</summary>
        public IReadOnlyCollection<string> Missing => missing;

        private readonly HashSet<string> missing = new(StringComparer.OrdinalIgnoreCase);

        public int RootCount => roots.Count;
        public int VpkCount => vpks.Count;
        public int PakfileEntries => pakEntries.Count;

        /// <summary>
        /// Opens the content a map can see, inferring the install layout from the map's own path.
        ///
        /// A .bsp lives in <c>&lt;mod&gt;/maps</c>, so the mod directory is two levels up and the rest of
        /// the install is its siblings. That inference is the whole reason no configuration is needed
        /// for the ordinary case; <paramref name="extraRoots"/> covers the rest.
        /// </summary>
        public static GameFiles Open(string bspPath, IEnumerable<string>? extraRoots = null)
        {
            var files = new GameFiles();

            var maps = Directory.GetParent(Path.GetFullPath(bspPath));
            var mod = maps?.Parent;

            if (mod is not null)
            {
                files.AddRoot(mod.FullName);

                // Addons are searched as roots in their own right. In Garry's Mod an installed addon is
                // a directory with the same shape as the mod - models/, materials/ - and a map built
                // against one names its props exactly as if they were installed normally.
                string addons = Path.Combine(mod.FullName, "addons");

                if (Directory.Exists(addons))
                {
                    foreach (var dir in Directory.EnumerateDirectories(addons))
                        files.AddRoot(dir);
                }

                // Sibling game directories the mod inherits content from. Named rather than enumerated:
                // a mod folder's siblings include plenty that are not content, and walking all of them
                // turns a lookup into a directory scan of the whole install.
                if (mod.Parent is { } install)
                {
                    foreach (var name in new[] { "sourceengine", "platform", "hl2", "cstrike", "episodic", "ep2", "tf", "portal" })
                    {
                        string path = Path.Combine(install.FullName, name);
                        if (Directory.Exists(path)) files.AddRoot(path);
                    }
                }
            }

            if (extraRoots is not null)
                foreach (var root in extraRoots) files.AddRoot(root);

            foreach (var root in files.roots.ToArray())
                files.AddVpksIn(root);

            files.OpenPakfile(bspPath);
            return files;
        }

        private void AddRoot(string path)
        {
            if (Directory.Exists(path) && !roots.Contains(path, StringComparer.OrdinalIgnoreCase))
                roots.Add(path);
        }

        private void AddVpksIn(string root)
        {
            try
            {
                // Only the directory archives. The numbered ones beside them hold bodies and are opened
                // through the directory that indexes them; treating one as an archive in its own right
                // finds no directory and wastes a file handle.
                foreach (var path in Directory.EnumerateFiles(root, "*_dir.vpk", SearchOption.TopDirectoryOnly))
                {
                    if (VpkArchive.TryOpen(path) is { } vpk) vpks.Add(vpk);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void OpenPakfile(string bspPath)
        {
            try
            {
                using var stream = File.OpenRead(bspPath);
                using var r = new BinaryReader(stream);

                r.BaseStream.Seek(8, SeekOrigin.Begin);

                int offset = 0, length = 0;
                for (int i = 0; i < BspFile.HeaderLumps; i++)
                {
                    int o = r.ReadInt32(), l = r.ReadInt32();
                    r.ReadInt32(); r.ReadInt32();

                    if (i == LumpPakfile) { offset = o; length = l; }
                }

                if (length <= 0) return;

                var bytes = LzmaLump.Read(r, offset, length);

                pak = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

                foreach (var entry in pak.Entries)
                    pakEntries[entry.FullName.Replace('\\', '/')] = entry;
            }
            catch (InvalidDataException) { pak = null; }
            catch (IOException) { pak = null; }
        }

        public bool TryRead(string relativePath, out byte[] bytes)
        {
            string path = relativePath.Replace('\\', '/').TrimStart('/');
            bytes = [];

            if (pakEntries.TryGetValue(path, out var entry))
            {
                try
                {
                    using var s = entry.Open();
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    bytes = ms.ToArray();
                    return true;
                }
                catch (InvalidDataException) { }
            }

            foreach (var root in roots)
            {
                string full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(full))
                {
                    try { bytes = File.ReadAllBytes(full); return true; }
                    catch (IOException) { }
                }
            }

            foreach (var vpk in vpks)
                if (vpk.TryRead(path, out bytes)) return true;

            missing.Add(path);
            return false;
        }

        public void Dispose()
        {
            foreach (var vpk in vpks) vpk.Dispose();
            vpks.Clear();
            pak?.Dispose();
            pak = null;
        }
    }
}
