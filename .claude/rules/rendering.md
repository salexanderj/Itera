---
paths:
  - "src/Itera.Engine/Rendering/**"
  - "src/Itera.Engine/Meshing/**"
  - "src/Itera.Engine/Windowing/**"
  - "src/Itera.Engine/Systems/{AtmosphereSystem,CameraExtractSystem,DebugOutlineSystem,LooseBodyModelSystem}.cs"
  - "tests/Itera.Engine.Tests/Rendering/**"
  - "tests/Itera.Engine.Tests/Meshing/**"
  - "tools/benchmark/**"
  - "src/Game/WorldGeneration/{GamePalette,GameMaterials,GameAtmosphere}.cs"
---

## Rendering

The game opens a Vulkan window and draws the resident voxels as triangle meshes through a raster pipeline (the
redesign's decisions and measurements are in `docs/rendering-redesign.md`; the shelved raymarcher is on the branch
`raymarcher` and the tag `raymarcher-final` of the Zorf TD archive, `salexanderj/ZorfTD-archive`). The world is meshed
from its 10 cm voxels near the camera (to 16.8 m) through 20 and 40 cm to 80 cm far (four levels stream by default,
`Engine:Voxels:Levels`), out to `Engine:Renderer:ViewDistanceMetres` (256 m), where fog hides it in a sky. The sun
lights it through two shadow cascades; the sky (more under overcast) and the sunlit ground's bounce light the shade,
scaled by a sky-visibility field and each vertex's openness; glowing materials give point lights; a filmic tone curve
ends the frame. The world's materials are drawn from texture sets, each patterned material's recipe baked into a set
once (`mesh-surface.glsl`'s `surfacePattern`, the recipes `VoxelPattern` names: `Soil`, `Streaks`, `Mottle`, `Clumps`,
`Plates` (baked on the plane of x and y), `Grain`, and others; the data in `VoxelLook` and the game's `IVoxelPalette`,
packed into `MeshMaterial`'s look and amounts words); no recipe is worked out per pixel (`tools/Textures` can also make
sets of CC0 photographs). The art direction (`docs/rendering-redesign.md`): a stylized look, less saturated and darker,
textures a few steps up from flat colours, no outlines, cracks, or scattered features unless the material is made of
them; its light and grade are data a game sets (the look, below). A material without a set and without a
pattern is drawn plain (`surfacePlain`: its colour with the patches); the models keep flat colours. Colours blend from a
material's colour to its tint colour by a game's tint map (`IVoxelTintMap`; none by default, `FieldTintMap` from any
field).

