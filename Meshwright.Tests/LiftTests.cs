using System.Collections.Generic;
using System.Linq;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// Which brush entities are lifts, and the heights a rider on each stands at.
    ///
    /// The connector reduces a platform to its stops, so a stop at the wrong height is a lift that
    /// connects nothing. rp_downtown_meowy's mine lift had railings, and its stops were taken from the
    /// top of the railings rather than from the plate a rider stands on - 32 units too high at both
    /// ends, and no connection at either. Vertical func_doors were not considered at all, and most of
    /// them are rightly not lifts: garage doors and shutters move up and down too.
    /// </summary>
    public class LiftTests
    {
        private static Dictionary<string, string> Door(string movedir, string lip, string spawnflags = "0",
            string? spawnpos = null)
        {
            var entity = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["classname"] = "func_door",
                ["movedir"] = movedir,
                ["lip"] = lip,
                ["spawnflags"] = spawnflags,
            };

            if (spawnpos is not null)
                entity["spawnpos"] = spawnpos;

            return entity;
        }

        private static readonly BspFile.Vector3 SlabMins = new(0, 0, 0);
        private static readonly BspFile.Vector3 SlabMaxs = new(128, 128, 16);

        /// <summary>
        /// A slab door going up: CBaseDoor travels its own height less the lip, so a lift made from one
        /// carries a negative lip, and here moves 16 + 112 = 128 units.
        /// </summary>
        [Fact]
        public void AVerticalSlabDoorStopsWhereItStartsAndAtTheEndOfItsTravel()
        {
            var stops = ElevatorConnector.DoorStops(Door("-90 0 0", "-112"), SlabMins, SlabMaxs, 16f);

            Assert.Equal(new[] { 16f, 144f }, stops);
        }

        [Fact]
        public void ADoorGoingDownStopsBelowWhereItStarts()
        {
            var stops = ElevatorConnector.DoorStops(Door("90 0 0", "-112"), SlabMins, SlabMaxs, 16f);

            Assert.Equal(new[] { 16f, -112f }, stops);
        }

        /// <summary>
        /// Starting open puts the door at its far position when the map spawns. The heights are the
        /// same; the one it rests at first comes first. Both the old spawnflag and the newer keyvalue
        /// say so.
        /// </summary>
        [Theory]
        [InlineData("1", null)]
        [InlineData("0", "1")]
        public void ADoorThatStartsOpenRestsAtItsFarStopFirst(string spawnflags, string? spawnpos)
        {
            var stops = ElevatorConnector.DoorStops(Door("-90 0 0", "-112", spawnflags, spawnpos),
                SlabMins, SlabMaxs, 16f);

            Assert.Equal(new[] { 144f, 16f }, stops);
        }

        /// <summary>A shutter, as rp_downtown_meowy has a dozen of: 4 deep, 128 wide, 124 tall.</summary>
        [Fact]
        public void AShutterIsNotALift()
        {
            var stops = ElevatorConnector.DoorStops(Door("-90 0 0", "5"),
                new BspFile.Vector3(0, 0, 0), new BspFile.Vector3(4, 128, 124), 124f);

            Assert.Null(stops);
        }

        /// <summary>
        /// A box elevator car 142 tall, whose floor cannot be told from its roof from outside, is left
        /// alone rather than given stops at its roof.
        /// </summary>
        [Fact]
        public void SomethingTallerThanAPlayerIsNotTakenForAPlatform()
        {
            Assert.False(ElevatorConnector.IsLiftShaped(new BspFile.Vector3(0, 0, 0), new BspFile.Vector3(234, 149, 142)));
            Assert.True(ElevatorConnector.IsLiftShaped(SlabMins, SlabMaxs));
        }

        [Fact]
        public void ADoorThatSlidesSidewaysIsNotALift()
        {
            Assert.Null(ElevatorConnector.DoorStops(Door("0 90 0", "-112"), SlabMins, SlabMaxs, 16f));
        }

        /// <summary>A door that barely moves is a door, not a lift.</summary>
        [Fact]
        public void ASlabThatMovesOnlyItsOwnThicknessIsNotALift()
        {
            Assert.Null(ElevatorConnector.DoorStops(Door("-90 0 0", "0"), SlabMins, SlabMaxs, 16f));
        }

        private const string MineLift = """
            {
            "classname" "worldspawn"
            }
            {
            "classname" "func_movelinear"
            "model" "*1"
            "origin" "0 0 0"
            "movedir" "90 0 0"
            "movedistance" "200"
            "startposition" "0"
            }
            """;

        /// <summary>
        /// The regression: a platform whose model reaches 40 units up, railings included, while its
        /// floor is at 8. The stops are where a rider stands.
        /// </summary>
        [Fact]
        public void APlatformsStopsAreAtItsFloorNotAtTheTopOfItsRailings()
        {
            var bsp = TestWorlds.Map(MineLift,
            [
                (new BspFile.Vector3(-1000, -1000, -500), new BspFile.Vector3(1000, 1000, 500)),
                (new BspFile.Vector3(-64, -64, 0), new BspFile.Vector3(64, 64, 40)),
            ]);

            var stops = ElevatorConnector.PlatformStops(bsp, TestWorlds.Ground(8f)).ToList();

            Assert.Equal([8f, -192f], stops.Select(s => s.Position.Z));
            Assert.All(stops, s => Assert.Equal(8f, s.Depth));
        }

        /// <summary>With nothing to trace, the top of the model is all there is to go on.</summary>
        [Fact]
        public void WithoutGeometryTheTopOfTheModelStands()
        {
            var bsp = TestWorlds.Map(MineLift,
            [
                (new BspFile.Vector3(-1000, -1000, -500), new BspFile.Vector3(1000, 1000, 500)),
                (new BspFile.Vector3(-64, -64, 0), new BspFile.Vector3(64, 64, 40)),
            ]);

            Assert.Equal([40f, -160f], ElevatorConnector.PlatformStops(bsp).Select(s => s.Position.Z));
        }

        /// <summary>
        /// A rider standing on the floor of the shaft at the lower stop, where the platform's own
        /// geometry is not there to be sampled, is still on the lift: the floor sits as far below the
        /// stop as the platform's underside does below its plate.
        /// </summary>
        [Fact]
        public void TheFloorAPlatformRestsOnCountsAsRidingIt()
        {
            var bsp = TestWorlds.Map(MineLift,
            [
                (new BspFile.Vector3(-1000, -1000, -500), new BspFile.Vector3(1000, 1000, 500)),
                (new BspFile.Vector3(-64, -64, 0), new BspFile.Vector3(64, 64, 40)),
            ]);

            // Stops at 40 and -160 without geometry, so the platform's underside is 40 below its floor.
            var nav = new NavFile();
            nav.Areas.Add(TestWorlds.Area(1, -50, -50, 50, 50, -200));   // shaft floor, under the lower stop
            nav.Areas.Add(TestWorlds.Area(2, 64, -50, 164, 50, -160));   // landing beside it

            var result = ElevatorConnector.Build(nav, bsp);

            Assert.Contains(2u, nav.Areas[0].Connections.SelectMany(c => c));
            Assert.Contains(1u, nav.Areas[1].Connections.SelectMany(c => c));
            Assert.True(result.Connections >= 2);
        }
    }
}
