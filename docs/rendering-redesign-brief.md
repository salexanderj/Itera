# Rendering and world representation: a brief for the redesign

Written 2026-10-06 (revised the same day to plan for underground digging, mining, and exploration) from an outside review of `ARCHITECTURE.md`, `docs/initial-plan.md`, `docs/anti-aliasing.md`,
`docs/smooth-normals.md`, `docs/voxel-structures.md`, `docs/voxel-characters.md`, `docs/health-system.md`,
`docs/character-progression.md`, `docs/procedural-audio.md`, and `docs/edit-log-compaction.md`, as they stood on
2026-10-05. It is the starting point for planning a redesign, not a decided plan. Lucy chooses what is built.

The brief was written for Zorf TD, the game Itera was built with. Its examples (creeps, goblins, Titans, the fortress)
are that game's, and the docs it names `docs/initial-plan.md`, `docs/voxel-characters.md`, `docs/health-system.md`, and
`docs/character-progression.md` are now in that game's archive. Its conclusions about the world representation and the
renderer hold for any game on a destructible voxel world; `docs/rendering-redesign.md` records what was built from it.

## How to use this brief

This brief was written without access to the code, only to the docs. Before planning from it:

- Check every figure quoted here against the current code and `ARCHITECTURE.md`. The figures are copied from the docs and
  may be out of date.
- Map the real dependencies of the GPU voxel mirror and the raymarcher before proposing what replaces them. The section
  "What depends on the raymarcher" lists the ones the docs mention, and there are probably more.
- Present choices to Lucy as measured options with trade-offs, as the existing docs do. The recommendations below are
  recommendations, and the open questions at the end are hers to decide.
- Do not delete the raymarcher. Shelve it on a branch. It is a large, polished body of work that may be worth
  revisiting, for example as a close-range detail layer.
- Treat outside games named here (Teardown, Deep Rock Galactic) as references to verify, not as facts relied on.

## The question that was asked

Lucy asked whether the game's fundamental design suits what it is meant to be: a first-person, co-op tower defence game
that joins a Roshan Defense–style Dota 2 custom game with an earlier Unity prototype. That prototype had players build
huge towers against Attack on Titan–style giants while levelling their own characters to fight swarms of smaller
enemies. The Unity prototype was abandoned partly because massive scale (huge enemies, huge swarms) was hard to make
efficient there. Itera exists to get full control of performance and to integrate the game holistically.

Lucy's two fears were these:

1. **Performance.** She wants the game to run extremely well across a range of hardware, and doubts the current design
   can deliver that.
2. **Art direction.** She wants a minimalist look with high visual clarity, clean images, and fast frames, but with a
   distinct dark fantasy style. Getting there through the current renderer has been slow and finicky.

The specific doubt was whether a world of 1 cm voxels rendered by raymarching is worth its cost. The only reason it was
chosen was a destructible, dynamic world. The doubt arises because the voxels are then smoothed, and every standard
rendering technique (shadows, anti-aliasing, ambient occlusion, normals, texture filtering) has had to be rebuilt by
hand.

The project is at a tipping point where a large change is still affordable. World generation needs heavy iteration
anyway. Items are barely implemented. There are no towers yet, and characters are basic.

Lucy has also said the game may later include underground digging, mining, and exploration. Nothing about it is
decided yet, but the redesign must not close that door. Underground play stresses a world representation in ways the
surface does not: lighting without the sky, hidden geometry, layered navigation, and much longer edit logs. So every
recommendation here is checked against it, and a dedicated section (H) covers what to design for now.

## The conclusion

The doubt is justified, and it is not just the frustration of a hard week. Most of the pain is inherent to the current
choice, not incidental to it.

The recommendation has two parts:

1. **Separate the world's simulation representation from its rendering representation.** Keep a voxel or density
   volume as the authoritative world for edits, physics, navigation, networking, and saves. Render it as triangle meshes
   extracted from that volume, through a conventional raster pipeline.
2. **Drop the 1 cm world resolution.** Nothing in the gameplay needs it. Something around 5 to 10 cm, with density
   rather than plain occupancy, gives destruction that reads just as well, at a small fraction of the cost.

