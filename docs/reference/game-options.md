# The game's options

The game's own options under `Game`, each with its default, the least and most its validator takes, and what it does; then the engine's options its `appsettings.json` ships at other values (the engine's are in `engine-options.md`). Generated from the code by the reference tests; do not edit it by hand (`docs/reference/README.md`).

## `Game:Player`

| Option | Default | Least to most | What it does |
| --- | --- | --- | --- |
| `Game:Player:HeightMetres` | `1.7` | `1.2` to `2.5` | Height from the feet to the top of the head, in metres. |
| `Game:Player:StartFlying` | `false` |  | When true, the game starts with the debug fly camera instead of walking (the console's `fly` switches). |
| `Game:Player:ThirdPerson` | `false` |  | When true, the game starts with the camera behind the player's right shoulder (third person; its key and the console's `camera` switch it for the rest of the run). |

## `Game:World`

| Option | Default | Least to most | What it does |
| --- | --- | --- | --- |
| `Game:World:CameraPose` | none |  | Debug aid: when set, the player's eye and the debug camera start here instead of at the world's start: "x,y,z,yaw,pitch" with the position in metres and the angles in degrees (yaw 0 looks along -z, 90 along -x; pitch below 0 looks down). The console's `pos` prints the pose in this form. |

## `Game:Minimap`

| Option | Default | Least to most | What it does |
| --- | --- | --- | --- |
| `Game:Minimap:Metres` | `64` | `16` to `256` | How much of the world the minimap shows across, in metres, round the player. |

## The engine's options the game's `appsettings.json` changes

| Option | The engine's default | The game's |
| --- | --- | --- |
| `Engine:Application:DataFolder` | `Itera` | `IteraGame` |
| `Engine:Application:Name` | `Itera` | `Itera Game` |
| `Engine:Assets:Extra` | `` | `` |
| `Engine:Renderer:ObjectVoxelPixels` | `1` | `4` |
| `Engine:Renderer:ViewDistanceMetres` | `256` | `8000` |
| `Engine:Voxels:Levels` | `4` | `9` |
| `Engine:Window:Title` | `Itera` | `Itera Game` |
