# Rendering a game can extend: decisions and their reasons

`docs/framework-limits.md` listed "rendering is closed to a game" as limit 3. On 2026-10-10 the engine gained, in six
phases: look data a game sets, a moving sun with night, blended mesh-asset materials, passes a game adds, orthographic
lenses, and image views (commits c78617d, f19b40c, 5b9651c, ca51a7e, d2eba9a, 646e7a0, and 963d75f). How each works is
in `.claude/rules/rendering.md` and ARCHITECTURE.md; this file records what was chosen, what else was offered, and why.
Times are GPU times on the RX 5700 XT at 2560x1440, offscreen, Release, with 4x MSAA unless said.

## The look

The art direction's values that were constants in five shader files (29 values, 45 numbers: the world's saturation and
side shading, the light's tints, sky and ground light, fog and haze, the sky's colours and the sun's disc, exposure, the
grade, the split tone, glow) are a record in code, `RenderLook`, whose default holds the old values. A game gives one
per frame through its atmosphere source (`IAtmosphereSource.LookAt`); the console's run-only `look` sets values over it.

- In code, not in `appsettings.json`. Offered: code with a live console command; 29 options in the settings file;
  both. Chosen: code. A look that changes during play (a darker grade at dusk) needs code in any case, and 29 options
  would each need a range in the validator and a pinned default (`ShippedSettingsTests`). The `look` command gives live
  tuning without a rebuild; the values found are copied into code.
- Worked out on the CPU once a frame. Reading the raw values per pixel from the look buffer made the world pass 1.4 to
  2.1 % slower than the constants had been: the shader compiler could no longer fold them. Computing on the CPU what
  the look gives with the frame's atmosphere (the sunlight, sky and ground light, the sky's colours, the grade's hue and
  amounts, the haze's density; `LookConstants.From`) made it 1.2 to 2.2 % faster than the constants. A new look value
  that a shader combines with others follows the same rule.
- What stayed constants: shadow filtering, the tone curve's coefficients, debug colours, and the material recipes'
  constants. They are not art direction a game would change per frame.

## The sun and night

A game sets the sun's direction per frame (`Atmosphere.SunDirection`; zero is the engine's sun).

- The starter's sun stays fixed. Offered: fixed, with run-only `sun` and `time` commands; a dawn-to-dusk loop; a full
  day and night with a moon. Chosen: fixed. The starter keeps the look already reviewed, and the commands let tests and
  screenshots cover a moving sun. A moving sun needs sky and light colours for each time of day, which is a game's look
  work.
- Night. From 2 degrees under the horizon there is no sunlight and no shadow maps are drawn (`SunPath`): their clears
  cost 0.015 ms, against 0.08 to 0.17 ms by day in the starter's views. A moon is the same directional light: the game
  points the sun's direction at the moon, above the horizon, with a dim, cool colour and its own disc; a direction more
  than 2 degrees under the horizon gives no light at all.
- Shadow edges while the sun moves. The cascade fit used to switch its reference axis within 1 degree of the zenith, a
  jump in every shadow; now `MeshView` carries the maps' image axis from frame to frame, turned as little as keeps it
  square to the sun. A sun crossing the sky in 20 minutes (0.0025 degrees a frame) changed no pixel by more than 1
  level, so the shadows do not move in steps.