Everything outside rendering and the voxel resolution looks sound and should carry over: storage concepts, the edit
log, deterministic generation, networking, Steam, the ECS, flow fields, Bepu loose bodies, health, progression,
combat, procedural audio, and UI.

Keeping a true 3D volume as the world, rather than moving to a heightmap, is what keeps underground play possible. The
recommendations deliberately keep it.

## Problems with the current method

### 1. Most of the 1 cm resolution never reaches the screen

At 2560x1440 a 1 cm voxel is about one pixel wide at 12.8 m. Level 0 is drawn only to 13.4 m. So 1 cm detail exists in
a bubble of about 13 m around the camera.

Inside that bubble, surfaces are deliberately smoothed by the 7x7x7 smooth-normal box (`docs/smooth-normals.md`). The
docs also record that a per-voxel colour variation was removed because it showed as a mosaic of 1 cm squares.

The project therefore pays the full cost of a 1 cm world and then hides that resolution, because 1 cm steps look worse
than smooth surfaces. That cost covers memory, streaming, encoding, edit replay, and every ray step.

### 2. The world pass is bound by memory latency, and this is fundamental

On the RX 5700 XT at 2560x1440, the world pass measured 8.85 ms on 2026-10-05 across the seven benchmark views, after a
long series of careful optimisations. Lucy's target is 6 ms at render scale 1. The pass breaks down as follows:

| Part | Time |
|---|---|
| Camera rays | 4.25 ms |
| Smooth normals | 2.11 ms |
| Shadow rays and smoothing | 0.92 ms |
| Shading | 1.54 ms |

Shader-clock counters show a wave's step costs about 4,700 cycles, nearly all of it waiting on memory. Each step reads
four or five levels of the tree one after another.

This is the nature of per-pixel traversal of a sparse tree. It does not point to a missing trick. The cost scales with
pixel count and memory latency, both of which get worse on the weaker GPUs the game is meant to support: integrated
graphics, handhelds, older cards. The 5700 XT is a mid-range card from 2019, and the world pass alone already exceeds
the target there. Render scale with FSR 1 brings it to 5.11 ms at 0.67, but only by giving up resolution.

### 3. The swarms are the bottleneck again

The game's identity includes very large swarms; the docs mention up to 1000 creeps. Characters are drawn as rigid voxel
parts, each traced inside a rasterised box, about 19 parts per humanoid. Measured on the 5700 XT at 1440p with 862
goblins in view:

| Seen from | Models pass | GPU a frame |
|---|---|---|
| 40 m, 18 m up | 1.53 ms | 12.5 ms |
| 4 m, 3 m up | 8.14 ms | 20.6 ms |

These figures already include the far-character flipbooks and the split into hit and light passes. Lighting was about
three quarters of the models pass before that split.

Close up, a crowd covers much of the screen, and every covered pixel pays for a ray march plus lighting. A conventional
renderer draws thousands of instanced, animated meshes for a fraction of this cost, and its cost depends on triangle
count, which level of detail controls, rather than on screen coverage times ray steps.

This is the same scale problem that drove the project away from Unity, reappearing in a different place.

### 4. The format limits scale, which is the core fantasy

The view distance is 256 m on a 1000 m map. Fog reaches full strength at the coarsest radius, and the enemy fortress
"comes out of the haze within about 150 to 250 m". A far-field silhouette for the fortress is already listed as an open
problem.

Titans are capped at 10 m. Their parts are held at level 2 (4 cm voxels), and a body larger than one chunk at 4 cm
gets no far flipbook and is always fully posed.

For a game about giants, seeing a 30 to 60 m Titan lumber towards the base from across the map is half the point. A
raster pipeline with mesh levels of detail and impostors can draw silhouettes to the map edge cheaply.

### 5. Every standard technique has had to be reinvented, and every new feature will need it too

A conventional raster pipeline gets each of the following from well-known, well-documented methods. Here each became a
research note and several rounds of measurement:

- **Shadows.** Shadow rays at one coarser level, a 2-degree sun disc sampled by an R2 sequence seeded from world
  cells, a checkerboard of traced blocks, frame blending, and a separate Gaussian smoothing pass. The first version
  crawled and showed lattice lines.
