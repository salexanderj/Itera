"""Makes the sound files the engine's tests and the mesh sample's demo read, from code: no recording, no licence.

    python3 -m venv /tmp/soundvenv && /tmp/soundvenv/bin/pip install numpy soundfile mutagen
    /tmp/soundvenv/bin/python tools/sounds/make_sounds.py fixtures
    dotnet build samples/Itera.MeshSample && /tmp/soundvenv/bin/python tools/sounds/make_sounds.py demo

`fixtures` writes three short Ogg Vorbis files into tests/Itera.Engine.Tests/Audio/Fixtures/ (0.5 s at 22,050 Hz,
a few KB each): a mono 440 Hz tone; a stereo tone (440 Hz left, 660 Hz right) tagged LOOPSTART and LOOPLENGTH; and a
mono 330 Hz tone tagged LOOPSTART and LOOPEND. libsndfile (through soundfile) encodes them; mutagen writes the tags,
which soundfile cannot. The tests check their pitch and structure, not exact samples, so another encoder's build
makes files that pass too.

`demo` writes the mesh sample's sounds into samples/Itera.MeshSample/Sounds/. It first runs the sample's Debug build
with no graphics and no sound device, in its agent data folder, to render two of the engine's recipes with the
console's `sound wav NAME LEVEL` (the struck bar's level 0.6 variations, a file each, and 8 s of the tone at 0.8), then:
clang-1.wav to clang-3.wav, three of the bar's variations (mono, 48 kHz, 16-bit, 2 s with a fade); hum.wav, the tone
with an intro of 0.6 s rising to it, then a loop of 2 s (660 of its cycles, so it joins without a step) that swells
gently, the loop named by a smpl chunk; chime.wav, a bell chord (stereo, 44.1 kHz, 24-bit); menu.ogg, a calm piece in A
minor, an intro of 4 s and its 12 s of chords once through, then those 12 s looping (from 16 s) as LOOPSTART and
LOOPLENGTH name; and yard.ogg, a lighter 16 s piece that loops whole. Each loop is rendered with the repeats before it, so what follows its end is what follows its start,
sample for sample, before encoding.
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


SAMPLE = os.path.join(ROOT, "samples", "Itera.MeshSample")
DEMO = os.path.join(SAMPLE, "Sounds")
MUSIC_RATE = 44100


def data_home():
    """Where the engine keeps its data folders on Linux: XDG_DATA_HOME, else ~/.local/share."""
    return os.environ.get("XDG_DATA_HOME") or os.path.join(os.path.expanduser("~"), ".local", "share")


def render_recipes():
    """The engine's bar (level 0.6, a file a variation) and tone (8 s at 0.8), rendered by the sample's own build."""
    import json
    import shutil
    import subprocess

    with open(os.path.join(SAMPLE, "appsettings.json")) as settings:
        folder = json.load(settings)["Engine"]["Application"]["DataFolder"] + "-agent"
    sounds = os.path.join(data_home(), folder, "Sounds")
    shutil.rmtree(sounds, ignore_errors=True)
    program = os.path.join(SAMPLE, "bin", "Debug", "net10.0", "Itera.MeshSample")
    if not os.path.exists(program):
        sys.exit(f"{program} is missing: run 'dotnet build samples/Itera.MeshSample' first")
    subprocess.run(
        [
            program,
            "--Engine:Window:Mode=None",
            "--Engine:Session:Mode=Offline",
            "--Engine:Steam:Enabled=false",
            "--Engine:Audio:Enabled=false",
            f"--Engine:Application:DataFolder={folder}",
            "--Engine:Console:ExitOnFailure=true",
            "--Engine:Console:Run=sound wav bar 0.6; sound wav tone 0.8; quit",
        ],
        check=True,
        timeout=180,
        stdout=subprocess.DEVNULL,
    )
    return sounds


def read_mono(path):
    samples, rate = soundfile.read(path, dtype="float32")
    assert rate == 48000 and samples.ndim == 1, path
    return samples


def write_wav(name, samples, rate, subtype="PCM_16"):
    path = os.path.join(DEMO, name)
    soundfile.write(path, samples, rate, subtype=subtype)
    print(f"{path}: {os.path.getsize(path)} bytes")
    return path


def write_wav_loop(name, samples, rate, start, end):
    """A mono 16-bit WAV with a smpl chunk naming one forward loop from `start` to just before `end`."""
    import struct

    pcm = np.clip(np.round(samples * 32768), -32768, 32767).astype("<i2").tobytes()
    fmt = struct.pack("<4sIHHIIHH", b"fmt ", 16, 1, 1, rate, rate * 2, 2, 16)
    # Manufacturer, product, sample period (ns), MIDI unity note, pitch fraction, SMPTE format and offset, one loop,
    # no sampler data; then the loop: cue id, forward, start, end (inclusive), fraction, play count (0: for ever).
    smpl = struct.pack("<4sI9I6I", b"smpl", 60, 0, 0, 1_000_000_000 // rate, 60, 0, 0, 0, 1, 0, 0, 0, start, end - 1, 0, 0)
    data = struct.pack("<4sI", b"data", len(pcm)) + pcm
    body = b"WAVE" + fmt + smpl + data
    path = os.path.join(DEMO, name)
    with open(path, "wb") as file:
        file.write(struct.pack("<4sI", b"RIFF", len(body)) + body)
    print(f"{path}: {os.path.getsize(path)} bytes, loop {start} to {end}")


def write_music(name, samples, tags=None):
    path = os.path.join(DEMO, name)
    soundfile.write(path, samples, MUSIC_RATE, format="OGG", subtype="VORBIS", compression_level=0.55)
    if tags:
        audio = OggVorbis(path)
        for key, value in tags.items():
            audio[key] = str(value)
        audio.save()
    print(f"{path}: {os.path.getsize(path)} bytes, {len(samples) / MUSIC_RATE:.1f} s, {tags or 'loops whole'}")


def fade_out(samples, seconds, rate):
    frames = int(seconds * rate)
    samples = samples.copy()
    ramp = np.linspace(1.0, 0.0, frames) ** 2
    samples[-frames:] *= ramp[:, None] if samples.ndim == 2 else ramp
    return samples


def hz(note):
    """A note's frequency from its MIDI number (69 is A4, 440 Hz)."""
    return 440.0 * 2 ** ((note - 69) / 12)


