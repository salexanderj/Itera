# Blender's Python API as the pinned Blender has it

Knowledge of Blender's Python API from before 5.0 breaks in the pinned Blender (`tools/models/blender.json`). Most
breaks fail loudly with an AttributeError or a TypeError, and the build's log shows them. Some fail silently, and only a
check of the output finds them (the last list). Each entry below was checked against the release notes from 4.0 to
5.2, and most were tried on the pinned Blender.

Before calling an operator or a property you have not used in this toolkit, read its signature in the local
reference. `tools/models/build.py docs` builds it once, from the pinned Blender, and prints its folder of .rst files.
Search it, for example:

```text
grep -n "def gltf" <docs>/bpy.ops.export_scene.rst
grep -rn "action_slot" <docs>/bpy.types.AnimData.rst
```

Or ask Blender itself in a recipe or a probe:

```text
bpy.ops.export_scene.gltf.get_rna_type().properties.keys()
dir(action); node.inputs.keys(); modifier.properties.inputs.keys()
```

## What changed

```text
OLD FORM (fails or misbehaves)                     WHAT THE PINNED BLENDER TAKES                         SINCE
action.fcurves, action.groups, action.id_root      layered actions: anim_utils.action_ensure_channelbag_    5.0
  fcurves.new(..., action_group=...)                 for_slot(action, slot).fcurves.new(..., group_name=)
                                                     (the toolkit's clips.new_clip and key_pose do it)
anim_data.action = action (alone)                  also set anim_data.action_slot; it may not be assigned   4.4
render.engine = 'BLENDER_EEVEE_NEXT'               'BLENDER_EEVEE' (renamed in 4.2, back in 5.0)            5.0
scene.eevee.use_bloom, use_ssr, use_soft_shadows,  removed; scene.eevee.use_raytracing, shadow_ray_count,   4.3
  shadow_cube_size, gi_*, sss_*, gtao_factor         use_fast_gi, fast_gi_method
scene.eevee.use_gtao, gtao_distance                view_layer.eevee.ambient_occlusion_distance              5.0
material.blend_method, light.cycles.cast_shadow    material.surface_render_method, light.use_shadow         4.2
mesh.use_auto_smooth, auto_smooth_angle,           bpy.ops.object.shade_smooth_by_angle() or                4.1
  calc_normals_split()                               shade_auto_smooth(); mesh.corner_normals
import bgl; image.bindcode; GPUShader(source)      the gpu module; gpu.shader.create_from_info()            5.0
gpu.* in background mode                           call gpu.init() first                                    5.2
bpy.ops.x.y(override_dict, ...)                    with bpy.context.temp_override(...): bpy.ops.x.y(...)    4.0
tree.inputs.new(...), tree.outputs.new(...)        tree.interface.new_socket(name, in_out=..., socket_type=) 4.0
Principled "Subsurface", "Specular",               "Subsurface Weight", "Specular IOR Level",               4.0
  "Transmission", "Coat", "Sheen", "Emission"        "Transmission Weight", "Coat Weight", "Sheen Weight",
                                                     "Emission Color"
bone.layers, armature.layers, bone groups          armature.collections.new(); collection.assign(bone)       4.0
bone.select, bone.select_head, bone.hide           edit_bone.select, pose_bone.select, pose_bone.hide       5.0
bpy.ops.export_scene.obj, import_scene.obj,        bpy.ops.wm.obj_export, wm.obj_import, wm.ply_export,     4.0
  export_mesh.stl, import_mesh.ply                   wm.stl_export
bpy.ops.import_scene.fbx                           bpy.ops.wm.fbx_import                                    5.0
wm.collada_export                                  removed                                                  5.0
glTF export_colors=, export_selected=              export_vertex_color=, use_selection= (TypeError)         5.2
geometry nodes modifier mod["Socket_2"] = value    mod.properties.inputs.Socket_2.value = value             5.2
boolean solver='FAST'                              'FLOAT' (also 'EXACT', 'MANIFOLD')                       5.0
material.use_nodes = True, world.use_nodes         nothing: a node tree always exists                       5.0
scene['cycles'] and other dict access              attribute access (scene.cycles)                          5.0
render pass names 'DiffCol', 'IndexMA', 'Z'        'Diffuse Color', 'Material Index', 'Depth'               5.0
image_settings.file_format alone                   set image_settings.media_type = 'IMAGE' first            5.0
bpy.data.grease_pencils_v3                         bpy.data.grease_pencils                                  5.0
MeshEdge.bevel_weight, MeshEdge.crease             attributes 'bevel_weight_edge', 'crease_edge'            4.0
light probe 'CUBEMAP', 'PLANAR', 'GRID'            'SPHERE', 'PLANE', 'VOLUME'                              4.1
a bpy.types subclass's own __init__                must call super().__init__(*args, **kwargs)              4.4
foreach_set with the wrong type                    raises TypeError (was written silently)                  4.1
mathutils values to NumPy as float64               float32, and not contiguous                              5.0
brush.sculpt_tool                                  brush.sculpt_brush_type                                  5.0
Python 3.11 syntax and wheels                      Python 3.13                                              5.1
```

## What fails silently

```text
Blender ends with exit code 0 after a script raises           run it with --python-exit-code 1 (build.py does)
an action assigned with no slot animates nothing              set anim_data.action_slot (clips.new_clip does)
automatic weights leave a part unweighted, with a warning     count unweighted vertices (checks.scene does)
the exporter drops vertex colours the material does not use   export_vertex_color='ACTIVE' (export.glb with
                                                                vertex_colors=True)
the exporter writes every action with bone keys               export only the clips named (export.glb with
                                                                clips=[...]), and remove a retarget's source
                                                                with clips.remove_clip
material.use_nodes = True does nothing, with a warning        build node trees directly
a mapping node's tiling exports as KHR_texture_transform,     bake the material (materials.bake) or keep
  which the engine does not apply                               tiling at 1; ModelCheck reports it
render.opengl (a viewport render) fails in background mode    render with Workbench, EEVEE, or Cycles
```