- **Normals.** A 9x9x9, then 7x7x7, box over the voxels with surface groups. This leaves faint rings on gentle slopes.
- **Ambient occlusion.** Derived from the normal box's count. It reaches only 2 voxels from a corner, so occlusion at
  larger scales, such as under the canopy, is still open.
- **Anti-aliasing.** SMAA 1x only. TAA and FSR 2 were judged too complex because they need jittered rays, motion
  vectors, and history. MSAA does not apply to one ray per pixel. `docs/anti-aliasing.md` notes that no single-frame
  method fixes flicker of features thinner than a pixel, which are everywhere: leaves, grass, branches.
- **Texture filtering.** The surface recipes shimmered and speckled at distance and had to be band-limited by hand.
  Mipmapped textures solve this automatically in a raster pipeline. An open issue remains: sparkle on the ground 3 to
  6 m ahead while moving.
- **Model shadows on the world.** These went from a shadow map, to blobs, to capsule shadows, all in one day.
- **Depth for rasterised passes.** A storage buffer, then a resolve pass into a depth image, because writing
  `gl_FragDepth` from the raymarch cost 4.6 to 5.4 %.
- **Level-of-detail seams.** A careful treatment of rays that step into coarser levels, which earlier left a moving line
  of wrong shading.

Each of these was done well. The issue is that the tax never ends. Water, transparency, decals, tower effects,
spell effects, lit particles, and larger-scale occlusion will each need the same treatment.

### 6. The current renderer has no answer for being underground

The fortress already shows the limits. Its 28 m tunnel and the hall under the tower are darkened by stored materials
that get darker in three steps (`TunnelDark`, `DarkerTunnel`, `DarkestTunnel`), because, in `ARCHITECTURE.md`'s words,
sky light is not shadowed at that scale. Occlusion reaches only 2 voxels from a corner, and there are no local lights
at all: braziers and lava glow, but light nothing around them.

For a few hand-built tunnels, painting darkness into materials works. For tunnels players dig anywhere, it cannot work,
because the darkness has to follow the edits. Underground play needs three things:

- large-scale sky occlusion, so a cave is dark because it is enclosed;
- many local lights (torches, lava, spells, muzzle flashes), some casting shadows;
- culling of everything the player cannot see.

Adding these to the raymarcher would mean more rays per pixel, on a pass that is already over its budget. In a raster
pipeline, each has a standard, cheap answer (section H).

### 7. Most of these are inherent to the approach

The following follow directly from casting one ray per pixel through a 1 cm sparse tree, and will not go away with more
work:

- per-pixel cost bound by memory latency;
- no hardware texture filtering of procedural surfaces, so no mipmaps;
- no MSAA, and no cheap way to anti-alias geometry thinner than a pixel;
- custom shadows, occlusion, and normals;
- a short view distance;
- cost that scales with screen coverage instead of geometric complexity.

Lucy's concern was that her doubts might come from a hard patch and not from the design. The evidence points to the
design.

## What destruction actually needs

The destruction the gameplay needs happens at roughly 10 cm to 1 m:

- Titans smashing towers and walls;
- explosions leaving craters;
- players digging trenches, chokepoints, and cover;
- trees coming down;
- characters losing limbs;
- later, perhaps, tunnels, shafts, and mined-out chambers, and digging out ore veins.

None of this needs a 1 cm terrain grid. Tunnels sized for characters 1.2 to 2.5 m tall, and Titans up to tens of
metres, are well served by 5 to 10 cm voxels. Mining individual ore deposits might argue for the finer end of that
range near the player. That is an open question, not a reason to keep 1 cm.

As far as is known, Teardown, the best-known raymarched voxel destruction game, uses voxels of about 10 cm on fairly
small maps and relies on TAA. Deep Rock Galactic has fully destructible terrain, large swarms, co-op first-person play,
and a minimalist, low-poly, dark look with very high readability, all rendered as meshes. That is close to the target
described here. Verify both before citing them in any decision.

The art direction Lucy wants (clean, clear, minimalist, dark fantasy) comes mainly from lighting, palette, value
contrast, fog, and silhouette. It does not come from geometric resolution. The 1 cm detail actually works against
clarity, because it is what produces the shimmer and speckle.

The one place where fine resolution does pay off in gameplay is characters: wounds, cuts, and severed limbs at close
range. Characters are small volumes, so they can keep fine resolution without the world doing so (see below).

