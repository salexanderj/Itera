"""Makes the sound files the engine's tests read (and, later, the mesh sample's), from code: no recording, no licence.

    python3 -m venv /tmp/soundvenv && /tmp/soundvenv/bin/pip install numpy soundfile mutagen
    /tmp/soundvenv/bin/python tools/sounds/make_sounds.py fixtures

`fixtures` writes three short Ogg Vorbis files into tests/Itera.Engine.Tests/Audio/Fixtures/ (0.5 s at 22,050 Hz,
a few KB each): a mono 440 Hz tone; a stereo tone (440 Hz left, 660 Hz right) tagged LOOPSTART and LOOPLENGTH; and a
mono 330 Hz tone tagged LOOPSTART and LOOPEND. libsndfile (through soundfile) encodes them; mutagen writes the tags,
which soundfile cannot. The tests check their pitch and structure, not exact samples, so another encoder's build
makes files that pass too.
"""

import os
import sys

import numpy as np
import soundfile
from mutagen.oggvorbis import OggVorbis

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
FIXTURES = os.path.join(ROOT, "tests", "Itera.Engine.Tests", "Audio", "Fixtures")
RATE = 22050


def tone(frequency, seconds=0.5, amplitude=0.5):
    """A sine of `frequency` Hz, with 5 ms fades at both ends so it starts and ends quietly."""
    frames = int(seconds * RATE)
    t = np.arange(frames) / RATE
    signal = amplitude * np.sin(2 * np.pi * frequency * t)
    fade = int(0.005 * RATE)
    ramp = np.linspace(0.0, 1.0, fade)
    signal[:fade] *= ramp
    signal[-fade:] *= ramp[::-1]
    return signal.astype(np.float32)


def write_ogg(name, samples, tags=None):
    path = os.path.join(FIXTURES, name)
    soundfile.write(path, samples, RATE, format="OGG", subtype="VORBIS", compression_level=0.9)
    if tags:
        audio = OggVorbis(path)
        for key, value in tags.items():
            audio[key] = str(value)
        audio.save()
    print(f"{path}: {os.path.getsize(path)} bytes")


def fixtures():
    os.makedirs(FIXTURES, exist_ok=True)
    write_ogg("tone-mono.ogg", tone(440))
    stereo = np.stack([tone(440), tone(660)], axis=1)
    write_ogg("tone-stereo-loop.ogg", stereo, {"LOOPSTART": 2205, "LOOPLENGTH": 8820})
    write_ogg("tone-loopend.ogg", tone(330), {"LOOPSTART": 1000, "LOOPEND": 9000})


if __name__ == "__main__":
    commands = {"fixtures": fixtures}
    if len(sys.argv) != 2 or sys.argv[1] not in commands:
        print("usage: make_sounds.py " + "|".join(commands))
        sys.exit(2)
    commands[sys.argv[1]]()
