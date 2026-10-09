using System.Numerics;
using Itera.Engine.Audio;

namespace Itera.Engine.Tests.Audio;

/// <summary>One xUnit collection for the real OpenAL device's tests: OpenAL's current context belongs to the process.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OpenAlCollectionDefinition
{
    public const string Name = "OpenAL";
}

/// <summary>
/// <see cref="OpenAlAudioDevice"/> on OpenAL Soft's loopback device, which mixes into memory
/// (the real device had no test): a source plays its gain above 1 up to
/// <see cref="AudioMixer.MaxGain"/> at the listener (OpenAL held it at 1); and an error a
/// call raised is kept for <see cref="IAudioDevice.TakeError"/>, not thrown by the next buffer made.
/// </summary>
[Collection(OpenAlCollectionDefinition.Name)]
public sealed class OpenAlLoopbackTests
{
    private const int Rate = 48_000;

    [Fact]
    public void A_gain_above_one_is_heard_at_the_listener_up_to_the_most()
    {
        using var device = OpenAlAudioDevice.OpenLoopback(Rate);
        Assert.NotNull(device);
        var buffer = device.CreateBuffer();
        var tone = new short[Rate];
        Array.Fill(tone, (short)3277);
        device.FillBuffer(buffer, tone, Rate);
        var source = device.CreateSource();
        device.SetListener(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY, 1);

        var one = Level(device, source, buffer, 1);
        var two = Level(device, source, buffer, 2);
        var six = Level(device, source, buffer, 6);

        Assert.InRange(one, 0.05f, 0.15f);
        Assert.InRange(two / one, 1.9f, 2.1f);
        Assert.InRange(six / one, AudioMixer.MaxGain * 0.95f, AudioMixer.MaxGain * 1.05f);
        Assert.Equal(0, device.TakeError());
        device.DeleteSource(source);
        device.DeleteBuffer(buffer);
    }

    [Fact]
    public void An_error_is_kept_for_take_error_and_does_not_fail_the_next_buffer()
    {
        using var device = OpenAlAudioDevice.OpenLoopback(Rate);
        Assert.NotNull(device);

        // A source that does not exist: AL_INVALID_NAME, latched until read.
        device.SetSource(12_345, Vector3.Zero, 1, 1, 1, atListener: false);
        var buffer = device.CreateBuffer();

        Assert.NotEqual(0, device.TakeError());
        Assert.Equal(0, device.TakeError());
        device.DeleteBuffer(buffer);
    }

    /// <summary>A device with its output reports it there (ALC_CONNECTED is read through OpenAL).</summary>
    [Fact]
    public void A_device_with_its_output_keeps_it()
    {
        using var device = OpenAlAudioDevice.OpenLoopback(Rate);
        Assert.NotNull(device);

        Assert.True(device.KeepConnected());
        Assert.Equal(0, device.TakeError());
    }

    /// <summary>The mean size of the samples a source at the listener plays at <paramref name="gain"/>, once settled.</summary>
    private static float Level(OpenAlAudioDevice device, uint source, uint buffer, float gain)
    {
        device.StopSource(source);
        device.SetSource(source, Vector3.Zero, gain, 1, 1, atListener: true);
        device.PlayBuffer(source, buffer);
        var output = new float[4096];
        device.RenderLoopback(output);
        var sum = 0f;
        for (var i = 2048; i < output.Length; i++)
        {
            sum += MathF.Abs(output[i]);
        }

        return sum / (output.Length - 2048);
    }
}
