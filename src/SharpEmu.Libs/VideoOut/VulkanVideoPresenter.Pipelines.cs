// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

// This partial creates translated Vulkan pipelines with their descriptor resources.
internal static unsafe partial class VulkanVideoPresenter
{

    private sealed partial class Presenter
    {
        private const string FullscreenBarycentricVertexSpirv =
            "AwIjBwAAAQALAAgAMgAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAAA8ACAAAAAAABAAAAG1haW4AAAAADQAAABoAAAApAAAAAwADAAIAAADCAQAABQAEAAQAAABtYWluAAAAAAUABgALAAAAZ2xfUGVyVmVydGV4AAAAAAYABgALAAAAAAAAAGdsX1Bvc2l0aW9uAAYABwALAAAAAQAAAGdsX1BvaW50U2l6ZQAAAAAGAAcACwAAAAIAAABnbF9DbGlwRGlzdGFuY2UABgAHAAsAAAADAAAAZ2xfQ3VsbERpc3RhbmNlAAUAAwANAAAAAAAAAAUABgAaAAAAZ2xfVmVydGV4SW5kZXgAAAUABQAdAAAAaW5kZXhhYmxlAAAABQAFACkAAABiYXJ5Y2VudHJpYwAFAAUALwAAAGluZGV4YWJsZQAAAEcAAwALAAAAAgAAAEgABQALAAAAAAAAAAsAAAAAAAAASAAFAAsAAAABAAAACwAAAAEAAABIAAUACwAAAAIAAAALAAAAAwAAAEgABQALAAAAAwAAAAsAAAAEAAAARwAEABoAAAALAAAAKgAAAEcABAApAAAAHgAAAAAAAAATAAIAAgAAACEAAwADAAAAAgAAABYAAwAGAAAAIAAAABcABAAHAAAABgAAAAQAAAAVAAQACAAAACAAAAAAAAAAKwAEAAgAAAAJAAAAAQAAABwABAAKAAAABgAAAAkAAAAeAAYACwAAAAcAAAAGAAAACgAAAAoAAAAgAAQADAAAAAMAAAALAAAAOwAEAAwAAAANAAAAAwAAABUABAAOAAAAIAAAAAEAAAArAAQADgAAAA8AAAAAAAAAFwAEABAAAAAGAAAAAgAAACsABAAIAAAAEQAAAAMAAAAcAAQAEgAAABAAAAARAAAAKwAEAAYAAAATAAAAAACAvywABQAQAAAAFAAAABMAAAATAAAAKwAEAAYAAAAVAAAAAABAQCwABQAQAAAAFgAAABUAAAATAAAALAAFABAAAAAXAAAAEwAAABUAAAAsAAYAEgAAABgAAAAUAAAAFgAAABcAAAAgAAQAGQAAAAEAAAAOAAAAOwAEABkAAAAaAAAAAQAAACAABAAcAAAABwAAABIAAAAgAAQAHgAAAAcAAAAQAAAAKwAEAAYAAAAhAAAAAAAAACsABAAGAAAAIgAAAAAAgD8gAAQAJgAAAAMAAAAHAAAAIAAEACgAAAADAAAAEAAAADsABAAoAAAAKQAAAAMAAAAsAAUAEAAAACoAAAAiAAAAIQAAACwABQAQAAAAKwAAACEAAAAiAAAALAAFABAAAAAsAAAAIQAAACEAAAAsAAYAEgAAAC0AAAAqAAAAKwAAACwAAAA2AAUAAgAAAAQAAAAAAAAAAwAAAPgAAgAFAAAAOwAEABwAAAAdAAAABwAAADsABAAcAAAALwAAAAcAAAA9AAQADgAAABsAAAAaAAAAPgADAB0AAAAYAAAAQQAFAB4AAAAfAAAAHQAAABsAAAA9AAQAEAAAACAAAAAfAAAAUQAFAAYAAAAjAAAAIAAAAAAAAABRAAUABgAAACQAAAAgAAAAAQAAAFAABwAHAAAAJQAAACMAAAAkAAAAIQAAACIAAABBAAUAJgAAACcAAAANAAAADwAAAD4AAwAnAAAAJQAAAD0ABAAOAAAALgAAABoAAAA+AAMALwAAAC0AAABBAAUAHgAAADAAAAAvAAAALgAAAD0ABAAQAAAAMQAAADAAAAA+AAMAKQAAADEAAAD9AAEAOAABAA==";

