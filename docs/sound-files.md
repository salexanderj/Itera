# Sound files and music: decisions and their reasons

`docs/framework-limits.md` listed "sound is procedural and mono (no file loading, no music path)" in limit 6, and order
of work item 5. On 2026-10-11 the engine gained, in seven phases: readers for WAV and Ogg Vorbis files (18a3cf1), three
volumes on a Sound tab (218095f), sounds read from files and played as one-shots (6ba854a), sounds from files looping
while a game sustains them (bb28a86), music streamed from files with cross-fades and the menu's track (80a92cb), the mesh
sample's sounds and music with the files made in code (9deed02), and these docs. How each part works is in
`.claude/rules/audio.md` and the `scaffold` skill; this file records what was chosen, what else was offered, and why,
with the measurements behind it. Sound made in code is `docs/procedural-audio.md`.

## The round's scope

Lucy's choices:

- All four parts: sounds from files by name, looping sounds from files, music streamed with cross-fades, and loop
  points named in the files.
- Three volumes: the master (Volume, still 10 % by default), Music, and Effects, the two under the master and full by
  default. They are on a Settings tab of their own, Sound, labelled Master Volume, Music Volume, and Effects Volume.
- A declared file that is missing or cannot be read is logged at Error and plays nothing; the run goes on.
- NVorbis 1.0.0-rc.2 for Ogg Vorbis, in place of the 0.10.5 first planned (below).
- The demo in the mesh sample, with files made in this repository, so no licence comes in; she listened before its
  commit and approved it as heard.
- The engine's menu plays the track a game names (`Engine:Audio:MenuMusic`); a game's own menu plays its music itself.
- Tracks are declared by name once a run and played by name, as recipes are.

## What a game plays

- A sound from files is a recipe, so names share one table and `Play`, `Sustain`, and `TryFind` work as before:
  `FileOneShotRecipe(paths)`, 1 to 16 files, each a variation of one level, never the same twice running; and
  `FileLoopRecipe(path)`, one file played while the game sustains it (by recipe and key, in the world or at the
  listener, `SoundLoop.AtListener`). Paths are from the game's folder, as a mesh asset's.
- A stereo file is mixed to mono, (left + right) / 2, and placed in the world, unless its recipe is `KeepStereo`: then it
  keeps both channels and always plays at the listener, where `AL_SOFT_direct_channels_remix` sends them left and right
  as they are (a stereo buffer in OpenAL is otherwise two virtual speakers at 30 degrees, and some of each side reaches
  the other). Only stereo buffers get direct channels, so a mono sound at the listener plays as it did before.
- Music is a track (`MusicPlayer.Declare(name, path, loop)`, at most 64), one heard at a time. `Play` cross-fades from
  what plays; the same track again does nothing unless `restart`; `Stop` fades out. `Current` says which track was
  asked for, and goes back to none when a track played once ends or its file fails; it follows the commands also with
  no device, so a game's logic and its menu's do not depend on one.

## Formats

- WAV, by a reader of the engine's own (`WavReader`, `WavDecoder`): PCM of 8 (unsigned), 16, 24, and 32 bits, IEEE float
  of 32 and 64 bits, the extensible header (its subformat checked), RF64 (`ds64`), chunks in any order (`LIST`, `cue `,
  `bext`, `fact`, `JUNK`, and `id3 ` passed over, odd sizes padded), a data size of 0 or 0xFFFFFFFF read to the file's
  end, and data cut short played as far as it goes with a Warning. A 16-bit file reads back exactly (s / 32768, then
  times 32768).
- Ogg Vorbis through NVorbis (MIT). Opus, FLAC, and MP3 are not read: Vorbis is the format games ship music in that
  has a small managed decoder, and WAV covers what is short.
- Mono and stereo; more channels are refused with the reason.
- A file is opened by its first bytes (`RIFF`, `RF64`, or `OggS`), not its name.

## Loop points

