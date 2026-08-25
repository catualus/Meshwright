using System;
using System.Collections.Generic;

namespace Meshwright
{
    /// <summary>
    /// A sampled point of walkable ground, modelled on Valve's <c>CNavNode</c>.
    ///
    /// The node is what makes a generator out of a flood fill. A bare grid cell knows only where it is;
    /// a node also knows which way the ground faces and which neighbours are actually reachable from
    /// it, and those two facts are what every later phase reads:
    ///
    /// - walkable is <c>normal.z >= nav_slope_limit</c>, not an inference from neighbouring heights
    /// - an area's four corner heights are the heights of its four corner nodes, which is why Valve's
    ///   areas follow slopes and staircases where flat quads cannot
    /// - areas may only grow across nodes that are co-planar with the corner they started from
    ///
    /// Links are deliberately **not** commutative. Valve's comment is explicit about why: falling off a
    /// ledge is a connection in one direction only, and collapsing that loses the distinction between a
    /// step and a drop.
    /// </summary>
    public sealed class NavNode(int gx, int gy, BspFile.Vector3 position, BspFile.Vector3 normal)
    {
        public int Gx { get; } = gx;
        public int Gy { get; } = gy;

        public BspFile.Vector3 Position { get; } = position;

        /// <summary>Ground normal, taken from the surface the sample trace landed on.</summary>
        public BspFile.Vector3 Normal { get; } = normal;

        public NavAttributes Attributes { get; set; }

        /// <summary>Reachable neighbour in each NavDirType, or null. One-way by design.</summary>
        public readonly NavNode?[] To = new NavNode?[NavGeometry.DirectionCount];

        /// <summary>Index of the area this node was consumed into, or -1 while unassigned.</summary>
        public int AreaIndex { get; set; } = -1;

        public bool IsCovered => AreaIndex >= 0;

        public float Z => Position.Z;

        /// <summary>Whether the ground here is shallow enough to stand on.</summary>
        public bool IsWalkable => Normal.Z >= NavConstants.SlopeLimit;

        public void ConnectTo(int direction, NavNode other) => To[direction] = other;

        /// <summary>
        /// Distance of a point off this node's ground plane. Used to decide whether a growing area is
        /// still following the same surface - Valve allows 5 units before it stops.
        /// </summary>
        public float DistanceOffPlane(BspFile.Vector3 point)
        {
            float dx = point.X - Position.X;
            float dy = point.Y - Position.Y;
            float dz = point.Z - Position.Z;

            return MathF.Abs(dx * Normal.X + dy * Normal.Y + dz * Normal.Z);
        }
    }

    /// <summary>
    /// The sampled node grid: every walkable point the flood reached, addressable by grid position and
    /// height, plus the links between them.
    /// </summary>
    public sealed class NavNodeGrid
    {
        private readonly Dictionary<(int Gx, int Gy), List<NavNode>> byCell = [];

        public List<NavNode> Nodes { get; } = [];

        /// <summary>Heights are quantised so repeated samples of one surface land on the same node.</summary>
        public const float HeightGranularity = 8f;

        public NavNode Add(int gx, int gy, BspFile.Vector3 position, BspFile.Vector3 normal)
        {
            var node = new NavNode(gx, gy, position, normal);

            if (!byCell.TryGetValue((gx, gy), out var list))
                byCell[(gx, gy)] = list = [];

            list.Add(node);
            Nodes.Add(node);
            return node;
        }

        public IReadOnlyList<NavNode> At(int gx, int gy)
            => byCell.TryGetValue((gx, gy), out var list) ? list : [];
    }
}
