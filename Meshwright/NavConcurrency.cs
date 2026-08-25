using System;
using System.Threading;
using System.Threading.Tasks;

namespace Meshwright
{
    /// <summary>
    /// The thread ceiling every parallel pass in Meshwright reads from.
    ///
    /// Every flood, trace and area pass here uses every core by default, because that is the right
    /// default for a compile step running on a machine dedicated to the compile. It is the wrong default
    /// for someone compiling on a shared build box, a laptop they want to keep using while it runs, or a
    /// machine running several compiles at once - and there was previously no way to ask for anything
    /// else short of editing the source.
    ///
    /// A single static setting rather than threading a value through every call: every pass already
    /// takes an optional collaborator (progress, a reference mesh) as a late parameter, and this is
    /// process-wide configuration in the same spirit as an environment variable, not per-call state.
    /// </summary>
    public static class NavConcurrency
    {
        private static int maxThreads = Environment.ProcessorCount;

        /// <summary>
        /// The thread ceiling for every `Parallel.For`/`Parallel.ForEach` pass. Defaults to every core.
        /// Clamped to at least 1 - a value of 0 or below would mean "no threads", which is not a request
        /// this can honour, so it is treated as 1 rather than silently doing nothing.
        /// </summary>
        public static int MaxThreads
        {
            get => maxThreads;
            set => maxThreads = Math.Max(1, value);
        }

        /// <summary>
        /// The token every `Parallel.For`/`Parallel.ForEach` pass polls between iterations, and that
        /// <see cref="ThrowIfCancelled"/> checks at the seams between passes.
        ///
        /// Defaults to <see cref="CancellationToken.None"/>, so a library caller that wants no
        /// cancellation gets none and pays nothing. The command line sets it from Ctrl+C; a host
        /// embedding this sets it from whatever its own cancel button raises.
        ///
        /// Both halves are needed and neither is sufficient. Checking only between passes cannot
        /// interrupt the visibility trace, which is two thirds of a run and minutes long on a large map;
        /// checking only inside the parallel loops leaves the sequential seams - loading, merging,
        /// writing - unresponsive. <see cref="Options"/> carries the token into every parallel pass
        /// automatically, so a pass gets the inner half simply by using it.
        /// </summary>
        public static CancellationToken CancellationToken { get; set; } = CancellationToken.None;

        /// <summary>
        /// Fresh <see cref="ParallelOptions"/> reflecting the current setting. Read at the start of each
        /// pass rather than cached, so a change takes effect on the next pass without needing every call
        /// site to notice a mutation to shared, possibly-in-use options.
        /// </summary>
        public static ParallelOptions Options => new() { MaxDegreeOfParallelism = maxThreads, CancellationToken = CancellationToken };

        /// <summary>
        /// The between-passes check, for the sequential seams the parallel loops above do not cover.
        ///
        /// <see cref="NavPipeline"/> calls this between every pass. It reads the same token the parallel
        /// options carry, so a caller that sets one gets both halves of cancellation - inside a long
        /// pass and between them - rather than having to remember to check the token itself at every
        /// seam.
        /// </summary>
        public static void ThrowIfCancelled() => CancellationToken.ThrowIfCancellationRequested();
    }
}
