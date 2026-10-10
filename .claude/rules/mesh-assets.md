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
texture coordinates, not voxels: a model read from a glTF 2.0 file, or one a game builds in code. Its vertices
(`MeshAssetVertex`, 36 bytes, upload-ready: a float position in the asset's space in metres, the normal and the tangent
in `Octahedral` encodings with the bitangent's sign in the tangent word's top bit, two sets of texture coordinates, and
a linear RGBA colour) and its 32-bit indices (three a triangle, counter-clockwise from the front, into its own vertices)
are split into primitives (`MeshPrimitive`), each a run of indices with one material (`MeshMaterialData`: glTF's
metallic-roughness factors and textures, alpha mode and cutoff, double-sidedness). Its textures (`MeshTextureData`) hold
every mip level, level 0 first. They are drawn with the world (`.claude/rules/rendering.md`, "Mesh assets"): an
entity with `MeshPlacement` and `DrawnMesh` (`.claude/rules/systems.md`), or any frame system through `MeshInstances`.
Skinned meshes are drawn in the pose their file stores until skinning is built (the mesh path's plan, phase 4).

`MeshAssets` (run-wide singleton) holds the run's assets by name and index. A game declares its models at its run
startup (`IRunStartup`, the run's "game" step): `Declare(name, path, metresPerUnit)` (a relative path from the
executable's folder; 0.01 for a file in centimetres) or `Add(name, MeshAsset)` for a mesh built in code; mods may at
theirs. The run's "mesh assets" step, after the mods', reads every declared file on worker threads (`LoadAll`): without
graphics (`Engine:Window:Mode=None`, and `HeadlessRun`) it decodes no images and materials name no textures. A file
that cannot be read stops the run with an Error line naming it; warnings (an image it cannot decode, an unlit
material, lines or points left out, `KHR_texture_transform` not applied) are Warning lines. Then `Mesh assets: N
model(s), T triangles, X texture(s) of Y MB, read in Z ms`. A declaration after the step is refused. Assets stay for the
run, so an ECS component refers to one by its `int` index (`Find(name)`, looked up once), with no generation. Reading
models during play is not built.

`GltfImport` is the only code that uses SharpGLTF, at load alone:

- It refuses a file that requires an extension it cannot read, naming the file and the extension: compressed geometry
  (`KHR_draco_mesh_compression`, `EXT_meshopt_compression`, `KHR_meshopt_compression`), Basis textures
  (`KHR_texture_basisu`), and any other not in its list (`KHR_mesh_quantization`, `KHR_materials_emissive_strength`).
- It walks the default scene (else the first): each mesh's triangles, strips, and fans, with the node's world transform
  and the unit scale baked into the vertices (normals by the inverse transpose), the winding and the tangents'
  handedness flipped where the transform mirrors. A skinned mesh is kept as its file stores it (glTF: its node's own
  transform does not apply); its joints, and drawing it posed, come with skinning (phase 4).
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
- Alpha `BLEND` is kept as such in the material; there is no transparent pass yet, so the renderer draws it as `MASK`
  with one Warning an asset.

### Package notes

- `SharpGLTF.Core` 1.0.7 (MIT, no dependencies): `ModelRoot.Load` with `ValidationMode.TryFix`. Material channels are
  `BaseColor` (`RGBA`), `MetallicRoughness` (`MetallicFactor`, `RoughnessFactor`), `Normal` (`NormalScale`), `Occlusion`
  (`OcclusionStrength`), and `Emissive` (`RGB`, `EmissiveStrength`); read them from `MaterialChannel.Parameters` by name
  (`MaterialChannel.Color` throws on a channel without a colour). `MeshPrimitive.GetTriangleIndices` gives triangles of
  strips and fans too. It does not decode images (`Image.Content` is the file's bytes).
