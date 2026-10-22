---
paths:
  - "src/Itera.Engine/Assets/**"
  - "src/Itera.Engine/Maths/Octahedral.cs"
  - "src/Itera.Engine/Rendering/GpuData/MeshAssetVertex.cs"
  - "tests/Itera.Engine.Tests/Assets/**"
  - "tests/Itera.Engine.Tests/Maths/OctahedralTests.cs"
---

## Mesh assets: models from glTF files

A mesh asset (`MeshAsset`, `src/Itera.Engine/Assets/`, namespace `Itera.Engine.Assets`) is a mesh drawn as triangles
with texture coordinates, not voxels. It is a model read from a glTF 2.0 file, or one a game builds in code. A game
builds its textures from RGBA8 texels with `MeshTextureData.FromRgba`, which makes the mips (`samples/Itera.MeshSample`'s
`Yard` does). Drawing the assets is in `rendering-mesh-assets.md`. Animation poses are in `animation.md`.

### What an asset holds

- Vertices (`MeshAssetVertex`, upload-ready): a float position in the asset's space in metres, the normal and the
  tangent in `Octahedral` encodings with the bitangent's sign in the tangent word's top bit, two sets of texture
  coordinates, and a linear RGBA colour.
- 32-bit indices, three a triangle, counter-clockwise from the front, into the asset's own vertices.
- Primitives (`MeshPrimitive`): each a run of indices with one material.
- Materials (`MeshMaterialData`): glTF's metallic-roughness factors and textures, alpha mode and cutoff, and
  double-sidedness. A material a game names may show an image view instead of its colour texture
  (`RenderViews.ShowOnMaterial`, `rendering-views.md`). Every material of the asset with that name shows it. A
  material with no name, `""` from a glTF file, cannot be named.
- Textures (`MeshTextureData`): every mip level, level 0 first (a compressed one's in whole 4x4 blocks,
  `MipChain.LevelBytes`), with the model's image they were made from (`Image`) and its hash (`SourceHash`).
- A skinned asset also has a `Rig` and each vertex's joints and weights (`Skin`, `MeshSkinVertex`: four 16-bit joints
  and four 16-bit weights). Its vertices are where the mesh was bound.
- Points (`MeshPoint`, `Points`, `FindPoint(name)`): named places such as a muzzle or a grip, each with a joint (-1 for
  none), a position in metres, and a rotation. A point under a joint is in that joint's space, so
  `MeshAttachment.To(holder, point.Joint, point.Position, point.Rotation)` holds an item there. Any other point is in
  the asset's space.

