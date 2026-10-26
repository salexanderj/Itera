using System.Buffers.Binary;
using Itera.Engine.Rendering.Pipelines;

namespace Itera.Engine.Tests.Rendering;

/// <summary>
/// The pipeline cache's file (<see cref="PipelineCacheFile"/>): which files the header check accepts, and the bytes
/// written and read again, without a device.
/// </summary>
public sealed class PipelineCacheFileTests : IDisposable
{
    private const uint Vendor = 0x1002;
    private const uint Device = 0x73BF;

    private static readonly byte[] Uuid = [.. Enumerable.Range(1, PipelineCacheFile.UuidBytes).Select(i => (byte)i)];

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "itera-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void A_header_of_this_device_is_accepted()
    {
        var data = Cache(Vendor, Device, Uuid, payload: 100);

        Assert.True(PipelineCacheFile.HeaderMatches(data, Vendor, Device, Uuid));
    }

    [Fact]
    public void A_header_of_another_device_is_refused()
    {
        byte[] otherUuid = [.. Uuid];
        otherUuid[15] ^= 0xFF;

        Assert.False(PipelineCacheFile.HeaderMatches(Cache(0x10DE, Device, Uuid, 100), Vendor, Device, Uuid));
        Assert.False(PipelineCacheFile.HeaderMatches(Cache(Vendor, 0x1234, Uuid, 100), Vendor, Device, Uuid));
        Assert.False(PipelineCacheFile.HeaderMatches(Cache(Vendor, Device, otherUuid, 100), Vendor, Device, Uuid));
    }

    [Fact]
    public void A_short_or_damaged_header_is_refused()
    {
        var data = Cache(Vendor, Device, Uuid, payload: 0);
        var otherVersion = Cache(Vendor, Device, Uuid, payload: 0);
        BinaryPrimitives.WriteUInt32LittleEndian(otherVersion.AsSpan(4), 2);
        var tooLong = Cache(Vendor, Device, Uuid, payload: 0);
        BinaryPrimitives.WriteUInt32LittleEndian(tooLong.AsSpan(), PipelineCacheFile.HeaderBytes + 1);

        Assert.True(PipelineCacheFile.HeaderMatches(data, Vendor, Device, Uuid));
        Assert.False(
            PipelineCacheFile.HeaderMatches(data.AsSpan(0, PipelineCacheFile.HeaderBytes - 1), Vendor, Device, Uuid)
        );
        Assert.False(PipelineCacheFile.HeaderMatches([], Vendor, Device, Uuid));
        Assert.False(PipelineCacheFile.HeaderMatches(otherVersion, Vendor, Device, Uuid));
        Assert.False(PipelineCacheFile.HeaderMatches(tooLong, Vendor, Device, Uuid));
    }

    [Fact]
    public void The_bytes_written_are_read_again_and_replace_the_old_file()
    {
        var path = Path.Combine(_folder, "Data", PipelineCacheFile.FileName);
        var first = Cache(Vendor, Device, Uuid, payload: 10);
        var second = Cache(Vendor, Device, Uuid, payload: 300);

        Assert.Null(PipelineCacheFile.Write(path, first));
        Assert.Null(PipelineCacheFile.Write(path, second));
        var read = PipelineCacheFile.Read(path, out var problem);

        Assert.Null(problem);
        Assert.Equal(second, read);
        Assert.Equal(
            [PipelineCacheFile.FileName],
            Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName)
        );
    }

    [Fact]
    public void No_file_reads_as_none_without_a_problem()
    {
        var read = PipelineCacheFile.Read(Path.Combine(_folder, PipelineCacheFile.FileName), out var problem);

        Assert.Null(read);
        Assert.Null(problem);
    }

    /// <summary>A cache's bytes: a version-one header for the device, then <paramref name="payload"/> bytes.</summary>
    private static byte[] Cache(uint vendor, uint device, byte[] uuid, int payload)
    {
        var data = new byte[PipelineCacheFile.HeaderBytes + payload];
        BinaryPrimitives.WriteUInt32LittleEndian(data, PipelineCacheFile.HeaderBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), PipelineCacheFile.HeaderVersionOne);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), vendor);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), device);
        uuid.CopyTo(data, 16);
        for (var i = PipelineCacheFile.HeaderBytes; i < data.Length; i++)
        {
            data[i] = (byte)(i * 7);
        }

        return data;
    }
}
