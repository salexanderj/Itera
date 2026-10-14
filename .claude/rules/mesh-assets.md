---
paths:
  - "src/Itera.Engine/Assets/**"
  - "src/Itera.Engine/Maths/Octahedral.cs"
  - "src/Itera.Engine/Rendering/GpuData/MeshAssetVertex.cs"
  - "tests/Itera.Engine.Tests/Assets/**"
  - "tests/Itera.Engine.Tests/Maths/OctahedralTests.cs"
---

## Mesh assets: models from glTF files

A mesh asset (`MeshAsset`, `src/Itera.Engine/Assets/`, namespace `Itera.Engine.Assets`) is a mesh drawn as triangles with
texture coordinates, not voxels: a model read from a glTF 2.0 file, or one a game builds in code (its textures from
RGBA8 texels with `MeshTextureData.FromRgba`, which makes the mips; `samples/Itera.MeshSample`'s `Yard`). Its vertices
(`MeshAssetVertex`, 36 bytes, upload-ready: a float position in the asset's space in metres, the normal and the tangent
in `Octahedral` encodings with the bitangent's sign in the tangent word's top bit, two sets of texture coordinates, and
a linear RGBA colour) and its 32-bit indices (three a triangle, counter-clockwise from the front, into its own vertices)
are split into primitives (`MeshPrimitive`), each a run of indices with one material (`MeshMaterialData`: glTF's
metallic-roughness factors and textures, alpha mode and cutoff, double-sidedness; a material a game names may show an
image view instead of its colour texture, `RenderViews.ShowOnMaterial`, `.claude/rules/rendering.md`, "Image views":
every material of the asset with that name shows it, and one with no name, `""` from a glTF file, cannot be named). Its
textures (`MeshTextureData`) hold every mip level, level 0 first (a compressed one's in whole 4x4 blocks,
`MipChain.LevelBytes`), with the model's image they were made from (`Image`) and its hash (`SourceHash`). A skinned
asset also has a `Rig` and each vertex's joints and weights (`Skin`,
`MeshSkinVertex`: four 16-bit joints and four 16-bit weights); its vertices are where the mesh was bound. They are drawn
with the world (`.claude/rules/rendering.md`, "Mesh assets"): an entity with `MeshPlacement` and `DrawnMesh`, posed by a
`MeshAnimator` (`.claude/rules/systems.md`), or any frame system through `MeshInstances` (`AddPosed` with the rig's
skinning matrices).

