---
paths:
  - "src/Itera.Engine/Rendering/MeshInstances.cs"
  - "src/Itera.Engine/Rendering/Meshes/{MeshAssetDraws,MeshAssetPass,MeshAssetPass.Resources,MeshAssetLayout,MeshAssetFrame,MeshBounds,MeshPoseCulling}.cs"
  - "src/Itera.Engine/Rendering/GpuData/{MeshAssetDraw,MeshAssetMaterial,MeshSkinGroup,MeshSkinVertex}.cs"
  - "src/Itera.Engine/Rendering/Shaders/{mesh-asset-data.glsl,mesh-asset.vert,mesh-asset.frag,mesh-asset-shadow.vert,mesh-asset-shadow.frag,mesh-skin.comp}"
  - "tests/Itera.Engine.Tests/Rendering/Meshes/{MeshAssetDrawsTests,MeshAssetPassTests,MeshPoseCullingTests}.cs"
---

## Drawing mesh assets

The run's mesh assets (`MeshAssets`, models read from glTF files) are drawn with the world and lit as it is. The assets,
their import, and their rigs are in `mesh-assets.md`. Related files: `rendering.md` (the core), `rendering-light.md`
(the lighting and the main pass), and `rendering-views.md` (an image view shown on a material).

### What a frame system gives

- A frame system adds each asset to draw to `MeshInstances` (run-wide): `Add(asset, transform, tint, shadow)`. The
  asset is an index. The transform is a `Matrix4x4` used as `M * v`. The tint is linear RGBA, or `NoTint` for none. The
  shadow is a `ModelShadow`.
- Instances past the frame's room are left out, with one Warning a run.
- `GameLoop` clears `MeshInstances` on every frame path.
- The engine's `MeshDrawSystem` adds every entity with `MeshPlacement` and `DrawnMesh` (`systems.md`).
- A skinned asset drawn posed (`AddPosed`) also gives its rig's skinning matrices (`Rig.WritePalette`), kept in the
  frame's pool (`MeshInstances.Palettes`).

### Culling before the posing

`MeshPoseCulling` (run-wide) tells a frame system what the frame's lists may draw, so it poses nothing else.

- `GameLoop` gives `RenderView` the picture's size, the view's distance, and the origin before the frame systems
  (`RenderView.BeginFrame`). `RenderView.TryFrameView` then gives the view the frame will have, once a frame system set
  the camera. `GameLoop` builds the frame's own view by the same `RenderView.CreateView`.
- `Begin` fits the camera's volume (`MeshView.Predict`), and each due image view's frustum alone
  (`ViewVolume.SetFrustum`: image views draw no shadows). It returns false without a picture
  or a camera set this frame: then nothing is culled.
- `MayDraw` tests a sphere as `MeshAssetDraws` tests an instance: the camera's view, the near and far cascades as its
  shadow allows, and each due image view.
- `GameLoop` calls `Check` after the views begin. When the camera, the sun, or the views due differ from those culled
  by, it warns once a run. A change of the shadows' switch alone is not checked: the settings turn it during the UI.
- `MeshBounds` holds the spheres: a posed instance's (`Posed`), and one round every pose of an asset (`EveryPose`: the
  rest pose and each clip at `MeshBounds.SamplesPerClip` times).

### The draw lists

`MeshAssetDraws.Write` runs on the main thread after `WorldMeshes.Upload` and before `MeshView.End`.

- It culls each instance's bounding sphere against the view and both cascades, as the instance's shadow allows. The
  sphere is the asset's, its radius times the transform's largest scale. A posed instance's comes from its joints'
  posed places grown by their reaches (`MeshAssetLayout.JointPositions` and `JointReaches`), and its unweighted
  vertices' sphere.
- An asset with levels (`MeshAssetLayout.LevelAssets`) is drawn in each list at the coarsest level whose height is
  above its sphere's. The camera's and each image view's measure pixels: `2 * radius * PixelsPerMetre`, over the depth
  (at least `Near`) in perspective. A cascade measures texels (`NearTexelMetres`, `FarTexelMetres`) and never draws
  finer than the camera. The blended list takes the camera's level.
