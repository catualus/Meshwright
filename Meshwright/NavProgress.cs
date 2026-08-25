using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Meshwright
{
    /// <summary>
    /// Reports how far through the nav passes a run is.
    ///
    /// The engine's own <c>nav_generate</c> puts a phase name and a percentage on screen, and it is doing
    /// far less work than this: a full analyse on a large map spends most of its time computing area
    /// visibility, which here can run for minutes with nothing to show for it. Compile Pal's compile
    /// progress only moves between steps, so without this the longest step in a compile is also the one
    /// that looks most like a hang.
    ///
    /// Two kinds of phase, because the passes honestly differ. Most iterate a known set - areas, nodes,
    /// area pairs - and can report a real fraction. The flood fill cannot: it discovers how much ground
    /// there is by walking it, so the total is not known until it is finished. Rather than invent a
    /// denominator, those phases report a running count and the renderer shows movement without claiming
    /// a percentage.
    /// </summary>
    public sealed class NavProgress
    {
        /// <summary>One phase of the pipeline and its share of the whole.</summary>
        public readonly record struct Step(string Name, double Weight);

        /// <summary>
        /// A single observation. <see cref="Fraction"/> is null for phases whose extent is unknown until
        /// they finish, in which case <see cref="Count"/> is what there is to show.
        /// </summary>
        public readonly record struct Update(
            string Phase,
            int Index,
            int Total,
            double? Fraction,
            long Count,
            double Overall,
            TimeSpan Elapsed);

        /// <summary>A sink that discards everything, so callers never need a null check.</summary>
        public static NavProgress None { get; } = new(null, []);

        private readonly Action<Update>? sink;
        private readonly List<Step> plan;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Lock gate = new();

        private int index = -1;
        private double completed;
        private long lastEmit;

        /// <summary>How often an in-progress phase may emit. Fast enough to look live, slow enough that
        /// a tight parallel loop is not bottlenecked on the console.</summary>
        private static readonly long EmitInterval = Stopwatch.Frequency / 10;

        /// <summary>
        /// Weights are normalised, so a caller can hand over the phases it is actually going to run
        /// without having to rebalance them by hand when an option turns one off.
        /// </summary>
        public NavProgress(Action<Update>? sink, IReadOnlyList<Step> steps)
        {
            this.sink = sink;
            plan = [.. steps];

            double total = 0;
            foreach (var step in plan)
                total += step.Weight;

            if (total > 0)
            {
                for (int i = 0; i < plan.Count; i++)
                    plan[i] = plan[i] with { Weight = plan[i].Weight / total };
            }
        }

        /// <summary>
        /// Moves to a phase, banking whatever came before it as done. A phase not in the plan is
        /// appended with no weight, so an unplanned pass shows its name without disturbing the bar.
        /// </summary>
        public void Enter(string phase)
        {
            if (sink is null) return;

            lock (gate)
            {
                for (int i = index + 1; i < plan.Count; i++)
                {
                    if (!string.Equals(plan[i].Name, phase, StringComparison.Ordinal))
                        continue;

                    for (int skipped = index + 1; skipped < i; skipped++)
                        completed += plan[skipped].Weight;

                    completed += index >= 0 ? plan[index].Weight : 0;
                    index = i;
                    Emit(0, 0, force: true);
                    return;
                }

                completed += index >= 0 && index < plan.Count ? plan[index].Weight : 0;
                plan.Add(new Step(phase, 0));
                index = plan.Count - 1;
                Emit(0, 0, force: true);
            }
        }

        /// <summary>Progress within the current phase, from 0 to 1.</summary>
        public void Report(double fraction)
        {
            if (sink is null || !DueToEmit()) return;

            lock (gate)
                Emit(Math.Clamp(fraction, 0, 1), 0, force: false);
        }

        /// <summary>
        /// Progress for a phase with no knowable total - a count of what has been found so far. The bar
        /// holds at the phase boundary rather than pretending to a percentage.
        /// </summary>
        public void Counted(long count)
        {
            if (sink is null || !DueToEmit()) return;

            lock (gate)
                Emit(null, count, force: false);
        }

        public void Finish()
        {
            if (sink is null) return;

            lock (gate)
            {
                index = plan.Count - 1;
                completed = 1;
                Emit(1, 0, force: true);
            }
        }

        /// <summary>
        /// Cheap enough to call from the body of a parallel loop: a single interlocked read against the
        /// last emit, with no lock taken on the overwhelmingly common "not yet" answer.
        /// </summary>
        private bool DueToEmit()
        {
            long now = Stopwatch.GetTimestamp();
            long previous = Interlocked.Read(ref lastEmit);

            return now - previous >= EmitInterval
                   && Interlocked.CompareExchange(ref lastEmit, now, previous) == previous;
        }

        private void Emit(double? fraction, long count, bool force)
        {
            if (force)
                Interlocked.Exchange(ref lastEmit, Stopwatch.GetTimestamp());

            string name = index >= 0 && index < plan.Count ? plan[index].Name : "working";
            double weight = index >= 0 && index < plan.Count ? plan[index].Weight : 0;

            sink!(new Update(
                Phase: name,
                Index: index + 1,
                Total: plan.Count,
                Fraction: fraction,
                Count: count,
                Overall: Math.Clamp(completed + weight * (fraction ?? 0), 0, 1),
                Elapsed: clock.Elapsed));
        }
    }
}