def pan(signal, position):
    """A mono signal placed between left (-1) and right (+1) at equal power."""
    angle = (position + 1) * np.pi / 4
    return np.stack([signal * np.cos(angle), signal * np.sin(angle)], axis=1)


def pluck(frequency, seconds, decay, rate=MUSIC_RATE):
    """A soft plucked note: a sine with two quieter harmonics, each dying faster than the last, struck in 4 ms."""
    t = np.arange(int(seconds * rate)) / rate
    tone = (
        np.sin(2 * np.pi * frequency * t) * np.exp(-t / decay)
        + 0.3 * np.sin(4 * np.pi * frequency * t) * np.exp(-t / (decay * 0.5))
        + 0.1 * np.sin(6 * np.pi * frequency * t) * np.exp(-t / (decay * 0.3))
    )
    return tone * np.minimum(1, t / 0.004)


def pad(frequency, seconds, attack, release, rate=MUSIC_RATE):
    """A held chord tone: soft harmonics (1/n squared), left and right 4 cents apart, swelling in and dying away."""
    length = seconds + release
    t = np.arange(int(length * rate)) / rate
    channels = []
    for cents in (-2, 2):
        f = frequency * 2 ** (cents / 1200)
        channels.append(sum(np.sin(2 * np.pi * f * n * t) / n**2 for n in range(1, 6)))
    envelope = np.minimum(1, t / attack)
    tail = t > seconds
    envelope[tail] *= np.exp(-(t[tail] - seconds) / (release / 4))
    return np.stack(channels, axis=1) * envelope[:, None]


def place(track, sound, at, rate=MUSIC_RATE):
    """Adds `sound` (frames by 2) into `track` from `at` seconds on, cut at its end."""
    start = int(round(at * rate))
    end = min(len(track), start + len(sound))
    track[start:end] += sound[: end - start]


def menu_music():
    """
    A calm A minor piece: an intro of 4 s, then 12 s of Am, F, C, G (3 s each) three times; the file holds the intro and
    the first two, and loops the second (from 16 s), which follows the first as the second follows itself.
    """
    intro, chord, loop = 4.0, 3.0, 12.0
    chords = [(45, [57, 60, 64]), (41, [57, 60, 65]), (48, [55, 60, 64]), (43, [55, 59, 62])]
    pattern = [0, 1, 2, 3, 2, 1, 0, 2]
    track = np.zeros((int((intro + 3 * loop + 4) * MUSIC_RATE), 2))
    # The intro: Am swelling in under four rising notes, gone before the loop starts.
    for tone in chords[0][1]:
        place(track, 0.12 * pad(hz(tone), 2.6, 2.0, 1.2), 0.0)
    for i, note in enumerate([69, 72, 76, 81]):
        place(track, pan(0.16 * pluck(hz(note), 1.8, 0.6), -0.4 + 0.27 * i), 0.4 + 0.55 * i)
    for repeat in range(3):
        for c, (root, tones) in enumerate(chords):
            at = intro + repeat * loop + c * chord
            for tone in tones:
                place(track, 0.12 * pad(hz(tone), chord, 0.8, 1.5), at)
            bass = 0.28 * pluck(hz(root), chord + 0.8, 1.6)
            place(track, pan(bass, 0), at)
            notes = sorted(tones + [tones[0] + 12])
            for step, index in enumerate(pattern):
                note = notes[index] + 12
                place(track, pan(0.11 * pluck(hz(note), 1.5, 0.45), 0.3 if step % 2 else -0.3), at + step * chord / 8)
    start = int((intro + loop) * MUSIC_RATE)
    length = int(loop * MUSIC_RATE)
    piece = track[: start + length]
    return 0.7 * piece / np.max(np.abs(piece)), start, length


