# Anti-aliasing before FSR 1: research and choice

Written 2026-10-05, with the change that added SMAA 1x (`SmaaPass`). `.claude/rules/rendering.md` describes the pass;
this file records the research behind the choice, so it need not be repeated.
The benchmark views and scenes it names (forest, trail, canopy, crowds of goblins) are those of Zorf TD, the game Itera
was built with; `docs/rendering-redesign.md` explains them.

## The question

Lucy asked for anti-aliasing to go with FSR 1. They believed that FSR 1 should be given an anti-aliased picture, and that
SMAA (Subpixel Morphological Antialiasing) would be the best method, and asked for both to be checked independently.

The renderer as it was then (the raymarcher, since removed from main; the raster pipeline draws with 4x MSAA and runs
SMAA only with MSAA off), as it bears on anti-aliasing:
- The world is raymarched in compute shaders, one camera ray per pixel, not rasterised. Characters are voxel volumes
  traced inside rasterised boxes. Thin features (leaves, grass, branches) are common, and far ones are under a pixel.
- The shading pass applies the tone curve, so the picture before upscaling is display-referred, in an sRGB image.
- Below render scale 1, FSR 1 (EASU, then RCAS) upscales it; dynamic render scaling moves the scale between 0.5 and 1.
- Frame blending reprojects each surface's light over 0.2 s, but the camera rays are not jittered and no colour history
  exists.
- Lucy turned down FSR 2 on 2026-10-05 as too complex, and methods tied to one GPU brand earlier.

## What FSR 1 asks of its input

AMD's FidelityFX SDK manual, FSR 1 page (gpuopen.com/manuals/fidelityfx_sdk/techniques/super-resolution-spatial/):
- the image "should already be well anti-aliased by a technique like TAA, MSAA etc.";
- "normalized to [0-1] and be in perceptual color space (sRGB, not linear)", and noise-free;
- FSR 1 goes "after anti-aliased rendering and tone mapping, but before any post-processing effects that introduce
  noise such as Film Grain, Chromatic Aberration etc or User Interface rendering".

The GPUOpen FSR page says FSR 1 "works by taking the current anti-aliased frame and upscaling it". Godot's resolution
scaling docs: "FSR1 upscaling works best when coupled with another form of antialiasing". So Lucy's first belief holds.

## The methods compared

Costs from Intel's CMAA 2.0 article (intel.com, "Conservative Morphological Anti-Aliasing 2.0"), added ms in the Bistro
scene:

| | GTX 1080 1080p | Vega 64 1080p | Vega 64 4K |
|---|---|---|---|
| FXAA | 0.11 | 0.09 | 0.30 |
| CMAA2 | 0.15 | 0.10 | 0.24 |
| SMAA 1x | 0.30 | 0.23 | 0.80 |

The Vega 64 is the nearest to this machine's RX 5700 XT.

- **FXAA 3.11.** One pass, cheapest, but it blurs; Godot's docs advise against it before FSR 1. Its licence text grants
  nothing clearly.
- **SMAA 1x.** Three passes (edges, blend weights, blend), two small lookup textures, no motion vectors, MIT licence,
  and an official GLSL path (`SMAA_GLSL_4`; used for Vulkan by vkBasalt, and in Godot 4.5). It handles long and
  diagonal edges best of the three. Its edges can be found by luma, colour, or depth, and a depth buffer can raise the
  threshold away from real edges (predication).
