using Itera.Engine.Voxels;

namespace Itera.Engine.Loading;

/// <summary>
/// A loading task that is done when every model in a list is on the GPU (<see cref="VoxelModels.IsUploaded"/>). The list
/// comes from a callback read at each check, so a game can build its models after the screen starts: until the list
/// holds the models, the task waits for the ones it holds, and an empty list is done. A game registers one for each
/// group of models it wants uploaded before play.
/// </summary>
public sealed class ModelsLoadingTask : ILoadingTask
{
    private readonly string _name;
    private readonly VoxelModels _models;
    private readonly Func<IReadOnlyList<int>> _handles;

    /// <param name="name">What the loading screen calls it: one or two short words.</param>
    /// <param name="models">The models' table.</param>
    /// <param name="handles">The model handles to wait for. Read once per check; it must not allocate.</param>
    public ModelsLoadingTask(string name, VoxelModels models, Func<IReadOnlyList<int>> handles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(handles);
        _name = name;
        _models = models;
        _handles = handles;
    }

    /// <param name="name">What the loading screen calls it: one or two short words.</param>
    /// <param name="models">The models' table.</param>
    /// <param name="handles">The model handles to wait for.</param>
    public ModelsLoadingTask(string name, VoxelModels models, IReadOnlyList<int> handles)
        : this(name, models, () => handles) { }

    public string Name => _name;

    public LoadingProgress Progress
    {
        get
        {
            var handles = _handles();
            var count = handles.Count;
            var done = 0;
            for (var i = 0; i < count; i++)
            {
                if (_models.IsUploaded(handles[i]))
                {
                    done++;
                }
            }

            return LoadingProgress.Part(done == count, count == 0 ? 0 : done / (float)count);
        }
    }
}
