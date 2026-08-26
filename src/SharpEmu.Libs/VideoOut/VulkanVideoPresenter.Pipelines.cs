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
        // Compute translation can produce an equivalent new byte array on a
        // later submit. Reference identity turns that into an expensive new
        // MoltenVK pipeline compilation every frame, so key the cache by the
        // program content and descriptor-layout shape instead.
        private readonly Dictionary<ComputePipelineKey, Pipeline> _computePipelines = new();
        private readonly Dictionary<GraphicsPipelineKey, Pipeline> _graphicsPipelines = new();
        private readonly Dictionary<byte[], string> _shaderDigests =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<DescriptorLayoutKey, DescriptorLayoutBundle>
            _descriptorLayouts = new();
        private readonly Stack<DescriptorPool> _recycledDescriptorPools = new();

        private readonly record struct GraphicsPipelineKey(
            string VertexShader,
            string FragmentShader,
            string RenderTargetLayout,
            bool HasDepthAttachment,
            PrimitiveTopology Topology,
            string BlendLayout,
            string ResourceLayout,
            string VertexLayout,
            GuestRasterState Raster,
            GuestDepthState Depth);

        private readonly record struct DescriptorLayoutKey(
            ShaderStageFlags Stages,
            string Resources);

        private readonly record struct ComputePipelineKey(
            string ShaderDigest,
            string Resources);

        private sealed record DescriptorLayoutBundle(
            DescriptorSetLayout DescriptorSetLayout,
            PipelineLayout PipelineLayout);
        private static int _shaderModuleDumpSequence;

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

        private TranslatedDrawResources CreateTranslatedDrawResources(
            VulkanTranslatedGuestDraw draw,
            RenderPass renderPass,
            IReadOnlyList<Format> renderTargetFormats,
            Extent2D extent,
            IReadOnlyList<GuestImageResource>? feedbackTargets = null,
            bool hasDepthAttachment = false,
            GuestDepthResource? feedbackDepth = null,
            GuestDepthResource? directReadOnlyDepthFeedback = null)
        {
            var isTitleDraw = IsTitleDraw(draw.VertexBuffers);
            var forceFullscreenVertex = _forceFullscreenPipeline ||
                _forceFullscreenVertex ||
                isTitleDraw && _forceTitleFullscreenVertex ||
                AnyTargetAddressMatches(
                    feedbackTargets,
                    "SHARPEMU_FORCE_FULLSCREEN_VERTEX_TARGETS");
            var forceRasterState = _forceFullscreenPipeline ||
                _forceDefaultRasterState ||
                isTitleDraw && _forceTitleDefaultRasterState ||
                AnyTargetAddressMatches(
                    feedbackTargets,
                    "SHARPEMU_FORCE_DEFAULT_RASTER_STATE_TARGETS");
            var forceTitleSolidFragment =
                _forceTitleSolidFragment &&
                isTitleDraw;
            var forceSolidFragment = forceTitleSolidFragment ||
                _forceFullscreenPipeline ||
                _forceSolidFragment ||
                AnyTargetAddressMatches(
                    feedbackTargets,
                    "SHARPEMU_FORCE_SOLID_FRAGMENT_TARGETS");
            var attributeFragmentLocation =
                _forceAttributeFragmentLocation.GetValueOrDefault();
            var forceAttributeFragment =
                _forceAttributeFragmentLocation.HasValue &&
                AnyTargetAddressMatches(
                    feedbackTargets,
                    "SHARPEMU_FORCE_ATTRIBUTE_FRAGMENT_TARGETS");
            var vertexSpirv = forceFullscreenVertex
                ? SpirvFixedShaders.CreateFullscreenVertex(0)
                : draw.VertexSpirv;
            var fragmentSpirv = forceSolidFragment
                ? SpirvFixedShaders.CreateSolidFragment(1f, 0f, 1f, 1f)
                : forceAttributeFragment
                    ? SpirvFixedShaders.CreateAttributeFragment(attributeFragmentLocation)
                : draw.PixelSpirv;
            if (forceSolidFragment && !string.IsNullOrWhiteSpace(_fixedFragmentDumpPath))
            {
                File.WriteAllBytes(_fixedFragmentDumpPath, fragmentSpirv);
            }
            if (draw.RenderState.Blends.Count != renderTargetFormats.Count)
            {
                throw new InvalidOperationException(
                    "color attachment formats and blend states must have matching counts");
            }
            if (vertexSpirv.Length == 0 &&
                !TryCompileFullscreenVertexShader(
                    draw.AttributeCount,
                    out vertexSpirv,
                    out var vertexError))
            {
                throw new InvalidOperationException($"translated vertex shader failed: {vertexError}");
            }

            var resources = new TranslatedDrawResources
            {
                DebugName = "SharpEmu draw",
                Textures = new TextureResource[draw.Textures.Count],
                GlobalMemoryBuffers =
                    new GlobalBufferResource[draw.GlobalMemoryBuffers.Count],
                VertexBuffers = new VertexBufferResource[draw.VertexBuffers.Count],
                VertexCount = GetDrawVertexCount(
                    draw.PrimitiveType,
                    draw.VertexCount,
                    draw.IndexBuffer,
                    draw.VertexBuffers.Count > 0),
                InstanceCount = Math.Max(draw.InstanceCount, 1),
                BaseVertex = draw.BaseVertex,
                Topology = GetPrimitiveTopology(
                    draw.PrimitiveType,
                    indexed: draw.IndexBuffer is not null,
                    vertexCount: draw.VertexCount,
                    hasVertexBuffers: draw.VertexBuffers.Count > 0),
                Blends = draw.RenderState.Blends.ToArray(),
                BlendConstant = draw.RenderState.BlendConstant,
                Scissor = draw.RenderState.Scissor,
                Viewport = draw.RenderState.Viewport,
                Raster = draw.RenderState.Raster,
                Depth = draw.RenderState.Depth,
                HasDepthAttachment = hasDepthAttachment,
                TargetFormats = renderTargetFormats.ToArray(),
            };
            if (forceFullscreenVertex)
            {
                resources.VertexCount = 3;
                resources.InstanceCount = 1;
                resources.Topology = PrimitiveTopology.TriangleList;
            }
            if (forceRasterState)
            {
                resources.Blends = Enumerable.Repeat(
                    GuestBlendState.Default,
                    renderTargetFormats.Count).ToArray();
                resources.Scissor = null;
                resources.Viewport = null;
                resources.Raster = GuestRasterState.Default;
                resources.Depth = GuestDepthState.Default;
            }
            if (isTitleDraw && _forceTitleDefaultBlend)
            {
                resources.Blends = Enumerable.Repeat(
                    GuestBlendState.Default,
                    renderTargetFormats.Count).ToArray();
            }
            if (isTitleDraw && _forceTitleDefaultViewportScissor)
            {
                resources.Scissor = null;
                resources.Viewport = null;
            }
            if (isTitleDraw && _forceTitleDisableCull)
            {
                resources.Raster = resources.Raster with
                {
                    CullFront = false,
                    CullBack = false,
                };
            }
            if (isTitleDraw && _forceTitleDisableDepth)
            {
                resources.Depth = GuestDepthState.Default;
            }
            if (isTitleDraw && _traceTitleState)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.title_state " +
                    $"viewport={resources.Viewport} scissor={resources.Scissor} " +
                    $"raster={resources.Raster} depth={resources.Depth} " +
                    $"blends=[{string.Join(',', resources.Blends)}]");
            }

            try
            {
                foreach (var texture in draw.Textures)
                {
                    // Skip address-0 storage bindings here: the real resolution
                    // path uses a scratch image for those, but this warm-up pass
                    // called ResolveStorageGuestImage directly, which throws on
                    // address 0 and dropped the whole draw (Demon's Souls G-buffer
                    // normals/IDs passes -> lighting had no input -> black).
                    if (texture.IsStorage && texture.Address != 0)
                    {
                        _ = ResolveStorageGuestImage(texture);
                    }
                }

                var hostMovieTextures = FindHostMovieTextureBindings(draw.Textures);
                for (var index = 0; index < draw.Textures.Count; index++)
                {
                    var texture = draw.Textures[index];
                    var usesDirectReadOnlyDepth =
                        directReadOnlyDepthFeedback is not null &&
                        IsMatchingGuestDepthTexture(
                            texture,
                            directReadOnlyDepthFeedback);
                    var resolved = usesDirectReadOnlyDepth
                        ? CreateReadOnlyDepthFeedbackResource(
                            texture,
                            directReadOnlyDepthFeedback!)
                        : index == hostMovieTextures.Luma
                        ? CreateHostMovieTextureResource(texture, plane: 0)
                        : index == hostMovieTextures.Chroma
                            ? CreateHostMovieTextureResource(texture, plane: 1)
                            : ResolveTextureResource(texture);
                    var feedbackTarget = !texture.IsStorage
                        ? feedbackTargets?.FirstOrDefault(target =>
                            ReferenceEquals(resolved.GuestImage, target))
                        : null;
                    // ResolveTextureResource may deliberately decline an
                    // address alias when the descriptor is incompatible with
                    // the render-target image. Only snapshot an alias which
                    // actually resolved to the target; a separately uploaded
                    // texture has no Vulkan attachment feedback hazard.
                    resources.Textures[index] =
                        usesDirectReadOnlyDepth
                            ? resolved
                            : feedbackDepth is not null &&
                        !texture.IsStorage &&
                        ReferenceEquals(resolved.GuestDepth, feedbackDepth)
                            ? CreateDepthFeedbackSnapshot(texture, feedbackDepth)
                            :
                        feedbackTarget is not null &&
                        !texture.IsStorage &&
                        ReferenceEquals(resolved.GuestImage, feedbackTarget)
                            ? CreateRenderTargetFeedbackSnapshot(texture, feedbackTarget)
                            : resolved;
                }

                PrepareGuestBufferAllocations(draw.GlobalMemoryBuffers);
                for (var index = 0; index < draw.GlobalMemoryBuffers.Count; index++)
                {
                    resources.GlobalMemoryBuffers[index] =
                        CreateGlobalBufferResource(draw.GlobalMemoryBuffers[index]);
                }

                var sharedVertexResources = new Dictionary<
                    byte[], VertexBufferResource>(
                    System.Collections.Generic.ReferenceEqualityComparer.Instance);
                for (var index = 0; index < draw.VertexBuffers.Count; index++)
                {
                    var guestVertex = draw.VertexBuffers[index];
                    if (sharedVertexResources.TryGetValue(
                            guestVertex.Data,
                            out var sharedVertex))
                    {
                        resources.VertexBuffers[index] =
                            CreateVertexBufferAlias(sharedVertex, guestVertex);
                    }
                    else
                    {
                        var vertexResource =
                            CreateVertexBufferResource(guestVertex);
                        resources.VertexBuffers[index] = vertexResource;
                        sharedVertexResources.Add(guestVertex.Data, vertexResource);
                    }
                }

                if (draw.IndexBuffer is { Length: > 0 } indexBuffer)
                {
                    resources.IndexBuffer = CreateHostBuffer(
                        indexBuffer.Data.AsSpan(0, indexBuffer.Length),
                        BufferUsageFlags.IndexBufferBit,
                        out resources.IndexMemory,
                        out _);
                    resources.Index32Bit = indexBuffer.Is32Bit;
                    if (indexBuffer.Pooled)
                    {
                        indexBuffer.TryReturnPooledData();
                    }
                }

                CreateTranslatedDescriptorResources(
                    resources,
                    ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit);
                CreateTranslatedPipeline(
                    resources,
                    vertexSpirv,
                    fragmentSpirv,
                    renderPass,
                    renderTargetFormats,
                    extent);
                return resources;
            }
            catch
            {
                DestroyTranslatedDrawResources(resources);
                throw;
            }
            finally
            {
                var returnedVertexData = new HashSet<byte[]>(
                    System.Collections.Generic.ReferenceEqualityComparer.Instance);
                foreach (var vertex in draw.VertexBuffers)
                {
                    if (vertex.Pooled && returnedVertexData.Add(vertex.Data))
                    {
                        GuestDataPool.Shared.Return(vertex.Data);
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private TranslatedDrawResources CreateComputeDispatchResources(
            VulkanComputeGuestDispatch dispatch)
        {
            var traceResources = dispatch.Textures.Count >= 8;
            if (traceResources)
            {
                TraceVulkanShader(
                    $"vk.compute_resources begin groups={dispatch.GroupCountX}x" +
                    $"{dispatch.GroupCountY}x{dispatch.GroupCountZ} textures={dispatch.Textures.Count}");
            }

            var resources = new TranslatedDrawResources
            {
                DebugName = BuildComputeDebugName(dispatch),
                Textures = new TextureResource[dispatch.Textures.Count],
                GlobalMemoryBuffers =
                    new GlobalBufferResource[dispatch.GlobalMemoryBuffers.Count],
            };

            try
            {
                for (var index = 0; index < dispatch.Textures.Count; index++)
                {
                    var texture = dispatch.Textures[index];
                    // Address-zero storage descriptors are valid scratch bindings.
                    // ResolveTextureResource creates their transient image below;
                    // pre-resolving them as guest-backed images throws and drops
                    // the entire compute dispatch before that path can run.
                    if (texture.IsStorage && texture.Address != 0)
                    {
                        if (traceResources)
                        {
                            TraceVulkanShader(
                                $"vk.compute_resources storage[{index}] begin " +
                                $"addr=0x{texture.Address:X16} fmt={texture.Format} " +
                                $"size={texture.Width}x{texture.Height} " +
                                $"view_mips={texture.BaseMipLevel}+{texture.MipLevels} " +
                                $"resource_mips={texture.ResourceMipLevels} " +
                                $"relative_level={texture.MipLevel}");
                        }

                        _ = ResolveStorageGuestImage(texture);
                        if (traceResources)
                        {
                            TraceVulkanShader($"vk.compute_resources storage[{index}] ready");
                        }
                    }
                }

                if (traceResources)
                {
                    TraceVulkanShader("vk.compute_resources resolve begin");
                }

                for (var index = 0; index < dispatch.Textures.Count; index++)
                {
                    resources.Textures[index] =
                        ResolveTextureResource(dispatch.Textures[index]);
                }

                if (traceResources)
                {
                    TraceVulkanShader("vk.compute_resources resolve ready");
                }

                PrepareGuestBufferAllocations(dispatch.GlobalMemoryBuffers);
                for (var index = 0; index < dispatch.GlobalMemoryBuffers.Count; index++)
                {
                    resources.GlobalMemoryBuffers[index] =
                        CreateGlobalBufferResource(dispatch.GlobalMemoryBuffers[index]);
                }

                if (traceResources)
                {
                    TraceVulkanShader("vk.compute_resources descriptors begin");
                }

                CreateTranslatedDescriptorResources(resources, ShaderStageFlags.ComputeBit);
                if (traceResources)
                {
                    TraceVulkanShader("vk.compute_resources descriptors ready");
                }

                if (traceResources)
                {
                    TraceVulkanShader(
                        $"vk.compute_resources pipeline begin " +
                        $"cs=0x{dispatch.ShaderAddress:X16} " +
                        $"spirv={dispatch.ComputeSpirv.Length} " +
                        $"textures={resources.Textures.Length} " +
                        $"globals={resources.GlobalMemoryBuffers.Length}");
                }

                CreateComputePipeline(resources, dispatch.ComputeSpirv);
                if (traceResources)
                {
                    TraceVulkanShader("vk.compute_resources pipeline ready");
                }

                return resources;
            }
            catch
            {
                DestroyTranslatedDrawResources(resources);
                throw;
            }
        }

        private static bool TryCompileFullscreenVertexShader(
            uint attributeCount,
            out byte[] spirv,
            out string error)
        {
            spirv = [];
            error = string.Empty;
            if (attributeCount > 32)
            {
                error = $"too many interpolated attributes: {attributeCount}";
                return false;
            }

            spirv = SpirvFixedShaders.CreateFullscreenVertex(attributeCount);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CreateTranslatedDescriptorResources(
            TranslatedDrawResources resources,
            ShaderStageFlags stageFlags)
        {
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
                    if (!isStorage &&
                        resources.Textures[index].Sampler.Handle == 0)
                    {
                        resources.Textures[index].Sampler =
                            CreateSampler(resources.Textures[index].SamplerState);
                    }

                    imageInfoPointer[index] = new DescriptorImageInfo
                    {
                        Sampler = isStorage ? default : resources.Textures[index].Sampler,
                        ImageView = resources.Textures[index].View,
                        ImageLayout = resources.Textures[index].ReadOnlyDepthFeedback
                            ? ImageLayout.DepthStencilReadOnlyOptimal
                            : isStorage ||
                            resources.Textures[index].GuestImage is { } guestImage &&
                            resources.Textures.Any(
                                texture =>
                                    texture.IsStorage &&
                                    texture.GuestImage == guestImage)
                                ? ImageLayout.General
                                : ImageLayout.ShaderReadOnlyOptimal,
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

        private void CreateTranslatedPipeline(
            TranslatedDrawResources resources,
            byte[] vertexSpirv,
            byte[] fragmentSpirv,
            RenderPass renderPass,
            IReadOnlyList<Format> renderTargetFormats,
            Extent2D extent)
        {
            var pipelineKey = new GraphicsPipelineKey(
                GetShaderDigest(vertexSpirv),
                GetShaderDigest(fragmentSpirv),
                string.Join(',', renderTargetFormats.Select(format => (uint)format)),
                resources.HasDepthAttachment,
                resources.Topology,
                string.Join(';', resources.Blends.Select(blend =>
                    $"{(blend.Enable ? 1 : 0)}:{blend.ColorSrcFactor}:{blend.ColorDstFactor}:" +
                    $"{blend.ColorFunc}:{blend.AlphaSrcFactor}:{blend.AlphaDstFactor}:" +
                    $"{blend.AlphaFunc}:{(blend.SeparateAlphaBlend ? 1 : 0)}:{blend.WriteMask}")),
                GetResourceLayoutKey(resources),
                GetVertexLayoutKey(resources),
                resources.Raster,
                resources.HasDepthAttachment ? resources.Depth : GuestDepthState.Default);
            if (_graphicsPipelines.TryGetValue(pipelineKey, out var cachedPipeline))
            {
                resources.Pipeline = cachedPipeline;
                resources.PipelineCached = true;
                return;
            }

            var vertexModule = CreateShaderModule(vertexSpirv);
            var fragmentModule = CreateShaderModule(fragmentSpirv);
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

                // One Vulkan binding per unique host buffer and input rate
                // (fetch_index). Attributes share that binding with
                // Offset = OffsetBytes.
                var bindingByBuffer = new Dictionary<(ulong Handle, bool PerInstance), uint>();
                var vertexBindingList = new List<VertexInputBindingDescription>();
                var vertexAttributeDescriptions =
                    new VertexInputAttributeDescription[resources.VertexBuffers.Length];
                for (var index = 0; index < resources.VertexBuffers.Length; index++)
                {
                    var vertexBuffer = resources.VertexBuffers[index];
                    var bufferKey = (vertexBuffer.Buffer.Handle, vertexBuffer.PerInstance);
                    if (!bindingByBuffer.TryGetValue(bufferKey, out var bindingIndex))
                    {
                        bindingIndex = (uint)vertexBindingList.Count;
                        bindingByBuffer[bufferKey] = bindingIndex;
                        vertexBindingList.Add(new VertexInputBindingDescription
                        {
                            Binding = bindingIndex,
                            Stride = vertexBuffer.Stride == 0
                                ? Math.Max(vertexBuffer.ComponentCount, 1) * sizeof(float)
                                : vertexBuffer.Stride,
                            InputRate = vertexBuffer.PerInstance
                                ? VertexInputRate.Instance
                                : VertexInputRate.Vertex,
                        });
                    }

                    vertexAttributeDescriptions[index] = new VertexInputAttributeDescription
                    {
                        Location = vertexBuffer.Location,
                        Binding = bindingIndex,
                        Format = ToVkVertexFormat(
                            vertexBuffer.DataFormat,
                            vertexBuffer.NumberFormat,
                            vertexBuffer.ComponentCount),
                        Offset = vertexBuffer.OffsetBytes,
                    };
                }

                var vertexBindingDescriptions = vertexBindingList.ToArray();

                fixed (VertexInputBindingDescription* vertexBindingPointerBase = vertexBindingDescriptions)
                fixed (VertexInputAttributeDescription* vertexAttributePointerBase = vertexAttributeDescriptions)
                {
                    var vertexInput = new PipelineVertexInputStateCreateInfo
                    {
                        SType = StructureType.PipelineVertexInputStateCreateInfo,
                        VertexBindingDescriptionCount = (uint)vertexBindingDescriptions.Length,
                        PVertexBindingDescriptions = vertexBindingDescriptions.Length == 0
                            ? null
                            : vertexBindingPointerBase,
                        VertexAttributeDescriptionCount = (uint)vertexAttributeDescriptions.Length,
                        PVertexAttributeDescriptions = vertexAttributeDescriptions.Length == 0
                            ? null
                            : vertexAttributePointerBase,
                    };
                    var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                    {
                        SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                        Topology = resources.Topology,
                        // Metal always applies primitive restart to strip/fan
                        // topologies with the max index as the cut value, so
                        // match that here. Enabling it for lists is what makes
                        // MoltenVK warn ("Metal does not support disabling
                        // primitive restart"); lists never carry a restart
                        // index, so leaving it off for them is both correct
                        // and warning-free.
                        PrimitiveRestartEnable = RequiresPrimitiveRestart(resources.Topology),
                    };
                    var viewport = new Viewport(0, 0, extent.Width, extent.Height, 0, 1);
                    var scissor = new Rect2D(new Offset2D(0, 0), extent);
                    var viewportState = new PipelineViewportStateCreateInfo
                    {
                        SType = StructureType.PipelineViewportStateCreateInfo,
                        ViewportCount = 1,
                        PViewports = &viewport,
                        ScissorCount = 1,
                        PScissors = &scissor,
                    };
                    var raster = resources.Raster;
                    var cullMode = CullModeFlags.None;
                    if (raster.CullFront)
                    {
                        cullMode |= CullModeFlags.FrontBit;
                    }

                    if (raster.CullBack)
                    {
                        cullMode |= CullModeFlags.BackBit;
                    }

                    var rasterization = new PipelineRasterizationStateCreateInfo
                    {
                        SType = StructureType.PipelineRasterizationStateCreateInfo,
                        // Wireframe (PolygonMode.Line) needs the fillModeNonSolid
                        // device feature and is effectively unused by shipping
                        // titles, so fall back to a solid fill.
                        PolygonMode = PolygonMode.Fill,
                        CullMode = cullMode,
                        FrontFace = raster.FrontFaceClockwise
                            ? FrontFace.Clockwise
                            : FrontFace.CounterClockwise,
                        DepthBiasEnable = raster.DepthBiasEnable,
                        // D32Sfloat cannot reproduce fixed-point guest bias
                        // scaling without the depth-bias-control extension.
                        DepthBiasConstantFactor = raster.ResolveDepthBiasConstantFactor(
                            hostDepthBits: 0),
                        DepthBiasClamp = _supportsDepthBiasClamp
                            ? raster.DepthBiasClamp
                            : 0,
                        DepthBiasSlopeFactor = raster.DepthBiasSlopeFactor,
                        LineWidth = 1,
                    };
                    var multisample = new PipelineMultisampleStateCreateInfo
                    {
                        SType = StructureType.PipelineMultisampleStateCreateInfo,
                        RasterizationSamples = SampleCountFlags.Count1Bit,
                    };
                    var colorBlendAttachments = stackalloc PipelineColorBlendAttachmentState[resources.Blends.Length];
                    for (var index = 0; index < resources.Blends.Length; index++)
                    {
                        var blend = resources.Blends[index];
                        colorBlendAttachments[index] = new PipelineColorBlendAttachmentState
                        {
                            BlendEnable = blend.Enable &&
                                IsBlendableFormat(renderTargetFormats[index]),
                            SrcColorBlendFactor = ToVkBlendFactor(blend.ColorSrcFactor),
                            DstColorBlendFactor = ToVkBlendFactor(blend.ColorDstFactor),
                            ColorBlendOp = ToVkBlendOp(blend.ColorFunc),
                            SrcAlphaBlendFactor = blend.SeparateAlphaBlend
                                ? ToVkBlendFactor(blend.AlphaSrcFactor)
                                : ToVkBlendFactor(blend.ColorSrcFactor),
                            DstAlphaBlendFactor = blend.SeparateAlphaBlend
                                ? ToVkBlendFactor(blend.AlphaDstFactor)
                                : ToVkBlendFactor(blend.ColorDstFactor),
                            AlphaBlendOp = blend.SeparateAlphaBlend
                                ? ToVkBlendOp(blend.AlphaFunc)
                                : ToVkBlendOp(blend.ColorFunc),
                            ColorWriteMask = ToVkColorWriteMask(blend.WriteMask),
                        };
                    }
                    var colorBlend = new PipelineColorBlendStateCreateInfo
                    {
                        SType = StructureType.PipelineColorBlendStateCreateInfo,
                        AttachmentCount = (uint)resources.Blends.Length,
                        PAttachments = colorBlendAttachments,
                    };
                    var dynamicStateValues = stackalloc DynamicState[3];
                    dynamicStateValues[0] = DynamicState.Viewport;
                    dynamicStateValues[1] = DynamicState.Scissor;
                    // CB_BLEND_RED..ALPHA vary per draw without a pipeline
                    // identity change, so the constant stays dynamic.
                    dynamicStateValues[2] = DynamicState.BlendConstants;
                    var dynamicState = new PipelineDynamicStateCreateInfo
                    {
                        SType = StructureType.PipelineDynamicStateCreateInfo,
                        DynamicStateCount = 3,
                        PDynamicStates = dynamicStateValues,
                    };
                    var depth = resources.Depth;
                    var depthStencil = new PipelineDepthStencilStateCreateInfo
                    {
                        SType = StructureType.PipelineDepthStencilStateCreateInfo,
                        DepthTestEnable = depth.TestEnable,
                        DepthWriteEnable = depth.WriteEnable,
                        DepthCompareOp = ToVkCompareOp(depth.CompareOp),
                        DepthBoundsTestEnable = false,
                        StencilTestEnable = false,
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
                        PDepthStencilState = resources.HasDepthAttachment ? &depthStencil : null,
                        PDynamicState = &dynamicState,
                        Layout = resources.PipelineLayout,
                        RenderPass = renderPass,
                        Subpass = 0,
                    };
                    Pipeline pipeline;
                    Check(
                        _vk.CreateGraphicsPipelines(
                            _device,
                            _pipelineCache,
                            1,
                            &pipelineInfo,
                        null,
                        out pipeline),
                    "vkCreateGraphicsPipelines(translated)");
                    MarkPipelineCacheDirty();
                    resources.Pipeline = pipeline;
                    resources.PipelineCached = true;
                    _graphicsPipelines.Add(pipelineKey, pipeline);
                    Interlocked.Increment(ref _perfPipelineCreations);
                    SetDebugName(
                        ObjectType.Pipeline,
                        pipeline.Handle,
                        $"SharpEmu graphics ps={fragmentSpirv.Length}b attrs={resources.Textures.Length}");
                }
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
                _vk.DestroyShaderModule(_device, fragmentModule, null);
                _vk.DestroyShaderModule(_device, vertexModule, null);
            }
        }

        private DescriptorLayoutBundle GetOrCreateDescriptorLayout(
            TranslatedDrawResources resources,
            ShaderStageFlags stageFlags,
            int bindingCount)
        {
            var key = new DescriptorLayoutKey(stageFlags, GetResourceLayoutKey(resources));
            if (_descriptorLayouts.TryGetValue(key, out var cached))
            {
                return cached;
            }

            DescriptorSetLayout descriptorSetLayout = default;
            if (bindingCount != 0)
            {
                var bindings = new DescriptorSetLayoutBinding[bindingCount];
                var bindingOffset = 0;
                if (resources.GlobalMemoryBuffers.Length != 0)
                {
                    bindings[bindingOffset++] = new DescriptorSetLayoutBinding
                    {
                        Binding = 0,
                        DescriptorType = DescriptorType.StorageBuffer,
                        DescriptorCount = (uint)resources.GlobalMemoryBuffers.Length,
                        StageFlags = stageFlags,
                    };
                }

                for (var index = 0; index < resources.Textures.Length; index++)
                {
                    bindings[bindingOffset + index] = new DescriptorSetLayoutBinding
                    {
                        Binding = (uint)(index + 1),
                        DescriptorType = resources.Textures[index].IsStorage
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

        private static string GetVertexLayoutKey(TranslatedDrawResources resources) =>
            resources.VertexLayoutKey ??= BuildVertexLayoutKey(resources);

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

        private static string BuildVertexLayoutKey(TranslatedDrawResources resources)
        {
            var key = new StringBuilder();
            foreach (var buffer in resources.VertexBuffers)
            {
                key.Append(buffer.Location).Append(',')
                    .Append(buffer.ComponentCount).Append(',')
                    .Append(buffer.DataFormat).Append(',')
                    .Append(buffer.NumberFormat).Append(',')
                    .Append(buffer.Stride == 0
                        ? Math.Max(buffer.ComponentCount, 1) * sizeof(float)
                        : buffer.Stride)
                    .Append(';');
            }

            return key.ToString();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CreateComputePipeline(
            TranslatedDrawResources resources,
            byte[] computeSpirv)
        {
            var pipelineKey = new ComputePipelineKey(
                GetShaderDigest(computeSpirv),
                GetResourceLayoutKey(resources));
            if (_computePipelines.TryGetValue(pipelineKey, out var cachedPipeline))
            {
                resources.Pipeline = cachedPipeline;
                resources.PipelineCached = true;
                return;
            }

            var computeModule = CreateShaderModule(computeSpirv);
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = computeModule,
                    PName = entryPoint,
                };
                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Flags = PipelineCreateFlags.CreateDispatchBaseBit,
                    Stage = stage,
                    Layout = resources.PipelineLayout,
                };
                Pipeline pipeline;
                Check(
                    _vk.CreateComputePipelines(
                        _device,
                        _pipelineCache,
                        1,
                        &pipelineInfo,
                        null,
                        out pipeline),
                    "vkCreateComputePipelines(translated)");
                MarkPipelineCacheDirty();
                resources.Pipeline = pipeline;
                resources.PipelineCached = true;
                SetDebugName(
                    ObjectType.Pipeline,
                    pipeline.Handle,
                    $"SharpEmu compute cs={computeSpirv.Length}b");
                _computePipelines.Add(pipelineKey, pipeline);
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
                _vk.DestroyShaderModule(_device, computeModule, null);
            }
        }


    }
}