## Recommendations

### A. Separate simulation from rendering

The voxel volume stays the source of truth for edits, physics, navigation, harvest and build, saves, and the network
edit log. Rendering reads meshes generated from it. This keeps everything the voxel approach is good at (arbitrary
deformation, deterministic replay, simple edit semantics) and gives up only the per-pixel ray march.

*Reasoning:* destructibility is a property of the data model, not of the renderer. Most destructible-terrain games
render meshes. The edit log, deterministic generation, and host-authoritative networking do not care how the world is
drawn.

### B. Lower the world resolution, and store density

Recommended: 5 to 10 cm voxels near the player, with coarser levels for distance. Store an 8-bit density or signed
distance value per voxel alongside material, instead of binary occupancy.

*Reasoning:*

- At 10 cm a volume holds 1000 times fewer voxels than at 1 cm; at 5 cm, 125 times fewer. This shrinks memory,
  streaming, encoding, and edit replay in proportion. `docs/edit-log-compaction.md` found that replay cost is driven by
  voxels per edit, which this cuts directly.
- Density lets the mesher place vertices between grid points. Smooth surfaces, such as a round crater, a dome, or a
  rolling hill, come out smooth at 10 cm, where binary occupancy would need 1 cm to look smooth. Normals come free from
  the density gradient, which replaces the smooth-normal box entirely.
- Physics and the character mover get far fewer voxel colliders, and the navigation grid is cheaper to build and
  rescan.

The existing sparse structure (64-tree nodes over bricks, palettes, homogeneous collapse, pools in native memory) can
carry over, re-parameterised for coarser voxels and an added density channel. The concepts in
`docs/voxel-structures.md` stay valid.

Thin features (twigs, rope, fine leaves, grass blades) cannot exist at 10 cm. They belong in props or meshes, not
terrain voxels (see C and F).

For underground play, three more requirements apply:

- **The volume must stay fully 3D.** Caves, overhangs, and tunnels under tunnels rule out a heightmap or a 2.5D
  representation anywhere in the world data, even for distant levels of detail.
- **Underground materials must be stored, not drawn.** Today the rock strata are drawn by the surface recipes and cost
  no memory. Once mining yields materials (stone, ores, perhaps gems), what lies underground has to be real data the
  CPU can read: ore veins, strata, and cave contents become stored materials, written by the generator. The palette
  bricks and homogeneous collapse already make this cheap where rock is uniform.
- **The world's floor may need to drop.** It is -40.96 m today, which leaves about 37 to 52 m below the ground. Deeper
  mining would need a lower floor. Empty or uniform layers cost almost nothing in the sparse structure, so this is
  mainly a question of generation and design.

### C. Trees, props, and towers as their own objects

Recommended: trees, rocks, ruins, and player-built towers are separate entities. Each has a procedurally generated mesh,
optionally backed by its own small voxel volume at whatever resolution suits it. They are not cells of the terrain grid.

*Reasoning:*

- Trees 14 to 30 m tall, stored as 1 cm terrain voxels with bark and canopy, are one of the largest memory costs today.
  `ARCHITECTURE.md` notes that storing grass and bark otherwise would roughly halve column memory.
- Separate objects can be instanced and given levels of detail and impostors to the map edge. They can also break apart
  into pieces that become Bepu bodies, which the loose-body system already supports.
- Towers that Titans smash fit the same pattern: an object with its own volume, meshed, shedding pieces as it is
  damaged. This unifies towers, props, and characters under one "small volume, meshed" system.

### D. Meshing: Surface Nets first

Recommended default: Surface Nets over density, per chunk, on worker threads. Remesh only dirty chunks after edits.

*Reasoning:*

- Surface Nets is simple and fast. It shares vertices, produces smooth, well-shaped triangles, and suits a stylised look.
- Dual Contouring keeps sharp features, which may matter for masonry, the fortress, and built structures. It needs
  Hermite data (normals at crossings) and is fussier.
- Marching Cubes with Transvoxel has the best-documented solution for seams between levels of detail.

These choices can coexist: Surface Nets for terrain, with sharp-edged structures as meshes from objects (C).

