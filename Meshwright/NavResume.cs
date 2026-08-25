using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace Meshwright
{
    /// <summary>
    /// Saves the mesh as it stands after the movement passes, so a later run that only wants to redo
    /// the work downstream of it does not have to rebuild it.
    ///
    /// The pipeline has a natural seam. Everything up to and including the reachability check decides
    /// *what the mesh is* - which areas exist, where their edges are, what connects to what. Everything
    /// after it only annotates that mesh: cover spots, sniper grades, encounters, and the visibility
    /// sets. Nothing in the second half can move an area or add a connection, so the first half's output
    /// is a complete, reusable answer.
    ///
    /// **What this is worth, stated plainly.** The half being cached is roughly a third of a full run;
    /// visibility, which is the other two thirds, is downstream of the seam and always recomputed. So
    /// this is not a way to make a repeated build instant. It is a way to stop paying for area
    /// generation and movement every time you change something that only visibility or spots care
    /// about - a different <c>-maxviewdistance</c>, adding visibility to a run that was made with
    /// <c>-novisibility</c>, turning encounter spots on. The staged <c>build-*</c> commands already
    /// offer the same thing explicitly, and more flexibly; this exists because <c>generate</c> - the one
    /// command, and the one Compile Pal runs - had no equivalent.
    ///
    /// **It fails closed.** Every reason to doubt the cache - a changed BSP, changed options, a
    /// different build of Meshwright, a truncated or unreadable file - regenerates instead. A stale
    /// mesh that loads successfully is the worst outcome this could have, far worse than a slow build,
    /// because it is a valid file that silently does not describe the map. That is why the fingerprint
    /// below errs towards including things rather than towards a smaller key.
    /// </summary>
    public static class NavResume
    {
        /// <summary>'MWRS'.</summary>
        private const uint Magic = 0x5352574D;

        /// <summary>
        /// Bumped whenever the layout of this file changes. Distinct from the build stamp in the
        /// fingerprint: that invalidates caches because the *mesh* would differ, this one because the
        /// file could not be read at all.
        /// </summary>
        private const uint FormatVersion = 1;

        /// <summary>Where the cache for a given map lives.</summary>
        public static string PathFor(string bspPath) => bspPath + ".mwresume";

        /// <summary>
        /// Everything that could change the mesh at the seam, as readable lines.
        ///
        /// Kept as text rather than hashed to a single number so a miss can say *which* input moved.
        /// "The BSP changed" is worth knowing; "cache miss" sends you looking.
        /// </summary>
        public static string Fingerprint(string? bspPath, string? navPath, NavPipeline.Options options)
        {
            var lines = new List<string>
            {
                // Any rebuild of Meshwright invalidates every cache. The module id changes on each
                // compile, which is exactly the granularity wanted: a change to how areas are grown
                // must not be masked by a mesh built before it.
                $"build={typeof(NavResume).Assembly.ManifestModule.ModuleVersionId}",

                $"bsp={Stamp(bspPath)}",

                // The seed mesh matters unless it is about to be discarded.
                $"seed={(options.FromScratch && options.GenerateAreas ? "discarded" : Stamp(navPath))}",

                // Only the options upstream of the seam. -maxviewdistance, the spot switches and the
                // visibility switches are all downstream and deliberately absent - reusing the mesh
                // across those is the entire point.
                $"areas={options.GenerateAreas}",
                $"scratch={options.FromScratch}",
                $"ladders={options.Ladders}",
                $"movement={options.Movement}",
                $"prune={options.PruneUnreachable}",

                // Movement limits change which ledges are climbable, so they change the mesh.
                $"cs={NavConstants.UseCounterStrikeLimits}",

                // Content roots decide which props have collision, and props clip areas.
                $"content={string.Join(";", GameFiles.AdditionalRoots)}",
            };

            return string.Join("\n", lines);
        }

        /// <summary>
        /// A file's identity for caching: length and last-write time, not a hash of its contents.
        ///
        /// Hashing a 237MB BSP on every run to decide whether to skip 4 seconds of work would cost more
        /// than it saves. Length and timestamp is what build systems use for the same reason, and its
        /// failure mode needs stating: a file replaced with a different one of exactly the same length,
        /// carrying a deliberately preserved timestamp, would not be noticed. That does not happen by
        /// accident, and the remedy - delete the cache file, or leave the flag off - is at hand.
        /// </summary>
        private static string Stamp(string? path)
        {
            if (path is null || !File.Exists(path))
                return "none";

            var info = new FileInfo(path);
            return $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }

        /// <summary>
        /// Reads a cached mesh, if there is one and it still applies.
        ///
        /// <paramref name="reason"/> always says what happened, whether or not it worked, because a
        /// resume that silently did not happen looks exactly like one that did - the run simply takes
        /// as long as it always did, and nothing says why.
        /// </summary>
        public static bool TryLoad(string path, string fingerprint, out NavFile nav, out string reason)
        {
            nav = new NavFile();

            if (!File.Exists(path))
            {
                reason = "no cache yet";
                return false;
            }

            try
            {
                using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
                using var r = new BinaryReader(stream, Encoding.UTF8);

                if (r.ReadUInt32() != Magic || r.ReadUInt32() != FormatVersion)
                {
                    reason = "cache written by a different version";
                    return false;
                }

                string stored = r.ReadString();

                if (stored != fingerprint)
                {
                    reason = $"{Changed(stored, fingerprint)} changed";
                    return false;
                }

                nav = NavFile.Read(r);
                reason = "reusing the cached mesh";
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or
                                       EndOfStreamException or ArgumentException)
            {
                // Deliberately broad, and deliberately not fatal. A cache is an optimisation; a
                // damaged one means do the work, never fail the build.
                nav = new NavFile();
                reason = "cache unreadable";
                return false;
            }
        }

        /// <summary>The name of the first fingerprint line that differs, for the miss message.</summary>
        private static string Changed(string stored, string current)
        {
            var was = stored.Split('\n');
            var now = current.Split('\n');

            for (int i = 0; i < Math.Min(was.Length, now.Length); i++)
            {
                if (was[i] == now[i]) continue;

                int eq = now[i].IndexOf('=');
                return eq > 0 ? now[i][..eq] : "inputs";
            }

            return "inputs";
        }

        /// <summary>
        /// Writes the cache, through a temporary file so an interrupted save cannot leave a half-written
        /// one to be read back later. A failure here is reported and otherwise ignored - the mesh in
        /// memory is fine, and a build should not fail because a cache could not be written.
        /// </summary>
        public static bool TrySave(string path, string fingerprint, NavFile nav, out string reason)
        {
            string temporary = path + ".tmp";

            try
            {
                using (var buffer = new MemoryStream())
                {
                    using (var w = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
                    {
                        w.Write(Magic);
                        w.Write(FormatVersion);
                        w.Write(fingerprint);
                        nav.Write(w);
                    }

                    File.WriteAllBytes(temporary, buffer.ToArray());
                }

                File.Move(temporary, path, overwrite: true);

                long bytes = new FileInfo(path).Length;

                reason = bytes >= 1024 * 1024
                    ? $"{bytes / (1024 * 1024f):N1} MB"
                    : $"{bytes / 1024f:N0} KB";
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }

                reason = ex.Message;
                return false;
            }
        }
    }
}
