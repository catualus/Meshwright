using System;
using System.IO;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That a run can tell its own earlier output from a mesh somebody authored, and says so only then.
    ///
    /// The two mistakes are not equally bad. Missing its own output leaves the run as quiet as it was
    /// before this existed. Mistaking an authored mesh for its own output would warn about somebody's
    /// work and tell them to throw it away, so most of these are about what must not be recognised.
    /// </summary>
    public class NavProvenanceTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"mw_provenance_{Guid.NewGuid():N}");

        private string Store => Path.Combine(root, "store");

        private static readonly DateTime Earlier = new(2026, 11, 1, 14, 30, 0, DateTimeKind.Utc);

        public NavProvenanceTests() => Directory.CreateDirectory(root);

        public void Dispose()
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }

        /// <summary>A small but real mesh written to <paramref name="name"/> in the test folder.</summary>
        private string Mesh(string name = "map.nav", uint bspSize = 1000, float width = 50)
        {
            var nav = new NavFile { BspSize = bspSize };
            var area = new NavArea { Id = 1 };
            area.SeCorner[0] = width;
            area.SeCorner[1] = 50;
            nav.Areas.Add(area);

            string path = Path.Combine(root, name);
            nav.Save(path);
            return path;
        }

        /// <summary>Overwrites four bytes in place, the way Compile Pal's NavStamp re-stamps the BSP size.</summary>
        private static void Patch(string path, int offset, uint value)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
            file.Seek(offset, SeekOrigin.Begin);
            file.Write(BitConverter.GetBytes(value));
        }

        [Fact]
        public void AMeshThisToolWroteIsRecognisedWithItsDate()
        {
            string nav = Mesh();

            Assert.True(NavProvenance.Record(nav, Earlier, Store));
            Assert.True(NavProvenance.TryRecognise(nav, out var written, Store));
            Assert.Equal(Earlier, written);
        }

        /// <summary>
        /// The case the masking exists for. Packing grows the BSP after the mesh is written and Compile
        /// Pal patches the new size into the .nav in place; the mesh itself has not changed.
        /// </summary>
        [Fact]
        public void ReStampingTheBspSizeDoesNotHideIt()
        {
            string nav = Mesh(bspSize: 1000);
            NavProvenance.Record(nav, Earlier, Store);

            Patch(nav, 12, 987_654);

            Assert.Equal(987_654u, NavFile.Load(nav).BspSize);
            Assert.True(NavProvenance.TryRecognise(nav, out _, Store));
        }

        /// <summary>
        /// A mesh saved again in game is somebody's work now, however small the change. Recognising it
        /// would tell them to start from scratch and lose it.
        /// </summary>
        [Fact]
        public void AMeshChangedSinceIsNotRecognised()
        {
            string nav = Mesh(width: 50);
            NavProvenance.Record(nav, Earlier, Store);

            Mesh(width: 75);

            Assert.False(NavProvenance.TryRecognise(nav, out _, Store));
        }

        /// <summary>Only the four size bytes are masked; a change one field later still counts.</summary>
        [Fact]
        public void AChangeJustPastTheSizeStillCounts()
        {
            string nav = Mesh();
            NavProvenance.Record(nav, Earlier, Store);

            // The analysed flag, the byte straight after the size in a version 16 header.
            using (var file = new FileStream(nav, FileMode.Open, FileAccess.ReadWrite))
            {
                file.Seek(16, SeekOrigin.Begin);
                file.WriteByte(1);
            }

            Assert.False(NavProvenance.TryRecognise(nav, out _, Store));
        }

        /// <summary>
        /// Older meshes have no subversion, so the size sits four bytes earlier. Masking it in the
        /// wrong place would let a re-stamp hide the file and let a real change at offset 12 through.
        /// </summary>
        [Fact]
        public void APreVersionTenHeaderIsMaskedWhereItsSizeIs()
        {
            string nav = Path.Combine(root, "old.nav");

            var bytes = new byte[32];
            BitConverter.GetBytes(NavFile.Magic).CopyTo(bytes, 0);
            BitConverter.GetBytes(9u).CopyTo(bytes, 4);
            BitConverter.GetBytes(1000u).CopyTo(bytes, 8);
            File.WriteAllBytes(nav, bytes);

            NavProvenance.Record(nav, Earlier, Store);

            Patch(nav, 8, 2000);
            Assert.True(NavProvenance.TryRecognise(nav, out _, Store));

            Patch(nav, 12, 1);
            Assert.False(NavProvenance.TryRecognise(nav, out _, Store));
        }

        /// <summary>Keyed by path: the same bytes somewhere else were not written there by this tool.</summary>
        [Fact]
        public void TheSameMeshAtAnotherPathIsNotRecognised()
        {
            string nav = Mesh();
            NavProvenance.Record(nav, Earlier, Store);

            string copy = Path.Combine(root, "copy.nav");
            File.Copy(nav, copy);

            Assert.False(NavProvenance.TryRecognise(copy, out _, Store));
        }

        [Fact]
        public void NothingRecordedIsNotRecognised()
        {
            string nav = Mesh();

            Assert.False(NavProvenance.TryRecognise(nav, out _, Store));
        }

        /// <summary>A later write replaces the record, so the date reported is the latest run's.</summary>
        [Fact]
        public void RecordingAgainReplacesTheEarlierRecord()
        {
            string nav = Mesh(width: 50);
            NavProvenance.Record(nav, Earlier, Store);

            Mesh(width: 75);
            var later = Earlier.AddDays(1);
            NavProvenance.Record(nav, later, Store);

            Assert.True(NavProvenance.TryRecognise(nav, out var written, Store));
            Assert.Equal(later, written);
        }

        /// <summary>A file that is not a mesh is neither recorded nor recognised.</summary>
        [Fact]
        public void SomethingThatIsNotAMeshIsLeftAlone()
        {
            string notNav = Path.Combine(root, "map.nav");
            File.WriteAllText(notNav, "not a mesh");

            Assert.False(NavProvenance.Record(notNav, Earlier, Store));
            Assert.False(NavProvenance.TryRecognise(notNav, out _, Store));
        }

        private static NavPipeline.Options Seeded(bool generate, bool scratch) => new()
        {
            GenerateAreas = generate,
            FromScratch = scratch,
            SeedFromEarlierRun = Earlier,
        };

        /// <summary>
        /// The Compile Pal case: "Generate the mesh" ticked, the last compile's output as the seed. The
        /// warning names when that was and the option that rebuilds it, under both of its names.
        /// </summary>
        [Fact]
        public void BuildingOnItsOwnOutputWarnsAndNamesStartFromScratch()
        {
            string? warning = NavPipeline.EarlierRunWarning(Seeded(generate: true, scratch: false));

            Assert.NotNull(warning);
            Assert.Contains(NavProvenance.Describe(Earlier), warning);
            Assert.Contains("\"Start from scratch\"", warning);
            Assert.Contains("-scratch", warning);
        }

        /// <summary>
        /// Only finishing the mesh still carries the earlier run's links and ladders forward, but
        /// "Start from scratch" does nothing on its own, so the warning has to say what does.
        /// </summary>
        [Fact]
        public void FinishingItsOwnOutputPointsAtBothOptions()
        {
            string? warning = NavPipeline.EarlierRunWarning(Seeded(generate: false, scratch: false));

            Assert.NotNull(warning);
            Assert.Contains("-generateareas -scratch", warning);
        }

        /// <summary>A seed about to be discarded carries nothing forward, so there is nothing to say.</summary>
        [Fact]
        public void StartingFromScratchIsNotWarnedAbout()
        {
            Assert.Null(NavPipeline.EarlierRunWarning(Seeded(generate: true, scratch: true)));
        }

        /// <summary>"Start from scratch" without generation is ignored, so the seed is kept after all.</summary>
        [Fact]
        public void ScratchWithoutGenerationStillWarns()
        {
            Assert.NotNull(NavPipeline.EarlierRunWarning(Seeded(generate: false, scratch: true)));
        }

        [Fact]
        public void AnAuthoredSeedIsNotWarnedAbout()
        {
            Assert.Null(NavPipeline.EarlierRunWarning(new NavPipeline.Options { GenerateAreas = true }));
        }
    }
}
