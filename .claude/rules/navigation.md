---
paths:
  - "src/Itera.Engine/Navigation/**"
  - "tests/Itera.Engine.Tests/Navigation/**"
---

## Navigation

Navigation (`NavigationService`, host and offline only) keeps a grid of 50 cm cells (`NavGrid.CellCentimetres`) on its
own thread from a game's `INavGridSource` (which fills a rectangle of cells from the world as generated, says the
ground's height at a column, and what blocks it) and builds the flow fields a game asks for (`NavFieldSpec`: goals as
absolute places, a body size (`NavSize`), and a cost table, `NavCostTable`: what each kind of cell costs, and whether a
block is a price to break through or a wall to walk round). The engine knows nothing of teams; a game's fields are its
own. Each fixed step reads new edits into the grid (at most 128 cells read from the level-0 voxels, or stamped from the
edit's shape where they are not loaded, and read once they load; an edit that covers no cell's centre column, or lies
outside the grid, is passed over; a read takes the floor from the surface the voxels' density gives,
`VoxelWorld.SurfaceOver`, within about a centimetre of the source's ground), and the thread builds the fields again
while their users keep the old ones. A read or a stamp judges solid from the source's ground there
(`INavGridSource.GroundAt`); a far removal of some materials stamps a pit only when it removes ground
(`INavGridSource.IsGround`). After `reset` the thread fills the grid again first (`NavigationService.Refilling`). A game
without an `INavGridSource` has no navigation (its loading task is done at once). Floors are absolute centimetres in
`int`s, so any map's heights fit. `NavigationDebugLines` draws a field's arrows round the camera for a game's debug view.

The voxel objects a world places (`.claude/rules/objects.md`) block as solid does. After each fill from the source (the
window at the start, a refill after a reset, a move's strips) the navigation thread stamps the objects as generated onto
the cells (`NavGridScanner.StampObjects`: the placer's objects in the areas the match keeps, `NavObjects`, read in the
mover's cells by `ObjectColumnReader`): each run of an object's blocking voxels in a cell's centre column blocks the cell
where it rises more than `LowCentimetres` over the ground (a trunk), raises the floor where lower (a flat stone), and
lowers the headroom where it hangs over the floor (a branch); passable voxels (leaves) change nothing. A read takes the
objects as they are (damaged too) from the mover's `ObjectColumns` (`NavGridScanner.Objects`, main thread: the
mover's own where its cells are the voxels'; where they are finer, `Engine:Physics:TerrainCellCentimetres`, one of
navigation's own in the same cells, and a cell reads every object's column in its 50 cm square, so a 10 cm wall
anywhere in it blocks it and a 30 cm block raises its floor, `NavigationPlacedObjectsTests`), by
the highest top and the lowest bottom with the voxels, and waits, with the stamped edits, until the objects round the
cell are known (their areas filled, `ObjectsKnown`). A removal of an object's part (`VoxelEditOperation.RemovePart`), and
an object placed or removed whole during play (`PlaceObject`, `RemoveObject`: the generator's placer does not know them,
so they are not stamped at a fill), changes the cells under its box (read once loaded; its stamp changes nothing); a
world whose generator places nothing reads objects wherever the run has kinds (`ObjectWorld.IsEnabled`), and each change of an object's copy
(`ObjectDamage.ReadChanges`: an edit or a cut, a replay, a copy made again after the cap dropped it, a reset's healing)
has the cells under its reach read again where loaded, else once loaded (one lost past the ring is passed over: the edits
that changed it wait for their own reads; those during a refill are read after it). A run of an object's voxels whose
bottom lies within `LowCentimetres` over the ground stands on the floor, in a stamp as in a read. A stamp keeps the headroom's top where an object raised the floor, and a read looks up from the new floor, so the
two differ in headroom there.

A game closes boxes of cells to every walker whatever the world holds there (`NavigationService.Close(minX, minZ, maxX,
maxZ)`, absolute centimetres, the cells whose centres lie in the box; a gate's leaf, a door, a thing it draws and makes
solid itself), moves them (`MoveClosed`), and opens them (`Open`), on the main thread: a closed cell's headroom reads as
none (its floor as it was) at once, through every read of the voxels, stamp, refill, and move of the window (applied to
the main thread's grid after each, and the rows marked for the next job), so a field charges it as a block (a price to
break through or a wall, by its cost table) and paths go round it; the cells a box leaves are queued to be read again
from the voxels (filled from the source without terrain), a box still holding one keeping it closed. A box closed before
the grid is ready closes its cells once it is; a client keeps its boxes and changes nothing. `Closed` counts them; a
move allocates nothing.

The grid is a window (`NavArea`, the map's square of whole cells; `Engine:Navigation:WindowCells`, 1024 by default, 256
to 4096): a map that fits is covered whole and the window never moves; on a larger one it starts round the match's
start (the origin) and follows a focus, what the game gives `NavigationService.Follow(WorldPoint)` (a point among its
players and walkers), else the first streaming centre. It moves by whole tiles (`NavigationService.TileCells`, 64
cells, 32 m) to put the focus in its middle, kept inside the map, when that corner lies more than a quarter of the
window away, or differs while the focus is within an eighth of the window of its edge. A move stops the edits' reads,
copies the cells both windows hold to the thread's grid at their new places (`NavGrid.CopyOverlap`), and finds the
logged edits since the last reset that reach the strips coming in; the thread fills those strips from the source
(`INavGridSource.Fill(grid, rect)`), stamps the edits on them (`NavGridScanner.Stamp` within the strip), and builds every
field; the main thread then takes that grid whole with the fields (`Moving` until then), and those edits wait to be
read from the voxels. Each `FlowField` keeps the window it was built over (`OriginX`, `OriginZ`; `TryCell`), so a
walker finds its cell in the field it holds, whatever the grid did since; a goal past the window stands in at the
window's edge cell nearest it (`NavGrid.NearestCell`), so walkers head that way until the window reaches it. The grid
and the fields are absolute (corners and floors in centimetres); their helpers in metres (`TryCell`, `CentreMetres`,
`FloorMetres`) and the debug lines take the match's origin and local positions.

A walker whose goal changes (a character moving to cover) asks for a path instead (`NavPathFinder`, A*, on the main
thread over `NavigationService.Grid`): `Find(grid, size, costs, start, goal, path, out length, budget)` writes the cells
after the start into a span (the first of a longer path), by a flow field's steps (eight neighbours, a diagonal only
where both cells beside it are walkable, a walkable cell's class cost, the steep cost for a rise past the size's climb),
but a cell the size cannot stand in is closed instead of priced, so a path walks round what blocks it; the heuristic is
the octile distance at the cheapest class's cost, so a path found costs what a flow field's route does. At most a
budget of cells is expanded (`DefaultBudget`, 4,096); past it, or where the goal is closed off or outside the grid, the
path ends at the cell searched nearest the goal (`NavPathResult.Nearest`; `None` for a start the size cannot stand in).
It keeps its arrays (an entry a grid cell, and a heap) between searches and allocates nothing once they have grown.

### Files in `src/Itera.Engine`

  - `Navigation/`: `NavigationService` (with `NavArea`), `NavGrid`, `NavSize`, `NavCostTable`, `NavFieldSpec`,
    `INavGridSource` (the game's part), `FlowFieldBuilder`, `FlowField`, `NavPathFinder` (with `NavPathResult`),
    `NavGridScanner`, `NavObjects`, `NavigationDebugLines`.
