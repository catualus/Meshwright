using System.Linq;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That the sampling flood cannot reach ground it has no way of walking to.
    ///
    /// This is the pass that decides where a mesh may exist at all, and it had no test of any kind. The
    /// symptom that prompted these was mesh generated inside sealed world geometry - areas on the nodraw
    /// top of a brush under a deck, with drop connections punching down through the deck into them - and
    /// the cause was that the flood judged a candidate entirely on the destination column: is there a
    /// floor, is it in reach, is there room to stand, is another floor of the same column in between.
    /// Nothing looked at the space between the two samples, so a wall thinner than the 25 unit sampling
    /// step had both of its sides in adjacent columns and nothing at all in the middle, and the flood
    /// walked through it into whatever it was holding back.
    /// </summary>
    public class SamplingLeakTests
    {
        private const int Solid = 0x1;

        /// <summary>Where the sampling grid is anchored, so a column index maps to a known coordinate.</summary>
        private static readonly BspFile.Vector3 Mins = new(0, 0, -500);
        private static readonly BspFile.Vector3 Maxs = new(1000, 1000, 500);

        /// <summary>
        /// Open ground at z = 0 with a wall standing on it, ten units thick, between two sampling
        /// columns and touching neither.
        ///
        /// The wall spans y = 35..45. Columns fall at y = 0, 25, 50, so it sits wholly between the
        /// second and the third and there is no sample inside it - which is the point. A test with the
        /// wall on a sample column proves nothing: that case was always rejected, by the headroom check
        /// on the destination.
        /// </summary>
        /// <param name="groundNodraw">
        /// Whether the ground's top face is one the renderer skips. The geometry is otherwise identical,
        /// so the two readings differ by the flag alone.
        /// </param>
        private static BspVisibility World(bool groundNodraw)
        {
            const int SurfNodraw = 0x0080;

            var planes = new[]
            {
                Plane(0, 0, 1, 0),        // 0: the ground's top face, and the split above/below it
                Plane(0, 1, 0, 35),       // 1: wall's near face, as a splitting plane
                Plane(0, 1, 0, 45),       // 2: wall's far face, as a splitting plane
                Plane(0, 0, -1, 500),     // 3: ground's underside, z >= -500
                Plane(-1, 0, 0, 1000),    // 4: x >= -1000
                Plane(1, 0, 0, 1000),     // 5: x <= 1000
                Plane(0, -1, 0, 1000),    // 6: y >= -1000
                Plane(0, 1, 0, 1000),     // 7: y <= 1000
                Plane(0, -1, 0, -35),     // 8: wall side, y >= 35
                Plane(0, 0, -1, 0),       // 9: wall underside, z >= 0
                Plane(0, 0, 1, 200),      // 10: wall top, z <= 200
            };

            // Leaf n is referenced as -(n) - 1.
            var nodes = new[]
            {
                new BspVisibility.Node { PlaneNum = 0, Child0 = 1,  Child1 = -1 }, // above / below the ground
                new BspVisibility.Node { PlaneNum = 1, Child0 = 2,  Child1 = -2 }, // past / before the wall
                new BspVisibility.Node { PlaneNum = 2, Child0 = -3, Child1 = -4 }, // past the wall / inside it
            };

            var leafs = new[]
            {
                new BspVisibility.Leaf { Contents = Solid }, // 0: the ground
                new BspVisibility.Leaf { Contents = 0 },     // 1: open air, near side
                new BspVisibility.Leaf { Contents = 0 },     // 2: open air, far side
                new BspVisibility.Leaf { Contents = Solid }, // 3: the wall
            };

            var brushes = new[]
            {
                new BspFile.Brush { FirstSide = 0, NumSides = 6, Contents = Solid },  // ground
                new BspFile.Brush { FirstSide = 6, NumSides = 6, Contents = Solid },  // wall
            };

            // Texinfo 0 is an ordinary drawn face, 1 is nodraw. Only the ground's top face is ever
            // pointed at 1, and only when the caller asks for it.
            var texInfos = new[]
            {
                new BspFile.TexInfo { Flags = 0 },
                new BspFile.TexInfo { Flags = SurfNodraw },
            };

            short groundTop = (short)(groundNodraw ? 1 : 0);

            var sides = new[]
            {
                Side(0, groundTop), Side(3, 0), Side(4, 0), Side(5, 0), Side(6, 0), Side(7, 0),
                Side(8, 0), Side(2, 0), Side(9, 0), Side(10, 0), Side(4, 0), Side(5, 0),
            };

            var leafBrushes = new int[]?[] { new[] { 0 }, null, null, new[] { 1 } };

            return BspVisibility.FromGeometry(planes, nodes, leafs, brushes, sides, leafBrushes, texInfos);
        }

        private static BspFile.Plane Plane(float x, float y, float z, float distance)
            => new() { Normal = new BspFile.Vector3(x, y, z), Distance = distance };

        private static BspFile.BrushSide Side(int plane, short texInfo)
            => new() { PlaneNum = (ushort)plane, TexInfo = texInfo };

        /// <summary>
        /// The control. Two columns of open floor with nothing between them stay connected, so a
        /// refusal below is the wall being seen rather than the sampler having stopped working.
        /// </summary>
        [Fact]
        public void OpenFloorIsStillSampledAcross()
        {
            var found = AreaGenerator.SampleNeighbours(World(groundNodraw: false), Mins, Maxs, 0, 0, 0f);

            Assert.Contains(found, c => c.Gx == 0 && c.Gy == 1);
        }

        /// <summary>
        /// The regression. A wall standing between two samples and touching neither still has to stop
        /// the flood.
        ///
        /// Both columns have ground at the same height, full standing headroom and no intervening floor
        /// in the destination column, so every test the flood used to apply says yes. Only sweeping a
        /// body along the step says no.
        /// </summary>
        [Fact]
        public void AWallBetweenTwoSamplesStopsTheFlood()
        {
            var found = AreaGenerator.SampleNeighbours(World(groundNodraw: false), Mins, Maxs, 0, 1, 0f);

            Assert.DoesNotContain(found, c => c.Gx == 0 && c.Gy == 2);

            // And the step back the other way, over open ground, is still offered - so this is the wall
            // being refused rather than the cell having no neighbours at all.
            Assert.Contains(found, c => c.Gx == 0 && c.Gy == 0);
        }

        /// <summary>
        /// A raised platform at z = 100 whose edge falls at y = 37.5, so the sampling columns at y = 25
        /// and y = 50 sit cleanly on either side of it with open ground below.
        /// </summary>
        private static BspVisibility Ledge()
        {
            var planes = new[]
            {
                Plane(0, 0, 1, 0),        // 0: the low ground's top face
                Plane(0, 1, 0, 37.5f),    // 1: the platform's edge
                Plane(0, 0, 1, 100),      // 2: the platform's top face
                Plane(0, 0, -1, 500),     // 3: undersides, z >= -500
                Plane(-1, 0, 0, 5000),    // 4: x >= -5000
                Plane(1, 0, 0, 5000),     // 5: x <= 5000
                Plane(0, -1, 0, 5000),    // 6: y >= -5000
                Plane(0, 1, 0, 5000),     // 7: y <= 5000
            };

            var nodes = new[]
            {
                new BspVisibility.Node { PlaneNum = 1, Child0 = 1,  Child1 = 2 },  // past / before the edge
                new BspVisibility.Node { PlaneNum = 0, Child0 = -1, Child1 = -2 }, // low ground
                new BspVisibility.Node { PlaneNum = 2, Child0 = -3, Child1 = -4 }, // the platform
            };

            var leafs = new[]
            {
                new BspVisibility.Leaf { Contents = 0 },     // 0: air over the low ground
                new BspVisibility.Leaf { Contents = Solid }, // 1: the low ground
                new BspVisibility.Leaf { Contents = 0 },     // 2: air over the platform
                new BspVisibility.Leaf { Contents = Solid }, // 3: the platform
            };

            var brushes = new[]
            {
                new BspFile.Brush { FirstSide = 0, NumSides = 6, Contents = Solid },  // low ground
                new BspFile.Brush { FirstSide = 6, NumSides = 6, Contents = Solid },  // platform
            };

            var sides = new[]
            {
                Side(0, 0), Side(3, 0), Side(4, 0), Side(5, 0), Side(6, 0), Side(7, 0),
                Side(2, 0), Side(3, 0), Side(4, 0), Side(5, 0), Side(6, 0), Side(1, 0),
            };

            var leafBrushes = new int[]?[] { null, new[] { 0 }, null, new[] { 1 } };

            return BspVisibility.FromGeometry(planes, nodes, leafs, brushes, sides, leafBrushes);
        }

        /// <summary>
        /// The other control, and the one the sweep most easily gets wrong. Stepping off a ledge is a
        /// legitimate move, and the sweep must not refuse it: it runs flat at the height of the *higher*
        /// surface, through the air above the fall, rather than following the ground down into the face
        /// of the drop.
        /// </summary>
        [Fact]
        public void SteppingOffALedgeIsStillSampled()
        {
            var found = AreaGenerator.SampleNeighbours(Ledge(), Mins, Maxs, 0, 1, 100f);

            Assert.Contains(found, c => c.Gx == 0 && c.Gy == 2 && c.Z == 0f);
        }

        /// <summary>
        /// And the climb back up is still refused, by reach rather than by the sweep - a hundred units
        /// is past a crouch jump. The asymmetry between a fall and a climb is the whole reason the node
        /// links are one-way, and adding an obstruction test must not quietly make them symmetric.
        /// </summary>
        [Fact]
        public void ClimbingBackUpTheLedgeIsStillRefused()
        {
            var found = AreaGenerator.SampleNeighbours(Ledge(), Mins, Maxs, 0, 2, 0f);

            Assert.DoesNotContain(found, c => c.Gy == 1);
        }

        /// <summary>
        /// Ground the renderer skips is not ground. Same geometry, same reach, same headroom; the only
        /// difference is the flag on the face, and a sealed floor is nearly always wearing it.
        /// </summary>
        [Fact]
        public void NodrawGroundIsNotSampled()
        {
            var found = AreaGenerator.SampleNeighbours(World(groundNodraw: true), Mins, Maxs, 0, 0, 0f);

            Assert.Empty(found);
        }
    }
}
