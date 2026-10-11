---
paths:
  - "src/Itera.Engine/Audio/**"
  - "tests/Itera.Engine.Tests/Audio/**"
---

## Sound

Sound (`Engine:Audio`, `AudioService`, `docs/procedural-audio.md`) is made in code: recipes by name (`SoundRecipes`)
model each sound's physics, driven by a level from 0 to 1. A one-shot recipe (`OneShotRecipe`) is rendered at start as 4
levels of 4 variations (by default) into OpenAL buffers, and an event plays the nearest level's (never the same
variation twice running). A stream recipe (`StreamRecipe`) runs live while the game sustains it each iteration
(`AudioService.Sustain`, by recipe and key; in the world, or at the listener with `SoundLoop.AtListener`) and fades out
over 80 ms after. Game code queues with `AudioService.Play`
and `Sustain`; after each main-loop iteration `Publish` hands them, the camera as the listener, and the volumes
(`MixLevels`: the master as the listener's gain, the effects' under it on every voice of the pool, music's for music;
the player's `Volume`, `EffectsVolume`, and `MusicVolume`) to the audio thread
(`AudioMixer`), which alone calls OpenAL: 64 voices (`Engine:Audio:MaxVoices`), each up to a gain of 4
(`AudioMixer.MaxGain`), sounds under 0.002 of gain never started, a louder sound taking the quietest one-shot's voice. A
render thread (`RecipeRenderer`) renders the one-shots at start, while the audio thread plays those ready; a recipe or
stream that throws stays silent alone. `SoundCache/` in the data folder keeps the rendered variations
(`Engine:Audio:Cache`), keyed by the sound code: a hash of the sources under the engine's `Audio/` folder and a game's
(a game with recipes of its own sets `<SoundCodeFolder>Audio</SoundCodeFolder>` in its project), written by the build
(`Directory.Build.targets`), so a later start reads them instead and only a change to sound code renders them again. The
engine's recipes are `bar`, `tone`, `menu-hover`, and `menu-press` (`EngineSounds`, played at the listener:
`SoundEvent.AtListener`). A game registers its own recipes once a run (an `IRunStartup`, before the audio thread takes
them) and plays them from its systems; `docs/procedural-audio.md` has the methods.

A sound may be read from files instead (`FileRecipe`): a game registers a `FileOneShotRecipe(paths)` by name as it
registers a recipe (paths from the game's folder; 1 to 16 files, each a variation, never the same twice running; each
at most 30 s) and plays it with `Play` as any one-shot. The render thread reads its files whole after the recipes
before it (`RecipeRenderer.Read`) and the audio thread puts each into a buffer at its own rate (OpenAL resamples). A
stereo file is mixed to mono and placed in the world, unless the recipe is `KeepStereo`: then it keeps its channels and
always plays at the listener, where `AL_SOFT_direct_channels_remix` sends them left and right as they are. A file that
cannot be read is logged at Error (`Audio: sound NAME: the file PATH cannot be read (WHY); it plays nothing`) and the
next good file plays in its place; with none the sound is silent, and still counts as ready for the loading screen
(`SoundRecipes.PreparedCount`: one-shot recipes and sounds from files; at most 256 recipes in all). With no device, a
missing file is still named at Error. A `FileLoopRecipe(path)` (one file of at most 120 s) plays while the game
sustains it, as a stream recipe does (`Sustain`, by recipe and key; in the world, or at the listener): its voice loops a
static buffer (`IAudioDevice.PlayLooping`), round the loop points its file names (set on the buffer with
`AL_SOFT_loop_points`, so the part before them is heard once), or the whole file from a random frame, fading in, so two
copies are not in step; it fades out by the time since the last round over 80 ms once no longer sustained
(`AudioMixer.FadeFileLoops`, through `IAudioDevice.SetGain`), and, as a stream, is never cut short for a louder sound.
The console's `sound files` lists each file as read (channels, rate, length, size, loop, problem), `sound play NAME`
plays a sound at the listener (a sustained one for 4 s), and `sound wav` writes no file for them; `sound wav NAME LEVEL`
writes a recipe's level as material for files (`AudioService.WriteLevel`: the variations a file each, or 8 s of a
stream; `tools/sounds/make_sounds.py demo` makes the mesh sample's files from them). A game's tests hear its sounds with
no device through `AudioService.QueueWithoutThread` (sound on), reading `QueuedEvents` and `QueuedLoops` until the next
`Publish` (the mesh sample's `SampleSoundSystemTests`).

Music (`AudioService.Music`, a `MusicPlayer`, also a DI singleton): a game declares its tracks by name once a run
(`Declare(name, path, loop)`, at most 64, in its `IRunStartup`; paths from the game's folder) and plays one at a time
(`Play(name or number, fadeSeconds = 2, restart)`, `Stop(fadeSeconds)`; `Current`, -1 for none and again once a track
played once ends or its file fails; the same track again does nothing unless `restart`). Each file's header is read at
the audio thread's step, also with no device: one that cannot be read is logged at Error and plays nothing (playing it
fades out what plays). The commands go to the audio thread with the sounds (`MusicExchange`; the last 8 kept). A thread
of its own, "Music" (`MusicStreamer`), opens the files and decodes ahead into each deck's ring (`MusicFeed`: 8 blocks
of 4096 frames as 16 bits, in the file's channels and rate, tagged by the request's generation); the audio thread plays
two decks (`MusicDecks`: a source each outside the voice pool, so `Engine:Audio:MaxVoices` is at most 254, with 4
buffers queued, at the listener, a stereo track's channels direct through `IAudioDevice.SetDirectChannels`). A track
played starts on a free deck (or the quieter, cut) and once its first block is queued fades in as the other fades out,
each at sin(t·π/2) of the music volume as t moves over the fade (0.71 each halfway); a command during a fade goes on
from the gains then. A looping track goes round its loop points (or whole) with no gap: read up to the loop's end, the
rest of that block comes from a second decoder of the file waiting at the loop's start (brought there between blocks,
since NVorbis seeks by decoding from the start; a WAV seeks at once), and the two swap; one that cannot be readied ends
the track at the loop's end. An Ogg loop shorter than its start takes to decode (the start at about 350 times real time:
a 0.3 s loop after 100 s) underruns. Sound off and the music volume
set the decks' gains (0 while off); the music streams on. A file failing as it plays is logged once and its deck ends;
the old track still fades out; should the music thread itself fail, it logs why and the music stops. The engine's menu plays `Engine:Audio:MenuMusic`'s track (`MusicPlayer.MenuTrack`; a
name no game declared is logged once) while there is no match or one is being joined, and stops it as a match starts unless
the game played another. The console's `music` shows what plays, the decks, underruns, and what the music thread
decoded and allocated; `music list`, `play NAME [SECONDS]`, `stop [SECONDS]`. With no tracks, neither the music thread
nor the decks start.