A `Rig` (`Rig.cs`) is the joints of a skin, parents first, in metres: each joint's parent (-1 for none), the fixed
transform from its parent joint (or the scene's root) to its own parent node (`Offsets`: the nodes between that are not
joints), its rest transform (`JointTransform`: translation, rotation, scale), its inverse bind matrix, its place in the
bound pose (`BindPositions`), and how far its vertices reach from it (`Reaches`, which bound a posed instance), and its
clips (`RigClip`: tracks of one joint's translation, rotation, or scale, `RigTrack`, sampled step, linear with the shorter
arc for rotations, or cubic spline with the file's tangents scaled by the time between keys; held at the ends; the
clip's `Duration` is its last key). `Pose(clip, seconds, locals)` (the rest pose for clip -1) and `WritePalette(locals,
palette)` (each joint's inverse bind, then its place in the asset's space: `v * InverseBind * Global`, `Global = local *
offset * parent's Global`, `Globals` alone) allocate nothing. A pose can be shaped before its palette, allocating
nothing: `FindJoint(name)`; `Mask(root, weights, weight)` (a joint and every joint below it) and `Blend(under, over,
weights, into)` (joint by joint, rotations the shorter way: legs from one clip, the upper body from another); `Turn(locals,
joint, turn, globals)` (a rotation in the asset's space about the joint's own place, the joints below swinging round it:
a spine bent to an aim); `Move(locals, joint, offset, globals)` (a move in the asset's space, the joints below
going with it: hips lowered to let a foot reach lower ground); `ReachTwoBone(locals, upper, lower, end, target, pole, globals)` (an arm or a leg reaching a
point in the asset's space, or stretched towards it, the middle joint bending towards the pole: a hand on a rifle). In
floats, so never for what peers must agree on: for drawing, and for what one peer alone judges (a host's hits on posed
bodies); the deterministic skeletons for bodies, aim, and foot IK are `Animation.Skeleton`'s
(`.claude/rules/animation.md`). Clips shared between rigs by joint name are not built.

`MeshAssets` (run-wide singleton) holds the run's assets by name and index. A game declares its models at its run
startup (`IRunStartup`, the run's "game" step): `Declare(name, path, metresPerUnit)` (a relative path from the
executable's folder; 0.01 for a file in centimetres) or `Add(name, MeshAsset)` for a mesh built in code; mods may at
theirs. The run's "mesh assets" step, after the mods', reads every declared file on worker threads (`LoadAll`): without
graphics (`Engine:Window:Mode=None`, and `HeadlessRun`) it decodes no images and materials name no textures. A file
that cannot be read stops the run with an Error line naming it; warnings (an image it cannot decode, an unlit
material, lines or points left out, `KHR_texture_transform` not applied) are Warning lines. Then `Mesh assets: N
model(s), T triangles, X texture(s) (C compressed) of Y MB, read in Z ms`. A declaration after the step is refused. Assets stay for the
run, so an ECS component refers to one by its `int` index (`Find(name)`, looked up once), with no generation. Reading
models during play is not built.

`GltfImport` is the only code that uses SharpGLTF, at load alone:

- It refuses a file that requires an extension it cannot read, naming the file and the extension: compressed geometry
  (`KHR_draco_mesh_compression`, `EXT_meshopt_compression`, `KHR_meshopt_compression`), Basis textures
  (`KHR_texture_basisu`), and any other not in its list (`KHR_mesh_quantization`, `KHR_materials_emissive_strength`).
- It walks the default scene (else the first): each mesh's triangles, strips, and fans, with the node's world transform
  and the unit scale baked into the vertices (normals by the inverse transpose), the winding and the tangents'
  handedness flipped where the transform mirrors. A skinned mesh is kept as its file stores it (glTF: its node's own
  transform does not apply).
- The first skin a mesh of the scene uses becomes the asset's rig (`GltfImport.Rig.cs`); meshes on another skin stand as
  stored, with a warning. Its joints are ordered by their depth below the scene's root (parents first), and every
  translation (rest, offsets, inverse binds, clip keys and tangents) is multiplied by the file's unit (the uniform
  scale's `S⁻¹ M S`), so the rig is in metres as the vertices are. Each skinned vertex keeps its four largest weights of
  `JOINTS_0`/`WEIGHTS_0` and `JOINTS_1`/`WEIGHTS_1`, scaled to sum to one, its joints in the rig's order; a vertex no
  joint weighs (a mesh of the asset off the skin) keeps all-zero weights and stands as stored. Each animation's channels
  on the rig's joints become a clip by its name; channels on other nodes and morph weights are left out, with a
  warning.
- Triangles without normals get flat ones, each its own three vertices (glTF asks for flat normals); a primitive without
  indices gets them in order. A normal-mapped material's vertices without tangents get generated ones (`MeshTangents`:
  Lengyel's method, not MikkTSpace, so they differ at mirrored UV seams; the bitangent `cross(normal, tangent) * w`
  points the way v increases); a file's own tangents are used when it has them.
- Each image a material uses is decoded once (`TextureDecode`: PNG, JPEG, or WebP through Skia into RGBA8 with straight
  alpha and no colour conversion, as glTF asks), and its mip chain made per role (`MipChain`, every level to 1x1, a box
  filter for any size): `Colour` (base colour, emission) as `Rgba8Srgb` averaged in linear light, `Linear` (occlusion in
  red, roughness in green, metalness in blue) as `Rgba8Unorm`, `Normal` as `Rg8Unorm` (x and y of the renormalised
  vector; z is rebuilt). An image in two roles makes two textures. A sampler maps to `MeshSampler` (nearest when its
  magnification filter is, its wrap along s for both directions, with a warning when t differs).
- Alpha `BLEND` is kept as such in the material, and the renderer draws it blended, sorted back to front per object,
  casting no shadow (`.claude/rules/rendering.md`, "Mesh assets").

### Compressed textures

A model's textures can be compressed offline for shipping (`CompressedTextures`; `tools/Textures`' model mode,
`dotnet run -c Release --project tools/Textures -- model PATH.gltf ...`, README there). Beside `Model.gltf`, the folder
`Model.textures/` holds a DDS file for each texture the import makes, an image in a role (`imageN-colour.dds` BC7 sRGB,
`imageN-linear.dds` BC7, `imageN-normal.dds` BC5): every level the import makes at load (`CompressedTextures.DecodeAll`,
through `TextureDecode` and `MipChain`), compressed in whole 4x4 blocks (a level's edge repeated to fill its last
blocks), and `textures.json` (`CompressedTextureList`: the model's file, the encoder, and each texture's image, role,
file, size, and the SHA-256 of the image's bytes as the model stores them, whether a file, a data URI, or a buffer view).
A run with graphics (`TextureReading.PreferCompressed`) reads a texture from there when its entry's hash matches the
image's and its file is whole, in its role's format, and at the entry's size (`MeshTextureFormat.Bc7Srgb`, `Bc7Unorm`,
`Bc5Unorm`; `MeshTextureData.Compressed`), a quarter of the decoded size (the fox's 1024-texel colour: 1.3 MB against
5.3 MB). Anything else is decoded at load: a list that is stale, unreadable, or lacks the texture gives a Warning naming
the tool; a model with decoded textures and no list gives an Information note. The hash covers the image alone, so after
a change to `TextureDecode` or `MipChain` make the folders again. The folder ships with the model (`Models/*/**`); the
starter's `FoxModelTests` checks `Fox.textures` against the fox's image.

### Package notes

- `SharpGLTF.Core` 1.0.7 (MIT, no dependencies): `ModelRoot.Load` with `ValidationMode.TryFix`. Material channels are
  `BaseColor` (`RGBA`), `MetallicRoughness` (`MetallicFactor`, `RoughnessFactor`), `Normal` (`NormalScale`), `Occlusion`
  (`OcclusionStrength`), and `Emissive` (`RGB`, `EmissiveStrength`); read them from `MaterialChannel.Parameters` by name
  (`MaterialChannel.Color` throws on a channel without a colour). `MeshPrimitive.GetTriangleIndices` gives triangles of
  strips and fans too. It does not decode images (`Image.Content` is the file's bytes).