Seams between chunks of different levels of detail need one of three treatments: Transvoxel transition cells, skirts,
or overlapping geometry hidden by fog. Skirts are the simplest to start with.

Chunk meshing at roughly 32³ cells typically takes well under a few milliseconds per chunk on a worker. Large edits will
show a frame or two of latency before their new mesh appears. The existing deferred application of large edits over
fixed steps already absorbs this kind of latency.

Underground play affects how levels of detail behave:

- **Seams.** Skirts are cheap on open terrain but can poke through thin walls between neighbouring tunnels. If
  underground play is likely, Transvoxel-style transition cells are the safer long-term choice for terrain.
- **Coarse levels.** A coarse level can seal a narrow cave mouth or open a hole in a thin cave roof. Coarse levels can
  be built conservatively (open where any finer cell is open, or the reverse), with the choice made per use. Far
  enough away, underground cavities can simply be left out of the far meshes, since nobody on the surface sees them.
- **Detail around the player.** Underground, the player's own surroundings need fine detail in every direction, not
  only on the surface around them. Levels of detail should be chosen by 3D distance from each streaming centre, not by
  horizontal distance.

### E. A conventional raster pipeline

Recommended:

- **Forward+ (clustered) rendering**, with a depth prepass. Clustered lighting handles hundreds of small local lights
  cheaply, which underground play needs (torches, lava, braziers, spells, muzzle flashes, glowing ores), and which the
  surface's braziers and lava falls already want.
- **Shadows:** cascaded shadow maps for the sun, two or three cascades, with stylised soft filtering. A small budget of
  shadow-casting local lights (cube or dual-paraboloid maps for the few nearest or brightest), with the rest
  unshadowed. Local-light shadows matter much more underground, where they are the only shadows there are.
- **Ambient occlusion:** per-vertex occlusion baked at mesh time from the density field, plus optional GTAO for larger
  scales. Per-vertex occlusion from the volume is nearly free and suits stylised voxel-derived meshes well.
- **Anti-aliasing:** MSAA, with alpha-to-coverage for foliage cards, as the clean default. SMAA can stay as a cheap
  option. TAA becomes practical later, because motion vectors and jitter are standard in a raster pipeline.
- **Materials:** triplanar mapping with texture arrays and mipmaps. The procedural surface recipes need not be thrown
  away: bake them into tiling textures at start-up, keeping the code-made ethos. Mipmaps and anisotropic filtering
  then remove the shimmer the band-limiting fought. The corruption tint map stays a lookup.
- **Atmosphere:** fog, haze, tone curve, and grade port almost directly.

*Reasoning:* every one of these is a known method with predictable cost and published fixes for its artefacts. They
scale down to weak hardware through resolution, cascade count, and level-of-detail distances, which is exactly where
the raymarcher scales worst.

### F. Characters: keep the rigid-part design, draw parts as meshes

Recommended:

- Keep the per-part voxel volumes that wounds, cuts, and severing operate on, at their current fine resolution if
  wanted. A part is small, so this is cheap.
- Mesh each part once per body kind and cache the result.
- Remesh only a part that has been wounded.
- Draw parts as rigid instanced meshes.
- Mesh the existing far flipbook frames (17 baked whole bodies per kind at 4 cm) as well, and draw far creeps as
  instanced meshes.

*Reasoning:* nearly all the design of `docs/voxel-characters.md` and `docs/health-system.md` survives. That includes
part volumes, cuts that sever, ragdolls of cut parts, blood, and organs. Only the ray march inside each part's box goes
away. Rasterised instanced meshes make a thousand near creeps affordable. Titans are no longer capped by what one
traced box can hold, so 30 to 60 m giants become possible, with parts at coarser voxel sizes.

### G. View distance to the map edge

Recommended: coarse terrain mesh levels of detail across the whole map, tree and prop impostors past a few hundred
metres, and silhouettes for Titans and the enemy fortress always drawn.

*Reasoning:* scale is the core fantasy. A raster pipeline makes far geometry cheap, and the fortress's missing
far-field silhouette stops being a special case.

### H. Underground digging, mining, and exploration: what to design for now

Underground play is not committed, but several of its needs are cheap to allow for now and expensive to retrofit. The
recommendation is to build the hooks during the redesign, even where the feature waits.