Sound files are read by `Audio/Files/`: `SoundDecoders.Open` picks a decoder by the file's
first bytes, the engine's own `WavDecoder` (`WavReader`: PCM of 8 to 32 bits, IEEE float of 32 or 64, extensible
formats, RF64, a `smpl` loop) or `VorbisDecoder` (NVorbis 1.0.0-rc.2; loops from `LOOPSTART` with `LOOPLENGTH` or
`LOOPEND`), mono or stereo, each as an `ISoundDecoder` of floats; `SoundFiles.Decode` reads one whole into 16-bit
samples (stereo kept or mixed to mono), refusing one longer than it is given. NVorbis's own seek lands on wrong samples
near a file's start, so `VorbisDecoder.Seek` decodes from the start up to the frame. A file that cannot be read throws
`SoundFileException` saying why. The tests' Ogg files are made by `tools/sounds/make_sounds.py`.

Positions are local to the match's origin. When it moves, `OriginShifts` calls `AudioService.Shift`: the events
queued this iteration, the loops sustained and held, and what waits in the exchange (events, loops, listener) move the
other way, and the exchange keeps the move for the audio thread's next take, which moves the sounds playing in the
world, one-shots and streams (a stream fading after its last round too; `AudioMixer`, whose voices remember what their
sources were set with). Sounds at the listener stay.

### Files in `src/Itera.Engine`

  - `Audio/`: `AudioService`, `AudioMixer`, `AudioExchange`, `AudioStats`, `SoundRecipe(s)`, `SoundEvent`,
    `SoundLoop`, `EngineSounds`, `WavWriter`, `SoundCache`; the music (`MusicPlayer`, `MusicTrack`, `MusicExchange`,
    `MusicFeed`, `MusicStreamer`, `MusicDecks`); sound files in `Audio/Files/` (`ISoundDecoder`,
    `SoundDecoders`, `WavReader`, `WavDecoder`, `VorbisDecoder`, `SoundFiles`, `LoopPoints`, `SoundFileException`);
    building blocks in `Audio/Synthesis/` (`SoundRandom`,
    `Dsp`, `OnePole`, `StateVariableFilter`, `NoiseSource`, `ModalBank`); the device (`IAudioDevice`,
    `OpenAlAudioDevice`, `NullAudioDevice`).

### Package notes

- OpenAL comes from `Silk.NET.OpenAL.Soft.Native`; when it cannot load, `AudioDeviceFactory` falls
  back to `NullAudioDevice` with a warning.
- NVorbis 1.0.0-rc.2 (MIT), a release candidate pinned for its fixes over 0.10.5 (whose seeks land off and can throw
  "GranulePos mismatch"). It allocates while it decodes and its `SeekTo` is wrong near a file's start (exact from 0).