The assets are drawn with the world: an entity with `MeshPlacement` and `DrawnMesh`, posed by a `MeshAnimator`
(`systems.md`), or any frame system through `MeshInstances` (`AddPosed` with the rig's skinning matrices).

### The rig

A `Rig` (`Rig.cs`) is the joints of a skin, parents first, in metres. It holds:

- Each joint's parent (-1 for none).
- The fixed transform from its parent joint (or the scene's root) to its own parent node (`Offsets`: the nodes between
  that are not joints).
- Its rest transform (`JointTransform`: translation, rotation, scale), its inverse bind matrix, and its place in the
  bound pose (`BindPositions`).
- How far its vertices reach from it (`Reaches`), which bound a posed instance.
- Its clips (`RigClip`): tracks (`RigTrack`) of one joint's translation, rotation, or scale. Sampling is step, linear
  with the shorter arc for rotations, or cubic spline with the file's tangents scaled by the time between keys. Keys
  are held at the ends. The clip's `Duration` is its last key.

Posing allocates nothing:

- `Pose(clip, seconds, locals)` poses the rig. Clip -1 gives the rest pose.
- `WritePalette(locals, palette)` writes each joint's inverse bind, then its place in the asset's space:
  `v * InverseBind * Global`, with `Global = local * offset * parent's Global`. `Globals` alone works out the globals.

A pose can be shaped before its palette, also with no allocation:

- `FindJoint(name)` finds a joint.
- `Mask(root, weights, weight)` marks a joint and every joint below it.
- `Blend(under, over, weights, into)` blends joint by joint, rotations the shorter way: legs from one clip, the upper
  body from another.
- `Turn(locals, joint, turn, globals)` rotates in the asset's space about the joint's own place, the joints below
  swinging round it: a spine bent to an aim.
- `Move(locals, joint, offset, globals)` moves in the asset's space, the joints below going with it: hips lowered to
  let a foot reach lower ground.
- `Place(locals, placed, placing, globals)` sets the marked joints to given transforms in the asset's space in one
  pass, the others following their parents: a ragdoll's bodies posing a figure.
- `ReachTwoBone(locals, upper, lower, end, target, pole, globals)` reaches an arm or a leg to a point in the asset's
  space, or stretches it towards it, the middle joint bending towards the pole: a hand on a rifle.

`Turn`, `Move`, and `ReachTwoBone` work out only the globals they need: the joint's parent, or the reach's end, and the
joints above it. Their `globals` is scratch. Read none of it after one without calling `Globals` again.

`Globals(locals, globals, joint)` works out one joint and those above it alone. It gives the same bits as the pass over
every joint (`Globals(locals, globals)`). A shaper that reads a hand or the head between its changes takes a few
matrices where the pass takes the whole rig's. `MeshPoses.JointInWorld` reads a held item's joint so.

The rig works in floats. Never use it for what peers must agree on. Use it for drawing, and for what one peer alone
judges (a host's hits on posed bodies). The deterministic skeletons for bodies, aim, and foot IK are
`Animation.Skeleton`'s (`animation.md`). Clips shared between rigs by joint name are not built.

### Declaring and reading

`MeshAssets` (run-wide singleton) holds the run's assets by name and index.

- A game declares its models at its run startup (`IRunStartup`, the run's "game" step). Mods may declare at theirs.
- `Declare(name, path, metresPerUnit)` declares a file. The path is relative to the executable's folder. The scale is
  0.01 for a file in centimetres.
- `Add(name, MeshAsset)` adds a mesh built in code.
- `DeclareVariant(name, of, baseColours)` declares a variant of an asset declared or added before it. It shares the
  vertices, indices, primitives, textures, rig, and clips (the arrays are shared, so its clips' indices are its
  source's). It replaces the base colour of each material named, in linear RGBA: a team's colours, where a tint, which
  multiplies every colour, cannot turn one hue into another. It is made at the step after the files are read. A
  material its source lacks stops the step, naming it.
- The run's "mesh assets" step, after the mods', reads every declared file on worker threads (`LoadAll`). Without
  graphics (`Engine:Window:Mode=None`, and `HeadlessRun`) it decodes no images and materials name no textures.
- A file that cannot be read stops the run with an Error line naming it. A warning covers an image it cannot decode, an
  unlit material, lines or points left out, and `KHR_texture_transform` not applied.
- A declaration after the step is refused.
- Assets stay for the run, so an ECS component refers to one by its `int` index (`Find(name)`, looked up once), with
  no generation. Models are not read during play.

### The import

`GltfImport` is the only code that uses SharpGLTF, at load alone.

- It refuses a file that requires an extension it cannot read, naming the file and the extension: compressed geometry
  (`KHR_draco_mesh_compression`, `EXT_meshopt_compression`, `KHR_meshopt_compression`), Basis textures
  (`KHR_texture_basisu`), and any other not in its list (`KHR_mesh_quantization`, `KHR_materials_emissive_strength`).
- It walks the default scene (else the first). It reads each mesh's triangles, strips, and fans. The node's world
  transform and the unit scale are baked into the vertices (normals by the inverse transpose), and the winding and the
  tangents' handedness are flipped where the transform mirrors. A skinned mesh is kept as its file stores it (glTF:
  its node's own transform does not apply).
- The first skin a mesh of the scene uses becomes the asset's rig (`GltfImport.Rig.cs`). Meshes on another skin stand as
  stored, with a warning.
  - The joints are ordered by their depth below the scene's root, parents first.
  - Every translation (rest, offsets, inverse binds, clip keys and tangents) is multiplied by the file's unit (the
    uniform scale's `S⁻¹ M S`), so the rig is in metres as the vertices are.
  - Each skinned vertex keeps its four largest weights of `JOINTS_0`/`WEIGHTS_0` and `JOINTS_1`/`WEIGHTS_1`, scaled to
    sum to one, its joints in the rig's order. A vertex no joint weighs (a mesh of the asset off the skin) keeps
    all-zero weights and stands as stored.
  - Each animation's channels on the rig's joints become a clip by its name. Channels on other nodes and morph weights
    are left out, with a warning.
- Each named leaf node that holds nothing becomes a point: no mesh, camera, or light, not a joint of any skin, and no
  children (Blender writes an empty so). One under a joint of the rig takes the transforms between it and the joint.
- Triangles without normals get flat ones, each its own three vertices (glTF asks for flat normals). A primitive
  without indices gets them in order.
- A normal-mapped material's vertices without tangents get generated ones (`MeshTangents`: Lengyel's method, not
  MikkTSpace, so they differ at mirrored UV seams). The bitangent `cross(normal, tangent) * w` points the way v
  increases. A file's own tangents are used when it has them.
- Each image a material uses is decoded once (`TextureDecode`: PNG, JPEG, or WebP through Skia into RGBA8 with
  straight alpha and no colour conversion, as glTF asks). Its mip chain is made per role (`MipChain`, every level to
  1x1, a box filter for any size):
  - `Colour` (base colour, emission) as `Rgba8Srgb`, averaged in linear light.
  - `Linear` (occlusion in red, roughness in green, metalness in blue) as `Rgba8Unorm`.
  - `Normal` as `Rg8Unorm` (x and y of the renormalised vector; z is rebuilt).
  - An image in two roles makes two textures.
- A sampler maps to `MeshSampler`: nearest when its magnification filter is, and its wrap along s for both directions,
  with a warning when t differs.
- Alpha `BLEND` is kept as such in the material. The renderer draws it blended, sorted back to front per object, casting
  no shadow (`rendering-mesh-assets.md`).

### Compressed textures

A model's textures can be compressed offline for shipping (`CompressedTextures`). `tools/Textures`' model mode does it:
`dotnet run -c Release --project tools/Textures -- model PATH.gltf ...` (README there).

- Beside `Model.gltf`, the folder `Model.textures/` holds a DDS file for each texture the import makes, an image in a
  role: `imageN-colour.dds` (BC7 sRGB), `imageN-linear.dds` (BC7), and `imageN-normal.dds` (BC5).
- Each file holds every level the import makes at load (`CompressedTextures.DecodeAll`, through `TextureDecode` and
  `MipChain`), compressed in whole 4x4 blocks. A level's edge is repeated to fill its last blocks.
- `textures.json` (`CompressedTextureList`) holds the model's file, the encoder, and each texture's image, role, file,
  size, and the SHA-256 of the image's bytes as the model stores them, whether a file, a data URI, or a buffer view.
- A run with graphics (`TextureReading.PreferCompressed`) reads a texture from there when its entry's hash matches the
  image's and its file is whole, in its role's format, and at the entry's size (`MeshTextureFormat.Bc7Srgb`,
  `Bc7Unorm`, `Bc5Unorm`; `MeshTextureData.Compressed`).
- Anything else is decoded at load. A list that is stale, unreadable, or lacks the texture gives a Warning naming the
  tool. A model with decoded textures and no list gives an Information note.
- The hash covers the image alone. After a change to `TextureDecode` or `MipChain`, make the folders again.
- The folder ships with the model (`Models/*/**`). A game tests its models' folders against their images.

### Checking a model

`ModelInspection.Inspect(path, metresPerUnit, budget)` reads a file as the import does, with every texture decoded. It
returns a `ModelReport`: the model's counts, notes, and problems, each of a `ModelProblemKind`. A model is ready when it
has no problems.

- `Refused`: the import cannot read the file. Every required extension it lacks is listed; the import stops at the first.
- `Import`: each warning of the import.
- `Content`: what the import leaves out or makes up without a warning, which it counts in `ImportFindings`. These are
  weights past four joints, primitives without a material, and unnamed materials, joints, and clips.
- `Content` also covers generated tangents, animations left out, cameras and lights, extra scenes, vertices off the
  skin of a skinned model, empties with no name, and joints, clips, or points that share a name.
- `Budget`: past a limit of a `ModelBudget`. A game keeps its budgets by asset class in a JSON file
  (`ModelBudget.ReadClasses`), which refuses a limit it does not know.

The notes name meshes used at more than one node and extensions the engine does not read. SharpGLTF refuses a skinned
vertex whose weights sum to zero, so such a file is `Refused`. `tools/ModelCheck` runs the check from the command line
(its README), and `ModelInspection.ToJson` writes the reports it prints with `--json`.

### Package notes

- `SharpGLTF.Core` (MIT, no dependencies): `ModelRoot.Load` with `ValidationMode.TryFix`.
- The material channels are `BaseColor` (`RGBA`), `MetallicRoughness` (`MetallicFactor`, `RoughnessFactor`), `Normal`
  (`NormalScale`), `Occlusion` (`OcclusionStrength`), and `Emissive` (`RGB`, `EmissiveStrength`). Read them from
  `MaterialChannel.Parameters` by name. `MaterialChannel.Color` throws on a channel without a colour.
- `MeshPrimitive.GetTriangleIndices` gives triangles of strips and fans too.
- It does not decode images: `Image.Content` is the file's bytes.

### Tests

`GltfImportTests`, `RigImportTests`, `RigClipTests`, `RigShapingTests`, `MeshAssetsTests`, `TextureDecodeTests`,
`MipChainTests`, `MeshTangentsTests`, `CompressedTexturesTests`, `MeshPointsTests`, `ModelInspectionTests`, and
`OctahedralTests`.
