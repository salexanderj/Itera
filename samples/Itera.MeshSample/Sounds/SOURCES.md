# The sample's sounds

Every file here is made in code by `tools/sounds/make_sounds.py demo` (its README): no recording and no one else's
work, so no licence but the repository's. Run it again only to change them.

| File | What it is | Made from |
| --- | --- | --- |
| `clang-1.wav` to `clang-3.wav` | A struck metal bar, three variations; mono, 48 kHz, 16-bit, 2 s (the last 0.4 s faded) | The engine's `bar` recipe at level 0.6 (`sound wav bar 0.6`, variations 1 to 3) |
| `hum.wav` | The yard screen's hum: 0.6 s rising, then a loop of 2 s (660 cycles of 330 Hz, swelling 12 % once a loop) named by a `smpl` chunk; mono, 48 kHz, 16-bit | The engine's `tone` recipe at level 0.8 (`sound wav tone 0.8`) |
| `chime.wav` | A bell chord, C6 E6 G6 C7, struck 70 ms apart from left to right; stereo, 44.1 kHz, 24-bit, 2 s | Sines with a bell's partials (1, 2, 3, and 4.2 times each note) |
| `menu.ogg` | A calm piece in A minor: 4 s of intro and the 12 s of Am, F, C, G once through, heard once, then those 12 s looping (`LOOPSTART` 705600, 16 s; `LOOPLENGTH` 529200); stereo, 44.1 kHz, Ogg Vorbis | Pads, a bass, and plucked arpeggios of sines |
| `yard.ogg` | A lighter piece of 16 s that loops whole: a drone on C and G under plucks of the pentatonic scale; stereo, 44.1 kHz, Ogg Vorbis | Sines, the melody from a fixed seed |