**Sky visibility (large-scale occlusion).** Keep a coarse 3D field, for example 50 cm to 1 m cells, holding how much
sky each cell can see. Fill it by propagating light down and sideways from open sky through air, in the style of
Minecraft's skylight, and recompute it locally on worker threads after edits.

Surfaces sample it, per vertex or per pixel, to scale sky and ambient light. A dug tunnel then darkens because it is
enclosed, with no special materials. The fortress's tunnel materials (`TunnelDark` and the rest) could become plain
stone again.

The same field gives a cheap answer to "how far underground is this point". That can drive atmosphere (underground
fog, darkness, the colour grade) and audio: procedural reverb for enclosed spaces fits naturally with the code-made
sound in `docs/procedural-audio.md`. It also helps on the surface, where large-scale occlusion under the canopy is
already an open problem.

**Local lights.** Clustered forward lighting (E) with a light budget, and light sources as ECS components, so that
torches, lava, glowing ores, and weapons all emit through one path. Darkness and pools of light suit dark fantasy, so
this is an art direction asset as much as a cost.

**Visibility culling.** Underground, most of the world is hidden behind rock, and from a cave the surface is invisible.
Frustum culling alone will draw far too much. Two standard options:

- **Connectivity-based culling.** Record, per chunk, which faces connect through air, and flood-fill from the camera's
  chunk, in the style of Minecraft's cave culling. It is cheap, works well for caves, and is updated when a chunk is
  remeshed.
- **GPU occlusion culling.** A hierarchical depth buffer and indirect draws. It is general, and it also helps in dense
  forest.

Either should be planned into the chunk mesh streaming from the start.

**Navigation.** `NavGrid` is one layer of 2,000 x 2,000 cells of 50 cm, with one floor per cell. A tunnel under walkable
ground cannot be represented. If creeps ever go underground (to dig towards the base, or to defend mines), navigation
needs several floors per cell, or sparse 3D cells below the surface joined to the surface grid. Even if only players go
underground, the grid must at least ignore tunnels rather than misread them. Decide whether creeps will go underground
before rebuilding navigation on the new world data.

**Edit volume.** Mining produces far more edits than surface fighting: a player tunnelling for an hour could match or
exceed the 50,000-edit hour measured in `docs/edit-log-compaction.md`. The world's bulk of uniform rock helps. Coarser
voxels also make each edit's replay much cheaper.

But long logs make that doc's method 2 (keep edited columns resident) and method 3 (snapshots of edited chunks, which
lets old edits leave memory, saves, and join payloads) much more likely to be needed. Keep the storage and save format
able to hold per-chunk snapshots alongside the edit log, even if snapshots are built later.

**Streaming.** Streaming centres and level choices should work in 3D (D). A player deep underground needs fine detail
around them in every direction, and only coarse surface detail above, if any is visible at all.

**Physics and the mover.** Tunnels mean tight spaces, ceilings, and steep or vertical shafts. Check that the character
mover handles low headroom, crouching, and climbing if those come. Plan colliders around tunnels, since Bepu bodies
dropping into shafts are common there. If cave-ins or unsupported rock falling are ever wanted, the storage should
support cheap connectivity queries ("is this piece of rock still attached?"). Detached pieces would then become loose
bodies, which the lift and loose-body system already does.

**Generation.** Caves, shafts, ore veins, underground strata, and perhaps buried structures, all as stored materials
(B). The generator rework already planned is the natural time to give it a 3D cave and ore stage, even if only a
placeholder.

## What depends on the raymarcher

These are the systems the docs show using the GPU voxel mirror or the ray traversal, each of which needs a replacement
or a decision:

