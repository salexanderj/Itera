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

### Files in `src/Itera.Engine`

  - `Navigation/`: `NavigationService` (with `NavArea`), `NavGrid`, `NavSize`, `NavCostTable`, `NavFieldSpec`,
    `INavGridSource` (the game's part), `FlowFieldBuilder`, `FlowField`, `NavGridScanner`, `NavigationDebugLines`.
