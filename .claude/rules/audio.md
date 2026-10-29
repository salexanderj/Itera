---
paths:
  - "src/Itera.Engine/Audio/**"
  - "tests/Itera.Engine.Tests/Audio/**"
---

## Sound

Sound (`Engine:Audio`, `AudioService`) is made in code or read from files. `docs/procedural-audio.md` has the synthesis methods, and `docs/sound-files.md` has files and music.

### Recipes and the mixer

Recipes by name (`SoundRecipes`) model each sound's physics, driven by a level from 0 to 1.

- A one-shot recipe (`OneShotRecipe`) is rendered at start as several levels with several variations each, into OpenAL buffers. An event plays the nearest level's buffer, and never the same variation twice running.
- A stream recipe (`StreamRecipe`) runs live while the game sustains it each iteration (`AudioService.Sustain`, by recipe and key). It sounds in the world, or at the listener with `SoundLoop.AtListener`. It fades out after the game stops sustaining it.
- The engine's recipes are `bar`, `tone`, `menu-hover`, and `menu-press` (`EngineSounds`), played at the listener (`SoundEvent.AtListener`).
- A game registers its own recipes once a run, in an `IRunStartup`, before the audio thread takes them. It plays them from its systems.

Threads and queues:

- Game code queues with `AudioService.Play` and `Sustain`.
- After each main-loop iteration, `Publish` hands the queue to the audio thread (`AudioMixer`). It also hands over the camera as the listener and the volumes (`MixLevels`).
- The master volume is the listener's gain. The effects volume sits under it on every voice of the pool. The music volume applies to music. They come from the player's `Volume`, `EffectsVolume`, and `MusicVolume`.
- The audio thread alone calls OpenAL. It plays `Engine:Audio:MaxVoices` voices, each up to a gain of `AudioMixer.MaxGain`. A sound under a minimum gain never starts, and a louder sound takes the quietest one-shot's voice.
- A render thread (`RecipeRenderer`) renders the one-shots at start, while the audio thread plays those ready.
- A recipe or stream that throws stays silent alone.

`SoundCache/` in the data folder keeps the rendered variations (`Engine:Audio:Cache`), keyed by the sound code. The sound code is a hash of the sources under the engine's `Audio/` folder and a game's. A game with recipes of its own sets `<SoundCodeFolder>Audio</SoundCodeFolder>` in its project. The build writes the key (`Directory.Build.targets`). A later start reads the cache, and only a change to sound code renders again.

### Sounds from files

A game registers a `FileOneShotRecipe(paths)` by name as it registers a recipe, and plays it with `Play` as any one-shot.

- The paths are from the game's folder. Each file is a variation, and the same one never plays twice running. A recipe has a limited number of files, each limited in length.
- The render thread reads the files whole after the recipes before it (`RecipeRenderer.Read`). The audio thread puts each into a buffer at its own rate, and OpenAL resamples.
- A stereo file is mixed to mono and placed in the world, unless the recipe is `KeepStereo`. A `KeepStereo` sound keeps its channels and always plays at the listener, where `AL_SOFT_direct_channels_remix` sends them left and right as they are.
- A file that cannot be read is logged at Error, and the next good file plays in its place. With no good file the sound is silent, and it still counts as ready for the loading screen (`SoundRecipes.PreparedCount` counts one-shot recipes and sounds from files). The registry limits the recipes in all.
- With no device, a missing file is still named at Error.

A `FileLoopRecipe(path)` is one file that plays while the game sustains it, as a stream recipe does (`Sustain`, by recipe and key; in the world, or at the listener).

- Its voice loops a static buffer (`IAudioDevice.PlayLooping`). The loop runs round the loop points the file names, set on the buffer with `AL_SOFT_loop_points`, so the part before them is heard once. Without loop points it loops the whole file from a random frame, fading in, so two copies are not in step.
- Once the game stops sustaining it, it fades out by the time since the last round (`AudioMixer.FadeFileLoops`, through `IAudioDevice.SetGain`).
- As a stream, it is never cut short for a louder sound.

Console and tests:

- `sound files` lists each file as read: channels, rate, length, size, loop, and problem.
- `sound play NAME` plays a sound at the listener, a sustained one for a few seconds.
- `sound wav NAME [LEVEL]` writes a recipe's level as material for files (`AudioService.WriteLevel`), and `sound wav all` every recipe's. It writes nothing for a recipe read from files. For other recipes it writes each variation as a file, or a stretch of a stream. `tools/sounds/make_sounds.py demo` makes the mesh sample's files from them.
- A game's tests hear its sounds with no device through `AudioService.QueueWithoutThread` (sound on). They read `QueuedEvents` and `QueuedLoops` until the next `Publish`. The mesh sample's `SampleSoundSystemTests` is an example.

### Reading sound files

`Audio/Files/` reads sound files.

