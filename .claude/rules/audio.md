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
(`AudioService.Sustain`, by recipe and key) and fades out over 80 ms after. Game code queues with `AudioService.Play`
and `Sustain`; after each main-loop iteration `Publish` hands them and the camera, as the listener, to the audio thread
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

### Files in `src/Itera.Engine`

  - `Audio/`: `AudioService`, `AudioMixer`, `AudioExchange`, `AudioStats`, `SoundRecipe(s)`, `SoundEvent`,
    `SoundLoop`, `EngineSounds`, `WavWriter`, `SoundCache`; building blocks in `Audio/Synthesis/` (`SoundRandom`,
    `Dsp`, `OnePole`, `StateVariableFilter`, `NoiseSource`, `ModalBank`); the device (`IAudioDevice`,
    `OpenAlAudioDevice`, `NullAudioDevice`).

### Package notes

- OpenAL comes from `Silk.NET.OpenAL.Soft.Native`; when it cannot load, `AudioDeviceFactory` falls
  back to `NullAudioDevice` with a warning.
