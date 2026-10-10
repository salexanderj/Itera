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
the world's pass, in frames that draw the world (under the loading screen they wait). They meet the world through the
camera's depth image (`DepthTarget`, handed to the compute pass and back): a particle that moves to just behind what
the image shows (0.3 m and its step) bounces off that surface, sticks, or dies as its recipe says, with the surface's
slope from the neighbouring depths; off the image, behind something, or within 0.6 m of the camera (an orthographic
view's: outside its box) it meets nothing, and a resting particle falls again where the image shows its support gone. A lit particle in
view takes its sunlight from the shadow cascades and its sky light from the sky field (`mesh-light.glsl`, with
`MeshPass`'s set bound as set 1), eased halfway each frame, into two bytes of its record. Lit cubes and soft sprites are
drawn after the world, each by one indirect draw. The pool (262,144 by default) is a ring that overwrites
the oldest. Nothing comes back to the game and nothing is sent: each game makes its own particles. The console's
`particles` tests recipes by name.

### Files in `src/Itera.Engine`

  - `Particles/`: `ParticleEmitter`, `ParticleBurst`, `ParticleRecipe(s)`, `ParticleStats`, `ParticleColours`,
    `StockEffects`; shaders
    `particle-*.comp`, `.vert`, `.frag` over `particle-data.glsl` and `particle-constants.glsl`.