A material whose look names a texture set (`VoxelLook.Texture`) is drawn from it (the materials plan in
`docs/rendering-redesign.md`: the recipes baked, or CC0 photographs). The console's run-only
`materials bake ID top|side [NAME]` bakes material ID's recipe (`MeshPass.Bake.cs`, `material-bake.comp`: the pattern at
every detail over a 2.56 m tile that repeats, `surface-noise.glsl` with `MESH_SURFACE_TILE`, on the plane of x and z or
of x and y) into 1024-texel images in the data folder's `Bakes/NAME/` (colour at no tint, occlusion from the hollows, a
normal map from the relief's slope, height from the hollows), which `tools/Textures` makes into a set as it makes a
photograph's (`"site": "bake"`; a shader reload makes the bake's pipeline again, so an edited recipe bakes without a
restart); every set's heights are moved to a mean of a half. Strata's and mottle's bands by height, which a
2D tile cannot hold, go on per pixel (`strataBands`, `MeshMaterial.BandsTextureBit`). The sets are the game's
`src/Game/Textures/<set>/` folders, made by `tools/Textures` and copied to `Textures/` next to the executable:
`colour.dds` (BC7 sRGB, occlusion in alpha), `normal.dds` (BC5, x and y), `height.dds` (BC4), and `set.json` (texels,
the tile's width in metres, the colour's mean in linear RGB, the source); `SOURCES.md` lists every source and licence.
`MaterialTextureSets` (`Rendering/Materials/`, with `DdsFile`) reads them at `MeshPass.Initialize`: 2048-texel sets into
the large arrays, 1024-texel ones into the small (bindings 11 to 13 and 14 to 16, colour, normals, heights; a 4-texel
stand-in for a group with none, never written: every set is 1024 texels today, so the large group holds only that); the first frame copies every level from an upload buffer that goes
`FrameSync.FramesInFlight` frames later. `WorldMeshes` packs each material's set into `MeshMaterial` (`Texture`: layer
plus 1, the large bit, the colour match; `TextureMetres`; `TextureMean`), warning for a name not loaded. `mesh.frag`'s
`surfaceTextured` reads the sides of x, y, and z the surface faces (sides under 5 % of the blend not read; derivatives
taken before the branches), filtered 16 times anisotropically where the device can (`VulkanContext.SupportsAnisotropy`;
`textureCompressionBC` is required): the colour, kept round the set's mean by `VoxelLook.TextureDetail` and read
`TextureBlur` mip levels softer (`MeshMaterial.TextureStyle`; with `TextureRelief`, the normal map's and occlusion's
strength; the console's run-only `materials tune` for trying them), moved to the material's own colour by
`VoxelLook.TextureColourMatch` (0 keeps the photograph's) and by the tint as the material's colour moves; the normal map bending the lit normal
(OpenGL's convention); the occlusion as the cavity; the patches on top. The console's run-only `materials`
(`ViewSwitches.Materials*`) draws materials from their sets (`textures`, the default) or flat, and with `all` every
world material without a set from the first small set, 3 m wide (for timings); `materials set ID SET|none [MATCH]` draws
one material from another loaded set (`WorldMeshes.SetTexture`, the materials staged again), for comparing candidates
in the game. The sky, sunlight, haze, and colour grade come from a game's `IAtmosphereSource` at the camera
(`AtmosphereSystem`), or the `Engine:Renderer` sky options without one.

The look (`RenderLook`, a record struct whose new value, `RenderLook.Default`, is the engine's and the starter's) holds
the art direction's values that were shader constants: the world's own surfaces' saturation, value, and side and top
shade; the sunlight's warmth and the shade's coolness; the sky light's share of the horizon colour, the ground's light
and bounce, the occlusion and sky floors, and full glow's strength; where the view's fog starts and the haze's half
distance; the sky's zenith (clear and overcast) and below-horizon shares and the sun disc's sharpness, strength, and
overcast hiding; the exposure; the grade's desaturation and darkening; and the split tone's tints and bounds. A game
gives it per frame through its atmosphere source (`IAtmosphereSource.LookAt`, a default method returning the engine's;
`AtmosphereSystem` sets `RenderView.Look`; `RenderLook.Lerp` blends two); the console's run-only `look` lays values
over it (`LookTuning`, a table of every value without reflection; `RenderView.DrawnLook`). `GameLoop` writes the drawn
look into the frame slot's record (`MeshPass.LookOf`, `LookConstants.From`: its values, and the light and colours it
gives with the frame's atmosphere worked out once a frame instead of at every pixel: the sunlight, sky and ground light,
the zenith and below-horizon colours, the sun's disc, the grade's hue and amounts, the haze's density; `look.glsl`: set
0 binding 17, and binding 8 of the particles' draw set, compiled with `LOOK_BINDING 8`), which `lighting.glsl`,
`sky.glsl`, `mesh-shade.glsl`, and `mesh.frag` read as `LOOK` (the slot's by `view.slot`).

The sun moves as a game says: its direction is the atmosphere's (`Atmosphere.SunDirection`, from the game's source each
frame; zero for the engine's, `ViewConstants.DefaultSunDirection`, high from +x and +z; `Atmosphere.TowardsSun`), or the
console's run-only `sun AZIMUTH ELEVATION` and `time HH:MM` (a test day: sunrise at 06:00, the engine's sun at 12:00,
sunset at 18:00; `RenderView.SunOverride`, `DrawnSun`). `SunPath` says how much light it gives: all from 2 degrees over
the horizon, none from 2 degrees under, fading between (the look's sunlight and sun disc, `LookConstants.From`); from 2
degrees under it is night: `ViewConstants.Create` leaves the shadows switch off, so no caster is listed and the maps
are only cleared. A game's moon is this light at night, dim and cool, with its own disc (the look's sharpness and
strength). While the sun moves, `MeshView` carries the maps' image axis from frame to frame
(`ShadowCascades.ImageAxis`: the least turn that keeps it square to the sun, so it never jumps, also overhead); a sun
that never moved keeps the fit's own axis, as before, and a match's end forgets the sun (`MeshView.ForgetSun`). The
particles' sunlight fades with the daylight too (`LookConstants.Daylight`).

Without a window (`Engine:Window:Mode=Offscreen`, `WindowService.Offscreen`) the surface is headless
(`HeadlessSurfaceSource`, `VK_EXT_headless_surface`; `WindowSurfaceSource` wraps GLFW's for a window: `ISurfaceSource`,
`SurfaceSource.cs`). Mesa's headless surface offers only UNORM formats, so where the device has
`VK_KHR_swapchain_mutable_format` (enabled where offered, asked of no device: `VulkanContext.SupportsMutableSwapchainFormat`)
the swapchain is made mutable and its images viewed through their sRGB twin (`VulkanSwapchain.SrgbTwin`):
`ImageFormat` is the twin, every pass draws as it does into a window's, and screenshots match a window's. Nothing
paces a headless present, so the render thread starts frames at most `Engine:Window:OffscreenFps` (240) a second
(`RenderThread`'s frame cap); the world uploads a share each frame, and at 60 the starter's view did not load in 15 s.

A render scale below 1 draws the world, models, particles, and debug lines into a smaller image, and AMD's FSR 1
(`UpscalePass`: EASU, then RCAS; `fsr-easu.frag`, `fsr-rcas.frag` over `ffx_a.glsl` and
`ffx_fsr1.glsl`) upscales it before the UI. Dynamic render scaling (`DynamicRenderScale`,
`Engine:Renderer:DynamicRenderScaling`, on by default) sets the scale, 0.5 to 1, from the GPU's time per frame, so the
frame rate holds the lower of `Engine:Renderer:DynamicScalingThreshold` (120 by default; the menu offers 30, 60, 75,
90, 100, 120, 144, 165, 180, 240, 360) and the screen's refresh rate (`WindowService.RefreshRate`); at 1 with time to
spare it stays at 1. A step every 8 frames measured: down at once by the square root of the time's excess, up by at
most 0.02. Off, the scale is `Engine:Renderer:RenderScale` (1 by default; the benchmark turns scaling off), and the
console's `scale` sets one for the run (`scale auto` gives it back).

Anti-aliasing: the world is drawn with `Engine:Renderer:MsaaSamples` (4 by default) samples a pixel. With one, SMAA 1x
runs instead when `Engine:Renderer:AntiAliasing` is on (`SmaaPass`; the console's `aa` for the run). The settings'
Anti-aliasing (`AntiAliasMode`: Off, SMAA, MSAA 2x, 4x, 8x; `PlayerSettings.SetAntiAliasMode`) sets both; a change of
the samples raises `PlayerSettings.SamplesChanged`, and the render thread makes the swapchain again with the device idle,
where `MeshPass.OnSwapchainRecreated` makes its pipelines and multisampled images with the samples asked for
(`RequestSamples`; the most the device offers up to them). At 2560x1440 on Zorf TD's view along its players' path (a measurement of that game's world) the world took 1.26 ms
with one sample, 1.65 with two, 1.95 with four, and 2.52 with eight; SMAA adds 0.30. SMAA's shaders: `smaa.glsl` (SMAA.hlsl unchanged, MIT), `smaa-common.glsl`, `smaa-edges.frag`, `smaa-weights.frag`,
`smaa-blend.frag`, before FSR 1, as AMD asks of its input (`docs/anti-aliasing.md` has the research). The world, models,
particles, and lines then draw into a scene image of the drawn size; the edges pass finds edges by luma in its
sRGB-encoded values (a UNORM view), the weights pass works out the blend weights with SMAA's lookup textures
(`SmaaTextures`, `Smaa/AreaTex.bin` and `SearchTex.bin`, copied to the GPU in the first anti-aliased frame), and the
blend pass blends in linear light into the swapchain image (scale 1) or FSR 1's source. 0.23
to 0.31 ms at 2560x1440 on the RX 5700 XT; `aa edges` and `aa weights` show what it found. `FrameImages` keeps the
drawn-size images of SMAA and the upscale until no frame in flight uses them. The console's `lines` draws the bodies'
outlines, stand-ins, and collision cells. The console's `view` sets the run's debug views (`RenderView.DebugViews`, the
view's switches `Wireframe`, `Levels`, and `Plain`; not saved): `wire` draws the camera's world and model commands again as lines
over what they drew (`MeshPass`'s wire pipelines, `mesh-wire.frag`: near black, depth test greater or equal without
writes, a depth bias of 64 steps and 1.5 times the slope; made only where the device draws triangles as lines,
`VulkanContext.SupportsWireframe`), and `levels` tints each surface by its voxels' size (`mesh.vert` passes the draw's
level, log2 of its voxel's centimetres, in bits 8 to 15 of the material output; `mesh.frag` mixes in 1 cm magenta, 2
violet, 4 blue, 8 cyan, 16 green, 32 yellow, 64 orange, 128 red; 10, 20, 40, and 80 cm voxels round to the 8, 16,
32, and 64 cm colours); `plain` draws every block of the world from its plain mesh, the seams between levels
open (`ViewSwitches.Plain`, read by `WorldMeshes` alone).

### The meshes

`WorldMeshes` (`Rendering/Meshes/`) follows the match's world (in a match without voxel terrain, its voxel models alone:
no columns, no sky field, no stitches; `BeginMatch` with a null world). It meshes the world block by block: a block is a 32-voxel
cell of a chunk, read with a 2-voxel apron on the main thread (`VoxelWorld.TryReadBlock`, `BlockMayHaveSurface`;
`VoxelWorld.Blocks.cs`, materials and densities) and meshed on `MeshWorkers` threads by `SurfaceNets`
(`src/Itera.Engine/Meshing/`: naive Surface Nets, one vertex per cell, at the mean of the points where the density
(`VoxelDensity`) crosses one half along the cell's crossing edges; normals from a 4x4x4 gradient of the densities;
openness from the mean density round each of the cell's corners, blended to the vertex's place, so it follows the
surface and not the cell; a model's blocks at their materials' default densities; the material most of the cell's
surface corners have (solid corners beside an air corner, so a one-voxel skin shows),
a block owning the edges whose lower voxel is in it, so blocks meet exactly). It follows the change list (columns,
bricks, and chunks changed) and meshes a column only while it can be drawn (`IsWanted`), the columns nearest the view
first and a column's blocks nearest the view first (`MeshColumn.DirtyNearestFirst`); the blocks edits mark count
towards the edits' latency (`MeshBlock.Edited`; a Debug line once all are meshed and staged), streaming blocks not; finished meshes go through the
slot's staging (`StagingWriter`) into `MeshPass`'s heap (`Engine:Renderer:MeshMegabytes`, 512 by default, lowered to
the device's limits; `GpuRangeHeap`, in `Rendering/Memory/`; a mesh's range is its palette's two words, then its
vertices, a `MeshVertex` 16 bytes, then its 16-bit indices; the materials and the tint map at fixed places at its start).
Each mesh's palette (`MeshBuffers.Palette`) is its surface's materials, the eight most common, and each vertex's
`Weights` how many of its cell's surface corners are each, four bits a slot (a material past the eighth counts as the
vertex's own; the meshes line counts the meshes that had more). `mesh.vert` passes the weights (two `vec4`) and the
palette (`meshPalette`, the words before the first vertex) for the world's blocks, and `mesh.frag` draws, of the two
materials that weigh most at the pixel, the one whose share plus its height (`HeightBlend` times the set's height map
less a half; `RecipeHeight` 0.5 without a set) is greater: the border between materials wanders round the stones of
their height maps instead of stepping at 10 cm (a hard edge, by choice; the models keep their vertices' material). A full heap first frees
the meshes of wanted columns that neither the camera's pass nor the far cascade's drew or would have drawn in the last
frame and that are not being meshed, farthest first (`Evict`: those a coarser drawn column covers, then those finer drawn
columns cover), at least 16 MB more than the mesh needs and at most 128 MB a frame; they stay wanted and are meshed again
when the view needs them (`MeshColumn.Evicted`, `NeededUpload`). A block that still finds no room keeps the mesh it had,
no longer counts as pending, and is read again at most once a second after a range has been freed (`RetryStarved`). The
meshes line reports what columns the last frame did not draw hold, what was freed, and the frames that drew wanted
columns not meshed yet. Each frame it picks a
level per column (a coarse column splits into its four finer ones when its nearest part is within the finer level's
reach times `Engine:Renderer:MeshReachShare`, 0.5 by default, and all four are meshed, and stays split while it is not
meshed itself and they are; `Engine:Renderer:MeshFinestLevel`,
0 by default, is the finest), keeps neighbouring columns, corners included, within one level of each other
(`LevelBalance`: a column beside one two levels finer splits where its finer columns are meshed; the meshes line counts
those left), draws the blocks of a column along a side or corner past which a column one level coarser is drawn from
their stitched meshes (`WorldMeshes.Stitches.cs`, below), culls blocks to the frustum, and writes a `MeshDraw` and an indexed indirect command per
block (`firstInstance` names the draw); the camera's commands are sorted nearest first by the distance to each block's
centre (`WriteCameraNearestFirst`), so the depth test rejects hidden leaves before they are shaded, and runs repeat (in
load order they did not; the shadow lists stay unsorted). The frame's view (`MeshView`, which `GameLoop` starts before
`WorldMeshes.Upload` and ends after it, and whose frustum, cascades, and lights every list drawn in `MeshPass` shares)
fits two sun shadow cascades (`ShadowCascades.Fit`: a square round the bounding sphere of the view's slice from 0 to 30 m
and from 30 m to 200 m or the view distance, its centre snapped to whole texels, reaching 100 m further towards the sun,
and further as the sun sinks: `MeshView.CasterReach`, up to 300 m)
and writes the slot's `MeshLighting`; with the view's shadows on, `WorldMeshes` lists the blocks that cast into each
(`CascadeBounds`): those whose bounding sphere meets the cascade's box and lies inside each plane of its slice that faces
the sun (a block shades the slice only from the sun's side). The near cascade takes the camera's levels; the far one
is chosen in a second pass one level coarser (`FarShadowCutShare`). A full list warns once a match. The seams between
levels are stitched (`SurfaceNets.MeshTransition`): the block meshed from its own voxels and the coarser level's block
that holds it (block `b >> 1`, at `(b & 1) * 16` of its voxels), its cells that straddle the coarser column's boundary
taking the coarser level's vertices, and at its high x and z faces the coarser level's quads across them. A stitched
mesh has its own heap range (`MeshColumn.Stitches`, `MeshStitch`, made on a column's first need) and is drawn by the
camera and the near cascade while the sides (`LevelBalance.SidesOf`, masked per block by `MeshColumn.BlockSides`), the
block's version, and the changes to the coarser block (`MeshStitch.CoarseChanges`, raised in `CoarseChanged` with that
block's version, apron included) are those it was read with; else the plain mesh is drawn and the block asks for one,
and the next frame reads the nearest asked for after the plain jobs, from a pool of 64 stitch jobs. Stitched meshes are
not pending and do not count towards a column's being meshed; the far cascade draws plain meshes. About nine in ten of
a seam's open edges close; the levels' samples disagree in a few places. It also prepares
the palette, counts each model as uploaded at the next frame, removes retired models, and answers `IWorldOnGpu`
(`IsUpToDate`, `Uploads`), which the world loading task and the benchmark wait on. `MeshPass` first draws the cascades into the two layers of a depth map array
`Engine:Renderer:ShadowMapTexels` texels a side (2048 by default; `mesh-shadow.vert`, `depth-only.frag`; only faces turned away from the sun, so a lit surface never meets its own
depth; timed as `ShadowMaps`, part of the `Voxels` time), then the world with `Engine:Renderer:MsaaSamples` (4 by
default) samples a pixel into images of its own, resolved as rendering ends into the target (the mean) and the depth
image (sample 0, which particles and lines test against; the depth image's frame-start barrier covers the resolve,
which Vulkan counts as a colour attachment write). Every block in view is one `vkCmdDrawIndexedIndirect` (`mesh.vert`
pulls the vertices from the heap, projected by `view.glsl`'s `viewClip` as every pass that draws into the view's depth
does; `mesh.frag` shades with the material's colour and tint, then `mesh-shade.glsl`'s `shadeSurface` (shared by every
surface the pass draws) with the sun through the cascade
that covers the point by view depth with a 1.5-texel normal offset (`mesh-light.glsl`'s `sunShadow`: in the near
cascade contact-hardening shadows, a 2 degree sun whose blur grows with the gap between caster and surface, found by an
8-tap blocker search of the maps read as depths (binding 9, no comparison) and filtered by 16 comparison taps of a
Poisson disc, 1 to 16 texels; in the far cascade 3x3 comparison lookups; the near cascade blends into the far one over
the 5 m before its end, `MeshLighting.NearBlendStart`, the far cascade's slice starting there too, and the far shadows
fade over the last tenth of theirs, `FarFadeStart`), sky and ground light
dimmed by openness and scaled by the sky field, the point lights, glow, fog, the tone curve, and the grade; the art
direction's light and grade, as the look gives them: the world's surfaces less saturated, darker, and darker on their
sides than on their tops (`mesh.frag`'s, the world's own), warm sunlight and cool sky light (`mesh-shade.glsl`'s), and dark tones towards teal and light ones towards orange
after the grade, less as the fog thickens, so the far ground meets the sky, which takes none; a glowing surface keeps its
colour, takes no grade or split tone, and adds its glow's own brightness through the tone curve after them), then the
sky where the depth is still 0
(`world-sky.frag`). The aim dot is the HUD's (`AimDotLayer`, `.claude/rules/ui.md`). SMAA does not run on a multisampled world. Its GPU time is the `Voxels` pass
(`GpuPass`). The camera's commands are sorted nearest first each frame (`WorldMeshes.WriteCameraNearestFirst`), so the
depth test rejects hidden leaves before they are shaded and benchmark runs repeat; the shadow lists stay in load
order. The particles move after the world's pass and meet it through the depth image, lit from the cascades and the
sky field (`.claude/rules/particles.md`); `MeshPass`'s set layout serves the compute stage for that, and its barriers
after the cascades and the sky field's copy name it.

The models (`VoxelModels`: loose bodies, and a game's bodies, parts, and items) are meshes too (`WorldMeshes.Models.cs`). Each level of each model is cut into 32-voxel blocks starting one voxel before
its box (`ModelBlockShift`, since a block meshes only the faces whose lower voxel is in it), read with a 2-voxel apron
of air from the decoded level (`ReadModelBlock`), meshed on the same workers before the world's blocks, and kept in the
mesh heap a range per block. A model counts as uploaded once all its blocks are in (`VoxelModels.SetUploaded`; cuts,
loose bodies, and loading tasks wait for that); a replaced model (a wound) draws its old meshes until the new ones are
all in (`VoxelModels.Revision` tells a new revision from the one being meshed); a removed model's ranges go the next
frame; retired models stay drawn until no world block is pending (a frame without graphics, `GameLoop`'s or `HeadlessMatch`'s,
removes them after it). Each frame `GameLoop` hands `ModelInstances` to
`WorldMeshes.Upload`: the draws (and the retired models) whose bounding sphere is in view, or casts into a cascade as
the world's blocks do, as each draw's `ModelShadow` allows (`Casts`; `None` for a model drawn for the viewer alone, such as first-person arms and what they
hold, drawn but casting nothing; `Only` for the local player's own body in first person, casting its shadow without
being drawn), are grouped by model level with a counting sort, and each block of a level is one indexed
indirect command whose instances are that level's draws (`MeshModelDraw`: corner, turn, voxel size, the block's vertex
base and place, tint), in three lists like the world's. `mesh.vert` and `mesh-shadow.vert` compiled with `MESH_MODEL`
place and turn them; `mesh.frag` takes the draw's tint instead of the tint map's. The model draws in the main pass are
timed as `Models` (part of `Voxels`).

Underground light, a first try (`WorldMeshes.Sky.cs`, `WorldMeshes.Lights.cs`). The sky field (`SkyField`) is how much
of the open sky each cell of a box of 200 x 100 x 200 cells round the view sees, a cell being a voxel of the level
nearest 64 cm (`SkyField.LevelFor`: level 3, 80 cm, in the game, a box of 160 x 80 x 160 m): those voxels (or, when
the level below is the coarsest, every other of its voxels; no field without that level) are read on the main thread from the resident columns, at
most 1 ms a frame over as many frames as that takes (`VoxelWorld.TryReadOwnBlock`: each block's own voxels, no apron; a
block whose cell is one material or air is one fill; an edit made meanwhile may show in part of the field until the next
build), air under open sky is 255, and a worker spreads it through air keeping 0.85 a cell, brightest first; solid cells
take their brightest air neighbour's. It is built again when the view is 32 m from its middle or 16 m above or below it,
or 2 s after an edit or a column of the read level loaded (a failed build is tried again after 2 s), staged whole in one
frame, and copied by `MeshPass.RecordSkyField` into an R8 3D texture (binding 8, linear, white border: open sky) with
the frame's other copies, under the loading screen too; `mesh.frag` scales sky and ground light by it, half a cell in
front of the surface, never below 0.02. Point lights: each world block's glowing vertices (a material's glow) give one
light at their glow-weighted middle when its mesh is uploaded, and events give others through `PointLights` (a run-wide
service the frame systems fill: `Add` for a light that shines while something happens, `Flash` for one that fades as the
square of its time left; `GameLoop` starts it each frame before the frame group and clears it at a match's end; a game
lights its flashes, fires, and glows there). Each frame the drawn blocks' lights (offered by `WorldMeshes` as it lists
them) and the events' (at `MeshView.End`) within 48 m whose reach (power over 0.02, square-rooted, at most 16 m) meets the
frustum are ranked by power over distance squared, and the light budget's brightest (`Engine:Renderer:MostLights`, 128
by default, at most `MeshPass.MostLights`, 256) go into the slot's list, brightest first (binding 7, `MeshLight`). The
mesh pass's cluster pass (`mesh-clusters.comp`, before the cascades) sorts them into the view's clusters, 16 x 9 tiles
of the drawn image by 24 slices of depth (the first to 1 m, the last from 48 m, between them a fixed ratio;
`mesh-data.glsl`'s `meshClusterSlice`), each keeping up to 32 of those that reach it, those that matter most to it (power over the squared distance to its box; binding 10, one region a slot), and
`mesh-shade.glsl`'s `pointLight` loops over its pixel's cluster's (Lambert, inverse square, to nothing at the reach). The console's `lights
test N` scatters test lights for the run. The per-slot record is `MeshLighting` (the cascades, the field's corner and cell, the light
count).

### Mesh assets

The run's mesh assets (`MeshAssets`, models read from glTF files: `.claude/rules/mesh-assets.md`) are drawn with the
world, lit as it is. A frame system adds each one to draw to `MeshInstances` (run-wide; `Add(asset, transform, tint,
shadow)`: the asset's index, a `Matrix4x4` used as `M * v`, a linear RGBA tint, `NoTint` for none, and a `ModelShadow`;
at most 16,384 a frame, those past it left out with one Warning a run), which `GameLoop` clears on every frame
path; the engine's `MeshDrawSystem` adds every entity with `MeshPlacement` and `DrawnMesh` (`.claude/rules/systems.md`).
A skinned asset drawn posed (`AddPosed`) also gives its rig's skinning matrices (`Rig.WritePalette`), kept in the
frame's pool (`MeshInstances.Palettes`, at most 32,768 a frame).
`MeshAssetDraws.Write` (main thread, after `WorldMeshes.Upload`, before `MeshView.End`) culls each instance's bounding
sphere (the asset's, its radius times the transform's largest scale; a posed instance's from its joints' posed places
grown by their reaches, `MeshAssetLayout.JointPositions` and `JointReaches`, and its unweighted vertices' sphere) against
the view and both cascades as its shadow allows, groups the draws by asset primitive with a counting sort, and writes into the frame slot's regions one
`DrawIndexedIndirectCommand` per primitive with instances (its `vertexOffset` the asset's first vertex, its
`firstInstance` its first `MeshAssetDraw`: the transform, the material, the tint, the vertex delta) for each of three lists, the camera's
and the two cascades', each in four ranges of pipeline (`MeshAssetRange`: opaque or masked, one-sided or two-sided);
at most `MeshAssetPass.MaxDraws` (16,384) draws and `MaxCommands` (4,096) commands a list, a full list warning once.
Each posed instance any list draws gets a skin group (`MeshSkinGroup`: its asset's first vertex and skin entry, its output's
first vertex, its first skinning matrix, its vertex count) and its matrices in the slot's regions, and its draws read
its skinned copy (`MeshAssetDraw.VertexDelta`, added to `gl_VertexIndex`); past the slot's room, or with matrices that
do not match its rig, it is drawn unposed, with one Warning a run. The counts travel in `RenderFrame.MeshAssets`
(`MeshAssetFrame`, with the skin groups, their most vertices, and their matrices).

`MeshAssetPass` (`Rendering/Meshes/`) is made at the run's step "mesh assets on the gpu", after "mesh assets" and before
the render thread: device-local buffers of every asset's vertices (`MeshAssetVertex`, pulled by `gl_VertexIndex`),
32-bit indices, and materials (`MeshAssetMaterial`: factors, alpha cutoff, five packed texture references, flags), one
image per texture with every mip (`Rgba8Srgb` colour and emission, `Rgba8Unorm` occlusion and metal-roughness, `Rg8Unorm`
normals; read from a model's compressed textures, `BC7_SRGB`, `BC7_UNORM`, and `BC5_UNORM`, each level copied in whole
4x4 blocks with its own size as the extent), and host-visible draws and commands a frame slot. The first frame copies the buffers and images from one upload
buffer (`RecordUploads`, before the mesh pass), freed `FrameSync.FramesInFlight` frames later. It owns descriptor set 1
(`mesh-asset-data.glsl`: six immutable samplers, linear with 16x anisotropy where the device can or nearest, each
repeating, clamped, or mirrored; the textures, `MESH_TEXTURE_COUNT` of them, a white stand-in first, indexed with
`nonuniformEXT`; the vertices; the materials; the draws; for the skinning, the skins, every slot's skin groups, and every
slot's skinning matrices); set 0 is `MeshPass`'s. The device's room caps the textures (the lower of its per-stage and
per-set sampled images less the mesh pass's nine, and its per-stage resources less the 25 other descriptors and
attachment a stage holds); those past it are left out with one Warning, and their
materials drawn with their factors. Vertices past the device's storage buffer range stop the run at the step. When the
run read a skinned asset, the vertex buffer also holds a region for each frame slot (`Engine:Renderer:SkinMegabytes`, 16
by default, lowered to fit the range), into which `RecordSkinning` (before the cascades, outside rendering; only on a
fresh frame, since a frame presented again reuses its slot's) dispatches `mesh-skin.comp`: a row of 64-vertex groups
an instance, each vertex moved by its four joints' matrices blended by its weights, its normal and tangent turned by the
blended matrix (right for turns and uniform scales; a joint scaled unevenly skews them) and packed again in `Octahedral`'s
encoding (an unweighted vertex copied; an asset of more than `MostSkinnedVertices`, 4,194,240, or past the slot's room is
drawn unposed, with one Warning), then a barrier from the compute writes to the
vertex shaders' reads (timed as `Skinning`, part of `Voxels`). Nine pipelines and the skinning's: the camera's (`mesh-asset.vert`,
`mesh-asset.frag`) and the cascades' (`mesh-asset-shadow.vert` with `depth-only.frag`, or `mesh-asset-shadow.frag` for
masked materials), for each of the four list ranges, and the blended one (below); depth and culling as the world's (back faces culled unless two-sided; the cascades
drawn from the faces turned away from the sun, so an open single-sided mesh, such as one quad, casts no shadow when
its front faces the sun). `MeshPass.Record` calls it through `IMeshPassDraws`: `RecordShadows`
inside each cascade's rendering, and `RecordCamera` in the main view after the models and before the sky (timed as
`Meshes`, `GpuPass`). Its camera pipelines are made again when the mesh pass's samples or colour format change, and every
pipeline on a shader reload. `mesh-asset.frag` draws glTF's base colour (the factor times the texture, the vertex colour,
and the draw's tint), cut at the alpha cutoff in the masked ranges, the normal map tilting the lit normal in tangent
space (a two-sided surface's back lit from its own side), the occlusion as the cavity, and the emission as glow, then
`shadeSurface` with the openness of flat ground; metalness and roughness are read and kept, not drawn.

Blended materials (glTF's `BLEND`; `MeshAssetRange.Blended` and `BlendedTwoSided`, after the four list ranges) are in no
list's ranges and cast no shadow. `MeshAssetDraws` writes them into the slot's fourth list (`MeshAssetPass.BlendedList`):
the seen instances sorted back to front by their sphere's centre (farthest first; an instance's primitives in its
asset's order), each primitive one draw and one command of one instance, a two-sided one two (its back faces, then its
front faces: `MeshAssetDraw.Sides`), at most `MaxCommands`, past which they are left out with one Warning a run.
`MeshPass.Record` calls `RecordTransparent` after the sky (which would paint over a surface that writes no depth): one
`vkCmdDrawIndexedIndirect` of every command, which the device draws in order, with one pipeline (`mesh-asset.vert` and
`.frag` compiled with `MESH_ASSET_BLEND`: no culling, each draw dropping the faces it does not show; the depth tested,
not written; premultiplied alpha over what is drawn, the colour shaded and fogged as an opaque surface's, then times its
alpha). It blends on the frame's display colours (there is no HDR image), as the particles do, inside the multisampled
rendering, so its edges are anti-aliased; it is timed with the world (`Voxels`), not `Meshes`. Objects that cross, or one
inside another, can show in the wrong order from some sides (sorted per object, not per pixel). Its log lines: `Mesh assets on the
GPU: N vertices, T triangles, M material(s), X texture(s) of Y MB; draws Z MB a frame slot`, and with skinned assets
`Mesh assets on the GPU: N skinned vertices; posed instances skinned into V vertices (M MB) a frame slot`; the stats
line's `mesh assets in N frames avg/max` and `skinning in N frames avg/max` are the `Meshes` and `Skinning` passes' GPU
time.

### Passes a game adds

A game adds its own GPU work as render passes (`IRenderPass`, in `RenderPasses.cs`), registered in its composition
(`services.AddSingleton<IRenderPass, MyPass>()`): at most `GpuTimings.MostGamePasses` (8), each named apart, or the
run's "render passes" step stops the run. Each records at one stage of the frame (`RenderStage`):

- `Compute`: after the mesh pass's light clusters and skinning, before the shadow maps, outside any rendering.
- `Opaque`: inside the world's multisampled rendering (`MeshPass`'s colour format and samples; its depth, reversed and
  compared Greater), after the engine's opaque draws (the world, the models, the mesh assets) and before the sky:
  surfaces that write depth. The mesh pass binds its own set and constants again after them.
- `Transparent`: inside that rendering, after the sky and the engine's blended mesh assets: surfaces that blend and
  write no depth, drawn after the engine's blended ones, not sorted among them.
- `Scene`: after the resolve, the particles, and the debug lines, on the drawn image (one sample a pixel, at the render
  scale's size) and the resolved depth, before SMAA and the upscale.
- `Screen`: after the upscale, on the swapchain image at the screen's size, colour only, before the UI.

For `Scene` and `Screen`, `RenderPasses` begins the rendering itself (after a barrier from the frame's earlier colour
and depth writes; the attachments loaded and stored) with the viewport (+y up) and scissor set; inside the world's
rendering they are the mesh pass's. The engine calls `Initialize(RenderPassSetup)` once at the run's "render passes"
step (after "mesh assets on the gpu", before the render thread; never in a run without graphics), `Write(slot,
RenderPassFrame)` on the main thread each frame after the frame systems (the frame's time and view; the pass writes its
slot's region of its host-visible memory, write only, and flushes it when it records), `Record(RenderPassContext)` on
the render thread at its stage (the command buffer, the frame slot, whether the frame is fresh, the view constants with
the stage's size, the extent, and `MeshPass`'s set), `OnTargetsChanged` when the swapchain is made again (the samples or
format may differ; the device idle), the shader reloads (`IReloadsShaders`), and `Dispose` (perhaps twice, and perhaps
without `Initialize`, so a pass's constructor makes no Vulkan object; after the engine gave up on a stuck device,
`VulkanContext.IsAbandoned`, it destroys nothing). The mesh pass sets its viewport, scissor, set, and constants again
after the `Opaque` stage.
`RenderPassSetup` gives the device, the allocator, the shader compiler, the stage's colour and depth formats and samples,
`MeshPass`'s set and its layout (a pipeline whose set 0 it is reads the look, the lighting, and the shadow maps), and
helpers: `CreateLayout`, `CreateTriangles` (a pipeline for the stage's targets with no vertex input, depth compared
Greater when tested, its colour blended as `ColorBlend` says: `None`, `Premultiplied`, `Alpha`, or `Additive`),
`CreateCompute`, and `CreateBufferSet` (a descriptor set of storage buffers, `RenderPassBufferSet`); `PipelineFactory`
and `VulkanResultExtensions` are public for more. A game's shaders are in its own `Shaders/` folder, copied to
`Shaders/Game/` beside the engine's, named `Game/NAME` to the compiler, and include the engine's (`#include
"../view.glsl"`: `viewClip` projects as every pass does); the compiler reads the `Game/` paths from
`Engine:Renderer:GameShaderSourceDirectory` when it is set (the game's folder in the repository, for `shaders reload`;
`game.py start` gives it), else from `Shaders/Game/` beside the executable, also while `ShaderSourceDirectory` points
the engine's at the repository (`ShaderSource`, `ShaderRoots`). Each pass is timed with its own pair of queries: their sum is `GpuPass.Game` (the stats line's `game passes
in N frames avg/max`; the `Compute`, `Opaque`, and `Transparent` stages' time is also part of the world's, `Voxels`), and each one has a Debug line (`GPU game pass NAME in N frames avg ... max ...`) and an entry in
the control port's `gpu` reply (`Game NAME`). The mesh sample's `AimHighlightPass` is the example (its README).

### Files in `src/Itera.Engine`

  - `Rendering/`: Vulkan bootstrap (`VulkanContext`, `DeviceRequirements`), the frame (`FrameRenderer`,
    `RenderThread`, `FrameExchange`), a game's own passes (`RenderPasses`, `IRenderPass`), the surface's source (`ISurfaceSource`: the window's or a headless one, in
    `SurfaceSource.cs`), `DepthTarget`, `ModelInstances`, `MeshInstances`, `DebugLines` and `DebugLinePass`, `UiPass`,
    `RenderScale` and `UpscalePass`, `SmaaPass`, `SmaaTextures`, `FrameImages`, `LoadingPass`, `ParticlePass`, `Screenshots`, `ScreenshotPass`,
    the look (`RenderLook`, `LookTuning`), `Atmosphere`,
    `ScreenshotEncoder`; the materials' texture sets (`MaterialTextureSets`, `DdsFile`) in `Materials/`; GPU memory (own block sub-allocator, `GpuBuffer`, `GpuImage`, per-slot `StagingBuffer`) in
    `Memory/` (with `GpuRangeHeap`, the mesh heap's ranges); `Pipelines/`; structs that shaders read in `GpuData/`; the
    palette and its looks (`IVoxelPalette`, `VoxelLook`), the tint map (`IVoxelTintMap`) and the CPU's read of it
    (`VoxelTintSampler`), and `StagingWriter` in `Voxels/`; the sun shadow cascades' fit (`ShadowCascades`);
    `WorldMeshes`, `MeshPass`, the frame's view `MeshView` (with `CascadeBounds`), and the mesh assets' `MeshAssetPass`,
    `MeshAssetDraws`, `MeshAssetLayout`, and `MeshAssetFrame` in `Meshes/` (with `MeshColumn`, `MeshWorkers`, `SkyField`, `LevelBalance`, and the
    test's interfaces `IMeshDrawTarget` and `IStagingRegions`, in `IMeshTargets.cs`), and `IWorldOnGpu`, which `WorldMeshes` answers; the CPU mesher (`SurfaceNets`, `MeshBuffers`) in `src/Itera.Engine/Meshing/`;
    GLSL files, the shader compiler, and reloads (`ShaderReloads`, `IReloadsShaders`, which every pass implements) in
    `Shaders/`.

### Conventions

- Shader sources are files in `src/Itera.Engine/Rendering/Shaders/` (`.vert`, `.frag`, `.comp`; `.glsl`
  for include-only files), copied to `Shaders/` in the output and compiled with `ShaderCompiler.Compile`. The folder
  `Game/` of the shader tree is the game's (`ShaderCompiler.GameFolder`); the engine's shaders use no folder of that name.

- Structs that shaders read use GLSL `layout(scalar)` and have a `GpuLayout` test. Matrices go to
  the GPU unchanged and are used as `M * v` in GLSL. The view's push constants (`ViewConstants`) are 108 of the 128 bytes
  the spec guarantees; its on/off switches share one word's bits (`ViewSwitches`, `viewSwitch` in view.glsl, which
  `ViewConstantsTests` checks), so a new switch takes a bit there. Details in the `ecs-vulkan` skill.

### Package notes

- Vulkan 1.3 is required on the loader and the device; there is no 1.2 fallback. `DeviceRequirements`
  requires `dynamicRendering`, `synchronization2`, `scalarBlockLayout`, and `multiDrawIndirect` with
  `drawIndirectFirstInstance` (the world's indirect draws, whose first instance names each block's draw), and
  `shaderSampledImageArrayDynamicIndexing` with `shaderSampledImageArrayNonUniformIndexing` (the mesh assets' texture
  array, indexed per pixel by material), and `D32_SFLOAT` with optimal tiling as a depth attachment; it enables `fillModeNonSolid` where the device has it (the
  wireframe debug view, `VulkanContext.SupportsWireframe`) and asks it of no device. Rendering uses dynamic
  rendering (no render pass or framebuffer objects), barriers use `CmdPipelineBarrier2`, and submits use
  `QueueSubmit2`.
