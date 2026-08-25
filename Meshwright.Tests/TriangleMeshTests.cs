using System;
using System.Runtime.Intrinsics;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That the eight-at-a-time trace answers exactly what a plain loop over the same triangles would.
    ///
    /// The wide path only runs when the hardware has 256-bit vectors and a leaf holds a full eight
    /// triangles, so a mistake in it - a lane mask the wrong way round, a tie broken differently, a
    /// tail of fewer than eight skipped - would not show up on a small fixture and would not throw. It
    /// would move a floor sample by a few units on one map and be found, if at all, as a mesh that sits
    /// slightly wrong somewhere.
    ///
    /// So this checks against an independent implementation rather than against itself: the expected
    /// answer below is worked out by a separate Moller-Trumbore written in the test, over every
    /// triangle, with no BVH and no vectors.
    /// </summary>
    public class TriangleMeshTests
    {
        private const int Contents = 0x1;

        /// <summary>
        /// A field of triangles at assorted angles and depths, deliberately not a plane: coincident or
        /// parallel geometry would let a broken nearest-hit search look right by accident.
        /// </summary>
        private static (BspFile.Vector3[] Vertices, int[] Contents) Field(int count, int seed)
        {
            var random = new Random(seed);
            var vertices = new BspFile.Vector3[count * 3];
            var contents = new int[count];

            BspFile.Vector3 Point() => new(
                (float)(random.NextDouble() * 200 - 100),
                (float)(random.NextDouble() * 200 - 100),
                (float)(random.NextDouble() * 200 - 100));

            for (int i = 0; i < count; i++)
            {
                var a = Point();

                // Spread from a common corner, so the triangles have real area rather than slivers.
                vertices[i * 3] = a;
                vertices[i * 3 + 1] = new BspFile.Vector3(
                    a.X + (float)(random.NextDouble() * 60 - 30),
                    a.Y + (float)(random.NextDouble() * 60 - 30),
                    a.Z + (float)(random.NextDouble() * 60 - 30));
                vertices[i * 3 + 2] = new BspFile.Vector3(
                    a.X + (float)(random.NextDouble() * 60 - 30),
                    a.Y + (float)(random.NextDouble() * 60 - 30),
                    a.Z + (float)(random.NextDouble() * 60 - 30));

                contents[i] = Contents;
            }

            return (vertices, contents);
        }

        /// <summary>Moller-Trumbore, written out again so the comparison is genuinely independent.</summary>
        private static bool Nearest(BspFile.Vector3[] v, int[] contents, int mask,
            BspFile.Vector3 a, BspFile.Vector3 b, out float fraction)
        {
            const float Epsilon = 1e-6f;
            fraction = 1f;
            bool found = false;

            float dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;

            for (int i = 0; i < contents.Length; i++)
            {
                if ((contents[i] & mask) == 0) continue;

                var v0 = v[i * 3];
                var v1 = v[i * 3 + 1];
                var v2 = v[i * 3 + 2];

                float e1x = v1.X - v0.X, e1y = v1.Y - v0.Y, e1z = v1.Z - v0.Z;
                float e2x = v2.X - v0.X, e2y = v2.Y - v0.Y, e2z = v2.Z - v0.Z;

                float px = dy * e2z - dz * e2y;
                float py = dz * e2x - dx * e2z;
                float pz = dx * e2y - dy * e2x;

                float det = e1x * px + e1y * py + e1z * pz;
                if (MathF.Abs(det) < Epsilon) continue;

                float inverse = 1f / det;

                float tx = a.X - v0.X, ty = a.Y - v0.Y, tz = a.Z - v0.Z;

                float u = (tx * px + ty * py + tz * pz) * inverse;
                if (u < 0f || u > 1f) continue;

                float qx = ty * e1z - tz * e1y;
                float qy = tz * e1x - tx * e1z;
                float qz = tx * e1y - ty * e1x;

                float vv = (dx * qx + dy * qy + dz * qz) * inverse;
                if (vv < 0f || u + vv > 1f) continue;

                float t = (e2x * qx + e2y * qy + e2z * qz) * inverse;
                if (t <= Epsilon || t >= 1f) continue;

                if (found && t >= fraction) continue;

                fraction = t;
                found = true;
            }

            return found;
        }

        /// <summary>
        /// Counts chosen around the vector width, so both paths and the boundary between them are
        /// exercised: fewer than eight goes scalar, eight or more goes wide, and the larger counts
        /// build a tree several leaves deep.
        ///
        /// What this does *not* reach is the wide loop's leftover handling, and that is worth saying
        /// rather than implying. A leaf holds at most eight triangles and a vector is exactly eight, so
        /// a leaf never has a remainder; deleting that code passes every test here. It is insurance
        /// against a future leaf size, not something under test.
        ///
        /// It also cannot distinguish the epsilon on the near end of the segment from zero - a hit
        /// closer than a millionth of the way along simply does not occur in random geometry.
        /// </summary>
        [Theory]
        [InlineData(3)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        [InlineData(15)]
        [InlineData(16)]
        [InlineData(64)]
        [InlineData(257)]
        public void TheNearestHitMatchesAPlainLoop(int triangles)
        {
            var (vertices, contents) = Field(triangles, seed: triangles);

            var mesh = new TriangleMesh();
            mesh.Build(vertices, contents);

            var random = new Random(99);
            int agreed = 0, hits = 0;

            for (int ray = 0; ray < 3000; ray++)
            {
                BspFile.Vector3 End() => new(
                    (float)(random.NextDouble() * 300 - 150),
                    (float)(random.NextDouble() * 300 - 150),
                    (float)(random.NextDouble() * 300 - 150));

                var a = End();
                var b = End();

                bool wanted = Nearest(vertices, contents, Contents, a, b, out float expected);
                bool got = mesh.TryTraceSurface(a, b, Contents, out float actual, out _);

                Assert.Equal(wanted, got);

                if (wanted)
                {
                    // Bit-identical: the wide path does the same operations in the same order, so a
                    // tolerance here would hide exactly the reordering that would matter.
                    Assert.Equal(expected, actual);
                    hits++;
                }

                agreed++;
            }

            Assert.Equal(3000, agreed);

            // A floor rather than a proportion. All this has to establish is that the fixture really
            // does put triangles in the way - how many of three thousand random rays through a
            // 300-unit cube hit three triangles is not a number worth tuning, and a bar set close to
            // what one fixture happens to produce fails the moment a seed changes.
            const int Required = 10;

            Assert.True(hits > Required,
                $"only {hits} of 3,000 rays hit anything: the fixture is not exercising this");
        }

        /// <summary>A triangle the caller did not ask about must not be reported, in either path.</summary>
        [Fact]
        public void ContentsAreFilteredPerTriangle()
        {
            var (vertices, contents) = Field(32, seed: 7);

            for (int i = 0; i < contents.Length; i++)
                contents[i] = i % 2 == 0 ? 0x1 : 0x2;

            var mesh = new TriangleMesh();
            mesh.Build(vertices, contents);

            var random = new Random(5);
            int seen = 0;

            for (int ray = 0; ray < 2000; ray++)
            {
                BspFile.Vector3 End() => new(
                    (float)(random.NextDouble() * 300 - 150),
                    (float)(random.NextDouble() * 300 - 150),
                    (float)(random.NextDouble() * 300 - 150));

                var a = End();
                var b = End();

                bool wanted = Nearest(vertices, contents, 0x2, a, b, out float expected);
                bool got = mesh.TryTraceSurface(a, b, 0x2, out float actual, out _);

                Assert.Equal(wanted, got);
                if (wanted) { Assert.Equal(expected, actual); seen++; }
            }

            Assert.True(seen > 20, "the masked half was never hit: the filter is untested");
        }

        [Fact]
        public void AnEmptyMeshHitsNothing()
        {
            var mesh = new TriangleMesh();
            mesh.Build([], []);

            Assert.False(mesh.TryTraceSurface(new BspFile.Vector3(0, 0, 0),
                new BspFile.Vector3(1, 1, 1), Contents, out _, out _));
        }

        /// <summary>
        /// Records what the machine running the suite can do. A green run on hardware without 256-bit
        /// vectors has only exercised the scalar half, and that is worth knowing rather than assuming.
        /// </summary>
        [Fact]
        public void TheWidePathIsReachableOnThisMachine()
        {
            Assert.True(Vector256.IsHardwareAccelerated,
                "no 256-bit vectors here, so the wide trace was never executed by these tests");
        }
    }
}