- `SoundDecoders.Open` picks a decoder by the file's first bytes. Both decoders are `ISoundDecoder`s of floats, for mono or stereo.
- `WavDecoder` is the engine's own, over `WavReader`. `WavReader` reads PCM of 8 to 32 bits and IEEE float of 32 or 64 bits, extensible formats, RF64, and a `smpl` loop.
- `VorbisDecoder` uses NVorbis. Loops come from `LOOPSTART` with `LOOPLENGTH` or `LOOPEND`.
- `SoundFiles.Decode` reads one file whole into 16-bit samples, stereo kept or mixed to mono. It refuses one longer than it is given.
- NVorbis's own seek lands on wrong samples near a file's start, so `VorbisDecoder.Seek` decodes from the start up to the frame.
- A file that cannot be read throws `SoundFileException`, which says why.
- The tests' Ogg files are made by `tools/sounds/make_sounds.py`.

### Music

`AudioService.Music` is a `MusicPlayer`, also a DI singleton.

- A game declares its tracks by name once a run, in its `IRunStartup`: `Declare(name, path, loop)`. Paths are from the game's folder. The player limits the tracks.
- The game plays one track at a time: `Play(name or number, fadeSeconds, restart)` and `Stop(fadeSeconds)`. Playing the same track again does nothing unless `restart` is set.
- `Current` is -1 for none, and again once a track played once ends or its file fails.
- The audio thread's step reads each file's header, also with no device. A file that cannot be read is logged at Error and plays nothing. Playing it fades out what plays.
- The commands go to the audio thread with the sounds (`MusicExchange`), which keeps the last few.
- A thread of its own, "Music" (`MusicStreamer`), opens the files and decodes ahead into each deck's ring (`MusicFeed`, tagged by the request's generation). `MusicFeed` holds 16-bit blocks in the file's channels and rate.
- The audio thread plays two decks (`MusicDecks`). A deck has a source outside the voice pool, so the voices and the decks together stay within OpenAL's sources, and `Engine:Audio:MaxVoices` is limited to match. Decks play at the listener. A stereo track's channels go direct through `IAudioDevice.SetDirectChannels`.
- A track played starts on a free deck, or on the quieter one, which is cut. Once its first block is queued, it fades in as the other deck fades out, on an equal-power curve of the music volume. A command during a fade goes on from the gains then.
- A looping track goes round its loop points, or the whole file, with no gap. The player reads up to the loop's end. The rest of that block comes from a second decoder of the file, waiting at the loop's start, and the two swap. The second decoder is brought to the loop's start between blocks, because NVorbis seeks by decoding from the start; a WAV seeks at once. A loop that cannot be readied ends the track at the loop's end.
- An Ogg loop shorter than its start takes to decode underruns.
- Sound off and the music volume set the decks' gains, which are 0 while off. The music streams on.
- A file failing as it plays is logged once, and its deck ends. The old track still fades out. Should the music thread itself fail, it logs why and the music stops.
- The engine's menu plays the track `Engine:Audio:MenuMusic` names (`MusicPlayer.MenuTrack`; a name no game declared is logged once). It plays while there is no match or one is being joined. It stops as a match starts, unless the game played another.
- With no tracks, neither the music thread nor the decks start.
- The console's `music` shows what plays, the decks, underruns, and what the music thread decoded and allocated. `music list`, `music play NAME [SECONDS]`, and `music stop [SECONDS]` control it.

### Origin shifts

Sound positions are local to the match's origin. When the origin moves, `OriginShifts` calls `AudioService.Shift`.

- The events queued this iteration, the loops sustained and held, and what waits in the exchange (events, loops, listener) move the other way.
- The exchange keeps the move for the audio thread's next take. That take moves the sounds playing in the world: one-shots and streams, including a stream fading after its last round (`AudioMixer`, whose voices remember what their sources were set with).
- Sounds at the listener stay.

### Tests

`AudioMixerTests`, `AudioExchangeTests`, `AudioServiceTests`, `SynthesisTests`, `SoundCacheTests`, `WavReaderTests`, `VorbisDecoderTests`, `SoundFilesTests`, `FileSoundTests`, `FileLoopTests`, `MusicTests`, `OpenAlLoopbackTests`.

### Files in `src/Itera.Engine`

- `Audio/`: `AudioService`, `AudioMixer`, `AudioExchange`, `AudioStats`, `SoundRecipe(s)`, `SoundEvent`, `SoundLoop`, `EngineSounds`, `WavWriter`, `SoundCache`.
- Music: `MusicPlayer`, `MusicTrack`, `MusicExchange`, `MusicFeed`, `MusicStreamer`, `MusicDecks`.
- `Audio/Files/`: `ISoundDecoder`, `SoundDecoders`, `WavReader`, `WavDecoder`, `VorbisDecoder`, `SoundFiles`, `LoopPoints`, `SoundFileException`.
- `Audio/Synthesis/`: `SoundRandom`, `Dsp`, `OnePole`, `StateVariableFilter`, `NoiseSource`, `ModalBank`, `GrainCloud`.
- The device: `IAudioDevice`, `OpenAlAudioDevice`, `NullAudioDevice`.

### Package notes

- OpenAL comes from `Silk.NET.OpenAL.Soft.Native`. When it cannot load, `AudioDeviceFactory` falls back to `NullAudioDevice` with a warning.
- NVorbis is a release candidate. It allocates while it decodes, and its `SeekTo` is wrong near a file's start.
