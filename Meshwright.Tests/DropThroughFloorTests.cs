using System.Linq;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That a drop connection is never recorded from an area to one sitting underneath it.
    ///
    /// Two areas whose footprints overlap in plan view are not neighbours, they are storeys, and the
    /// crossing between them is through a floor. <c>SharedEdge</c> compared the two facing edges with an
    /// absolute difference, which is symmetric, so a candidate lying up to a full <c>EdgeGap</c> back
    /// underneath the area being left read as abutting it. The crossing then tested runs backwards, and
    /// every clearance test in the pass answers a question about the open air above the upper area
    /// rather than about the floor in between - <c>HasGroundBetween</c> in particular finds that floor
    /// and approves of it, since all it asks is that the ground is not *below* the lower area.
    ///
    /// Exercised through <see cref="ConnectionBuilder.Explain"/>, which runs the real tests in the real
    /// order and says which one refused.
    /// </summary>
    public class DropThroughFloorTests
    {
        private const int Solid = 0x1;

        /// <summary>
        /// Open air over solid ground at z = 0. No obstruction anywhere above it, so nothing here can
        /// refuse a crossing on the strength of geometry in the way - which is the point. What is being
        /// tested is the shape of the pair, not the world between them.
        /// </summary>
        private static BspVisibility Ground()
        {
            var planes = new[]
            {
                Plane(0, 0, 1, 0),        // 0: the ground's top face
                Plane(0, 0, -1, 500),     // 1: underside, z >= -500
                Plane(-1, 0, 0, 5000),    // 2: x >= -5000
                Plane(1, 0, 0, 5000),     // 3: x <= 5000
                Plane(0, -1, 0, 5000),    // 4: y >= -5000
                Plane(0, 1, 0, 5000),     // 5: y <= 5000
            };

            var nodes = new[]
            {
                new BspVisibility.Node { PlaneNum = 0, Child0 = -2, Child1 = -1 },
            };

            var leafs = new[]
            {
                new BspVisibility.Leaf { Contents = Solid }, // 0: the ground
                new BspVisibility.Leaf { Contents = 0 },     // 1: everything above it
            };

            var brushes = new[] { new BspFile.Brush { FirstSide = 0, NumSides = 6, Contents = Solid } };
            var sides = new[] { Side(0), Side(1), Side(2), Side(3), Side(4), Side(5) };
            var leafBrushes = new int[]?[] { new[] { 0 }, null };

            return BspVisibility.FromGeometry(planes, nodes, leafs, brushes, sides, leafBrushes);
        }

        private static BspFile.Plane Plane(float x, float y, float z, float distance)
            => new() { Normal = new BspFile.Vector3(x, y, z), Distance = distance };

        private static BspFile.BrushSide Side(int plane) => new() { PlaneNum = (ushort)plane };

        /// <summary>A flat area spanning the given box at one height.</summary>
        private static NavArea Area(uint id, float minX, float minY, float maxX, float maxY, float z)
        {
            var area = new NavArea { Id = id };

            area.NwCorner[0] = minX; area.NwCorner[1] = minY; area.NwCorner[2] = z;
            area.SeCorner[0] = maxX; area.SeCorner[1] = maxY; area.SeCorner[2] = z;
            area.NeZ = z;
            area.SwZ = z;

            return area;
        }

        /// <summary>
        /// The control. A ledge at z = 200 with ground beginning where it ends is a drop a walker can
        /// take, and it is still found - so a refusal below is the overlap being rejected rather than
        /// drops having stopped working.
        /// </summary>
        [Fact]
        public void ADropOffTheEndOfALedgeIsStillFound()
        {
            var upper = Area(1, 0, 0, 500, 200, 200f);
            var lower = Area(2, 500, 0, 1000, 200, 0f);

            var log = ConnectionBuilder.Explain(Ground(), upper, lower);

            Assert.Contains(log, line => line.StartsWith("east:") && line.Contains("would connect"));
        }

        /// <summary>
        /// The regression. The same ledge, with the lower area running thirty units back underneath it,
        /// is not a drop at all - the only way from one to the other is through the ledge.
        /// </summary>
        [Fact]
        public void ADropIntoAnAreaUnderneathIsRefused()
        {
            var upper = Area(1, 0, 0, 500, 200, 200f);
            var lower = Area(2, 470, 0, 1000, 200, 0f);

            var log = ConnectionBuilder.Explain(Ground(), upper, lower);

            Assert.DoesNotContain(log, line => line.Contains("would connect"));
            Assert.Contains(log, line => line.StartsWith("east:") && line.Contains("do not face each other"));
        }

        /// <summary>
        /// The overlap small enough to survive the edge test still has to be refused by the fall test,
        /// which is the guard that was silently degenerating.
        ///
        /// A five unit overlap leaves a run of seven units between the two probe points, and the sweep
        /// down the fall wants its first sample clear of the edge being stepped off - eight units along.
        /// That used to clamp, collapsing all three samples onto the landing point, which is precisely
        /// the single column in the wrong place the sweep was written to replace. The run being too
        /// short to sample is not a licence to sample once; it means the landing is not past the edge,
        /// and there is no fall.
        /// </summary>
        [Fact]
        public void AFallWithNoRoomToLeaveTheEdgeIsRefused()
        {
            var upper = Area(1, 0, 0, 500, 200, 200f);
            var lower = Area(2, 495, 0, 1000, 200, 0f);

            var log = ConnectionBuilder.Explain(Ground(), upper, lower);

            Assert.DoesNotContain(log, line => line.Contains("would connect"));
            Assert.Contains(log, line => line.StartsWith("east:") && line.Contains("REFUSED by Fall"));
        }
    }
}
