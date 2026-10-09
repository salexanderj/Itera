namespace Itera.Engine.Rendering;

/// <summary>
/// The images a pass draws with at the current size (<see cref="Current"/>), and the ones it drew with before, each
/// kept until no frame in flight can use it. Images replaced, or no longer used, from the frame of a slot on are
/// destroyed once the frame before (the previous slot's) has passed its fence; that fence also covers every earlier
/// frame. A slot's descriptor sets are pointed at the current images only after that slot's fence
/// (<see cref="SetsNeedWriting"/>). Nothing waits for the device. Render thread only;
/// <see cref="UpscalePass"/> and <see cref="SmaaPass"/> keep theirs in one.
/// </summary>
/// <typeparam name="T">The images of one size, destroyed by their <see cref="IDisposable.Dispose"/>.</typeparam>
internal sealed class FrameImages<T> : IDisposable
    where T : class, IDisposable
{
    // The images each slot's descriptor sets point at, and the images each slot's next fence frees.
    private readonly T?[] _setImages = new T?[FrameSync.FramesInFlight];
    private readonly List<T>[] _retired = [.. Enumerable.Range(0, FrameSync.FramesInFlight).Select(_ => new List<T>())];
    private int _unusedFrames;

    /// <summary>The images the frame being recorded draws with, or null when there are none.</summary>
    public T? Current { get; private set; }

    /// <summary>True while images exist, for this frame or for frames still in flight.</summary>
    public bool HasImages => Current is not null || Array.Exists(_retired, list => list.Count > 0);

    /// <summary>For a frame of <paramref name="slot"/> that draws with the images, after the slot's fence.</summary>
    public void Use(int slot)
    {
        FreeRetired(slot);
        _unusedFrames = 0;
    }

    /// <summary>
    /// For a frame of <paramref name="slot"/> that does not draw with the images, after the slot's fence: after
    /// <paramref name="framesBeforeFree"/> such frames in a row, retires the current images, which the frame before may
    /// still read.
    /// </summary>
    public void NotUsed(int slot, int framesBeforeFree)
    {
        FreeRetired(slot);
        if (Current is not null && ++_unusedFrames >= framesBeforeFree)
        {
            Retire(Current, slot);
            Current = null;
        }
    }

    /// <summary>
    /// Makes <paramref name="images"/> the current images from the frame of <paramref name="slot"/> on; the ones before
    /// are retired.
    /// </summary>
    public void Replace(T images, int slot)
    {
        if (Current is not null)
        {
            Retire(Current, slot);
        }

        Current = images;
    }

    /// <summary>True when <paramref name="slot"/>'s descriptor sets do not point at <see cref="Current"/>.</summary>
    public bool SetsNeedWriting(int slot) => _setImages[slot] != Current;

    /// <summary><paramref name="slot"/>'s descriptor sets now point at <see cref="Current"/>.</summary>
    public void SetsWritten(int slot) => _setImages[slot] = Current;

    /// <summary>Destroys every image, current and retired. The GPU must be done with them. Safe to call more than once.</summary>
    public void Clear()
    {
        if (Current is not null)
        {
            Destroy(Current);
            Current = null;
        }

        foreach (var list in _retired)
        {
            foreach (var images in list)
            {
                Destroy(images);
            }

            list.Clear();
        }
    }

    public void Dispose() => Clear();

    /// <summary>
    /// Keeps <paramref name="images"/>, no longer used from the frame of <paramref name="slot"/> on, until the fence of
    /// the frame before (the previous slot's) has passed; that fence also covers every earlier frame.
    /// </summary>
    private void Retire(T images, int slot)
    {
        _retired[(slot + FrameSync.FramesInFlight - 1) % FrameSync.FramesInFlight].Add(images);
    }

    /// <summary>Destroys the images <paramref name="slot"/>'s fence has released. After that slot's fence wait.</summary>
    private void FreeRetired(int slot)
    {
        foreach (var images in _retired[slot])
        {
            Destroy(images);
        }

        _retired[slot].Clear();
    }

    private void Destroy(T images)
    {
        images.Dispose();
        // A slot whose sets pointed at them points them again before its next use.
        for (var slot = 0; slot < _setImages.Length; slot++)
        {
            if (_setImages[slot] == images)
            {
                _setImages[slot] = null;
            }
        }
    }
}
