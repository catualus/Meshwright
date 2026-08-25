using System;
using System.Threading;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That asking a run to stop actually stops it.
    ///
    /// This is tested because the mechanism spent a long time being inert. Every parallel pass took its
    /// token from <see cref="NavConcurrency"/> and <see cref="NavPipeline"/> checked between passes -
    /// eleven call sites - and nothing anywhere ever cancelled that token, so all eleven were reading a
    /// value that could not change. Nothing failed; Ctrl+C simply killed the process instead. A test
    /// that cancels the token and expects the work to stop is the only thing that can tell the wired-up
    /// version from the dead one.
    /// </summary>
    public class CancellationTests : IDisposable
    {
        private readonly CancellationToken original = NavConcurrency.CancellationToken;

        public void Dispose() => NavConcurrency.CancellationToken = original;

        private static NavArea Area(uint id, float x)
        {
            var a = new NavArea { Id = id };
            a.NwCorner[0] = x; a.NwCorner[1] = 0; a.NwCorner[2] = 0;
            a.SeCorner[0] = x + 50; a.SeCorner[1] = 50; a.SeCorner[2] = 0;
            return a;
        }

        private static NavFile Mesh(int areas)
        {
            var nav = new NavFile();

            for (int i = 0; i < areas; i++)
                nav.Areas.Add(Area((uint)(i + 1), i * 50));

            // A chain, so the pass has real work rather than an empty graph.
            for (int i = 0; i + 1 < areas; i++)
                nav.Areas[i].Connections[NavGeometry.East].Add(nav.Areas[i + 1].Id);

            return nav;
        }

        [Fact]
        public void TheSeamCheckThrowsOnceCancelled()
        {
            using var source = new CancellationTokenSource();
            NavConcurrency.CancellationToken = source.Token;

            NavConcurrency.ThrowIfCancelled();   // not yet asked to stop

            source.Cancel();

            Assert.Throws<OperationCanceledException>(NavConcurrency.ThrowIfCancelled);
        }

        /// <summary>
        /// The inner half: a parallel pass has to see the token too, or a run stays uninterruptible for
        /// as long as its longest pass - which for visibility is most of the run.
        /// </summary>
        [Fact]
        public void AParallelPassStopsOnceCancelled()
        {
            using var source = new CancellationTokenSource();
            source.Cancel();

            NavConcurrency.CancellationToken = source.Token;

            Assert.ThrowsAny<OperationCanceledException>(() => AreaConnectionFixer.Fix(Mesh(500)));
        }

        [Fact]
        public void NothingIsThrownWhenNobodyHasAsked()
        {
            NavConcurrency.CancellationToken = CancellationToken.None;

            NavConcurrency.ThrowIfCancelled();
            var result = AreaConnectionFixer.Fix(Mesh(200));

            Assert.NotNull(result);
        }

        /// <summary>
        /// The parallel options must read the token at the point a pass starts, not when they were first
        /// built - callers set the token before dispatch and passes run long afterwards.
        /// </summary>
        [Fact]
        public void OptionsCarryWhicheverTokenIsCurrent()
        {
            using var source = new CancellationTokenSource();

            NavConcurrency.CancellationToken = source.Token;
            Assert.Equal(source.Token, NavConcurrency.Options.CancellationToken);

            NavConcurrency.CancellationToken = CancellationToken.None;
            Assert.Equal(CancellationToken.None, NavConcurrency.Options.CancellationToken);
        }
    }
}
