---
paths:
  - "src/Itera.Engine/Rendering/{RenderLook,LookTuning,Atmosphere,SunPath,HazeGlow}.cs"
  - "src/Itera.Engine/Rendering/GpuData/LookConstants.cs"
  - "src/Itera.Engine/Rendering/Shaders/look.glsl"
  - "tests/Itera.Engine.Tests/Rendering/{RenderLookTests,SunPathTests,RenderViewTests}.cs"
---

## The look, the sun, and the atmosphere

The look and the sun are data a game sets. The shaders read them from a per-slot record. Related files: `rendering.md`
(the core), `rendering-light.md` (the passes that use them), `rendering-materials.md` (the surfaces),
`rendering-clouds.md` (the clouds), and `docs/rendering-extension.md` (what a game can change, and the choices offered
for each).

### Where they come from

A game's `IAtmosphereSource` gives the sky, the sunlight, the haze, the colour grade, and the haze glows at the camera's
absolute place.
`AtmosphereSystem` reads it each frame. Without a source, the `Engine:Renderer` sky options give them.

### The look

`RenderLook` is a record struct. `RenderLook.Default` is the engine's value. It holds the art direction's values:

- The world's own surfaces: saturation, value, the side and top shade, and how far their colour wanders over about
  20 m (`WorldHueDrift`, each channel scaled by a value noise in `mesh.frag`).
- The sunlight's warmth and the shade's coolness.
- The sky light's share of the horizon colour, the ground's light and bounce, the occlusion and sky floors, and full
  glow's strength.
- The mesh assets' edge light: its strength, a share of the sky's light, and its sharpness.
- Where the view's fog starts, the haze's half distance, where the haze starts, the most of a surface it hides, and how
  fast each colour fades in it.
- The ground haze: its thickness, half distance, floor height in the world, and falloff with height.
- The sky's zenith (clear and overcast), the power by which the sky turns to it, the below-horizon share, and the sun
  disc's sharpness, strength, and overcast hiding.
- The glow round the sun: its strength and sharpness.
- The sky's light over the half of the sky towards the sun (the lobe: its strength and sharpness), its darkening away
  from the sun, and a band of light along the horizon (its strength and height).
- The point lights' halos in the air: their strength, how many of the frame's brightest lights have one, and their
  radius.
- The clouds: their cover, colour, opacity, scale, stretch, heading, height, drift, warp, light, ridge and billow
  shares, edge softness and erosion, and shading by their thickness towards the sun. Also their share of the haze, how
  much their light depends on the view against the sun, their reach, their thickness in metres, their shadow on the
  ground, and how much a second layer and the lower wisps show.
- The exposure, the grade's desaturation and darkening, and the split tone's tints and bounds.

The edge light, the glow, the clouds, the ground haze, the hue's wandering, the lobe, the darkening away from the sun,
the horizon band, the haze's start and cap, and the lights' halos are off in `RenderLook.Default`.

- The edge light is in `shadeSurface` in `mesh-shade.glsl`, by its `rim` argument: `mesh-asset.frag` gives 1 and
  `mesh.frag` 0, so only the mesh assets take it. It is the sky's light where a surface turns from the eye, as much as
  the sky field lets the sky reach it.

- The glow is part of `skyColour` in `sky.glsl`. The fog takes the sky's colour behind a surface, so the haze glows
  towards the sun too.
- The lobe, the darkening, and the horizon band are part of `skyColour` too, so the haze takes them.
- The clouds are `rendering-clouds.md`'s.
- The haze's density along a ray is `hazeDensity` in `lighting.glsl`, with the ground haze averaged between the eye's
  height and the surface's. `hazeOver` applies the haze's start and cap.
