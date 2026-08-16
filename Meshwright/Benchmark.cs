using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Meshwright
{
    /// <summary>
    /// Runs a pass repeatedly and reports the distribution, so a change can be judged.
    ///
    /// This exists because timing a single run on an ordinary machine is worthless and quietly
    /// misleading. Measured on the laptop this was developed against, repeated identical runs of
    /// <c>build-spots</c> spread 25% at sixteen threads and 9% at eight - wide enough to swallow every
    /// optimisation anyone is likely to attempt, and wide enough that three separate "improvements"
    /// were reported here from single runs before anyone checked. Two of them turned out to be noise.
    ///
    /// **Minimum, not mean.** The distribution is one-sided: nothing makes a run faster than the work
    /// requires, but a scheduler tick, another process, or the CPU dropping a boost bin makes it
    /// slower. The mean therefore measures the machine's mood as much as the code, while the minimum
    /// converges on the real cost as samples accumulate. The mean and max are still printed, because a
    /// large gap between minimum and mean is itself the signal that the machine is too noisy to trust
    /// the run at all.
    ///
    /// A warm-up run is discarded before timing, since the first pass pays for JIT compilation and for
    /// faulting the BSP in from disk, neither of which is what a change is being judged on.
    /// </summary>
    public static class Benchmark
    {
        public sealed class Sample
        {
            public required string Name { get; init; }
            public required long[] Milliseconds { get; init; }

            public long Min
            {
                get
                {
                    long best = long.MaxValue;
                    foreach (long v in Milliseconds) best = Math.Min(best, v);
                    return best;
                }
            }

            public long Max
            {
                get
                {
                    long worst = 0;
                    foreach (long v in Milliseconds) worst = Math.Max(worst, v);
                    return worst;
                }
            }

            public double Mean
            {
                get
                {
                    double total = 0;
                    foreach (long v in Milliseconds) total += v;
                    return Milliseconds.Length == 0 ? 0 : total / Milliseconds.Length;
                }
            }

            /// <summary>
            /// How far the worst run strayed above the best, as a fraction. Above roughly 10% the
            /// machine is contributing more than most changes will, and a result should not be trusted
            /// without more samples or a quieter machine.
            /// </summary>
            public double Spread => Min == 0 ? 0 : (Max - Min) / (double)Min;
        }

        /// <summary>
        /// Times <paramref name="action"/> <paramref name="repeats"/> times after one discarded warm-up.
        /// </summary>
        public static Sample Measure(string name, int repeats, Action action)
        {
            action();   // warm-up: JIT and file cache, not what is being measured

            var timings = new long[repeats];
            var clock = new Stopwatch();

            for (int i = 0; i < repeats; i++)
            {
                clock.Restart();
                action();
                clock.Stop();
                timings[i] = clock.ElapsedMilliseconds;
            }

            return new Sample { Name = name, Milliseconds = timings };
        }

        /// <summary>Prints one sample as a row, with the spread flagged when it is wide enough to matter.</summary>
        public static void Report(Sample sample)
        {
            string warning = sample.Spread > 0.10
                ? $"   <- spread {sample.Spread:P0}, too noisy to trust a small change"
                : "";

            Console.WriteLine($"  {sample.Name,-26} min {sample.Min,7:N0} ms   " +
                              $"mean {sample.Mean,8:N0} ms   max {sample.Max,7:N0} ms{warning}");
        }

        /// <summary>
        /// Compares two samples on their minima, which is the only statistic worth comparing here.
        ///
        /// Reports the change as inconclusive when it is smaller than the noise either sample carries -
        /// a 3% gain measured on a machine with 25% spread is not a gain, and saying so is the whole
        /// reason this exists.
        /// </summary>
        public static void Compare(Sample baseline, Sample candidate)
        {
            double ratio = candidate.Min == 0 ? 0 : baseline.Min / (double)candidate.Min;
            double noise = Math.Max(baseline.Spread, candidate.Spread);
            double change = Math.Abs(ratio - 1.0);

            Console.WriteLine();
            Console.WriteLine($"  {baseline.Name} -> {candidate.Name}: {ratio:N2}x");

            if (change < noise)
            {
                Console.WriteLine($"  INCONCLUSIVE - a {change:P0} change against {noise:P0} noise. " +
                                  "Take more samples or quieten the machine.");
            }
            else
            {
                Console.WriteLine(ratio > 1.0
                    ? $"  faster by {change:P0}, against {noise:P0} noise"
                    : $"  SLOWER by {change:P0}, against {noise:P0} noise");
            }
        }
    }
}
