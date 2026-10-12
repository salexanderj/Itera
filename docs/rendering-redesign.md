# Rendering and world redesign

Started 2026-10-06 from `docs/rendering-redesign-brief.md`, an outside review that recommends drawing the voxel world
as triangle meshes through a raster pipeline, a world of 5 to 10 cm voxels with density, trees, props, and towers as
their own objects, and room for underground play. The renderer as it stood then (the raymarcher) is kept only in the
archive of Zorf TD, the game Itera was built with (`salexanderj/ZorfTD-archive`: its branch `raymarcher` and tag
`raymarcher-final`). Itera keeps no part of it.

This file is the renderer's history: the decisions, the inventory of everything the raymarcher touched, and each
stage's measurements. It was written while Zorf TD was the only game, so "the game" below means Zorf TD, and the
benchmark's views are views of its map: `start` and `start-low` (the players' start), `forest`, `forest-down`,
`forest-up`, and `canopy` (among trees 14 to 30 m tall), `trail`, `tunnel` (a 28 m tunnel into a tower's hall), `flight`
(a circle), and `crowd-near` and `crowd-far` (1,000 held goblin models). The numbers hold for those views; a game's own
views (`BenchmarkViews` in `src/Game`) give other numbers. Names from the game's content appear where a
measurement used them: Stan (a floating head of 1 cm voxels, a test model), creeps (walking characters), Titans (giants
of 4 cm voxels), and the fortress (a tower with tunnels and a hall). "Lucy" is the project's maintainer, who made the
decisions.

## The phases

0. Shelve the raymarcher on the branch, and write this inventory.
1. The first raster stage, kept on main if it passes: an option to choose the pipeline, Surface Nets meshes of the
   existing voxel levels, sun shadows and MSAA, a crowd of 1,000 meshed goblins, and an underground view with point
   lights and a rough sky-visibility field. Measured against the bar below.
2. Decisions with Lucy, from phase 1's numbers and pictures.
3. The renderer core on the existing world data: cracks between levels of detail, remeshing after edits, mesh memory,
   culling, cascaded shadows, clustered lights, materials, anti-aliasing, debug views.
4. Characters, loose bodies, and particles as meshes; then the swap, one commit that deletes the raymarcher, its tests,
   and its docs from main. (Since stage 1.7 the raymarcher may leave main earlier: Decisions, after stage 1.7.)
5. The world at 10 cm with a stored density (chosen after look prototype D).
6. Trees, props, and towers as their own objects; the far view to the map edge.
7. Underground lighting and space, when underground play is taken up.

## Decisions

### At the start (2026-10-06)

- The raymarcher is archived on `raymarcher` and `raymarcher-final` (now in the Zorf TD archive).
- It stays on main behind `Engine:Renderer:Pipeline` (`Raymarch` or `Raster`) until the raster path draws everything
  it draws; then the swap removes it. (Withdrawn after stage 1.7: decision 1 of the next list.)
- Phase 1 is written to the project's standards as the renderer's first stage, not as throwaway code.
- The bar for phase 1, on the RX 5700 XT at 2560x1440 and render scale 1: the world with shadows takes at most 3 ms over
  the seven benchmark views; 1,000 near goblins add at most 4 ms; this machine's integrated GPU
  (`PreferredDevice=RAPHAEL`, 2 RDNA2 compute units) holds 60 fps at 1280x720 on the seven views; a tunnel darkens
  convincingly with a rough sky-visibility field. A result that misses this bar but passes the brief's (the world
  under 6 ms, a near crowd without the frame time doubling) goes to Lucy with its numbers and pictures. (The integrated
  GPU's part was retired for now after stage 1.7: decision 2 of the next list.)
- Phase 1 includes the meshed crowd (about 8 to 11 working days) and stops after the terrain and shadow stages if the
  world with shadows takes more than 6 ms.
- With `Raster`, the voxel GPU heap is not created, so particles have no world collision and are lit as if in sun until
  phase 4 gives them their own collision.
- At the swap, docs that only describe the raymarcher leave main; docs that partly do lose those sections; the branch
  keeps the full text. `ARCHITECTURE.md` is rewritten in place, since its rendering bullets sit among the rest.
- The world goes to 5 or 10 cm (not the 4 or 8 cm that today's levels 2 and 3 give for free). Which one, and how, is
  decided after phase 1.
- Characters keep their code-built bodies and per-part voxel volumes for damage and health; only how they are drawn
  changes.

### After stage 1.7 (2026-10-06)

1. **The raster pipeline is final.** The game moves to it completely and for good; the raymarcher gets no legacy
   support, and nothing is done with a move back to it in mind.
   - The raymarcher, its tests, its options, and its docs may leave main as soon as it is convenient, instead of
     waiting for the planned swap. `Engine:Renderer:Pipeline` goes with it.
   - No formats, code paths, or abstractions are kept compatible with it, and it is not ported to any new world format.
     Where work meets its code (for example its seven uses of `VoxelsPerMetre` in the unit split), that code is deleted,
     not adapted.
   - The `raymarcher` branch and the `raymarcher-final` tag are a historical archive only and are not maintained. An
     idea from them that is useful later (voxel traversal for fog or lighting, for instance) is written fresh as new
     code; none is a reason to keep anything on main now.
   - This removes the main cost of order C in stage 1.7 (porting or breaking the raymarcher during phase 5). The order
     of phases 3 and 5 is checked again and goes to Lucy with the phase 2 decisions.
2. **Testing and tuning for low-end hardware wait until after phase 6.** The aim is to shorten the switch to the
   raster pipeline: most low-end gains come from work done anyway (the draw order, trees as objects, the resolution
   change), so a low preset tuned now would mostly be redone.
   - The draw-order fix stays (decision 3).
   - The settings that scale the cost stay configurable, not hard-coded: MSAA samples, the cascades' count and
     resolution, `MeshReachShare`, a cap on lights, the render scale, and the mesh heap's size. The fixed 1 GB
     `MeshPass.RequestedHeapBytes` becomes an option (since done: `Engine:Renderer:MeshMegabytes`). No presets are
     built or tuned around them yet.
   - At the end of each phase the benchmark runs once on the integrated GPU (`PreferredDevice=RAPHAEL`) as a check for
     large regressions, and the numbers go in this file. Nothing is tuned; anything much worse is flagged.
   - Deferred: the runs on a downclocked 5700 XT, the firmware carve-out test, tuning a low preset for Raphael, the sky
     field per vertex as an option, and enforcing a mesh memory budget.
   - The bar's integrated GPU criterion (60 fps at 1280x720 on Raphael) is retired for now.
   - A leaning, not a decision: the eventual low-end target is probably a GTX 1650 / RX 570 class card at 1920x1080 as
     the store page's minimum, with the Steam Deck as a named target. Design choices should not rule these out, but
     nothing is optimised for them yet.
3. **The draw-order fix is the first item of phase 3.** The camera's block draws are sorted nearest first each frame,
   as the stage 1.7 prototype did (`ITERA_PROTO_ORDER`), written properly on main; no depth pre-pass. The earlier
   stages' raster numbers were taken in upload order, so the benchmark runs again on the 5700 XT afterwards and becomes
   phase 3's baseline. The fix does not depend on phase 2's answers and goes ahead before them.
4. **Decisions are Lucy's, asked for explicitly.** Before any phase 2 decision takes effect, every item on the phase 2
   list is put to her with its options, their trade-offs and the measurements behind them, and a recommendation with
   its reasons. Recommendations in this file are not decisions. During the work, anything that affects the game's look
   or feel, performance targets or budgets, file, save, edit-log, or network protocol formats, player-facing settings,
   the removal or reduction of a feature, scope or schedule by more than a day or so, or anything hard to reverse goes
   to her first, questions grouped where possible; routine choices are recorded here as they are made. Her answers are
   recorded in this section with their date.

### Phase 2 answers (2026-10-06)

Asked with each option's trade-offs and measurements and a recommendation; the alternatives are named in brackets.

1. **The raymarcher leaves main now**, after the draw-order fix, and the particles' world collision (phase 4's item) is
   brought forward to follow it, so only the materials go flat in the meantime. (Not: removing it without collision;
   keeping it until phase 3's data-independent work is done.)
2. **The look prototype D runs first**: a density made from the resident 1 cm voxels near the camera, meshed at 5 and
   10 cm, beside today's 8 cm occupancy meshes. The voxel size and density are decided from its pictures.
3. **Phases 5 before 3** (stage 1.7's order C, updated now that the raymarcher no longer has to keep working): D, the
   raymarcher out, the unit split, phase 5, then phase 3 whole on the new world, the rest of phase 4, and phase 6. Debug
   views of the wireframe and the detail levels come before phase 5. (Not: B, phase 3 split round phase 5; A, as first
   planned.)
4. **The models' voxel unit is split from the world's right after the raymarcher leaves**, so its uses of the shared
   unit are deleted, not adapted. (Not: before everything; at phase 5's start wherever it falls.)
5. **The world reaches 5 or 10 cm by a base spacing**: coordinates stay in centimetres, a level-k voxel is the base
   times 2^k cm, and the voxel store takes its base as a parameter, so models keep 1 cm. (Not: `VoxelsPerMetre` of 10 or
   20 with every centimetre number rescaled.)
6. **The mesher stays Surface Nets.** (Not: Dual Contouring; Marching Cubes with Transvoxel.)
7. **Seams between detail levels are stitched** (transition cells), built during or after phase 5; no skirts. The specks
   of sky between levels stay until then. (Not: skirts now.)
8. **Trees, bushes and undergrowth, rocks, and loose props become objects** in phase 6; large structures, walls,
   paths, and cliffs stay terrain. (Not: trees only; trees and undergrowth first.)
9. **Objects break as their own small voxel volumes**, dug and cut as characters' parts are; a trunk cut through falls
   as a loose body. (Not: pre-fractured pieces; whole objects with hit points.)
10. **Particles collide with the camera's depth image**: they meet everything drawn, characters and loose bodies too,
    and nothing off screen or hidden; lit by the cascades and the sky field. (Not: a coarse occupancy texture round the
    camera; both.)
11. **Anti-aliasing is 4x MSAA by default**, SMAA when MSAA is off, and the Graphics tab's Anti-aliasing row becomes a
    choice of Off, SMAA, MSAA 2x, 4x, and 8x. No TAA is planned. (Not: an on/off row; planning TAA.) Measured costs are
    under "Phase 3, item 1" below.
12. **Shadows: two cascades with a blend band where they meet, and contact-hardening soft shadows** (sharp where a
    caster meets the surface, softer farther from it, as the raymarcher's 2-degree sun was). (Not: the blend band
    alone; three cascades; three cascades with contact-hardening.)
13. **Materials: the raymarcher's surface recipes, ported to the mesh shader per pixel**, with their distance fades,
    are phase 3's first material prototype; baked texture arrays if they cost too much. (Not: baked arrays first; flat
    colours with value and edge shading; authored textures.)
14. **Characters keep 1 cm part volumes, drawn with coarser levels**: level 1 from the camera and level 2 past 8 m, the
    player's own arms and held items at level 0. (Not: today's levels; 2 cm part volumes.)
15. **The size of giant models is decided when they are worked on**; until then no new code caps a model's size.
    (Not: planning 30 to 60 m giants now.)
16. **The view reaches the map edge in phase 6**, with simplified far meshes, impostors, and large structures and giants as
    silhouettes. (Not: 256 m; 512 m.)
17. **Walkers stay on the surface for now**: phase 5 keeps one floor per navigation cell, and layered navigation waits
    for underground play. (Not: layered navigation in phase 5.)
18. **The world pass's budget is 4 ms** on the RX 5700 XT at 2560x1440 with shadows and materials, models extra, in
    every benchmark view, checked at the end of each phase; anything over goes to Lucy. (Not: 3 ms; 6 ms; no number.)
19. **A soft mesh memory target of 512 MB** in every benchmark view by the end of phase 6, reported at each phase's
    end and not enforced. (Not: no number yet; 256 MB.)
20. **The sky field's read gets lighter now**: no apron, uniform blocks filled without decoding, spread over frames,
    and a rebuild every 32 m of travel instead of 16; right after the particles' collision. Summaries kept with the
    columns wait for phase 7. (Not: the summaries now; nothing until phase 7.)
21. **Raw materials are counted in cubic centimetres** after phase 5, so a 10 cm voxel adds 1,000 and prices, amounts,
    build costs, and saved inventories stay as they are. (Not: counted in the new voxels.)
22. **The world's floor moves to the next whole chunk at or below today's -40.96 m**: -44.8 m at 5 cm, -51.2 m at
    10 cm. (Not: about -100 m; -38.4 m.)
23. **The brief's other underground questions** (mining yields, cave-ins, darkness, the sky field for the canopy) wait
    until underground play is taken up.

The voxel size (5 or 10 cm) and density (stored or worked out from occupancy) waited for prototype D's pictures (below).
The order of work from here: the raymarcher leaves main; the particles' depth collision; the sky field's lighter read;
the look prototype D, and with its pictures the voxel size and density; the unit split; debug views of the wireframe
and the detail levels; phase 5; phase 3; the rest of phase 4; phase 6.

### After look prototype D (2026-10-06)

Asked with the prototype's pictures and numbers ("Look prototype D" under Measurements); my recommendation was taken
each time.

1. **The world's voxels are 10 cm** (level k is 10 times 2^k cm): 0.62 times today's triangles at today's reaches, an
   eighth of the voxels 5 cm would store for generation, edits, saves, streaming, and the mover. The flagstone joints,
   wall courses, and small stones that 5 cm kept as shapes come back as phase 3's per-pixel materials. (Not: 5 cm.)
2. **The density is stored** (phase 5's item 8: a density block in surface bricks, edits that write coverage, generator
   shapes that give distances, save format 7, a protocol bump), for the smooth ground, bushes, and digs of the
   pictures. (Not: worked out from occupancy when meshing.)
3. **The sky field is also built again after 16 m of vertical travel**, beside the 32 m in any direction, so the box's
   top stays at least about 11 m above the view. (Not: 32 m every way.)
4. **The sky field's read keeps sharing the upload's 2 ms of block reading**: mesh jobs start in about 1 ms a frame
   while a read goes on. (Not: meshing keeps its full 2 ms beside it.)

Routine choices made along the way (2026-10-06):

- The draw order sorts the camera's world blocks by the squared distance from the camera to each block's centre; the
  shadow lists and the models' commands keep their order. The commands are gathered in managed arrays and copied into
  the mapped list once, as reading mapped memory back is slow.
- The mesh heap's option is `Engine:Renderer:MeshMegabytes` (64 to 4096, 1024 by default), the plan's name for it; the
  shadow maps' size became an option at the same time, `Engine:Renderer:ShadowMapTexels` (512 to 4096, 2048 by
  default), since it was one constant. The cascades' count and a cap on the lights become options when phase 3 builds
  them.

## The frame today

As it was before the redesign; the frame since the raymarcher left main is described in `.claude/rules/rendering.md`.
The render thread records, in `FrameRenderer.Record` (`src/Itera.Engine/Rendering/FrameRenderer.cs:365`) and
`RecordWorld` (`:495`):

1. Voxel copies from staging into the voxel heap (`VoxelPass.RecordUploads`), when the frame has any.
2. The particles' spawn and simulate passes (`particle-spawn.comp`, `particle-simulate.comp`, which reads the voxel
   heap).
3. The shadow casters' buffers flushed (`ShadowCasterGrid.Flush`).
4. The swapchain image to colour attachment.
5. Unless the loading screen covers it, the world:
   1. the target chosen: SMAA's scene image, FSR 1's source, or the swapchain image;
   2. the world pass (`VoxelPass.Record`): `voxel-beam.comp`, `voxel-trace.comp`, `voxel-normals.comp`,
      `voxel-shadow.comp` and `voxel-shadow-smooth.comp`, then `voxels.frag`, which also writes the per-pixel depth
      buffer, the centre-ray probe, and the light history;
   3. the depth copy (`depth-resolve.frag` into `DepthTarget`), when models, particles, or debug lines follow;
   4. the models (`ModelPass`): boxes drawn into a hit image (`model-hits.frag`), then `model-light.frag` over it;
   5. particles (cubes, then sprites), debug lines, SMAA, and FSR 1.
6. The loading screen, a screenshot without the UI, the UI, a screenshot with it, and present.

## What depends on the raymarcher

The inventory written before the redesign started. Everything in it marked "retired" left main on 2026-10-06
(stage "The raymarcher leaves main" below); it is kept as the record of what went.

### Retired with it

| Group | Files | Lines |
|---|---|---|
| C# in `Rendering/Voxels/` | `VoxelPass`, `ModelPass`, `ModelInstanceBuilder`, `ScreenBuffers`, `VoxelGpuEncoder`, `VoxelGpuFormat`, `VoxelGpuHeap`, `VoxelGpuLayout`, `VoxelGpuMirror`, `VoxelGpuModels`, `VoxelGpuUploader`, `VoxelHistory`, `VoxelProbeCheck` | 4,805 |
| `Rendering/GpuData/` | `ModelInstance`, `VoxelProbe` | 64 |
| Shaders | `voxel-beam.comp`, `voxel-trace.comp`, `voxel-normals.comp`, `voxel-shadow.comp`, `voxel-shadow-smooth.comp`, `voxels.frag`, `depth-resolve.frag`, `voxel-traversal.glsl`, `voxel-tree.glsl`, `voxel-data.glsl`, `voxel-hits.glsl`, `voxel-history.glsl`, `model-data.glsl`, `model-hits.glsl`, `model-hits.frag`, `model-light.frag`, `models.vert` | 2,694 |
| Tests in `tests/Itera.Engine.Tests/Rendering/` | `GpuRayTracer`, `GpuRayTracer.Tree`, `GpuRayTracerTests`, `GpuRayTracerConstantsTests`, `GpuVoxelReader`, `FakeGpu`, `ModelPassTests`, `ModelTraceTests`, `SurfaceGroupTests`, `VoxelGpuEncoderTests`, `VoxelGpuFormatTests`, `VoxelGpuHeapTests`, `VoxelGpuLevelsTests`, `VoxelGpuMirrorTests`, `VoxelGpuModelsTests`, `VoxelHistoryTests`, `VoxelProbeCheckTests`, `ZeroDirectionTests`, `ScreenBuffersTests` | 5,983 |

`VoxelGpuHeap`'s bitmap range allocator was general: it moved to `Rendering/Memory/GpuRangeHeap` for the mesh buffers,
with its tests, in stage 1.2.

### Kept, with changes

| What | Change |
|---|---|
| `FrameRenderer.RecordWorld` | A second branch for the raster passes; the rest of `Record` stays. |
| `FrameExchange` (`RenderFrame`) | `VoxelCopies`, `ModelsOutside`, `ModelsInside`, and `RenderFrameRequest.MostScale` go; `ShadowCasters` goes with the capsules. |
| `RenderView`, `ViewConstants`, `ViewSwitches` | `DebugView` values 1 to 6, `HistoryFrame`, `HistoryBlend`, frame blending, `SmoothNormals`, and `TextureFiltering` go; `Shadows` changes meaning; the camera, sun, sky, air, size, and scale stay. |
| `GpuTimings` (`GpuPass`) | `Upload`, `Depth`, `Rays`, `Normals`, `Shadows`, `ModelHits` go; `Voxels` and `Models` become the world and model raster passes; new passes for mesh uploads and shadow cascades. |
| `DepthTarget` | Becomes the real depth attachment (it is filled by a copy today). |
| `DeviceRequirements` | `fragmentStoresAndAtomics` is needed only by the voxel shader's writes. |
| `Memory/GpuMemoryAllocator.CreateImage` | Only single-sample 2D images with one mip and one layer: needs samples, mips, and layers. |
| `Pipelines/PipelineFactory` | No vertex input, no MSAA, no depth-only pipelines: needs them. |
| `Memory/StagingBuffer`, `Voxels/StagingWriter` | General in shape; only the voxel path uses them today. |
| `ParticlePass` and its shaders | Bind `VoxelPass`'s descriptor set (`ParticlePass.cs:171,354,469`); `particle-simulate.comp` collides and finds sunlight through the heap; `particle-cubes.frag` reads the capsule casters. Need their own world collision and lighting. |
| `ShadowCasters`, `ShadowCasterGrid`, `shadow-casters.glsl` | Bound inside `VoxelPass`'s set (bindings 9, 10, 12). Replaced by shadow maps in phase 4. |
| `voxel-surface.glsl`, `voxel-lighting.glsl`, `view.glsl` | The recipes, fog, tone curve, grade, and ambient light are ported; the parts tied to the normal box go. |
| `VoxelLook`, `IVoxelPalette`, `IVoxelTintMap`, `VoxelTintSampler`, `VoxelMaterial` | Material colours, tints, and glow carry over; `IVoxelPalette.Group` (surface groups) served the smooth normals. |
| `DynamicRenderScale` | Its upper limit (`mostScale`) comes from the raymarcher's per-pixel buffers. |
| `ModelInstances` | Its draw list stays; its reader (`ModelPass.Write`) is replaced. `MaxModels` is 16,384. |
| `DebugLinePass`, `particle-*.vert` | Project with `view.right`, `up`, and `forward` and the reverse depth; they work as they are against a real depth image. |

### Kept as they are

The Vulkan context, swapchain, frame sync, render thread, and frame exchange; the allocator's blocks and memory
types; `UiPass`, `LoadingPass`, `SmaaPass`, `SmaaTextures`, `UpscalePass`, `FrameImages`, `RenderScale`; screenshots;
the shader compiler and reloads; `GpuTimer`; `Atmosphere` and `sky.glsl`; `DebugLines`; the control port; and outside
rendering, `VoxelChangeLog`, `VoxelWorld.Raycast` (about 25 gameplay callers), and the CPU side of `VoxelModels`
(wounds, physics, hit tests).

### Couplings outside the renderer

Gameplay waited on GPU uploads: a wounded character part kept its kind's model until its own copy was uploaded, loose
bodies' retirement waited for the mirror, the game's loading tasks waited for models, the world loading task and the
benchmark's `IsLoaded` waited for the uploader, and the match had a "world on the gpu" step. The voxel uploader was the
only caller of `IVoxelPalette.Prepare`. The options check capped `Engine:Voxels:Levels` by the GPU format and checked
the GPU's column windows. Model trees lived in the world heap's last 32 MB, and the model shader traced a sun ray
through the world's voxels. No shader used a projection matrix: every vertex shader projects with `view.right`, `up`,
and `forward` and writes reverse depth (0.01 m over the distance, an infinite reverse-Z projection), so meshes could do
the same. Character levels followed the world's reaches. Tools read log text (`tools/benchmark/parse.py` reads
`FrameStats.LogGpuPasses` and the voxel GPU lines, `flight.py` the heap lines, `compare.py` keys such as `voxels_ms`, and
the control tool's `gpu` prints the `GpuPass` names). Five shaders drew the aim dot. Some tests borrowed helpers from
retired ones, and two scanned the whole `Shaders/` folder. Agent guidance named the centre-ray probe's
`GpuBuffer.Invalidate` as one of the two Vulkan calls the main thread may make.

### Options

| Option (`Engine:Renderer`) | After the swap |
|---|---|
| `PreferredDevice`, `Validation`, `SyncValidation`, `BestPracticesValidation`, `SkyRed`, `SkyGreen`, `SkyBlue`, `DebugLines`, `ShaderSourceDirectory`, `AntiAliasing` | Kept. |
| `RenderScale`, `DynamicRenderScaling`, `DynamicScalingThreshold` | Kept; the per-pixel buffer limit goes. |
| `Shadows` | Kept; turns the shadow maps on and off. |
| `UploadMegabytesPerFrame` | Kept for mesh uploads; `WindowsFit` goes. |
| `VoxelGpuMegabytes`, `ModelVoxelMegabytes`, `DebugView` (values 1 to 6), `FrameBlendSeconds`, `SmoothNormals` | Removed. |
| `TextureFiltering` | Removed as built (band-limited recipes); mipmaps take its place. |

The `Engine:Voxels` options `Levels`, `StreamingRadiusMetres`, `Level0ReachMetres`, and `ReachGrowth` set both
streaming and the GPU windows today. The console's `view` and `blend`, the `settings` command's `smoothing` and
`filtering`, and the Graphics tab's Smooth normals and Texture filtering rows go with them; `shadows`, `scale`, `aa`,
and `lines` stay.