| Dependent | What it uses today | Possible replacement |
|---|---|---|
| Camera rays and shading | `voxel-trace.comp`, `voxels.frag`, hit buffer | Raster passes |
| Smooth normals and occlusion | `voxel-normals.comp` | Density gradient and baked vertex occlusion |
| World shadows | `voxel-shadow.comp`, smoothing pass | Shadow maps |
| Model shading | A world sun ray traced per model pixel | Shadow maps |
| Model shadows on the world | Capsule shadows | Shadow maps, so capsules may become unnecessary |
| GPU particles | `traceVoxelsFrom` to stop at the first solid voxel | Depth-buffer (screen-space) collision for cosmetic particles, or a slim coarse occupancy texture of the near region |
| Depth for rasterised passes | Storage buffer and resolve pass | Native depth buffer |
| Debug views | Steps per ray, level per ray | Wireframe, level-of-detail tint, chunk bounds |
| Correctness check | Centre-ray check against `VoxelWorld.Raycast` | Replaced or dropped |
| Aim dot | Drawn in `voxels.frag` | Moves to the HUD (already planned) |
| GPU memory | Column windows, heap ranges, edit reserve, upload path | Mesh buffers |
| Tests | `GpuRayTracer` and its tests | Retired with the raymarcher |

Separately, these CPU systems read the voxel world and are affected only if the resolution changes (B):

- the character mover (`VoxelCharacterMover`, against level 0);
- Bepu voxel colliders (`VoxelColliders`, levels 0 and 2);
- lift and put-back of loose bodies;
- navigation grid scans;
- harvest counts and build-mode cubes;
- world generation;
- the edit-log replay.

Underground play would also touch these:

- `NavGrid`, which has one floor per cell (H);
- the rock strata, which are drawn by the surface recipes and not stored (B);
- the fortress tunnels' darkness, which is painted into materials (H);
- edit-log compaction (H).

## What carries over unchanged, or nearly

- Voxel storage concepts and native memory pools (re-parameterised).
- The seed plus edit log, the join payload, and saves.
- Deterministic generation. The generator's shapes were going to be iterated anyway.
- Host-authoritative networking, sessions, and Steam.
- The Arch ECS and system structure.
- Flow-field pathfinding (the grid is rebuilt from coarser voxels).
- Bepu loose bodies and ragdolls.
- Health, wounds, blood, progression, armour, inventory, weapons, and strikes.
- Procedural audio in its entirety.
- Skia UI, the main menu and run structure, dynamic render scale, and FSR 1 (still useful as an option).
- Particle simulation and recipes (only world collision changes).

## Risks and costs of the new approach

Meshing has its own problems. Plan for them rather than discovering them:

- **Level-of-detail seams and popping.** Needs transition cells, skirts, or fog, plus some blending or dithering on
  transitions.
- **Remesh latency.** Large edits, such as the 4 m spheres measured today, will spread their remeshing over frames. The
  existing deferred-edit machinery helps.
- **Thin features.** These disappear below the voxel size and must move to props or meshes.
- **Smooth versus sharp edges.** Surface Nets rounds corners. Structures that need crisp edges need Dual Contouring or
  object meshes.
- **Mesh memory.** Vertex buffers per chunk per level of detail must be budgeted against what the voxel GPU heap uses
  today (768 MB default).
- **Foliage overdraw.** Alpha-tested canopy cards can cost fill rate. Alpha-to-coverage and levels of detail manage it.
- **Effort.** The renderer, the GPU format, and parts of physics and generation are a substantial rewrite. The project's
  pace so far (most of the engine built since 2026-09-22) suggests it is affordable, but scope it in phases with
  measurements at each step.
- **Underground light leaks.** Shadow maps and coarse levels of detail can let sunlight or sky light through thin cave
  roofs and walls. The sky-visibility field (H) is the main defence. Shadow-map bias and near-cascade resolution need
  tuning with caves in view.
- **Culling.** Connectivity culling must be kept correct under edits, or chunks will vanish or be drawn when they
  should not.
- **Navigation.** Navigation that ignores underground spaces now may need a larger rework later if creeps go
  underground. That is why it is an early decision (H).
- **Second-system effect.** The new design should be simpler than the old one. If the plan starts accumulating custom
  techniques, stop and reconsider.

## A suggested order of work

### Phase 0: preparation

- Shelve the raymarcher on a branch.
- Inventory every reader of the GPU voxel mirror and the traversal in code, completing the table above.

### Phase 1: a throwaway spike to test the conclusion

The coarse levels already exist: level 3 is 8 cm and level 4 is 16 cm.

1. Run naive Surface Nets over level-3 columns of the current world, on binary occupancy with light smoothing. It will
   look a little lumpier than density-based meshing would.
