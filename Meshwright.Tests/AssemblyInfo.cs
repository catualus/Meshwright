using Xunit;

// Tests run one at a time, not in parallel collections.
//
// Several things this codebase configures are deliberately process-wide - the thread ceiling, the
// cancellation token, the Counter-Strike movement limits, the extra content roots. That is the right
// shape for them: they are settings a person supplies once for a whole run, and making them per-call
// state would mean every pass threading a value it never reasons about (see GameFiles.AdditionalRoots
// for why the alternative silently loses content).
//
// It does mean a test that sets one is visible to every other test running at that moment. Cancelling
// the token to prove a parallel pass stops will also stop an unrelated pass another test is midway
// through, and the failure surfaces in the innocent test, intermittently, with nothing in it to
// suggest why. Serialising costs this suite well under a second and removes the whole class of
// problem.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
