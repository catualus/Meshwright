using System.Linq;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That the declared plan matches the order passes are actually entered in.
    ///
    /// The plan is not decoration. <see cref="NavProgress"/> counts phases as they arrive and banks the
    /// weight of everything before them, so a plan that lists a phase before one that really runs first
    /// makes the counter jump backwards - Compile Pal's bar used to report "[8/10]" and then "[11/11]"
    /// because stairs were declared before clipping and entered after it. Nothing fails when that
    /// happens; the numbers are just wrong, which is why it survived.
    ///
    /// The two constraints these pin are the ones that were got wrong in practice: clipping comes
    /// between connecting and stairs, and visibility is last. Both are ordering facts about the
    /// pipeline, not about the bar, so they belong next to the pipeline.
    /// </summary>
    public class NavPipelineTests
    {
        private static string[] Phases(NavPipeline.Options options)
            => [.. NavPipeline.Plan(options).Select(s => s.Name)];

        private static NavPipeline.Options Everything() => new()
        {
            GenerateAreas = true,
            Ladders = true,
            Movement = true,
            Spots = true,
            SniperSpots = true,
            EncounterSpots = true,
            Visibility = true,
            CompressVisibility = true,
        };

        [Fact]
        public void ClippingIsDeclaredBetweenConnectingAndMarkingStairs()
        {
            // The real constraint: clipping needs the connection graph to exist, and stair marking
            // needs the clipped shape. Running stairs before the clip marked 8 areas on gm_construct
            // against 17 after it, because an area still overhanging geometry sends the floor probes
            // off the end of the flight.
            var phases = Phases(Everything());

            int connecting = System.Array.IndexOf(phases, NavPipeline.PhaseConnections);
            int clipping = System.Array.IndexOf(phases, NavPipeline.PhaseClipping);
            int stairs = System.Array.IndexOf(phases, NavPipeline.PhaseStairs);

            Assert.True(connecting < clipping, "connections must be declared before clipping");
            Assert.True(clipping < stairs, "clipping must be declared before stairs");
        }

        [Fact]
        public void VisibilityAndItsCompressionComeLast()
        {
            var phases = Phases(Everything());

            Assert.Equal(NavPipeline.PhaseCompress, phases[^1]);
            Assert.Equal(NavPipeline.PhaseVisibility, phases[^2]);
        }

        [Fact]
        public void SpotGradingIsDeclaredAfterTheMovementPassesItReads()
        {
            // An encounter is a list of the covered spots seen along a path, so it needs both the
            // connection graph and the cover flags the hiding pass sets.
            var phases = Phases(Everything());

            int stairs = System.Array.IndexOf(phases, NavPipeline.PhaseStairs);
            int hiding = System.Array.IndexOf(phases, NavPipeline.PhaseHiding);
            int encounters = System.Array.IndexOf(phases, NavPipeline.PhaseEncounters);

            Assert.True(stairs < hiding, "movement must be declared before spots");
            Assert.True(hiding < encounters, "hiding spots must be declared before encounters");
        }

        [Fact]
        public void ATurnedOffStageContributesNoPhases()
        {
            var options = Everything();
            options.Visibility = false;

            var phases = Phases(options);

            Assert.DoesNotContain(NavPipeline.PhaseVisibility, phases);

            // Compression is nested under visibility, so turning the parent off has to take it too -
            // a bar that waits on a phase which will never be entered never reaches 100%.
            Assert.DoesNotContain(NavPipeline.PhaseCompress, phases);
        }

        [Fact]
        public void SkippingSniperGradingKeepsTheOtherTwoSpotPhases()
        {
            var options = Everything();
            options.SniperSpots = false;

            var phases = Phases(options);

            Assert.DoesNotContain(NavPipeline.PhaseSnipers, phases);
            Assert.Contains(NavPipeline.PhaseHiding, phases);
            Assert.Contains(NavPipeline.PhaseEncounters, phases);
        }

        [Fact]
        public void EveryPhaseIsDeclaredExactlyOnce()
        {
            // NavProgress looks a phase up by name. Two steps sharing one would make the second
            // arrival resolve to the first's index and drag the bar backwards.
            var phases = Phases(Everything());

            Assert.Equal(phases.Length, phases.Distinct().Count());
        }

        [Fact]
        public void TheWeightsAreNormalisedRatherThanTakenLiterally()
        {
            // The declared weights are shares of a full run, so any subset of stages sums to less than
            // one. NavProgress divides through by the total, which is what lets a caller hand over the
            // phases it is actually going to run without rebalancing them by hand.
            var options = Everything();
            options.GenerateAreas = false;
            options.Visibility = false;

            var plan = NavPipeline.Plan(options);

            Assert.NotEmpty(plan);
            Assert.True(plan.Sum(s => s.Weight) < 1.0);
            Assert.All(plan, s => Assert.True(s.Weight > 0));
        }
    }
}