- `fogAt(vec3, float)` gives the share of a surface lost as one colour, for what fades with the fog (the particles'
  sprites, a game's passes). `fogAmounts` gives it for each colour by the look's fade.
- `air.glsl`'s `throughAir` mixes a surface's colour into the sky's colour by `fogAmounts` and adds the haze glows.
  `mesh-shade.glsl`, `grass.frag`, and `particle-cubes.frag` call it. At the view's end every colour is the sky's, so a
  new term of the air keeps that.
- `fogAt(float, float)` stays for shaders that know only a distance, without the ground haze.

How it reaches the shaders:

- A game gives it per frame through its atmosphere source (`IAtmosphereSource.LookAt`, a default method returning the
  engine's). `AtmosphereSystem` sets `RenderView.Look`. `RenderLook.Lerp` blends two.
- The console's run-only `look` lays values over it (`LookTuning`, a table of every value without reflection;
  `RenderView.DrawnLook`).
- `GameLoop` writes the drawn look and the frame's haze glows into the frame slot's record (`MeshPass.LookOf`,
  `LookConstants.From`). The record
  holds the values and the light and colours worked out once a frame: the sunlight, the sky and ground light, the
  zenith and below-horizon colours, the sun's disc and glow, the grade's hue and amounts, the clouds' colour and
  heading, and the haze's and the ground haze's densities. The ground haze's floor is written local to the origin.
- `look.glsl` declares it: set 0, binding 17, and binding 8 of the particles' draw set (compiled with
  `LOOK_BINDING 8`). `lighting.glsl`, `sky.glsl`, `mesh-shade.glsl`, and `mesh.frag` read it as `LOOK`, the slot's by
  `view.slot`.

A new look value takes:

1. A property in `RenderLook`, with the old look as its default.
2. A row in `LookTuning`'s table, for the `look` command. `RenderLookTests` fails without it.
3. When a shader reads it, a field in `LookConstants` and in `look.glsl`, written in `LookConstants.From`. The `GpuLayout`
   tests at binding 17 and at the particles' binding 8 check that the two match.

Work out in `LookConstants.From`, once a frame, whatever a shader would work out at every pixel from the value and the
frame's atmosphere.

### Haze glows

A haze glow is a place where the air glows: a large source's light that the haze scatters towards the eye (`HazeGlow`:
an absolute place, a colour, and a radius).

- A game's source gives them (`IAtmosphereSource.GlowsAt`; none by default). `AtmosphereSystem` copies the first
  `HazeGlow.Most` into `RenderView` (`SetGlows`, `Glows`). A match's end clears them.
- `LookConstants.From` writes them into the record, their places local to the origin (`HazeGlowConstants`).
- `hazeGlows` in `sky.glsl` adds their light along a ray: one over the squared distance to the glow softened by its
  radius, integrated along the ray, and dimmed by the haze between the glow and the eye.
- `throughAir` adds it in front of every surface, and `world-sky.frag` along the sky's rays. An orthographic view draws
  none, since its rays do not start at the eye.

### Light halos

- `lightHalos` in `light-halos.glsl` adds the light the haze scatters from the frame's brightest point lights along a
  ray: the look's halo count of the slot's list, which comes brightest first (`rendering-light.md`). Each is one over
  the squared distance softened by the halo radius, integrated along the ray, faded past the light's reach.
- `mesh-shade.glsl`, `grass.frag`, and `world-sky.frag` add it after the air. An orthographic view draws none.
- Each light costs every pixel two arctangents, so halos are dear while lights are near the eye. Off, they cost
  nothing: the shader returns before its loop.

### The sun

- The sun's direction is the atmosphere's (`Atmosphere.SunDirection`, from the game's source each frame;
  `Atmosphere.TowardsSun`). The engine's own is `ViewConstants.DefaultSunDirection`, high from +x and +z.
- The console's run-only `sun AZIMUTH ELEVATION` and `time HH:MM` override it (`RenderView.SunOverride`, `DrawnSun`).
  The test day has sunrise at 06:00, the engine's sun at 12:00, and sunset at 18:00.
- `SunPath` says how much light the sun gives: all of it a little over the horizon, none a little under, fading
  between. The look's sunlight and sun disc use it (`LookConstants.From`).
- When the sun is under the horizon it is night. `ViewConstants.Create` leaves the shadows switch off, so no caster
  is listed and the maps are only cleared.
- A game's moon is this light at night. The game points the sun's direction at the moon, above the horizon, with a dim,
  cool sun colour and the look's disc sharpness and strength for its disc. A direction under the horizon gives no
  light.
- The particles' sunlight fades with the daylight too (`LookConstants.Daylight`).
- The shadow cascades follow the sun (`rendering-light.md`).

### Tests

`RenderLookTests`, `SunPathTests`, and `RenderViewTests`.
