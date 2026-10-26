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

Particles (`Engine:Particles`, `ParticleEmitter`, `ParticlePass`) live on the GPU. The look they follow is in
`rendering-look.md`, and the world they meet in `rendering-light.md`.

### Making and moving them

- Game code queues bursts of a recipe by name (`ParticleRecipes`, registered once a run).
- `StockEffects` registers seven general recipes, `flare`, `sparks`, `embers`, `debris`, `smoke`, `dust-puff`, and
  `grit`, with helpers to emit them, when a game makes it.
- A spawn and a simulate compute pass make and move the particles after the world's pass. They run in frames that draw
  the world. Under the loading screen they wait.
- A frame without graphics (the loop's with `Engine:Window:Mode=None`, and `HeadlessMatch.Frame`) has no pool. It drops
  the bursts queued since the last at its start (`ParticleEmitter.Discard`), so the queue never fills.
- The pool is a ring that overwrites the oldest particle. Its size is `Engine:Particles:Capacity`.
- The emitter keeps a particles' clock that runs as the GPU ages them. It adds the frame's capped delta (the constants'
  `Delta`) only in frames that simulate: ones that draw the world with particles alive or made.
- A frame that makes particles keeps the pass active until the longest life among its bursts' recipes has passed on
  that clock, with a margin (`ParticleEmitter.DeathMargin`) for the GPU's float rounding.
- The emitter keeps each such frame as a spawn batch: where it starts in the ring and when its particles are all dead.
  The frame carries the span of the ring that can be alive (`ParticleFrame.LiveFirst` and `LiveCount`): from the oldest
  batch not yet dead to the head.
- The batches are a fixed ring of `ParticleEmitter.MaxBatches`. When it is full, the two oldest become one, with the
  later death. A span of a full lap or more is the whole pool.
- The simulate pass reads only the span: invocation i reads slot `LiveFirst` + i round the pool
  (`ParticleConstants.LiveFirst`), in as many groups as `LiveCount` needs.
- Under the loading screen (`ParticlePass.Write` told the world is not drawn) and while off, the clock stops and the
  particles wait. Bursts drained under the loading screen are not made and get no batch.
- Nothing comes back to the game and nothing is sent: each game makes its own particles.
- The console's `particles` tests recipes by name.

### Meeting the world

Particles meet the world through the camera's depth image (`DepthTarget`, handed to the compute pass and back).

- A particle that moves to just behind what the image shows bounces off that surface, sticks, or dies, as its recipe
  says. The surface's slope comes from the neighbouring depths. A particle comes to rest on a surface that faces
  against the match's gravity.
- Off the image, behind something, or too near the camera (an orthographic view's: outside its box), a particle meets
  nothing.
- A resting particle falls again where the image shows its support gone.
- A recipe's gravity is a multiple of the match's (`PhysicsWorld.Gravity`, its direction too; the Earth's with no
  match). `GpuParticleRecipe` carries the vector and the match's up (+y with no gravity). `ParticlePass.Write` is given
  both each frame.

### Drawing

- A lit particle in view takes its sunlight from the shadow cascades and its sky light from the sky field
  (`mesh-light.glsl`, with `MeshPass`'s set bound as set 1). The values ease halfway each frame into two bytes of its
  record. The sunlight is one lookup (`sunShadowOnce`), not the near cascade's soft filter.
- A sprite whose opacity is under a level of 255 at its axis (a smoke's faded tail, a sprite deep in fog) is moved out
  of the view in `particle-sprites.vert`, so it shades no pixel.
- Lit cubes and soft sprites are drawn after the world, each by one indirect draw, tested against its depth. A blended
  mesh asset writes none, so a particle behind one draws over it, untinted.
- Their light and colour follow the frame's look: `look.glsl`, binding 8 of their draw set, compiled with
  `LOOK_BINDING 8`. This covers the exposure, the tone curve, and the grade. Their sunlight is scaled by `LOOK.daylight`,
  so it fades at dusk and is gone at night.
- `ParticleConstants` carry the view's near, far, and lens (`Orthographic`), so the depth read back, the collisions,
  the culling, and the sprites' facing work through an orthographic camera too.

### Under a moved origin

The pool's particles are local to the match's origin and move with it.

- The render thread keeps the origin the pool was last simulated in (`ParticlePoolOrigin`). Each frame's `ParticleWrite`
  carries its origin.
- The first frame that simulates in another origin gives the difference in `ParticleConstants.Shift`.
  `particle-simulate.comp` takes it off every particle made before that frame: its position and segment end. The
  spawn pass marks the frame's new particles (`PARTICLE_NEW`), already in the frame's origin.
- Frames that do not simulate (presented again, under the loading screen, none alive) leave the move pending. An
  emptied pool takes the frame's origin.
- The queued bursts, the attractors, and the stress test's place move on the CPU (`ParticleEmitter.Shift`, from
  `OriginShifts`).

### Files in `src/Itera.Engine`

- `Particles/`: `ParticleEmitter`, `ParticleBurst`, `ParticleRecipe(s)`, `ParticleStats`, `ParticleColours`, and
  `StockEffects`.
- Shaders `particle-*.comp`, `.vert`, and `.frag` over `particle-data.glsl` and `particle-constants.glsl`.

### Tests

`ParticleEmitterTests`, `ParticlePassTests`, and `ParticleCommandsTests`.
