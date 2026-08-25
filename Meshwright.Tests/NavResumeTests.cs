using System;
using System.IO;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That the resume cache is used exactly when it still applies, and never when it might not.
    ///
    /// The danger here is one-sided. A cache that misses costs a slow build; a cache that hits when it
    /// should not produces a valid .nav that quietly does not describe the map, which is the failure
    /// this codebase is least able to detect - it round-trips, it loads, and every quality measure
    /// scores it normally. So these tests are mostly about the misses.
    /// </summary>
    public class NavResumeTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"mw_resume_{Guid.NewGuid():N}");

        public NavResumeTests() => Directory.CreateDirectory(root);

        public void Dispose()
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }

        private string File_(string name, string body = "x")
        {
            string path = Path.Combine(root, name);
            System.IO.File.WriteAllText(path, body);
            return path;
        }

        private static NavPipeline.Options Options() => new()
        {
            GenerateAreas = true,
            Movement = true,
            Ladders = true,
        };

        private static NavFile Mesh(uint id = 1)
        {
            var nav = new NavFile();
            var area = new NavArea { Id = id };
            area.SeCorner[0] = 50; area.SeCorner[1] = 50;
            nav.Areas.Add(area);
            return nav;
        }

        [Fact]
        public void AMeshComesBackAsItWentIn()
        {
            string bsp = File_("map.bsp");
            string cache = NavResume.PathFor(bsp);
            string print = NavResume.Fingerprint(bsp, null, Options());

            Assert.True(NavResume.TrySave(cache, print, Mesh(42), out _));
            Assert.True(NavResume.TryLoad(cache, print, out var back, out _));

            Assert.Single(back.Areas);
            Assert.Equal(42u, back.Areas[0].Id);
        }

        [Fact]
        public void NoCacheIsNotAnError()
        {
            Assert.False(NavResume.TryLoad(Path.Combine(root, "absent"), "any", out _, out string why));
            Assert.Contains("no cache", why);
        }

        /// <summary>
        /// The point of the feature: options that only affect passes downstream of the seam must not
        /// invalidate the mesh built upstream of it.
        /// </summary>
        [Fact]
        public void DownstreamOptionsDoNotChangeTheFingerprint()
        {
            string bsp = File_("map.bsp");
            string before = NavResume.Fingerprint(bsp, null, Options());

            var options = Options();
            options.MaxViewDistance = 1234;
            options.Visibility = false;
            options.Spots = false;
            options.SniperSpots = false;
            options.EncounterSpots = false;
            options.CompressVisibility = false;

            Assert.Equal(before, NavResume.Fingerprint(bsp, null, options));
        }

        [Theory]
        [InlineData("areas")]
        [InlineData("ladders")]
        [InlineData("movement")]
        [InlineData("prune")]
        public void UpstreamOptionsDoChangeIt(string which)
        {
            string bsp = File_("map.bsp");
            string before = NavResume.Fingerprint(bsp, null, Options());

            var options = Options();
            switch (which)
            {
                case "areas": options.GenerateAreas = false; break;
                case "ladders": options.Ladders = false; break;
                case "movement": options.Movement = false; break;
                case "prune": options.PruneUnreachable = true; break;
            }

            Assert.NotEqual(before, NavResume.Fingerprint(bsp, null, options));
        }

        [Fact]
        public void ReplacingTheMapInvalidatesIt()
        {
            string bsp = File_("map.bsp", "one");
            string cache = NavResume.PathFor(bsp);
            string print = NavResume.Fingerprint(bsp, null, Options());

            Assert.True(NavResume.TrySave(cache, print, Mesh(), out _));

            System.IO.File.WriteAllText(bsp, "a different map entirely");

            Assert.False(NavResume.TryLoad(cache, NavResume.Fingerprint(bsp, null, Options()),
                out _, out string why));

            Assert.Contains("bsp", why);
        }

        /// <summary>
        /// Movement limits decide which ledges are climbable, so they decide what the mesh is. This one
        /// is easy to leave out of a key because it arrives as a global rather than as an option.
        /// </summary>
        [Fact]
        public void TheMovementLimitsAreInTheFingerprint()
        {
            string bsp = File_("map.bsp");
            bool original = NavConstants.UseCounterStrikeLimits;

            try
            {
                NavConstants.UseCounterStrikeLimits = false;
                string standard = NavResume.Fingerprint(bsp, null, Options());

                NavConstants.UseCounterStrikeLimits = true;
                Assert.NotEqual(standard, NavResume.Fingerprint(bsp, null, Options()));
            }
            finally
            {
                NavConstants.UseCounterStrikeLimits = original;
            }
        }

        /// <summary>Content roots decide which props have collision, and props clip areas.</summary>
        [Fact]
        public void ContentRootsAreInTheFingerprint()
        {
            string bsp = File_("map.bsp");
            var original = GameFiles.AdditionalRoots;

            try
            {
                GameFiles.AdditionalRoots = [];
                string bare = NavResume.Fingerprint(bsp, null, Options());

                GameFiles.AdditionalRoots = [root];
                Assert.NotEqual(bare, NavResume.Fingerprint(bsp, null, Options()));
            }
            finally
            {
                GameFiles.AdditionalRoots = original;
            }
        }

        [Theory]
        [InlineData(0)]      // empty
        [InlineData(8)]      // header only
        [InlineData(40)]     // truncated mid-fingerprint
        public void ADamagedCacheIsIgnoredRatherThanThrown(int keepBytes)
        {
            string bsp = File_("map.bsp");
            string cache = NavResume.PathFor(bsp);
            string print = NavResume.Fingerprint(bsp, null, Options());

            Assert.True(NavResume.TrySave(cache, print, Mesh(), out _));

            var all = System.IO.File.ReadAllBytes(cache);
            System.IO.File.WriteAllBytes(cache, all[..Math.Min(keepBytes, all.Length)]);

            Assert.False(NavResume.TryLoad(cache, print, out _, out _));
        }

        [Fact]
        public void JunkIsIgnoredRatherThanThrown()
        {
            string bsp = File_("map.bsp");
            string cache = NavResume.PathFor(bsp);

            System.IO.File.WriteAllBytes(cache, new byte[512]);

            Assert.False(NavResume.TryLoad(cache, NavResume.Fingerprint(bsp, null, Options()), out _, out _));
        }
    }
}