- Casters reach 100 m towards the sun at 30 degrees and higher, and up to 300 m as it sinks, so a low sun keeps far
  casters. The longer reach adds 0.01 to 0.03 ms; the low sun itself costs more (the starter's start view: 0.15 ms at
  the engine's sun, 0.19 at 30 degrees, 0.36 at 15, 0.40 at 5).

## Transparency

glTF `BLEND` materials were drawn cut at their alpha. Now each blended instance is drawn back to front by its bounding
sphere's centre, inside the multisampled pass, after the sky.

- Sorted per object, not order-independent. Offered: sorted per object; weighted blended order-independent
  transparency (McGuire and Bavoil, 2013); both. Chosen: sorted. It needs no extra memory, its CPU sort takes
  microseconds for hundreds of objects, it keeps the MSAA edges, and separate objects blend exactly. It is wrong only
  where two blended objects cross or one wraps another (a glass box round a glass ball can show the wrong one in front
  from some sides). The order-independent method never depends on order, but where layers overlap its colours are
  approximate (close layers blur together, with a weight tuned to the scene's depth range), and it needs two more
  targets (about 133 MB at 2560x1440 with 4x MSAA, 33 MB without) and a composite pass each frame.
- After the sky. The sky is drawn where the depth is still 0, so a surface that writes no depth and is drawn before the
  sky is painted over.
- On display colours. The frame has no HDR image (one would take about 118 MB at 2560x1440 with 4x MSAA), so glass over
  a glowing surface tints it but cannot let it shine through brighter, as with the particles.
- No shadows from blended surfaces. Offered: none; cut at their alpha (a full, dark shadow where the alpha passes the
  cutoff); tinted (a second, coloured shadow map: about 33 MB, 0.1 to 0.3 ms, 1.5 to 2 days of work). Chosen: none.
  Glass, water planes, and smoke let the sun through, at no cost; an object with see-through parts casts its solid
  parts' shadow when they use an opaque material.
- A game's own blended draws (the `Transparent` stage) come after the engine's blended assets, not sorted among them.

## Passes a game adds

A game registers `IRenderPass` singletons, each recorded at one of five stages.

- The stages are the places in the frame where a game's draw can go without the engine's help: `Compute` (before the
  shadow maps, outside rendering), `Opaque` (in the world's rendering before the sky: surfaces that write depth),
  `Transparent` (after the sky and the engine's blended assets), `Scene` (the resolved image and depth at the drawn
  size, before SMAA and the upscale: effects on the picture), and `Screen` (after the upscale, at the screen's size,
  before the UI).
- At most eight. Each pass is timed by its own pair of timestamp queries in the GPU timer's fixed pool, and named in
  the Debug line and the `gpu` reply; the pool holds the engine's passes and eight more.
- A game's shaders are the shader tree's `Game/` folder, beside the engine's, so they include the engine's `.glsl`
  files and `shaders reload` compiles them. A first version read them from the engine's folder, which hid them while
  `ShaderSourceDirectory` pointed the engine's shaders at the repository; the compiler now reads `Game/` from its own
  place (`ShaderRoots`).
- The example. Offered: a highlight on the block the player aims at (Transparent stage); a full-screen effect (Screen
  stage); both. Chosen: the highlight (the mesh sample's `AimHighlightPass`), since it shows a game's own draw, shader,
  and per-frame data, and helps climbing.
- Reading the picture (added 2026-10-13, for a game's blur when a player's head is hurt and its grey when downed). A
  pass cannot sample the image it draws into, so an effect over the whole screen needs a copy. Offered: effects the
  engine owns (a blur and a grey a game sets each frame, in the engine's shader); a game's `Screen` pass that asks for a
  copy of the picture, frame by frame. Chosen: the second, since any full-screen effect then belongs to the game, and the
  engine keeps only the copy. The pass says per frame whether it reads the picture (`IRenderPass.ReadsPicture`), so a
  frame with no effect copies nothing; the copy is of the swapchain image, after the upscale, into one image of the
  screen's size kept as the upscale's images are, and its descriptor set and layout come from the engine, so the game
  makes no Vulkan object but its pipeline. Cost: with no pass reading, none; a blur of 24 taps over a disc and a
  grey, with the copy, took about 0.12 ms a frame at 849x1390 and about 0.16 to 0.22 ms at 1280x720 offscreen (before its
  taps' offsets became a constant table, 0.63 ms at 2560x1440).

## Orthographic lenses

A camera's lens is perspective or orthographic (`CameraLens`).

- An orthographic camera's position is the middle of what it shows, and its near plane may lie behind it, so fog, the
  terrain's levels, the light clusters, and the shadow cascades measure from the position as they do from a
  perspective camera's eye.
- A perspective view now draws from its lens's near plane (5 cm) instead of a fixed 1 cm; the starter's screenshots did
  not change except the moving fox.
- Lens values are limited to 100 km (`ViewConstants.MostLensMetres`): a review found the cascade fit failing for a
  box about 300 km away.
- Cost while unused: the world pass took 0.5 to 1.0 % longer on every benchmark view (the start view 1.718 to 1.730 ms),
  from the branches in the projection; within the plan's 1 %.

## Image views

A game declares views at its run's start, asks for one with a camera each frame it wants it, and shows it on the HUD or
on a mesh material (`RenderViews`).

- The examples. Offered: a minimap in the starter; a screen in the mesh sample; both. Chosen first: the minimap, which
  also uses the orthographic lens. When a material showing a view was chosen for the same phase, the sample's screen
  became its example.
- No shadows and no particles. Offered: none for now; each view with options for its own shadows (its own two shadow
  maps, 32 MB a view at 2048 texels, and its own caster lists) and its own particles (lists culled to the view), about
  1.5 to 2 days more. Chosen: none for now. A view lit by the sun without shadows reads well as a map or a camera's
  picture.
- One sample a pixel. The minimap at the world's 4x MSAA cost 1.59 ms an update, more than the main view's whole world
  pass at 1280x720: a view from above draws every block within about 45 m at the camera's finest levels, many of its
  triangles smaller than a texel. With one sample it cost 0.74 ms (about 0.18 ms a frame at one update in four), at the
  price of a second set of the mesh pipelines at one sample. Also offered: terrain levels chosen for each view's own
  texel size (likely the largest saving, about a day, and it needs coarser meshes kept near the player); updating every
  8 frames; no change.
- The terrain's levels and meshes are the main camera's. A view draws what is meshed for the camera, so no second
  meshing or level choice runs; a view far from the camera sees coarser terrain, or none.
- At most four. The UI pass's push constants hold four rectangles and one word with a byte for each view's index, and
  `ui.frag` reads four images; each view also takes a slot of the mesh assets' texture array.
- A material shows a view by name. Offered: later (no game used it yet, and naming which material shows which view
  needed its own design); now. Chosen: now. A game names an asset's material at its run's start; each view has a slot
  of the asset pass's texture array after the assets' textures, so the materials are written once. The picture is
  tone-mapped when the view is drawn and shaded again on the material, as any texture.
- The minimap. Offered: 32, 64, and 128 m across; square and north up, round, or square and turning with the player; on
  from the start or off until M. Chosen: 64 m (the boulders read clearly, about 30 m shows each side), square and north
  up with a thin frame and an arrow, on from the start; M hides and shows it, kept in `settings.json` (a game's own
  on/off setting, `PlayerSettings.RegisterSwitch`); the benchmark hides it, so its numbers stay comparable.
- Cost while unused: with no view drawn, the world pass took the same time as before image views on every benchmark
  view (-0.4 to +0.0 %).

## Left for later

Split screen (it also needs input per player, limit 6), HDR rendering, order-independent transparency, transparent voxel
materials (water in the terrain), a physically based sky, spot lights, shadowed point lights, image views with shadows
and particles, and terrain levels chosen for an image view.
