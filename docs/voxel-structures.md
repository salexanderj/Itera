# Voxel storage: 64-trees, OpenVDB, NanoVDB, and others

Written 2026-09-23.
`ARCHITECTURE.md` ("Voxel storage") records the decision; this file records the research behind it, so it need not be
repeated.

## The question

The question was whether a 64-tree, OpenVDB, NanoVDB, or a related structure would be worth using instead of the current
sparse voxel hierarchy, given the project's goals. Here a 64-tree is a sparse tree whose nodes have 4 x 4 x 4 = 64
children and a 64-bit child mask (also called a tetrahexacontree or contree).

The open goals that depend on how voxels are stored:
- a faster traversal for the raymarcher (shelved since). Its voxel pass takes 14-15.5 ms at 2560x1440 on
  the RX 5700 XT with shadows, and 8.6-10.6 ms without;
- fewer copy regions after long play;
- memory under the 1 GB budget;
- frequent edits that every peer replays the same way (the world is its seed plus its edit log);
- Vulkan on AMD, NVIDIA, Intel, and MoltenVK, from C#, on Linux, Windows, and macOS.

## Answer

Keep the current structure.
- Itera's chunk tree is already a 64-tree, over bricks the size of VDB's leaves.
- A benchmark of a pure 64-tree against a layout like Itera's cells and bricks put the 64-tree 0 to 12 % faster in
  five of six scenes. Its gains came from traversal techniques, which Itera can adopt without a new structure.
- NanoVDB would take 1.5 to 5.5 times the memory. It cannot change its tree after building, so every column load or
  edit would need a new grid. Its tools that build or change a tree on the GPU run only on CUDA.
- OpenVDB is a C++ library that needs TBB and has no .NET binding. It would replace memory that Itera already
  manages, and its leaves take about 600 B each, against about 128 B per brick in Itera, nodes included.

Four traversal techniques and one layout idea from this research were listed for the raymarcher and tried the same day: node
masks, the beam pass, and one heap range per column were kept (see the note before "Sources").

## How the sources were read

- Read in full: the VoxelRT README (downloaded through the GitHub API) and `PNanoVDB.h` from the OpenVDB repository.
- Read in part: the NanoVDB lines of the OpenVDB 13.0 and 13.1 GitHub release notes, the file list of
  `nanovdb/nanovdb/tools/cuda/`, and the list of types and functions in tree64's `src/lib.rs`.
- Read through a web fetch that returned summaries and quotes: the 64-tree guide, the Hybrid Voxel Formats paper,
  the Aokana paper, the NanoVDB FAQ, and the OpenVDB dependency page.
- Only abstracts and search results were read: the VDB and NanoVDB papers (the ACM pages refused access), HashDAG,
  fVDB, and GVDB.

To fetch the two repositories again at the commits that were read:
- `gh api "repos/AcademySoftwareFoundation/openvdb/contents/nanovdb/nanovdb/PNanoVDB.h?ref=d5996c89dae24c2e9218871cef4fcbec7984517b" -H "Accept: application/vnd.github.raw"`
- `gh api "repos/dubiousconst282/VoxelRT/contents/README.md?ref=e605812840a9f6abbadf84a436f8602e2972ee43" -H "Accept: application/vnd.github.raw"`

## The current structure

- `ChunkTree` (`src/Itera.Engine/Voxels/ChunkTree.cs`) holds a 128^3 chunk in three levels:
  - a root node of 4^3 cells of 32 voxels;
  - a cell node of 4^3 bricks of 8 voxels;
  - a brick of 8^3 voxels.
- A CPU node (`NodeRecord`) is a 64-tree node: a 64-bit child mask and a packed list of children read by rank
  (`PopCount`). A second mask marks children of one solid material. An all-air or all-one-material child costs one
  list entry (VDB calls these tiles), and a node that becomes uniform is freed.
