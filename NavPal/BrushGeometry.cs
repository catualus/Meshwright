using System;
using System.Collections.Generic;

namespace NavPal
{
    /// <summary>
    /// Recovers the actual vertices of a Source brush.
    ///
    /// Brushes are stored as convex volumes defined by half-spaces, with no explicit vertex list. For
    /// axis-aligned brushes you can read bounds straight off the six axis planes, but that silently
    /// produces a too-large box for anything angled - a diagonal ladder would report the bounds of the
    /// wall it spans rather than of the ladder itself.
    ///
    /// The general solution is to intersect every combination of three planes and keep the points that
    /// lie inside all the others. That is O(n^3) in side count, which is fine here: brushes have a
    /// handful of sides, and this only runs on candidate ladder brushes.
    /// </summary>
    public static class BrushGeometry
    {
        /// <summary>Tolerance for "point is inside this half-space", in world units.</summary>
        private const float InsideEpsilon = 0.01f;

        /// <summary>Below this the three planes are treated as not forming a corner.</summary>
        private const float DeterminantEpsilon = 1e-6f;

        /// <summary>
        /// Computes the corner points of a brush. Returns an empty list if the brush is degenerate or
        /// unbounded (which happens for brushes whose sides are all bevel planes).
        /// </summary>
        public static List<BspFile.Vector3> GetVertices(BspFile bsp, BspFile.Brush brush)
        {
            var planes = new List<BspFile.Plane>(brush.NumSides);
            for (int i = 0; i < brush.NumSides; i++)
            {
                int index = brush.FirstSide + i;
                if (index < 0 || index >= bsp.BrushSides.Length)
                    continue;

                var side = bsp.BrushSides[index];
                if (side.PlaneNum >= bsp.Planes.Length)
                    continue;

                planes.Add(bsp.Planes[side.PlaneNum]);
            }

            var vertices = new List<BspFile.Vector3>();
            if (planes.Count < 4)
                return vertices;

            for (int i = 0; i < planes.Count - 2; i++)
            for (int j = i + 1; j < planes.Count - 1; j++)
            for (int k = j + 1; k < planes.Count; k++)
            {
                if (!TryIntersect(planes[i], planes[j], planes[k], out var point))
                    continue;

                if (!IsInsideAll(planes, point))
                    continue;

                if (!AlreadyPresent(vertices, point))
                    vertices.Add(point);
            }

            return vertices;
        }

        /// <summary>Bounds derived from real vertices, correct for angled brushes.</summary>
        public static bool TryGetBounds(BspFile bsp, BspFile.Brush brush,
            out BspFile.Vector3 mins, out BspFile.Vector3 maxs)
        {
            mins = default;
            maxs = default;

            var vertices = GetVertices(bsp, brush);
            if (vertices.Count == 0)
                return false;

            mins = new BspFile.Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            maxs = new BspFile.Vector3(float.MinValue, float.MinValue, float.MinValue);

            foreach (var v in vertices)
            {
                mins.X = Math.Min(mins.X, v.X);
                mins.Y = Math.Min(mins.Y, v.Y);
                mins.Z = Math.Min(mins.Z, v.Z);
                maxs.X = Math.Max(maxs.X, v.X);
                maxs.Y = Math.Max(maxs.Y, v.Y);
                maxs.Z = Math.Max(maxs.Z, v.Z);
            }

            return true;
        }

        /// <summary>
        /// The outward normal of the brush's largest mostly-vertical face, which for a ladder is the
        /// face the climber's back is to. Used to derive the ladder's facing direction without assuming
        /// axis alignment. Returns false if no such face exists.
        /// </summary>
        public static bool TryGetDominantHorizontalNormal(BspFile bsp, BspFile.Brush brush,
            out BspFile.Vector3 normal)
        {
            normal = default;

            float bestScore = 0f;
            bool found = false;

            for (int i = 0; i < brush.NumSides; i++)
            {
                int index = brush.FirstSide + i;
                if (index < 0 || index >= bsp.BrushSides.Length)
                    continue;

                var side = bsp.BrushSides[index];
                if (side.Bevel != 0) // bevel planes are collision padding, not real faces
                    continue;
                if (side.PlaneNum >= bsp.Planes.Length)
                    continue;

                var n = bsp.Planes[side.PlaneNum].Normal;

                // near-vertical faces only: a ladder's climbing face is close to plumb
                float horizontal = MathF.Sqrt(n.X * n.X + n.Y * n.Y);
                if (horizontal < 0.7f)
                    continue;

                if (horizontal > bestScore)
                {
                    bestScore = horizontal;
                    normal = n;
                    found = true;
                }
            }

            return found;
        }

        /// <summary>
        /// Converts a horizontal normal into Source's NavDirType: 0 north (-Y), 1 east (+X),
        /// 2 south (+Y), 3 west (-X).
        /// </summary>
        public static uint ToNavDirection(BspFile.Vector3 normal)
        {
            if (MathF.Abs(normal.X) > MathF.Abs(normal.Y))
                return normal.X > 0 ? 1u : 3u;

            return normal.Y > 0 ? 2u : 0u;
        }

        private static bool TryIntersect(BspFile.Plane a, BspFile.Plane b, BspFile.Plane c,
            out BspFile.Vector3 point)
        {
            point = default;

            // Cramer's rule on the 3x3 system of plane equations
            var n1 = a.Normal;
            var n2 = b.Normal;
            var n3 = c.Normal;

            var cross23 = Cross(n2, n3);
            float det = Dot(n1, cross23);

            if (MathF.Abs(det) < DeterminantEpsilon)
                return false; // planes are parallel or share a line

            var cross31 = Cross(n3, n1);
            var cross12 = Cross(n1, n2);

            point = new BspFile.Vector3(
                (a.Distance * cross23.X + b.Distance * cross31.X + c.Distance * cross12.X) / det,
                (a.Distance * cross23.Y + b.Distance * cross31.Y + c.Distance * cross12.Y) / det,
                (a.Distance * cross23.Z + b.Distance * cross31.Z + c.Distance * cross12.Z) / det);

            return true;
        }

        private static bool IsInsideAll(List<BspFile.Plane> planes, BspFile.Vector3 point)
        {
            foreach (var plane in planes)
            {
                if (Dot(plane.Normal, point) - plane.Distance > InsideEpsilon)
                    return false;
            }

            return true;
        }

        private static bool AlreadyPresent(List<BspFile.Vector3> vertices, BspFile.Vector3 point)
        {
            foreach (var v in vertices)
            {
                if (MathF.Abs(v.X - point.X) < 0.1f &&
                    MathF.Abs(v.Y - point.Y) < 0.1f &&
                    MathF.Abs(v.Z - point.Z) < 0.1f)
                    return true;
            }

            return false;
        }

        private static BspFile.Vector3 Cross(BspFile.Vector3 a, BspFile.Vector3 b) => new(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);

        private static float Dot(BspFile.Vector3 a, BspFile.Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    }
}
