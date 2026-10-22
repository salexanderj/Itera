# ModelCheck

Checks glTF models (`.glb` or `.gltf`) as the engine reads them, and against a game's budgets. It runs the engine's own
import with every texture decoded (`ModelInspection` in `src/Itera.Engine/Assets/`), so what it passes the game loads
as checked. A development tool, not game code; it needs only the .NET SDK.

```bash
dotnet run --project tools/ModelCheck -- src/Game/Models/Fox/Fox.gltf --unit 0.01
dotnet run --project tools/ModelCheck -- --budget BUDGETS.json --class prop MODEL.glb [MORE.glb ...]
dotnet run --project tools/ModelCheck -- --json MODEL.glb        # every report as JSON, for scripts
```

- `--unit METRES`: metres a unit of the file, as the model's `Declare` line gives it (0.01 for a file in centimetres).
  Heights and budgets are in metres.
- `--budget FILE --class NAME`: hold each model to the budget of that asset class. The file is a JSON object of classes,
  each with any of `mostTriangles`, `mostJoints`, `mostMaterials`, `mostTextureTexels` (a texture's longest side),
  `leastHeight` and `mostHeight` (metres), `skinned` (true or false), and `clips` (names the model must have). Comments
  are allowed; a key it does not know stops the run.
- `--json`: prints a list of reports: `file`, `ok`, `problems` (each a `kind` and a `message`), `notes`, `triangles`,
  `vertices`, `boundsMin`, `boundsMax`, `materials`, `textures` (`name`, `role`, `width`, `height`), `joints`, and
  `clips` (`name`, `seconds`).

Each problem has a kind:

- `refused`: the engine cannot read the file (a compressed extension, a broken file, weights that sum to zero).
- `import`: a warning of the import (morph targets, lines, an unlit material, an image it cannot decode).
- `content`: what the import leaves out or makes up without a warning, or what a game cannot name: weights past four
  joints, a primitive without a material, an unnamed material, joint, or clip, generated tangents, animations left
  out, cameras and lights, and joints or clips that share a name.
- `budget`: over or under a limit of the class.

Notes do not fail a model: a mesh used at two nodes, an extension the engine does not read.

The exit code is 0 when every model is ready, 1 when one has a problem, and 2 for a wrong command line or budget file.
