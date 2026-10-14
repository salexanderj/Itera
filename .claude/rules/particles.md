---
paths:
  - "src/Itera.Engine/Particles/**"
  - "src/Itera.Engine/Rendering/Shaders/particle-*"
  - "tests/Itera.Engine.Tests/Particles/**"
  - "src/Itera.Engine/Rendering/ParticlePass.cs"
  - "src/Itera.Engine/Commands/ParticleCommands.cs"
  - "tests/Itera.Engine.Tests/{Rendering/ParticlePass,Commands/ParticleCommands}Tests.cs"
---

## Particles

Particles (`Engine:Particles`, `ParticleEmitter`, `ParticlePass`) live on the GPU. Game code queues bursts of a recipe
by name (`ParticleRecipes`, registered once a run; `StockEffects` registers seven general ones, `flare`, `sparks`,
`embers`, `debris`, `smoke`, `dust-puff`, and `grit`, with helpers to emit them, when a game makes it); a spawn and a simulate compute pass make and move them after
the world's pass, in frames that draw the world (under the loading screen they wait); a frame without graphics (the
loop's with `Engine:Window:Mode=None`, and `HeadlessMatch.Frame`) has no pool, and drops the bursts queued since the
last at its start (`ParticleEmitter.Discard`), so the queue never fills. They meet the world through the
camera's depth image (`DepthTarget`, handed to the compute pass and back): a particle that moves to just behind what
the image shows (0.3 m and its step) bounces off that surface, sticks, or dies as its recipe says, with the surface's
slope from the neighbouring depths (it comes to rest on one that faces against the match's gravity); off the image,
behind something, or within 0.6 m of the camera (an orthographic
view's: outside its box) it meets nothing, and a resting particle falls again where the image shows its support gone. A lit particle in
view takes its sunlight from the shadow cascades and its sky light from the sky field (`mesh-light.glsl`, with
`MeshPass`'s set bound as set 1), eased halfway each frame, into two bytes of its record. Lit cubes and soft sprites are
drawn after the world, each by one indirect draw, tested against its depth (a blended mesh asset writes none, so a
particle behind one draws over it, untinted). Their light and colour follow the frame's look (`look.glsl`, binding 8 of
their draw set, compiled with `LOOK_BINDING 8`; the look is in `.claude/rules/rendering.md`): the exposure, the tone curve,
and the grade, and their sunlight scaled by `LOOK.daylight`, so it fades at dusk and is gone at night. Their constants
(`ParticleConstants`) carry the view's near, far, and lens (`Orthographic`), so the depth read back, the collisions,
the culling, and the sprites' facing work through an orthographic camera too. The pool (262,144 by default) is a ring
that overwrites the oldest. A recipe's gravity is a multiple of the match's (`PhysicsWorld.Gravity`, its direction
too; the Earth's with no match): `GpuParticleRecipe` carries the vector and the match's up (+y with no gravity), which
`ParticlePass.Write` is given each frame. Nothing comes back to the game and nothing is sent: each game makes its own particles. The console's
`particles` tests recipes by name.

The pool's particles are local to the match's origin and move with it: the render thread keeps the origin the pool was
last simulated in (`ParticlePoolOrigin`), and the first frame that simulates in another (each frame's `ParticleWrite`
carries its origin) gives the difference in `ParticleConstants.Shift`, which `particle-simulate.comp` takes off every
particle made before that frame (its position and segment end; the spawn pass marks the frame's new ones,
`PARTICLE_NEW`, already in the frame's origin). Frames that do not simulate (presented again, under the loading screen,
none alive) leave the move pending, and an emptied pool takes the frame's origin. The queued bursts, attractors, and the
stress test's place move on the CPU (`ParticleEmitter.Shift`, from `OriginShifts`).

### Files in `src/Itera.Engine`

  - `Particles/`: `ParticleEmitter`, `ParticleBurst`, `ParticleRecipe(s)`, `ParticleStats`, `ParticleColours`,
    `StockEffects`; shaders
    `particle-*.comp`, `.vert`, `.frag` over `particle-data.glsl` and `particle-constants.glsl`.
