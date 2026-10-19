---
paths:
  - "src/Itera.Engine/Navigation/**"
  - "tests/Itera.Engine.Tests/Navigation/**"
---

## Navigation

`NavigationService` runs on the host and offline only. It keeps a grid of cells (`NavGrid.CellCentimetres`) on its own thread, from a game's `INavGridSource`, and builds the flow fields a game asks for. The engine knows nothing of teams; a game's fields are its own.

A game without an `INavGridSource` has no navigation, and its loading task is done at once.

### The source and the fields

An `INavGridSource` makes the game's fields (`CreateFields`) and fills a rectangle of cells from the world as generated (`Fill`). It says the ground's height at a column (`GroundAt`) and whether a material is ground (`IsGround`).

A field is described by a `NavFieldSpec`:

- Goals, as absolute places.
- A body size (`NavSize`): its height, radius, highest climb, and deepest drop. Any drop is allowed unless the size gives one.
- A cost table (`NavCostTable`):
  - what each kind of cell costs;
  - whether a block is a price to break through or a wall to walk round;
  - the steep cost of a rise past the climb or a drop past the drop, so routes go round a pit the walker would not climb back out of;
  - a cost a metre for each walkable step down, none unless the table gives one and never more than the steep cost. Routes then go round a hole where the way round is short. Every route between two places drops as far as it climbs, less the height between them, so only going down and back up costs.

`GroundAt` and `IsGround` are optional: they default to no ground height (`int.MinValue`) and every material counting as ground.

Floors are absolute centimetres in `int`s, so any map's heights fit. `NavigationDebugLines` draws a field's arrows round the camera for a game's debug view.

### Edits

Each fixed step reads new edits into the grid, up to a limit of cells a step. The step reads cells from the level-0 voxels where they are loaded. Where they are not loaded, it stamps the cells from the edit's shape and reads them once they load. A dig under the floor lowers it under the same ceiling.

- An edit that covers no cell's centre column, or lies outside the grid, is passed over.
- A read takes the floor from the surface the voxels' density gives (`VoxelWorld.SurfaceOver`), which stays near the source's ground.
- A read or a stamp judges solid from the source's ground there (`INavGridSource.GroundAt`).
- A far removal of some materials stamps a pit only when it removes ground (`INavGridSource.IsGround`).
- The thread builds the fields again, while their users keep the old ones.
- After `reset` the thread fills the grid again first (`NavigationService.Refilling`).

### Voxel objects

The voxel objects a world places (`.claude/rules/objects-meeting.md`) block as solid does.

