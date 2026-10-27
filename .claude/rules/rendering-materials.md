---
paths:
  - "src/Itera.Engine/Rendering/Materials/**"
  - "src/Itera.Engine/Rendering/Voxels/{IVoxelPalette,VoxelLook,IVoxelTintMap,VoxelTintSampler}.cs"
  - "src/Itera.Engine/Rendering/Meshes/{MeshPass.Bake,MeshPass.Textures}.cs"
  - "src/Itera.Engine/Rendering/GpuData/MeshMaterial.cs"
  - "src/Itera.Engine/Rendering/Shaders/{mesh.frag,mesh-surface.glsl,surface-noise.glsl,material-bake.comp}"
  - "tests/Itera.Engine.Tests/Rendering/Materials/**"
---

## The world's materials

A game's voxel materials are drawn from texture sets, from patterns, or plain. Related files: `rendering.md` (the core),
`rendering-world.md` (the meshes carry each vertex's material weights), `rendering-light.md` (the lighting),
`rendering-look.md` (the look), and `docs/rendering-redesign.md` (the art direction and the materials plan).

The art direction: a stylized look, less saturated and darker, with textures a few steps up from flat colours, and no
outlines, cracks, or scattered features unless the material is made of them. A game sets its light and grade as data
(`rendering-look.md`).

### How a material is drawn

The data is in `VoxelLook` and the game's `IVoxelPalette`, packed into the look and amounts words of `MeshMaterial`.
`WorldMeshes` packs the palette.

- A material whose look names a texture set (`VoxelLook.Texture`) is drawn from it (`surfaceTextured` in `mesh.frag`).
- A material with a pattern and no set is drawn from `mesh-surface.glsl`'s `surfacePattern`. `VoxelPattern` names the
  recipes (`Soil`, `Streaks`, `Mottle`, `Clumps`, `Plates`, `Grain`, and others). `Plates` bakes on the plane of x and
  y. The recipes are baked into a set once; no recipe is worked out per pixel.
- A material with neither is drawn plain (`surfacePlain`: its colour with the patches).
- The models keep flat colours.
- A strata or mottle band by height cannot live in a 2D tile, so it is applied per pixel (`strataBands`,
  `MeshMaterial.BandsTextureBit`). A textured material takes it where its look's `StrataBands` says, or else where its
  pattern is `Strata` or `Mottle`.
- A bake's `Blocks` courses wrap at the tile, so their joints meet where it repeats.
- A game's tint map blends a material's colour towards its tint colour (`IVoxelTintMap`: its corner and texels in
  absolute centimetres; none by default; `FieldTintMap` makes one from any field). The CPU reads it at an absolute
  point with `VoxelTintSampler`. A model's draw takes its own tint instead.
- Where two materials meet in a block, `mesh.frag` draws the one whose share plus its height is greater. `HeightBlend`
  times the set's height map, less a half, gives the height (`RecipeHeight` without a set). It reads no height where the
  sets' height ranges (`MeshMaterial.Heights`) decide it whatever the heights are.

### The texture sets

A set is a folder `Textures/<set>/` of a game, made by `tools/Textures` from a recipe's bake, from a texture recipe in
Python (`tools/Textures/RecipeTools`), or from CC0 photographs, and copied next to the executable. It holds:

- `colour.dds`: BC7 sRGB, with occlusion in alpha.
- `normal.dds`: BC5, x and y.
- `height.dds`: BC4.
- `set.json`: the texels, the tile's width in metres, the colour's mean in linear RGB, and the source.

Every set's heights have a mean of a half.

`MaterialTextureSets` (`Rendering/Materials/`, with `DdsFile`) reads the sets at `MeshPass.Initialize`.

- A set's texel size must be `MaterialTextureSets.LargeTexels` (the large arrays) or `SmallTexels` (the small ones).
  Any other size is refused and the set is left out. Bindings 11 to 13 are the
  large group's colour, normals, and heights, and 14 to 16 the small group's. A group with no sets gets a stand-in
  that is never written.
- Each set's height range (`HeightRange`, on `MaterialTextureSlot.Heights`) is found from the BC4 blocks of its largest
  level when it is read. `MeshMaterial.From` puts it in `Heights`.
- The first frame copies every level from an upload buffer that goes `FrameSync.FramesInFlight` frames later.
- `WorldMeshes` packs each material's set into `MeshMaterial`: `Texture` (layer plus 1, the large bit, the colour
  match), `TextureMetres`, and `TextureMean`. It warns for a name not loaded.
- `surfaceTextured` reads the sides of x, y, and z that the surface faces, filtered anisotropically where the device
  can (`VulkanContext.SupportsAnisotropy`): up to 16 times for the colours, 8 for the normals and heights.
  `textureCompressionBC` is required. Take derivatives before the branches.
- The plane noise of every world pixel (the patches, the strata's wander, the hue drift) comes from tables
  (`NoiseTables`, binding 18): one gather a lattice cell in place of four hashes (`planeNoise` in `mesh-surface.glsl`).
  The bake hashes, its lattices wrapped at its tile. The sky's clouds read a texture of their own (`CloudNoise`, binding
  19, `rendering-look.md`).
- The colour is kept round the set's mean by `VoxelLook.TextureDetail` and read `TextureBlur` mip levels softer
  (`MeshMaterial.TextureStyle`). `TextureRelief` sets the normal map's and the occlusion's strength.
- The colour moves to the material's own colour by `VoxelLook.TextureColourMatch` (0 keeps the photograph's) and by
  the tint as the material's colour moves.
- The normal map (OpenGL's convention) bends the lit normal. The occlusion is the cavity. The patches go on top.

### Baking a recipe

The console's run-only `materials bake ID top|side [NAME]` bakes material ID's recipe into images in the data
folder's `Bakes/NAME/` (`MeshPass.Bake.cs`, `material-bake.comp`).

- It works out the pattern at every detail over a tile that repeats (`surface-noise.glsl` with `MESH_SURFACE_TILE`), on
  the plane of x and z or of x and y.
- The images are colour at no tint, occlusion from the hollows, a normal map from the relief's slope, and height from
  the hollows.
- `tools/Textures` makes them into a set as it makes a photograph's (`"site": "bake"`). Its README has the steps.
- `material-bake.comp` compiles at the first bake, on the render thread, and stays. A bake whose shader does not
  compile logs an Error and makes nothing.
- A shader reload compiles it again only if a bake has compiled it, and makes the bake's pipeline again, so an edited
  recipe bakes without a restart.

### Console commands

- `materials textures|flat` draws materials from their sets (the default) or flat (`ViewSwitches.Materials*`).
- `materials ... all` draws every world material without a set from the first small set, for timings.
- `materials set ID SET|none [MATCH]` draws one material from another loaded set (`WorldMeshes.SetTexture`, the
  materials staged again), to compare candidates in the game.
- `materials tune` tries the style values.

### Tests

`MaterialTextureSetsTests`.
