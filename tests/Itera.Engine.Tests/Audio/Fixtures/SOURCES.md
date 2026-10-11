# Sound file fixtures

Made in this repository by `tools/sounds/make_sounds.py fixtures` (sine tones written by numpy, encoded by libsndfile
through soundfile, tagged by mutagen): no recording, no licence. Each is 0.5 s at 22,050 Hz.

| File | Channels | What it holds | Tags |
|---|---|---|---|
| `tone-mono.ogg` | 1 | 440 Hz | none |
| `tone-stereo-loop.ogg` | 2 | 440 Hz left, 660 Hz right | `LOOPSTART=2205`, `LOOPLENGTH=8820` |
| `tone-loopend.ogg` | 1 | 330 Hz | `LOOPSTART=1000`, `LOOPEND=9000` |