- A WAV file names its loop in a `smpl` chunk (the first forward loop; its end is inclusive, so the engine adds one),
  before or after the data. An Ogg file names it with the tags `LOOPSTART` and `LOOPLENGTH` (as RPG Maker writes them)
  or `LOOPEND`; a start alone loops to the end.
- The part before the loop is heard once, then the loop over and over. With no loop named, a looping sound repeats whole.
- Points that reach past the sound's end, or name no frames, are dropped with a Warning, and the sound loops whole.
- A seamless loop is the file's work: what follows the loop's end must be what follows its start. The mesh sample's files
  are rendered with the repeats before them for that (`tools/sounds/make_sounds.py demo`): the music is rendered three
  times through, and the file holds the intro and the first two passes, looping the second, which follows the first as
  it follows itself; a tone loops on a whole number of its cycles. Vorbis's loss adds a little noise at the join (at
  most 0.03 of a peak of 0.7 over the 2,000 samples before it, in `menu.ogg`), which is not heard.

## How each kind plays, and why

- **Short sounds are read whole.** The sound render thread reads a file one-shot or loop as it renders the recipes
  (`RecipeRenderer`), and the audio thread puts each file into a buffer at its own rate (OpenAL resamples). A file
  one-shot may last 30 s and a loop 120 s; a longer one is refused with "stream it as music". The sound cache keeps
  only what is rendered from recipes: a file is its samples already.
- **16-bit samples.** OpenAL Soft takes float buffers too (`AL_EXT_FLOAT32`), but 16 bits halve the memory, a 16-bit
  file stays exact, and 96 dB of range is more than a game's mix uses.
- **File loops are static looping sources.** A loop's buffer carries its loop points (`AL_SOFT_loop_points`, set before
  any source holds it), and the source loops with `AL_LOOPING`: OpenAL Soft goes round it sample for sample, with no work
  on the audio thread, where a queued stream needs a refill every few milliseconds. A loop that names no points starts
  at a random frame and fades in over 80 ms, so two copies of one ambience are not in step; one with points starts at
  its start at full gain. Out of sustain it fades over 80 ms by the time since the last round, then stops. Like a
  stream, it is never cut short for a louder sound.
- **Music streams, on a thread of its own.** A track may be minutes long, and NVorbis allocates as it decodes (below), so
  the "Music" thread (`MusicStreamer`) decodes ahead into each deck's ring (`MusicFeed`: 8 blocks of 4096 frames in the
  file's channels and rate, tagged by the request; about 0.7 s at 48 kHz, 0.74 s at 44.1 kHz), and the audio thread,
  still the only caller of OpenAL, queues the blocks on its decks (`MusicDecks`: 4 buffers each, about 0.34 s more at 48 kHz, 0.37 s at 44.1 kHz).
  Its stalls up to about 1 s cost nothing heard; a longer one is counted as an underrun.
- **Two decks outside the voice pool.** A cross-fade needs two sources at once, and music must never lose its voice to a
  louder sound. OpenAL Soft makes 256 sources by default, so `Engine:Audio:MaxVoices` is at most 254 (it was 255).
  With no track declared, neither the thread nor the decks start.
- **Equal-power cross-fades.** Each deck is heard at sin(t·π/2) of the music volume as t moves between 0 and 1 over the
  fade, so halfway both are at 0.71 and the sum of their powers stays 1 (a linear fade dips by 3 dB in the middle). A new
  track's fade starts when its first block is queued, not when it was asked for, so a slow file never leaves a gap. A
  command during a fade goes on from the gains then: playing the track that is fading out brings it back in from where
  it is. A third track during a cross-fade cuts the quieter deck at once.
- **Gapless loops by a second decoder.** NVorbis's seek is wrong near a file's start (below), so the engine's seek
  decodes from the start up to the frame, which takes time: a loop's start 3 minutes in would take about 0.5 s, against
  the ring's 0.7 s. A looping deck therefore keeps a second decoder of the file waiting at the loop's start, brought there
  between blocks (eight blocks a round, after the rings are full); at the loop's end the block is filled up to the end
  from one and on from the other, and the two swap. A WAV seeks there at once. A spare that cannot be made ready ends the
  track at the loop's end, logged.
