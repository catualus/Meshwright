using System.IO;
using System.Text;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// Byte-for-byte round-trip tests for the .nav reader and writer.
    ///
    /// <see cref="NavFile"/>'s own summary says it is "gated on a byte-for-byte round-trip test rather
    /// than on 'it parsed without throwing'" - and nothing enforced that until this file existed. The
    /// gate earns its place because of how this format fails. An area record is a run of fixed-size
    /// arrays whose lengths come from engine constants (MAX_NAV_TEAMS, NUM_CORNERS, NUM_DIRECTIONS,
    /// NUM_LADDER_DIRECTIONS), and getting one of them wrong does not throw: it desynchronises every
    /// following byte, so the file still loads, still reports a plausible area count, and fills those
    /// areas with garbage. The symptom arrives days later as a mesh the engine rejects or quietly
    /// mispaths on.
    ///
    /// What this does and does not prove is worth being straight about. Writing a fixture, reloading it
    /// and requiring the reload to re-emit identical bytes pins the layout against desynchronisation,
    /// truncation and dropped fields, which is the failure above. It cannot catch a reader and writer
    /// that misunderstand the format in the *same* way - both would agree with each other perfectly.
    /// The defence against that is `meshwright verify` run on a file the engine itself produced, which is a
    /// fixture-dependent check and so lives in the CLI rather than here.
    /// </summary>
    public class NavFileRoundTripTests
    {
        private static byte[] Write(NavFile nav)
        {
            using var stream = new MemoryStream();

            using (var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
                nav.Write(w);

            return stream.ToArray();
        }

        private static NavFile Read(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            using var r = new BinaryReader(stream, Encoding.ASCII);
            return NavFile.Read(r);
        }

        /// <summary>
        /// A mesh exercising every optional and variable-length part of the format at once: several
        /// places, uneven per-direction connection counts, hiding spots, an encounter with spots along
        /// it, ladder references in both ladder directions, per-corner light, a visibility list with an
        /// inheritance parent, and trailing custom data.
        ///
        /// Deliberately lopsided. Equal counts in every direction is exactly the shape that lets an
        /// index or stride mistake round-trip cleanly, so no field here shares a length with its
        /// neighbour if it can avoid it.
        /// </summary>
        private static NavFile Fixture(uint version)
        {
            var nav = new NavFile
            {
                Version = version,
                SubVersion = 3,
                BspSize = 1234567,
                IsAnalyzed = true,
                HasUnnamedAreas = true,
            };

            nav.Places.Add("Bunker");
            nav.Places.Add("Courtyard");
            nav.Places.Add("");   // an empty name is still a directory entry

            var first = new NavArea
            {
                Id = 1,
                AttributeFlags = (int)(NavAttributes.Crouch | NavAttributes.Stairs),
                PlaceIndex = 2,
                InheritVisibilityFrom = 2,
            };

            first.NwCorner[0] = -10; first.NwCorner[1] = -20; first.NwCorner[2] = 5;
            first.SeCorner[0] = 90; first.SeCorner[1] = 80; first.SeCorner[2] = 8;
            first.NeZ = 6;
            first.SwZ = 7;

            // uneven on purpose: 1, 2, 0 and 1 across the four directions
            first.Connections[NavGeometry.North].Add(2);
            first.Connections[NavGeometry.East].Add(2);
            first.Connections[NavGeometry.East].Add(3);
            first.Connections[NavGeometry.West].Add(3);

            first.HidingSpots.Add(new HidingSpot
            {
                Id = 7,
                Position = [1f, 2f, 3f],
                Flags = (byte)HidingSpot.SpotFlags.InCover,
            });
            first.HidingSpots.Add(new HidingSpot
            {
                Id = 8,
                Position = [4f, 5f, 6f],
                Flags = (byte)HidingSpot.SpotFlags.Exposed,
            });

            var encounter = new SpotEncounter
            {
                FromAreaId = 1,
                FromDirection = (byte)NavGeometry.North,
                ToAreaId = 2,
                ToDirection = (byte)NavGeometry.South,
            };
            encounter.Spots.Add((7u, (byte)0));
            encounter.Spots.Add((8u, (byte)128));
            first.Encounters.Add(encounter);

            first.Ladders[0].Add(1);
            first.Ladders[1].Add(1);
            first.Ladders[1].Add(2);

            first.EarliestOccupyTime = [1.5f, 2.5f];
            first.LightIntensity = [0.1f, 0.2f, 0.3f, 0.4f];

            first.VisibleAreas.Add(new VisibleArea { AreaId = 2, Attributes = 1 });
            first.VisibleAreas.Add(new VisibleArea { AreaId = 3, Attributes = 0 });

            // A second area carrying none of the optional parts, so the "everything empty" path is
            // covered in the same file as the "everything populated" one.
            var second = new NavArea { Id = 2 };
            second.NwCorner[0] = 90; second.NwCorner[1] = -20;
            second.SeCorner[0] = 190; second.SeCorner[1] = 80;

            nav.Areas.Add(first);
            nav.Areas.Add(second);

            nav.Ladders.Add(new NavLadder
            {
                Id = 1,
                Width = 32f,
                Top = [10f, 20f, 200f],
                Bottom = [10f, 20f, 40f],
                Length = 160f,
                Direction = 2,
                TopForwardAreaId = 1,
                TopLeftAreaId = 0,
                TopRightAreaId = 2,
                TopBehindAreaId = 0,
                BottomAreaId = 2,
            });

            // CNavMesh::SaveCustomData output from a derived game class. Preserving it verbatim is the
            // difference between rewriting a mesh and quietly stripping state we do not model.
            nav.TrailingData = [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01];

            return nav;
        }

        /// <summary>
        /// The current format. Reload and re-emit must be identical to the first emit, or some field is
        /// being read at a different width than it was written.
        /// </summary>
        [Fact]
        public void CurrentVersionRoundTripsByteForByte()
        {
            byte[] once = Write(Fixture(NavFile.CurrentVersion));
            byte[] twice = Write(Read(once));

            Assert.Equal(once, twice);
        }

        /// <summary>
        /// Older meshes, where whole fields are absent rather than merely empty.
        ///
        /// Each of these versions sits on one side of a conditional in both the reader and the writer -
        /// SubVersion at 10, LightIntensity at 11, HasUnnamedAreas at 12, IsAnalyzed at 14, the
        /// visibility block at 16 - and the pair has to agree about which. A version that writes a field
        /// the reader skips does not throw; it shifts everything after it.
        /// </summary>
        [Theory]
        [InlineData(4u)]
        [InlineData(10u)]
        [InlineData(11u)]
        [InlineData(12u)]
        [InlineData(14u)]
        [InlineData(15u)]
        [InlineData(16u)]
        public void EverySupportedVersionRoundTripsByteForByte(uint version)
        {
            byte[] once = Write(Fixture(version));
            byte[] twice = Write(Read(once));

            Assert.Equal(once, twice);
        }

        /// <summary>
        /// The variable-length parts survive with their contents intact, not merely with the right byte
        /// count. A length-prefixed list read at the wrong stride can still consume the right number of
        /// bytes overall and hand back nonsense.
        /// </summary>
        [Fact]
        public void VariableLengthSectionsSurviveIntact()
        {
            var nav = Read(Write(Fixture(NavFile.CurrentVersion)));

            Assert.Equal(3, nav.Places.Count);
            Assert.Equal("Bunker", nav.Places[0]);
            Assert.Equal("Courtyard", nav.Places[1]);
            Assert.Equal("", nav.Places[2]);

            var area = nav.Areas[0];

            Assert.Equal([2u], area.Connections[NavGeometry.North]);
            Assert.Equal([2u, 3u], area.Connections[NavGeometry.East]);
            Assert.Empty(area.Connections[NavGeometry.South]);
            Assert.Equal([3u], area.Connections[NavGeometry.West]);

            Assert.Equal([1u], area.Ladders[0]);
            Assert.Equal([1u, 2u], area.Ladders[1]);

            Assert.Equal(2, area.HidingSpots.Count);
            Assert.Equal(8u, area.HidingSpots[1].Id);
            Assert.Equal(6f, area.HidingSpots[1].Position[2], 3);

            var encounter = Assert.Single(area.Encounters);
            Assert.Equal(2u, encounter.ToAreaId);
            Assert.Equal(2, encounter.Spots.Count);
            Assert.Equal((8u, (byte)128), encounter.Spots[1]);

            Assert.Equal(2u, area.InheritVisibilityFrom);
            Assert.Equal(2, area.VisibleAreas.Count);

            // The zero-attribute entry is an explicit "not visible" override, not padding, so it has to
            // survive as a real entry rather than being dropped as empty.
            Assert.Equal(3u, area.VisibleAreas[1].AreaId);
            Assert.Equal(0, area.VisibleAreas[1].Attributes);
        }

        /// <summary>
        /// Corner heights are four independent floats, and three of them live outside the two corner
        /// vectors. Swapping NeZ and SwZ is the natural mistake and leaves the file exactly the right
        /// size, so only reading the values back catches it.
        /// </summary>
        [Fact]
        public void CornerHeightsKeepTheirIdentity()
        {
            var area = Read(Write(Fixture(NavFile.CurrentVersion))).Areas[0];

            Assert.Equal(5f, area.NwCorner[2], 3);
            Assert.Equal(6f, area.NeZ, 3);
            Assert.Equal(8f, area.SeCorner[2], 3);
            Assert.Equal(7f, area.SwZ, 3);
        }

        [Fact]
        public void TrailingCustomDataIsPreservedVerbatim()
        {
            var nav = Read(Write(Fixture(NavFile.CurrentVersion)));
            Assert.Equal<byte[]>([0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01], nav.TrailingData);
        }

        /// <summary>
        /// A file that is not a mesh has to be rejected at the magic rather than parsed into garbage.
        /// </summary>
        [Fact]
        public void RejectsAFileWithTheWrongMagic()
        {
            byte[] bytes = Write(Fixture(NavFile.CurrentVersion));
            bytes[0] ^= 0xFF;

            Assert.Throws<InvalidDataException>(() => Read(bytes));
        }

        /// <summary>
        /// A version this reader does not model is refused outright. Reading it on a best-effort basis
        /// would produce exactly the silent desynchronisation these tests exist to prevent.
        /// </summary>
        [Theory]
        [InlineData(3u)]
        [InlineData(17u)]
        public void RejectsUnsupportedVersions(uint version)
        {
            byte[] bytes = Write(Fixture(NavFile.CurrentVersion));
            System.BitConverter.GetBytes(version).CopyTo(bytes, 4);

            Assert.Throws<InvalidDataException>(() => Read(bytes));
        }
    }
}
