---
paths:
  - "src/Itera.Engine/Navigation/**"
  - "tests/Itera.Engine.Tests/Navigation/**"
---

## Navigation

Navigation (`NavigationService`, host and offline only) fills a grid of 50 cm cells (`NavGrid.CellCentimetres`) over the
map on its own thread from a game's `INavGridSource` (which says the ground's height and kind at a cell, and what blocks
it) and builds the flow fields a game asks for (`NavFieldSpec`: a goal, a body size (`NavSize`), and a cost table,
`NavCostTable`: what each kind of cell costs, and whether a block is a price to break through or a wall to walk round).
The engine knows nothing of teams; a game's fields are its own. Each fixed step reads new edits into the grid (at most
128 cells read from the level-0 voxels, or stamped from the edit's shape where they are not loaded, and read once they
load; an edit that covers no cell's centre column is passed over; a read takes the floor from the surface the voxels'
density gives, `VoxelWorld.SurfaceOver`, within about a centimetre of the source's ground), and the thread builds the
fields again while their users keep the old ones. A read or a stamp judges solid from the source's ground there
(`INavGridSource.GroundAt`); a far removal of some materials stamps a pit only when it removes ground
(`INavGridSource.IsGround`). After `reset` the thread fills the grid again first (`NavigationService.Refilling`). A game
without an `INavGridSource` has no navigation (its loading task is done at once). The grid keeps floors in 16-bit
centimetres, so navigation starts only on a world whose floor and top lie within ±327.67 m (`NavGridScanner` refuses a
taller one). `NavigationDebugLines` draws a field's arrows round the camera for a game's debug view.

### Files in `src/Itera.Engine`

  - `Navigation/`: `NavigationService`, `NavGrid`, `NavSize`, `NavCostTable`, `NavFieldSpec`, `INavGridSource` (the
    game's part), `FlowFieldBuilder`, `FlowField`, `NavGridScanner`, `NavigationDebugLines`.
