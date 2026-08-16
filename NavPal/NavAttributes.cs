using System;

namespace NavPal
{
    /// <summary>
    /// <c>NavAttributeType</c> from <c>nav.h</c>. These are the per-area movement hints the engine acts
    /// on, and the ones a generator has to get right for bots to move sensibly.
    /// </summary>
    [Flags]
    public enum NavAttributes
    {
        None = 0,

        /// <summary>Must crouch to use this area.</summary>
        Crouch = 0x00000001,

        /// <summary>Must jump to traverse this area. The engine's own jump-area fixup can remove these.</summary>
        Jump = 0x00000002,

        /// <summary>Must move precisely; no running.</summary>
        Precise = 0x00000004,

        /// <summary>Inhibit discontinuity jumping.</summary>
        NoJump = 0x00000008,

        Stop = 0x00000010,
        Run = 0x00000020,
        Walk = 0x00000040,
        Avoid = 0x00000080,

        /// <summary>Area may become invalid, e.g. a breakable floor.</summary>
        Transient = 0x00000100,

        DontHide = 0x00000200,
        Stand = 0x00000400,
        NoHostages = 0x00000800,

        /// <summary>
        /// Area is on a staircase. Bots use this to stop trying to jump their way up steps, which is
        /// the visible symptom of stairs being mismarked.
        /// </summary>
        Stairs = 0x00001000,

        NoMerge = 0x00002000,
        ObstacleTop = 0x00004000,
        Cliff = 0x00008000,
    }

    /// <summary>
    /// Movement constants from <c>nav.h</c>, in Source units. Every threshold in the connection and
    /// stair passes traces back to one of these rather than to a tuned number.
    /// </summary>
    public static class NavConstants
    {
        /// <summary>Largest height difference traversable without jumping.</summary>
        public const float StepHeight = 18f;

        /// <summary>Standing jump height.</summary>
        public const float JumpHeight = 41.8f;

        /// <summary>
        /// Crouch-jump height - the real ceiling on an upward connection.
        ///
        /// 64, not 58. Valve's nav.h defines this twice: 58 under <c>#if defined(CSTRIKE_DLL)</c> and
        /// 64 for every other game. This had the Counter-Strike figure, which is the wrong one for the
        /// games Compile Pal is pointed at here - Garry's Mod and TF2 both take the 64 branch. A wall
        /// between 58 and 64 units high is one a player can crouch-jump onto and the flood was refusing
        /// to climb it, so no area was generated on top of it and no jump connection to it existed.
        /// A Counter-Strike build would want 58; this is not currently switched per game.
        /// </summary>
        public const float JumpCrouchHeight = 64f;

        /// <summary>Standing height; the headroom a sampled position needs to be walkable.</summary>
        public const float HumanHeight = 71f;

        /// <summary>
        /// Half <see cref="HumanHeight"/>, and a constant in its own right in nav.h rather than a
        /// derived one - 35.5, which is what <c>GetGroundHeight</c> and the hiding-spot cover test both
        /// measure from.
        /// </summary>
        public const float HalfHumanHeight = 35.5f;

        /// <summary>Eye height standing. Where a bot's sight actually starts from.</summary>
        public const float HumanEyeHeight = 62f;

        /// <summary>
        /// Eye height crouched. Not interchangeable with <see cref="HumanCrouchHeight"/>, which is the
        /// body's height rather than the eye's - the two sit next to each other in nav.h and this
        /// codebase has already confused that pair once.
        /// </summary>
        public const float HumanCrouchEyeHeight = 37f;

        /// <summary>
        /// How far up a climbable surface may be scaled. Another of nav.h's per-game pairs: 58 under
        /// <c>CSTRIKE_DLL</c> and 200 elsewhere, and unlike the others the two are wildly different
        /// rather than merely a few units apart.
        /// </summary>
        public const float ClimbUpHeight = 200f;

        /// <summary>
        /// Drop past which ground is a cliff rather than a step down. Unconditional in nav.h. Nothing
        /// here sets <see cref="NavAttributes.Cliff"/> yet, so this is currently unused.
        /// </summary>
        public const float CliffHeight = 300f;

        /// <summary>
        /// Valve's <c>HumanCrouchHeight</c>. Was 37, which is their <c>HumanCrouchEyeHeight</c> - a
        /// different constant that happens to sit next to it in nav.h.
        /// </summary>
        public const float HumanCrouchHeight = 55f;

        /// <summary>Shoulder width; connections need this much lateral clearance.</summary>
        public const float HumanWidth = 32f;

        /// <summary>
        /// Drop beyond which the engine stops generating a fall connection.
        ///
        /// 400, not 200 - the same CSTRIKE_DLL split as <see cref="JumpCrouchHeight"/>, and nav.h's own
        /// comment on the non-CS branch explains it ("Increased DeathDrop from 200, since zombies don't
        /// take falling damage"). With 200 here, every survivable drop between 200 and 400 units was
        /// refused as fatal and the areas below it left unreachable from above.
        /// </summary>
        public const float DeathDrop = 400f;

        /// <summary>Half the human width; the clearance a connection needs on each side.</summary>
        public const float HalfHumanWidth = HumanWidth / 2f;

        /// <summary>
        /// `nav_slope_limit`. Ground whose normal's Z is at least this is walkable; anything steeper
        /// becomes a jump area in Valve's generator rather than being discarded.
        /// </summary>
        public const float SlopeLimit = 0.7f;

        /// <summary>
        /// Flatness required before a surface is considered for the stair test. Valve's `TestStairs`
        /// only looks at surfaces above this, since a staircase reads as a series of flat treads.
        /// </summary>
        public const float StairNormal = 0.97f;

        /// <summary>
        /// `GenerationStepSize`. The spacing of the sampling grid, and so of the nodes areas are built
        /// from.
        /// </summary>
        public const float GenerationStepSize = 25f;
    }
}