- A brick (`BrickCodec`) is a 512-bit occupancy mask plus a palette of 0, 1, 2, 4, or 8 bits per voxel.
- Above the chunks, each level of detail has a column window: a grid of 32 x 32 columns addressed by column
  coordinate modulo 32. It does the job of VDB's root table, but a lookup is one step, with no hash or search.
- The GPU copy (`VoxelGpuFormat`) keeps the bricks as they are (the CPU's 8-byte record becomes a 4-byte header). It
  stores each node as a dense table of 64 words (256 B), as VDB does, not as a packed list.
- The shader (`voxel-traversal.glsl`) steps over the largest empty or solid block that holds the current voxel: 128,
  32, 8, 4, 2, or 1 voxels of the level, the last three from the brick's mask. VoxelRT's benchmark (below) found
  this kind of traversal, stepping by bitmask tests, faster than DDA from node to node. Every step starts again at the
  column window (`probe` calls `probeTree`).

Sizes in bytes:

| | Itera, CPU | Itera, GPU | NanoVDB, byte values | 64-tree of the guide |
|---|---|---|---|---|
| Node | 24, plus a list of 8, 16, 32, or 64 entries of 4 B | 256 | 33,856 for its 16^3 node over 128^3 voxels | 12, plus its children packed |
| 8^3 leaf of air and 1 material | 72 | 68 | 608 | 8 leaves of 4^3: 96, plus 1 per solid voxel |
| 8^3 leaf of 2 / 3-4 / 5-16 / 17+ materials | 152 / 216 / 344 / 584 | 148 / 212 / 340 / 580 | 608 | the same |

## 64-trees

### The guide (dubiousconst282, 2024)

"A guide to fast voxel ray tracing using sparse 64-trees":
- Nodes are 12 bytes: a leaf flag, a 31-bit offset to the children, and a 64-bit child mask. Children are packed and
  found by rank. Leaves are 4^3 voxels with one material byte per solid voxel.
- Traversal gains, in GPU cycles per ray on the Bistro scene at 4096^3 on a laptop's integrated GPU:
  - keeping a stack of ancestor nodes, instead of descending from the root at each step: 16,903 to 8,896 (about 2x);
  - merging empty 2x2x2 groups of cells into one step, by mask tests: 8,898 to 7,052 (21 %);
  - mirroring the ray into one octant, so every exit-plane test takes the same form: 7,061 to 6,358 (10 %).
- Memory on Bistro at 8192^3: 224.1 MB, against 368.0 MB for ESVO.
- Its caveats:
  - grazing rays near flat planes remain the slow case;
  - edits of packed nodes need a pool allocator and careful handling of freed nodes;
  - "it's probably best to have many smaller trees at a top-level grid rather than a single giant tree".

### The VoxelRT benchmark (same author)

VoxelRT (`alt-renderers` branch at `e605812`) compares nine structures on six static scenes on an integrated GPU.
Its `XBrickMap` has three levels: sectors of 4^3 bricks, 8^3-voxel bricks, and 4^3, 2^3, and 1^3 empty-block skips
inside bricks. That is Itera's cell and brick, without Itera's chunk root and level-of-detail windows. Primary rays,
in millions per second:

| Scene | XBrickMap | Tree64 | Tree64 against XBrickMap | ESVO | Steps per ray, XBrickMap / Tree64 |
|---|---|---|---|---|---|
| Sponza 2k | 165.6 | 182.6 | +10 % | 95.0 | 19.2 / 19.9 |
| Eco House 2k | 96.3 | 107.5 | +12 % | 67.1 | 27.8 / 26.8 |
| Bistro 4k | 109.6 | 109.7 | 0 % | 67.9 | 27.9 / 30.9 |
| San Miguel 4k | 109.8 | 112.1 | +2 % | 67.9 | 25.2 / 28.8 |
| Forest Lake 2k | 88.8 | 93.1 | +5 % | 57.4 | 26.2 / 27.4 |
| NY Highway I95 4k | 115.2 | 208.9 | +81 % | 136.9 | 31.3 / 16.0 |

- The two layouts take about the same number of steps per ray. Only the highway scene, with long empty stretches,
  favours the deeper tree.
- The author reports that Tree64 was about as fast as XBrickMap until two changes, which together gave 18 %:
  - the ancestor stack in group-shared memory instead of a local array: about 9 %;
  - mirroring into the ray's negative octant: 11 %.
- On leaves: "a simpler pointer-based tree structure with even wider leaf nodes has shown little drawbacks over
  compressed nodes, and additional opportunities in material compression (palettes and tile hashing)". That
  describes Itera's bricks.
- The Tree64 backend keeps "a traditional chunk hierarchy for bookkeeping", and rebuilds a chunk's tree on every
  change.
- "trees and BVHs are better suited for LODs". That holds when coarse voxels are derived from fine ones. Itera
  generates each level from the seed instead (see "Why a world-sized 64-tree does not fit").
- The numbers are ratios on an integrated GPU, and the highway scene shows that they change with the scene. They do
  not predict times on the RX 5700 XT.

### tree64 (expenses, Rust)

A library built on the guide, updated 2026-08. It edits by path copying (new nodes for the changed path, old ones
kept, so it has undo), shares identical node lists through a hash table, and reports the ranges that changed for
upload. Path copying appends new nodes and keeps the old ones, so memory grows with edits.

## OpenVDB

C++, version 13.1.0 of 2026-09-16, from the Academy Software Foundation.

- Layout (Museth 2013):
  - a root hash map, so the index space has no bounds;
  - internal nodes of 32^3 and 16^3 children, each a dense table of child pointers or tile values, with a child mask
    and a value mask;
  - 8^3 leaves with a dense array of 512 values and a 512-bit value mask;
  - a value accessor that caches the last nodes visited, so reads near the last one are fast;
  - topology that changes at run time.
- It was built for film effects: level sets, fog volumes, and fluid simulation, with float values and many tools
  (CSG, morphology, meshing, filtering, and ray intersection by hierarchical DDA).
- It does not fit Itera:
  - It is C++ that needs TBB and Imath (Blosc and zlib are optional). It has no .NET binding, so Itera would have to
    write, build, and ship a native wrapper for six runtime identifiers.
  - It would replace native memory that Itera already owns and budgets (`VoxelMemory`) and whose content hashes the
    tests pin.
  - A leaf keeps a value for each of its 512 voxels: with its mask, about 600 B for byte values. Itera's bricks take
    72 to 584 B, and about 128 B on average in the log of 2026-09-23 15:50:31 (381.2 MB for 3,134,263 bricks, nodes
    included). OpenVDB compresses only in files, and Itera has no chunk files.
  - Its CSG and level-set tools use floats; Itera's edits must use integer geometry so every peer gets the same voxels.
  - Itera already has what it would use: topology that changes at run time, tiles, and lookups in constant time.

## NanoVDB

Part of the OpenVDB repository (`PNanoVDB.h` read at `d5996c8`, 2026-09-18).

- It is a VDB tree written into one contiguous buffer, with offsets instead of pointers, for the GPU. `PNanoVDB.h`
  compiles as C, HLSL, or GLSL, so a Vulkan shader could read it.
- The topology is fixed. The FAQ says: "While values can be modified in a NanoVDB grid its tree topology cannot."
  The repository's `nanovdb/nanovdb/tools/cuda/` holds tools that build or change topology (points to grid, dilate,
  merge, prune, coarsen, refine, `TopologyBuilder`). They are CUDA only (`.cuh`), and the 13.0 release notes say
  such operations "generate new NanoVDB grids (as oppose to in-palace modifications in OpenVDB)". For Itera, every
  column load, unload, or edit would mean a new grid. On AMD, Intel, and Apple GPUs it would be built on the CPU and
  uploaded whole.
