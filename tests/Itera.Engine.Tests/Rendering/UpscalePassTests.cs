using System.Numerics;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Shaders;
using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.Vulkan;

namespace Itera.Engine.Tests.Rendering;

public sealed class UpscalePassTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "itera-tests", Guid.NewGuid().ToString("N"));
    private readonly ShaderCompiler _compiler;

    public UpscalePassTests()
    {
        _compiler = new ShaderCompiler(NullLogger<ShaderCompiler>.Instance, _cache);
        _compiler.Initialize();
    }

    public void Dispose()
    {
        _compiler.Dispose();
        if (Directory.Exists(_cache))
        {
            Directory.Delete(_cache, recursive: true);
        }
    }

    [Fact]
    public void The_push_constant_block_in_the_upscale_shader_matches_the_struct()
    {
        var spirv = _compiler.Compile("fsr-easu.frag");

        Assert.Empty(GpuLayout.PushConstantDifferences<EasuConstants>(spirv));
    }

    [Fact]
    public void The_push_constant_block_in_the_sharpening_shader_matches_the_struct()
    {
        var spirv = _compiler.Compile("fsr-rcas.frag");

        Assert.Empty(GpuLayout.PushConstantDifferences<RcasConstants>(spirv));
    }

    // The words FsrEasuCon in ffx_fsr1.glsl packs (AU1_AF1), worked out from its formula in 32-bit floats outside .NET.
    [Theory]
    [InlineData(
        1280f,
        720f,
        1920f,
        1080f,
        1920f,
        1080f,
        new uint[]
        {
            0x3F2AAAAB,
            0x3F2AAAAA,
            0xBE2AAAAA,
            0xBE2AAAAC,
            0x3A088889,
            0x3A72B9D6,
            0x3A088889,
            0xBA72B9D6,
            0xBA088889,
            0x3AF2B9D6,
            0x3A088889,
            0x3AF2B9D6,
            0x00000000,
            0x3B72B9D6,
            0x00000000,
            0x00000000,
        }
    )]
    [InlineData(
        1707f,
        960f,
        2560f,
        1440f,
        2560f,
        1440f,
        new uint[]
        {
            0x3F2AB333,
            0x3F2AAAAB,
            0xBE2A999A,
            0xBE2AAAAA,
            0x39CCCCCD,
            0x3A360B61,
            0x39CCCCCD,
            0xBA360B61,
            0xB9CCCCCD,
            0x3AB60B61,
            0x39CCCCCD,
            0x3AB60B61,
            0x00000000,
            0x3B360B61,
            0x00000000,
            0x00000000,
        }
    )]
    [InlineData(
        960f,
        540f,
        960f,
        540f,
        1920f,
        1080f,
        new uint[]
        {
            0x3F000000,
            0x3F000000,
            0xBE800000,
            0xBE800000,
            0x3A888889,
            0x3AF2B9D6,
            0x3A888889,
            0xBAF2B9D6,
            0xBA888889,
            0x3B72B9D6,
            0x3A888889,
            0x3B72B9D6,
            0x00000000,
            0x3BF2B9D6,
            0x00000000,
            0x00000000,
        }
    )]
    [InlineData(
        1001f,
        563f,
        1366f,
        768f,
        1366f,
        768f,
        new uint[]
        {
            0x3F3B988D,
            0x3F3BAAAB,
            0xBE08CEE6,
            0xBE08AAAA,
            0x3A3FE803,
            0x3AAAAAAB,
            0x3A3FE803,
            0xBAAAAAAB,
            0xBA3FE803,
            0x3B2AAAAB,
            0x3A3FE803,
            0x3B2AAAAB,
            0x00000000,
            0x3BAAAAAB,
            0x00000000,
            0x00000000,
        }
    )]
    public void The_upscale_constants_have_the_bits_of_the_shaders_formula(
        float pictureWidth,
        float pictureHeight,
        float sourceWidth,
        float sourceHeight,
        float outputWidth,
        float outputHeight,
        uint[] expected
    )
    {
        var constants = EasuConstants.Create(
            new(pictureWidth, pictureHeight),
            new(sourceWidth, sourceHeight),
            new(outputWidth, outputHeight)
        );

        Assert.Equal(expected, Words(constants));
    }

    [Fact]
    public void The_upscale_constants_take_the_drawn_size_as_the_viewport_and_the_image_as_the_input()
    {
        var constants = EasuConstants.Create(new(1280, 720), new(1920, 1080), new(1920, 1080));

        Assert.Equal(new Vector2(1280, 720), constants.PictureSize);
        Assert.Equal(new Vector2(1920, 1080), constants.SourceSize);
        // Output pixels step through the viewport, and source texels are a step of the image's size.
        Assert.Equal(1280f / 1920f, constants.Con0.X, 6);
        Assert.Equal(1f / 1920f, constants.Con1.X);
    }

    [Fact]
    public void A_new_render_scale_keeps_the_images()
    {
        var screen = new Extent2D(1920, 1080);

        Assert.True(UpscalePass.ImagesFit(screen, new Extent2D(1280, 720), screen));
        Assert.True(UpscalePass.ImagesFit(screen, new Extent2D(1700, 956), screen));
    }

    [Fact]
    public void A_new_screen_makes_new_images()
    {
        var images = new Extent2D(1920, 1080);

        Assert.False(UpscalePass.ImagesFit(images, new Extent2D(1280, 720), new Extent2D(2560, 1440)));
        Assert.False(UpscalePass.ImagesFit(images, new Extent2D(800, 450), new Extent2D(1280, 720)));
    }

    private static uint[] Words(EasuConstants constants)
    {
        Vector4[] vectors = [constants.Con0, constants.Con1, constants.Con2, constants.Con3];
        var words = new uint[16];
        for (var i = 0; i < vectors.Length; i++)
        {
            words[4 * i] = BitConverter.SingleToUInt32Bits(vectors[i].X);
            words[4 * i + 1] = BitConverter.SingleToUInt32Bits(vectors[i].Y);
            words[4 * i + 2] = BitConverter.SingleToUInt32Bits(vectors[i].Z);
            words[4 * i + 3] = BitConverter.SingleToUInt32Bits(vectors[i].W);
        }

        return words;
    }

    [Theory]
    [InlineData(Format.B8G8R8A8Srgb, Format.B8G8R8A8Unorm)]
    [InlineData(Format.R8G8B8A8Srgb, Format.R8G8B8A8Unorm)]
    [InlineData(Format.A8B8G8R8SrgbPack32, Format.A8B8G8R8UnormPack32)]
    [InlineData(Format.B8G8R8A8Unorm, Format.B8G8R8A8Unorm)]
    public void The_source_is_read_through_the_unorm_twin_of_an_srgb_format(Format format, Format expected)
    {
        Assert.Equal(expected, UpscalePass.Unorm(format));
    }
}