- **Sound off keeps the music going.** The console's `sound off` sets the decks' gains to 0 and the music streams on, so
  it comes back where it would have been.

## Measurements

On the development machine:

- NVorbis decodes at about 350 times real time and allocates about 53 KB a second of 48 kHz stereo (phase 1).
- The mesh sample's run (Debug, 2026-10-11): `yard.ogg`, which loops whole, cost the music thread 1.7 MB for 30 s of
  music, about 57 KB a second; `menu.ogg`, whose loop starts 16 s in, 5.3 MB for 31 s, since its waiting decoder decodes
  the first 16 s again at each pass of the 12 s loop. No underruns. The garbage is the music thread's (how often it
  sets off a collection was not measured).
- An Ogg loop shorter than its start takes to decode (the start at about 350 times real time) underruns: a 0.3 s loop
  after 100 s of start. A game's tracks are minutes with starts of seconds.
- Header reading, on the main thread at the run's "audio thread" step: the sample's two tracks in 21 ms.

## NVorbis 1.0.0-rc.2, not 0.10.5

The plan named NVorbis 0.10.5, the last stable release. Measured in phase 1: its seek lands about 367 frames off and can
throw "GranulePos mismatch". 1.0.0-rc.2 (July 2026, MIT) fixes those, an endless loop, and some allocations; Lucy chose
it, pinned. Its own `SeekTo` still lands on wrong samples within about the first 0.6 to 2 s of a file (exact from the
start and further in), so `VorbisDecoder.Seek` decodes from the start, and the music's loops never seek on the deck
that plays (above). It still allocates per packet, so it runs on the sound render thread and the music thread, never on
the audio thread.

## The volumes

Master stays the listener's gain; Music and Effects are under it. Effects is every voice of the pool (recipes, sounds
from files, the menu's clicks), set on each source (`SourceGain`: its gain, a file loop's fade, the effects' volume), so
a moved origin or a changed volume never undoes a fade; Music is each deck's gain times its fade. The player's settings
keep them (`Volume`, `MusicVolume`, `EffectsVolume` in `settings.json`), and the console's `settings` sets them.

## For agents and tests

- `sound files` lists each sound from files as read (channels, rate, length, size, loop, problem); `sound play NAME`
  plays any sound at the listener (a sustained one for 4 s); `music` shows the track, each deck's track and gain,
  underruns, and what the music thread decoded and allocated; `music list`, `music play NAME [SECONDS]`, and
  `music stop [SECONDS]`. `sound wav NAME LEVEL` writes a recipe's level as files, the material for a game's own files
  made from the engine's recipes.
- The engine's tests run the mixer, the decks, and the music thread's work round by round on an in-memory device
  (`FakeAudioDevice`, which can keep every sample queued on a source) and in-memory files (`FakeDecoder`, whose samples
  say which frame they came from), so a loop's join is checked frame for frame; a WAV's and an Ogg file's loops through
  the streamer are checked against the files decoded whole. Loopback tests on OpenAL Soft check stereo kept apart (a
  buffer and a deck's queue), a loop's intro heard once, and gains above 1.
- A game's tests hear its sounds with no device through `AudioService.QueueWithoutThread` (with sound on), reading
  `QueuedEvents` and `QueuedLoops` until the next `Publish`, and a `MusicPlayer` with no thread says what it was asked
  to play (the mesh sample's `SampleSoundSystemTests`, `SampleSoundsTests`, and `SampleMenuTests`).

## Left for later

Filters by distance and occlusion (OpenAL's EFX), reverb by place, Doppler, voices from recordings, playlists, a track's
own volume, ducking the music under other sounds, sounds from files past 120 s that are not music, more than two
channels, and Opus, FLAC, or MP3 files.
