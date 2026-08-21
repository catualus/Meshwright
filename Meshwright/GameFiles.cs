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

        /// <summary>
        /// Paths to the VPKs that exist, and the archives themselves once anything has needed one.
        ///
        /// Opening a VPK means parsing its whole directory - three nested runs of null-terminated
        /// strings covering every file in the archive - and a Garry's Mod install has nine of them.
        /// That measured 133ms, which was the single largest part of loading a map's props, and on any
        /// install where the content is unpacked not one of those directories is ever consulted: all
        /// 169 of rp_downtown_meowy's models resolved from loose files.
        ///
        /// So the paths are collected eagerly, which is a directory listing, and the directories are
        /// parsed only when a lookup has already missed the map's pakfile and every loose root. A
        /// machine that really does need them pays exactly what it paid before, on first use.
        /// </summary>
        private readonly List<string> vpkPaths = [];

        /// <summary>Workshop addon archives, discovered eagerly and indexed on demand.</summary>
        private readonly List<string> gmaPaths = [];

        private List<GmaArchive>? gmas;

        private List<VpkArchive>? vpks;
        private ZipArchive? pak;
        private readonly Dictionary<string, ZipArchiveEntry> pakEntries = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Paths asked for and not found anywhere, so the gap can be reported rather than guessed at.</summary>
        public IReadOnlyCollection<string> Missing => missing.Keys.ToArray();

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> missing =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Guards the two archive readers, which are stateful and are not safe to share.
        ///
        /// Callers load models in parallel, and most of that work - parsing a hull out of the bytes -
        /// genuinely is independent. Fetching the bytes is not always: <see cref="ZipArchive"/> is
        /// documented as unsafe for concurrent use, and a <see cref="VpkArchive"/> seeks and reads
        /// shared file handles, so two threads reading different files from one archive would interleave
        /// each other's seeks and return spliced garbage.
        ///
        /// Deliberately narrow. Loose files are read outside it, because <c>File.ReadAllBytes</c> is safe
        /// concurrently and loose content is the common case - on a normal Garry's Mod install with
        /// addons unpacked, nearly every model resolves before this lock is ever taken. Parsing always
        /// happens outside it, which is where the time goes.
        /// </summary>
        private readonly object archives = new();

        /// <summary>Milliseconds spent parsing VPK directories and reading the map's pakfile.</summary>
        public long VpkMs, PakMs, GmaMs;

        public int RootCount => roots.Count;
        public int VpkCount => vpkPaths.Count;
        public int GmaCount => gmaPaths.Count;
        public int GmasOpened => gmas?.Count ?? 0;
        public int VpkOpened => vpks?.Count ?? 0;
        public int PakfileEntries => pakEntries.Count;

        /// <summary>
        /// Where reads were served from. Worth counting because two of the three sources are behind a
        /// lock and one is not, so the split decides whether loading models in parallel is worth
        /// anything at all on a given map.
        /// </summary>
        public int ReadsFromPakfile;
        public int ReadsFromDisk;
        public int ReadsFromVpk;
        public int ReadsFromGma;

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



            // Subscribed content, which for Garry's Mod is where most third-party props live. The cache


            // directory is the game's own download location; loose .gma files beside the addons are the


            // other place they turn up.


            if (mod is not null)


            {
                files.AddGmasIn(Path.Combine(mod.FullName, "cache", "workshop"));
                files.AddGmasIn(Path.Combine(mod.FullName, "addons"));


                // Steam keeps its own copy of subscribed items, outside the game directory entirely:

                // steamapps/workshop/content/4000, a sibling of the "common" folder the install lives in.

                if (mod.Parent?.Parent?.Parent is { } steamapps)

                    files.AddGmasIn(Path.Combine(steamapps.FullName, "workshop", "content", "4000"));


            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            files.OpenPakfile(bspPath);
            files.PakMs = clock.ElapsedMilliseconds;
            return files;
        }

        private void AddRoot(string path)
        {
            if (Directory.Exists(path) && !roots.Contains(path, StringComparer.OrdinalIgnoreCase))
                roots.Add(path);
        }

        /// <summary>
        /// Collects addon archives from a directory, and from one level of subdirectories beneath it.
        ///
        /// Subscribed content lands in two shapes and both have to be looked at. The game downloads
        /// straight into <c>garrysmod/cache/workshop</c> as loose .gma files, while Steam's own
        /// workshop tree gives every item its own numbered folder with the archive inside. Scanning
        /// only the top level finds 15 of this machine's addons and misses 55.
        /// </summary>
        private void AddGmasIn(string directory)
        {
            try
            {
                if (!Directory.Exists(directory)) return;

                gmaPaths.AddRange(Directory.EnumerateFiles(directory, "*.gma", SearchOption.TopDirectoryOnly));

                foreach (var child in Directory.EnumerateDirectories(directory))
                    gmaPaths.AddRange(Directory.EnumerateFiles(child, "*.gma", SearchOption.TopDirectoryOnly));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void AddVpksIn(string root)
        {
            try
            {
                // Only the directory archives. The numbered ones beside them hold bodies and are opened
                // through the directory that indexes them; treating one as an archive in its own right
                // finds no directory and wastes a file handle.
                vpkPaths.AddRange(Directory.EnumerateFiles(root, "*_dir.vpk", SearchOption.TopDirectoryOnly));
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
                    lock (archives)
                    {
                        using var s = entry.Open();
                        using var ms = new MemoryStream();
                        s.CopyTo(ms);
                        bytes = ms.ToArray();
                    }

                    System.Threading.Interlocked.Increment(ref ReadsFromPakfile);
                    return true;
                }
                catch (InvalidDataException) { }
            }

            foreach (var root in roots)
            {
                string full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(full))
                {
                    try { bytes = File.ReadAllBytes(full); System.Threading.Interlocked.Increment(ref ReadsFromDisk); return true; }
                    catch (IOException) { }
                }
            }

            lock (archives)
            {
                // Addons before the base game, matching how the engine mounts them: a workshop addon
                // that replaces a stock prop is meant to win.
                foreach (var gma in OpenGmas())
                    if (gma.TryRead(path, out bytes)) { System.Threading.Interlocked.Increment(ref ReadsFromGma); return true; }

                foreach (var vpk in OpenVpks())
                    if (vpk.TryRead(path, out bytes)) { System.Threading.Interlocked.Increment(ref ReadsFromVpk); return true; }
            }

            missing[path] = 0;
            return false;
        }

        /// <summary>
        /// The addon archives, indexed on first use.
        ///
        /// Lazily and in parallel for the same reasons as the VPKs, but it matters much more here: a
        /// subscribed Garry's Mod install has hundreds of these - 652 on the machine this was written on
        /// - and indexing one means walking its entire file list, because a .gma stores no offsets and a
        /// body's position is the sum of every size before it.
        /// </summary>
        private List<GmaArchive> OpenGmas()
        {
            if (gmas is not null) return gmas;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var found = new GmaArchive?[gmaPaths.Count];

            System.Threading.Tasks.Parallel.For(0, gmaPaths.Count, NavConcurrency.Options,
                i => found[i] = GmaArchive.TryOpen(gmaPaths[i]));

            var opened = new List<GmaArchive>();

            foreach (var gma in found)
                if (gma is not null) opened.Add(gma);

            GmaMs = clock.ElapsedMilliseconds;
            return gmas = opened;
        }

        /// <summary>
        /// The VPK archives, parsing their directories on first use. Callers hold
        /// <see cref="archives"/>, which is what makes the one-time build safe to do lazily.
        /// </summary>
        private List<VpkArchive> OpenVpks()
        {
            if (vpks is not null) return vpks;

            var clock = System.Diagnostics.Stopwatch.StartNew();

            // Nine independent files, each needing its whole directory parsed. Doing them one at a time
            // was 133ms; they share nothing, so they are parsed together and collected in path order
            // afterwards to keep lookup priority stable.
            //
            // Being lazy alone was not enough here, and it is worth recording why. A map that names ten
            // models nobody has still forces every VPK open, because "not in any VPK" is not a
            // conclusion you can reach without looking - so the laziness only pays on a map where every
            // model resolves earlier, and this one does not.
            var found = new VpkArchive?[vpkPaths.Count];

            System.Threading.Tasks.Parallel.For(0, vpkPaths.Count, NavConcurrency.Options,
                i => found[i] = VpkArchive.TryOpen(vpkPaths[i]));

            var opened = new List<VpkArchive>(vpkPaths.Count);

            foreach (var vpk in found)
                if (vpk is not null) opened.Add(vpk);

            VpkMs = clock.ElapsedMilliseconds;
            return vpks = opened;
        }

        public void Dispose()
        {
            if (vpks is not null)
            {
                foreach (var vpk in vpks) vpk.Dispose();
                vpks = null;
            }


            if (gmas is not null)
            {
                foreach (var gma in gmas) gma.Dispose();
                gmas = null;
            }

            pak?.Dispose();
            pak = null;
        }
    }
}
