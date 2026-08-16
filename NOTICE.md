# Notice on Valve's Source SDK

Meshwright is a free tool, licensed **GPL-3.0** (see [LICENSE](LICENSE)). It reimplements algorithms
that Valve documents in the public [Source SDK 2013](https://github.com/ValveSoftware/source-sdk-2013),
and contains no Valve source code.

This file exists to tell contributors what they may and may not take from the SDK while working on
it.

## What you may take

Copyright covers **expression**, not **ideas, procedures or methods of operation**. Facts about how
the engine behaves are not Valve's to withhold, and getting them right is the entire point of this
project — the worst bugs in this codebase have all been cases of guessing a constant instead of
reading it.

So the following need no permission and are actively encouraged:

- Reading the SDK to understand how an algorithm works.
- Taking numeric constants and thresholds: `cornerSize = 20`, a corner score of exactly 2,
  `HumanCrouchEyeHeight = 37`, `nav_quicksave` defaulting to `1`, and so on.
- Naming the SDK function a pass corresponds to, so a reader can find the original.
- Describing an algorithm in a comment in your own words.

## What you may not take

- SDK code, in any language — including hand-translating C++ into C#.
- Anything from a leaked, decompiled or reverse-engineered copy of the engine. Those carry no licence
  at all and nothing above applies to them.

If you are unsure, write down what the code *does* and implement from your description rather than
from the code in front of you. That is how every pass here was written, and it has been accurate
enough to reproduce the engine's hiding spots exactly and its encounter structure exactly.

## Comments

Doc comments here describe the SDK's algorithms in this project's own words. Where a comment refers to
Valve's reasoning behind a threshold, it paraphrases rather than quotes. Please keep it that way when
adding to them — naming the SDK function or constant you are matching is useful to a reader and costs
nothing, but reproduce the behaviour, not the prose.

## On the SDK licence

Worth knowing, since it comes up: the Source 1 SDK Licence is not simply an alternative set of rules
to follow. It is a conditional grant, and its grant is *"to develop a modified Valve game running on
Valve's Source 1 engine"*. Meshwright is a standalone .NET program that reads a `.bsp` and writes a
`.nav`; it does not run on the Source engine. Complying with the licence's conditions would therefore
not obviously grant rights over SDK code for a tool of this shape, whatever the tool's own licence
happened to be.

That is information, not a verdict — the licensing of this project is the maintainer's decision, and
none of the guidance above depends on which way it goes. Nothing here is legal advice.
