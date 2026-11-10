using System;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That a triangle lying exactly where a segment ends counts as hit by that segment, and so by
    /// exactly one of two segments that meet there.
    ///
    /// A triangle used to count only strictly inside a segment, with both ends left out. The floor
    /// search walks a column down in four-unit segments, terrain heights and the heights it starts from
    /// are short binary fractions, and terrain on a boundary between two segments was inside neither:
    /// the column read as having no floor at all. cp_badlands area 210 sits on terrain two units above
    /// its own surface and was reported 82 units afloat. The start of a segment stays out, so a line
    /// traced up from a floor point does not stop on that floor.
    ///
    /// Each test runs over a leaf of two triangles and one of eight, because the nearest-hit search
    /// takes a scalar path below eight and an eight-wide one at eight, and both apply the bounds.
    /// </summary>
    public class SegmentBoundaryTests
    {
        private const int Solid = 0x1;

        /// <summary>Heights to put the terrain at: small, fractional, negative, and large enough for coarse floats.</summary>
        public static TheoryData<float, int> Heights()
        {
            var data = new TheoryData<float, int>();

            foreach (float z in new[] { 2f, 0.5f, -37.25f, 130.5f, 1027.125f, -3000.75f })
            {
                data.Add(z, 1);    // two triangles: the scalar path
                data.Add(z, 4);    // eight triangles: the wide path
            }

            return data;
        }

        /// <summary>
        /// Flat terrain at <paramref name="z"/>, 1000 units square around the origin, laid out as a row
        /// of <paramref name="quads"/> quads of two triangles each.
        /// </summary>
        private static (BspFile.Vector3[] Vertices, int[] Contents) Terrain(float z, int quads)
        {
            var vertices = new BspFile.Vector3[quads * 6];
            var contents = new int[quads * 2];

            float width = 1000f / quads;

            for (int q = 0; q < quads; q++)
            {
                float x0 = -500f + q * width, x1 = x0 + width;

                vertices[q * 6] = new BspFile.Vector3(x0, -500f, z);
                vertices[q * 6 + 1] = new BspFile.Vector3(x1, -500f, z);
                vertices[q * 6 + 2] = new BspFile.Vector3(x1, 500f, z);
                vertices[q * 6 + 3] = new BspFile.Vector3(x0, -500f, z);
                vertices[q * 6 + 4] = new BspFile.Vector3(x1, 500f, z);
                vertices[q * 6 + 5] = new BspFile.Vector3(x0, 500f, z);

                contents[q * 2] = Solid;
                contents[q * 2 + 1] = Solid;
            }

            return (vertices, contents);
        }

        private static TriangleMesh Mesh(float z, int quads)
        {
            var (vertices, contents) = Terrain(z, quads);
            var mesh = new TriangleMesh();
            mesh.Build(vertices, contents);
            return mesh;
        }

        /// <summary>
        /// Nothing in the BSP but a slab far below, and the terrain over all of it - the world
        /// <see cref="AreaClipperTests.TerrainOnASegmentBoundaryIsStillFloor"/> builds.
        /// </summary>
        private static BspVisibility World(float z, int quads)
        {
            var (vertices, contents) = Terrain(z, quads);
            return World(vertices, contents);
        }

        /// <inheritdoc cref="World(float, int)"/>
        private static BspVisibility World(BspFile.Vector3[] vertices, int[] contents)
        {
            var planes = new[] { TestWorlds.Plane(0, 0, 1, -10000f) };
            var nodes = new[] { new BspVisibility.Node { PlaneNum = 0, Child0 = -1, Child1 = -2 } };
            var leafs = new[] { new BspVisibility.Leaf { Contents = 0 }, new BspVisibility.Leaf { Contents = Solid } };
            var vis = BspVisibility.FromGeometry(planes, nodes, leafs, [], [], new int[]?[leafs.Length]);

            vis.AttachDisplacements(BspDisplacements.FromTriangles(vertices, contents));
            return vis;
        }

        private static BspFile.Vector3 At(float z) => new(13f, -7f, z);

        /// <summary>A segment ending exactly on the terrain hits it, at its own end.</summary>
        [Theory]
        [MemberData(nameof(Heights))]
        public void ASegmentEndingOnTerrainHitsIt(float z, int quads)
        {
            var mesh = Mesh(z, quads);

            Assert.True(mesh.Blocks(At(z + 4f), At(z), Solid));
            Assert.True(mesh.TryTraceSurface(At(z + 4f), At(z), Solid, out float fraction, out var normal));

            // Reported at the end point and no further, so a caller placing the hit stays on the segment.
            Assert.Equal(1f, fraction);
            Assert.Equal(1f, normal.Z);
        }

        /// <summary>
        /// A segment starting exactly on the terrain does not, whichever way it goes: a headroom line
        /// traced up from a floor point must not stop on that floor.
        /// </summary>
        [Theory]
        [MemberData(nameof(Heights))]
        public void ASegmentStartingOnTerrainDoesNotHitIt(float z, int quads)
        {
            var mesh = Mesh(z, quads);

            Assert.False(mesh.Blocks(At(z), At(z + 4f), Solid));
            Assert.False(mesh.Blocks(At(z), At(z - 4f), Solid));
            Assert.False(mesh.TryTraceSurface(At(z), At(z + 72f), Solid, out _, out _));
        }

        /// <summary>
        /// Two segments meeting on the terrain see it exactly once between them, and a column walked
        /// down in four-unit pieces from anywhere a whole number of pieces above it sees it once.
        /// </summary>
        [Theory]
        [MemberData(nameof(Heights))]
        public void SegmentsMeetingOnTerrainHitItExactlyOnce(float z, int quads)
        {
            var mesh = Mesh(z, quads);

            for (int pieces = 1; pieces <= 8; pieces++)
            {
                float top = z + 4f * pieces;
                int hits = 0;

                for (int k = 0; k < pieces + 2; k++)
                {
                    if (mesh.Blocks(At(top - 4f * k), At(top - 4f * (k + 1)), Solid))
                        hits++;
                }

                Assert.Equal(1, hits);
            }
        }

        /// <summary>
        /// The floor search itself, through the traced world: terrain on a boundary between two of its
        /// segments is found, where it is, from every start a whole number of segments above it.
        /// </summary>
        [Theory]
        [MemberData(nameof(Heights))]
        public void TheFloorSearchFindsTerrainOnASegmentBoundary(float z, int quads)
        {
            var vis = World(z, quads);

            for (int pieces = 1; pieces <= 8; pieces++)
            {
                Assert.True(StairMarker.TryFindFloor(vis, 13f, -7f, z + 4f * pieces, 64f, out float floor),
                    $"no floor found from {pieces} segments above terrain at {z}");

                // The search places the surface from above, to within its last bisection step of 4/128;
                // the bound allows twice that.
                Assert.InRange(floor, z, z + 4f / 64f);
            }
        }

        /// <summary>
        /// The floor search on steep terrain far from the origin, started a whole number of segments
        /// above the surface it is to find, where the surface itself was found by a single trace - as an
        /// area's height is. Rounding on a slope like that places a segment's end on the triangle only to
        /// a few millionths of the segment, so a surface on a boundary could still land just past the end
        /// of one segment and just before the start of the next; rp_downtown_tits_v25 area 16702 had a
        /// sample over open air that way. The segments of the search overlap to cover it.
        /// </summary>
        [Fact]
        public void TheFloorSearchFindsSteepTerrainOnASegmentBoundaryFarFromTheOrigin()
        {
            var random = new Random(16702);
            int columns = 0;
            var misses = new System.Collections.Generic.List<string>();

            for (int surface = 0; surface < 200; surface++)
            {
                // A plane through a centre point, sloping at 35 to 60 degrees in a random direction,
                // cut down to one triangle about 128 units across.
                float cx = 8000f + (float)random.NextDouble() * 4000f;
                float cy = -12000f + (float)random.NextDouble() * 4000f;
                float cz = -600f + (float)random.NextDouble() * 400f;

                float angle = (float)(random.NextDouble() * Math.PI * 2);
                float gradient = MathF.Tan((35f + (float)random.NextDouble() * 25f) * MathF.PI / 180f);
                float gx = MathF.Cos(angle) * gradient, gy = MathF.Sin(angle) * gradient;

                BspFile.Vector3 V(float dx, float dy) => new(cx + dx, cy + dy, cz + gx * dx + gy * dy);

                var vis = World([V(-64f, -64f), V(64f, -64f), V(-64f, 64f)], [Solid]);

                for (int column = 0; column < 20; column++)
                {
                    // Whole and half units, as sample points so often are, inside the triangle.
                    float x = MathF.Round(cx - 48f + (float)random.NextDouble() * 48f) + 0.5f * random.Next(2);
                    float y = MathF.Round(cy - 48f + (float)random.NextDouble() * 48f) + 0.5f * random.Next(2);

                    if (!vis.TryTraceSurface(new BspFile.Vector3(x, y, cz + 1000f), new BspFile.Vector3(x, y, cz - 1000f),
                            BspVisibility.GroundMask, out var hit, out _))
                    {
                        continue;
                    }

                    columns++;

                    for (int pieces = 1; pieces <= 8; pieces++)
                    {
                        float top = hit.Z + 4f * pieces;

                        if (!StairMarker.TryFindFloor(vis, x, y, top, 64f, out float floor) ||
                            MathF.Abs(floor - hit.Z) > 0.1f)
                        {
                            misses.Add($"({x}, {y}) from {top:R}: terrain at {hit.Z:R}");
                        }
                    }
                }
            }

            Assert.True(columns > 1000, $"only {columns} columns crossed the terrain: the fixture is not exercising this");
            Assert.True(misses.Count == 0, $"{misses.Count} searches missed: " + string.Join("; ", misses.GetRange(0, Math.Min(5, misses.Count))));
        }

        /// <summary>
        /// The same two rules through <see cref="BspVisibility"/>, which every caller goes through:
        /// a line ending on terrain is blocked and stops at its end, and one starting on it is clear.
        /// </summary>
        [Theory]
        [MemberData(nameof(Heights))]
        public void TheWorldTraceAgreesAtBothEnds(float z, int quads)
        {
            var vis = World(z, quads);

            Assert.False(vis.IsLineClear(At(z + 4f), At(z), BspVisibility.GroundMask));
            Assert.True(vis.TryTraceSurface(At(z + 4f), At(z), BspVisibility.GroundMask, out var point, out _,
                out bool onDisplacement));
            Assert.Equal(z, point.Z);
            Assert.True(onDisplacement);

            Assert.True(vis.IsLineClear(At(z), At(z + 72f), BspVisibility.GroundMask));
        }
    }
}