def yard_music():
    """A lighter 16 s piece that loops whole: a slow drone on C and G, plucks of the pentatonic scale over it."""
    loop = 16.0
    rng = np.random.default_rng(7)
    scale = [72, 74, 76, 79, 81, 84]
    melody = [(i * 0.5, scale[rng.integers(len(scale))], rng.uniform(-0.5, 0.5)) for i in range(32) if rng.random() < 0.7]
    track = np.zeros((int(3 * loop * MUSIC_RATE), 2))
    t = np.arange(len(track)) / MUSIC_RATE
    for note, level in ((48, 0.09), (55, 0.06)):
        # A whole number of cycles in the loop, so the drone joins at the loop's end without a step.
        f = round(hz(note) * loop) / loop
        swell = 0.75 + 0.25 * np.cos(2 * np.pi * t / (loop / 2))
        drone = level * swell * (np.sin(2 * np.pi * f * t) + 0.3 * np.sin(4 * np.pi * f * t))
        track += np.stack([drone, drone], axis=1)
    for repeat in range(2):
        for at, note, position in melody:
            place(track, pan(0.14 * pluck(hz(note), 2.5, 0.7), position), repeat * loop + at)
        for at, note in ((0, 36), (8, 43)):
            place(track, pan(0.2 * pluck(hz(note), 4.0, 2.0), 0), repeat * loop + at)
    length = int(loop * MUSIC_RATE)
    piece = track[length : 2 * length]
    return 0.7 * piece / np.max(np.abs(piece))


def chime():
    """A bell chord, C6 E6 G6 C7, struck one after another 70 ms apart from left to right; 2 s."""
    rate = MUSIC_RATE
    track = np.zeros((int(2.0 * rate), 2))
    for i, note in enumerate([84, 88, 91, 96]):
        t = np.arange(int(1.9 * rate)) / rate
        f = hz(note)
        bell = sum(level * np.sin(2 * np.pi * f * ratio * t) * np.exp(-t / decay)
                   for ratio, level, decay in ((1, 1, 0.9), (2, 0.35, 0.45), (3, 0.15, 0.25), (4.2, 0.08, 0.15)))
        place(track, pan(bell * np.minimum(1, t / 0.003), -0.6 + 0.4 * i), 0.07 * i, rate)
    return fade_out(0.5 * track / np.max(np.abs(track)), 0.3, rate)


def demo():
    os.makedirs(DEMO, exist_ok=True)
    sounds = render_recipes()
    for i in range(1, 4):
        bar = read_mono(os.path.join(sounds, f"bar-0.6-{i}.wav"))[: 2 * 48000]
        write_wav(f"clang-{i}.wav", fade_out(bar, 0.4, 48000), 48000)
    tone = read_mono(os.path.join(sounds, "tone-0.8.wav"))[48000:]
    intro, loop = 28_800, 96_000
    hum = tone[: intro + loop].copy()
    rise = np.linspace(0.0, 1.0, intro)
    hum[:intro] *= rise * rise * (3 - 2 * rise)
    hum[intro:] *= 1 - 0.12 * (1 - np.cos(2 * np.pi * np.arange(loop) / loop)) / 2
    write_wav_loop("hum.wav", hum, 48000, intro, intro + loop)
    write_wav("chime.wav", chime(), MUSIC_RATE, subtype="PCM_24")
    menu, start, length = menu_music()
    write_music("menu.ogg", menu.astype(np.float32), {"LOOPSTART": start, "LOOPLENGTH": length})
    write_music("yard.ogg", yard_music().astype(np.float32))


if __name__ == "__main__":
    commands = {"fixtures": fixtures, "demo": demo}
    if len(sys.argv) != 2 or sys.argv[1] not in commands:
        print("usage: make_sounds.py " + "|".join(commands))
        sys.exit(2)
    commands[sys.argv[1]]()
