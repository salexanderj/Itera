# Adding to Itera: models, sounds, and music

The `scaffold` skill's recipes for models, sounds, and music. Each lists every file the addition touches, in order, and the test that fails when one is missed; finish with the skill's "Then".

## A model from glTF

A model the game makes itself is built by a recipe in `src/Game/ModelRecipes/NAME/`, with the `models` skill, which
writes step 1's files and compresses its textures. A model from elsewhere starts at step 1.

1. The files in `src/Game/Models/NAME/` (`.gltf` with its `.bin` and images, or one `.glb`; no Draco, meshopt, or Basis
   compression), copied by the csproj's `Models/*/**` rule, and a row in `src/Game/Models/SOURCES.md` with its source,
   authors, licence, and changes. Its textures compressed for shipping: `dotnet run -c Release --project tools/Textures
   -- model src/Game/Models/NAME/NAME.gltf` writes `NAME.textures/` beside it (BC7 and BC5, a quarter of the decoded
   size); commit it, and run it again when an image changes (a test like `FoxModelTests`' compressed-textures test
   catches one forgotten).
2. A name in `GameModels` and a `Declare` line there: the path from the executable's folder and the file's unit
   (`metresPerUnit`: 1 for metres, 0.01 for centimetres). `GameRunStartup` declares them at the run's start; a mod
   declares its own at its startup.
3. A system that places it: `World.Create(MeshPlacement.At(position, orientation, scale), DrawnMesh.Of(index))`, with
   the index from `MeshAssets.Find(name)` looked up once (`FoxSystem`); the position is local, so a place the game
   knows in absolute terms (a `WorldPoint`, as its start pose is) goes through the match's `WorldOrigin`
   (`origin.ToLocal(point)`; CLAUDE.md's Units); a fixed system that moves it copies the pose
   into the previous one first. `MeshDrawSystem` draws it. A glTF model's front is +z.
4. A model that things stand on or bump into gets a collider where it is placed: `MeshColliders.Add(asset, transform,
   tagsOf)` from a system's `Initialize` (or later), `tagsOf` giving each material's surface tags (game-defined bits;
   voxel materials' through an `IVoxelSurfaceTags`). Loose bodies land on it; `CollisionSources.Raycast` meets it.
5. A skinned model plays a clip with `MeshAnimator.Playing(rig.FindClip("Walk"))` added to it (the rig from
   `MeshAssets.TryGet`), and changes clip with `animator.FadeTo(clip, 0.3f)` (a fade) or a new `Playing`; a system that
   changes clips runs before the engine's `MeshAnimatorSystem`, which the game registers once, after every such
   system.
6. Check it: `dotnet run --project tools/ModelCheck -- PATH` reads it as the engine does; the game's
   `GameModelsTests` checks every declared model (`ModelChecks`), each recipe's model against its budget class. In a
   run, `meshes` lists it (triangles, materials, textures, size, joints, clips), `meshes place NAME [CLIP [SECONDS]]`
   puts one in front of the camera, and the log's `Mesh assets:` lines have no Warning or note about it. A
   `ScenarioTests` row finds it in `MeshInstances`, and for a skinned model a test like `FoxModelTests` compares its
   clips with SharpGLTF's own world matrices.

## A sound from a file

1. The files in a folder of the game's (`src/Game/<Folder>/`) (WAV, or Ogg Vorbis; mono or stereo), copied by a csproj item like the mesh
   sample's (`<None Update="Sounds\*.wav;Sounds\*.ogg" CopyToOutputDirectory="PreserveNewest" />`), and a row each in
   `Sounds/SOURCES.md` with its source and licence. A loop names its points in the file: a WAV's `smpl` chunk, or an Ogg
   file's `LOOPSTART` with `LOOPLENGTH` or `LOOPEND`; the file makes the join seamless (`docs/sound-files.md`).
2. Register it in the game's `IRunStartup` by name, as a recipe: `audio.Recipes.Register("clang", new
   FileOneShotRecipe(Path.Combine("Sounds", "clang-1.wav"), ...) { Gain = 0.5f, ReferenceMetres = 2 })` (1 to 16
   files, each a variation, at most 30 s each; `KeepStereo = true` for a stereo sound at the listener), or `new
   FileLoopRecipe(path)` (one file of at most 120 s) for a sound held while sustained (`SampleSounds`).
3. A system finds it once (`audio.Recipes.TryFind`, in `Initialize`) and plays it: `audio.Play(new SoundEvent { Recipe,
   Position, Level })`, or for a loop `audio.Sustain(new SoundLoop { Recipe, Key, Position })` each frame it should
   play (positions local; `SoundLoop.AtListener` or `SoundEvent.AtListener` for no place).
4. Check it: `sound files` lists each file as read, `sound play NAME` plays it at the listener, the log names a file
   that cannot be read at Error, and a test hears it with no device (`AudioService.QueueWithoutThread`, with sound on:
   `QueuedEvents`, `QueuedLoops`; the mesh sample's `SampleSoundSystemTests`) and reads the file
   (`SoundDecoders.Open`: channels, rate, frames, loop points; `SampleSoundsTests`).

## Music

1. The track's file (Ogg Vorbis, or WAV) with the game's sounds (above), its loop named in the file when it has an
   intro; a track loops whole by default.
2. Declare it in the game's `IRunStartup`: `audio.Music.Declare("forest", Path.Combine("Sounds", "forest.ogg"),
   loop: true)` (at most 64, before the audio thread starts; the headers are read then, a bad file logged at Error).
3. Play it from a system or the game's menu: `music.Play("forest")` (cross-fades over 2 s from what plays;
   `fadeSeconds`, and `restart` to play it from its start when it plays already), `music.Stop()`; `music.Current` says
   what was asked for. The engine's menu plays the track `Engine:Audio:MenuMusic` names; a game's own menu plays its own
   (`SampleMenu.FollowMusic`).
4. Check it: `music list`, `music play NAME`, and `music` (the decks' gains, underruns, what the music thread decoded).
