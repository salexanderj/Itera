using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering.Pipelines;

/// <summary>
/// Creates the Vulkan objects a pass needs: shader modules, pipeline layouts, and graphics pipelines for dynamic
/// rendering. Pipelines take their viewport and scissor at record time, so they survive a swapchain resize; a
/// change of the swapchain's format needs a new pipeline. Call from the thread that owns Vulkan at the time. A game's
/// passes use it too (<see cref="RenderPassSetup"/> fills in a stage's formats and samples).
/// </summary>
public static unsafe class PipelineFactory
{
    /// <summary>Creates a shader module from SPIR-V. The caller destroys it once the pipelines using it exist.</summary>
    public static ShaderModule CreateShaderModule(VulkanContext context, byte[] spirv, string name)
    {
        if (spirv.Length == 0 || spirv.Length % 4 != 0)
        {
            throw new ArgumentException($"SPIR-V for {name} is {spirv.Length} bytes, not a whole number of words.");
        }

        fixed (byte* code = spirv)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)code,
            };
            context
                .Vk.CreateShaderModule(context.Device, &info, null, out var module)
                .ThrowIfFailed("vkCreateShaderModule");
            context.SetObjectName(ObjectType.ShaderModule, module.Handle, name);
            return module;
        }
    }

    /// <summary>A pipeline layout with the given descriptor set layouts and one push constant range at offset 0.</summary>
    public static PipelineLayout CreateLayout(
        VulkanContext context,
        ReadOnlySpan<DescriptorSetLayout> setLayouts,
        ShaderStageFlags pushStages,
        uint pushBytes,
        string name
    )
    {
        if (pushBytes % 4 != 0 || pushBytes > context.Limits.MaxPushConstantsSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pushBytes),
                pushBytes,
                $"Push constants must be a multiple of 4 bytes and at most {context.Limits.MaxPushConstantsSize}."
            );
        }

        PipelineLayout layout;
        var range = new PushConstantRange
        {
            StageFlags = pushStages,
            Offset = 0,
            Size = pushBytes,
        };
        fixed (DescriptorSetLayout* sets = setLayouts)
        {
            var info = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = (uint)setLayouts.Length,
                PSetLayouts = sets,
                PushConstantRangeCount = pushBytes > 0 ? 1u : 0u,
                PPushConstantRanges = pushBytes > 0 ? &range : null,
            };
            context
                .Vk.CreatePipelineLayout(context.Device, &info, null, out var created)
                .ThrowIfFailed("vkCreatePipelineLayout");
            layout = created;
        }

        context.SetObjectName(ObjectType.PipelineLayout, layout.Handle, name);
        return layout;
    }

    /// <summary>
    /// A pipeline that draws one full-screen triangle (no vertex input, <c>vkCmdDraw(3)</c>) into one colour
    /// attachment of <paramref name="colorFormat"/> and the depth attachment <paramref name="depth"/> describes,
    /// without culling, and blended as <paramref name="blend"/> says (none by default), into attachments of
    /// <paramref name="samples"/> samples (one by default).
    /// </summary>
    public static Pipeline CreateFullscreen(
        VulkanContext context,
        byte[] vertexSpirv,
        byte[] fragmentSpirv,
        PipelineLayout layout,
        Format colorFormat,
        in DepthState depth,
        string name,
        ColorBlend blend = ColorBlend.None,
        SampleCountFlags samples = SampleCountFlags.Count1Bit
    ) =>
        CreateWithoutVertexInput(
            context,
            vertexSpirv,
            fragmentSpirv,
            layout,
            colorFormat,
            PrimitiveTopology.TriangleList,
            CullModeFlags.None,
            depth,
            name,
            blend,
            samples
        );

    /// <summary>
    /// A pipeline that draws lines, two vertices each, whose positions the vertex shader finds itself (no vertex input;
    /// from <c>gl_VertexIndex</c>), into one colour attachment of <paramref name="colorFormat"/> and the depth
    /// attachment <paramref name="depth"/> describes, without blending.
    /// </summary>
    public static Pipeline CreateLines(
        VulkanContext context,
        byte[] vertexSpirv,
        byte[] fragmentSpirv,
        PipelineLayout layout,
        Format colorFormat,
        in DepthState depth,
        string name
    ) =>
        CreateWithoutVertexInput(
            context,
            vertexSpirv,
            fragmentSpirv,
            layout,
            colorFormat,
            PrimitiveTopology.LineList,
            CullModeFlags.None,
            depth,
            name
        );

    /// <summary>
    /// A pipeline that draws triangles whose corners the vertex shader finds itself (no vertex input), culling the
    /// faces <paramref name="cull"/> names (front faces wind counter-clockwise on screen), into one colour attachment
    /// of <paramref name="colorFormat"/> (none when it is <see cref="Format.Undefined"/>) and the depth attachment
    /// <paramref name="depth"/> describes, blended as <paramref name="blend"/> says (none by default), into attachments of
    /// <paramref name="samples"/> samples (one by default). <paramref name="lines"/> draws each triangle's edges instead
    /// of filling it (the device's <c>fillModeNonSolid</c>, <see cref="VulkanContext.SupportsWireframe"/>), and
    /// <paramref name="bias"/> moves its depth by a constant and a share of its slope (Vulkan's depth bias factors).
    /// </summary>
    public static Pipeline CreateTriangles(
        VulkanContext context,
        byte[] vertexSpirv,
        byte[] fragmentSpirv,
        PipelineLayout layout,
        Format colorFormat,
        CullModeFlags cull,
        in DepthState depth,
        string name,
        ColorBlend blend = ColorBlend.None,
        SampleCountFlags samples = SampleCountFlags.Count1Bit,
        bool lines = false,
        DepthBias bias = default
    ) =>
        CreateWithoutVertexInput(
            context,
            vertexSpirv,
            fragmentSpirv,
            layout,
            colorFormat,
            PrimitiveTopology.TriangleList,
            cull,
            depth,
            name,
            blend,
            samples,
            lines ? PolygonMode.Line : PolygonMode.Fill,
            bias
        );

    private static Pipeline CreateWithoutVertexInput(
        VulkanContext context,
        byte[] vertexSpirv,
        byte[] fragmentSpirv,
        PipelineLayout layout,
        Format colorFormat,
        PrimitiveTopology topology,
        CullModeFlags cull,
        in DepthState depth,
        string name,
        ColorBlend blend = ColorBlend.None,
        SampleCountFlags samples = SampleCountFlags.Count1Bit,
        PolygonMode polygon = PolygonMode.Fill,
        DepthBias bias = default
    )
    {
        var vk = context.Vk;
        var device = context.Device;
        var vertex = CreateShaderModule(context, vertexSpirv, name + " vertex");
        var fragment = default(ShaderModule);
        var entry = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            fragment = CreateShaderModule(context, fragmentSpirv, name + " fragment");
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vertex,
                PName = entry,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragment,
                PName = entry,
            };

            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
            };
            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = topology,
            };
            var viewport = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1,
            };
            var rasterization = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = polygon,
                CullMode = cull,
                FrontFace = FrontFace.CounterClockwise,
                DepthBiasEnable = bias.Constant != 0 || bias.Slope != 0,
                DepthBiasConstantFactor = bias.Constant,
                DepthBiasSlopeFactor = bias.Slope,
                LineWidth = 1f,
            };
            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = samples,
            };
            // Premultiplied: the shader's colour is already multiplied by its alpha, so it is added as it is over
            // what is left of the colour below. Alpha: the shader's colour times its alpha over the colour below times
            // what the alpha leaves. Additive: the shader's colour added to the colour below.
            var (source, destination) = blend switch
            {
                ColorBlend.Premultiplied => (BlendFactor.One, BlendFactor.OneMinusSrcAlpha),
                ColorBlend.Alpha => (BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha),
                ColorBlend.Additive => (BlendFactor.One, BlendFactor.One),
                _ => (BlendFactor.One, BlendFactor.Zero),
            };
            var blendAttachment = new PipelineColorBlendAttachmentState
            {
                BlendEnable = blend != ColorBlend.None,
                SrcColorBlendFactor = source,
                DstColorBlendFactor = destination,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = blend == ColorBlend.None ? BlendFactor.Zero : BlendFactor.One,
                DstAlphaBlendFactor = blend == ColorBlend.None ? BlendFactor.Zero : BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask =
                    ColorComponentFlags.RBit
                    | ColorComponentFlags.GBit
                    | ColorComponentFlags.BBit
                    | ColorComponentFlags.ABit,
            };
            var hasColor = colorFormat != Format.Undefined;
            var colorBlend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = hasColor ? 1u : 0u,
                PAttachments = hasColor ? &blendAttachment : null,
            };
            var hasDepth = depth.Format != Format.Undefined;
            var depthStencil = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = hasDepth,
                DepthWriteEnable = hasDepth && depth.Write,
                DepthCompareOp = depth.Compare,
            };
            var dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamic = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynamicStates,
            };
            var rendering = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = hasColor ? 1u : 0u,
                PColorAttachmentFormats = hasColor ? &colorFormat : null,
                DepthAttachmentFormat = depth.Format,
            };
            var info = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &rendering,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewport,
                PRasterizationState = &rasterization,
                PMultisampleState = &multisample,
                PDepthStencilState = hasDepth ? &depthStencil : null,
                PColorBlendState = &colorBlend,
                PDynamicState = &dynamic,
                Layout = layout,
            };

            Pipeline pipeline;
            vk.CreateGraphicsPipelines(device, default, 1, &info, null, &pipeline)
                .ThrowIfFailed("vkCreateGraphicsPipelines");
            context.SetObjectName(ObjectType.Pipeline, pipeline.Handle, name);
            return pipeline;
        }
        finally
        {
            // Modules may be destroyed once the pipeline exists (spec, vkDestroyShaderModule).
            if (fragment.Handle != 0)
            {
                vk.DestroyShaderModule(device, fragment, null);
            }

            vk.DestroyShaderModule(device, vertex, null);
            SilkMarshal.Free((nint)entry);
        }
    }

    /// <summary>A compute pipeline running the <c>main</c> entry point of <paramref name="spirv"/>.</summary>
    public static Pipeline CreateCompute(VulkanContext context, byte[] spirv, PipelineLayout layout, string name)
    {
        var vk = context.Vk;
        var device = context.Device;
        var module = CreateShaderModule(context, spirv, name + " compute");
        var entry = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var info = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = module,
                    PName = entry,
                },
                Layout = layout,
            };

            Pipeline pipeline;
            vk.CreateComputePipelines(device, default, 1, &info, null, &pipeline)
                .ThrowIfFailed("vkCreateComputePipelines");
            context.SetObjectName(ObjectType.Pipeline, pipeline.Handle, name);
            return pipeline;
        }
        finally
        {
            // The module may be destroyed once the pipeline exists (spec, vkDestroyShaderModule).
            vk.DestroyShaderModule(device, module, null);
            SilkMarshal.Free((nint)entry);
        }
    }
}

/// <summary>
/// The depth attachment a pipeline renders with: its format (<see cref="Format.Undefined"/> for none), the compare op
/// of the depth test, and whether the pipeline writes depth. The default is no depth attachment.
/// </summary>
public readonly record struct DepthState(Format Format, CompareOp Compare, bool Write);

/// <summary>
/// A pipeline's depth bias: <see cref="Constant"/> in the depth format's smallest steps and <see cref="Slope"/> times the
/// primitive's depth slope, added to each fragment's depth. None by default.
/// </summary>
public readonly record struct DepthBias(float Constant, float Slope);

/// <summary>How a pipeline's colour output is combined with the attachment.</summary>
public enum ColorBlend
{
    /// <summary>The output replaces the attachment's colour.</summary>
    None = 0,

    /// <summary>
    /// The output is premultiplied by its alpha and drawn over the attachment: colour = src + dst * (1 - src alpha).
    /// </summary>
    Premultiplied = 1,

    /// <summary>The output's colour, by its alpha, over the attachment's: colour = src * src alpha + dst * (1 - src alpha).</summary>
    Alpha = 2,

    /// <summary>The output's colour added to the attachment's (a glow): colour = src + dst.</summary>
    Additive = 3,
}