- It groups the draws by asset primitive with a counting sort.
- The camera's list puts each primitive's draws nearest first, so the depth test rejects hidden pixels before they are
  shaded. A counting sort takes the distances into 16 buckets by their float's exponent. The cascades' and the image
  views' lists keep the instances' order.
- It writes into the frame slot's regions one `DrawIndexedIndirectCommand` per primitive with instances. The command's
  `vertexOffset` is the asset's first vertex and its `firstInstance` its first `MeshAssetDraw`: the transform, the
  material, the tint, and the vertex delta.
- There are three lists: the camera's and the two cascades'. Each has four ranges of pipeline (`MeshAssetRange`:
  opaque or masked, one-sided or two-sided).
- A list is limited to `MeshAssetPass.MaxDraws` draws and `MaxCommands` commands. A full list warns once.
- The counts travel in `RenderFrame.MeshAssets` (`MeshAssetFrame`, with the skin groups, their vertices in all, and
  their matrices).

### Skinning

- Each posed instance that any list draws gets a skin group for each level it is drawn at (`MeshSkinGroup`: the
  level's first vertex and skin entry, its output's first vertex, its first skinning matrix, and its vertex count). Its
  matrices go in the slot's regions once, shared by its groups.
- Each list's draws read the skinned copy of the level it draws (`MeshAssetDraw.VertexDelta`, added to
  `gl_VertexIndex`; `MeshAssetDraw.NotPosed` for the asset's own). An instance is skinned whole or not at all.
- The skinned copy is two streams, positions and packed normals and tangents (`MeshAssetPass.SkinnedVertexBytes` a
  vertex). The camera's draws take the rest of the vertex from the asset's own; the cascades read the positions alone.
- When the slot's room cannot take every instance, those marked `DrawnMesh.SkinFirst` (`MeshInstance.SkinFirst`: a
  game's players, say) are skinned first, then the rest, each nearest the camera first. Those left over are not drawn
  that frame, and those with matrices that do not match their rig are drawn unposed, each with one Warning a run.