**Stamping at a fill.** After each fill from the source (the window at the start, a refill after a reset, a move's strips), the navigation thread stamps the objects as generated onto the cells.

- `NavGridScanner.StampObjects` reads the placer's objects in the areas the match keeps (`NavObjects`).
- `ObjectColumnReader` reads them in the mover's cells, from the kinds' columns' runs. `Initialize` makes the runs before the thread starts (`VoxelObjectKinds.MakeColumnRuns`).
- Each run of an object's blocking voxels in a cell's centre column acts on the cell. `LowCentimetres` is the highest a solid may rise over a cell's floor and still be stood on. Where a run rises more than that over the ground (a trunk), it blocks the cell. Where it is lower (a flat stone), it raises the floor. Where it hangs over the floor (a branch), it lowers the headroom.
- Passable voxels (leaves) change nothing.
- A run whose bottom lies within `LowCentimetres` over the ground stands on the floor, in a stamp as in a read.
- A stamp keeps the headroom's top where an object raised the floor, and a read looks up from the new floor. The two differ in headroom there.

**Reading.** A read takes the objects as they are, damaged too, from the mover's `ObjectColumns` (`NavGridScanner.Objects`, on the main thread).

- Where the mover's cells are the voxels', the read uses the mover's own columns.
- Where the mover's cells are finer (`Engine:Physics:TerrainCellCentimetres`), navigation has its own in the same cells. A cell reads every object's column in its square, so a thin wall anywhere in it blocks it, and a low block raises its floor (`NavigationPlacedObjectsTests`).
- The read uses the highest top and the lowest bottom with the voxels.
- A read waits, with the stamped edits, until the objects round the cell are known (their areas filled, `ObjectsKnown`).

**Changes.**

- A removal of an object's part (`VoxelEditOperation.RemovePart`), and an object placed or removed whole during play (`PlaceObject`, `RemoveObject`), change the cells under its box. They are read once loaded; their stamp changes nothing. The generator's placer does not know them, so a fill does not stamp them.
- A world whose generator places nothing reads objects wherever the run has kinds (`ObjectWorld.IsEnabled`).
- Each change of an object's copy (`ObjectDamage.ReadChanges`) has the cells under its box read again, where loaded, else once loaded. A change is an edit or a cut, a replay, a copy made again after the cap dropped it, or a reset's healing. The read covers what an edit changed, or the object's whole reach.
- A change lost past the ring is passed over. The edits that changed the object wait for their own reads. Changes during a refill are read after it.

### Closed boxes

A game closes boxes of cells to every walker whatever the world holds there. A gate's leaf or a door is an example: a thing the game draws and makes solid itself. The calls run on the main thread, with absolute centimetres:

- `NavigationService.Close(minX, minZ, maxX, maxZ)` closes the cells whose centres lie in the box.
- `MoveClosed` moves a closed box.
- `Open` opens it.

A closed cell's headroom reads as none at once, with its floor as it was. This holds through every read of the voxels, stamp, refill, and move of the window. The service applies it to the main thread's grid after each, and marks the rows for the next job. A field then charges the cell as a block (a price to break through, or a wall, by its cost table), and paths go round it.

The cells a box leaves are queued to be read again from the voxels (filled from the source without terrain). A box still holding a cell keeps it closed. A box closed before the grid is ready closes its cells once it is. A client keeps its boxes and changes nothing. `Closed` counts the boxes. A move allocates nothing.

### The window

The grid is a window (`NavArea`) over the map's square of whole cells. `Engine:Navigation:WindowCells` sets its size.

A map that fits is covered whole, and the window never moves. On a larger map the window starts round the match's start (the origin) and follows a focus. The focus is what the game gives `NavigationService.Follow(WorldPoint)`, a point among its players and walkers. Without one, it is the first streaming centre.

The window moves by whole tiles (`NavigationService.TileCells`) to put the focus in its middle, kept inside the map. It moves when that corner lies far from the current one, or differs while the focus is near the window's edge.

A move works in this order:

1. It stops the edits' reads.
2. It copies the cells both windows hold to the thread's grid at their new places (`NavGrid.CopyOverlap`).
3. It finds the logged edits since the last reset that reach the strips coming in.
4. The thread fills those strips from the source (`INavGridSource.Fill`), stamps the edits on them (`NavGridScanner.Stamp` within the strip), and builds every field.
5. The main thread takes that grid whole with the fields (`Moving` until then). Those edits wait to be read from the voxels.

Absolute positions:

- Each `FlowField` keeps the window it was built over (`OriginX`, `OriginZ`; `TryCell`). A walker finds its cell in the field it holds, whatever the grid did since.
- A goal past the window stands in at the window's edge cell nearest it (`NavGrid.NearestCell`). Walkers head that way until the window reaches it.
- The grid and the fields are absolute: corners and floors in centimetres. Their helpers in metres (`TryCell`, `CentreMetres`, `FloorMetres`) and the debug lines take the match's origin and local positions.

### Paths

A walker whose goal changes (a character moving to cover) asks for a path instead of a field. `NavPathFinder` is A*, on the main thread, over `NavigationService.Grid`.

- `Find(grid, size, costs, start, goal, path, out length, budget)` writes the cells after the start into a span. A longer path gives its first cells.
- It steps as a flow field does: eight neighbours, a diagonal only where both cells beside it are walkable, a walkable cell's class cost, a step down's cost for its drop, and the steep cost for a rise past the size's climb or a drop past its drop.
- A cell the size cannot stand in is closed, not priced, so a path walks round what blocks it.
- The heuristic is the octile distance at the cheapest class's cost, so a path found costs what a flow field's route does.
- At most a budget of cells is expanded (`DefaultBudget`). Past it, or where the goal is closed off or outside the grid, the path ends at the cell searched nearest the goal (`NavPathResult.Nearest`; `None` for a start the size cannot stand in).
- It keeps its arrays (an entry a grid cell, and a heap) between searches and allocates nothing once they have grown. They are large for a big window, so a match's walkers share one finder, `NavigationService.PathFinder`. It is made at its first use, and a search keeps nothing of the one before.

### Tests

`FlowFieldTests`, `NavGridScannerTests`, `NavPathFinderTests`, `NavigationServiceTests`, `NavigationObjectsTests`, `NavigationPlacedObjectsTests`, `NavigationWindowTests`.

### Files in `src/Itera.Engine`

- `Navigation/`: `NavigationService` (with `NavArea`), `NavGrid`, `NavSize`, `NavCostTable`, `NavFieldSpec`, `INavGridSource` (the game's part), `FlowFieldBuilder`, `FlowField`, `NavPathFinder` (with `NavPathResult`), `NavGridScanner`, `NavObjects`, `NavigationDebugLines`.