### Tests kept with changes

The tests of the render passes, the options and their checks, the settings and the menu, the stats and GPU timings, the
shadow caster grid (until the capsules went), the model extraction, and the system order were kept with changes. The
others left with the raymarcher.

## What a world resolution change touches

- **Levels are sampled, not averaged.** `ColumnJob.Generate` fills every level from the generator, each voxel tested at
  its centre (`VoxelConstants.SampleAt`); edits do the same per level. A feature thinner than a level's voxel vanishes
  there, and the generator works round it (`SurfaceDepthAt`, `Flora.CoarsestLevel`, debris buried 16 cm).
- **Levels are powers of two in centimetres.** Chunk keys, coordinate-to-index conversions in `VoxelWorld` and its
  partial files, `EditLog`'s index, `VoxelEditing` (24 uses of `SampleAt`), `VoxelRows`, and 13 generator shape files
  shift by the level. 5 or 10 cm needs either a base spacing in those conversions (coordinates stay in centimetres) or
  `VoxelsPerMetre` of 20 or 10 with every centimetre constant rescaled.
- **`VoxelsPerMetre` is also the models' unit**: character, item, and head models share `ChunkTree`, `VoxelMemory`,
  and `VoxelEditing` with the world. The two units must be split first.
- **A voxel is one material byte with no spare bits.** A density value needs a new memory pool, a rule for uniform
  blocks, a new `BrickCodec`, a new edit-log format, and a new protocol version.
- **Bounds**: the map is `MinChunkXZ` -391 to `MaxChunkXZ` 390 (±500.48 m, also `Ground.MapHalfWidth`); the world is
  `MinChunkY` -32 to `MaxChunkY` 63 (-40.96 m to 81.92 m); chunks are 128 voxels a side.
- **Streaming is horizontal**: `HorizontalDistanceSquared` (`VoxelWorld.cs:1115`) uses x and z only, and every column
  loads its full height.
- **Readers of level 0**: the mover (`VoxelCharacterMover`, 1,405 lines, its slope and ledge rules tuned to 1 cm steps
  and its strip 128 cells), `VoxelColliders` (32-voxel cells, coarse level 2), lifts and pieces (`CatchUpLevel0`), the
  navigation scanner, `CountRemoved`, foot placement, streaming centres' discs, `Foliage`, and tool rays.
- **Navigation** has one floor per cell (`NavGrid`) and is built from the layout, not from voxels.
- **Edits smaller than 5 cm**: the game's strikes (2 to 3 cm), a rifle's bore (2 cm), and a laser (4 cm). Generator
  features thinner than 5 cm: flower stalks, bark (3 cm), moss (4 cm), mortar joints, bones, pebbles, thin branches.
- **Counts of removed material** were in level-0 voxels (`IVoxelRemovals`).
- **Pinned hashes** of the generator, the navigation grid, and the voxel workers had to be pinned again. Hashes of
  character poses and pieces do not depend on the world's resolution.

## Docs and guidance changed at the swap

Done when the raymarcher left main (2026-10-06), not at a later swap: the docs that described only the raymarcher left,
those that partly did lost those sections, `ARCHITECTURE.md` was rewritten in place (about 4,040 lines to 3,650, with a
new bullet on the raster pipeline), and the rules files, skills, agent guidance, and `tools/benchmark/` scripts
followed (`parse.py`, `compare.py`, and `flight.py` read the new log lines; `shaderstats.sh` went).

## Ambient occlusion methods

Moved from `docs/ambient-occlusion.md` when it left main (2026-10-06; the archive's branch `raymarcher` keeps the whole file:
the trial in the raymarcher's shadow pass, its measured costs and noise, and what a second attempt should do). The
comparison was written for the raymarcher; with meshes and a depth buffer, screen-space methods cost less to add, and
the sky-visibility field (stage 1.5) already covers the coarse grid's row.

### The methods compared

The research covered papers, engine documentation, and frame breakdowns (sources at the end). Costs from other engines
are on other hardware and give only a rough scale.

| Method | Occluders off screen (a canopy overhead) | Holes, overhangs | Digging and building | Verdict |
| --- | --- | --- | --- | --- |
| Screen-space: SSAO, HBAO, GTAO / XeGTAO, visibility bitmasks | No: uses only what is on screen; looking ahead, the canopy is above the frame | A hole seen from above, yes; changes as the view turns | Free | Not as the main method |
| Short rays through the voxel tree (Teardown; ray-traced obscurance) | Yes, within the rays' reach | Yes | Free: the tree is the data | Recommended |
| Cone marching on coarse levels (voxel cone tracing, NVIDIA VXAO) | Yes | Yes | Needs each coarse cell's filled share, kept up to date | Fallback |
| Distance fields (Unreal's distance-field AO) | Yes | Yes | Rebuilt on edits (jump flooding 2.4 ms for 128³, 22 ms for 256³) | No |
| Top-down height map (CryEngine's height-map AO) | As a soft layer | No: one height per column | Updated on edits | Perhaps later, for far forests |
| Coarse sky-light grid (Minecraft-style fill, 25-50 cm cells) | Yes | Coarse | New data, refilled near edits and new columns | Perhaps later, for the canopy |
| Light probes with visibility (DDGI) | Yes | No: probes 2-3 m apart | Re-traced | No |

Notes on each:
- **Screen-space.** XeGTAO (Intel, MIT) costs 0.56 ms at 1080p on an RTX 2060 at its high preset; it relies on TAA for
  its temporal part, which Itera does not have. Large radii (2-3 m) cost more and halo round thin things (grass,
  branches, limbs). Its one real advantage: bodies would darken the world and be darkened by it with no extra work.
- **Teardown** (frame breakdowns by Acko and by Montoya): 2 cosine-weighted rays per pixel, a bounded distance with
  steps that double through coarser mips, ambient strength from how far the ray went, blue noise with Roberts' R2
  sequence, a 12-tap bilateral blur, and a temporal blend with the history clamped to the new frame. Gustafsson's 2018
  prototype did AO, lighting, fog, and reflections in about 9 ms at 1080p on a GTX 1080.
- **Obscurance** (Zhukov et al. 1998): what a ray hits takes less light the farther away it is, so open ground keeps its
  light and the darkening fades with distance. Short rays are enough.
- **Cone marching** needs the filled share of each coarse cell. Itera's coarse voxels are solid when their centre is,
  and node masks say only "something here", so cones would over- or under-darken without new data.
- **Bent normals**: rays that escape show which way is open, and could weight sky light against ground light. Cheap once
  rays exist.

### Sources

- Acko, "Teardown Frame Teardown": https://acko.net/blog/teardown-frame-teardown/
- Montoya, "Teardown Teardown": https://juandiegomontoya.github.io/teardown_breakdown.html
- Gustafsson, "From screen space to voxel space" (2018): https://blog.voxagon.se/2018/10/17/from-screen-space-to-voxel-space.html
- Intel XeGTAO (costs and limits): https://github.com/GameTechDev/XeGTAO
- Therrien, Levesque, Gilet, "Screen Space Indirect Lighting with Visibility Bitmask" (2023): https://arxiv.org/html/2301.11376v2
- Jimenez et al., GTAO (2016): https://research.activision.com/publications/archives/practical-real-time-strategies-for-accurate-indirect-occlusion
- Unreal, Distance Field Ambient Occlusion: https://dev.epicgames.com/documentation/en-us/unreal-engine/distance-field-ambient-occlusion-in-unreal-engine
- Unreal, Lumen technical details: https://dev.epicgames.com/documentation/en-us/unreal-engine/lumen-technical-details-in-unreal-engine
- RTSDF, jump-flood distance fields: https://arxiv.org/html/2210.06160v1
- Crassin et al., voxel cone tracing (2011): https://research.nvidia.com/publication/2011-09_interactive-indirect-illumination-using-voxel-cone-tracing
- NVIDIA VXAO: https://developer.nvidia.com/vxao-voxel-ambient-occlusion
- CryEngine Height Map AO: https://www.cryengine.com/docs/static/engines/cryengine-3/categories/1114113/pages/21890652
- Minecraft wiki, Light: https://minecraft.wiki/w/Light
- Lysenko, "Ambient occlusion for Minecraft-like worlds": https://0fps.net/2013/07/03/ambient-occlusion-for-minecraft-like-worlds/
- Majercik et al., DDGI (2019): https://www.jcgt.org/published/0008/02/01/
- Wolfe et al., spatiotemporal blue noise (2022): https://arxiv.org/html/2112.09629v1
- Zhukov, Iones, Kronin, "An ambient light illumination model" (1998): https://www.semanticscholar.org/paper/An-Ambient-Light-Illumination-Model-Zhukov-Iones/94496ee631237f285db5939fd69967f3290be04e

## Measurements

Each stage's numbers go here, against the bar in the decisions above.

### Stage 1.1: the option and the raster frame (2026-10-06)

`Engine:Renderer:Pipeline=Raster` runs a match without the voxel GPU heap (768 MB), `VoxelPass`, the depth copy,
`ModelPass`, the shadow caster grid, or the centre-ray check. `WorldMeshes` takes the uploader's other jobs (the
palette, models counted as uploaded, retired models removed, `IWorldOnGpu` for the loading screen and the benchmark),
and `MeshPass` clears the depth image and draws the sky. Particles run with `NO_VOXEL_WORLD`. Where the plan put the
allocator's and pipeline factory's new image and pipeline kinds in this stage, they come with the stages that use
them (1.2 and 1.3), and new GPU pass names likewise; the raster world is timed as the `Voxels` pass, so the benchmark
tools compare the two pipelines' world as they are.

- The sky alone: 0.055 ms a frame at 1280x720 on the RX 5700 XT.
- Validation (`validate.sh`, both standard profiles, each pipeline): no message. Raster: debug lines, the upscale at
  0.75, particles moved and drawn, a screenshot, and 13 pipelines reloaded. Raymarch: unchanged (22 pipelines).

### Stage 1.2: Surface Nets terrain (2026-10-06)

The raster pipeline now draws the world as meshes: 32-voxel blocks read with a 2-voxel apron on the main thread
(`VoxelWorld.TryReadBlock`: 16 µs a surface block in Release), meshed on 4 worker threads by naive Surface Nets
(0.155 ms a sloped block in Release), and uploaded into a 1 GB mesh heap; one indexed indirect draw covers every block
in view. Flat colours with the tint map, sun without shadows, sky and ground light with per-vertex openness, fog, tone
curve, and grade. The mesher and the block reader were written by writer agents.

What the first runs showed, at the start view at 1920x1080 (world pass, triangles drawn, mesh heap):

| Levels drawn | World | Triangles | Heap |
|---|---|---|---|
| The raymarcher's reaches, five levels (8 cm to 107 m) | 3.0 ms | 10.5 M | over 1 GB (full) |
| A quarter of the reaches, five levels | 2.73 ms | 26.8 M | 604 MB |
| A quarter, seven levels (32 and 64 cm far) | 0.86 ms | 5.0 M | 374 MB |
| An eighth, seven levels | 0.60 ms | 3.0 M | 218 MB |

The raymarcher's reaches keep a voxel about a pixel wide, which for a mesh means triangles a pixel wide; an eighth
keeps 8 cm triangles 6 to 8 pixels wide at 1440p (`Engine:Renderer:MeshReachShare`, 0.125 by default). The two extra
coarse levels (`Engine:Voxels:Levels=7`) cut the far triangles; `Engine:Renderer:ViewDistanceMetres=256` keeps the fog
where five levels put it, so the pictures compare. Levels were first chosen by a column's centre, which left a coarse
column drawn right next to the camera; they are chosen by its nearest point now. Only columns that can be drawn are
meshed. The world generator divided by zero at level 5 (a hollow pocket whose crust filled it); fixed without changing
levels 0 to 4.

Benchmark, release build, 2560x1440, render scale 1, world pass in ms (`r12-raster` with seven levels, view distance
256 m, an eighth of the reaches; `r12-raymarch` the same build):

| View | Raymarch | Raster (no shadows yet) |
|---|---|---|
| start | 8.50 | 0.95 |
| start-low | 8.39 | 0.93 |
| forest | 9.37 | 1.00 |
| forest-down | 7.15 | 0.38 |
| forest-up | 9.51 | 1.14 |
| canopy | 8.99 | 1.09 |
| trail | 10.05 | 0.82 |
| flight | 7.01 | 1.01 |

The integrated GPU (`PreferredDevice=RAPHAEL`), drawn at 1280x720 (render scale 2/3 on the 1920x1080 screen), world
pass in ms: raymarch 52.9, 51.3, 62.7, 32.9, 58.5, 56.8, 70.5 over the seven views; raster 6.8, 6.5, 8.6, 2.1, 10.5,
9.0, 5.4. FSR 1's upscale to 1080p took 4.0 ms there besides; stage 1.3 measures a native 720p window.

The pictures show the same shapes as the raymarcher's, without its surface recipes or shadows, and with a few specks of
sky between levels of detail (nothing joins the levels yet).


### Stage 1.3: sun shadows and MSAA (2026-10-06)

The raster world now takes the sun's shadows from two cascades and is drawn with 4x MSAA.

- Cascades: two 2048x2048 depth maps in one array. Each is a square round the bounding sphere of a slice of the view
  (0 to 30 m and 30 to 200 m of view depth), snapped to whole texels so a still world point keeps its place in its
  texel, and reaching 100 m further towards the sun (`ShadowCascades`, written by a writer agent, 19 tests).
  `mesh.frag` picks the cascade by view depth, moves the point 1.5 texels along its normal, and blends 3x3 comparison
  lookups.
- Casters: the same block meshes, drawn by a depth-only pass from the same heap and draw table, one indirect draw per
  cascade. Three things brought the maps' cost down (forest view, 1080p, release build):

  | Change | Shadow maps |
  |---|---|
  | Every block in each cascade's box, both faces | 3.55 ms (1440p benchmark) |
  | Only faces turned away from the sun, and only blocks that can shade the cascade's slice (the slice's planes that face the sun) | 1.44 ms |
  | The far cascade's blocks one level coarser than the camera's | 1.27 ms |

  The faces turned away from the sun are enough because the meshes are closed: a ray from a shaded point to the sun
  enters the solid through such a face. A lit surface then never meets its own depth in the map: with the bias and
  the normal offset set to 0, no acne showed. Even so the maps take 16 M triangles at that view (2,270 near and 2,598
  far blocks); back faces are culled before they are rasterised, which is why they cost little.
- MSAA: 4 samples a pixel (`Engine:Renderer:MsaaSamples`, 1, 2, 4, or 8), resolved into the frame's target (the mean)
  and the depth image (sample 0) by dynamic rendering. SMAA does not run on a multisampled raster world. Particles and
  lines draw after the resolve. Vulkan counts a depth resolve as a colour attachment write, so the depth image's
  frame-start barrier waits for and on that stage (sync validation found it).
- Validation: no message with the raster pipeline (sync and best practices).

Benchmark, release build, 2560x1440, render scale 1, world pass with shadows in ms (`r13b-raster`; `r13-raster` was the
first build, before the three changes above):

| View | First build | Shadow maps | Final build | Shadow maps | Main pass |
|---|---|---|---|---|---|
| start | 4.15 | 1.75 | 2.56 | 0.60 | 1.96 |
| start-low | 4.07 | 1.76 | 2.51 | 0.59 | 1.92 |
| forest | 5.88 | 3.55 | 3.40 | 1.34 | 2.06 |
| forest-down | 4.61 | 3.56 | 2.79 | 1.75 | 1.04 |
| forest-up | 6.01 | 3.49 | 3.83 | 1.64 | 2.19 |
| canopy | 5.76 | 3.42 | 2.84 | 0.52 | 2.32 |
| trail | 4.21 | 2.39 | 2.74 | 0.86 | 1.88 |
| flight | 5.07 | 3.10 | 2.66 | 0.73 | 1.92 |

Against the bar: under 6 ms everywhere (the checkpoint), under the strict 3 ms in six of eight (forest 3.40, forest-up
3.83). MSAA and the shadow lookups added about 1 to 1.2 ms to the main pass (stage 1.2: 0.38 to 1.14 ms).

The integrated GPU, drawn at 1280x720 (render scale 2/3 on the 1080p screen; FSR 1's 4.0 ms upscale to 1080p is left
out, as a native 720p window has none), world pass in ms:

| View | 4x MSAA, shadows | Shadow maps | Main pass |
|---|---|---|---|
| start | 19.17 | 6.06 | 13.11 |
| start-low | 18.47 | 6.00 | 12.46 |
| forest | 28.00 | 11.65 | 16.35 |
| forest-down | 20.26 | 15.41 | 4.85 |
| forest-up | 31.95 | 13.91 | 18.04 |
| canopy | 23.63 | 4.89 | 18.73 |
| trail | 21.16 | 7.69 | 13.47 |
| flight | 21.92 | 7.02 | 14.90 |

Two cheaper settings on the integrated GPU, world pass in ms (`r13-igpu-nomsaa`: no MSAA, so SMAA runs, 0.5 to 1.0 ms
besides; `r13-igpu-coarse`: also `MeshReachShare` 0.0625, every mesh one level coarser):

| View | No MSAA | Shadow maps | Main pass | Also coarser meshes | Shadow maps | Main pass |
|---|---|---|---|---|---|---|
| start | 14.10 | 6.10 | 8.00 | 13.14 | 5.08 | 8.07 |
| start-low | 13.72 | 6.09 | 7.64 | 12.84 | 5.04 | 7.80 |
| forest | 20.54 | 11.58 | 8.96 | 19.93 | 8.90 | 11.03 |
| forest-down | 18.34 | 15.50 | 2.84 | 14.90 | 12.00 | 2.89 |
| forest-up | 24.38 | 14.06 | 10.33 | 23.02 | 10.86 | 12.16 |
| canopy | 15.72 | 4.89 | 10.83 | 14.84 | 3.78 | 11.06 |
| trail | 15.99 | 7.81 | 8.18 | 12.16 | 5.59 | 6.58 |
| flight | 15.68 | 6.94 | 8.75 | 12.48 | 5.74 | 6.73 |

What these show:

- The integrated GPU misses the strict bar's 60 fps (16.7 ms a frame) in every view with shadows, in most views even
  without MSAA. MSAA costs it 5 to 8 ms; the shadow lookups cost it little (its main pass without MSAA is close to
  stage 1.2's 2.1 to 10.5 ms).
- Its shadow maps take 3.8 to 15.5 ms, about ten times the discrete GPU's: they are bound by triangles (4 to 6.5 M a
  frame there), and a mesh one level coarser takes off only a quarter.
- Coarser meshes do not shorten its main pass: it is not bound by triangles there. The forest views draw layer on
  layer of leaves in no order, so most of their fragments are shaded and then hidden; drawing the blocks nearest
  first, or a depth pre-pass, is the likely fix.
- Without shadows and MSAA, the world pass on the integrated GPU is stage 1.2's 2.1 to 10.5 ms: 60 fps holds there.

Departures from the plan and points left open:

- Particles and debug lines draw after the resolve, tested against sample 0's depth, not into the multisampled image as
  the plan said: they are not anti-aliased, and where they cross the world the line is stepped. Phase 4, which gives
  particles their world collision again, can move them before the resolve.
- The integrated GPU was measured at render scale 2/3 on the 1080p screen with the upscale's time left out, not in a
  native 1280x720 window (the benchmark script makes the window full screen).
- The shadow bias (`ShadowDepthBias` 0.0003 of the cascade's depth, `ShadowNormalTexels` 1.5) was set before the maps
  took only faces turned away from the sun. With that, a bias of 0 and a normal offset of 0.5 texel showed no acne at
  the forest and canopy views and looked the same; the values stay until a look test. (2026-10-12: the depth bias went,
  `docs/voxel-objects.md`.)
- The cascades meet at 30 m without a blend. Casters more than 100 m beyond a slice towards the sun are clipped, not
  clamped, so a low sun loses far hills' shadows. PCF lookups at the square's outer corners can read past it (lit).

### Stage 1.4: a meshed crowd (2026-10-06)

The raster pipeline now draws the models: characters' parts, far flipbook frames, loose bodies, and items.

- Meshing: each level of each model is cut into 32-voxel blocks and meshed as the world is, on the same workers,
  before the world's blocks. A block meshes only the faces whose lower voxel is in it, so a model's blocks start one
  voxel before its box; without that, the faces on its three low sides were missing (a test checks that a model's
  blocks make one closed surface). A replaced part (a wound) draws its old meshes until the new ones are in.
- Cost: the kinds built at start (1,070 models in a match with the benchmark's crowd) took the Characters loading
  task from 2.0 s to 3.0 s, under the world's 6.4 s, and 80 MB of the mesh heap.
- Drawing: the frame's draws (about 19,000 for 1,000 goblins) are grouped by model and level with a counting sort, and
  each block of a level is one indexed indirect command whose instances are that level's draws, so 1,000 goblins are
  about 90 commands. They cast into both cascades. `ModelInstances.MaxModels` went from 16,384 to 32,768 and
  `BenchmarkOptions.MostCrowd` from 800 to 1,500.
- Level policy: `Game:World:CharacterLevelZeroMetres` and `CharacterLevelOneMetres` (debug options; unset, the world's
  reaches of about 13 and 27 m, as the raymarcher used) set where parts go from level 0 to 1 and 1 to 2.

Benchmark, release build, 2560x1440, 1,000 goblins (`--Game:Benchmark:Crowd=1000`), world pass in ms. "No models" is
the same build with model drawing switched off by a temporary switch for this run, so the crowd's creeps are simulated
in both:

| View | No models | Today's levels | Added | Model triangles | Coarser levels | Added | Model triangles |
|---|---|---|---|---|---|---|---|
| crowd-near | 1.28 | 2.54 | 1.27 | 4.83 M | 1.73 | 0.45 | 1.45 M |
| crowd-far | 1.48 | 2.18 | 0.71 | 2.15 M | 2.08 | 0.60 | 1.70 M |

"Coarser levels" is no level 0 and level 1 to 8 m (`CharacterLevelZeroMetres=0`, `CharacterLevelOneMetres=8`). With
today's levels, the model draws in the main pass took 0.94 ms in crowd-near and their shadows 0.39 ms. Both policies
are well under the strict bar's 4 ms; the pictures of the two are hard to tell apart at 1440p, the near goblins a
little softer with the coarser one.

The crowd runs drew the forest views 0.35 to 0.4 ms slower than stage 1.3's run, with models off as well. The same
build without the crowd (`r14a-raster`) matched stage 1.3's numbers, so the 1,000 simulated creeps made the frames wait
on the main thread and the GPU, idle between frames, ran at a lower clock: a view's GPU time is only comparable between
runs with the same CPU load. The world's vertex outputs were packed into three slots meanwhile (the offset with the
openness, the normal, the material with the tint; four slots in stage 1.3): 0.1 ms less in the start views, no change
in the forest (`r14b-raster`: world with shadows 2.44 to 3.81 ms over the seven views).


### Stage 1.5: underground (2026-10-06)

- A benchmark view `tunnel`: 6 m into the enemy fortress's first gate at eye height, looking along its 28 m tunnel into
  the hall under the tower.
- The sky field (`SkyField`): how much of the open sky each 64 cm cell of a 128 x 64 x 128 m box round the view sees.
  Level-6 voxels are read from the resident columns on the main thread (about 10 ms in Release; spread over frames
  since, see "The sky field's lighter read"); a worker spreads the sky from air under open sky through air, keeping 0.85
  a cell (about a third 4 m in), brightest first (about 18 ms); solid cells take their brightest air neighbour's value,
  so a surface reads the air in front of it. It is built again when the view is 16 m from its middle (32 m since the
  lighter read) or 2 s after an edit, staged whole in one frame, and copied into an R8 3D texture that `mesh.frag`
  samples once a pixel to scale sky and ground light (no less than 0.02). A first version kept the field in the mesh
  heap and blended its eight cells by hand: 0.29 ms at 1080p against the texture's 0.10 ms.
- Point lights: each world block's glowing vertices (embers, lava, glow caps) make one light at their glow-weighted
  middle, found when its mesh is uploaded. Each frame the lights within 48 m whose reach meets the view are ranked by
  power over distance squared and the 32 brightest go to `mesh.frag`, which loops over them at every pixel. 64 cost
  0.7 ms at 1080p in the fortress's hall, where windows, crown, lava, and braziers all glow; 32 cost about half that.
  The power scale (glowing area times glow times 400) was set by eye so that the hall's braziers light the floor
  round them red; at 40 they showed nothing on the hall's near-black lining.
- Validation: no message, shadows on and off (the shadow map's layers are now always cleared, and the field's texture
  cleared to open sky in its first frame, which best practices asked for).

What the pictures show (sent to Lucy): in a shaft dug with the debug tools into the players' plateau, the field darkens
the walls with depth and the far end goes black, where without it the shaft was evenly lit. The fortress's tunnels show
little difference, since the generator already lines them near-black in three steps to fake the darkness (phase 7 makes
them plain stone). The hall's braziers light the floor round them.

Cost, release build, 2560x1440, two runs of each build taken in turn (`r15-ab-*`), world pass in ms:

| View | Stage 1.4 | Stage 1.5 | Change |
|---|---|---|---|
| start | 2.58 | 2.62 | +1.3 % |
| forest | 3.60 | 3.75 | +4.4 % |
| forest-up | 4.06 | 4.24 | +4.4 % |
| canopy | 2.96 | 3.11 | +5.0 % |
| trail | 2.77 | 2.85 | +3.1 % |
| tunnel | - | 2.25 | |

The forest views differ by up to 0.4 ms between runs of one build, so single runs mislead: the first pair taken showed
+15 %. Points left: the main thread's 10 ms read at each rebuild (a hitch every 16 m of travel; spread over frames,
every 32 m, since "The sky field's lighter read"); lights are one per
block, so a lava fall is many and far braziers lose to nearer glowing blocks; no light casts shadows; clustering the
lights is phase 3's, and the field's update after edits and its reach (128 m) are phase 7's.

Review fixes after the stage (a reader's): a field finished while the loading screen covered the frame was staged but
never copied (its copy was recorded with the world, which the loading screen skips), so the shader read an empty or old
field under the new field's corner; it is now recorded with the frame's other copies. A failed build is tried again
after 2 s, not every frame; only a column of the read level loading asks for a rebuild; and a world needs level 5 for a
field (with level 4 the read would copy about 9,000 blocks on the main thread).

### Stage 1.6: results of phase 1 (2026-10-06)

The spike's numbers against Lucy's strict bar, from the final build (`r16-*`, release, two raster runs averaged; the
raymarcher measured on the same build). All raster runs use `--Engine:Renderer:Pipeline=Raster
--Engine:Voxels:Levels=7 --Engine:Renderer:ViewDistanceMetres=256`.

**1. The world with shadows on the RX 5700 XT at 2560x1440, at most 3 ms over the seven views: missed in three.**

| View | Raymarch | Raster | Shadow maps | Main pass |
|---|---|---|---|---|
| start | 8.53 | 2.63 | 0.60 | 2.03 |
| start-low | 8.45 | 2.57 | 0.60 | 1.97 |
| forest | 9.39 | 3.56 | 1.34 | 2.22 |
| forest-down | 7.17 | 2.84 | 1.75 | 1.09 |
| forest-up | 9.52 | 4.01 | 1.65 | 2.36 |
| canopy | 9.00 | 3.02 | 0.52 | 2.50 |
| trail | 10.06 | 2.89 | 0.86 | 2.03 |
| tunnel | 7.57 | 2.26 | 0.50 | 1.76 |
| flight | 7.00 | 2.90 | 0.75 | 2.15 |

The raster world is 2.4 to 3.5 times faster than the raymarcher everywhere, with sun shadows from two cascades, 4x
MSAA (the raymarcher's SMAA is 0.2 to 0.4 ms besides), the sky field, and point lights. Forest (3.56), forest-up
(4.01), and canopy (3.02) are over 3 ms. The forest views vary by up to 0.4 ms between runs of one build.

**2. 1,000 near goblins add at most 4 ms: passed.** They added 1.27 ms in crowd-near and 0.71 ms in crowd-far (stage
1.4, against the same build with models off); with today's levels crowd-near draws 4.83 M model triangles, 2.51 M of
them at level 0, 1.73 M at level 1, and 0.59 M at level 2 (crowd-far: 0.65 M at level 1, 1.49 M at level 2). A
coarser policy (level 1 from the camera, level 2 past 8 m) brought crowd-near's cost to 0.45 ms with pictures hard to
tell apart at 1440p. The final build's crowd-near world pass is 2.74 ms (models 1.02 ms, shadow maps 0.49 ms).

**3. The integrated GPU at 1280x720 holds 60 fps: missed by far.** World pass (drawn at 1280x720, render scale 2/3 on
the 1080p screen; FSR 1's 4.0 ms upscale to 1080p left out), in ms:

| View | World | Shadow maps |
|---|---|---|
| start | 20.2 | 5.9 |
| start-low | 19.4 | 5.9 |
| forest | 34.3 | 11.7 |
| forest-down | 21.1 | 15.5 |
| forest-up | 40.8 | 14.0 |
| canopy | 26.6 | 4.9 |
| trail | 23.2 | 7.8 |
| tunnel | 14.4 | 4.5 |

These are one run. Against stage 1.3's build, two runs of each taken in turn (`r16-igpu-13*`,
`r16-igpu-16*`), the final build is slower on the iGPU: start +30 % (19.2 to 24.9 ms), forest +17 % (27.7 to 32.4),
forest-up +18 % (32.1 to 37.8), canopy, trail, and the flight +11 to 13 %, forest-down +2.5 %; the shadow maps are
unchanged. In a running game at forest-up the sky field's lookup cost the iGPU 2.75 ms and the lights 0.05 ms; the rest
is not explained. The final build's runs spread much more than stage 1.3's (start 22.5 to 27.3 ms), so CPU work that
shares the chip's power with the iGPU (sky field floods, light gathering) is the first suspect; stage 1.4's shader
changes are the other. (Stage 1.7 found neither: the difference was the order the blocks were drawn in, which changes
from run to run.) Stage 1.3 measured the rest of the time there: 4x MSAA 5 to 8 ms, the shadow maps 4 to 15 ms
(bound by triangles), and the main pass otherwise near stage 1.2's 2.1 to 10.5 ms. Without MSAA and shadows the iGPU
held 60 fps in stage 1.2. Lucy put a low setting for weak GPUs off until after phase 1.

**4. A tunnel darkens convincingly with a crude sky field: passed in a dug shaft** (pictures in stage 1.5). The
fortress's own tunnels change little: the generator lines them near-black to fake the darkness.

**Outcome:** the strict bar is missed (three forest views over 3 ms, and the iGPU), the brief's bar is passed (the
world under 6 ms everywhere, a crowd of 1,000 adding 1.27 ms to a 2.7 ms frame, not doubling it). By the plan, the
numbers and pictures go to Lucy to decide.

Meshing and memory:

- A block: read 16 µs on the main thread, meshed in 0.155 ms on a worker (release, stage 1.2); 4 workers.
- Models: the kinds built at start (1,070 models) took the Characters loading task from 2.0 to 3.0 s.
- An edit: a 2 m dig, which the world applies over a few fixed steps, has each step's blocks meshed and staged within
  3 to 11 ms (2 to 5 frames); the whole dig shows within about three fixed steps (50 ms).
- Mesh heap use at the start view with seven levels and the crowd's kinds: about 530 MB of its 1 GB, against the
  voxel heap's 768 MB (the voxel world's CPU memory is unchanged).
- Main thread: `WorldMeshes.Upload` about 0.1 to 0.3 ms a frame when nothing loads (2 ms at most while it reads blocks),
  and a 10 ms read at each sky field build (every 16 m of travel; at most about 1 ms a frame, every 32 m, since "The
  sky field's lighter read").

The pictures (sent to Lucy, raymarch left, raster right, the eight views): the same shapes, shadows, and light; the
raster world is flat colours without the raymarcher's surface recipes (phase 3's materials). Visible on the raster
side: vertical stripes on the stone walls (normals from occupancy on 8 cm meshes of 1 cm detail), blocky patches where
the flagstone road's stones should be (the material per vertex at 8 cm), and specks of sky between levels of detail
(no seams yet).

Open for phase 2 and after, besides Lucy's decisions (phase 2's list):