- When the run read a skinned asset, the streams hold a region for each frame slot (`Engine:Renderer:SkinMegabytes`,
  lowered to fit the device's range and one dispatch, `MostSkinnedVertices`).
- `RecordSkinning` runs before the cascades, outside rendering, and only on a fresh frame (a frame presented again
  reuses its slot's). It dispatches `mesh-skin.comp` once, an invocation for each vertex of every group; each finds its
  group by a binary search of the groups' outputs, which lie one after another. Each vertex moves by its
  four joints' matrices blended by its weights. Its normal and tangent turn by the blended matrix, right for turns and
  uniform scales; a joint scaled unevenly skews them. They are packed again in `Octahedral`'s encoding. An unweighted
  vertex is copied.
- An asset of more than `MostSkinnedVertices` is drawn unposed, with one Warning.
- A barrier from the compute writes to the vertex shaders' reads follows. The pass is timed as `Skinning`, part of
  `Voxels`.

### The pass

`MeshAssetPass` (`Rendering/Meshes/`) is made at the run's step "mesh assets on the gpu", after "mesh assets" and before
the render thread.

- It holds device-local buffers of every asset's vertices (`MeshAssetVertex`, pulled by `gl_VertexIndex`), 32-bit
  indices, and materials (`MeshAssetMaterial`: factors, alpha cutoff, five packed texture references, flags), and one
  image per texture with every mip.
- It holds a texture assets share (the same `MeshTextureData`: a file's parts, a model's variants) once
  (`MeshAssetLayout.Textures`). Each asset's textures map to their slots (`MeshAssetLayout.TextureSlots`), and its
  materials are packed through that map (`MeshAssetLayout.Slotted`).
- The formats are `Rgba8Srgb` for colour and emission, `Rgba8Unorm` for occlusion and metal-roughness, and `Rg8Unorm`
  for normals. A model's compressed textures are `BC7_SRGB`, `BC7_UNORM`, and `BC5_UNORM`, each level copied in whole
  4x4 blocks with its own size as the extent.
- Draws and commands are host-visible, one set for each frame slot.
- The first frame copies the buffers and images from one upload buffer (`RecordUploads`, before the mesh pass). The
  buffer is freed `FrameSync.FramesInFlight` frames later.
- It owns descriptor set 1 (`mesh-asset-data.glsl`). Set 0 is `MeshPass`'s. Set 1 holds:
  - six immutable samplers: linear, with anisotropy where the device can, or nearest; each repeating, clamped, or
    mirrored;
  - the textures, `MESH_TEXTURE_COUNT` of them: a white stand-in first, each of the assets' once, then a slot for each image view,
    indexed with `nonuniformEXT`;
  - the vertices, the materials, and the draws;
  - for the skinning, the skins, every slot's skin groups, and every slot's skinning matrices;
  - the skinned streams, every slot's region of positions and of normals and tangents.
- The device's room caps the textures: the lower of its per-stage and per-set sampled images less the mesh pass's, and
  its per-stage resources less the other descriptors and attachments a stage holds. Both are less one slot for each
  image view. Textures past the cap are left out with one Warning, and their materials are drawn with their factors.
- Vertices past the device's storage buffer range stop the run at the step.
- It makes its camera pipelines again when the mesh pass's samples or colour format change, and every pipeline on a
  shader reload.

### Pipelines and shading

The pass has the skinning's pipeline and these:

- The camera's (`mesh-asset.vert`, `mesh-asset.frag`) and the cascades' (`mesh-asset-shadow.vert` with
  `depth-only.frag`, or `mesh-asset-shadow.frag` for masked materials), for each of the four list ranges.
- The blended pipeline (below).
- Depth and culling are the world's: back faces are culled unless the material is two-sided. The cascades draw the
  faces turned away from the sun, so an open single-sided mesh, such as one quad, casts no shadow when its front
  faces the sun.
- `MeshPass.Record` calls the pass through `IMeshPassDraws`. `RecordShadows` runs inside each cascade's rendering.
  `RecordCamera` runs in the main view after the models and before the sky. It is timed as `Meshes` (`GpuPass`).
- `mesh-asset.frag` draws glTF's base colour: the factor times the texture, the vertex colour, and the draw's tint. It
  cuts at the alpha cutoff in the masked ranges. The normal map tilts the lit normal in tangent space, and a
  two-sided surface's back is lit from its own side. The occlusion is the cavity and the emission is glow. Then
  `shadeSurface` runs with the openness of flat ground.
- Metalness and roughness are read and kept, not drawn.

### Blended materials

glTF's `BLEND` materials (`MeshAssetRange.Blended` and `BlendedTwoSided`, after the four list ranges) are in no list's
ranges and cast no shadow.

- `MeshAssetDraws` writes them into the slot's fourth list (`MeshAssetPass.BlendedList`). The seen instances are sorted
  back to front by their sphere's centre, farthest first. An instance's primitives stay in its asset's order.
- Each primitive is one draw and one command of one instance. A two-sided one is two: its back faces, then its front
  faces (`MeshAssetDraw.Sides`). The commands are limited to `MaxCommands`; a full list warns once a run.
- `MeshPass.Record` calls `RecordTransparent` after the sky, which would paint over a surface that writes no depth. One
  `vkCmdDrawIndexedIndirect` draws every command in order, with one pipeline: `mesh-asset.vert` and `.frag` compiled
  with `MESH_ASSET_BLEND`.
- The pipeline does no culling; each draw drops the faces it does not show. The depth is tested, not written. The
  blend is premultiplied alpha over what is drawn, with the colour shaded and fogged as an opaque surface's, then
  times its alpha.
- It blends on the frame's display colours, as the particles do, inside the multisampled rendering, so its edges are
  anti-aliased. It is timed with the world (`Voxels`), not `Meshes`.
- A draw's tint multiplies the base colour's alpha too, so a tint's alpha fades a blended instance.
- Objects that cross, or one inside another, can show in the wrong order from some sides: the sort is per object, not
  per pixel.
- A blended face in the plane of an opaque surface flickers with it as the view moves. Leave such a face out or lift it
  off the surface, as the mesh sample's see-through boxes do.
- A blended surface writes no depth, so the particles and the debug lines, drawn after the world and tested against its
  depth, draw over one in front of them, untinted.

### Tests

`MeshAssetDrawsTests`, `MeshAssetPassTests`, and `MeshPoseCullingTests`.
