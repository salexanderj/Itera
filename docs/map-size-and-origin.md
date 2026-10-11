# Large maps: decisions and their reasons

`docs/framework-limits.md` listed "a voxel world in a fixed box" as limit 2: a map of ±512 m and -51.2 to 102.4 m,
voxels of at most 10 cm, positions in floats from the map's middle, a view of at most 2 km, and a navigation grid over
the whole map. On 2026-10-10 the engine gained, in ten phases: a map's size in its generator's grid (74349ba), voxels
from 1 cm to 1 m (0dcccee), absolute types wherever a position crosses the origin (18cb39c), moves of the origin on the
CPU (993b184) and in the renderer, particles, and sound (e0958e3), the origin following the camera with positions in
messages (28f9cd4), the starter's 8 km map (cd9022c), views to 8 km (61b3201), a navigation grid that moves with the
players (bac0d6b, d2602f8), and columns that store only the layers round their ground (d51559b, d41960e, bf5d7b9,
cf2ce3f, fa54e12). How each works is in `.claude/rules/` (voxels, physics, rendering, navigation, sessions, engine) and
ARCHITECTURE.md; this file records what was chosen, what else was offered, and why.

## The map's size

A game sets its map's size in its generator's grid (`VoxelGrid.Sized(base, halfWidthMetres, floorMetres, topMetres)`),
rounded out to whole level-0 chunks, up to ±160 km on every axis.

- In the grid, not in an option. Offered: an engine option; the generator's grid. Chosen: the grid. Peers and saves
  already agree on a world through its generator's id and version, so a size change raises the version and the wire and
  save formats stay as they were. An option would have needed a new field in the session's welcome and in saves.
- ±160 km. The edit format holds origins within 2^24 cm (167.8 km); 160 km leaves room for the play area's 1 km
  margin. Larger maps would need a new edit format.
- Up and down too. A column used to hold every layer of its world's height, so height was capped at 128 layers (1.6 km
  at 10 cm). Sparse columns (below) lifted that.

## Voxels from 1 cm to 1 m