- **CMAA2 (Intel).** Compute passes that change the colour image in place; about half SMAA's cost and slightly sharper
  (Intel's PSNR against a supersampled picture: CMAA2 36.86 dB, FXAA 36.75, SMAA 36.67). But it "does not yet include
  advanced handling of diagonal aliasing patterns", has no depth option, exists only as HLSL (Apache 2.0), and its
  in-place writes need a storage image, which the sRGB swapchain formats do not offer.
- **TAA.** The only method here that adds samples, so the only one that fixes features under a pixel and their
  flicker in motion (Teardown, a voxel raymarcher, uses it). It needs jittered rays, motion vectors for moving
  characters, special handling for particles and edits, and clamping against ghosting; it softens the picture. That is
  FSR 2's class of complexity.
- **SMAA T2x.** SMAA 1x plus two jittered frames and a velocity buffer: most of TAA's work for two samples.
- **MSAA** does not apply: it needs rasterised coverage, and the world is one ray per pixel. **Supersampling** (more
  rays, or more rays only at edges) costs ray time where the frame is busiest.

No single-frame method (FXAA, SMAA 1x, CMAA2) recovers geometry thinner than a pixel. SMAA's authors say of the
morphological class that it is "designed to work on the silhouettes of objects and not with thin lines and features",
and that edges "may flicker as the edge flips back and forth between adjacent pixels".

## The choice

Lucy chose SMAA 1x on 2026-10-05, with an "Anti-aliasing" switch on the Graphics tab, on by default, and the
console's `aa` for the run. Reasons:
- it smooths the steps along edges that the benchmark shots show (canopy against the sky, wall tops, trunk and goblin
  outlines), and FSR 1 then enlarges smooth edges rather than steps;
- no ghosting, no motion vectors, and a ready GLSL path under MIT, like the vendored FSR 1 files;
- its cost is small, and smaller at lower render scales;
- it is the base of SMAA T2x, should a temporal method come later.

What it does not do, said before the choice: it does not fix flicker of thin features under a pixel, nor the speckle of
the surface recipes at distance (bark, ground). That speckle is aliasing of the recipes themselves; filtering them by
the pixel's footprint was a separate task, done the same day (below).

## How it is built

- `SMAA.hlsl` from github.com/iryoku/smaa (commit 71c806a8) is `Shaders/smaa.glsl`, unchanged. `smaa-common.glsl`
  sets `SMAA_GLSL_4`, `SMAA_PRESET_HIGH`, and `SMAA_RT_METRICS` from push constants; the three passes use the engine's
  full-screen triangle and work out SMAA's vertex offsets in the fragment shader from `gl_FragCoord`.
- SMAA was written for Direct3D, whose texture origin is the top-left corner, as Vulkan's is: no flip is needed, and
  the lookup textures are uploaded in their headers' row order. The console's `aa edges` and `aa weights` show the
  edges and weights; they lie on the outlines.
- The edges are found in sRGB-encoded values (a UNORM view of the scene image) and the blend happens in linear light
  (the image's sRGB view), as SMAA asks.
- With anti-aliasing on, the world draws into a scene image at every scale; the blend writes the swapchain image at
  scale 1 (and draws the aim dot) or FSR 1's source below it. The debug pictures are never anti-aliased.
- Not built: SMAA's stencil step that limits the weights pass to edge pixels (it needs a stencil image), and depth
  predication (only if the look test finds texture detail smeared).

## Measured

Benchmark on 2026-10-05 (Release, RX 5700 XT, 2560x1440, seed 1, labels `aa-off-1`, `aa-on-1`, `aa-off-067`,
`aa-on-067`; the first pair with 512 goblins for the crowd views). SMAA's three passes per view:

| | start | start-low | forest | forest-down | forest-up | canopy | trail | crowd-near | crowd-far | flight |
|---|---|---|---|---|---|---|---|---|---|---|
| scale 1 | 0.38 | 0.38 | 0.34 | 0.30 | 0.31 | 0.31 | 0.45 | 0.40 | 0.40 | 0.28 |
| scale 0.67 | 0.18 | 0.18 | 0.17 | 0.14 | 0.14 | 0.14 | 0.21 | - | - | 0.12 |

- The voxel and model passes took the same time with it on and off (within 0.1 % and 0.7 %), and FSR 1 its 0.42 ms.
- The cost follows the edges found: most in the trail view (many trunks and the trail's pebbles), least looking down.
- In the forest view at 1920x1080, SMAA changed 8.8 % of the pixels. The edges picture (`aa edges`) shows edges on
  every outline, and also many short ones in the bark's speckle on sunlit trunks far off, which SMAA then softens.
- Below scale 1 the gain is larger: without SMAA, FSR 1's sharpening (RCAS) turns the steps and the bark speckle into
  hard blocks; with it, both come out smoother.
- With anti-aliasing off, the new build draws what the old one did: with hard shadows (`FrameBlendSeconds=0`, so no
  soft-shadow samples that change each frame), the views captured after loading were pixel-identical at scales 1 and
  0.67 (start, forest, forest-down, forest-up); in those short runs the other screenshots caught the camera on its way to
  the next view.

## Texture shimmer: band-limiting the surface recipes

Written 2026-10-05, after SMAA, for the raymarcher's surface recipes. Phase 3 ports these recipes to the mesh shader
with the same fades (`docs/rendering-redesign.md`, phase 2 answer 13), so this section stays as their reference; the
Texture filtering setting left with the raymarcher on 2026-10-06. SMAA smooths the steps along edges; it cannot fix detail inside a surface that is finer
than a pixel. Lucy found the surface textures noisy and shimmering at a distance, most on trees, and on the trails, where
a ring of artefacts surrounded the player while moving.

### What caused it

A pixel covers 0.078 level-0 voxels per metre of distance at 1440p (one voxel at 12.8 m), and a feature starts to alias
once a pixel covers half of it. The recipes already faded their features by the pixel's footprint, but:
- the fade (`1 - smoothstep(0.35, 1.5, footprint / size)`) still kept 95 % of a feature where it began to alias, and half
  of it at about twice that distance;
- thin features faded at the size of the feature they belong to: bark cracks 1 to 2.2 voxels wide faded with the 8-voxel
  plate (half at 95 m, aliasing from 6 to 14 m); the 1-voxel grain (grass blade tips, bark grain) at 2 voxels;
- some variation never faded: each bark plate's own shade (aliasing past about 50 m), the wall stone's mottle;
- the footprint ignored the angle a surface is seen at: along a trail at 9 m a pixel covers 3.6 voxels of ground
  lengthwise, not the 0.7 assumed.

The ring: level 0 (1 cm voxels) ends at 13.4 m, then 2 cm to 26.9 m, 4 cm to 53.8 m, 8 cm to 107.5 m. Where the level
changes, aliased bark and pebbles flip to other values, so a band of the picture changes as the boundary sweeps over it.
Measured by drawing the trail view with level 0 reaching 24 m instead and counting changed pixels in the band: 4.2 % of
them changed by more than 20 levels; without the fine speckle 3.7 % (so the speckle's own re-roll as the ground's height
snaps was a small part). Level-dependent lighting (the shadow ray's start, the smoothing box) changes there too.

### What was built

All arithmetic on values the recipes have; no extra noise samples (Quilez's band-limited noise, box-filtered lines, and
Golus's grid lines are the models):
- **The footprint along the surface** (`surfaceFootprint`): divided by `sqrt(max(|dot(ray, normal)|, MinFacing))`,
  the mean of the pixel's two axes on the surface, so ground seen along the view fades earlier than a surface facing the
  camera. `MinFacing` 0.15 (0.1 measured the same, 0.25 a little worse). Lucy chose the square root after walking the
  trail with both: the full stretch (the pixel's longer axis, dividing by the facing itself) shimmered least (trail
  2.16 %, forest 0.98 %, start 0.94 %) but left ground seen at a slant plain within a few metres (the trail's pebbles
  beyond about 8 m, the flagstones' grain from 4 to 6 m); the square root keeps pebbles to about 20 m.
- **The fade at the sampling limit** (`fadeFor`): `1 - smoothstep(-2, 0, log2(footprint / size))`, kept below a quarter
  of the size, half at half, gone at the size. A linear step from 0.5 to 1.0 showed a visible line on the trail: on
  ground the footprint grows with the square of the distance, so a narrow step happens within a short stretch; the step
  over the logarithm spreads it over twice the distance.
- **Thin cracks as coverage**: a bark crack narrower than a pixel is drawn a pixel wide and as much fainter, so the
  network keeps its mean darkness instead of breaking into specks (fainter by the cracks' mean width, the same on both
  sides of a crack: by each cell's own width the two halves differed and a line showed down the middle); the 1-voxel
  grain fades at 1 voxel (the nearest trunk in the forest view loses its fine vertical grain, which aliased from 6 m;
  plates and cracks stay). Pebble rims widened to a pixel were tried and dropped: they shrank the pebbles' cover before
  the fade began, and on the ground, where pebbles lie, the footprint along the surface fades them early.
- **Fading to the mean**: plate shades, the cracks' share of bark (0.72 plate), the pebbles' cover of the ground (0.31
  of the accent share), and grass clumps' darkening (0.33) fade to their means, worked out over many cells of each
  recipe's layout, so a surface keeps its mean brightness with distance (measured within 0.3 levels on bark and grass,
  within 3 on the trail, which lightens a little as its relief fades). The strata's mottle fades at its size.
- **A setting**: the settings' Texture filtering (`Engine:Renderer:TextureFiltering`, on by default; Lucy's request)
  turns it all off through a bit of the view's switches (`ViewSwitches.TextureFiltering`): every changed formula takes
  its earlier value, and the picture is the earlier build's to the pixel (checked at three poses; the shimmer measure
  gave the earlier numbers too).
- Not changed: leaves (they hardly shimmered, and fading their darkening to its mean would darken far canopies) and the
  wall's blocks (their fade was already at the joint and bevel's width). Not built: speckle by x and z only on the
  ground, since the ring measure no longer showed the ground once the rest was in.

### Measured

Shimmer: at each pose a shot, a turn of 0.02 degrees (about half a pixel), a second shot; the share of pixels that
change by more than 20 levels (anti-aliasing off, hard shadows so frames repeat exactly, 2560x1440).

| | start | forest | forest-down | forest-up | canopy | trail | trail looking down |
|---|---|---|---|---|---|---|---|
| before | 2.18 % | 1.87 % | 0.34 % | 1.82 % | 0.72 % | 4.79 % | 4.51 % |
| after | 1.01 % | 1.16 % | 0.26 % | 1.10 % | 0.60 % | 2.54 % | 2.64 % |

At render scale 0.67 (with FSR 1): trail 8.41 % to 3.18 %, forest 3.80 % to 1.33 %, start 3.55 % to 1.40 %. With the
full stretch, the footprint along the surface alone took the trail to 3.77 % and the fade over the logarithm on top of
it to 2.84 %.

The ring measure fell from 4.2 % to 2.1 %, and on the ground from 8.0 % to 1.6 % (1.4 % with the full stretch): the
trail no longer shows a ring at the level boundaries; what is left there is on near trunks and canopy edges, where the
smoothed normals and shading change with the level (out of scope here). The ring Lucy saw was a different one, nearer
the player, and it remains (the open issue below).

`voxels.frag` went from 64 to 72 vector registers (16 to 14 waves per SIMD) with the footprint along the surface,
however it was written; the world pass measured the same (9.342 against 9.340 and 9.343 ms in the forest view), since the
shadow rays no longer run in that shader. `model-light.frag` stayed at 64. The benchmark (three runs each, 512 goblins)
gave the world pass within 0.4 % in every view and the models' passes unchanged; SMAA became 3 to 17 % cheaper, since it
finds fewer edges in speckle (the trail 0.447 to 0.372 ms). That benchmark ran with the full stretch and before three
one-operation fixes from the review; the square root compiles to the same instructions (a reciprocal square root for a
reciprocal), and the registers, rechecked after, did not change.

### Open issue (minor): sparkle on the ground 3 to 6 m ahead while moving

Left open by Lucy's choice (2026-10-05): it is not severe enough for the work a fix would take. While walking on the
trail, the plateau, and some grass, a band of the ground about 3 to 6 m ahead sparkles from the moment walking starts.
It is hardly visible on cobblestone, and it is worse walking than sprinting. It is not the level boundaries above, which
start at 13.4 m.

What was measured (Lucy's settings: FOV 120, dynamic render scaling at 120 fps, which drew at a scale of 0.82 to 0.83
on the trail). The measure: 16 shots with the camera turned by a 4x4 grid of sub-pixel offsets, averaged as a reference;
per distance band, how much a pixel changes across the 16 shots, and the error of one shot against the reference.
- The cause is recipe detail 1 to 3 pixels across that has hard edges and has not faded yet:
  - the pebbles' colour at their rims, and their relief and hollows, which stop sharply at the rim;
  - the grass clumps' relief, which stops sharply at a clump's edge (`step(0.0, dome)`);
  - the speckle, a sharp step of noise on a 1- or 2-voxel lattice.
- On the ground, a pixel covers more ground along the view than the square-root footprint counts (at 4 m, 1.3 voxels
  against 0.84), so these details stay sharp just where they alias.
- The recipes replaced by plain colour leave the 2 to 8 m ground still, with shadows on and with frame blending during a
  walk, so lighting, smoothed normals, and shadows are not part of it.
- The render scale stays put while walking (0.83, then 0.82 once), and frame blending reuses earlier light over the
  whole ground mid-walk (debug view 6).
- FSR 1's sharpening (RCAS, `UpscalePass.Sharpness`, 0.2 stops) nearly doubles it below scale 1: on the trail at 2.5 to
  3.5 m the change across the shots was 5.7 at scale 0.83 against 3.2 at scale 1, with anti-aliasing on.

What was tried and not kept:
- The texture fix brought the trail at 2.5 to 3.5 m from 5.7 to 3.9 at scale 0.83, and from 3.5 to 2.7 at scale 1. The
  grass at 1.5 to 3.5 m fell by about 60 %. `voxels.frag` stayed at 72 vector registers. Lucy saw no clear difference in
  the game, with the speckle kept longer or not. The fix had three parts:
  - pebble rims and clump edges drawn as coverage over the pixel's extent across each edge (from the view direction in
    the surface's plane and 1 / facing);
  - speckle edges widened by the pixel's length along the view;
  - the finest grain faded by that length.
- Weaker RCAS sharpening (2 stops) gave 3.3, and 2.6 with the texture fix. Rejected: it makes distant objects
  indistinct.

Not tried: blending the surface colour over frames as the light already is, since the sparkle shows only in motion;
fewer or softer-edged pebbles on the trail.
