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
        /// The token every `Parallel.For`/`Parallel.ForEach` pass polls between iterations. Defaults to
        /// <see cref="CancellationToken.None"/>, so nothing here behaves differently unless a caller
        /// (currently <c>MeshwrightProcess.Run</c>, the CompilePal compile step) opts in. Without this, a
        /// pass that has already started - the visibility trace can run several minutes on a large map -
        /// ran to completion regardless of an outer cancellation request, since the only place that was
        /// ever checked was between whole passes, not inside one.
        /// </summary>
        public static CancellationToken CancellationToken { get; set; } = CancellationToken.None;

        /// <summary>
        /// Fresh <see cref="ParallelOptions"/> reflecting the current setting. Read at the start of each
        /// pass rather than cached, so a change takes effect on the next pass without needing every call
        /// site to notice a mutation to shared, possibly-in-use options.
        /// </summary>
        public static ParallelOptions Options => new() { MaxDegreeOfParallelism = maxThreads, CancellationToken = CancellationToken };
    }
}