        private const string FullscreenBarycentricFragmentSpirv =
            "AwIjBwAAAQALAAgAEgAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAAA8ABwAEAAAABAAAAG1haW4AAAAACQAAAAwAAAAQAAMABAAAAAcAAAADAAMAAgAAAMIBAAAFAAQABAAAAG1haW4AAAAABQAFAAkAAABvdXRDb2xvcgAAAAAFAAUADAAAAGJhcnljZW50cmljAEcABAAJAAAAHgAAAAAAAABHAAQADAAAAB4AAAAAAAAAEwACAAIAAAAhAAMAAwAAAAIAAAAWAAMABgAAACAAAAAXAAQABwAAAAYAAAAEAAAAIAAEAAgAAAADAAAABwAAADsABAAIAAAACQAAAAMAAAAXAAQACgAAAAYAAAACAAAAIAAEAAsAAAABAAAACgAAADsABAALAAAADAAAAAEAAAArAAQABgAAAA4AAAAAAAAANgAFAAIAAAAEAAAAAAAAAAMAAAD4AAIABQAAAD0ABAAKAAAADQAAAAwAAABRAAUABgAAAA8AAAANAAAAAAAAAFEABQAGAAAAEAAAAA0AAAABAAAAUAAHAAcAAAARAAAADwAAABAAAAAOAAAADgAAAD4AAwAJAAAAEQAAAP0AAQA4AAEA";

        private readonly Dictionary<byte[], string> _shaderDigests =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<DescriptorLayoutKey, DescriptorLayoutBundle>
            _descriptorLayouts = new();
        private readonly Stack<DescriptorPool> _recycledDescriptorPools = new();

        private readonly record struct DescriptorLayoutKey(
            ShaderStageFlags Stages,
            string Resources);

        private sealed record DescriptorLayoutBundle(
            DescriptorSetLayout DescriptorSetLayout,
            PipelineLayout PipelineLayout);
        private static int _shaderModuleDumpSequence;

