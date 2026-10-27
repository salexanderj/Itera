---
paths:
  - "src/Itera.Engine/Rendering/Shaders/{clouds.glsl,cloud-shape.glsl}"
  - "src/Itera.Engine/Rendering/Meshes/CloudNoise.cs"
  - "tests/Itera.Engine.Tests/Rendering/Meshes/CloudNoiseTests.cs"
---

## The clouds

The look's clouds: their shape, light, haze, and shadow. Their values are the look's (`rendering-look.md`), which
`LookConstants` writes into the frame slot's record.

- The clouds are drawn by the sky's own pass alone (`skyClouds` in `clouds.glsl`, which only `world-sky.frag`
  includes), on a plane at the look's cloud height in the world. So they stay over the land as the eye moves, and drift
  along their heading by the run's clock.
- Their shape and their shadow on the ground are `cloud-shape.glsl`'s, which reads by mip level so any stage may
  include it.
- Their shape is the clouds' noise (`CloudNoise`, binding 19): a tileable RGBA texture made on the CPU when the pass
  starts, gradient noise in red and cellular noise turned inside out at three scales in the rest. A tile spans four
  lattice cells (`CloudCellsPerTile`).
- The look's billow share carves the gradient noise by the cellular noise into rounded puffs, its ridge share thins it
  to wisps, and its softness sets the edges. Its erosion eats the edges between the cells of a three times finer read
  (`CloudErosionScale`), into rounded lobes. The warp is a read at a quarter scale.
- A scrap narrower than a lobe lets the sky show through it (`CloudScrapOpacity`), so it does not stand as a line.
- Each read takes the pixel's footprint from derivatives found before any pixel leaves the shader (`textureGrad`), so
  the reads pick their mip levels aright at the horizon. A footprint's long side is drawn in to the sampler's
  anisotropy (`CloudMostSlant`), so a slanted read stays sharp across the screen.
- The CPU works out the clouds' lattice point at the origin, less the drift, wrapped to 4,096 cells (`LookConstants`'
  `CloudNear` and `CloudFar`), a whole number of every read's tiles, so the point keeps its precision at any place or
  time.
- A cloud takes the sky's light, less where it is deeper. With the look's cloud shadow, the shape blurred over about a
  lobe's width (`cloudReadLod`) at the point and three steps towards the sun (`CloudLightSteps`) shades it: a mass is
  lit on its sunward side and dark in its middle and on its far side. Without it, edges facing the sun take the light.
- Each billow is lit on its sunward side and shaded on its far side: the shape raised by the erosion's lobes, a little
  blurred, against the same a short step sunwards (`cloudRelief`).
- With the look's phase, the light depends on the view against the sun (`cloudPhase`): a forward lobe, narrow for a
  mass's thin margin and wide for its middle, and a backward lobe for thick cloud.
- No cloud is brighter than `LookConstants.CloudSunShare` of the sky towards the sun with its disc (`CloudBrightest`,
  worked out once a frame).
- With the look's haze share, the clouds take that share of the atmosphere's haze (`CloudHazePerMetre`, through
  `hazeOver`) by where the ray meets their plane. The bank of sides takes its base's, as one.
- A second layer twice as high, larger to the eye, may show behind.
- Thin wisps may show in front, at `LookConstants.LowerCloudHeightShare` of the height. They are drawn out more along
  the heading (`LowerCloudStretch`) and drift faster (`LowerCloudDrift`), from a lattice point of their own
  (`CloudLowerNear`).
- The clouds thin to none at the horizon, so the fogged far ground still meets the sky. Without a reach, their size
  changes more slowly near the horizon and they thin by their angle. With the look's reach, the ray meets the plane
  about where it does (`CloudHorizonLift`), and they thin over the farther 70 % of the reach.
- With a reach, clouds low over the horizon take a far look by the ray's angle (`CloudFarFrom`, `CloudFarTo`): less
  light, erosion, and upper layer, and crisper edges. Seen at a slant, those details are only streaks, smudges, and
  lines of light.
- With the look's thickness, each cloud is a column over the plane, as high as its shape is over the cover's edge
  (`cloudColumn`), domed on top. Where the flat base does not hide the sky, `cloudSides` steps through the slab
  (`CloudSlabSteps`) to the top plane, found past the reach (`cloudPlane`). Each column rising over the ray hides
  what lies behind, front to back.
- A side is darker low down, lit where little of its column lies over it, and lit more when the sun is behind the
  eye. The sides come in only low over the horizon (`CloudSideFrom`, `CloudSideTo`), so the clouds overhead keep
  their flat look. The bank fades by its angle (`CloudBankFadeFrom`, `CloudBankFadeTo`).
- With the look's ground shadow, `cloudGroundShadow` dims the sunlight under a cloud along the sun's rays, from a
  blurred read. `shadeSurface` takes it for the world, the models, and the mesh assets, and `grass.frag` for near and far
  blades. The particles take none: `particle-simulate.comp` has no look.

### Tests

`CloudNoiseTests`, and the clouds' values in `RenderLookTests`.
