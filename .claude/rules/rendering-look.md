---
paths:
  - "src/Itera.Engine/Rendering/{RenderLook,LookTuning,Atmosphere,SunPath}.cs"
  - "src/Itera.Engine/Rendering/GpuData/LookConstants.cs"
  - "src/Itera.Engine/Rendering/Shaders/look.glsl"
  - "tests/Itera.Engine.Tests/Rendering/{RenderLookTests,SunPathTests,RenderViewTests}.cs"
---

## The look, the sun, and the atmosphere

The look and the sun are data a game sets. The shaders read them from a per-slot record. Related files: `rendering.md`
(the core), `rendering-light.md` (the passes that use them), `rendering-materials.md` (the surfaces), and
`docs/rendering-extension.md` (what a game can change, and the choices offered for each).

### Where they come from

A game's `IAtmosphereSource` gives the sky, the sunlight, the haze, and the colour grade at the camera's absolute place.
`AtmosphereSystem` reads it each frame. Without a source, the `Engine:Renderer` sky options give them.

### The look

`RenderLook` is a record struct. `RenderLook.Default` is the engine's value. It holds the art direction's values:

- The world's own surfaces: saturation, value, and the side and top shade.
- The sunlight's warmth and the shade's coolness.
- The sky light's share of the horizon colour, the ground's light and bounce, the occlusion and sky floors, and full
  glow's strength.
- Where the view's fog starts and the haze's half distance.
- The sky's zenith (clear and overcast) and below-horizon shares, and the sun disc's sharpness, strength, and overcast
  hiding.
- The exposure, the grade's desaturation and darkening, and the split tone's tints and bounds.

How it reaches the shaders:

- A game gives it per frame through its atmosphere source (`IAtmosphereSource.LookAt`, a default method returning the
  engine's). `AtmosphereSystem` sets `RenderView.Look`. `RenderLook.Lerp` blends two.
- The console's run-only `look` lays values over it (`LookTuning`, a table of every value without reflection;
  `RenderView.DrawnLook`).
- `GameLoop` writes the drawn look into the frame slot's record (`MeshPass.LookOf`, `LookConstants.From`). The record
  holds the values and the light and colours worked out once a frame: the sunlight, the sky and ground light, the
  zenith and below-horizon colours, the sun's disc, the grade's hue and amounts, and the haze's density.
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
