// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        // This partial owns HDR presentation resources.

        private void CreateHdrPresentationResources()
        {
            var colorAttachment = new AttachmentDescription
            {
                Format = _swapchainFormat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = ImageLayout.PresentSrcKhr,
            };
            var colorReference = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorReference,
            };
            var dependency = new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal,
                DstSubpass = 0,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
            };
            var renderPassInfo = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = 1,
                PAttachments = &colorAttachment,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 1,
                PDependencies = &dependency,
            };
            Check(
                _vk.CreateRenderPass(_device, &renderPassInfo, null, out _hdrRenderPass),
                "vkCreateRenderPass(HDR presentation)");

            _hdrFramebuffers = new Framebuffer[_swapchainImageViews.Length];
            for (var index = 0; index < _swapchainImageViews.Length; index++)
            {
                var view = _swapchainImageViews[index];
                var framebufferInfo = new FramebufferCreateInfo
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = _hdrRenderPass,
                    AttachmentCount = 1,
                    PAttachments = &view,
                    Width = _extent.Width,
                    Height = _extent.Height,
                    Layers = 1,
                };
                Check(
                    _vk.CreateFramebuffer(
                        _device,
                        &framebufferInfo,
                        null,
                        out _hdrFramebuffers[index]),
                    "vkCreateFramebuffer(HDR presentation)");
            }

            var binding = new DescriptorSetLayoutBinding
            {
                Binding = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            };
            var descriptorLayoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1,
                PBindings = &binding,
            };
            Check(
                _vk.CreateDescriptorSetLayout(
                    _device,
                    &descriptorLayoutInfo,
                    null,
                    out _hdrDescriptorSetLayout),
                "vkCreateDescriptorSetLayout(HDR presentation)");

            var descriptorPoolSize = new DescriptorPoolSize
            {
                Type = DescriptorType.CombinedImageSampler,
                DescriptorCount = checked((uint)_presentationSampleViews.Length * 2),
            };
            var descriptorPoolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = checked((uint)_presentationSampleViews.Length * 2),
                PoolSizeCount = 1,
                PPoolSizes = &descriptorPoolSize,
            };
            Check(
                _vk.CreateDescriptorPool(
                    _device,
                    &descriptorPoolInfo,
                    null,
                    out _hdrDescriptorPool),
                "vkCreateDescriptorPool(HDR presentation)");

            var samplerInfo = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = Filter.Linear,
                MinFilter = Filter.Linear,
                MipmapMode = SamplerMipmapMode.Linear,
                AddressModeU = SamplerAddressMode.ClampToEdge,
                AddressModeV = SamplerAddressMode.ClampToEdge,
                AddressModeW = SamplerAddressMode.ClampToEdge,
                MaxLod = 1f,
            };
            Check(
                _vk.CreateSampler(_device, &samplerInfo, null, out _hdrSampler),
                "vkCreateSampler(HDR presentation)");

            _hdrDescriptorSets = new DescriptorSet[_presentationSampleViews.Length];
            _hdrPqDescriptorSets = new DescriptorSet[_presentationSampleViews.Length];
            for (var pq = 0; pq < 2; pq++)
            {
                var descriptorSets = pq == 0 ? _hdrDescriptorSets : _hdrPqDescriptorSets;
                var imageViews = pq == 0 ? _presentationSampleViews : _presentationImageViews;
                for (var index = 0; index < descriptorSets.Length; index++)
                {
                    var layout = _hdrDescriptorSetLayout;
                    var allocateInfo = new DescriptorSetAllocateInfo
                    {
                        SType = StructureType.DescriptorSetAllocateInfo,
                        DescriptorPool = _hdrDescriptorPool,
                        DescriptorSetCount = 1,
                        PSetLayouts = &layout,
                    };
                    Check(
                        _vk.AllocateDescriptorSets(
                            _device,
                            &allocateInfo,
                            out descriptorSets[index]),
                        "vkAllocateDescriptorSets(HDR presentation)");
                    var imageInfo = new DescriptorImageInfo
                    {
                        Sampler = _hdrSampler,
                        ImageView = imageViews[index],
                        ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                    };
                    var write = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = descriptorSets[index],
                        DstBinding = 1,
                        DescriptorCount = 1,
                        DescriptorType = DescriptorType.CombinedImageSampler,
                        PImageInfo = &imageInfo,
                    };
                    _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);
                }
            }

            var descriptorSetLayout = _hdrDescriptorSetLayout;
            var pipelineLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &descriptorSetLayout,
            };
            Check(
                _vk.CreatePipelineLayout(
                    _device,
                    &pipelineLayoutInfo,
                    null,
                    out _hdrPipelineLayout),
                "vkCreatePipelineLayout(HDR presentation)");
            CreateHdrPresentationPipelines();
        }

        private void CreateHdrPresentationPipelines()
        {
            CreateHdrPresentationPipeline(
                SpirvFixedShaders.CreateCopyFragment(_hdrSdrWhiteLevel),
                "SDR",
                out _hdrPipeline);
            CreateHdrPresentationPipeline(
                SpirvFixedShaders.CreatePqToScRgbFragment(),
                "PQ",
                out _hdrPqPipeline);
        }

        private void CreateHdrPresentationPipeline(
            byte[] fragmentBytes,
            string label,
            out Pipeline pipeline)
        {
            var vertexModule = CreateShaderModule(SpirvFixedShaders.CreateFullscreenVertex(1));
            var fragmentModule = CreateShaderModule(fragmentBytes);
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var stages = stackalloc PipelineShaderStageCreateInfo[2];
                stages[0] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vertexModule,
                    PName = entryPoint,
                };
                stages[1] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = fragmentModule,
                    PName = entryPoint,
                };
                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                };
                var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.TriangleList,
                };
                var viewport = new Viewport(0, 0, _extent.Width, _extent.Height, 0, 1);
                var scissor = new Rect2D(new Offset2D(0, 0), _extent);
                var viewportState = new PipelineViewportStateCreateInfo
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    PViewports = &viewport,
                    ScissorCount = 1,
                    PScissors = &scissor,
                };
                var rasterization = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    CullMode = CullModeFlags.None,
                    FrontFace = FrontFace.CounterClockwise,
                    LineWidth = 1,
                };
                var multisample = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit,
                };
                var blendAttachment = new PipelineColorBlendAttachmentState
                {
                    ColorWriteMask = ColorComponentFlags.RBit |
                                     ColorComponentFlags.GBit |
                                     ColorComponentFlags.BBit |
                                     ColorComponentFlags.ABit,
                };
                var blend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = 1,
                    PAttachments = &blendAttachment,
                };
                var pipelineInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2,
                    PStages = stages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterization,
                    PMultisampleState = &multisample,
                    PColorBlendState = &blend,
                    Layout = _hdrPipelineLayout,
                    RenderPass = _hdrRenderPass,
                };
                Check(
                    _vk.CreateGraphicsPipelines(
                        _device,
                        _pipelineCache,
                        1,
                        &pipelineInfo,
                        null,
                        out pipeline),
                    $"vkCreateGraphicsPipelines(HDR {label} presentation)");
                MarkPipelineCacheDirty();
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
                _vk.DestroyShaderModule(_device, fragmentModule, null);
                _vk.DestroyShaderModule(_device, vertexModule, null);
            }
        }

        private void RecordHdrPresentation(uint imageIndex, bool isHdr)
        {
            var sourceBarrier = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.MemoryWriteBit |
                                AccessFlags.TransferWriteBit |
                                AccessFlags.ColorAttachmentWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = ImageLayout.ShaderReadOnlyOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _presentationImages[imageIndex],
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.FragmentShaderBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &sourceBarrier);

            var clearValue = new ClearValue
            {
                Color = new ClearColorValue(0f, 0f, 0f, 1f),
            };
            var renderPassInfo = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = _hdrRenderPass,
                Framebuffer = _hdrFramebuffers[imageIndex],
                RenderArea = new Rect2D(new Offset2D(0, 0), _extent),
                ClearValueCount = 1,
                PClearValues = &clearValue,
            };
            _vk.CmdBeginRenderPass(
                _commandBuffer,
                &renderPassInfo,
                SubpassContents.Inline);
            _vk.CmdBindPipeline(
                _commandBuffer,
                PipelineBindPoint.Graphics,
                isHdr ? _hdrPqPipeline : _hdrPipeline);
            var descriptorSet = isHdr
                ? _hdrPqDescriptorSets[imageIndex]
                : _hdrDescriptorSets[imageIndex];
            _vk.CmdBindDescriptorSets(
                _commandBuffer,
                PipelineBindPoint.Graphics,
                _hdrPipelineLayout,
                0,
                1,
                &descriptorSet,
                0,
                null);
            _vk.CmdDraw(_commandBuffer, 3, 1, 0, 0);
            _vk.CmdEndRenderPass(_commandBuffer);
        }
    }
}
