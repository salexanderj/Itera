# Sound files made in code

`make_sounds.py` writes the sound files the engine's tests read (and, from the mesh sample's demo on, the sample's),
from code: sine tones and the engine's own recipes, encoded here, so no recording and no licence. It needs a Python
with numpy, soundfile (which brings libsndfile, with Ogg Vorbis), and mutagen (for the loop tags soundfile cannot
write):

```bash
python3 -m venv /tmp/soundvenv && /tmp/soundvenv/bin/pip install numpy soundfile mutagen
/tmp/soundvenv/bin/python tools/sounds/make_sounds.py fixtures   # tests/Itera.Engine.Tests/Audio/Fixtures/*.ogg
```

The files it writes are committed; run it again only to change them. The tests check their pitch and structure, not
exact samples, so another build of libsndfile makes files that pass too.