2. Rasterise the result with one directional shadow map and 4x MSAA.
3. Use the existing benchmark's seven views and camera paths.
4. Add a stress test of about 1,000 instanced goblin meshes, made by meshing their part volumes once.
5. Add an underground view: dig a long tunnel and a chamber with the existing dig tool, or use the fortress's tunnel
   and hall, and place a handful of point lights. Check the meshing of the tunnel at the coarse level, and how much
   sunlight leaks in. A crude sky-visibility field, even a slow one, will show whether it gives convincing darkness.
6. Compare GPU time on the RX 5700 XT at 2560x1440, and on the weakest hardware available.
7. Compare the look side by side.

Decide with Lucy beforehand what results would confirm or kill the direction. One possible bar: the world pass well
under 6 ms at render scale 1, and 1,000 visible creeps near the camera without the frame doubling. The spike should
take days, not weeks.

### Phase 2: decisions

Take the open questions below to Lucy, each with options and measured trade-offs from the spike.

### Phase 3: the renderer core

Build the meshing jobs, chunk mesh streaming and levels of detail, raster passes, shadow maps, anti-aliasing,
materials, and atmosphere. Run it against the existing world data first, before any resolution change.

Build clustered lighting and the culling path in from the start, even if few local lights exist yet. Both are much
harder to add after the pipeline has settled.

### Phase 4: characters, loose bodies, and particles

Move characters and loose bodies to meshes, add far instancing, and give particles their new world collision.

### Phase 5: world resolution and density

If chosen, change the world data: storage parameters, the density channel, the generator, the mover, colliders,
navigation, harvest and build, with the pinned world hashes and tests rebased.

At the same time, add the underground data hooks: stored strata and ores, the world's floor if it moves, and room in
the save format for per-chunk snapshots.

### Phase 6: separate objects and the far field

Move trees, props, and towers to separate objects, and add impostors and the far field to the map edge.

### Phase 7: underground lighting and space

Add the sky-visibility field and its use in lighting, atmosphere, and audio, and retire the fortress's painted tunnel
darkness. Build out connectivity or occlusion culling for caves. Rebuild navigation in layers if creeps go
underground. Add the generator's cave and ore stage.

Parts of this, such as sky visibility, also fix surface problems like canopy occlusion, and could come earlier.

Phases 3 and 5 can swap, or overlap. Doing the renderer first on existing data keeps the game playable throughout and
isolates the two changes. Phase 7 can follow whenever underground play is taken up, provided the hooks from phases 3
and 5 are in place.

## Open questions for Lucy

1. **World voxel size:** 5 cm, 10 cm, or something else? Should near and far differ, beyond the level-of-detail levels?
2. **Density:** density or signed distance per voxel, or occupancy only?
3. **Meshing:** Surface Nets, Dual Contouring, or Marching Cubes with Transvoxel? How should seams between levels of
   detail be handled?
4. **Trees and props:** which objects leave the terrain grid? How do they break: pre-fractured pieces, their own small
   volumes, or both?
5. **Anti-aliasing:** MSAA, SMAA, TAA, or a combination?
6. **Shadows:** how many cascades, and how soft?
7. **Materials:** baked procedural textures, authored textures, or flat colours with value and edge shading? The last
   is very clean and very cheap, and may suit the minimalist target best.
8. **Character resolution:** do part volumes stay at 1 cm, or coarsen?
9. **Titan size:** what is the new upper limit?
10. **Hardware target:** which machines define "a range of hardware"? An integrated GPU or handheld at 720p or 800p
    and 60 fps is a common bar to aim for.
11. **View distance:** is the goal "to the map edge"?
12. **The spike:** what result would confirm the redesign, and what would stop it?
13. **Underground depth:** how deep should the world go? Is the current floor of -40.96 m enough?
14. **Creeps underground:** will creeps ever path underground, by digging towards the base or defending mines? This
    decides whether navigation must be layered.
15. **Mining yields:** what does mining yield (stone, ores, gems, buried structures), and must it be exact per voxel or
    can it be approximate?
16. **Cave-ins:** is unsupported rock falling wanted, now or ever?
17. **Darkness:** how dark should underground be, and how much should it depend on carried and placed lights? How many
    local lights may cast shadows?
18. **Sky visibility and canopy:** should the sky-visibility field also replace the canopy-occlusion work on the surface?