Level-0 voxels may be any whole number of centimetres from 1 to 100. Asked whether a game whose ground is a heightmap
with craters needs its own system, the answer was that the voxel terrain already makes craters with sphere clears;
coarse voxels make it cheap. The cost of a coarse world: a shape under a voxel shows only as a soft dip in the
densities, walls and caves come in voxel steps, and a piece lifted from the ground keeps the voxels' steps. Options in
metres that suit one voxel size (the streaming radius, level 0's reach) take their defaults from the grid when 0.

## A floating origin

Each peer keeps its own origin (`WorldOrigin`, whole metres) near its camera, and every float position it keeps (the
ECS's, Bepu's, the renderer's, the particles', the sound's) is measured from it. Voxels, edits, saves, and what peers
send stay absolute.

- Floats near the camera, doubles where positions cross. Offered: doubles everywhere; a floating origin. Chosen: the
  origin. Bepu, the GPU, and `System.Numerics` work in floats; doubles everywhere would have meant replacing them. A
  local float becomes absolute only through `WorldOrigin`, in doubles (`floor((local + origin) * scale)`), so a move or
  ray 150 km out reads the cells one at the middle does, bit for bit (`FarFromTheMiddleTests`).
- Whole metres. A whole-metre move is exact in a float's sum for every position under 2^24 m, so nothing drifts at a
  move. A walk with the origin following every 8 m matched one where it stayed within 1 mm; the one that stayed was the
  less exact (its feet rounded 30 to 40 m from its origin).
- 512 m (`Engine:Origin:ShiftMetres`, 8 to 4096). A float under 512 m steps at most 61 micrometres; a move costs 0.6 ms
  with 384 balls in Debug, so moving more often would cost more for no gain the eye sees.
- Only at a step's start. A move runs in the first fixed system (`OriginShiftSystem`, which throws anywhere else) or
  between steps, so every system of a step sees one origin. A `tp` far away asks for the move and the system applies
  it at the next step's start, before the jump: moving at once in the command let the next step, whose camera was still
  where it was, move the origin straight back. The jump also moves the active camera, so a second step before the next
  frame does not follow the old camera back.
- The renderer takes the origin back where things are drawn by place. Noise, strata, and the tint map read whole
  centimetres plus the origin in integers; a texture set's tile takes the origin's remainder, worked out on the CPU per
  material (the shader's own integer remainder was exact only for whole-centimetre tiles and slower). Shadow texels
  snap in absolute terms, so a move changes no texel. Pictures before and after a forced move differed in 7 to 15 pixels
  on silhouettes.
- Positions in messages are absolute: three int32 in 1/64 cm (`MessageWriter.WritePosition`), 0.16 mm to ±335 km,
  past the largest map. A host steps every body in its own origin, so bodies near a client 100 km from the host's
  camera step in 8 mm floats; separate simulations for far peers are left for a game that needs them.
- Mods are not told of moves: the mod contract has no positions to move.

## The starter's 8 km map

The starter's grid is ±4 km, its floor and top as before. Past 1 km from the middle one octave of noise 2 km across
raises or lowers the ground by up to 30 m, faded in to whole at 1.5 km, so within 1 km the world is version 1's voxel
for voxel and its pictures are unchanged. Gravel lies where the hills alone are under 2 m, not the ground with the
relief: measured against the ground, the relief's broad hollows would turn to gravel. (Lucy has not yet seen this
choice.)

## Views to 8 km

`Engine:Renderer:ViewDistanceMetres` goes to 8000 (it stopped at 2000 for no recorded reason; the coarsest streamed
level's radius ends a view anyway). Two faults showed only past 7 levels. The level balance marked the level over every
finest column under each drawn column, a quarter of a million entries for one level-9 column (4.2 s a frame); it now
keeps the drawn columns and finds the one over a cell level by level. And a coarse column split only when all four
children were meshed, so a child past the view's end or the map's edge kept it whole: a 2 km view at 10 levels drew the
start from 51.2 m voxels. Such children now draw nothing and let it split. On the GPU an 8 km view at nine levels
costs the starter's start view 1.85 ms of world pass against 1.74 at 256 m and four levels, and the view from 40 m up
3.00 against 2.37 (more ground drawn where there was sky; ARCHITECTURE.md has the table for 7 to 10 levels and 2 to 8
km). The whole item costs 0.4 to 1.0 % of the world pass at the engine's default view, from the shaders' origin and
texture tile work: within the 1 % it was held to. Lucy chose, from pictures of the 3.5 km and 40 m views, the starter's
map seen to its end (an 8 km view over nine levels) with half the haze it had (half of a surface lost at 3 km, not 1.5
km); the engine's defaults stay 256 m and four levels.

The far shadows end at 200 m. Lucy asked what a third cascade past it would give the average player against its cost;
the answer, from the engine's sun 58 degrees up, was almost nothing (a high sun's shadows are short, and flat ground far
off is seen nearly edge-on), and more under a low sun. She chose it as an engine option, off by default
(`Engine:Renderer:DistantShadowMetres`): terrain only, drawn only while the sun is under 30 degrees and faded in down to
25, so a high sun costs nothing. Measured at 10 degrees it doubles the shadow maps' time (0.19 to 0.40 ms) and shows
only shapes taller than about 4 m: on the starter, the hills' back slopes under a sun a few degrees up.

## Navigation that moves with the players

The navigation grid is a window: the whole map when it is at most `Engine:Navigation:WindowCells` across (1024, 512
m), else a square of that many cells that follows a focus (`NavigationService.Follow`, else the first streaming
centre).

- A window, not a grid over the map. A grid over an 8 km map is 256 million cells; a 1024-cell window is about 6 MB a
  grid whatever the map's size.
- The whole map when it fits, so a small map's grid never moves (a choice made for Lucy; she has not seen it).
- Moves by 32 m tiles when the window's corner for the focus lies a quarter of the window away, or differs while the
  focus is within an eighth of the window of its edge. The quarter alone left a walker stuck at the window's edge near
  the map's edge, where keeping the window inside the map left the corner exactly a quarter away. Under 256 cells the
  window, moving by 64-cell tiles, moved only once its focus had left it, so 256 is the least.
- A move fills only the strips coming in and stamps on them the logged edits since the last reset that reach them;
  those are read from the voxels later, as any stamped edit. Edits and rescans pause during a move, so nothing the main
  thread writes is overwritten when it takes the thread's grid. Each field keeps the window it was built over, so a
  walker never reads a field in another window; a goal past the window stands in at its nearest edge cell. A walker
  crossed 2 km of a 4 km map with 11 moves, each building its field in 30 ms in Release.

## Sparse columns

A column stores a window of its layers, the generator's range and the layers its edits reach, with one fill below and
one above (`Column.First`, `Below`, `Above`).

- One window per column, with uniform states round it. Offered: per-layer allocation; one window. Chosen: one window.
  Every layer used to cost 8 bytes of chunk state and about 4 KB of mesh blocks; a column of a 320 km world would have
  cost about 100 MB of mesh blocks. 64 columns of the hills test generator store 111 layers in a world 12 layers tall
  and in one 25,000 layers tall.
- The generator says its range (`IChunkGenerator.DescribeRange`); by default the whole column, so a game's own
  generator works as before and costs as before. The heightfield base finds its range by halving the column: a block of
  layers described whole as one fill is that fill throughout, so it never describes the column layer by layer.
- Mesh blocks only for the runs of layers that can hold a surface (`VoxelWorld.CollectSurfaceLayers`: a layer of more
  than one fill, or one whose fill differs from the layer above it or from the columns past its +x and +z sides, whose
  faces with it its blocks own). A first version kept blocks for the whole stored window; an edit 100 km up then widened a column's window to 7,813 layers and its mesh
  column with it. A ball 60 m up over 1 cm voxels now keeps blocks for 11 of the 45 layers its column stores.
- The level choice counts height: a column's distance is to the box of its blocks, so a camera high over the ground
  meshes no fine level under it. The starter's start, high, and far views draw as they did.
- Rays and collision walk a tall uniform stretch a chunk at a time; a ray 100 km down took 0.6 ms in Debug, so they
  were left as they are.

## Left for later

Separate physics simulations for far peers, a tint map that moves with the view, atmospheres by region, and navigation
windows per group of players are left for a game that needs them.
