# Sound made in code

Written 2026-09-28, when sound was brought forward. The project has no recorded or bought sound effects and asked
whether a game on Itera can make its sounds in code with the methods of Andy Farnell's *Designing Sound* (MIT Press,
2010). This note gives the answer, the methods compared, and how the engine's recipes work. Since 2026-10-11 a game
also plays sounds and music read from files, where synthesis does badly or a recording is at hand
(`docs/sound-files.md`).

## The book

*Designing Sound* teaches sound design as modelling. Each practical starts from the physics of a sound: what gives
the energy (a hit, friction, air flow, a spark), what vibrates (a bar, a plate, a string, a cavity), and what shapes
the sound on its way to the ear (the object's body, distance, the room). It then builds a small program of noise,
oscillators, filters, and envelopes, driven by a few numbers from the game (speed, size, material), in Pure Data. The
same models are a few dozen lines of C# each; nothing in them needs Pure Data.

The practicals most useful to a game on a voxel world (chapter numbers):

| Practical | Model | Use |
|---|---|---|
| 30 Bouncing, 31 Rolling | a bank of ringing modes tuned to the object, struck once per contact; energy lost per bounce; rolling as a stream of small hits through a low-pass | loose bodies, thrown balls, cut pieces |
| 29 Telephone bell, 4.3 bars and plates | the mode ratios of bars, plates, and bells; higher modes die faster | stone, metal, wood hits |
| 38 Rain | drops at random times (a Poisson process) with random sizes, each a short click through the struck surface | the crunch of a dig: grains of soil and stone |
| 39 Electricity | a hum with its harmonics, crackle from sparse random clicks, a comb filter for movement | beams and sparks |
| 49 Footsteps | the force of heel, ball, and toe on the ground as three overlapping curves, shaping noise filtered by the ground | footsteps by gait and ground |
| 41 Wind | noise through filters whose level and centre drift slowly; whistles where it meets edges | ambience |
| 34 Fire, 35 Bubbles | hiss (filtered noise), crackle (sparse clicks), roar (low noise); bubbles as rising sine chirps | fire, lava, water |
| 54 Explosions | a shock (a short burst), a body of low noise, debris falling after | blasts |
| 52 Mammals | a vocal tract of band-pass filters over pulses | creature voices: weak (below) |

Chapter 22 (game audio) gives the reasons that matter here:

- **Behaviour, not events.** A recording plays the same way each time; a model follows the game's numbers (the speed
  of a landing, the size of a dig, the ground under a foot).
- **Default sounds.** A model driven by material and size gives every object a sound, with no file to make for each.
  A world with dozens of voxel materials can sort them into a few sound materials (soft ground, stone, wood, metal,
  flesh, leaves).
- **Cost and level of detail.** A synthesised sound can be made cheaper as it grows quieter, and a mix needs a budget
  (a voice count, a cull by loudness). With hundreds of walkers this matters for footsteps.
- **Hybrid.** Rendering variations once and playing them as samples keeps most of the variety at a recording's cost
  (section 22.5).
- **Hard sounds.** Voices and music are where synthesis does badly; the book says so itself (p. 325).
- **Denormals.** Long decays and quiet filters reach denormal floats, which slow the processor badly (p. 323).

## Other methods

- **Modal synthesis from physics** (van den Doel and Pai, FoleyAutomatic, SIGGRAPH 2001; later work such as
  Raghuvanshi and Lin 2006): an object's sound is its resonant modes ringing down after a hit; tuning a bank of modes
  to a material and a size gives that object's hits, and the contact's length sets the brightness (a hard, short hit
  excites the high modes). This is the core of the impact sounds.
- **Modes measured from a recording** (the method of an Xbox 360 game, Crackdown 2): needs recordings the project does
  not have.
- **Finite element or boundary element modes** (computed from a mesh): accurate but far more work than voxel bodies of
  a few materials need; mode ratios from the book's tables are enough.
- **Free recordings.** The Sonniss GDC bundles (tens of GB, released each year) are royalty-free for commercial games,
  need no attribution, and may not be used to train AI models. They are the practical source for what synthesis does
  badly. Not used yet.

## Verdict

| Sound | Verdict |
|---|---|
| hits, landings, rolling | good and cheap (modes) |
| digs and places | good (grains through a ground resonance) |
| beams and sparks | good; they are not real sounds anyway |
| footsteps | good (force curves over filtered noise) |
| wind, fire, lava | good and very cheap (filtered noise) |
| wounds | fair |
| explosions | good (layers of shaped noise) |
| creature voices | poor; wait, then recordings |
| music | no; a composer's work |

## How recipes work

A recipe is a small model of a sound's physics driven by one number, its level (0 to 1: how hard, how big). It is
registered by name in `SoundRecipes` (at most 256). The engine offers two kinds made in code (a sound read from a file,
`FileOneShotRecipe`, is registered and played the same way):

- A `OneShotRecipe` (a hit, a step, a dig) is rendered at start, `Levels` times `VariationsPerLevel` variations at fixed
  seeds, into OpenAL buffers. An event plays the variation nearest its level, never the same one twice running, with
  its own gain and pitch. This is Farnell's hybrid: the variety of synthesis at a recording's cost.
- A `StreamRecipe` (a beam, wind, fire) makes a `SoundGenerator` that the audio thread runs live, a block at a time,
  following the level the game gives it.

The building blocks are in `src/Itera.Engine/Audio/Synthesis/`: noise of three colours, a one-pole filter, a state
variable filter (Andrew Simper's form, stable while it changes), and a modal bank of up to 24 modes as rotating
phasors (stable at any frequency). Filter and resonator states are flushed below 1e-20 once a block, since .NET cannot
flush denormal floats. Recipes need no determinism across machines, so `MathF` is allowed; sound is cosmetic and never
sent over a session.

The engine's side (the audio thread, the voices, the cache of rendered variations, the console's `sound`) is in
`ARCHITECTURE.md`, "Sound is made in code". Only listening decides whether a recipe is right, so the console renders
a recipe to a WAV file (`sound wav NAME`) for listening outside the game. A game registers its recipes in an
`IRunStartup`, and the engine keeps its own few (`EngineSounds`: the menu's clicks and two test sounds).