- Materials and normals (phase 3): flat colours, wall stripes, flagstone patches, Stan's faint voxel steps.
- Seams between levels (specks of sky); the drawn level by 3D distance.
- Shadows: bias tuning, a blend at the cascades' meeting, far casters clipped past 100 m.
- Lights loop over 32 at every pixel and are one per block: clustering (phase 3).
- The sky field: a 10 ms main-thread read per build (spread over frames since "The sky field's lighter read"), 128 m
  reach, 2.75 ms on the iGPU.
- Particles: no world collision, drawn after the MSAA resolve (phase 4).
- Retired models stay drawn while any world block is pending (phase 4).
- The iGPU: far over its budget; a low setting (no MSAA, one cheap cascade, coarser meshes) waits for Lucy.
- Raster runs need `Levels=7` and `ViewDistanceMetres=256` on the command line.

### Stage 1.7: investigations before phase 2 (2026-10-06)

After an outside review of phase 1's results, Lucy asked for a short round of measurements and proposals before the
phase 2 decisions: explain the integrated GPU's slowdown, make the sky field cheaper, find where the forest views' time
goes, and prepare three items for the decision list (the low-end hardware target, the order of phases 3 and 5, and the
models' voxel unit), with a note on the mesh heap. Nothing here decides an item on that list.

How it was measured: release builds; `tools/benchmark/run.sh` with the raster arguments of stage 1.6 and a 20 s flight;
two runs of every variant, the variants of a set taken in turn. The integrated GPU at 1280x720 (render scale 2/3 on
the 1080p screen, the FSR 1 upscale left out as before), the RX 5700 XT at 2560x1440. The variants ran from a
prototype build kept off main (a detached worktree): environment switches for the draw order, a depth
pre-pass, the sky field per vertex or off, the lights off, and one cheap cascade, and log lines that count each list's
triangles by material class, the mesh heap by level, and the sky field read's blocks. The stage 1.3 build
was given the same draw-order switch.

**1. The integrated GPU's slowdown: the draw order, not the stages.** Stage 1.6 compared two runs of stage 1.3's build
with two of the final build and found the final build 11 to 30 % slower, with the sky field lookup (2.75 ms) the only
known cost. A bisect over the four stage builds, two runs each in turn, found no step between builds; instead the same
build differed by up to 6 ms between runs, while each view held within 0.1 ms through its own 8 s. World pass, ms, the
two runs' mean and half their spread:

| Build | start | forest | forest-up | canopy | trail |
|---|---|---|---|---|---|
| 1.3, stage 1.6's runs | 19.19 ± 0.74 | 27.65 ± 0.32 | 32.06 ± 0.39 | 23.77 ± 0.65 | 21.72 ± 0.05 |
| 1.3, again | 21.47 ± 2.36 | 30.21 ± 2.24 | 34.91 ± 2.67 | 24.54 ± 0.25 | 21.96 ± 0.12 |
| 1.4 | 24.18 ± 0.26 | 30.14 ± 3.03 | 35.02 ± 3.24 | 24.68 ± 1.27 | 22.08 ± 0.44 |
| 1.5 | 21.89 ± 0.80 | 30.01 ± 0.60 | 34.68 ± 0.74 | 25.46 ± 0.60 | 23.12 ± 0.25 |
| 1.6, stage 1.6's runs | 24.91 ± 2.39 | 32.35 ± 2.23 | 37.77 ± 2.84 | 26.49 ± 0.39 | 24.28 ± 0.33 |
| 1.6, again | 21.11 ± 1.47 | 31.94 ± 2.35 | 37.20 ± 3.08 | 25.95 ± 0.46 | 23.57 ± 0.44 |

The suspects stage 1.6 named do not hold:

- **CPU work and the chip's power.** A sample a second of the iGPU through the runs: whenever it was busy its clock
  was 2200 MHz, its highest (2 samples of 689 were changing state), at 17 W on average and 55 °C at most. Nothing
  throttled. The sky field's floods take 22 to 113 ms on a worker, every 2 s at most.
- **Stage 1.4's shader changes.** The triangles drawn and the shadow maps' time were the same in both builds; the
  difference between runs is larger than any between builds.

The cause is the order the camera's blocks are drawn in. A column's blocks are drawn in the order their meshes were
uploaded (`MeshColumn.Drawn`, with removals filled from the end) and the columns in the order they loaded
(`_byLevel`), both of which change from run to run with the workers' timing. In the forest the leaves of the blocks
in front then may or may not be drawn before those behind them, so the hidden leaves are shaded or rejected by the
depth test, a different share in each run. Sorting the camera's list by distance each frame (nearest first, the
prototype's `ITERA_PROTO_ORDER`, an `Array.Sort` of at most a few thousand commands) fixed it:

| iGPU, world pass, ms | start | forest | forest-down | forest-up | canopy | trail | tunnel |
|---|---|---|---|---|---|---|---|
| Final build, upload order | 22.20 ± 0.77 | 34.25 ± 0.22 | 20.90 ± 0.03 | 40.02 ± 0.35 | 27.58 ± 0.30 | 23.69 ± 0.33 | 14.94 ± 0.48 |
| Nearest first | 15.31 ± 0.18 | 22.59 ± 0.30 | 19.93 ± 0.11 | 25.83 ± 0.25 | 16.84 ± 0.41 | 18.38 ± 0.12 | 13.90 ± 0.11 |
| Nearest first, sky field off | 14.22 ± 0.10 | 21.84 ± 0.29 | 19.59 ± 0.02 | 25.01 ± 0.12 | 16.12 ± 0.02 | 17.54 ± 0.07 | 13.61 ± 0.01 |
| Nearest first, lights off | 15.07 ± 0.04 | 22.81 ± 0.06 | 19.94 ± 0.00 | 26.17 ± 0.08 | 16.56 ± 0.52 | 18.44 ± 0.00 | 11.18 ± 0.04 |
| Stage 1.3's build, nearest first | 14.47 ± 0.22 | 21.55 ± 0.01 | 19.50 ± 0.10 | 24.84 ± 0.14 | 16.12 ± 0.28 | 17.32 ± 0.00 | - |

- Nearest first takes a third off the leafy views (forest 34.3 to 22.6 ms, forest-up 40.0 to 25.8, canopy 27.6 to
  16.8) and runs agree within 0.4 ms. The shadow maps are unchanged (11.6 ms at forest), so the main pass at forest
  went from 22.7 to 11.0 ms.
- With both builds drawn nearest first, the final build without the sky field matches stage 1.3's within 0.3 ms. What
  stages 1.4 to 1.6 added is the sky field's lookup, 0.3 to 1.1 ms, and the point lights, 2.7 ms in the tunnel view
  (the hall's braziers and lava; 0.2 ms at start, nothing in the forest). The 2.75 ms stage 1.6 measured for the
  lookup was in upload order, so it included the lookups of hidden leaves shaded and then covered.
- The 1.3 build's faster runs in stage 1.6 were most likely runs whose load order happened to suit those views.

A finding on the way, for items 4a and 5: the iGPU here has a 512 MB carve-out of system memory as its own; the game
asks for 1.32 GB of device-local memory (the 1 GB mesh heap among it), and the kernel keeps 276 MB of it in the carve-out
and 1.26 GB in GTT, system memory the GPU reaches through its page tables (`/proc/<pid>/fdinfo`). Every vertex the
iGPU draws is read from there.

**2. The sky field, cheaper.**

Per vertex. The prototype samples the field in `mesh.vert` (`textureLod`, half a cell along the normal as before) and
passes the value in the spare fourth component of the normal's output, so a vertex exports no more than before;
`mesh.frag` uses it in place of its own lookup. World pass, ms, nearest first:

| | start | forest | forest-up | canopy | trail | tunnel |
|---|---|---|---|---|---|---|
| iGPU, per pixel | 15.31 | 22.59 | 25.83 | 16.84 | 18.38 | 13.90 |
| iGPU, per vertex | 14.88 | 21.89 | 25.25 | 16.47 | 18.02 | 13.73 |
| iGPU, no lookup | 14.22 | 21.84 | 25.01 | 16.12 | 17.54 | 13.61 |
| 5700 XT, per pixel | 2.04 | 2.84 | 3.15 | 2.02 | 2.34 | 2.19 |
| 5700 XT, per vertex | 2.03 | 2.82 | 3.14 | 1.99 | 2.30 | 2.16 |
| 5700 XT, no lookup | 1.99 | 2.78 | 3.10 | 1.97 | 2.28 | 2.14 |

The lookup per pixel costs the iGPU 0.3 to 1.1 ms; per vertex 0.05 to 0.66 ms, most at start and trail, where much of
the view is far ground of many small triangles. On the 5700 XT it is 0.05 to 0.06 ms per pixel and 0.02 to 0.04 ms per
vertex.

Look (pictures sent to Lucy: per pixel, per vertex, and no field, side by side, at the mouth, the middle, and the end of
the shaft dug as in stage 1.5, and in the tunnel view): at the shaft's mouth and middle and in the tunnel, per vertex
and per pixel give the same picture (a mean difference of 0.1, 0.6, and 0 of 255). At the shaft's end, looking up,
where its walls are within a metre or two of the camera, per vertex shows darker blotches on the wall (a mean
difference of 5 of 255; 1 % of pixels differ by more than 44): each vertex samples half a cell (32 cm) out along its
own normal, and the normals of these occupancy meshes are noisy, so neighbouring vertices sample different cells,
where per pixel samples with the interpolated normal. A shorter offset or density normals (phase 5) would likely
remove it (not tried). Without the field the shaft is lit evenly to its end, as stage 1.5 found.

What a value per vertex means when the field changes but a block's mesh does not:

- Sampled in the vertex shader, as the prototype does, there is nothing to refresh: every frame reads the field as it
  is, so a rebuild (every 16 m of travel then, 32 m since; 2 s after an edit) shows on every block at once.
- Stored in the vertex beside the openness the mesher computes, it would be stale on every block meshed before the
  field's last rebuild, which is most of them. A vertex's three words have no spare bits (x and y; z, material, and
  openness; the normal), so it would take a fourth word or a separate byte stream, and a refresh after each rebuild:
  either meshing every block in the box again (thousands of blocks at 0.155 ms each, every 16 m), or a small compute
  pass that samples the field for the vertices of the blocks in the box and writes their bytes. Storing saves the
  vertex shader a texture fetch, which measured at most 0.66 ms on the iGPU; not worth a refresh path.

Moving the rebuild's read off the main thread. The read takes 10 to 16 ms on the main thread in a running game: the
level-6 blocks of the resident columns, each copied with `VoxelWorld.TryReadBlock`'s 2-voxel apron (36³ samples for
32³ cells). At the start view it reads 196 blocks, of which 91 are all solid and 10 all air. Three ways:

1. A lighter read, spread over frames: no apron, uniform blocks filled without decoding (about half of them), and a
   slice of the box per frame within about 1 ms. An edit that lands mid-read leaves the field mixed until the rebuild
   2 s later. The main thread then pays about 1 ms in each of a few frames instead of 10 to 16 ms in one. Hours of
   work.
2. Occupancy summaries kept with the columns: the column job that generates a level-6 column on a worker also writes
   its occupancy, one bit a cell (128 x 128 x 192 cells, 390 KB a column); an edit updates the bits of its box where it
   already writes voxels on the main thread, copying the column's summary first (copy on write); the main thread hands
   the flood's worker references to the summaries in the box. The main thread then does nothing per rebuild. Two or
   three days; also the base for phase 7's field rebuilt locally after edits.
3. A worker reading the chunk trees itself would need a lock round the world, which the main thread changes every step
   (edits, streaming): not proposed.

Rebuilding every 32 m of travel instead of 16 (the box is 128 m wide) would also halve the rebuilds.

**3. Where the forest views' time goes.**

Draw order and a depth pre-pass (the camera's blocks drawn once into depth only, then shaded where the depth is equal,
with no depth writes; `invariant gl_Position` keeps the two passes' depths the same). World pass, ms:

| | start | forest | forest-down | forest-up | canopy | trail | tunnel |
|---|---|---|---|---|---|---|---|
| iGPU, upload order | 22.20 | 34.25 | 20.90 | 40.02 | 27.58 | 23.69 | 14.94 |
| iGPU, nearest first | 15.31 | 22.59 | 19.93 | 25.83 | 16.84 | 18.38 | 13.90 |
| iGPU, pre-pass | 17.82 | 24.68 | 20.06 | 28.82 | 18.88 | 18.74 | 14.94 |
| iGPU, nearest first and pre-pass | 18.26 | 24.90 | 20.16 | 29.01 | 18.87 | 18.88 | 15.06 |
| 5700 XT, upload order | 2.78 | 3.56 | 2.84 | 4.01 | 3.03 | 2.89 | 2.26 |
| 5700 XT, nearest first | 2.04 | 2.84 | 2.73 | 3.15 | 2.02 | 2.34 | 2.19 |
| 5700 XT, pre-pass | 2.49 | 3.37 | 2.81 | 3.84 | 2.54 | 2.63 | 2.37 |
| 5700 XT, nearest first and pre-pass | 2.36 | 3.16 | 2.79 | 3.53 | 2.32 | 2.57 | 2.37 |

- Nearest first, the 5700 XT's world is under 3 ms in every view but forest-up (3.15 ms): forest 3.56 to 2.84 ms,
  canopy 3.03 to 2.02, start 2.78 to 2.04. The shadow maps did not change (1.31 to 1.73 ms in the forest views), so
  they are now about half of the leafy views' time there. This is a measurement of the prototype, not a change on
  main, and nothing else was tuned for 3 ms.
- The pre-pass helps less than the order on both GPUs, and with the order it makes things worse: its second vertex
  pass over every block costs more than the hidden shading it saves once the blocks come nearest first. On the iGPU,
  which is short of vertex throughput, it loses 2.0 to 3.2 ms against the order alone in the leafy views.
- The 5700 XT's runs agreed within 0.02 ms in every variant, nearest first or not.
- The pictures are the same: the screenshots of the ordered and pre-pass runs match upload order's in every view
  (`imgdiff.py`: no pixel over its threshold, 2 of 255 at most), so the sort drops nothing.

The shadow maps by source. Each block's triangles were counted by the material of their first vertex, in five groups:
canopy and foliage (leaves, needles, bush leaves, flowers, mushrooms), trunks (bark, wood, dead wood), ground (grass,
soil, trail, moss, gravel, clay, ash, buried stone and bone), rock, and structures (masonry, flagstones, the walls, the
tower, the tunnels, iron, embers, lava). Triangles listed into each cascade before back faces are culled (about half
are), millions, and the share of the canopy and foliage:

| View | Camera | Near cascade | Far cascade | Foliage: camera, near, far | Trunks | Ground |
|---|---|---|---|---|---|---|
| start | 3.23 | 2.05 | 5.51 | 69 %, 51 %, 68 % | 4-6 % | 16-20 % |
| forest | 3.54 | 7.14 | 9.75 | 68 %, 90 %, 81 % | 4-9 % | 6-22 % |
| forest-down | 0.48 | 9.12 | 13.78 | 6 %, 83 %, 76 % | 6-13 % | 11-81 % |
| forest-up | 4.30 | 7.98 | 12.65 | 76 %, 86 %, 78 % | 6-7 % | 8-16 % |
| canopy | 3.46 | 1.30 | 4.92 | 74 %, 98 %, 75 % | 1-8 % | 1-18 % |
| trail | 2.65 | 4.02 | 6.61 | 47 %, 74 %, 68 % | 10-17 % | 16-34 % |
| tunnel | 1.38 | 2.93 | 3.61 | 0 % | 0-1 % | 0-9 % (structures 90-100 %) |

Rock is under 2 % everywhere; structures are 5 to 27 % at start (the players' wall) and nothing in the forest. The
canopy and foliage make three quarters to nine tenths of what the cascades draw in the forest, and most of what the
camera draws. They come from 1 cm leaf voxels meshed at 8 cm: porous, many-sided shapes. Trees as their own objects
(phase 6), with simple shadow shapes or levels of detail for their crowns, would take away most of the maps' work
there; the world at 5 or 10 cm with density (phase 5) acts on the ground and the trunks, a fifth of the maps'
triangles at most.
Not tuned further, as asked.

**4. For the phase 2 list.**

*a. The low-end hardware target.* Raphael's 2 compute units are below what other 3D games with shadows aim at. The
candidates (from the makers' and Valve's figures, gathered by a reader agent; FP32 from shader count and clock):

| GPU | FP32 | Memory bandwidth | Memory | Against the 5700 XT (FP32, bandwidth) |
|---|---|---|---|---|
| Raphael (this machine's iGPU) | 0.56 TFLOPS | about 83 GB/s, shared | 512 MB carve-out here | 0.06, 0.19 |
| Steam Deck (LCD, OLED) | 1.6 TFLOPS | 88, 102 GB/s, shared | 1 GB carve-out by default | 0.17, 0.20-0.23 |
| Radeon 680M, 780M | 3.7, 4.1 TFLOPS | about 90-100 GB/s, shared | system memory | 0.38-0.43, about 0.2 |
| GTX 1650 (GDDR5, GDDR6) | 3.0, 2.9 TFLOPS | 128, 192 GB/s | 4 GB | 0.29-0.31, 0.29-0.43 |
| GTX 1060 6 GB | 4.4 TFLOPS | 192 GB/s | 6 GB | 0.45, 0.43 |
| RX 570, RX 580 | 5.1, 6.2 TFLOPS | 224, 256 GB/s | 4-8 GB | 0.52-0.63, 0.50-0.57 |
| RX 5700 XT | 9.75 TFLOPS | 448 GB/s | 8 GB | 1, 1 |

Valve's bar for "Verified" on the Steam Deck is 30 fps at 1280x800 in the default settings. Comparable games' minimum
GPUs on their store pages: Teardown (a voxel game) a GTX 1060 with 4 GB, "integrated graphics cards are not
supported"; Enshrouded a GTX 1060 6 GB, an RX 580, or an Arc A380; Valheim a GTX 950; Lethal Company a GTX 1050. On
Steam's hardware survey (September 2026, read through a summarising fetch, so approximate) the GTX 1650 is the tenth
most common GPU at 2.2 %, and the cards at or below it together are about a tenth of users.

Options for the target:

1. A GTX 1650 / RX 570 class card at 1920x1080 and 60 fps on a low preset: the common old card, and about the minimum
   comparable games ask for.
2. The Steam Deck at 1280x800: 30 fps at least (Verified), 60 fps as the aim on a low preset.
3. Both, each with its own preset: option 1 as the minimum on the store page, option 2 as a Deck target.

In every option Raphael stays a floor: the game should start and play there at a low preset, not a target the presets
are tuned for.

What a target means for the settings: the measured costs above give the steps. MSAA (4x costs the iGPU 1.9 to 4.6 ms
at 720p, nearest first; off or 2x on a low preset, with SMAA 1x, 0.3 to 0.45 ms at 1440p on the 5700 XT, instead); the
cascades (two of 2048² on high; one of 1024² over 50 m costs the iGPU 2.1 to 7.0 ms against 4.5 to 15.4); the mesh
reach (`MeshReachShare` 0.0625 instead of 0.125 takes 4 to 27 % off the camera's triangles, 17 to 38 % off the near
cascade's, and a fifth to a quarter off the heap); the sky field per vertex; a cap on the lights; the view distance; the render
scale with FSR 1 (already there, with dynamic scaling); and the mesh heap's size (item 5).

The low preset for Raphael, measured (720p, nearest first, world pass in ms, two runs each):

| Preset | start | forest | forest-down | forest-up | canopy | trail | tunnel |
|---|---|---|---|---|---|---|---|
| As shipped (4x MSAA, two 2048² cascades) | 15.31 | 22.59 | 19.93 | 25.83 | 16.84 | 18.38 | 13.90 |
| No MSAA | 11.46 | 18.24 | 18.03 | 21.33 | 12.23 | 13.88 | 9.99 |
| No MSAA, one 1024² cascade to 50 m | 7.82 | 11.41 | 9.45 | 13.25 | 7.78 | 9.04 | 7.51 |
| That, the sky per vertex, and `MeshReachShare` 0.0625 | 7.10 | 9.05 | 7.19 | 9.88 | 6.26 | 6.84 | 6.51 |
| No MSAA, no shadows, `MeshReachShare` 0.0625 | 5.80 | 5.41 | 2.32 | 6.19 | 5.53 | 4.09 | 4.90 |

With the blocks drawn nearest first, no MSAA, and one cascade of 1024² over 50 m, every view is under 16.7 ms: Raphael
holds 60 fps at 720p (the 4 ms FSR 1 upscale to this machine's 1080p screen aside; a 720p window needs none). The
single cascade still draws 9 to 11 M triangles in the forest, four fifths of them leaves, which is why its map costs
5.7 to 7 ms there. The last preset (no sun shadows) is the "runs at all" floor.

What we can measure here, and what we have to estimate:

- Raphael: measured, as above.
- An RX 570-like card: the 5700 XT pinned to its 800 MHz shader clock and 500 MHz memory clock is about 4.1 TFLOPS and
  256 GB/s. That needs root (`power_dpm_force_performance_level` set to manual, `pp_dpm_sclk` and `pp_dpm_mclk` to
  level 1 on the 5700 XT's card): three commands with `sudo` before the runs and one after. A GTX 1650 cannot be matched: the
  5700 XT's lowest working shader level is 800 MHz on all 40 compute units.
- The Steam Deck, the 780M, and the GTX 1650: estimated only. Raphael does worse than its FP32 predicts (shared memory,
  a small cache, the mesh heap in GTT), so a Deck figure scaled from Raphael by FP32 (2.9 times) would be too hopeful;
  scaled by bandwidth (1.1 to 1.2 times) too gloomy. Raising this machine's carve-out in the firmware (512 MB to 2 GB)
  would show what keeping the heap out of GTT is worth, which bears on the Deck's 1 GB.

*b. Splitting or reordering phase 3.* Phase 3's items by whether they depend on the world's data:

- Independent of the voxel size and density: the draw order (above: a third of the iGPU's leafy views) and culling
  (GPU frustum, connectivity, a depth pyramid if needed); edits remeshed nearest first and their latency; the mesh
  memory option and eviction; the cascades as options, their blend and filtering; clustered lights and the light
  component; the material system (texture arrays, mipmaps, triplanar mapping, the tint map, glow after the grade);
  the anti-aliasing options; debug views; the loading screen; the Graphics settings.
- Partly dependent: cracks between levels (skirts work on any mesh; transition cells depend on the mesher and on
  density); the shadow bias (tuned again on the new meshes, a small job).
- Dependent: normals (density gradients replace the occupancy gradient that makes the stripes on stone walls), the
  material chosen and blended per vertex (the patchy flagstones), and the look pass on walls, flagstones, and Stan's
  voxel steps.

The orders, with what each costs, from "What a world resolution change touches":

- **A. As planned (3, 4, 5, 6).** Phase 3's normal and material tuning (perhaps 3 to 5 days of its 3 to 4 weeks) is
  done on meshes that phase 5 replaces and is partly redone. The world is seen at its final resolution only after
  phases 3, 4, and 5, 9 to 12 weeks from now.
- **B. Phase 3 split (3a, 4, 5, 3b, 6).** 3a is everything independent plus skirts (about 2.5 to 3 weeks); the swap
  (phase 4) follows; phase 5 then changes the world with no raymarcher left to keep working; 3b (about 1 to 1.5 weeks)
  does normals from density, material blending, the look pass, and transition cells if chosen. The total is the same
  as A less the redone tuning, and the debug views, culling, and memory budget of 3a are in place to judge phase 5 with.
- **C. Phase 5 forward, before the swap (after phase 2, or after 3a).** The world is seen at its final resolution sooner
  (about 2 weeks sooner than B if after 3a, 4 to 5 weeks if straight after phase 2), and phase 3's look work is done
  once. But the raymarcher is still on main until the swap and assumes the 1 cm unit and power-of-two levels: `voxel-data.glsl`'s 100 voxels a metre, `VoxelGpuFormat`'s 3-bit
  levels, `GpuRayTracer`'s CPU copy of the traversal, the model tracer, and `VoxelTintSampler`. It would be ported to
  the new world (about 1 to 2 weeks of work on code deleted weeks later), or `Raymarch` would be left broken during
  phase 5, which changes Lucy's decision that it stays until the raster pipeline draws everything it draws. Phase 5's
  own work (the mover, colliders, navigation scanner, generator, edits, saves, protocol, about twelve pinned hash
  classes) is the same in every order.
- **D. The decision forward, not the work.** A look prototype of 3 to 5 days: mesh, near the camera where the 1 cm
  voxels are resident, a coverage density made from them (the share of solid level-0 voxels in each 5 or 10 cm cell), with Surface Nets
  placing vertices by it and normals from its gradient, and put 5 cm and 10 cm density pictures beside today's 8 cm
  occupancy. Decisions 1 and 2 (voxel size, density) are then made with pictures of the real thing, and phase 5 keeps
  its place. It needs no change to the world's format.

My recommendation: B with D in phase 2. B costs nothing over A and removes the redone tuning; it keeps the swap before
the world changes, so no work goes into the raymarcher's format; D gives the voxel size and density decisions the
pictures the plan promised, which phase 1 did not make (it drew 8 cm only). And the draw order goes first in 3a.

*c. Separating the models' voxel unit from the world's.* It can be done early as its own step, in any order. A reader
agent mapped it; the main claims were checked against the code.

- `VoxelConstants.VoxelsPerMetre` has 168 uses (149 in `src`, 19 in tests): about 91 world only, 25 models only, 24
  where the two meet, 7 in raymarcher-only code. The storage code (`ChunkTree`, `VoxelMemory`, `BrickCodec`,
  `VoxelEditing`, `EditLog`) never names it: the coupling is in conversions between metres and voxels. About 40
  literals assume 1 cm without naming it (body plans' centimetres over 100, the skeletons' eye heights,
  `HitResolver`'s `* 100`, the navigation grid's `/ 100`).
- Where they meet, four places need real changes, not renames:
  - Digs, blasts, and lasers wound characters through `CharacterTargets.Cut(world, VoxelCoord centre, int radius,
    ...)`, a world voxel and a radius in voxels that is used unscaled in the part's space (`CharacterTargets.Wounds.cs`).
    `Cut` and `Reaches` should take metres.
  - `VoxelColliders` takes a model's base level as the first world level to try (`VoxelColliders.cs:287`).
  - `LooseBodies.Metres` serves both lifted world pieces and cut parts, and lifted pieces are added as models in world
    units (`LooseBodies.Pieces.cs:109`).
  - A model has no unit of its own: `WorldMeshes.Models.cs` and `ModelInstanceBuilder` derive its voxel size from the
    world's constant. `VoxelModels.Model` needs a metres-per-voxel field, for 5 or 10 cm alike.
- Size: about 45 files and 150 to 250 lines, mostly mechanical, 1 to 2 days with tests. It does not depend on 5 or 10
  cm, nor on whether coordinates keep centimetres. The character hashes (`PosedHumanHash`, `KeptForearmHash`,
  `AheadHash`, Stan's head) and the twelve world hash classes must not change, and the physics and character tests at
  the meeting points pin the rest. Done before the swap, it touches the raymarcher's seven uses too.

**5. The mesh heap.** The heap in use (world meshes by level, then the models' meshes and the fixed regions), from the
prototype's count at each view:

| View | World | Level 3 (8 cm) | Level 4 | Level 5 | Level 6 | Models and fixed |
|---|---|---|---|---|---|---|
| start | 278 MB | 66 MB, 2,327 blocks | 91 MB | 73 MB | 47 MB | 81 MB |
| forest (all four forest views) | 495 MB | 155 MB, 4,718 blocks | 124 MB | 115 MB | 100 MB | 81 MB |
| trail | 423 MB | 89 MB | 96 MB | 132 MB | 104 MB | 81 MB |
| tunnel | 177 MB | 75 MB | 43 MB | 32 MB | 25 MB | 80 MB |

The 530 MB of stage 1.6 was the start view with the crowd's kinds; the forest needs 576 MB without a crowd. What would
change it:

- Density at the same voxel size changes little: Surface Nets makes one vertex per cell the surface crosses, density
  or not. Smoother surfaces have somewhat less area than today's 8 cm meshes of 1 cm detail (the porous leaves above
  all).
- The voxel size does: 10 cm instead of today's 8 cm finest gives about 0.64 of the finest level's vertices; 5 cm, if
  drawn as far as 8 cm is today, about 2.6 times.
- Simplified far meshes: levels 5 and 6 hold 43 % of the forest's world meshes (215 MB), mostly gentle ground and
  crowns; a simplifying pass could cut them by half or more (an estimate). `MeshReachShare` 0.0625 instead of 0.125
  took the forest's world meshes from 495 to 392 MB and the start's from 278 to 206 MB.
- Trees as instanced objects: the canopy and foliage are about three quarters of the forest's triangles, so most of
  the forest's world meshes would go, replaced by a few meshes per kind of tree and level.

The heap was a fixed 1 GB request (`MeshPass.RequestedHeapBytes`; since then `Engine:Renderer:MeshMegabytes`, 1024 by
default). A 4 GB card holds it; the Steam Deck's carve-out is
1 GB by default and this iGPU's 512 MB, so there the heap lives in GTT, as measured above. A target for cards and
chips with less memory belongs on the phase 2 list, because it bounds the reaches, the levels, and how much of the
world the trees may take; phase 3's mesh memory option and eviction would then hold the heap to it.

**In short.**

- The iGPU's slowdown was the draw order: blocks drawn in the order they were uploaded, which changes from run to run.
  Drawn nearest first, runs agree, the leafy views lose a third of their time, and stage 1.3 and the final build differ
  only by the sky field's lookup (0.3 to 1.1 ms) and the lights (2.7 ms in the tunnel view).
- The sky field per vertex costs at most 0.66 ms on the iGPU against up to 1.1 ms per pixel, needs no refresh path when
  sampled in the vertex shader, and looks the same except for blotches on walls close to the camera. The read can leave
  the main thread by a lighter read spread over frames (hours) or by summaries kept with the columns (days).
- In the forest, three quarters to nine tenths of the shadow maps' triangles are leaves; a pre-pass does not pay on
  either GPU; nearest first brings the 5700 XT under 3 ms in every view but forest-up (3.15 ms).
- Raphael holds 60 fps at 720p with nearest first, no MSAA, and one 1024² cascade over 50 m.
- For Lucy: the low-end target (a, three options), the order of phases 3 and 5 (b, four orders; B with the look
  prototype D recommended), and the unit split (c, possible early, 1 to 2 days); and whether a memory target for the
  mesh heap goes on the list (5). To measure an RX 570-like card the 5700 XT's clocks need pinning with root; raising
  the iGPU's carve-out in the firmware would show what GTT costs.


### Phase 3, item 1: the draw order on main (2026-10-06)

The camera's world blocks are drawn nearest first (`WorldMeshes.WriteCameraNearestFirst`), as the stage 1.7 prototype
did; no depth pre-pass. With it, `Engine:Renderer:MeshMegabytes` and `Engine:Renderer:ShadowMapTexels` (Decisions,
routine choices). A test checks the order, and fails with the sort taken out. Validation (`validate.sh`): no message,
raster (with a 1024-texel shadow map and the viewer's models) and raymarch.

The new baseline for the phases that follow, release build, 2560x1440, render scale 1, the raster arguments of stage
1.6 and a 20 s flight, two runs (`p3-base-*`, which agreed within 0.01 ms), world pass in ms:

| | start | start-low | forest | forest-down | forest-up | canopy | trail | tunnel | flight |
|---|---|---|---|---|---|---|---|---|---|
| World | 2.03 | 2.01 | 2.84 | 2.73 | 3.14 | 2.02 | 2.33 | 2.19 | 2.33 |
| Shadow maps | 0.58 | 0.57 | 1.32 | 1.73 | 1.59 | 0.50 | 0.84 | 0.50 | 0.65 |

These match the prototype's nearest-first runs within 0.02 ms (stage 1.7). The main thread's time in
`WorldMeshes.Upload` while the views were measured was 0.12 to 0.35 ms a frame, as in the prototype's runs with the order
off; the sort's cost is within the frame-to-frame spread.

Measured for the phase 2 questions on anti-aliasing and shadows (one run each, `p3-msaa*`, `p3-noshadows`; world pass in
ms, SMAA's own pass added in):

| | start | forest | forest-down | forest-up | canopy | trail | tunnel |
|---|---|---|---|---|---|---|---|
| No anti-aliasing | 1.34 | 2.09 | 2.20 | 2.42 | 1.27 | 1.57 | 1.41 |
| SMAA (MSAA off) | 1.63 | 2.37 | 2.41 | 2.69 | 1.55 | 1.86 | 1.64 |
| MSAA 2x | 1.74 | 2.51 | 2.51 | 2.83 | 1.70 | 2.00 | 1.87 |
| MSAA 4x (the default) | 2.03 | 2.84 | 2.73 | 3.14 | 2.02 | 2.33 | 2.19 |
| MSAA 8x | 2.63 | 3.44 | 3.17 | 3.79 | 2.68 | 2.95 | 2.74 |
| 4x MSAA, no shadows | 1.40 | 1.47 | 0.93 | 1.54 | 1.43 | 1.41 | 1.65 |

- Over no anti-aliasing: SMAA 0.23 to 0.31 ms, MSAA 2x 0.40 to 0.46, 4x 0.53 to 0.78, 8x 0.97 to 1.41. The MSAA images
  take 59, 118, and 236 MB at 2560x1440 (four bytes of colour and four of depth a sample).
- The shadows cost 0.54 to 1.80 ms, almost all of it drawing the maps (0.50 to 1.73 ms); the lookups while shading cost
  0.01 to 0.09 ms, so a softer filter costs little and a cascade more costs triangles.
- With the default settings the start view's near cascade has a texel about 3.7 cm wide and the far one about 24 cm
  (90-degree view), so today's 3x3 filter of 2x2 comparisons softens edges by about 15 cm near and 1 m far.

### The raymarcher leaves main (2026-10-06)

Decision 1 and phase 2 answer 1: a commit deleted the raymarcher, about 17,400 lines. Gone: its passes,
shaders, GPU format, heap, mirror, uploader, history, centre-ray check, model tracing, capsule shadow casters, and their
tests (`GpuRayTracer` among them); the options `Pipeline`, `VoxelGpuMegabytes`, `ModelVoxelMegabytes`, `DebugView`,
`FrameBlendSeconds`, `SmoothNormals`, and `TextureFiltering` (settings.json drops the last two keys at its next save);
the console's `view` and `blend`; the Graphics tab's Smooth normals and Texture filtering rows; the render scale's cap
from the per-pixel buffers; the GPU timer passes of the depth copy, rays, normals, shadow rays, and model hits.

Routine choices made on the way:

- `Engine:Voxels:Levels` defaults to 7 and `Engine:Renderer:ViewDistanceMetres` to 256, as the raster runs used.
  Measured in a minute at the start view (release): 2,528 columns resident and 425 MB of the 1,024 MB voxel budget,
  against 2,149 and 370 MB with five levels; loading took 5.8 s against 6.6 s. `Levels` may now be 1 to 10
  (`VoxelConstants.MaxLevel` + 1); only up to 7 has run. `ModelExtractSystem` stops at the view distance too (seven
  levels reach 1,024 m).
- Models cast through the shadow maps. Under the raymarcher the first-person arms cast no shadow and the player's own
  body cast its capsules' shadow in first person (Lucy's choices); `ModelDraw.Shadow` (`ModelShadow`: `Casts`, `None`,
  `Only`) keeps both: the arms and what they hold are drawn and cast nothing, and the own body casts its shadow without
  being drawn (checked in the game, looking down from four sides).
- Particles meet no world and keep the sunlight they are made with until their depth-image collision (next).
- The particles' descriptor set is set 0 (it was set 1 over the raymarcher's set 0).
- Fog is worked out in metres (`lighting.glsl`, the old `voxel-lighting.glsl`), with the same result.
- The menu's Anti-aliasing row stays an SMAA switch, which only acts with `MsaaSamples` 1, until phase 3 makes it the
  choice of Off, SMAA, and MSAA 2x, 4x, 8x (phase 2 answer 11).
- The surface recipes' data stays (`VoxelLook`'s fields, the game's `WorldPalette`): phase 3 ports the recipes from
  the raymarcher's `voxel-surface.glsl` in the archive (phase 2 answer 13). The palette's surface
  groups, which only the smooth normals used, are gone.
- The device no longer needs `fragmentStoresAndAtomics`: no fragment shader writes a buffer now.
- The main thread's only Vulkan call while the render thread runs is the staging flush (the centre-ray probe's
  invalidate went with the probe).
- `tools/benchmark`: `run.sh` passes no removed option; `parse.py` reads the meshes line (MB uploaded, the heap,
  triangles drawn) instead of the voxel GPU line and keeps no centre-ray counts; `flight.py` follows the mesh heap and
  pending blocks; `shaderstats.sh` is gone.

Checks: the full suite passed (3,074 tests); `validate.sh` gave no message with the viewer's models and with the FSR 1
upscale at 0.75; the benchmark (`p3-norm-1`, 2560x1440, the RX 5700 XT) matched phase 3's baseline within 0.03 ms in
every view: start 2.03, start-low 2.01, forest 2.81, forest-down 2.73, forest-up 3.13, canopy 2.02, trail 2.33,
tunnel 2.19, flight 2.32 ms.

### Particles meet the world again (2026-10-06)

Phase 2 answers 1 and 10: the particles collide with the camera's depth image, brought forward to follow the
raymarcher's removal.

- The particles' compute passes run after the world's pass, in frames that draw the world; under the loading screen the
  particles wait (a cleared emitter still empties the pool at the next frame that runs). The depth image
  (`DepthTarget`, now also sampled) goes to the simulate pass and back (`RecordToSampled`, `RecordToAttachment`; after
  a multisample resolve a memory barrier makes the resolve's colour attachment write available first, as best
  practices asks).
- A moving particle that reaches a point just behind what the image shows (within 0.3 m and its step) is put back on
  that surface and bounces off it, sticks, or dies as its recipe says, with the surface's slope from the neighbouring
  depths (each axis's neighbour nearer in depth, so an edge takes the pixel's own surface). Off the image, behind
  something, or within 0.6 m of the camera along the view (the first-person arms are in the image) it meets nothing.
  A resting particle falls again where the image shows its support more than 10 cm behind it (dug away, or a body
  that left). It meets characters and loose bodies too, which the raymarcher's voxel collision did not.
- Lit particles take their light in the simulate pass, once a frame for those in view: the sun through the shadow
  cascades and the sky light from the sky field (`mesh-light.glsl`, shared with `mesh.frag`; `MeshPass`'s set bound as
  set 1, its layout and its barriers after the cascades and the sky field's copy now naming the compute stage), eased
  halfway each frame into two bytes of the particle's record, which the cube and sprite shaders read. Point lights are
  not added (phase 4's clustered lights).
- The particle push constants lost two fields no shader read (the time and the frame) and gained the view's right and
  up and the drawn size.

Checks: `ParticlePassTests` (the simulate pass reads set 0 and the mesh set as set 1, the lighting record's layout,
the push constants); `validate.sh` with about 237,000 particles from `particles stress blood-drops 4`, with 4x MSAA and
with one sample a pixel and SMAA at render scale 0.75: no message. Cost there (the RX 5700 XT): the simulate pass 0.16 ms
a frame, the draws 0.84 to 0.89 ms. In the game, debris, grit, and blood burst at the ground 2 m ahead lay on it after
2.5 s and stayed when the view turned.

### The sky field's lighter read (2026-10-06)

Phase 2 answer 20. The read of the sky field's voxels moved from one main-thread frame to many:

- `VoxelWorld.TryReadOwnBlock` reads a block's own 32³ voxels, without the 2-voxel apron the mesher's read copies (the
  field never used the apron). A block whose cell in its chunk's tree is one material or air writes nothing and is one
  fill of the field's cells; the others fill their uniform bricks and decode their stored ones.
- `SkyField.BeginRead` sets the box, clears its cells, and lists the blocks of the resident columns that cover it;
  `ContinueRead` reads listed blocks until a time limit, at least one a call. `WorldMeshes` gives it 1 ms of each frame
  (`SkyReadMilliseconds`), counted from the frame's first call, so the first frame's 1 ms includes the clear of the 4 MB
  of cells and the listing. The flood starts on a worker once every block is read.
- A rebuild comes after 32 m of travel instead of 16 (the box is 128 m wide and 64 m tall), and 2 s after an edit or a
  column of the read level loaded, as before.

Routine choices: the block list is made once at the start of a read, so a column that loads during the read is not
read (its load sets the change flag, and the field is built again 2 s later), and one that unloads leaves its cells
air; an edit made during the read may show in some of its blocks and not others until that rebuild, as the decision
accepted. A read going on when the match ends is dropped. The Debug line `Sky field: built ...` now gives the read's
total, its frames, its longest frame, and how many blocks were one fill and how many were read.

Measured (Release, 2560x1440, RX 5700 XT, the benchmark with a 20 s flight; the reads' own times from that line):

| | Before | After |
|---|---|---|
| Read during the flight | 10 to 12 ms in one frame, 6 builds | 7 to 8 ms over 8 frames, at most 1.06 ms a frame, 3 builds |
| Reads after the jumps between the fixed views | 11.7 to 29 ms in one frame, 9 builds | 6 to 25 ms over 6 to 23 frames, at most 1.0 to 2.1 ms a frame, 9 builds |
| Blocks read at the start view | 196 with aprons | 196: 19 to 55 one fill, the rest read |
| The flight's slowest frame each second, seconds with a build | 10.2 to 12.6 ms | (not comparable: see below) |

Few blocks are one fill: a level-6 cell under the ground holds stone with pockets of gravel and clay, and soil with
stones, so most solid cells hold more than one material. The world pass's GPU times did not change (start 2.03 and
2.03 ms, flight 2.32 and 2.33 ms). A game Lucy started on this machine during the second run loaded two CPU cores and
halved the frame rate in every view (the tunnel view: 455 frames a second before, 214 after, with the same 2.19 ms of
GPU time), so its frame times are not compared, and its read times are likely higher than on an idle machine.

Checks: `VoxelBlocksTests` (a block's own voxels equal the middle of its samples, a uniform block is named by its cell
with nothing written, refused blocks, buffer length, no allocation), `SkyFieldTests` (a read of one block a call
gives the field a read in one call gives; a read allocates nothing once its lists have grown), and
`WorldMeshesTests`' sky field test, unchanged, pass.

After a reader's review of the change:

- Those tests read at a stride of 32, one voxel a block, so an index error inside a block could pass them. A new test
  reads the slope world with seven levels (stride 1) and six (stride 2) round a point 60 m from its loaded centre, so
  the box's edges cut blocks and its far side lies outside the resident columns, and checks every cell against the
  generator's own formula (a swapped y and z in the copy fails it; the earlier tests passed it).
- The read's 1 ms counts in the 2 ms a frame that the upload's block reads may take (`ReadMillisecondsPerFrame`,
  counted from the upload's start), so while a read goes on, mesh jobs start in about 1 ms a frame instead of 2. Kept
  as it is: the main thread's limit stays 2 ms. Giving meshing its full 2 ms beside the read would let the upload take
  about 3 ms in those frames; that is Lucy's call.
- A read that throws ends the match, as any exception in the upload does, and the match's end clears the read; a
  failed flood is tried again after 2 s, as before.
- The box is 64 m tall and centred on the view when built, and the rebuild came after 32 m, so after a climb of almost
  32 m the box's top was at the view's height, and a roof above the view counted as open sky until the rebuild. Lucy
  chose a second rebuild after 16 m of vertical travel ("After look prototype D", 3); `WorldMeshesTests` checks that a
  20 m climb builds the field again and 20 m across does not.

### Look prototype D (2026-10-06)

Phase 2 answer 2: pictures of the world at 5 and 10 cm, with and without density, before the voxel size and density
are chosen. The prototype was built in a detached worktree (kept until the decision; no
part of it is on main). A console command meshes the world inside a 16 m disc round the view again from the resident
1 cm voxels, in cells of 5 or 10 cm; `mesh.vert` and `mesh-shadow.vert` clip today's meshes inside the disc and the
prototype's outside it (clip distances), so one view shows one kind. The runs streamed level 0 to 20 m
(`Level0ReachMetres=20`) so the whole disc was resident.

- **Density**: each cell's share of solid 1 cm voxels; Surface Nets with the threshold at half, each vertex at the mean
  of its cell's crossing points found along the edges by the density, the normal from the density's gradient (the
  mesher's 4x4x4 smoothed stencil), the openness from the mean density round the cell.
- **Occupancy**: a cell is solid where at least half of it is; today's mesher (edge midpoints, normals from the blurred
  occupancy). This stands for phase 5 without stored density ("derived at mesh time").
- **Material**: the one most common among the cell's solid voxels that touch air, and those of the air cells beside
  it. A first rule took only the cell's own voxels: a cover thinner than the cell (grass, moss, bark) that lay mostly in
  the cell above, which the threshold drops, was lost, and soil showed in rings on the forest floor and the wood under
  the bark in patches. The generator keeps cover one voxel thick at every level today, and would at 5 or 10 cm.
- A first occupancy took the voxel at each cell's centre, as a generator sampling at that size would: the porous grass
  cover was then missed in most cells at 10 cm, and the ground went brown. A generator at 5 or 10 cm must decide its
  voxels by coverage or by rules, not by one point.

Measured in the disc (the camera's draws; triangles and mesh heap of the blocks whose centre lies in it; the same
blocks at 8 cm are today's level 3):

| View | 8 cm (today) | 10 cm | 5 cm |
|---|---|---|---|
| start | 483,000 triangles, 5.7 MB | 300,000, 3.5 MB (0.62) | 1,262,000, 14.9 MB (2.61) |
| trail | 1,000,000, 11.8 MB | 611,000, 7.2 MB (0.61) | 2,594,000, 30.6 MB (2.59) |
| forest | 2,694,000, 31.8 MB | 1,711,000, 20.2 MB (0.63) | 6,936,000, 81.9 MB (2.57) |
| tunnel | 1,406,000, 16.6 MB | 866,000, 10.2 MB (0.62) | 3,714,000, 43.9 MB (2.64) |

Occupancy and density give the same triangles (the same cells are solid). GPU time of the world pass against today's
8 cm in the same run (two rounds each, within 0.02 ms; the times are higher than the benchmark's because level 0
streamed to 20 m, which moves every level out):

| View | 10 cm | 5 cm | of which shadow maps (5 cm) |
|---|---|---|---|
| start | +0.01 ms | +0.29 ms | +0.17 ms |
| trail | +0.01 ms | +0.55 ms | +0.45 ms |
| forest | -0.06 ms | +1.38 ms | +1.25 ms |

The disc drew its finest cells to 16 m, as level 3 is drawn today. In phase 5 the levels' reaches are set again: with
each level drawn to the same share of its voxel size as today, the triangles at any distance stay about today's at
either size (5 cm near, then 10, 20, 40 cm; or 10 cm near, then 20, 40, 80 cm), and the size decides how near the
finest detail reaches (about 10 m at 5 cm, 20 m at 10 cm, against 16 m at 8 cm today). With today's reaches kept, the
forest's world meshes (495 MB) would be about 310 MB at 10 cm and about 1.3 GB at 5 cm. The 5 cm shadow cost is the
near cascade drawing the finest meshes; it could take a coarser level, as the far cascade does.

What the pictures show (the grids: today's 8 cm, then occupancy and density at 10 and at 5 cm):

- Density smooths: the 1 cm terraces that show as contour lines on the forest floor and the bushes at 8 cm and with
  occupancy are gone at both sizes; a 2 m dig is a clean bowl; occupancy at 10 cm adds lumps to the flagstone road.
- 5 cm keeps shapes 10 cm loses: the flagstones and their moss joints, the wall's courses, the ruts. At 10 cm the
  road's stones merge into patches, as at 8 cm today.
- Material edges zigzag a cell at a time (flat colours a vertex); phase 3's per-pixel materials replace them.
- The vertical stripes on the stone walls stay at every size, with density, and with the shadows off. With every
  vertex's openness constant they almost go: they come from the openness, taken from the 4x4x4 samples round the
  vertex's cell rather than round the vertex, which rises and falls cell by cell along a wall that is not square to the
  grid. Stage 1.6 put them down to occupancy normals; that was wrong. A mesher fix at either size (phase 3 or 5).
- The tunnel is too dark to judge; it shows a thin bright seam where the disc's edge crosses its walls (the
  prototype's clip, not a property of either size).

Building the disc from the 1 cm voxels took 5 to 15 s at 100 ms a frame (mostly reading and counting the 1 cm voxels);
that cost belongs to the prototype only, since phase 5 stores the 5 or 10 cm voxels.

Carried into phase 5, whatever the size: the generator decides each coarse voxel by its coverage or by rules (one point
at the voxel's centre misses porous covers); covers stay one voxel thick; the mesher takes the openness round the
vertex, not round its cell; the levels' reaches are set again for the new size; at 5 cm the near cascade may draw a
coarser level than the camera.

### Phase 5 inventory, from the unit split's survey (2026-10-06)

Three reader agents classified every place in `src/` that turns a level into a size or voxels into metres, against
phase 5's world: coordinates stay in centimetres (`VoxelCoord`, edits, rays, the wire), a world voxel at level k becomes
10 times 2^k cm, and models keep 1 cm. The unit split (next stage) separates the models and the game's model-side code;
what is listed here is left for phase 5's base spacing.

The world store's conversions from centimetres to indices are shifts and masks by powers of two, which a 10 cm base
breaks; they need floor division by `base << level` (and the reverse):

- `VoxelConstants`: `SampleAt` (the sample rule, shared with models; see below), the bounds `MinChunkXZ`/`MaxChunkXZ`
  (±500 m in 1 cm voxels; the same chunk counts would give ±5 km), `MinChunkY`/`MaxChunkY`, the `MinVoxel*`/`EndVoxel*`
  bounds (used as centimetres by edits, the edit log, the mover, navigation, and loose bodies, and as level-voxel
  indices by collision: two sets are needed), `ReachMarginCentimetres`, the per-level chunk bounds.
- `ChunkKey.Containing`, `MinVoxel`, `EdgeInLevel0Voxels`, `CentreVoxelX/Z`; `Column.ReadVoxel`; `EditLog.Reaches` and
  `ColumnsAt`; `ColumnJob`'s edit overlap box and the generator's `SampleAt` contract (`IChunkGenerator`).
- `VoxelWorld`: `DeferVoxels` (a voxel count compared with a centimetre volume), `AddWanted`, `Priority`,
  `HasMovedHalfAColumn`, the keep radius; `VoxelWorld.Edits` (chunk and column shifts, `PendingOrder.Distance`);
  `VoxelWorld.Raycast` (walks centimetres as level-0 indices, masks blocks, the coarse lift); `VoxelWorld.Collision`
  (an index-space API that every caller feeds centimetres); `VoxelWorld.Pieces` (lifts loop per centimetre).
- Physics: `VoxelColliders` (`CellSize`, the per-level scale, box centres), `PhysicsWorld.VoxelBox`.
- Rendering: `WorldMeshes` column sizes and slack, `WorldMeshes.Draws` (`NearestSquared`, a block's `VoxelMetres`),
  `SkyField` (`CellMetres`, `cellVoxels`), the doc of `MeshDraw.VoxelMetres`. The shaders need nothing: every draw
  carries its own voxel size.
- Navigation: `NavGridScanner` (centimetres given as voxel indices to `ColumnsResident` and `HighestTop`, `DepthVoxels`),
  `NavigationService` (the same).
- Options and their checks: `MeshFinestLevel`, `MeshReachShare`, `StreamingRadiusMetres`, `Level0ReachMetres`, the
  validator's 3 m minimum (two 1.28 m columns).
- Lights: `LightPowerScale` was tuned against 1 cm-based face areas.

The character mover's grid is world level 0 (1 cm cells today): its constants are in cells (`Skin`, `Edge`, `MaxPart`,
`StripCells`, `WindowCells`, `SupportDepth`, `LookAhead`, the "1 cm steps" rules), and models are drawn into that grid one
voxel to one cell (`SolidModelVoxels`' scale, `LooseBodyVoxels`' brick shifts and offsets). Phase 5's mover rework covers
both.

The shared edit code: `VoxelEditing` (`SampleAt`, `FirstSampleAtOrAfter`, row spans, coverage, counts) serves the world
and models alike; it needs the grid's base as a parameter, models passing 1 cm. The unit split routes the characters'
own sample calls through `VoxelModels.SampleAt` first, so the world's rule can change without moving a character's
voxels. The game's `VoxelRows` (row spans and fills) serves both the generator and the characters' body fills
(`CharacterBody`); the generator's side takes the base, the characters' keeps 1 cm.

Pieces (lifted, thrown, settled): `VoxelPiece` keeps its box corner in centimetres and its size, pivot, and radius in
piece voxels, the same numbers today. `VoxelEdit.PieceAt` stores the piece's voxel radius where its bounds read
centimetres; `PiecePlacement` maps centimetre sample points one to one onto piece half-voxels; `LooseBodies.PutBack`
adds a centimetre corner to a piece-voxel pivot; the lift size check compares a centimetre extent with 128 voxels;
`EditLogSerializer` and `PieceRuns` carry piece sizes in the save and on the wire. Changing them changes the save format
and the protocol.

Questions for Lucy at phase 5's start (not asked yet):

- Thrown balls: 1 cm pieces (as models) or 10 cm world voxels (a 12 cm ball is then 2 to 3 voxels across)?
- Edits smaller than a voxel (the 2 and 3 cm punch and kick dents, the rifle's 1 to 3 cm bores, Stan's 4 cm laser):
  with stored density a small edit lowers a voxel's density a little; how much of it should show?
- The map's bounds: keep ±500 m and -40.96 m (decision 22 moves the floor to -51.2 m) by changing the chunk counts.
- What a ray hit names: today `VoxelHit.Voxel` is the entry centimetre, used as a 1 cm voxel by the tools (`+ 0.5`
  middles, `hit + normal` placing, `PlayerTools.PlaceCentre`, `ToolAim.Exit`, `BuildTargets`); with 10 cm voxels each
  needs the hit voxel's box.

### The unit split (2026-10-06)

Phase 2 answer 4, taken after the voxel size was chosen (10 cm with coordinates in centimetres). Under a base spacing
the world and the models keep one coordinate unit, the centimetre, so most of the shared constant's uses (centimetres
to metres) stay right; what the split separates is voxel sizes, where code took a model's voxel and a world voxel at
the same level to be the same size. No behaviour changes at today's sizes; the full suite and its pinned hashes pass.

- The world's voxel size has one function, `VoxelWorld.VoxelCentimetres(level)` and `VoxelMetres(level)` (since phase
  5's step 1 the world's `Grid`, `VoxelGrid.Edge` and `EdgeMetres`; 2^level cm
  until phase 5's base spacing).
- A model has a unit of its own (`VoxelModels`: `UnitCentimetres`, `VoxelMetres(handle, tree)`; `Centimetres` = 1 for
  characters' parts, items, and Stan; `LevelCentimetres`, `LevelMetres`), and the meshes draw each model at its own
  size. A lifted piece is a model of the world's level-0 voxels: its mass, hull, box, and drawn size take the world's
  size. Stan's voxel size is the models'.
- A cut body collides with the world's coarse level (`VoxelColliders.CoarseLevel`) when its parts' voxels are 4 cm or
  coarser (`CoarsePartCentimetres`), compared by size, not by level number. A test checks that the coarse level's world
  voxels are 4 cm, so it fails when the world's voxels change size until `CoarseLevel` follows.
- `VoxelWorld.CountRemoved` (harvests, build costs) counts cubic centimetres: a voxel counts as its edge cubed (decision
  21), the same numbers today.
- Characters' draw levels have distances of their own (`ModelExtractSystem.LevelZeroMetres` 13.44 m, `LevelOneMetres`
  26.88 m, `FarMetres` 26.88 m for the far flipbook), the world's level reaches at the default settings. With other
  voxel settings (`Level0ReachMetres`, `ReachGrowth`) characters no longer follow the world's reaches; the debug
  options `Game:World:CharacterLevelZeroMetres` and `CharacterLevelOneMetres` still move the parts' levels, not the
  far flipbook. Three profile tests took their far distance from a small test world (2.88 m in two of them); they now
  use 26.88 m, the distance their comments describe, so their logged times change.
- The foliage exit, the dig sound's probe, and the tools' "within a voxel" margins use the world's voxel size.
- The characters' own sample calls go through `VoxelModels.SampleAt` (the world's rule today).

Left for phase 5 (the inventory above): the world store's shifts, the mover's grid, the shared edit code and
`VoxelRows`, the piece format, and what a ray hit names. Renames of centimetre values named in voxels follow in their
own commit.

### Debug views of the wireframe and the detail levels (2026-10-06)

Phase 2 answer 3 put these before phase 5, to judge its meshes with. The console's `view` sets them for the run, not
saved: `wire`, `levels`, `all`, `off`.

- `view wire` draws the camera's world and model commands a second time as lines over what they drew, near black, with
  a depth test of greater or equal and no depth writes, pulled towards the camera by a depth bias (64 steps and 1.5
  times the face's slope) so a face's edges are not hidden by the face. It needs the device to draw triangles as lines
  (`fillModeNonSolid`): enabled where the device has it, asked of no device, so no device is refused for a debug view;
  elsewhere `view wire` says so and draws nothing.
- `view levels` tints each surface by its voxels' size: 1 cm magenta, 2 violet, 4 blue, 8 cyan, 16 green, 32 yellow,
  64 orange, 128 red. `mesh.vert` passes the draw's level (log2 of its voxel's centimetres, so 10 and 20 cm voxels get
  levels of their own too) in spare bits of the material output, so no vertex output is added.
- A Debug run with validation (sync and best practices) drew both at the test lineup with no message. In the levels
  view the plaza's ground is 8 cm and its walls 16 and 32 cm: a coarse column (20 to 41 m wide) changes level whole, so
  walls 20 m away may be drawn at 32 cm.

## Phase 5 plan: the world at 10 cm with a stored density (2026-10-06)

Written after the inventory above, which the first plan's estimate (4 to 6 weeks) did not count. The re-estimate is 6 to
8.5 weeks of work (31 to 43 days), 2 to 2.5 weeks over the first. The world stays playable after every step: the base
spacing is built first with a base of 1 cm, so nothing changes until the generator's step switches it to 10.

1. **Base spacing in the store, at a base of 1 cm (6 to 8 days).** One world grid value (the base, then the level's
   voxel `base << level` cm) used by every conversion between centimetres and indices that shifts by the level today:
   `VoxelConstants` (the sample rule, bounds, reach margins), `ChunkKey`, `Column`, `EditLog`, `ColumnJob` and the
   generator's sample contract, `VoxelWorld` (streaming, edits, rays, collision, pieces), `VoxelEditing` and the game's
   `VoxelRows` with the base as a parameter (models passing 1 cm), `VoxelColliders`, `PhysicsWorld.VoxelBox`,
   `WorldMeshes`, `SkyField` (its cells become the level nearest 64 cm: level 3, 80 cm, at a 10 cm base), and the
   navigation scanner. A ray hit names the hit voxel's box, and the tools that took it as 1 cm (`ToolAim.Exit`,
   `PlayerTools.PlaceCentre`, `BuildTargets`, the `+ 0.5` middles) use the box. The grid is a value each world owns
   (`VoxelGrid`, passed down, models passing 1 cm), not a constant, and generators take it, so a test can build a world
   at a base of 10 cm before step 2. Level numbers that stand for a size (the sky field's 64 cm, the colliders' 4 cm)
   become the grid's lookups. Acceptance: a test world at a base of 10 cm whose shapes' faces lie off the 10 cm lines,
   checked against the generator's own shapes (materials, rays and their hit boxes, edits and their counts, the edit
   log's columns, the sky field); the full suite at a base of 1 cm pins that nothing else changed.
2. **The generator at 10 cm and the switch (4 to 6 days).** The base becomes 10; the map keeps ±500 m (the chunk counts
   change) and the floor moves to -51.2 m (decision 22). The generator decides each voxel by coverage or by rules
   (prototype D: one point misses porous covers), keeps covers one voxel thick, and handles features thinner than a
   voxel (a question for Lucy then, with pictures). `WorldGenerator.Version` rises and every pinned world and
   navigation hash is pinned again. The levels' reaches, `MeshFinestLevel`, and `MeshReachShare` are set again.
3. **Stored density (7 to 9 days).** A density block in surface bricks (a `VoxelMemory` pool, a `ChildState` rule,
   `BrickCodec`), the generator's coverage, edits that write coverage, the mesher on density (prototype D's, written to
   project standards with tests, and the openness taken round the vertex), the save format 7, and a protocol version
   with both wire fingerprints.
4. **The mover and physics at 10 cm (7 to 10 days).** The mover's grid becomes world level 0 (10 cm cells): its slope,
   rim, and ledge rules and constants are tuned again, models are drawn into its grid at their scale
   (`SolidModelVoxels`, `LooseBodyVoxels`), and its tests and the 10,000 random moves are rebased. Colliders' cells,
   loose bodies, and the piece format (save and wire) follow.
5. **Gameplay at 10 cm (4 to 6 days).** Small edits, thrown balls, harvests and costs (already counted in cubic
   centimetres), the foliage rules, Stan's lasers, the rifle's and bazooka's craters, digs and building, and their
   tests.
6. **Tuning and measurement (3 to 4 days).** Light power, shadow bias, the benchmark against the 4 ms budget and the 512
   MB soft target, Lucy's look test, and the integrated GPU's benchmark at the phase's end (on the HDMI monitor, asked
   for first).

Lucy's answers (2026-10-06, each my recommendation):

1. **Go ahead as planned**, at 6 to 8.5 weeks. (Not: trim the scope first.)
2. **Thrown balls fly as round 1 cm models** and, at rest, go back into the world as a 10 cm sphere of stone with
   density; the ball's save and wire record becomes a sphere instead of a piece (step 5, 1 to 2 days). (Not: pieces of
   10 cm world voxels.)
3. **Edits smaller than a voxel lower the density** of the voxels they cover by what they cover, and harvest exactly
   that many cubic centimetres, so punches and kicks keep harvesting soil and leaves. (Not: leaving the world alone, as
   the first plan said.)
4. **The mover reads the stored density** for the ground's height inside a voxel, so slopes stay smooth ramps (step 4,
   2 to 3 days). (Not: 10 cm steps with the eye eased.)

The thin features wait for step 2's pictures.

### Step 1 done (2026-10-06)

The world owns its grid (`VoxelWorld.Grid`, its generator's `IChunkGenerator.Grid`, 1 cm by default); the store indexes
voxels by level-0 voxel of the grid, and only the places where centimetres meet indices convert (`VoxelGrid.ToVoxel`,
`ToCentimetres`, `SampleAt`; `GridLevel` for edits and the generator's shapes). Converted: edits and their bounds, the
edit log's columns, column jobs, streaming and point reads, the generator's shapes and `VoxelRows`, rays (a hit names
its box: `BoxMin`, `BoxCentimetres`, `BoxMiddle`), lifts and pieces' residency, the mover's and the navigation's reads,
colliders and voxel boxes (the coarse colliders at the level nearest 4 cm), the meshes, the sky field (the level nearest
64 cm), the order large edits are applied in, and the tools that took a hit as 1 cm (`ToolAim.Exit`,
`PlayerTools.PlaceCentre`, `BuildTargets`, digs and the laser at the struck voxel's middle). Everything gives the same
result at 1 cm: the full suite passes unchanged.

Acceptance: `BaseTenWorldTests` builds worlds on a 10 cm grid from `GridShapesGenerator` (a floor at 137 cm, a stone box,
and a ball of leaves, every face off the 10 cm lines) and checks each against the shapes at each voxel's sample point:
point reads, rays against a walk through 10 cm voxels (hit voxel, face, distance, box), a coarse ray's 40 cm box, a
sphere's clear at levels 0 to 3 and its count (1,000 cm³ a voxel at level 0, 64,000 at level 2), an edit across a 12.8 m column's edge replayed into both columns after
they unload and load again, the order a large edit's columns are applied in, and the sky field's 80 cm cells. Breaking
the edit log's grid, the large edits' order, or the row spans' grid on purpose fails them.

Found for later steps:

- **Streaming (step 2).** A column streams when its centre lies within a streaming centre's disc. At 10 cm a column is
  12.8 m wide, so the 3 m disc round a loose body and the 4 m disc round a remote player can want no column at all (at
  a corner of four, a disc needs more than 9.05 m to want any). The rule needs the column's nearest point instead, or
  at least the column that holds the centre; the reach margin (2 columns, 25.6 m at 10 cm) is retuned with it. The
  tests use a 10 m disc until then.
- **Small edits (step 5).** Strike dents, rounds' channels, blast digs, and `Foliage`'s ground still take a hit's
  `Voxel` (the level-0 voxel's low corner) as the struck centimetre; with density they take the entry point or the box.
- **Pieces (step 4).** Loose bodies' pivots and settled pieces are in centimetres of 1 cm models; the piece format
  follows the mover's step.

### Step 2 done: the world at 10 cm (2026-10-06 and 07)

- **Bounds from the grid**. The world's chunk bounds moved from `VoxelConstants` onto `VoxelGrid`, worked out
  from fixed extents: whole level-0 chunks covering ±500 m, the floor at the first chunk at or below -40.96 m, the top
  at whole chunks above 81.92 m rounded up to a multiple of 8. At 1 cm they are today's; at 10 cm ±512 m, -51.2 m
  (decision 22), and +102.4 m. Levels 0 to 2 share the floor and 0 to 3 the top.
- **Streaming by a column's nearest point**. A column loads when its nearest point lies within a centre's
  radius, so a 3 or 4 m disc (loose bodies, remote players) loads the 12.8 m columns it meets.
- **The switch.** `WorldGenerator.WorldGrid` is 10 cm and `WorldGenerator.Version` 10. The generator needed only two
  rules changed: its sizes were already in centimetres (`GridLevel.Edge`). Flowers and mushrooms are drawn only where
  voxels are at most 2 cm (they had been level 0 and 1), so the 10 cm world has none. The options check
  (`EngineOptionsValidator`) works out the level distances on the registered generator's grid and refuses a radius
  that draws level 0 nowhere.
- **Defaults** (routine choices): each level drawn to 168 of its voxels, as today's 8 cm level is (13.44 m): 10 cm to
  16.8 m, 20 cm to 33.6 m, 40 cm to 67.2 m, then 80 cm to the view's 256 m. `Levels` 4, `MeshFinestLevel` 0,
  `MeshReachShare` 0.5, `StreamingRadiusMetres` 59.2 (level 0 reaches 33.6 m and loads to 59.2 m; the other half of
  the reach and the two-column margin are room to load before a level is drawn). `ReachMarginColumns` stays 2.
- **Measured** at the start view (Debug, 2560x1440, the same pose in both builds):

| | Today (1 cm world, 8 cm meshes) | 10 cm world |
|---|---|---|
| Voxel memory | 234 MB with 1,468 of 2,741 columns loaded after 10 s (425 MB after a minute in earlier runs) | 50 MB, 241 columns, all loaded after 10 s |
| Triangles drawn (camera) | 4,303,682 | 3,774,558 (0.88) |
| Triangles into the shadow cascades | 8,214,242 | 7,048,020 (0.86) |
| Mesh heap | 360 MB | 333 MB |
| Loading (Debug) | 15.9 s | 7.1 s |

- **Lucy's answers (2026-10-07, each my recommendation):** flowers and mushrooms are left out of the 10 cm world until
  phase 6's objects (not: kept as 10 cm blobs); the other thin shapes (joints, courses, flagstones, ruts, pebbles,
  bones, trail stones, twigs, the portcullis's tips, coals) stay as they are, to come back as phase 3's materials,
  phase 6's objects, and step 3's density (not: the pedestal fitted to 10 cm lines; every thin feature thickened to a
  voxel). Pictures: the pedestal's top two courses merge, the road's flagstones blur, and the forest floor looks as
  today's.
- **Found.** Levels of 80 cm and coarser show one or two voxels of buried dirt or stone open to the air in some
  places, as the 1 cm world's 32 and 64 cm levels did (never checked before; measured with a probe on 2026-10-06):
  the exposure test checks voxels to 40 cm. At 40 cm one wood voxel of a branch's end can show: the tree test checks
  voxels to 20 cm. A chunk of 12.8 m is wider than any crown clump and the podium is shorter than one, so no layer of
  leaves or tower stone alone remains; the tests check the descriptions' exactness instead.
- **Tests kept at 1 cm until their step.** The creeps' turning (`CreepTurningTests`: on 10 cm steps the mover crabs a
  goblin 11 degrees and a wolf 21) until step 4; the thrown balls (`RealGroundCollisionTests`) until step 5; the
  giants' streaming (`StreamingCentreSystemTests`, whose numbers are 1 cm levels) and the pedestal's courses and
  joints (shapes, at 1 cm). `WorldGenerator(VoxelGrid)` builds such a world.
- **Open.** Creeps walk on the mover wherever level 0 is loaded, now to about 59 m from a player against 16 m before,
  so more of them collide with the world; each costs less (10 cm cells), and step 6 measures the total.

### Step 3 done: stored density (2026-10-07)

- **Storage**. Each world voxel has a density (`VoxelDensity`): how much of its cube is solid, 0 to 255. A
  solid voxel's density is 128 to 255 and an air voxel's 0 to 127, so the rays, colliders, mover, and navigation, which
  read materials, see the same world; the density places the drawn surface inside a voxel. A brick keeps a block of
  one byte per voxel (512 bytes, `VoxelPool.Density`, its handle in the brick's record, now 12 bytes) only when some
  voxel's density is not its material's default (air 0, solid 255). A brick of one material with other densities is
  stored, with an empty mask when it is all air. Models keep only materials.
- **The mesher**. A vertex lies at the mean of the points where the density crosses one half along its
  cell's crossing edges; the normal comes from the 4x4x4 gradient of the densities; the openness is the mean density
  of the 3x3x3 voxels round each of the cell's corners, blended to the vertex's place in the cell, so it follows the
  surface and not the cell (prototype D's finding). On a wall turned 30 degrees from the grid the openness along the
  face runs from 124 to 131, against 79 to 175 when it was counted round the cell: the vertical stripes on stone walls.
  Models mesh at their materials' default densities. Validation (sync, best practices) passed.
- **Edits**. A sphere, capsule, or box clear, fill, set, or clear of some materials changes each voxel by
  the share of it the shape covers, measured on up to 10 x 10 sub-rows along y and z, each to the centimetre along x
  (a 10 cm voxel's 1,000 cubic centimetres exactly): a clear takes that share of what is solid in the voxel
  (`d - round(d x share)`), a fill or set adds that share of what is empty, and the material follows the density
  across one half. Lifts, paints, and pieces keep to sample points (a lift's piece holds whole voxels). Models keep the
  old rules. `CountRemoved` counts what each voxel's density loses times its volume, so a dent smaller than a voxel
  harvests what it takes (Lucy's answer 3); entry 0 holds what a clear takes from air voxels' densities, which no
  harvest counts but a build's cost does, so a cube on the voxel lines costs what its fill adds. A capsule's row is
  found from its formula and corrected onto the integer test, and a chunk is edited a strip of bricks at a time, each
  row measured once: Debug times for one 12.8 m chunk, a 2.56 m sphere 17 ms, a jet-sized capsule (30 cm, 6.4 m) 13
  ms, a 5 m box 2 ms. A 2 m dig in the game took 22.6 ms of a step (Debug).
- **The generator** (version 11). The ground's voxels take the share of each below the ground's height (a double,
  before it is rounded to a whole centimetre), fitted to the material the generator already gives, so every pinned
  hash of materials is unchanged and the densities are pinned beside them. A voxel a feature (rocks, trees, bushes,
  the walls, the pedestal, the fortress, pockets) made solid or air is whole (`VoxelDensity.Agree`). A layer is
  described as air or one material only where every voxel is wholly so (the voxels' edges, not their sample points).
  Column jobs fit the generator's densities before replaying edits, so a replay starts from the bytes a live edit sees.
- **Routine choices.** One byte per voxel, in full 512-byte blocks: at the start view the densities take 35 MB, about
  68,000 blocks, a fifth of the stored bricks. A compact block of only the partial voxels' densities (a mask and the
  values) might take 20 MB less (an estimate, not measured; about a day). A clear takes its share of what is solid in a voxel, which is my reading of
  Lucy's answer 3 ("lower the density by what they cover"): so a clear repeated on one spot keeps taking (a 5 %
  share 14 times makes a full voxel air), and a dent on a half-full surface voxel takes half its share; the other
  reading, `min(d, 255 - share)`, would leave a voxel alone when the shape covers only its empty part, and a dent
  on a surface voxel would then take nothing. No save format or protocol change: an edit's record is the same, and the
  generator's version, which saves and sessions both check, rose to 11 with the densities and the new rules (the plan
  said format 7 and a protocol bump). The column job pool is filled at start (a free for every column kept), as the
  column tables are: under load slow frees grew it during play.
- **Measured** at the start view (Debug, 2560x1440, as in step 2):

| | Step 2 | Step 3 |
|---|---|---|
| Voxel memory | 50.4 MB, 364,947 bricks | 85.5 MB, 368,336 bricks |
| Column job (average while loading) | 95 ms | 199 ms (Debug); Release, a mixed chunk at the players' base 0.60 to 0.99 ms |
| Loading the world (Debug) | 5.1 s | 8.0 s |
| Triangles drawn | 3,774,558 | 3,769,984 |
| Mesh heap | 333 MB | 333 MB |

- **Pictures.** The base floor's contour lines (where the floor crossed a 10 cm line) are gone, and so are the
  vertical stripes on the walls; a 2 m dig is a round bowl with smooth rings of dirt and stone; the forest floor looks
  as in step 2. The pedestal's courses, the walls' blocks, and other features keep whole voxels.
- **Lucy's answers (2026-10-07, each my recommendation):** the features (trunks, branches, logs, rocks, bushes,
  pockets, the walls, the pedestal) keep whole voxels until phase 6 makes them objects (not: capsules and ellipsoids
  giving their share now, 1 to 2 days); a clear takes its share of what is solid in a voxel, as built (not: only what
  it leaves uncovered); the densities stay in full 512-byte blocks (not: compact blocks now, about a day).

### Step 4 done: the mover and physics at 10 cm (2026-10-07)

- **The mover's distances in centimetres**. Its skin, tolerances, support depth, look-ahead, lift margin,
  and the "a voxel more" allowance of its slope rules were voxel counts tuned at 1 cm, so at 10 cm each was ten times
  its size. They are given in centimetres and divided by the grid's base: at 1 cm each is exactly what it was. That
  alone took a goblin's crab on the first trail from 11 degrees to 2 and a wolf's from 21 to 1.
- **The ground's density** (Lucy's answer 4). On voxels coarser than 1 cm the mover reads each column's
  surface where the density crosses one half between its top voxel's centre and the one above's (`SurfaceOver`, the
  mesher's crossing), blended bilinearly between the columns' centres, but only between neighbours whose surfaces differ
  by less than the slope limit over one voxel (45 degrees: one voxel), so a slope under the limit is a ramp and a ledge,
  a stair, or a wall stays sharp. Measured against the mesh of slopes of 10 to 44 degrees, the blended heights lie within
  0.62 cm of the drawn surface; the columns' own heights were up to 5.4 cm off it, and each column's share below the
  ground up to 5.6 cm (`VoxelSurfaceTests`). A body stands on the blend's highest point under it (its flat bottom on the
  uphill side, as on 1 cm steps); a column whose surface lies under its feet, or within the ramp for a walking body, is
  no wall, and the feet follow the ground up onto it after each part of a walk. The rim and rise rules read the
  columns' surfaces. On a grid of 1 cm a voxel's top is its surface, and nothing changes there.
- **Measured.** On a 30-degree slope of 10 cm voxels a walking body's feet rise at most 2.7 cm in a tick against the
  slope's 2.2 cm (a voxel a time would be 10); 30 and 40 degrees are walked up, 60 not, stairs of 20 cm risers are,
  a 50 cm step is not, and 10,000 random moves never leave a body in the ground (`DensityMoverTests`). A move costs
  134 microseconds there (Debug). The creeps' turning test runs on the 10 cm world: a goblin crabs 0 degrees, a wolf 1,
  and a Titan's legs jolt 2.0 degrees against the 3 allowed (5.8 before), since its feet read the blended ground too.
- **Loose bodies and solid models**. A lifted piece holds centimetres (a lift reads it a centimetre at a
  time), so the mover's voxel takes the piece's voxel under its centre; a solid model's voxel is its size over the
  world's (0.4 of a voxel for Stan's 4 cm head). Since step 2 the mover had read a thrown ball and Stan's head ten times
  their size and distance.
- **Colliders.** On voxels coarser than 1 cm a column's top voxel in a collider ends where its density puts the
  surface, a box to the millimetre (`PhysicsWorld.SurfaceBox`), so a ball rests on the drawn floor, not on a voxel's
  top up to 5 cm away. A merged box whose top layer holds such a column gives that layer column by column.
- **Ceilings and cost** (from a review). A walking body's ramp column stays a wall where anything blocks
  within the rise over its head; a part the ramp-follow has no room to rise on is undone; a step-up whose landing lies
  over its raised slide is refused; a landing lifts a body only as far as its headroom. A move remembers each column's
  surface, so its repeated reads walk the voxels once: a move on a 10 cm slope takes about 80 microseconds in Debug,
  134 before. Walking (4.5 m/s) and running (7 m/s) up 30 and 40 degrees keep their pace with no false wall. Every
  body in a 10 cm world follows the blended ground after each part of a move, not only one that may step,
  since a jump beside a ceiling left the feet in the slope; 10,000 random moves on a 30-degree slope under a stone
  ceiling never leave a body in a voxel.
- **In the game** (Debug): the player walked 33 m over the hills, climbing 2.9 m, with no warning or error.
- **Time.** Steps 3 and 4 were planned at 7 to 9 and 7 to 10 days and took about a day each, with the piece format
  moved to step 5. The plan's total (31 to 43 days) is not lowered on that; step 5 is estimated again in its plan
  below.
- **Moved to step 5.** The piece format: the only world pieces in the game are thrown balls, which step 5 makes 1 cm
  models that settle as a sphere (Lucy's answer 2); placing a piece of centimetres back into 10 cm voxels is left
  until then, and the thrown balls' tests stay on 1 cm voxels till then.
- **Open for step 6.** The mover's cost with many creeps near players (about 600 surface reads a goblin's move before
  the memo), measured there.

### Step 5 done: the game's side at 10 cm (2026-10-07)

Two read-only surveys of the game's tools, foliage, navigation, and feet on the 10 cm world found most of it already
right: digs and the lasers (centred on the hit voxel's middle, `BoxMiddle`), placing stone, building (cubes off the
voxel lines change voxels by share; cost from `CountRemoved`), harvest amounts (cubic centimetres of density lost),
walking through leaves, feet on the blended density surface, streaming discs, the play area, and the footstep and
landing sounds. Four things needed work. The step took part of a day against the plan's 4 to 6 days.

- **Thrown balls** (answer 2: round 1 cm models that go back as a 10 cm sphere with density). A throw lifted the ball's
  space as a piece of 1 cm voxels, but the body's hull, mass, model, and settled box took the world's voxel, so a ball
  flew ten times its size. A throw now makes a body from nothing: a ball of 1 cm stone voxels (`VoxelWorld.MakeBall`,
  one per radius and material, shared, not in the edit log) whose hull and mass come from them. At rest it goes back as
  a sphere `Fill` of stone about its rounded pivot (`LooseBodies.Settle`), which adds to each 10 cm voxel the share of
  its air the sphere covers. `BodySpawn` carries the ball's radius instead of a piece. Lifts and pieces stay in the
  engine (tests, saves) until phase 6 decides how objects fall and break. A ball weighs 14.3 kg, the big one 390.5 kg.
- **Small edits at the entry point.** `VoxelHit.Entry(origin, direction)` gives the centimetre where a ray entered the
  voxel it met, half a centimetre further along the ray, rounded down. Dents, rounds' channels, and a rocket's crater
  start there instead of at the struck voxel's low corner, which on 10 cm voxels lay up to 10 cm off across the face
  and, on the ground's top, 10 cm under it. Clients send that centimetre, so the protocol version rose (the bytes are
  the same; their meaning changed).
- **The navigation scanner** reads the density surface over a cell's highest blocking voxel (`VoxelWorld.SurfaceOver`,
  as the mover stands and the mesher draws). Floors more than 3 cm off the layout's, before and after: forest 286 of
  983 to 0, wall gap 607 of 921 to 3, trail 281 of 1,012 to 0, bank 647 of 868 to 3, 50 stamped edits 285 of 1,000 to
  1. The floor tolerance of the test went from 6 cm to 3.
- **The tests** of tools, blasts, harvests, throws, requests, feet, and sounds moved to 10 cm voxels (`FlatGenerator`
  and the physics harness take a grid). No test found a fault in the game's code.

**What small edits do to a 10 cm soil floor**, measured (a floor at 153 cm, each hit straight down at the entry point,
the same spot each time; the drawn surface is where the density crosses one half in that column):

| Edit | 1 hit | 5 hits | 10 hits | Harvest a hit |
|---|---|---|---|---|
| Punch, 2 cm | 0.14 cm lower | 0.74 cm | 1.64 cm | about 24 cm³ |
| Kick, 3 cm | 0.51 cm | 3.15 cm | 7.81 cm | about 75 cm³ |
| Punch at most power, 4 cm | 1.14 cm | 8.31 cm | 18.3 cm | about 150 cm³ |
| Kick at most power, 6 cm | 6.30 cm | 26.3 cm | 50.2 cm | about 500 cm³ |
| Rifle round in soil, 2 cm by 15 cm | 0.69 cm | 7.31 cm | 21.4 cm | about 200 cm³ |

On the 1 cm world a punch made a 4 cm hole at once. On 10 cm voxels with answer 3, one punch moves the drawn ground
about a millimetre and ten punches at one spot about 1.6 cm; the harvest is what the world lost, whatever the size.
Larger dent radii would show sooner and harvest more with the volume (a radius of 5 cm about 15 times a 2 cm punch).

Lucy's answers (2026-10-07, each my recommendation): small dents stay as decided (each hit takes what it covers, so a
punch alone barely shows and repeated hits dig a shallow dip; not: larger dent radii; not: deciding after a look), and
lifts and pieces stay in the engine until phase 6 (not: removing them now, about a day, with a save format and protocol
change).

Lucy's answers (2026-10-08, each my recommendation but the third): look tests wait until step 6 is done; step 6's bar is
the world pass at most 4 ms in every benchmark view on the RX 5700 XT at 2560x1440 with shadows and materials
(decision 18), 1,000 near goblins adding at most 4 ms, and mesh memory reported against the 512 MB soft target
(decision 19), anything over going to her; and no integrated GPU run in step 6 (the machine's second monitor is not
used).

### Step 6 and the end of phase 5: tuning and measurement (2026-10-08)

Phase 5 was planned at 6 to 8.5 weeks and built in three days (2026-10-06 to 08). The estimate counted the hundreds of
places that converted between centimetres and voxels by a level's 2^k as a week of work, where one grid value passed
down did most of it, and each step's tests found its faults the same day. Against the bar of step 6, on the RX 5700 XT
at 2560x1440, release, render scale 1 (benchmarks `step6-base-1` and `step6-crowd-2`, against the last 1 cm runs):

| View | World pass at 1 cm | At 10 cm | Shadow maps at 10 cm | Mesh heap at 10 cm |
|---|---|---|---|---|
| start | 2.03 | 1.91 | 0.51 | 333 MB |
| start-low | 2.01 | 1.89 | 0.50 | 333 MB |
| forest | 2.83 | 2.33 | 1.03 | 523 MB |
| forest-down | 2.73 | 2.38 | 1.43 | 523 MB |
| forest-up | 3.14 | 2.62 | 1.31 | 523 MB |
| canopy | 1.99 | 1.70 | 0.37 | 523 MB |
| trail | 2.32 | 2.05 | 0.64 | 502 MB |
| tunnel | 2.19 | 1.91 | 0.33 | 214 MB |
| flight | 2.33 | 2.09 | 0.59 | |

- **The world pass is within 4 ms in every view** (1.7 to 2.6 ms, shadow maps included), 10 to 18 % less than at 1 cm.
- **1,000 near goblins add about 1.1 ms of models:** crowd-near is 2.73 ms in all (2.66 at 1 cm), its models 1.07 ms
  (1.03), crowd-far 2.23 ms (2.26). A first run showed 4.47 ms and 1.77 ms for models. It was taken with 1,000 held
  goblins all on the mover, whose long fixed steps left the GPU idle part of each frame and its clock low, so every pass
  measured slower (the start views, with no models in them, took 3.2 ms against 1.9). A GPU benchmark with a CPU-heavy
  scene measures the GPU at a lower clock; read the start views to tell.
- **Mesh memory** is 523 MB in the forest views, 11 MB over the soft target of 512 MB (577 MB at 1 cm), and 214 to 502 MB
  elsewhere. The target is for the end of phase 6.
- **Voxel memory** at the start view is 85 MB (about 425 MB at 1 cm, not measured again).
- **Characters on the voxel mover.** Level 0 now loads to about 59 m round a player, against 16 m, so more characters
  walked through the mover and placed their feet on voxels. 768 held goblins near the player took 9.2 ms of fixed step
  against 3.9 ms at 1 cm (movement 5.0 against 2.0 ms, feet 3.6 against 1.3). A goblin on the voxels costs 1.3 to 1.5
  times what it did, a Titan's move about 3 times (179 microseconds against 55, after the mover remembers each column's
  surface). Lucy's answer (2026-10-08): creeps walk on the voxels and place their feet only within 16 m of a player or
  the camera, as at 1 cm, and on the grid beyond (back to the grid at 18 m, a margin against changing every step). The
  same crowd then took 5.9 ms a step and the 95th percentile frame 10.4 ms (15.3 before, 8.5 at 1 cm). (Not: 24 m;
  keeping 59 m and making each creep cheaper; keeping it as is.)
- **Shadow bias** (a normal offset of 1.5 texels and a depth bias of 0.0003, set in stage 1.3): a build with stage 1.7's
  values (0.5 texel, no depth bias) costs the same, within 0.01 ms in every view, and neither shows acne or a contact
  gap in the benchmark views; they differ in up to 3 % of pixels. The shipped values stay. (2026-10-12: the depth bias
  went: it lit a strip of ground at the feet of voxel objects, `docs/voxel-objects.md`.)
- **Light power** (tuned against 1 cm faces): the fortress hall's braziers light their surroundings as little as before;
  no change.
- **Leftovers.** A thrown ball whose space check passes (at sample points) can start up to 8 cm inside the floor's
  collider, whose top voxel ends at the density's surface; Bepu pushes it out at up to 2 m/s and it rests on the
  surface within 0.5 s, a small hop seen only throwing steeply at the ground just ahead; left as it is. A rocket into
  256 held goblins costs one step of 54 to 70 ms (cuts, ragdolls, fragments), as measured at 1 cm and 10 cm alike: an
  existing hitch, not phase 5's, left open. A crowd that a profile test calls "pressed against a 2.5 m stone wall" does
  not stay there: all 256 friendly goblins end up in or past the wall within 9 s on the build before phase 5, and 144 at
  10 cm, because the wall's grid cells are blocked across the map and the friendly field has no way round. Whether such
  creeps should pass is the game's to say.

Lucy's look test (2026-10-08, played on a 4K monitor): the slope walk, a thrown ball's settled lump, and punches on soil
look good, and she saw no difference between the two shadow-bias pictures. Next in the order (phase 2's answers): phase
3.

## Phase 3 plan: the renderer core on the 10 cm world (2026-10-08)

Three read-only surveys went before it: the seams between levels, the materials' port, and the rest of the plan's
items. Lucy asked for the seams first.

What stands today:

- **Seams.** Each level's blocks are meshed on their own; where a finer column meets a coarser one their outer cells
  overlap and end apart, which shows the sky as slits at about 17, 34, and 67 m. Neighbouring columns can differ by
  two levels (level 3 beside level 1 at 67 to 85 m, level 2 beside level 0 at 34 to 42 m), and a column whose finer
  ones are still loading stays coarse beside finer neighbours. The coarse levels' samples do not nest in the fine ones'
  (each level is sampled at its own voxels' centres), so Transvoxel's tables do not apply, and no published method
  stitches Surface Nets of this kind.
- **Edits** reach the screen in about two frames (columns sorted nearest first); blocks within a column are not, and
  the edit-latency log line counts streaming too (one read 10.6 s), so it cannot measure a 4 m sphere yet.
- **Mesh memory.** A full heap warns once; nothing makes room, and a block that finds no room is probably read and
  meshed again every frame until some appears.
- **Culling** is the CPU's per block, about 0.1 ms a frame for 8,660 meshed blocks and 667 drawn.
- **Shadows.** Two cascades meet with a hard switch at 30 m; no blend band, no contact hardening (decision 12).
- **Lights.** One per world block's glowing vertices, the 32 brightest looped at every pixel; nothing in the game
  makes a light for a muzzle flash, a laser, or a blast.
- **Anti-aliasing.** 4x MSAA by default and SMAA without it; the settings' row is still an SMAA switch (decision 11
  asks for Off, SMAA, MSAA 2x, 4x, and 8x), and the sample count is fixed when the renderer starts.
- **Materials** are flat colours; the raymarcher's recipes (634 lines of `voxel-surface.glsl`, 8 patterns) need only a
  point in centimetres, the normal, and a footprint, which the mesh shader has; the flagstones' joints and the walls'
  courses were stored shapes and need new recipes.
- **Debug views:** wire and levels; **the loading screen** waits for every wanted column to the view distance.

The work, in order:

1. **Seams** (Lucy's decision 7: stitched, no skirts). A balance pass after the level choice splits any column with a
   neighbour, diagonal included, two levels finer, so neighbours differ by at most one level (and the far cascade's
   choice the same). Then the finer side's border blocks are meshed in a transition mode: their job also reads a thin
   slab of the coarser neighbour, and their outer ring of cells joins the coarser column's own rim vertices (computed
   from the coarse densities as the coarse mesh computes them), so the two meet exactly; the coarse mesh is unchanged.
   A border block's variant is meshed before its neighbour's level changes, as children are before a split. Tests:
   random density fields meshed at two levels side by side with no open edge where they meet; the balance rule; the
   remesh on a change of level; no allocation. A block-bounds debug view comes first, to see the seams' blocks.
2. **Edits:** the latency line counts only edited blocks, a column's dirty blocks go nearest first, and a 4 m sphere's
   time to the screen is measured.
3. **Mesh memory:** a full heap stops re-reading blocks that found no room, and frees the farthest blocks the view does
   not draw before warning.
4. **The anti-aliasing row** (decision 11) with the settings and the sample count changed for the run.
5. **Shadows** (decision 12): a blend band where the cascades meet, then contact-hardening soft shadows (a blocker
   search; the maps hold only faces turned from the sun, so the search needs the casters' front or a thickness), with
   pictures for Lucy at each.
6. **Clustered lights:** a light component and its extraction (a frame system), a compute pass that bins lights into
   clusters, a light budget as an option; lights from glowing materials as today, and the sources Lucy chooses.
7. **Materials** (decision 13): the recipes ported per pixel with their fades, the material table extended, two
   patterns first (the trail's pebbles, bark) measured against the 4 ms budget and for shimmer, then the rest and new
   flagstone and wall-course recipes; baked texture arrays only if the port costs too much. Pictures and numbers to
   Lucy.
8. **Glow after the grade**, with pictures.
9. **The phase's end:** the benchmark against the 4 ms budget and 512 MB, the doc, the rules files, memory.

Estimate: the items' sizes add up to 30 to 40 days (the seams 8 to 14 of them, materials 6 to 10, lights 5 to 7,
shadows 4 to 6). Phase 5 took three days against 6 to 8.5 weeks, so 1 to 2 weeks is likely, the seams the most
uncertain part.

Lucy's answers (2026-10-08, each my recommendation):

1. **GPU culling, cave culling, and levels by 3D distance are deferred**: GPU culling to phase 6, when objects multiply
   the draws; cave culling and 3D-distance levels to underground play (phase 7). (Not: GPU culling now; all three now.)
2. **Only the Anti-aliasing row** joins the Graphics tab in phase 3; shadow and material quality rows wait for the
   low-end tuning after phase 6, their options kept. (Not: a shadows row too; all three.)
3. **A change of anti-aliasing takes effect at once**: the renderer rebuilds its pipelines and images with the new
   sample count. (Not: at the next start.)
4. **Event lights in phase 3: muzzle flashes, lasers (the debug lasers and Stan's), and rocket blasts**, besides the
   glowing materials. (Not: none for now.)

### Seams, part 1: the balance and the transition mesher (2026-10-08)

The balance pass (`LevelBalance`) keeps every drawn column within a level of its neighbours, sides and
corners, in the camera's pass and the far cascade's; the game's start view needs no forced split it could not make
(0 columns left unbalanced in the meshes line).

`SurfaceNets.MeshTransition` meshes a block beside columns one level coarser: its cells that straddle such a column's
boundary take the coarser level's own vertex (each coarser cell standing over two of them along each axis; of a coarser
cell the surface does not cross, the nearest crossed one beside it by the cell's own vertex), and where the coarser
column lies past the block's high face it also makes the coarser level's quads of the edges that cross that face. On
random shapes sampled as the world's levels are (`SurfaceNetsTransitionTests`), the edges a seam leaves open fall from
104 to 268 to 5 to 27 along a side, and from 185 to 331 to 8 to 29 at a corner: about nine in ten. Choosing the coarser
cell by where the fine cell's own vertex lay left twice as many. What remains are holes of a voxel or two where the two
levels' samples (each level's at its own voxels' centres, which never coincide) disagree on which cells the surface
crosses; no rule that maps fine cells onto coarse ones closes those, and no published method stitches Surface Nets
over levels sampled this way.

Lucy's answer (2026-10-08, my recommendation): **stitch with this now, and work on an exact method after the
materials** (3 to 8 days, uncertain). (Not: leaving it at nine in ten; the exact method first; skirts for the rest,
which would reverse decision 7.)

### Seams, part 2: stitching in the game (2026-10-08)

The camera's pass names, for each column it draws, the sides and corners past which a column one level coarser is
drawn (`LevelBalance.SidesOf`); each block along them (its x or z cell in the chunk 0 or 3, `MeshColumn.BlockSides`)
is drawn from a stitched mesh (`WorldMeshes.Stitches.cs`): the block meshed by `SurfaceNets.MeshTransition` from its own
voxels and the coarser level's block that holds it (block `b >> 1`, at `(b & 1) * 16` of its voxels). A stitched mesh
has its own heap range and counts as current while the sides, the block's version, and a count of changes to the
coarser block (raised whenever that block's version rises, apron included) are those it was read with; until then the
plain mesh is drawn and the block asks for one. The next frame reads the nearest asked for after the plain jobs, from a
pool of 64 stitch jobs that each hold two blocks' samples (6 MB). The near cascade takes the camera's choice; the far
cascade, whose columns are chosen apart, draws the plain meshes. A run-only debug view, `view plain`, draws every block
from its plain mesh, to compare.

Measured: in `WorldMeshesTests` (a stair of 1 cm steps without densities, which the two levels sample apart) the open
edges along the seams fall from 3,262 to 290, and after an edit across a seam from 3,368 to 284 once its stitched
meshes are made again. In the game (Release, the players' path at 12 m, 2560x1440 on DP-1), 250 of 1,047 drawn blocks
are stitched; the dark slits along the 10 and 20 cm levels' seam on the ground and on the pines' tiers are gone, and a
full-size picture with and without stitching differs in 766 pixels, all on those seams. The meshes' main-thread time
stays 0.3 ms a frame on average (0.31 standing, 0.27 to 0.37 flying 240 m in 4 m steps, 279 to 494 stitched meshes made
every 10 s), at most 2.4 to 2.9 ms in a frame while moving (the read budget is 2 ms).

My choices, routine (Lucy may change them):
- Stitched meshes are made when first needed, a frame or two after a level changes, not before (the plan said before,
  as children are before a split): the sides follow the levels drawn, which follow the plain meshes, so making them
  ahead needs a guess at the next frame's levels. The plain mesh shows the seam meanwhile.
- They count neither as pending (the loading screen and `IsUpToDate` do not wait for them) nor towards a column's
  being meshed (a split does not wait for them).
- The near cascade draws them too (one record a block; no light through the seam); the plan's first version kept the
  shadows plain.
- A block whose own voxels hold no surface gets no stitched mesh, though the coarser surface may cross its face: a rare
  hole, left for the exact method.
- `view plain` instead of the block-bounds view the plan named first: comparing the two pictures showed the seams'
  blocks without it.

What remains: the holes of a voxel or two where the levels disagree on which cells the surface crosses (the exact
method after the materials), and small patches where the finer level's surface lies in front of the coarser one's at
the boundary (on the pines' tiers; they show the same with and without stitching).

Fixed on the way:
- `MeshTransition` could reach for the coarser block's cell 32 (the fallback looking one above cell 31) and read past the
  samples (`IndexOutOfRangeException`, once at the game's start, that block drawn without a mesh); cells are now -1 to
  31 as a block's own, and a test meshes noise at all eight places with every side.
- The balance pass looped over its leaves through the interface, which boxed the enumerator: 96 bytes a frame since
  the balance pass was added, and `WorldMeshesTests`' allocation test failed from then until now.
- A block that finds no room in a full heap no longer counts as pending and is no longer read and meshed every frame:
  it keeps the mesh it had, and is read again once a second at most after a range has been freed (phase 3 item 3 still
  frees the farthest undrawn blocks first). A stitched mesh that finds no room is tried again the same way. With the
  heap full for long, a retry can loop once a second: a coarser block marked again makes the finer stitched meshes
  stale, making them again frees their old ranges, and that allows the next retry. Item 3's eviction should end it.
- Two reviews of the change found: `view plain` freed every stitched mesh (so leaving it made them all again); the
  fallback could take a coarser cell beyond the block's half, whose vertex lay up to three voxels outside the block,
  past its culling box and the vertex format's reach (now kept to the block's half: the tests' open edges stayed in
  the same ranges); a stale stitched mesh kept its range until replaced (now freed when the new plain mesh lands); and
  a coarser column loaded again did not mark the finer stitched meshes that read it. All fixed, with tests where a test
  can see them. A coarser cell outside the coarser block now throws instead of reading the next row.

### Edits (2026-10-08)

The edits' latency line (`Meshes: N edited blocks meshed and staged ... ms (F frames) after the first edit's change
was read`, Debug) counts only the blocks edits mark in drawn columns (`MeshBlock.Edited`, until a mesh of their voxels
since is staged, or they turn out empty or unreadable), no longer every pending block, so streaming does not stretch
it. A column's blocks are read nearest the view first by their centres (`MeshColumn.DirtyNearestFirst`), as the
columns already were, instead of bottom layer first. The line does not wait for an edited border block's stitched
mesh, which follows a frame or so after its plain one.

Measured in the game (Release, 2560x1440 on DP-1, VSync off, about 520 frames a second, the debug tools' digs from 4 m
above the players' path): a 50 cm sphere marks 15 to 16 blocks, staged 2.2 to 11 ms (2 to 3 frames) after its change
is read; a 2 m sphere (4 m across, the plan's "4 m sphere") marks 80 to 98, staged in 4.8 to 5.2 ms (3 frames).
Flying in 8 m steps while digging 2 m spheres, with columns streaming in: 24 to 80 blocks in 2.7 to 11.5 ms (2 to 5
frames, mostly 3). The meshes are drawn the frame after they are staged. Most of that time is the workers' meshing,
so at 60 to 144 frames a second an edit is staged the next frame and drawn the one after, which meets the plan's
"small edits shown within two frames". My choice: edited blocks get no queue of their own ahead of streaming; the
nearest-first order already reads them first when they are near, and an edit far away waiting behind nearer columns
is what the order means to do.

### Mesh memory (2026-10-08)

A full heap now first frees the meshes of columns the view does not draw (`WorldMeshes.Evict`): wanted columns that
neither the camera's pass nor the far cascade's drew in the last frame, nor would have drawn had they been meshed (the
finer columns of a near column waiting to split), and that are not being meshed, farthest first; first those a coarser
drawn column covers (the rings meshed ahead of a split: the coarser column stays drawn until they are meshed again),
then those finer drawn columns cover (parents: a column not meshed whose finer columns are now stays split until it is,
which also covers a parent still loading as the view moves away). It frees 16 MB more than the mesh needs, at most
128 MB a frame. An evicted column stays wanted, ignores changes, and is meshed again whole when the view needs it. Only
when nothing is left to free does a block go without room and the heap warn. Model blocks are staged the same way.

A review found two faults before the commit, both fixed: a column between the coarsest and the drawn ones (split, not
drawn) could be freed, and then its coarser column could no longer split, so the region fell back to the coarser level
(or a hole) until it was meshed again; now a column counts as drawable for a split while its own finer columns are
(`TryChildren(orFiner)`; the balance pass, which draws the four as leaves, still asks for meshed ones), and a test with
three levels and shadows frees everything at once and checks the next frames draw the same (without the fix, 714 blocks
of 1,205). And a column whose meshes were staged earlier in the same frame could be freed and its range staged again,
two copies to one place in one copy command (which Vulkan forbids): such columns are no longer freed, and the tests'
emulated heap now checks every frame's copies for overlap. Left as is: when the heap cannot hold even what the view
draws, a block without room still counts as meshed, so its column may show a hole until the next retry (a second at
most).

Measured with `tools/benchmark/run.sh` (8 views and the 3-minute circle, 2560x1440 on DP-1):
- At the default 1024 MB, half the mesh memory is in columns the view does not draw: 166 of 343 MB at the start view,
  266 of 535 MB in the forest. What the view draws is 180 to 270 MB.
- At `MeshMegabytes=512` the forest fills the heap to 500 MB and one round frees 15 columns (34 MB); on the circle,
  most 10 s spans free 5 to 25 columns (16 to 56 MB). No warning, no error; every view's GPU time within 0.02 ms of the
  1024 MB run's; the meshes' main-thread time on the circle 1.64 ms against 1.65; frame times alike (p99 2.48 ms).
- Frames that draw a wanted column not meshed yet (a hole): alike at both sizes. Up to 13 to 23 columns while a view's
  new ground loads after the benchmark's jumps, and on the circle a few frames a span with one column at the view's far
  edge (2 to 27 frames at 512 MB, 1 to 38 at 1024 MB).

The meshes' main-thread time is about 1.05 ms a frame in the forest and 1.65 ms on the circle at either size, against
0.3 ms at the start view: more columns and blocks to select and list. It is not new; phase 3's end looks at it. On the
committed code at 512 MB the circle freed 272 columns (760 MB) in its 10 minutes, with no warning; one 10 s span's most
was 6.3 ms on the main thread against 4.3 to 5.0 ms in the runs before.

Lucy's answer (2026-10-08, my recommendation): **the mesh heap's default is 512 MB** (`Engine:Renderer:MeshMegabytes`),
the phase's budget, freeing undrawn meshes when full. (Not: keeping 1024 MB; 768 MB.)

### The anti-aliasing row (2026-10-08)

The Graphics tab's Anti-aliasing is a slider through Off, SMAA, MSAA 2x, 4x, and 8x (decision 11; `AntiAliasMode`), kept
in `settings.json` as SMAA's switch and `MsaaSamples` (so `Engine:Renderer:AntiAliasing` and `MsaaSamples` stay the
options, and the command line still wins for a run); the console's `settings aa off|smaa|2x|4x|8x` does the same. A
change of the samples takes effect at once (Lucy's answer): the render thread makes the swapchain again with the device
idle, and the mesh pass makes its pipelines and multisampled images with the new count. Switched live through all five
in a Debug run (validation with sync and best practices): no warning or error. GPU time of the world at 2560x1440,
scale 1, from the players' path: 1.26 ms with one sample, 1.65 with two, 1.95 with four (the default), 2.52 with eight;
SMAA adds 0.30. The patches on the path keep their steps in every mode: they are where a vertex's material changes, not
the edge of a triangle (the material recipes of item 7 replace them). Dragging the slider across several modes remakes
the pipelines and images once a frame for each step crossed, a short stutter behind the menu; a device that offers
fewer samples than asked draws with the most it offers, while the row shows the choice.

### Shadows (2026-10-08)

The two cascades no longer meet in a line at 30 m: the near cascade blends into the far one over the 5 m before its
end (the far cascade's slice now starts at 25 m, so it covers the band), and the far cascade's shadows fade out over
the last tenth of its reach instead of stopping (`MeshLighting.NearBlendStart`, `FarFadeStart`; `mesh-light.glsl`'s
`sunShadow`, which the particles share). In the forest the line where the ground's shadows changed from sharp to blocky
is gone (pictures sent to Lucy).

Contact-hardening shadows in the near cascade (decision 12): a blocker search reads the maps as depths (a second binding
of the same image, no comparison; binding 9), takes the casters' mean depth, and the filter's 16 comparison taps of a
Poisson disc spread over the blur the sun gives that gap (1 to 16 texels): sharp where caster and shadow meet, soft far
from them. The far cascade keeps its 3x3 lookups (its texels, about 17 cm, are as wide as that blur). The maps hold only
faces turned from the sun, so the gap is measured to the caster's far side; thin casters (trunks, leaves) show no
difference. Measured at 2560x1440, MSAA 4x, scale 1 (the world pass, `Voxels`): forest 2.83 ms with 3x3, 3.21 with a
16-tap search, 3.05 with 8; a close view 2.66, 3.04, 2.88; the start view 2.06, 2.27, 2.16.

Lucy's answers (2026-10-08, each my recommendation): **a sun 2 degrees across** (as the raymarcher's soft sun; a 10 m
gap blurs 35 cm), **and an 8-tap blocker search** (as good as 16 in the close view, edge pixels within 32 of 255, for
half the cost). (Not: 0.5, 1, or 3 degrees, where 3 hit the 16-texel cap and showed stepped copies of the edge; 16 taps.)

Seen while looking at the shadows: in the forest a band of the darker grass has one straight side tens of metres long
(not a block or level edge: the wireframe and the levels view show none). It comes from the ground's materials, not the
renderer; left for the materials' work (item 7) or a look at the generator.

### Clustered lights (2026-10-08)

Events light the world: `PointLights` (run-wide; the frame systems `Add` a light that shines while something happens and
`Flash` one that fades as the square of its time left; `GameLoop` starts it each frame before the frame group, hands its
lights to `WorldMeshes.Upload`, and clears it when a match ends). The game's effects lit a rifle's muzzle
blast (6, 0.06 s), a bazooka's launch at the muzzle (12) and out of the rear (20, 0.15 s), a warhead's blast (150,
0.5 s), a laser's burn (2.5, 0.3 m off the surface; five times for the gigalaser), and a head's eyes (1) and burns (4),
in the colours of their particles; the numbers are each light's power, what it gives a surface facing it 1 m away
(placeholders for Lucy's look). My choice: a service the effects call, not a light component and an extract system as
planned, since none of these lights belongs to an entity; a component waits for lights that do (towers, torches).

The world's block lights and the events' are ranked together (power over distance squared, within 48 m, the reach
meeting the frustum), and the light budget's brightest go to the GPU (`Engine:Renderer:MostLights`, 128 by default, 1 to
256; the shader's ceiling is 256). A compute pass before the cascades (`mesh-clusters.comp`) sorts them into the view's
clusters, 16 x 9 tiles of the drawn image by 24 slices of depth (the first to 1 m, the last from 48 m, between them a
fixed ratio), each keeping up to 32 of those that reach it, the ones that matter most to it (power over the squared
distance to its box); `mesh.frag` loops over its pixel's cluster's instead of every light. Keeping the first 32 in the
camera's ranking dropped a laser's burn in the fortress's tunnel, whose 47 brazier lights all reach the clusters near
the camera; a burn's light also sat 5 cm off the wall at first and lit almost nothing (light that grazes a surface).

Measured (Release, 2560x1440, MSAA 4x, scale 1; the world pass): over the benchmark's views with no event lights the
clusters cost 0.03 to 0.06 ms, and the tunnel, with its brazier lights, is 0.17 ms quicker (2.12 to 1.95 ms; two runs
each, within 0.01 ms). With the console's `lights test N` in the tunnel (lights within 25 m of the camera): 2.04 ms with
none, 2.62 with 32, 3.69 with 128. The meshes line reports the lights shaded and the events' offered.

Lucy's answers (2026-10-08, each my recommendation): **the event lights keep their placeholder powers until she judges
them in play** (not: stronger now; more pictures first), **and the light budget stays 128** (not: 64; 256).

### Materials, part 1: the recipes ported (2026-10-08)

The raymarcher's eight surface recipes draw the world's meshes per pixel (`mesh-surface.glsl`, from `voxel-surface.glsl`
in the archive): the tint map's lookup moved to the caller, the texture-filtering switch kept on, and the
footprint is the raymarcher's (the pixel's width at its distance over the square root of how obliquely the view meets
the surface; a first version from screen-space derivatives faded too early where a surface's stretch runs diagonally on
screen, found by a reader's review). `MeshMaterial` carries the packed look and amounts and the accent colour (48
bytes). The relief tilts the normal the light meets, the hollows take sky light, and a surface turned from the sun stays
dark whatever its relief, as in the raymarcher; the shadow maps and the sky field are read along the mesh's normal.
Models keep flat colours (their recipes would need a point in the model's own space). All eight were ported at once
rather than pebbles and bark first; each pattern's cost was then measured by turning it off.

Measured (Release, 2560x1440, MSAA 4x, scale 1; the world pass; two runs each in turns, within 0.02 ms):

| View | Flat colours | Recipes |
|---|---|---|
| start | 2.03 | 3.58 |
| start-low | 2.00 | 3.54 |
| forest | 2.55 | 4.24 |
| forest-down | 2.60 | 3.71 |
| forest-up | 2.78 | 4.53 |
| canopy | 1.79 | 3.54 |
| trail | 2.36 | 4.29 |
| tunnel | 1.96 | 2.93 |
| flight | 2.48 | 4.12 |

Four are over the 4 ms budget. By pattern, over five views: the leaves' clusters 0.35 to 1.05 ms, the rock's strata
0.48 (start), bark 0.03 to 0.38, grass clumps 0.02 to 0.27, pebbles up to 0.19, and the large patches about 0.4 ms
everywhere. At one sample the recipes add 1.1 to 1.3 ms, so MSAA is not the main cause. The camera draws 2.9 million
triangles for 3.7 million pixels: far ground and foliage are thin triangles a pixel or two wide, and the GPU shades
pixels in 2x2 groups for every triangle that touches them, so each visible pixel runs the recipe about two to three
times; the raymarcher shaded each pixel once, and its recipes and tone curve cost about 0.6 ms of its 13 ms pass
(2026-09-27). The shader went from 1,115 to 6,147 instructions and from 48 to 64 vector registers (20 to 16 waves a
SIMD). Skipping a pattern's work where it has faded away (the same picture) saved 0.01 to 0.08 ms; one leaf octave
instead of two saved 0.12 to 0.35 ms, the 0.6 m patches over x and z instead of in 3D 0.12 to 0.16 ms, and a hash of
24-bit multiplies was 0.07 ms slower. Stills along the trail 3 cm apart show the pebbles fading with no sparkle; Lucy's
look in motion is the real test. Borders between materials show 10 cm steps (one material a triangle), which the 1 cm
raymarcher hid.

Lucy's answer (2026-10-08): before choosing, build one ground material each way, textures scanned from photographs and
textures baked from our recipes, and show her both.

### Materials, part 2: a texture prototype on the trail (2026-10-08)

Lucy asked for the usual way games make materials (textures) against ours (recipes worked out per pixel), and for one
ground material built each way. The console's run-only `materials` draws the trail from its recipe, from a texture
**baked** from the recipe, from a texture **scanned** from a photograph, or in its flat colour; `all` draws every world
material that way, to time it over the whole picture.

- **Baked:** `material-bake.comp` works the pebbles recipe out once at start over a 2.56 m tile, its lattices wrapped
  so the tile repeats seamlessly (`surface-noise.glsl`, the recipes' noise split out of `mesh-surface.glsl`, with
  `MESH_SURFACE_TILE`), into masks (grain, pebble cover, shade, hollow) and the relief's slope, 2048 texels square
  (1.25 mm a texel); mip levels average them, which does what the recipe's fades do per pixel. The material's own
  colours, tint, contrast, and relief colour it, so the corruption still moves it.
- **Scanned:** Poly Haven's `forest_ground_04` (CC0, 3.15 m): colour, an OpenGL normal map, and occlusion as the
  cavity, 2048 texels square, in `Textures/Prototype/` next to the executable (not in the repository). The tint moves its
  colour as it moves the material's own.
- Both are read from three sides blended by the normal (triplanar), sides under 5 % of the blend not read, with
  derivatives taken before the branches; filtered through the mip levels and 16 times anisotropically
  (`samplerAnisotropy`, enabled where the device has it). The large patches stay procedural on top.

Measured (Release, 2560x1440, MSAA 4x; the world pass at five views through the control port, every world material
drawn each way; within about 0.05 ms between runs):

| View | Flat | Recipes | Baked | Scanned |
|---|---|---|---|---|
| canopy | 1.87 | 3.46 | 2.72 | 2.70 |
| forest-up | 2.85 | 4.52 | 3.73 | 3.71 |
| forest | 2.63 | 4.24 | 3.49 | 3.47 |
| trail | 2.43 | 4.25 | 3.45 | 3.43 |
| start | 2.09 | 3.57 | 2.93 | 2.91 |

Textures add 0.8 to 1.0 ms, the recipes 1.5 to 1.8. Reading all three sides everywhere cost 1.2 to 2.6 ms over flat
(the trail 5.0 ms), more than the recipes; one side alone 0.5 to 0.9. Anisotropic filtering 16 times costs 0.2 to
0.46 ms over none (8 times about 0.06 to 0.27). The patches cost 0.05 to 0.24 ms under textures. The textures are
uncompressed (4 bytes a texel; block compression would read a quarter or less). The renderer's shading of pixels two to
three times over (thin far triangles) applies to textures as to recipes. Up close the baked trail matches the recipe a
little softer; from 10 m on they look the same; the scanned one is darker earth with real stones and twigs, and stands
apart from the recipe-drawn gravel at its edges. No tiling shows from 14 m up looking along the trail.

## Materials plan: scanned photographs first, recipes as the fallback (2026-10-08)

Lucy's direction (2026-10-08): the world's materials come from scanned photographs (CC0: Poly Haven, ambientCG; both
allow commercial use, redistribution in a game, and no credit, checked on their licence pages that day) wherever a good
one exists, and from our own recipes where none does. This replaces the rest of item 7 (the remaining recipes and new
flagstone and wall-course recipes).

**Where it stands.** The prototype (part 2) draws the trail baked or scanned; over five views textures add 0.8 to
1.0 ms to the world pass, the recipes 1.5 to 1.8. Of the palette's 76 materials, 38 are the world's (the others are
the models', which keep flat colours). The world's fall into four groups: the forest floor (most of the picture), trees
(bark, wood, and leaves), rock and stone (including the road's flagstones and the walls' courses), and the corrupt side's
materials (glowing, or nothing real to photograph).

Facts gathered (two readers, 2026-10-08, and this machine's `vulkaninfo`): block-compressed textures
(`textureCompressionBC`) are on 99.6 % of Windows, 97.9 % of Linux, and all macOS Vulkan devices (gpuinfo.org;
MoltenVK passes on Metal's `supportsBCTextureCompression`, which Apple says every Mac has); anisotropic filtering on all
Windows devices and always on MoltenVK. `VK_KHR_fragment_shader_barycentric` (a pixel reading each corner of its
triangle) is on about 42 % of desktop devices and **not on the RX 5700 XT** (RADV gives it from RDNA2 on; this
machine's iGPU has it), so the borders cannot rely on it. BCnEncoder.NET (MIT or Unlicense, 2.3.0 of March 2026, pure
managed code) encodes BC1 to BC7 into KTX or DDS. Borders in terrain engines are usually blended by height (each
material's height map decides which shows, Mishkinis's "Advanced Terrain Texture Splatting", Unity's terrain), from
weights stored per vertex where barycentrics are missing (Terrain3D: two materials and a blend per vertex). Hex tiling
(Mikkelsen 2022) hides repetition at three reads for one.

**The design.**

1. **One texture path for every world material.** A material's set is three layers: colour with occlusion (BC7),
   normals (BC5, z rebuilt in the shader), and height (BC4, for the borders). A photograph fills them from its files; a
   recipe is baked once by the game into images of the same layers (the prototype's bake made general and written out
   by a console command: what is finer than a tile is baked, what is larger stays per pixel: the patches, the strata's
   bands by height, the grass's hue drift) and goes through the same tool, so every set loads the same way. The
   per-pixel recipes go, and `mesh.frag` keeps one material path: the sides the surface faces, read through mip levels
   and filtered anisotropically.
2. **Each photograph is matched to its material's palette colour** (its mean moved to `VoxelLook.Colour`, its
   variation kept), so the palette and the corruption's tint keep their meaning.
3. **A tool in `tools/`** downloads each photograph by name (or takes a baked recipe's images), matches and compresses
   it, and writes the files the game loads, with a list of every photograph's name, site, licence, and link.
4. **Borders blended by height:** each vertex carries how much of its block's main materials surround it, a pixel
   blends those weights, and the material whose height plus weight is greatest shows, with a soft band where two meet.
   Pixels inside one material read one set; pixels at a border read two.
5. **Repetition** hidden by the patches we have; hex tiling only where a field still shows it, with pictures first.
6. **Settings:** a texture quality that drops the largest mip level (half or a quarter of the memory); anisotropic
   filtering 16 times now, 8 offered if a step needs the time (8 is up to 0.25 ms quicker looking along the ground).

**Memory** for one set (three layers, with mip levels): 13.3 MB at 2048 texels, 3.3 MB at 1024. About 25 photographs
and 10 baked recipes: about 470 MB at 2048, 120 MB at 1024, beside the 512 MB mesh heap. At 2048 a 2 to 3 m
photograph has 700 to 1,000 texels a metre (a pixel at the player's feet covers about 2 mm, 1440p, 90 degrees); at 1024
half that, softer at the feet.

**The work, in order** (each step with pictures and numbers to Lucy; about 6 to 8 days):

1. Texture sets as data (about a day): the palette names each material's set; a run-wide registry loads the sets into
   the arrays at start, compressed; the tool and the list of sources; the prototype's `materials` command kept for
   comparing.
2. Group A's photographs (half a day, then Lucy's choice): two or three candidates for each shown in the game on the
   same views; colour matching.
3. Borders (one to two days): the vertices' weights (the mesher, `MeshVertex`, the stitched meshes), height blending,
   the cost at borders measured.
4. The general bake (one to two days): every recipe that stays baked; every world material on the texture path; the
   per-pixel recipes removed.
5. Groups B, C, and D (one to two days, with Lucy's choices): candidates, matching, any corrupt-side photographs.
6. The phase's end: the benchmark against 4 ms and memory, docs, rules, memory.

Lucy's answers (2026-10-08, each my recommendation): **the forest floor's sets at 2048 texels and the rest at 1024**,
about 195 MB in all (not: 2048 everywhere, about 470 MB; 1024 everywhere, about 120 MB); **the compressed files in git
directly** (not: Git LFS; downloaded by the tool); **borders blended by height from weights in the vertices**, on every
GPU (not: the steps left; the barycentric extension where a GPU has it, which the RX 5700 XT has not).

### Materials step 1 done: texture sets as data (2026-10-08)

A material's look names its texture set (`VoxelLook.Texture`; the trail's is `forest-ground-04`), with a colour match
(`TextureColourMatch`, 0 to 1, 0 by default: the photograph's own colours, which Lucy liked; 1 moves its mean to the
palette colour). `tools/Textures` makes each set of `src/Game/Textures/sources.json` from its CC0 photograph
(downloads cached outside the repository; mip levels in linear light, normals made unit length again; BC7 sRGB colour with
occlusion in alpha, BC5 normals, BC4 height through BCnEncoder.NET; about 100 s for a 2048 set) into the set's folder with
`set.json` (texels, metres, the colour's mean) and `SOURCES.md`. The game copies the folders to `Textures/`;
`MaterialTextureSets` reads them at start into six arrays (large and small, three layers each), copied in the first
frame from an upload buffer that goes three frames later (my choice of the two the advisor named: no one-time submit
before the render thread exists). `textureCompressionBC` is now a required device feature. The prototype's photograph
in the build folders and its baked layers are gone; `materials` takes `textures`, `recipes`, or `flat`, with `all`.

Measured (Release, 2560x1440, MSAA 4x, every world material on the set, five views): 2.66, 3.64, 3.42, 3.37, 2.87 ms
(canopy, forest-up, forest, trail, start), 0.04 to 0.07 ms under the prototype's uncompressed textures and 0.79 to 0.95
over flat colours. The trail looks as the prototype's photograph did; the photograph's mean colour (0.156, 0.111,
0.066) is about two thirds of the trail's palette colour (0.24, 0.17, 0.10).

### Materials step 2 done: the forest floor's photographs (2026-10-08)

Sixteen candidates were made and shown in the game on their own materials (the console's run-only `materials set ID SET
[MATCH]`, which re-stages the materials; `materials sets` lists the loaded sets), from a road near the game's start
(tint near 0), the trail (about 0.3, whose tint moves a photograph as it moves the material's own colour: a grey moss
photograph put on the grass turned pink there), and the enemy's base. Lucy chose one photograph for each material of
the forest floor and every photograph's colour **halfway to its palette colour** (`PhotoColourMatch` 0.5; not: the
photograph's own; fully the palette's; per material). The others were removed. ambientCG lists its grass and moss as
photographs ("height field photogrammetry"); it gives no width for Moss002 (2 m taken). A photograph of dry ground with
rocks showed its tiling as diagonal rows on the base floor.

Measured (Release, 2560x1440, MSAA 4x, 90 degrees, five views; the forest floor from photographs against every
material from its recipe): canopy 3.44 (3.46), forest-up 4.53 (4.53), forest 4.06 (4.24), trail 4.02 (4.24), start 3.51
(3.57) ms. Forest-up stays over the budget for the leaves' and bark's recipes, which steps 4 and 5 replace. Eight sets
of 2048 texels are 106 MB on the GPU and in git. Recipe-drawn neighbours (the road's flagstones, group C) still stand
apart.

### Materials step 3 done: borders between materials (2026-10-08)

A mesh's palette is its surface's eight most common materials (two words before its vertices in the heap), and each
vertex's fourth word (`MeshVertex.Weights`, 16 bytes a vertex now) holds how many of its cell's surface corners are
each, four bits a slot; a material past the eighth counts as the vertex's own. Blended across the triangles, the
weights say where borders lie; `mesh.frag` draws, of the two materials that weigh most at a pixel, the one whose share
plus its height (its texture set's height map, or 0.5 for a recipe) is greater, so the border wanders round the stones
of the height maps instead of stepping at 10 cm. Of 9,513 meshes uploaded over a run of the five views, 166 (1.7 %) had
more than eight surface materials (the meshes line counts them). The mesh heap reached 511 of its 512 MB after all five
views (vertices a third larger); freeing undrawn columns kept it in bounds.

Measured (Release, 2560x1440, MSAA 4x, 90 degrees; the world pass, ms; before the steps were blended / a soft band
where two materials meet, two surfaces worked out / a hard edge / a hard edge within 25 m only): trail 4.02 / 4.46 /
4.34 / 4.27; forest-up 4.53 / 4.85 / 4.81 / 4.77; start 3.51 / 4.09 / 3.82 / 3.77; canopy 3.44 / 3.77 / - / 3.67;
forest 4.06 / 4.40 / - / 4.28. Most of the cost is the weights every vertex carries and passes on, not the border
pixels: without the border's choice the trail view took 4.18, with constant weights 4.00; four weights in one output
instead of eight in two saved 0.03 to 0.08. Full-size crops of the trail's edge show the hard edge and the soft band
almost alike. Lucy's choice (2026-10-08, my recommendation): **the hard edge at every distance** (not: within 25 m; the
soft band; the steps kept). The stitch test's 100-frame allocation window failed one run in three beside the mesher's
tests (16,800 phantom bytes from collections other threads set off; none alone, and none before the larger vertices);
it now measures with `SystemAllocation.BytesAllocatedWithoutCollection`, which runs the window again when a collection
fell in it, so a real allocation still fails.

### Materials step 4 done: the recipes baked (2026-10-08)

No recipe is worked out per pixel now. The console's run-only `materials bake ID top|side [NAME]` has the render thread
run `material-bake.comp` (the recipe's pattern, `mesh-surface.glsl`'s `surfacePattern`, at every detail over a 2.56 m
tile that repeats, on the plane of x and z or of x and y) into a 1024-texel image of three layers, read it back when
its frame slot comes round again, and write `colour.png` (at no tint), `occlusion.png` (the hollows), `normal.png` (the
relief's slope as a normal map), and `height.png` (the hollows: crests high, cracks low) into the data folder's
`Bakes/NAME/` (`MeshPass.Bake.cs`). `tools/Textures` makes them into a set (`"site": "bake"`) as it makes a
photograph's, and moves every set's heights to a mean of a half, so at a border only the two materials' shapes differ
(the eight photograph sets were made again for it). 24 materials are baked: on the side's plane bark, conifer and log
bark, dead wood, wood, stone, rock, masonry, the two wall stones, bone, and mushroom stems; on the top's leaves,
needles, bush leaves, ash, clay, the two flagstones, rock moss, mushroom caps, iron, embers, and lava. Strata's bands by
height (rock darkening with depth), which a 2D tile cannot hold, go on per pixel (`strataBands`, `MeshMaterial`'s bands
bit); a material with no pattern (tower stone, the tunnels, glow caps) is drawn plain, its colour with the patches;
glow stays a material's own. `materials` takes `textures` or `flat` now (no `recipes`).

Measured (the benchmark, Release, 2560x1440, MSAA 4x, two runs each; the world pass against the recipes per pixel of
part 1, before the photographs and the borders): start 3.58 to 3.28, start-low 3.54 to 3.25, forest 4.24 to 3.63,
forest-down 3.71 to 3.27, forest-up 4.53 to 3.84, canopy 3.54 to 2.87, trail 4.29 to 3.68, tunnel 2.93 to 2.55,
flight 4.12 to 3.60 ms: every view within the 4 ms budget, 8 to 19 % under the recipes, with the photographs and the
blended borders. The shadow maps take 3 % more (vertices a third larger). Before the bake, every material on the first
large set gave 3.83 ms at forest-up, the floor this step aimed at. The baked sets look as the recipes did (bark, leaves,
needles, the wall's stone and bands, side by side at five views); 32 sets are 187 MB on the GPU.

### The photographs filtered for a painted look (2026-10-08)

Lucy asked to try an anisotropic Kuwahara filter on the scanned textures to stylize them. `tools/Textures` filters a
set's photograph before its mip levels (`Kuwahara.cs`: each texel's direction and how strongly the picture is directed
from the smoothed structure tensor of its gradients; an ellipse round it, stretched along that direction, cut into
eight sectors with polynomial weights; the sectors' means weighted by how little their colours vary; wrapping at the
edges): 2 to 13 s for a 2048-texel photograph at radius 6 to 20. On the photographs alone, radius 6 gives clear strokes,
12 and 20 wash them out, and stony ground lightens (the dark gaps between stones vary most, so they lose). In the game
the change is small at a distance and a softer, painted ground up close; filtering the colour alone leaves the normal
map and occlusion bringing the photograph's detail back through the light, so all three maps are filtered; larger
radii read as smears at grazing angles. A filter on the drawn frame (strokes of one screen size on everything,
estimated 1.5 to 3 ms at 1440p) was offered and not taken. Lucy's choices (2026-10-08, each my recommendation): **the
filter on the textures** (not: on the frame; both; dropped), **radius 8 on all three maps** (not: 6 on all; 6 on the
colour; per material), **on all eight photographs** (not: grass and trail; the bakes too). It costs nothing at runtime.

## Art direction: a stylized dark-fantasy look (2026-10-08)

Lucy liked the photographed and filtered sets better than the recipes, but not the look: she wants a stylized
dark-fantasy art style like Dota 2's, clear and readable, where the textures were too noisy and detailed. I offered
ways there, each to be shown in pictures before anything was kept; Lucy chose four to try:

- **A, calmer textures**: each material's set drawn with less detail (its colour moved towards the set's
  mean), less relief (the normal map's and the occlusion's strength), and a softer read (a mip bias), from
  `VoxelLook.TextureDetail`, `TextureRelief`, and `TextureBlur` (`MeshMaterial.TextureStyle`); the console's run-only
  `materials tune ID DETAIL RELIEF BLUR` tries them. "Calmer" (0.3, 0.3, 2) on every set read much cleaner than the
  photographs. The defaults stay 1, 1, 0.
- **E, light and grade:** the world's surfaces a quarter less saturated and 15 % darker (the characters stand out),
  their sides darker than their tops, warm sunlight and cool sky light, deeper inner corners (`OcclusionFloor` 0.6 to
  0.35), and after the grade the dark tones turned towards teal and the light ones towards orange. The warm and cool
  light and the split tone fall on the models too; the sky and the particles keep the old grade. **Kept as tried**
  (mesh.frag's constants, `lighting.glsl`'s `OcclusionFloor`).
- **C, painted CC0 sets:** a reader searched OpenGameArt, itch.io, Kenney, ambientCG, and Poly Haven for tiling
  painted textures free for a commercial game. The pool is thin: usable grass and dirt (512 texels), rock and cobbles
  (1024, with height and normal maps), each author in a style of their own, and nothing good for bark, needles, leaves,
  moss, or ash. Eleven were tried on the grass, dirt, trail, flagstones, and cliffs. Not chosen.
- **D, stylized recipes:** recipes designed for the look and baked into sets as before: big shapes, a flat fill per
  shape with a small step of hue and value, a light rim inside each shape's edge, dark gaps, little fine noise, and the
  shapes' relief in the height map (so borders between materials follow them). A first try of flagstones, grass,
  needles, and bark: the flagstones and bark read well; the grass and needles (overlapping tufts) Lucy "greatly
  disliked": too noisy, "yellow splotches of liquid on a green background"; the grass she liked before the renderer
  rework (the raymarcher's clump recipe) was much better. **Chosen:** stylized recipes for the world's materials, with
  grass and needles kept close to their original recipes.

A shader reload now also makes the bake's pipeline again (`MeshPass.ReplaceBakePipeline`), so a recipe can be edited
in a shader copy (`Engine:Renderer:ShaderSourceDirectory`) and baked again without a restart; new sets still load only
at start (`MeshPass.Initialize`).

### Art direction: recipes in a calm style, no photographs (2026-10-08)

Lucy chose the stylized recipes (D) and the light and grade (E) over the photographs and painted sets, and kept the
result group by group: the forest floor, bark, stone, and the rest.

- **Soil first.** Grass: the grass recipe of the raymarcher era baked into a set (`grass`, 1024 texels), chosen over the
  photograph and a calmed bake. Then stylized flagstones and soil with pebbles of varied size were tried. Her verdict:
  the pebbles on soil "look clunky and unnatural", the soft patches under them are fine, gravel is fine, and the
  flagstones' "randomized lines" are "too noisy": "I'd basically just like textures which are a few steps up from flat
  colors, but nothing too fancy for now. We can iterate on them later; I just want to get some basic, easily-readable
  textures down for now." So three new recipe kinds were written, each baked into a set:
  - `VoxelPattern.Soil` (`mesh-surface.glsl`'s `soilAt`): the colour in soft round patches of 0.15 to 0.6 m (gradient
    noise: value noise's square cells showed), a little lighter and warmer or darker and cooler, sparse light and dark
    specks a few centimetres wide, and pebbles in the accent's share of cells of the feature size (none at a share of 0)
    with a sparser layer twice their size, each a flat fill lighter in its middle with a light rim and a ring of
    shadow. Dirt, trail, base floor, bare earth, and ash take it without pebbles, gravel with pebbles in 85 % of 8 cm
    cells, mushroom caps with pale spots, embers with coals. The relief comes from the height half a centimetre to each
    side.
  - `VoxelPattern.Streaks` (`streaksAt`): soft streaks the feature size wide and the aspect times as long up the plane
    the surface faces, in two tones, with soft furrows where the streaks' gradient noise crosses its middle, darker and
    towards the accent by the accent's share. The first bark bakes were a dense network of small plates, the busiest
    thing on the trees. Lucy chose streaks for mushroom stems and wood; for bark and wood on trunks she then asked for
    the raymarcher's `Plates` (8 cm, conifer bark twice as tall, with its red inner bark) and `Grain` (1 cm by 8),
    baked on the plane of x and y, the side a trunk shows.
  - `VoxelPattern.Mottle` (`mottleAt`): the colour in soft round patches of half to twice the feature size, lighter and
    warmer or darker and cooler by the contrast, over a faint 4 cm grain; strata's bands by height still go on per
    pixel. Stone, rock, the wall stones, clay, bone, iron, and lava take it (their old grain and pebble bakes showed
    value noise's square cells).
- **What stayed.** The roads keep their old flagstone bakes, masonry its blocks, and the leaves, needles, and bush leaves
  their old cluster bakes. Moss and clover went back to their own recipes, baked. With that no material is drawn from a
  photograph: the scanned sets and `PhotoColourMatch` went, every set is 1024 texels (the large arrays hold only their
  4-texel stand-in), and `tools/Textures` keeps its photograph and Kuwahara paths for later.
- **Measured.** Two benchmark runs against step 4's (RX 5700 XT, 2560x1440): the world pass 2 to 3 % slower everywhere
  (start 3.28 to 3.36 ms, forest 3.63 to 3.72, forest-up 3.84 to 3.93, canopy 2.87 to 2.97, trail 3.68 to 3.77, tunnel
  2.55 to 2.60, flight 3.60 to 3.70): the style controls and the light and grade; the shadow maps the same. Every view
  stays under the 4 ms budget, and the sets take less memory than before. The full suite and `validate.sh` passed. The
  materials plan's step 5 (photographs for the other groups) is replaced by this: the game's own simple recipes,
  baked.

### Glow after the grade (2026-10-08)

Phase 3's item 8, roadmap item 3's leftover. Glowing materials (embers, lava, glow caps) were lit and glowed before the
tone curve and the grade, and took the art direction's desaturation too, so the braziers' coals and the lava falls near
the enemy, where the grade greys the picture, came out salmon and washed out. Two ways were shot (the fortress hall,
two braziers near, a lava fall, the forest): A, only the glow's own brightness through the tone curve added after the
grade and the split tone; B, that, and a glowing surface also left out of the grade, the split tone, and the world's
desaturation and darkening where the fog leaves it. A reddened the hall's coals but left the near ones salmon, since
their own brazier light lights them and that part was still graded; B gave vivid red coals and orange-red lava. B is
in (`mesh.frag`); the forest is unchanged, as nothing there glows.

### The aim dot moves to the HUD (2026-10-08)

Phase 4's last item. The aim dot was drawn by whichever pass ran last (the world's and the sky's shaders, the particle
cubes, SMAA's blend, or RCAS), with a view switch, two push constants, and the render thread choosing. It is now the
HUD's lowest layer (`AimDotLayer`, a match's layer the engine adds before the game's): the same pixels (a white
square where a pixel's centre lies less than 1.5 pixels from the screen's centre along each axis, black less than 2.5:
2 in 4 on an even side, 3 in 5 on an odd one; `AimDotLayerTests`), at the screen's own pixels at any render scale,
hidden while the loading screen is drawn. `ViewSwitches.AimDot`, `SmaaConstants.AimDot`, `RcasConstants.AimDot`, and
the five shaders' code went. Checked in the game with SMAA at render scale 1 and 0.6: 2 by 2 white in 4 by 4 black at
2560x1440, and nothing without the HUD.

Phase 3 and phase 4 are done, apart from the seams' look test, which Lucy takes after phase 6 (they show most on
trees, which become objects there); the event lights passed hers (2026-10-08).
