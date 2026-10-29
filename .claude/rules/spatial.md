---
paths:
  - "src/Itera.Engine/Spatial/**"
  - "tests/Itera.Engine.Tests/Spatial/**"
---

## Finding entities by place

`SpatialGrid` (`Spatial/`, namespace `Itera.Engine.Spatial`) keeps where a set of entities stand, by cells over the
ground, so a game finds those near a point or a line without going over every one. The engine makes no grid and names
no game's things. A game makes the grids it wants (scoped to the match, `new SpatialGrid(origin, world, cellMetres)`)
and fills them.

- An entry (`SpatialEntry`) is an upright cylinder at an entity's feet (local metres), its radius and height, and the
  game's tags, a `ulong` of the game's own bits (its kind, side, and state as it was set). `Set` adds or moves an
  entry, `Remove` takes one out, and `Clear` empties the grid. Things that move are set afresh each step after a
  `Clear`, by a system placed after the systems that move them; things that stay are set and removed as they come and
  go.
- Queries hand a struct visitor (`ISpatialVisitor`, `allows ref struct`) every entry whose cylinder comes within the
  radius, with the least distance to it, in no set order:
  - `Within`: a point and a radius.
  - `Across`: a point and a radius on the ground alone, at any height.
  - `Along`: a line and a radius.
- `Nearest` keeps the nearest few entries a struct filter allows (`ISpatialFilter`; `EveryEntry` keeps all), nearest
  first. Entries as near come in the order of their entities (id, then version), so the answer does not hang on the
  order they were set in. `Any` says whether one is there.
- `SpatialMatch(All, None, Any)` picks entries by tags: all of `All`'s bits, none of `None`'s, and one of `Any`'s (any
  tags when it is 0). The default finds every entry.
- Distances are to the cylinder: across past its radius and up or down past its base or top, 0 inside (`DistanceTo`).
  `Across` counts the distance across alone. `Along` finds the line's nearest point by a golden-section search.
- An entry whose radius is at most half a cell is kept in the cell its feet are in, and a query widens its box by the widest of
  them; a wider entry (a building, a giant) is on a list every query goes over. A query whose box covers more cells
  than there are binned entries goes over every entry instead. Choose a cell about the radius most queries ask, and at least
  twice the radius of most entries.
- A query passes over an entry whose entity is no longer alive (`World.IsAlive`, the version too), so one destroyed
  between the setting and the asking is never handed out. Tags are as they were set: a caller tests the state it
  needs from the entity's components.
- Entries are kept from a whole-metre point of the grid's own (the match's origin when it was made or last cleared),
  and every position set or asked is turned to it in whole metres, so a move of the origin between the setting and the
  asking changes nothing.
- It doubles its room as it needs more, with no limit; setting, removing, clearing, and asking allocate nothing once it
  has held as many entries. Main thread; a visitor does not change the grid it is visiting.

Tests: `SpatialGridTests`.
