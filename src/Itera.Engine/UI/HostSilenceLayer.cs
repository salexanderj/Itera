using System.Globalization;
using Itera.Engine.Loading;
using Itera.Engine.Replication;
using Itera.Modding;
using SkiaSharp;

namespace Itera.Engine.UI;

/// <summary>
/// On a client, "No data from the host for N s" in the upper middle of the screen while it has read no snapshot for
/// <see cref="ReplicationService.SilenceSeconds"/> or more (<see cref="ReplicationService.SecondsWithoutSnapshots"/>):
/// the host's players and creatures stand still while its own player moves. A match's layer the engine adds after the aim
/// dot, hidden while the loading screen is drawn. Its texts, one for each second up to <see cref="MostSeconds"/>, are made
/// once (<see cref="CentredMessageLayer"/>), so it allocates nothing after its first draw. Main thread.
/// </summary>
public sealed class HostSilenceLayer : IMatchUiLayer, IDisposable
{
    /// <summary>The longest silence counted out; past it the text says it is over that.</summary>
    public const int MostSeconds = 120;

    private static readonly string[] s_texts = Texts();

    private readonly Func<double> _seconds;
    private readonly Func<bool> _loading;
    private readonly CentredMessageLayer _message;

    public HostSilenceLayer(UiFonts fonts, ReplicationService replication, LoadingScreen loading)
        : this(fonts, () => replication.SecondsWithoutSnapshots, () => loading.Drawn) { }

    /// <param name="fonts">The UI's typeface and sizing rule.</param>
    /// <param name="seconds">The seconds since the client last read a snapshot.</param>
    /// <param name="loading">True while the loading screen is drawn.</param>
    internal HostSilenceLayer(UiFonts fonts, Func<double> seconds, Func<bool> loading)
    {
        _seconds = seconds;
        _loading = loading;
        _message = new CentredMessageLayer(fonts, s_texts, Choose, textScale: 1.5f, heightShare: 0.3f);
    }

    /// <summary>The text shown, or null while nothing is.</summary>
    public string? Text => _message.Text;

    public bool Visible => _message.Visible;

    public bool Dirty => _message.Dirty;

    public void Update(in FrameTime time, int width, int height) => _message.Update(in time, width, height);

    public void Update(in FrameTime time) => _message.Update(in time);

    public SKRectI Bounds(int width, int height) => _message.Bounds(width, height);

    public void Draw(SKCanvas canvas, int width, int height) => _message.Draw(canvas, width, height);

    public void Dispose() => _message.Dispose();

    /// <summary>The text of the whole seconds without a snapshot, or none below the silence or while loading.</summary>
    private int Choose()
    {
        var seconds = _seconds();
        if (_loading() || seconds < ReplicationService.SilenceSeconds)
        {
            return CentredMessageLayer.None;
        }

        return (int)Math.Min(seconds, MostSeconds + 1) - ReplicationService.SilenceSeconds;
    }

    private static string[] Texts()
    {
        var first = ReplicationService.SilenceSeconds;
        var texts = new string[MostSeconds - first + 2];
        for (var i = 0; i < texts.Length - 1; i++)
        {
            texts[i] = string.Create(CultureInfo.InvariantCulture, $"No data from the host for {first + i} s");
        }

        texts[^1] = string.Create(CultureInfo.InvariantCulture, $"No data from the host for over {MostSeconds} s");
        return texts;
    }
}
