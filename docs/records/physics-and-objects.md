# Physics and voxel objects: measurements and history

Passages taken out of `.claude/rules/physics.md` and `.claude/rules/objects.md` when the rules files were cleaned and split
(2026-10-19). Each is verbatim from the old file, with the rules file that holds the rule now. The measurements carry no
dates of their own; they were made while the physics and the objects were built, and are kept as they were written.

## Physics

### The physics warm-up (now `physics-world.md`)

"Bepu makes its solver's and collision code for each joint, pair of shapes, and kind of contact at run time, which
ReadyToRun cannot compile ahead, so without it the first ragdoll of a run took a step of 44 ms (composite ReadyToRun) or
66 ms (Debug) compiling them; with it, 4.9 ms at most."

The warm-up's contents, as written: "a world of its own with the match's settings and Bepu callbacks, on a thread of its
own, into which two people's ragdolls, a pile of convex hulls, a kinematic mirror, a cut part, a stand-in, and a trigger
zone fall onto a voxel cell's static of boxes and a mesh ramp, 4 s of steps, then all taken away and freed."

### The finer mover (now `physics-mover.md`)

"On smooth hills of 1 m voxels a 10 cm mover's feet keep within about 2 cm of the 1 m mover's (`FineMoverTests`), but a
ledge under a voxel high is a step no higher than the body's, where 1 m cells walk a column up to a cell over the feet
(their allowance is a cell); a walking move costs about 2.4 times as much (41 against 17 microseconds in Release)."

"On voxels coarser than 1 cm almost all of a move is reading the ground's surface under the footprint (`Surface`: a window
of columns read and their blend), asked about seven times a move (lifting it out, grounded and steep before and after,
the vertical part), a third or more of those the same question again: within one move or query each question is worked
out once and remembered (16 at most, forgotten with the surfaces' cache at the next move), the same bits (`FineMoverTests`
against a mover that works every answer out again, `RemembersSurfaces`); a body standing still on 10 cm cells over 1 m
voxels then takes about 15 microseconds a move against 25."

"A move over triangles alone costs more than one over voxels, more so in small cells (columns are read one by one, voxels
a brick's mask at a time; the figures are in ARCHITECTURE.md)."

"(30 cm voxels at 50 degrees, for a person)": the example of a slope climbed a tread at a time on cells wider than the
body's radius.

"making one allocates until the cache is full", for mesh column chunks, and: "past that, the chunk read longest ago is
dropped and its memory used for the new one, so bodies far apart that need more at once make chunks again at every read".
The limit is 256 chunks (`MostChunks`).

### Physics materials and bounces (now `physics-contacts.md`)

"Splitting costs: over a block of a random material in each voxel, cells of three physics materials built in about twice
the time of one, with 1.85 times the boxes."

"The bounce does not depend on the substeps; a ball then rises a step's rise short of the curve (at 60 Hz, about 3 cm from
a 1 m drop at bounciness 0.8), less what damping takes. A ball rolling over voxels hops up to about 2 cm at a cell's
border at 4 m/s (Bepu's contact with the next cell's edge, whatever its material); a bouncy one bounces where such a hop
lands at 0.5 m/s or more."

Buffer sizes: bounce pairs "4,096 pairs, shared by the workers, at least 1,024 each"; impact events "8,192 a step";
trigger pairs "4,096 pairs shared by the workers, at least 1,024 each"; at most 64 physics materials, 8 voxel materials
among them with the default, 32 layers, 256 trigger zones. A trigger example: "a 20 cm ball at 30 m/s through a zone 20 cm
deep" crosses unseen. Option ranges given: `Substeps` 1 to 8, `VelocityIterations` 8 (1 to 32), damping 0.03 (0 to 1),
gravity 0, -9.81, 0 (each -100 to 100), friction 0 to 4, bounciness 0 to 1. These are in the code and in
`docs/reference/engine-options.md`.

### The hit history (now `physics-rays.md`)

"(without it, a shot from the eye at a character 20 m off passes about 0.4 m beside the one the crosshair covers)": the
reason `ThirdPersonCamera.Aim` takes a tick and the aimer to leave out. The history keeps 32 steps of up to 32 capsules for
up to 1,024 entities (`HitHistory.Steps`, `MostCapsules`, `MostEntities`).

