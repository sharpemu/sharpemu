// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

// Writes each channel of a single-sample color image into one sample of a multisample depth image.
public sealed unsafe class ColorToMultisampleDepthBlit : IDisposable
{
    public const ImageLayout DestinationLayout = ImageLayout.DepthStencilAttachmentOptimal;
    private const uint SetsPerPool = 64;

    private readonly record struct PipelineKey(uint Samples, Format Format);

    private readonly record struct CachedPipeline(PipelineKey Key, RenderPass RenderPass, Pipeline Pipeline);

    private readonly GpuDeviceInfo _device;
    private readonly SubmissionScheduler _scheduler;
    private readonly TickDescriptorPools _pools;
    private readonly List<CachedPipeline> _pipelines = new();
    private DescriptorSetLayout _descriptorLayout;
    private PipelineLayout _pipelineLayout;
    private ShaderModule _vertexShader;
    private ShaderModule _fragmentShader;

    public ColorToMultisampleDepthBlit(GpuDeviceInfo device, SubmissionScheduler scheduler)
        : this(device, scheduler, BlitShaders.CreateFullscreenTriangleVertex(), BlitShaders.CreateColorToMultisampleDepthFragment())
    {
    }

    // Tests pass other modules to compare outputs; production always uses the assembled ones.
    internal ColorToMultisampleDepthBlit(GpuDeviceInfo device, SubmissionScheduler scheduler, byte[] vertexSpirv, byte[] fragmentSpirv)
    {
        _device = device;
        _scheduler = scheduler;
        var vk = device.Vk;
        var binding = new DescriptorSetLayoutBinding(0, DescriptorType.SampledImage, 1, ShaderStageFlags.FragmentBit);
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding,
        };
        RequireSuccess(vk.CreateDescriptorSetLayout(device.Device, &layoutInfo, null, out _descriptorLayout), "vkCreateDescriptorSetLayout(blit)");
        fixed (DescriptorSetLayout* layout = &_descriptorLayout)
        {
            var pipelineLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = layout,
            };
            RequireSuccess(vk.CreatePipelineLayout(device.Device, &pipelineLayoutInfo, null, out _pipelineLayout), "vkCreatePipelineLayout(blit)");
        }

        _vertexShader = CreateShader(vertexSpirv, "blit vertex");
        _fragmentShader = CreateShader(fragmentSpirv, "blit fragment");
        _pools = new TickDescriptorPools(device, scheduler, [new DescriptorPoolSize(DescriptorType.SampledImage, SetsPerPool)], SetsPerPool);
    }

    public void Dispose()
    {
        var vk = _device.Vk;
        foreach (var cached in _pipelines)
        {
            vk.DestroyPipeline(_device.Device, cached.Pipeline, null);
            vk.DestroyRenderPass(_device.Device, cached.RenderPass, null);
        }

        _pipelines.Clear();
        _pools.Dispose();
        vk.DestroyShaderModule(_device.Device, _fragmentShader, null);
        vk.DestroyShaderModule(_device.Device, _vertexShader, null);
        vk.DestroyPipelineLayout(_device.Device, _pipelineLayout, null);
        vk.DestroyDescriptorSetLayout(_device.Device, _descriptorLayout, null);
        _fragmentShader = default;
        _vertexShader = default;
        _pipelineLayout = default;
        _descriptorLayout = default;
    }

    private static void RequireSuccess(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw SubmissionScheduler.Fatal($"{operation} failed: result={result}.");
        }
    }

    private ShaderModule CreateShader(byte[] spirv, string operation)
    {
        if (spirv.Length == 0)
        {
            throw SubmissionScheduler.Fatal($"The {operation} shader is empty.");
        }

        fixed (byte* code = spirv)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)code,
            };
            RequireSuccess(_device.Vk.CreateShaderModule(_device.Device, &info, null, out var module), $"vkCreateShaderModule({operation})");
            return module;
        }
    }

    private RenderPass CreateRenderPass(PipelineKey key, SampleCountFlags samples)
    {
        var attachment = new AttachmentDescription
        {
            Format = key.Format,
            Samples = samples,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = DestinationLayout,
            FinalLayout = DestinationLayout,
        };
        var reference = new AttachmentReference(0, DestinationLayout);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            PDepthStencilAttachment = &reference,
        };
        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &attachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
        };
        RequireSuccess(_device.Vk.CreateRenderPass(_device.Device, &info, null, out var renderPass), "vkCreateRenderPass(blit)");
        return renderPass;
    }

    private CachedPipeline GetPipeline(PipelineKey key)
    {
        foreach (var cached in _pipelines)
        {
            if (cached.Key == key)
            {
                return cached;
            }
        }

        var samples = ImageDescription.VulkanSampleCount(key.Samples);
        if (samples == 0 || key.Format == Format.Undefined)
        {
            throw SubmissionScheduler.Fatal($"The blit pipeline key is invalid: samples={key.Samples} format={(int)key.Format}.");
        }

        var renderPass = CreateRenderPass(key, samples);
        var entry = (byte*)Marshal.StringToHGlobalAnsi("main");
        Pipeline pipeline;
        Result created;
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = _vertexShader, PName = entry };
            stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = _fragmentShader, PName = entry };
            var vertexInput = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
            var inputAssembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
            var viewport = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
            var rasterization = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                LineWidth = 1.0f,
            };
            var multisample = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = samples };
            var depth = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = true,
                DepthWriteEnable = true,
                DepthCompareOp = CompareOp.Always,
            };
            var colorBlend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo };
            var dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamic = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamicStates };
            var create = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewport,
                PRasterizationState = &rasterization,
                PMultisampleState = &multisample,
                PDepthStencilState = &depth,
                PColorBlendState = &colorBlend,
                PDynamicState = &dynamic,
                Layout = _pipelineLayout,
                RenderPass = renderPass,
                Subpass = 0,
            };
            created = _device.Vk.CreateGraphicsPipelines(_device.Device, default, 1, &create, null, out pipeline);
        }
        finally
        {
            Marshal.FreeHGlobal((nint)entry);
        }

        if (created != Result.Success)
        {
            _device.Vk.DestroyRenderPass(_device.Device, renderPass, null);
            RequireSuccess(created, "vkCreateGraphicsPipelines(blit)");
        }

        var result = new CachedPipeline(key, renderPass, pipeline);
        _pipelines.Add(result);
        return result;
    }

    public void Reinterpret(CachedImage source, CachedImage destination)
    {
        ref readonly var sourceInfo = ref source.Description;
        ref readonly var destinationInfo = ref destination.Description;
        if (DepthFormatRule.AspectTransferFormat(sourceInfo.PixelFormat) != Format.Undefined ||
            DepthFormatRule.AspectTransferFormat(destinationInfo.PixelFormat) == Format.Undefined ||
            sourceInfo.Samples != 1 || destinationInfo.Samples <= 1 || destinationInfo.Samples > 4 ||
            source.Backing.ImageType != ImageType.Type2D || destination.Backing.ImageType != ImageType.Type2D ||
            sourceInfo.Extent.Width != destinationInfo.Extent.Width || sourceInfo.Extent.Height != destinationInfo.Extent.Height ||
            sourceInfo.Extent.Depth != 1 || destinationInfo.Extent.Depth != 1 || !source.Backing.Exists || !destination.Backing.Exists)
        {
            throw SubmissionScheduler.Fatal(
                $"The color-to-multisample-depth blit needs a 2D single-sample color source and a 2D multisample depth destination of the same size: " +
                $"sourceFormat={(int)sourceInfo.PixelFormat} sourceSamples={sourceInfo.Samples} sourceExtent={sourceInfo.Extent.Width}x{sourceInfo.Extent.Height}x{sourceInfo.Extent.Depth} " +
                $"destinationFormat={(int)destinationInfo.PixelFormat} destinationSamples={destinationInfo.Samples} destinationExtent={destinationInfo.Extent.Width}x{destinationInfo.Extent.Height}x{destinationInfo.Extent.Depth}.");
        }

        _scheduler.EndRendering();
        var sourceView = source.GetOrCreateView(ImageViewDescription.Default with
        {
            Format = sourceInfo.PixelFormat,
            Type = ImageViewType.Type2D,
            Aspect = ImageAspectFlags.ColorBit,
            Usage = ImageUsageFlags.SampledBit,
        });
        var destinationView = destination.GetOrCreateView(ImageViewDescription.Default with
        {
            Format = destinationInfo.PixelFormat,
            Type = ImageViewType.Type2D,
            Aspect = ImageAspectFlags.DepthBit,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit,
        });

        var vk = _device.Vk;
        var command = new CommandBuffer(_scheduler.Current.Handle);
        source.Transition(ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit, null, command);
        destination.Transition(DestinationLayout, AccessFlags.DepthStencilAttachmentWriteBit, null, command);

        var cached = GetPipeline(new PipelineKey(destinationInfo.Samples, destinationInfo.PixelFormat));
        var extent = new Extent2D(destinationInfo.Extent.Width, destinationInfo.Extent.Height);
        var framebufferInfo = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = cached.RenderPass,
            AttachmentCount = 1,
            PAttachments = &destinationView,
            Width = extent.Width,
            Height = extent.Height,
            Layers = 1,
        };
        RequireSuccess(vk.CreateFramebuffer(_device.Device, &framebufferInfo, null, out var framebuffer), "vkCreateFramebuffer(blit)");
        _scheduler.QueueCompletionAction(() => vk.DestroyFramebuffer(_device.Device, framebuffer, null));

        var set = _pools.Allocate(_descriptorLayout);
        var imageInfo = new DescriptorImageInfo { ImageView = sourceView, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.SampledImage,
            PImageInfo = &imageInfo,
        };
        vk.UpdateDescriptorSets(_device.Device, 1, &write, 0, null);

        var clear = new ClearValue { DepthStencil = new ClearDepthStencilValue(0.0f, 0) };
        var begin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = cached.RenderPass,
            Framebuffer = framebuffer,
            RenderArea = new Rect2D(new Offset2D(0, 0), extent),
            ClearValueCount = 1,
            PClearValues = &clear,
        };
        vk.CmdBeginRenderPass(command, &begin, SubpassContents.Inline);
        vk.CmdBindDescriptorSets(command, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &set, 0, null);
        vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, cached.Pipeline);
        var viewport = new Viewport(0.0f, 0.0f, extent.Width, extent.Height, 0.0f, 1.0f);
        var scissor = new Rect2D(new Offset2D(0, 0), extent);
        vk.CmdSetViewport(command, 0, 1, &viewport);
        vk.CmdSetScissor(command, 0, 1, &scissor);
        vk.CmdDraw(command, 3, 1, 0, 0);
        vk.CmdEndRenderPass(command);
    }
}
