# Textures

Makes the world materials' texture sets (below), and with `model` a model's compressed textures (the next section).

## A model's compressed textures

```bash
dotnet run -c Release --project tools/Textures -- model src/Game/Models/Fox/Fox.gltf [OTHER.gltf ...]
```

For each model (`.gltf` or `.glb`) it asks the engine for every texture the model's import makes
(`CompressedTextures.DecodeAll`: each image in each role a material uses, decoded and given every level down to 1x1 by
the engine's `MipChain`, as a run without compressed textures makes them), compresses each level with BCnEncoder.NET
(quality Balanced; a level's last row and column repeated to fill whole 4x4 blocks), and writes `NAME.textures/` beside
the model:

- `imageN-colour.dds` (BC7 sRGB: base colour and emission), `imageN-linear.dds` (BC7: occlusion, roughness, and
  metalness), `imageN-normal.dds` (BC5: a normal map's x and y), for image N of the model; any size;
- `textures.json`: the model's file, the encoder, and for each texture its image, role, file, size, and the SHA-256 of
  the image's bytes as the model stores them.

Files of the folder the new list does not name are deleted (a model with no textures left loses them all). A run with
graphics reads a texture from the folder when its hash matches the model's image, a quarter of the decoded size (the
fox's 1024-texel colour: 1.3 MB against 5.3 MB), and decodes the rest: a stale list gives a Warning naming this command,
a model with no list an Information note. The hash covers the image alone, so run the tool again after an image
changes, and on every model after a change to the engine's `TextureDecode` or `MipChain`. Commit the folder with the
model; the game's build copies it (`Models/*/**`). The fox takes about 2 s. A model that cannot be read is reported and
the next one made; the exit code is then 1.

## Material texture sets

Makes the world materials' texture sets (docs/rendering-redesign.md, the materials plan and the art direction) from the
game's own recipes, baked, from texture recipes in Python (the next section), or from CC0 photographs. For each set in
`src/Game/Textures/sources.json` (or the sets named on the command line) it reads the bake's maps from the data
folder's `Bakes/` (below), runs the texture recipe, or downloads a photograph's colour, OpenGL normal, occlusion, and height maps from Poly Haven
or ambientCG at 2048 or 1024 texels into `the game's data folder (`~/.local/share/<DataFolder>/`, `Engine:Application:DataFolder`), TextureDownloads/` (`LocalApplicationData` elsewhere;
once, then from there), makes every mip level
(colour and occlusion averaged in linear light, normals averaged and made unit length again), compresses them with
BCnEncoder.NET, and writes the set's folder in `src/Game/Textures/`:

- `colour.dds`: BC7 sRGB, the colour with the occlusion in alpha (white where the photograph has no occlusion map);
- `normal.dds`: BC5, the normal map's x and y (the shader leaves z out);
- `height.dds`: BC4, the height map (mid grey where there is none);
- `set.json`: the size in texels, the photographed width in metres (Poly Haven's dimensions, ambientCG's `dimensionX`, or
  2 m with a warning), the colour's mean in linear RGB, and the source.

It also writes `SOURCES.md` there (every set's site, asset, link, and licence). The game copies the set folders to
`Textures/` next to the executable, and `MaterialTextureSets` loads them; a material names its set in its `VoxelLook`
(`Texture`).

```bash
dotnet run -c Release --project tools/Textures                      # every set of sources.json
dotnet run -c Release --project tools/Textures -- grass             # one set
dotnet run -c Release --project tools/Textures -- --data-folder IteraGame-agent bark   # bakes and downloads of another data folder (also with --preview)
```

`sources.json` lists `{ "set", "site" ("polyhaven", "ambientcg", "bake", or "recipe"), "asset" (the site's id, the
bake's folder, or the recipe's file from the repository's root), "texels" (2048 or 1024), "kuwahara" (optional) }`. `"kuwahara": { "radius": 8, "allMaps": true }` filters the
photograph with the anisotropic Kuwahara filter (`Kuwahara.cs`: Kyprianidis, Kang, and Döllner 2009, with the
polynomial sector weights of 2011; wrapping, so the tile stays seamless) for a painted look: the colour, and with
`allMaps` the normal map and occlusion too, so the lighting does not bring the photograph's fine detail back (also
`sharpness`, `hardness`, `alpha`; 8, 8, 1). `--preview SET RADIUS OUT.png` writes a set's colour map filtered at a radius, uncompressed, in seconds. A bake is a material's recipe the game wrote with the console's
`materials bake ID top|side NAME` into the data folder's `Bakes/NAME/` (`colour.png`, `normal.png`, `occlusion.png`,
`height.png`, `metres.txt`); the tool makes it into a set as it makes a photograph's. The bakes are not in the repository:
to make a set again, bake its material in the game first (`top` for the recipes laid on the ground's plane, soil and
mottle among them; `side` for streaks and plates, such as bark and wood grain), with the set's name as `NAME`. Every set's heights are moved to a
mean of a half, so at a border between materials only their shapes differ.
A 1024 set takes 2 to 25 s (BC7 with a varying alpha is the slow part), a 2048 one about four times as long. Commit
the set folders and `SOURCES.md`; the downloads and bakes stay out of the repository.

## Texture recipes

A texture recipe makes a set's maps in Python, with numpy and Pillow, outside the game: shapes of any size, a real
height in metres, and maps made from that height. `RecipeTools/` holds the helpers any game's recipes use; the recipes
are the game's own files. A source `{ "set": "rock", "site": "recipe", "asset": "src/Game/TextureRecipes/rock.py",
"texels": 2048 }` runs `RecipeTools/make.py` on the recipe at the set's size into the cache's `recipe/SET/` folder,
each time, then makes the set from those maps as from a bake.

- A recipe is a Python file with `METRES` (the tile's width), optionally `TEXELS` (2048) and `SEED` (1), and
  `make(tile)`, which takes a `tiles.Tile` and returns a `tiles.Maps`: the colour in linear RGB, the height in metres,
  and optionally the occlusion (made from the height if left out).
- `tiles.py`: the tile, whose every operation wraps at its edges, so the set repeats without a seam. Noise by
  wavelength in metres, blurs, slopes, hollows and edges of a height, Voronoi cells on a jittered grid (some merged),
  rectangles cut again and again (panels), courses of blocks, colour ramps, and the writing of the maps. Its random
  numbers come from the seed and a name, so a feature added to a recipe leaves the others as they were.
- Arrays are [row, column]: u along a row, v up the rows. On a wall v is the world's up, so row 0 is the bottom of the
  tile, as the engine reads a set. The normal map's green is the height's slope up the rows.
- A ground recipe should have no direction: on slopes under 45 degrees the shader takes most of the colour from the
  projection from above, whose u runs along the world's x, so a stretched shape would run down slopes facing east or
  west.
- `make.py RECIPE.py OUT [--texels N] [--preview SHEET.png]` runs one recipe by hand. `--texels 512` tries a recipe in
  about a second at the same sizes in metres.
- `preview.py OUT.png LABEL FOLDER [LABEL FOLDER ...]` draws sets lit by a low sun 1.28 m, 5.12 m, and 15.36 m across,
  with the lit lightness's spread for each: the widest shows the tile's repeat, and a spread near 0 there means no
  shape reads from far off. It reads a bake's folder too.