### Loose bodies (now `physics.md`)

Limits as written: "At most 384 loose bodies exist (`Engine:Physics:MaxLooseBodies`)"; the balls settle "after 3 s at rest
(`Engine:Physics:SettleSeconds`...)"; `Engine:Physics:Threads` "(0: a quarter of the processors, 1 to 6...)"; "Cut bodies go
30 s after they rest (also when frozen) and at the latest 60 s after they were made"; "A piece of more than 4,096 box
corners is hulled from its farthest points in 42 directions"; the voxel cells for coarse bodies are "of 32 voxels".
The mover's widest body "is 2 m in radius (`MaxRadiusMetres`)"; `TerrainCellCentimetres` "0, the default, for the
voxels'"; `CollisionCentimetres` "(10 by default)".

## Voxel objects

### Kinds and the columns' runs (now `objects.md`)

"measured with a game's 65 kinds, the largest 30 by 21 by 30 m of 10 cm voxels: 22 ms in Release and 96 ms in Debug, where
each reader made its own table of a kind at its first read (that largest one in 186 ms in Debug, a stall during play)."

"(`Register`, `TryFind`, at most 4,096; `VoxelObjectKindId`)": the kinds limit. "A kind's parts own at most 112 voxels a
side (`ObjectPart.OwnVoxels`; a kind 5.6 m wide at 5 cm is one part across) and hold a border of their neighbours' 8
voxels round them (`BorderVoxels`; 4 at level 1, 2 at level 2: the two past an edge the mesher reads for a vertex's normal
and openness), so a part's model box is its own plus 16".

"(an area is 512 m wide on 1 m voxels, where reading every object made a game's sight lines slow among thousands of placed
blocks: 16,000 blocks took a step's slowest from 8 to 31 ms)": why `Near` reads the area's cells of `IndexCellCentimetres`
(4 m). `Engine:Objects:HandOverLevel` is "2 by default, 1 to 9"; `FillsPerUpdate` is 8; `ObjectPlacement.HeadingSteps` is
4,096.

### Placing during play (now `objects.md`)

"Objects placed and removed during play (since save format 9, protocol 45, and `ObjectEditing.RuleVersion` 2)": the
current protocol is `SessionProtocol.Version`, and the save format and rule version are in the code.

"(an area of 2,000 placed blocks, 101 of them dug into by 200 digs, fills in about 100 ms in Release)": the fill reading
its area's edits once for all its objects.

### Damage and cuts (now `objects-damage.md`)

"(a crater of 1.5 m across a gate of 10 cm voxels took 8 ms a pass in Release counting the voxels round the sphere one
point at a time, before both)": the passing over of unchangeable voxels and of voxels missing a sphere.

"(a part of 128 by 128 by 58 voxels takes 3 to 4 ms in Release; the buffers it is made in, 8 MB, are made at `Initialize`
where the run has kinds)": `PartsPerUpdate` is 1, `RebuildsPerUpdate` 2, `AsksPerUpdate` 4, `Engine:Objects:MostDamaged` 256,
`FallenParts.FewestVoxels` 27, the read-changes ring 256, a copy 1 KB for each changed brick, fallen bodies 800 kg a
cubic metre (`FallenParts.DensityKilogramsPerCubicMetre`).

### Far copies (now `objects-copies.md`)

Thresholds as written: a thin part is kept when its share is "at least a 64th, `ThinShare`", made solid "at `KeptShare`,
160"; an air voxel turns solid "from half (128)"; `LevelFadeMetres` "(2 m)"; `HandOverFadeMetres` "(30 m...)".

### Bodies meeting objects (now `objects-meeting.md`)

"all made at the match's "objects" step, `VoxelColliders.MakeObjectShapes`, since one made at a body's first approach took
a step of 89 ms for a kind of 55,676 boxes".

Rays: "the ray is walked in stretches of 16 m... at most 10 km". Chunks of the mover's object columns: "at most 256 chunks".
The statics' boxes are whole: "at 5 cm, up to 2.5 cm off the drawn surface".