- Node sizes, from `pnanovdb_grid_type_constants`, are fixed whatever a node holds:

  | Grid type | Upper node (32^3 children) | Lower node (16^3 children) | Leaf (8^3 voxels) |
  |---|---|---|---|
  | `UINT8` (a material byte per voxel) | 270,400 | 33,856 | 608 |
  | `ONINDEX` (an index into a separate array of values) | 270,400 | 33,856 | 96, plus 1 byte per solid voxel in the array |

- Memory for the voxels of one run (the log of 2026-09-23 15:50:31: five levels, 2,522 columns, 8,399 mixed chunks,
  3,134,263 bricks, 381.2 MB; Itera's logs count a MB as 2^20 bytes, and so do these figures). A lower node covers
  128^3 voxels, the same as a chunk, and a leaf is the size of a brick, so the counts carry over:
  - `UINT8`: 8,399 x 33,856 B = 271 MB of lower nodes and 3,134,263 x 608 B = 1,817 MB of leaves, about 2,090 MB in
    all: 5.5 times as much.
  - `ONINDEX`: 271 MB + 287 MB = 558 MB before the material bytes, 1.46 times as much, and more with them.
- Its ray traversal (hierarchical DDA) steps at node sizes: 4096, 128, 8, and 1 voxels. Itera's steps at 128, 32, 8,
  4, 2, and 1. PNanoVDB finds a root tile by scanning the tiles in a loop (`pnanovdb_root_find_tile`); Itera's column
  window takes one step.

## Other structures

- **fVDB** (NVIDIA, SIGGRAPH 2024): deep learning on NanoVDB index grids, in PyTorch and CUDA. Not relevant.
- **GVDB Voxels** (NVIDIA, 2017): a VDB for CUDA and OptiX with topology that changes at run time. NVIDIA GPUs only;
  last change 2022-10.
- **ESVO** (Laine and Karras 2010): an octree with contours and a beam optimisation. In VoxelRT it gave 57 to 137
  million primary rays per second, against 89 to 209 for XBrickMap and Tree64, and was the slowest of the three in
  five of the six scenes.
- **SVDAG and HashDAG** (Kämpe, Sintorn and Assarsson 2013; Careil, Billeter and Eisemann 2020): these save memory by
  sharing identical subtrees. HashDAG keeps the tree in a hash table so it can be edited without rebuilding. It was
  built for large static models edited by hand, runs on CUDA, and needs garbage collection of unused nodes. Itera's
  world is regenerated from the seed, and memory does not limit it now (381 to 600 MB of the 1024 MB budget).
- **Aokana** (I3D 2025): an open-world voxel renderer. Each 256^3 chunk is a shallow SVDAG whose lowest three levels
  use 64-bit masks of 4^3; levels of detail merge 8 chunks into one. It starts rays from rasterised chunk boxes, with
  Hi-Z culling of 8x8 tiles against the previous frame's depth. It states: "our implementation does not support
  runtime modification of voxels".
- **Hybrid Voxel Formats** (2024): compared 40 stacks of grids, distance fields, SVOs, and SVDAGs on an RTX 2060
  Super. It recommends "a good default format for most models": a plain grid on top with DAGs below.

The guide, VoxelRT, Aokana, and the Hybrid Voxel Formats paper all end with the same shape as Itera: a grid of
shallow trees.

## Why a world-sized 64-tree does not fit

- Coarse levels are generated from the seed: a coarser voxel is solid when its centre is. 1 cm voxels exist only
  within 16 m, so there is no fine data to derive the coarse levels from. A tree that keeps levels of detail in its
  inner nodes cannot replace the five windows.
- Streaming, unloading, and the GPU mirror all work column by column. A tree over the whole level would need its top
  nodes rewritten as columns come and go.
- 4^3 leaves:
  - `solidOffsets` would read 27 leaves instead of 8: its 9-voxel box always spans exactly 2 bricks per axis, and it
    has an `#error` for any other radius (the box has been 7 voxels since 2026-10-04: one or two bricks per axis,
    still 8 looked up);
  - a brick of air and one material costs 72 B. The same voxels as 4^3 leaves with a byte per solid voxel cost 96 B
    plus up to 512 B.
- Packed child lists on the GPU would save little and cost copy regions:
  - the logs do not count nodes. If a stored 32 cm cell holds 16 to 32 stored bricks (a layer or two of bricks where
    the ground crosses it), dense nodes are about 6 to 13 % of the heap;
  - a packed list changes size with each edit, so edits would need reallocations, and each reallocated block
    becomes another copy region, which the raymarcher already had too many of.

The raymarcher's candidates (four traversal techniques and one layout idea, each tried and measured on 2026-09-23) and
the trials' results (node masks, the beam pass, and one heap range per column kept; octant mirroring and brick reuse
rejected) were cut when the raymarcher left main on 2026-10-06; the Zorf TD archive's branch `raymarcher` and tag
`raymarcher-final` keep them. The GPU copy and the traversal shader that the sections above name were the raymarcher's
too.