        private void CreateBarycentricPipeline()
        {
            var vertexBytes = Convert.FromBase64String(FullscreenBarycentricVertexSpirv);
            var fragmentBytes = Convert.FromBase64String(FullscreenBarycentricFragmentSpirv);
            var vertexModule = CreateShaderModule(vertexBytes);
            var fragmentModule = CreateShaderModule(fragmentBytes);
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var shaderStages = stackalloc PipelineShaderStageCreateInfo[2];
                shaderStages[0] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vertexModule,
                    PName = entryPoint,
                };
                shaderStages[1] = new PipelineShaderStageCreateInfo
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
                var colorBlendAttachment = new PipelineColorBlendAttachmentState
                {
                    ColorWriteMask =
                        ColorComponentFlags.RBit |
                        ColorComponentFlags.GBit |
                        ColorComponentFlags.BBit |
                        ColorComponentFlags.ABit,
                };
                var colorBlend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = 1,
                    PAttachments = &colorBlendAttachment,
                };
                var pipelineInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2,
                    PStages = shaderStages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterization,
                    PMultisampleState = &multisample,
                    PColorBlendState = &colorBlend,
                    Layout = _pipelineLayout,
                    RenderPass = _renderPass,
                    Subpass = 0,
                };
                Check(
                    _vk.CreateGraphicsPipelines(
                        _device,
                        _pipelineCache,
                        1,
                        &pipelineInfo,
                        null,
                        out _barycentricPipeline),
                    "vkCreateGraphicsPipelines");
                MarkPipelineCacheDirty();
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
                _vk.DestroyShaderModule(_device, fragmentModule, null);
                _vk.DestroyShaderModule(_device, vertexModule, null);
            }
        }

        private ShaderModule CreateShaderModule(byte[] code)
        {
            string? dumpPath = null;
            var dumpDirectory = Environment.GetEnvironmentVariable("SHARPEMU_SHADER_SPIRV_DUMP_DIR");
            if (!string.IsNullOrWhiteSpace(dumpDirectory))
            {
                Directory.CreateDirectory(dumpDirectory);

                var sequence = Interlocked.Increment(ref _shaderModuleDumpSequence);
                dumpPath = Path.Combine(dumpDirectory, $"{sequence:D4}.spv");
                File.WriteAllBytes(dumpPath, code);

                _pendingShaderModuleDumpPath = dumpPath;
            }

            try
            {
                fixed (byte* codePointer = code)
                {
                    var createInfo = new ShaderModuleCreateInfo
                    {
                        SType = StructureType.ShaderModuleCreateInfo,
                        CodeSize = (nuint)code.Length,
                        PCode = (uint*)codePointer,
                    };
                    Check(
                        _vk.CreateShaderModule(_device, &createInfo, null, out var module),
                        "vkCreateShaderModule");
                    return module;
                }
            }
            finally
            {
                _pendingShaderModuleDumpPath = null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CreateTranslatedDescriptorResources(
            TranslatedDrawResources resources,
            ShaderStageFlags stageFlags)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DescriptorSetup);
            var textureCount = resources.Textures.Length;
            var sampledImageCount = resources.Textures.Count(texture => !texture.IsStorage);
            var storageImageCount = textureCount - sampledImageCount;
            var globalBufferCount = resources.GlobalMemoryBuffers.Length;
            var bindingCount = textureCount + (globalBufferCount == 0 ? 0 : 1);
            var layout = GetOrCreateDescriptorLayout(resources, stageFlags, bindingCount);
            resources.DescriptorSetLayout = layout.DescriptorSetLayout;
            resources.PipelineLayout = layout.PipelineLayout;
            resources.DescriptorLayoutCached = true;
            if (bindingCount == 0)
            {
                return;
            }

            var setLayout = layout.DescriptorSetLayout;

            var poolSizes = new DescriptorPoolSize[
                (sampledImageCount == 0 ? 0 : 1) +
                (storageImageCount == 0 ? 0 : 1) +
                (globalBufferCount == 0 ? 0 : 1)];
            var poolSizeIndex = 0;
            if (sampledImageCount != 0)
            {
                poolSizes[poolSizeIndex++] = new DescriptorPoolSize
                {
                    Type = DescriptorType.CombinedImageSampler,
                    DescriptorCount = (uint)sampledImageCount,
                };
            }

            if (storageImageCount != 0)
            {
                poolSizes[poolSizeIndex++] = new DescriptorPoolSize
                {
                    Type = DescriptorType.StorageImage,
                    DescriptorCount = (uint)storageImageCount,
                };
            }

            if (globalBufferCount != 0)
            {
                poolSizes[poolSizeIndex] = new DescriptorPoolSize
                {
                    Type = DescriptorType.StorageBuffer,
                    DescriptorCount = (uint)globalBufferCount,
                };
            }

            if (_recycledDescriptorPools.TryPop(out var recycledPool))
            {
                Check(
                    _vk.ResetDescriptorPool(_device, recycledPool, 0),
                    "vkResetDescriptorPool");
                resources.DescriptorPool = recycledPool;
            }
            else
            {
                // Generously sized so any draw's set fits, making the pool
                // recyclable regardless of the draw's binding mix. AAA titles
                // (e.g. Demon's Souls) bind well over 32 textures in a single
                // descriptor set, so the sampled-image budget in particular
                // must be large enough to avoid a per-draw dynamic fallback.
                var genericPoolSizes = stackalloc DescriptorPoolSize[3];
                genericPoolSizes[0] = new DescriptorPoolSize
                {
                    Type = DescriptorType.CombinedImageSampler,
                    DescriptorCount = 256,
                };
                genericPoolSizes[1] = new DescriptorPoolSize
                {
                    Type = DescriptorType.StorageImage,
                    DescriptorCount = 64,
                };
                genericPoolSizes[2] = new DescriptorPoolSize
                {
                    Type = DescriptorType.StorageBuffer,
                    DescriptorCount = 64,
                };
                var poolInfo = new DescriptorPoolCreateInfo
                {
                    SType = StructureType.DescriptorPoolCreateInfo,
                    MaxSets = 1,
                    PoolSizeCount = 3,
                    PPoolSizes = genericPoolSizes,
                };
                DescriptorPool descriptorPool;
                Check(
                    _vk.CreateDescriptorPool(
                        _device,
                        &poolInfo,
                        null,
                        out descriptorPool),
                    "vkCreateDescriptorPool");
                resources.DescriptorPool = descriptorPool;
            }

            var allocateInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = resources.DescriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &setLayout,
            };
            DescriptorSet descriptorSet;
            Check(
                _vk.AllocateDescriptorSets(_device, &allocateInfo, out descriptorSet),
                "vkAllocateDescriptorSets");
            resources.DescriptorSet = descriptorSet;

            var imageInfos = new DescriptorImageInfo[textureCount];
            var bufferInfos = new DescriptorBufferInfo[globalBufferCount];
            var writes = new WriteDescriptorSet[bindingCount];
            fixed (DescriptorImageInfo* imageInfoPointer = imageInfos)
            fixed (DescriptorBufferInfo* bufferInfoPointer = bufferInfos)
            fixed (WriteDescriptorSet* writePointer = writes)
            {
                var writeIndex = 0;
                if (globalBufferCount != 0)
                {
                    for (var index = 0; index < globalBufferCount; index++)
                    {
                        bufferInfoPointer[index] = new DescriptorBufferInfo
                        {
                            Buffer = resources.GlobalMemoryBuffers[index].Buffer,
                            Offset = resources.GlobalMemoryBuffers[index].Offset,
                            Range = resources.GlobalMemoryBuffers[index].Size,
                        };
                    }

                    writePointer[writeIndex++] = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = resources.DescriptorSet,
                        DstBinding = 0,
                        DescriptorCount = (uint)globalBufferCount,
                        DescriptorType = DescriptorType.StorageBuffer,
                        PBufferInfo = bufferInfoPointer,
                    };
                }

                for (var index = 0; index < textureCount; index++)
                {
                    var isStorage = resources.Textures[index].IsStorage;
                    if (!isStorage && resources.Textures[index].Sampler.Handle == 0)
                    {
                        resources.Textures[index].Sampler = ResolveSampler(resources.Textures[index].SamplerState);
                    }

                    imageInfoPointer[index] = new DescriptorImageInfo
                    {
                        Sampler = isStorage ? default : resources.Textures[index].Sampler,
                        ImageView = resources.Textures[index].View,
                        ImageLayout = resources.Textures[index].Layout,
                    };
                    writePointer[writeIndex++] = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = resources.DescriptorSet,
                        DstBinding = (uint)(index + 1),
                        DescriptorCount = 1,
                        DescriptorType = isStorage
                            ? DescriptorType.StorageImage
                            : DescriptorType.CombinedImageSampler,
                        PImageInfo = &imageInfoPointer[index],
                    };
                }

                _vk.UpdateDescriptorSets(
                    _device,
                    (uint)bindingCount,
                    writePointer,
                    0,
                    null);
            }
        }

        private DescriptorLayoutBundle GetOrCreateDescriptorLayout(
            TranslatedDrawResources resources,
            ShaderStageFlags stageFlags,
            int bindingCount)
        {
            _ = bindingCount;
            var storageImages = new bool[resources.Textures.Length];
            for (var index = 0; index < storageImages.Length; index++)
            {
                storageImages[index] = resources.Textures[index].IsStorage;
            }

            return GetOrCreateDescriptorLayout(stageFlags, GetResourceLayoutKey(resources), resources.GlobalMemoryBuffers.Length, storageImages);
        }

        // Binding 0 holds every storage buffer; the images follow one binding each.
        private DescriptorLayoutBundle GetOrCreateDescriptorLayout(
            ShaderStageFlags stageFlags,
            string resourceKey,
            int globalBufferCount,
            ReadOnlySpan<bool> storageImages)
        {
            var key = new DescriptorLayoutKey(stageFlags, resourceKey);
            if (_descriptorLayouts.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var bindingCount = storageImages.Length + (globalBufferCount == 0 ? 0 : 1);
            DescriptorSetLayout descriptorSetLayout = default;
            if (bindingCount != 0)
            {
                var bindings = new DescriptorSetLayoutBinding[bindingCount];
                var bindingOffset = 0;
                if (globalBufferCount != 0)
                {
                    bindings[bindingOffset++] = new DescriptorSetLayoutBinding
                    {
                        Binding = 0,
                        DescriptorType = DescriptorType.StorageBuffer,
                        DescriptorCount = (uint)globalBufferCount,
                        StageFlags = stageFlags,
                    };
                }

                for (var index = 0; index < storageImages.Length; index++)
                {
                    bindings[bindingOffset + index] = new DescriptorSetLayoutBinding
                    {
                        Binding = (uint)(index + 1),
                        DescriptorType = storageImages[index]
                            ? DescriptorType.StorageImage
                            : DescriptorType.CombinedImageSampler,
                        DescriptorCount = 1,
                        StageFlags = stageFlags,
                    };
                }

                fixed (DescriptorSetLayoutBinding* bindingPointer = bindings)
                {
                    var descriptorInfo = new DescriptorSetLayoutCreateInfo
                    {
                        SType = StructureType.DescriptorSetLayoutCreateInfo,
                        BindingCount = (uint)bindings.Length,
                        PBindings = bindingPointer,
                    };
                    Check(
                        _vk.CreateDescriptorSetLayout(
                            _device,
                            &descriptorInfo,
                            null,
                            out descriptorSetLayout),
                        "vkCreateDescriptorSetLayout");
                }
            }

            var pipelineInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
            };
            if (descriptorSetLayout.Handle != 0)
            {
                pipelineInfo.SetLayoutCount = 1;
                pipelineInfo.PSetLayouts = &descriptorSetLayout;
            }

            var computePushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Offset = 0,
                Size = 3 * sizeof(uint),
            };
            if ((stageFlags & ShaderStageFlags.ComputeBit) != 0)
            {
                pipelineInfo.PushConstantRangeCount = 1;
                pipelineInfo.PPushConstantRanges = &computePushConstantRange;
            }

            PipelineLayout pipelineLayout;
            Check(
                _vk.CreatePipelineLayout(
                    _device,
                    &pipelineInfo,
                    null,
                    out pipelineLayout),
                "vkCreatePipelineLayout");
            var created = new DescriptorLayoutBundle(descriptorSetLayout, pipelineLayout);
            _descriptorLayouts.Add(key, created);
            return created;
        }

        private string GetShaderDigest(byte[] spirv)
        {
            if (_shaderDigests.TryGetValue(spirv, out var digest))
            {
                return digest;
            }

            digest = Convert.ToHexString(SHA256.HashData(spirv));
            _shaderDigests.Add(spirv, digest);
            return digest;
        }

        private static string GetResourceLayoutKey(TranslatedDrawResources resources) =>
            resources.ResourceLayoutKey ??= BuildResourceLayoutKey(resources);

        private static string BuildResourceLayoutKey(TranslatedDrawResources resources)
        {
            var key = new StringBuilder();
            key.Append(resources.GlobalMemoryBuffers.Length).Append(':');
            foreach (var texture in resources.Textures)
            {
                key.Append(texture.IsStorage ? 'S' : 'T');
            }

            return key.ToString();
        }

    }
}
