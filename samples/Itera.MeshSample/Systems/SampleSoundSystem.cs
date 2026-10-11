using System.Numerics;
using Arch.Core;
using Arch.System;
using Itera.Engine.Audio;
using Itera.Engine.Ecs;
using Itera.Engine.Loading;
using Itera.Engine.Maths;
using Itera.Modding;

namespace Itera.MeshSample.Systems;

/// <summary>
/// The sample's sounds (frame group, after <see cref="AimHighlightSystem"/>, whose aim its clang follows): the yard's
/// music, cross-fading from the menu's as the match starts; the yard screen's hum, held at the middle of its picture; a
/// clang at a block's middle as the player's aim comes onto it (<see cref="AimHighlight"/>); and a chime at the listener
/// once the loading screen no longer covers the picture (and again whenever it has covered it once more: a game with
/// voxel terrain covers it after a reset; the sample has none). Reads and writes no components.
/// </summary>
public sealed class SampleSoundSystem(
    World world,
    AudioService audio,
    AimHighlight highlight,
    LoadingScreen loading,
    WorldOrigin origin
) : BaseSystem<World, FrameTime>(world), IFrameUpdateSystem
{
    /// <summary>The middle of the yard screen's picture, where its hum comes from.</summary>
    public static WorldPoint Speaker => Yard.ScreenMiddle;

    private int _clang = -1;
    private int _hum = -1;
    private int _chime = -1;
    private int _yard = -1;
    private bool _started;
    private bool _chimed;
    private bool _aimed;
    private WorldPoint _block;

    public override void Initialize()
    {
        _clang = Find(SampleSounds.Clang);
        _hum = Find(SampleSounds.Hum);
        _chime = Find(SampleSounds.Chime);
        if (!audio.Music.TryFind(SampleSounds.YardMusic, out _yard))
        {
            throw new InvalidOperationException($"No music track is named {SampleSounds.YardMusic}.");
        }
    }

    public override void Update(in FrameTime frame)
    {
        if (!_started)
        {
            _started = true;
            audio.Music.Play(_yard);
        }

        audio.Sustain(
            new SoundLoop
            {
                Recipe = _hum,
                Key = 1,
                Position = origin.ToLocal(Speaker),
                Level = 1,
            }
        );
        if (loading.Active)
        {
            _chimed = false;
        }
        else if (!_chimed)
        {
            _chimed = true;
            audio.Play(new SoundEvent { Recipe = _chime, AtListener = true });
        }

        if (highlight.Active && (!_aimed || highlight.Block != _block))
        {
            // The rim's box is a unit cube over the block: its middle is the block's.
            audio.Play(
                new SoundEvent
                {
                    Recipe = _clang,
                    Position = Vector3.Transform(new Vector3(0.5f), highlight.Transform),
                    Level = 1,
                }
            );
            _block = highlight.Block;
        }

        _aimed = highlight.Active;
    }

    private int Find(string name) =>
        audio.Recipes.TryFind(name, out var recipe)
            ? recipe
            : throw new InvalidOperationException($"No sound recipe is named {name}.");
}