For physics and the navigation grid: a cached accessor like VDB's, which keeps the last column, chunk,
and brick. Today each `VoxelWorld` voxel read looks the column up in a dictionary.

## Sources

- dubiousconst282 (2024), "A guide to fast voxel ray tracing using sparse 64-trees".
  https://dubiousconst282.github.io/2024/10/03/voxel-ray-tracing/
- dubiousconst282, VoxelRT, "Benchmark of acceleration structures" (README, branch `alt-renderers`).
  https://github.com/dubiousconst282/VoxelRT/tree/alt-renderers
- expenses, tree64. https://github.com/expenses/tree64
- Museth, K. (2013), "VDB: High-Resolution Sparse Volumes with Dynamic Topology", ACM Transactions on Graphics 32(3).
  https://dl.acm.org/doi/10.1145/2487228.2487235
- Museth, K. (2021), "NanoVDB: A GPU-Friendly and Portable VDB Data Structure For Real-Time Rendering And
  Simulation", SIGGRAPH 2021 Talks. https://dl.acm.org/doi/10.1145/3450623.3464653
- NanoVDB FAQ. https://www.openvdb.org/documentation/doxygen/NanoVDB_FAQ.html
- OpenVDB repository and release notes. https://github.com/AcademySoftwareFoundation/openvdb
- OpenVDB dependencies. https://www.openvdb.org/documentation/doxygen/dependencies.html
- Williams, F. et al. (2024), "fVDB: A Deep-Learning Framework for Sparse, Large-Scale, and High-Performance Spatial
  Intelligence". https://arxiv.org/abs/2407.01781
- NVIDIA, GVDB Voxels. https://github.com/NVIDIA/gvdb-voxels
- Laine, S. and Karras, T. (2010), "Efficient Sparse Voxel Octrees", I3D 2010.
  https://research.nvidia.com/publication/efficient-sparse-voxel-octrees
- Kämpe, V., Sintorn, E. and Assarsson, U. (2013), "High Resolution Sparse Voxel DAGs", ACM Transactions on Graphics
  32(4). https://dl.acm.org/doi/10.1145/2461912.2462024
- Careil, V., Billeter, M. and Eisemann, E. (2020), "Interactively Modifying Compressed Sparse Voxel
  Representations", Computer Graphics Forum 39(2). https://onlinelibrary.wiley.com/doi/abs/10.1111/cgf.13916 and
  https://github.com/Phyronnaz/HashDAG
- "Aokana: A GPU-Driven Voxel Rendering Framework for Open World Games" (2025), Proceedings of the ACM on Computer
  Graphics and Interactive Techniques. https://arxiv.org/abs/2505.02017
- "Hybrid Voxel Formats for Efficient Ray Tracing" (2024). https://arxiv.org/abs/2410.14128
