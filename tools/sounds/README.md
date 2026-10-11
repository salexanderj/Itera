# Sound files made in code

`make_sounds.py` writes the sound files the engine's tests and the mesh sample's demo read,
from code: sine tones and the engine's own recipes, encoded here, so no recording and no licence. It needs a Python
with numpy, soundfile (which brings libsndfile, with Ogg Vorbis), and mutagen (for the loop tags soundfile cannot
write):

```bash
python3 -m venv /tmp/soundvenv && /tmp/soundvenv/bin/pip install numpy soundfile mutagen
/tmp/soundvenv/bin/python tools/sounds/make_sounds.py fixtures   # tests/Itera.Engine.Tests/Audio/Fixtures/*.ogg
dotnet build samples/Itera.MeshSample
/tmp/soundvenv/bin/python tools/sounds/make_sounds.py demo       # samples/Itera.MeshSample/Sounds/
```

`demo` first runs the mesh sample's Debug build with no graphics and no sound device (in its agent data folder,
`IteraMeshSample-agent`), whose console's `sound wav bar 0.6` and `sound wav tone 0.8` render two of the engine's recipes
into WAV files (a level's variations a file each; 8 s of a stream), then makes the sample's files from them and from
sines: what each is, is in `samples/Itera.MeshSample/Sounds/SOURCES.md`. Its loops are rendered with the repeats before
them, so what follows a loop's end is what follows its start, sample for sample, before Vorbis encodes it.

The files it writes are committed; run it again only to change them. The tests check their pitch, structure, and
lengths, not exact samples, so another build of libsndfile makes files that pass too.
