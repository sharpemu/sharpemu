// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using VkBuffer = Silk.NET.Vulkan.Buffer;

// This partial is the render host: it records what the executor resolved with dynamic rendering.
internal static unsafe partial class VulkanVideoPresenter
{
    private const string DynamicRenderingExtensionName = "VK_KHR_dynamic_rendering";
    private const string ExtendedDynamicStateExtensionName = "VK_EXT_extended_dynamic_state";
    private const string ExtendedDynamicState2ExtensionName = "VK_EXT_extended_dynamic_state2";
    private const string ColorWriteEnableExtensionName = "VK_EXT_color_write_enable";
    private const string DepthClipControlExtensionName = "VK_EXT_depth_clip_control";
    private const string DepthClipEnableExtensionName = "VK_EXT_depth_clip_enable";
    private const int DrawsPerBatch = 64;
    private const ulong MappedPageSize = 4096;
    private const uint SingleRectangleVertexCount = 4;
    private const int ComputeThreadLimitBytes = 3 * sizeof(uint);

    private static void RequireRenderingFeature(bool supported, string extension, string deviceName)
    {
        if (!supported)
        {
            throw SubmissionScheduler.Fatal($"The device lacks a required rendering feature: device={deviceName} extension={extension}.");
        }
    }

    private sealed partial class Presenter : IRenderHost, AgcExports.IHostPipelineFactory
    {
        // One created pipeline with the layout it binds and the description its variants derive from.
        private sealed class RenderPipelineEntry
        {
            public ulong Id;
            public Pipeline Pipeline;
            public PipelineLayout Layout;
            public DescriptorSetLayout SetLayout;
            public RenderPipelineDescription? Description;
            public Pipeline StripVariant;
            public Pipeline ListVariant;

            public bool RectangleList => Description is { Topology: PrimitiveTopology.PatchList };
        }

        // Everything a graphics pipeline creation reads; the cache key is built from it.
        private sealed record RenderPipelineDescription(
            byte[] VertexSpirv,
            byte[]? PixelSpirv,
            Format[] ColorFormats,
            Format DepthFormat,
            Format StencilFormat,
            SampleCountFlags Samples,
            PrimitiveTopology Topology,
            bool PrimitiveRestart,
            PipelineColorBlendAttachmentState[] Blends,
            CullModeFlags CullMode,
            FrontFace FrontFace,
            bool NegativeOneToOne,
            bool DepthClipEnable,
            bool DepthBoundsTest,
            float DepthMinBounds,
            float DepthMaxBounds,
            bool StencilTest,
            StencilOpState StencilFront,
            StencilOpState StencilBack,
            VertexInputBindingDescription[] VertexBindings,
            VertexInputAttributeDescription[] VertexAttributes,
            string ResourceKey);

        private readonly record struct RenderPipelineKey(string VertexShader, string PixelShader, string State);

        private readonly record struct ComputePipelineKey(string ShaderDigest, string Resources);

        private sealed class PreparedStageBindings(
            ShaderStageResources stage,
            AgcExports.CompiledStageProgram program,
            TextureResource[] textures,
            GlobalBufferResource[] globals,
            GlobalBufferResource? scalars) : IPreparedBindings
        {
            public ShaderStageResources Stage => stage;

            public AgcExports.CompiledStageProgram Program => program;

            public TextureResource[] Textures => textures;

            public GlobalBufferResource[] Globals => globals;

            public GlobalBufferResource? Scalars => scalars;
        }

        // The scope between the first binding and the recorded draw; the stream ring cannot wrap inside it.
        private sealed class RenderPreparation(Presenter owner, IDisposable streamRetention) : IResourcePreparation
        {
            public List<PreparedStageBindings> Stages { get; } = new();

            // Host buffers that took an upload the ring could not hold; the committed draw owns them.
            public List<(VkBuffer Buffer, DeviceMemory Memory)> OverflowBuffers { get; } = new();

            public Dictionary<CachedImage, CachedImage>? StencilStorageImages { get; set; }

            public bool Committed { get; set; }

            public bool CommandsRecorded { get; set; }

            public void Dispose()
            {
                if (!ReferenceEquals(owner._preparation, this))
                {
                    return;
                }

                owner._preparation = null;
                streamRetention.Dispose();
                if (StencilStorageImages is { } stencilImages)
                {
                    foreach (var (attachment, storage) in stencilImages)
                    {
                        try
                        {
                            if (CommandsRecorded)
                                attachment.CopyStencilStorage(storage, owner._bufferCache.GetUtilityBuffer(GpuBufferUsage.DeviceLocal), writeBack: true);
                        }
                        finally
                        {
                            owner._scheduler.QueueCompletionAction(storage.Dispose);
                        }
                    }
                }

                if (Committed)
                {
                    return;
                }

                foreach (var stage in Stages)
                {
                    owner.DestroyStageBindings(stage);
                }

                foreach (var (buffer, memory) in OverflowBuffers)
                {
                    owner.RecycleHostBuffer(buffer, memory);
                }
            }
        }

        private KhrDynamicRendering _dynamicRenderingApi = null!;
        private ExtExtendedDynamicState _extendedDynamicStateApi = null!;
        private ExtExtendedDynamicState2 _extendedDynamicState2Api = null!;
        private ExtColorWriteEnable? _colorWriteEnableApi;
        private bool _supportsDepthClipControl;
        private bool _supportsDepthClipEnable;
        private bool _supportsDepthBounds;
        private RenderHostLimits _renderHostLimits;
        private IGuestBackedSpace _guestBacking = null!;

        private readonly Dictionary<RenderPipelineKey, RenderPipelineEntry> _renderPipelines = new();
        private readonly Dictionary<ComputePipelineKey, RenderPipelineEntry> _computeEntries = new();
        private readonly Dictionary<ulong, RenderPipelineEntry> _pipelineEntries = new();
        private ulong _nextPipelineId;
        private RenderPreparation? _preparation;
        private RenderPipelineEntry? _boundGraphicsPipeline;
        private readonly uint[] _computeThreadLimits = new uint[3];
        private GlobalBufferResource? _nullBufferResource;

        private bool _renderingActive;
        private RenderingState _renderingState;
        private long _renderingScopesBegun;
        private bool _hasBoundDepth;
        private DepthAttachmentState _boundDepth;
        private ImageLayout _boundDepthLayout;
        private DepthStencilState _boundDepthLoadState;
        // A sampled depth target was cleared with a transfer; the rendering scope loads it instead.
        private bool _depthClearRecordedSeparately;

        private void LoadRenderingCommands(bool supportsColorWriteEnable, string deviceName)
        {
            if (!_vk.TryGetDeviceExtension(_instance, _device, out _dynamicRenderingApi))
            {
                throw SubmissionScheduler.Fatal($"The device extension commands are unavailable: device={deviceName} extension={DynamicRenderingExtensionName}.");
            }

            if (!_vk.TryGetDeviceExtension(_instance, _device, out _extendedDynamicStateApi))
            {
                throw SubmissionScheduler.Fatal($"The device extension commands are unavailable: device={deviceName} extension={ExtendedDynamicStateExtensionName}.");
            }

            if (!_vk.TryGetDeviceExtension(_instance, _device, out _extendedDynamicState2Api))
            {
                throw SubmissionScheduler.Fatal($"The device extension commands are unavailable: device={deviceName} extension={ExtendedDynamicState2ExtensionName}.");
            }

            if (supportsColorWriteEnable)
            {
                if (!_vk.TryGetDeviceExtension(_instance, _device, out ExtColorWriteEnable colorWriteEnable))
                {
                    throw SubmissionScheduler.Fatal($"The device extension commands are unavailable: device={deviceName} extension={ColorWriteEnableExtensionName}.");
                }

                _colorWriteEnableApi = colorWriteEnable;
            }

            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan rendering extensions color_write_enable={(supportsColorWriteEnable ? 1 : 0)} " +
                $"depth_clip_control={(_supportsDepthClipControl ? 1 : 0)} depth_clip_enable={(_supportsDepthClipEnable ? 1 : 0)} " +
                $"depth_bounds={(_supportsDepthBounds ? 1 : 0)}");
        }

        RenderHostLimits IRenderHost.Limits => _renderHostLimits;

        IImageFormatSupport IRenderHost.FormatSupport => _deviceInfo;

        public bool IsRecording
        {
            get
            {
                if (_deviceLost)
                {
                    return false;
                }

                _ = BeginBatchedGuestCommands();
                return _scheduler.Active;
            }
        }

        public void RunPendingOperations() => RunPendingCommands();

        public void SetDebugInformation(RecordedOperation operation, ulong submitId, uint argument0, uint argument1, uint argument2, uint argument3, ulong argument4) =>
            _scheduler.Current.SetDebugInfo((uint)operation, submitId, argument0, argument1, argument2, argument3, argument4);

        // The part of the range that is mapped from its start; unmapped starts are fatal as the executor cannot bind them.
        public ulong ClampMappedSize(ulong address, ulong size)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BufferMappedRangeValidation);
            if (address == 0 || size == 0 || size > ulong.MaxValue - address || !_guestBacking.IsBackedView(address))
            {
                throw SubmissionScheduler.Fatal($"The buffer range starts in unmapped memory: address=0x{address:X16} size=0x{size:X16}.");
            }

            if (_guestBacking.IsBackedRange(address, size))
            {
                return size;
            }

            var end = address + size;
            var page = (address & ~(MappedPageSize - 1)) + MappedPageSize;
            while (page < end && _guestBacking.IsBackedView(page))
            {
                page += MappedPageSize;
            }

            var clamped = Math.Min(size, page - address);
            if (RenderTrace.Enabled)
            {
                RenderTrace.Write($"Clamped a buffer range to its mapped part: address=0x{address:X16} size=0x{size:X16} clamped=0x{clamped:X16}");
            }

            return clamped;
        }

        public ResourceSlotIdentifier FindImage(ref ImageRequest request, bool exactFormat)
        {
            _ = BeginBatchedGuestCommands();
            return _imageCache.FindImage(ref request, exactFormat);
        }

        public void ResetBindings()
        {
            ResetImageBindings();
            _hasBoundDepth = false;
            _depthClearRecordedSeparately = false;
            _boundGraphicsPipeline = null;
        }

        ColorAttachmentAcquisition IRenderHost.AcquireColorAttachment(in ColorTargetState target)
        {
            var attachment = new ColorAttachment
            {
                ImageIdentifier = target.Image,
                Request = target.Resolution.Request,
                Resolution = target.Resolution,
            };
            AcquireColorAttachment(attachment);
            return new ColorAttachmentAcquisition(
                attachment.ImageIdentifier,
                attachment.View,
                attachment.Layout,
                attachment.Image.Backing.Samples,
                attachment.Clear,
                attachment.ClearValue);
        }

        DepthAttachmentAcquisition IRenderHost.AcquireDepthAttachment(in DepthAttachmentState depth)
        {
            var resolution = depth.Target.Target;
            if (IsStaleImage(depth.Image, out _))
            {
                throw SubmissionScheduler.Fatal($"The depth target changed after render-state discovery: address=0x{resolution.DepthAddress:X16}.");
            }

            var view = _imageCache.AcquireDepthTargetView(depth.Image, resolution.Request);
            _ = BeginBatchedGuestCommands();
            var layer = resolution.Request.View.BaseLayer;
            if (resolution.HasHtile && resolution.DepthClearEnabled && !_imageCache.ClearMetadata(resolution.HtileAddress))
            {
                throw SubmissionScheduler.Fatal($"The HTile metadata could not be acquired for a depth clear: htile=0x{resolution.HtileAddress:X16}.");
            }

            var metadataClear = resolution.HasHtile && _imageCache.IsMetadataCleared(resolution.HtileAddress, layer);
            if (metadataClear && !_imageCache.SetMetadataSlice(resolution.HtileAddress, layer, false))
            {
                throw SubmissionScheduler.Fatal($"The HTile clear state could not be consumed: htile=0x{resolution.HtileAddress:X16} layer={layer}.");
            }

            var image = _imageCache.GetImage(depth.Image);
            return new DepthAttachmentAcquisition(view, image.Backing.Samples, metadataClear);
        }

        // The image transition ends the scope only when it records a barrier.
        public void TransitionDepthAttachment(in DepthAttachmentState depth, ImageLayout layout, ImageAspectFlags writeAspects)
        {
            var command = BeginBatchedGuestCommands();
            var image = _imageCache.GetImage(depth.Image);
            var view = depth.Target.Target.Request.View;
            var access = AccessFlags.DepthStencilAttachmentReadBit | (writeAspects != 0 ? AccessFlags.DepthStencilAttachmentWriteBit : 0);
            image.Transition(layout, access, new SubresourceRange(view.BaseLevel, view.LevelCount, view.BaseLayer, view.LayerCount), command);
            _hasBoundDepth = true;
            _boundDepth = depth;
            _boundDepthLayout = layout;
            _boundDepthLoadState = depth.LoadState;
        }

        public BufferBinding NullBuffer => new(_bufferCache.GetBuffer(GuestBufferCache.NullBufferId).Handle.Handle, 0);

        public BufferBinding ObtainBuffer(ulong address, ulong size, bool isWritten)
        {
            var (buffer, offset) = _bufferCache.ObtainBuffer(address, size, isWritten);
            return new BufferBinding(buffer.Handle.Handle, offset);
        }

        // The ring takes the bytes unless it would wrap over a prepared binding; then a host buffer of the draw takes them.
        public BufferBinding UploadTransient(ReadOnlySpan<byte> data, uint alignment)
        {
            var preparation = RequirePreparation();
            var stream = _bufferCache.GetUtilityBuffer(GpuBufferUsage.Stream);
            if (stream.TryMap((ulong)data.Length, out var offset, alignment))
            {
                data.CopyTo(stream.Mapped[(int)offset..]);
                stream.Commit();
                return new BufferBinding(stream.Handle.Handle, offset);
            }

            var buffer = CreateHostBuffer(data, BufferUsageFlags.IndexBufferBit | BufferUsageFlags.VertexBufferBit, out var memory, out _);
            preparation.OverflowBuffers.Add((buffer, memory));
            if (RenderTrace.Enabled)
            {
                RenderTrace.Write($"The stream ring could not take an upload inside a preparation; a host buffer holds it: bytes={data.Length} alignment={alignment}");
            }

            return new BufferBinding(buffer.Handle, 0);
        }

        public void BindVertexBuffers(ReadOnlySpan<BufferBinding> bindings)
        {
            var command = BeginBatchedGuestCommands();
            var buffers = stackalloc VkBuffer[bindings.Length];
            var offsets = stackalloc ulong[bindings.Length];
            for (var index = 0; index < bindings.Length; index++)
            {
                buffers[index] = new VkBuffer(bindings[index].Handle);
                offsets[index] = bindings[index].Offset;
            }

            _vk.CmdBindVertexBuffers(command, 0, (uint)bindings.Length, buffers, offsets);
        }

        public void BindIndexBuffer(BufferBinding binding, IndexType type) =>
            _vk.CmdBindIndexBuffer(BeginBatchedGuestCommands(), new VkBuffer(binding.Handle), binding.Offset, type);

        public IResourcePreparation BeginPreparation()
        {
            if (_preparation is not null)
            {
                throw SubmissionScheduler.Fatal("A resource preparation is already open.");
            }

            // Acquire the decoded frame before any stage selects its movie texture planes.
            PumpHostMovieFrame();

            if (_batchDrawCount >= DrawsPerBatch)
            {
                EndRendering();
                FlushBatchedGuestCommands();
            }

            EnsureGuestSubmissionCapacity();
            _ = BeginBatchedGuestCommands();
            _preparation = new RenderPreparation(this, _bufferCache.GetUtilityBuffer(GpuBufferUsage.Stream).RetainContents());
            return _preparation;
        }

        private RenderPreparation RequirePreparation() =>
            _preparation ?? throw SubmissionScheduler.Fatal("The resource preparation is not open.");

        private static AgcExports.CompiledStageProgram RequireCompiledProgram(ShaderStageResources stage) =>
            stage.Program as AgcExports.CompiledStageProgram
            ?? throw SubmissionScheduler.Fatal($"The stage program is not a compiled program: stage={stage.Program?.Stage} hash=0x{stage.Program?.Hash ?? 0:X16}.");

        public IPreparedBindings PrepareBindings(ShaderStageResources stage)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawResources);
            var preparation = RequirePreparation();
            var program = RequireCompiledProgram(stage);
            var textures = ResolveStageTextures(program.Textures);
            var globals = new GlobalBufferResource[program.GlobalBuffers.Count];
            for (var index = 0; index < globals.Length; index++)
            {
                globals[index] = CreateGlobalBufferResource(program.GlobalBuffers[index], program.Buffers[index].Formatted);
            }

            var scalars = program.ScalarBuffer is { } scalarBuffer ? CreateTransientGlobalBufferResource(scalarBuffer) : null;
            var prepared = new PreparedStageBindings(stage, program, textures, globals, scalars);
            preparation.Stages.Add(prepared);
            return prepared;
        }

        // The image lookups and the binding bookkeeping; the views follow once the targets are bound.
        private TextureResource[] ResolveStageTextures(IReadOnlyList<GuestDrawTexture> textures)
        {
            var bindings = new TextureResource[textures.Count];
            var hostMovie = FindHostMovieTextureBindings(textures);
            for (var index = 0; index < bindings.Length; index++)
            {
                bindings[index] = index == hostMovie.Luma
                    ? CreateHostMovieTextureResource(textures[index], plane: 0)
                    : index == hostMovie.Chroma
                        ? CreateHostMovieTextureResource(textures[index], plane: 1)
                        : ResolveTexture(textures[index]);
            }

            return bindings;
        }

        private void DestroyStageBindings(PreparedStageBindings stage)
        {
            foreach (var texture in stage.Textures)
            {
                if (texture is { StagingBuffer.Handle: not 0 })
                {
                    RecycleHostBuffer(texture.StagingBuffer, texture.StagingMemory);
                }
            }

            foreach (var global in stage.Globals)
            {
                DestroyGlobalBufferResource(global);
            }

            if (stage.Scalars is { } scalars)
            {
                DestroyGlobalBufferResource(scalars);
            }
        }

        private void DestroyGlobalBufferResource(GlobalBufferResource resource)
        {
            resource.StreamRetention?.Dispose();
            if (resource.OwnsBuffer)
            {
                RecycleHostBuffer(resource.Buffer, resource.Memory);
            }
        }

        public void PrepareDeviceAddresses() =>
            throw SubmissionScheduler.Fatal("A program uses device addresses, which the shader pipeline provider does not produce yet.");

        public void BindResources(IPreparedBindings prepared)
        {
            var stage = (PreparedStageBindings)prepared;
            AcquireTextureViews(stage.Textures, stage.Program.Textures);
        }

        private RenderPipelineEntry RequirePipelineEntry(in PipelineHandle pipeline) =>
            _pipelineEntries.TryGetValue(pipeline.Pipeline, out var entry)
                ? entry
                : throw SubmissionScheduler.Fatal($"The pipeline handle is unknown: pipeline={pipeline.Pipeline} layout={pipeline.Layout}.");

        private GlobalBufferResource NullBufferResource()
        {
            var buffer = _bufferCache.GetBuffer(GuestBufferCache.NullBufferId);
            if (_nullBufferResource is null || _nullBufferResource.Buffer.Handle != buffer.Handle.Handle)
            {
                _nullBufferResource = new GlobalBufferResource { Buffer = buffer.Handle, Size = buffer.Size };
            }

            return _nullBufferResource;
        }

        // Joins every stage into the flat descriptor layout, records the image work and binds the set.
        public void CommitBindings(PipelineBindPoint bindPoint, in PipelineHandle pipeline, ReadOnlySpan<IPreparedBindings> stages)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DescriptorSetup);
            var preparation = RequirePreparation();
            var entry = RequirePipelineEntry(in pipeline);
            var command = BeginBatchedGuestCommands();
            _commandBuffer = command;
            var totalGlobals = 0;
            var totalTextures = 0;
            // All stages that read the same stencil bytes share the shader's working image.
            if (preparation.StencilStorageImages is { } stencilImages)
            {
                foreach (var prepared in stages)
                {
                    foreach (var texture in ((PreparedStageBindings)prepared).Textures)
                    {
                        if (texture.CachedImage is { } attachment && ViewFormatRules.IsStencilViewFormat(texture.Request.View.Format) &&
                            stencilImages.TryGetValue(attachment, out var storage))
                        {
                            texture.CachedImage = storage;
                            texture.Image = storage.Backing.Handle;
                            texture.View = storage.GetOrCreateView(texture.Request.View with { Aspect = ImageAspectFlags.ColorBit });
                        }
                    }
                }
            }

            foreach (var prepared in stages)
            {
                var stage = (PreparedStageBindings)prepared;
                totalGlobals = Math.Max(totalGlobals, stage.Program.TotalGlobalBuffers);
                totalTextures += stage.Textures.Length;
            }

            var resources = new TranslatedDrawResources
            {
                DebugName = bindPoint == PipelineBindPoint.Compute ? "SharpEmu dispatch" : "SharpEmu draw",
                GlobalMemoryBuffers = new GlobalBufferResource[totalGlobals],
                Textures = new TextureResource[totalTextures],
                OverflowBuffers = preparation.OverflowBuffers.Count == 0 ? null : preparation.OverflowBuffers.ToArray(),
                PipelineLayout = entry.Layout,
                DescriptorSetLayout = entry.SetLayout,
                DescriptorLayoutCached = true,
                Pipeline = entry.Pipeline,
                PipelineCached = true,
            };
            var uploadStage = bindPoint == PipelineBindPoint.Compute ? PipelineStageFlags.ComputeShaderBit : PipelineStageFlags.FragmentShaderBit;
            foreach (var prepared in stages)
            {
                var stage = (PreparedStageBindings)prepared;
                var program = stage.Program;
                for (var index = 0; index < stage.Globals.Length; index++)
                {
                    PlaceGlobalBuffer(resources, program.GlobalBufferBase + index, stage.Globals[index], program);
                }

                if (stage.Scalars is { } scalars)
                {
                    PlaceGlobalBuffer(resources, program.ScalarBufferIndex, scalars, program);
                }

                for (var index = 0; index < stage.Textures.Length; index++)
                {
                    var slot = program.ImageBindingBase + index;
                    if (slot < 0 || slot >= resources.Textures.Length || resources.Textures[slot] is not null)
                    {
                        throw SubmissionScheduler.Fatal(
                            $"The image slot is outside the descriptor layout or bound twice: slot={slot} images={resources.Textures.Length} stage={program.Stage} hash=0x{program.Hash:X16}.");
                    }

                    resources.Textures[slot] = stage.Textures[index];
                }

                RecordStageTextureTransitions(stage.Textures);
                foreach (var texture in stage.Textures)
                {
                    if (texture.IsHostMovie && texture.NeedsUpload)
                    {
                        EndRendering();
                        break;
                    }
                }

                RecordHostMovieUploads(stage.Textures, uploadStage);
            }

            for (var slot = 0; slot < resources.GlobalMemoryBuffers.Length; slot++)
            {
                resources.GlobalMemoryBuffers[slot] ??= NullBufferResource();
            }

            AllocateDescriptorSet(resources);
            _batchResources.Add(resources);
            preparation.OverflowBuffers.Clear();
            preparation.Committed = true;
            if (resources.DescriptorSet.Handle != 0)
            {
                var descriptorSet = resources.DescriptorSet;
                _vk.CmdBindDescriptorSets(command, bindPoint, entry.Layout, 0, 1, &descriptorSet, 0, null);
            }
        }

        private static void PlaceGlobalBuffer(TranslatedDrawResources resources, int slot, GlobalBufferResource resource, AgcExports.CompiledStageProgram program)
        {
            if (slot < 0 || slot >= resources.GlobalMemoryBuffers.Length || resources.GlobalMemoryBuffers[slot] is not null)
            {
                throw SubmissionScheduler.Fatal(
                    $"The buffer slot is outside the descriptor layout or bound twice: slot={slot} buffers={resources.GlobalMemoryBuffers.Length} stage={program.Stage} hash=0x{program.Hash:X16}.");
            }

            resources.GlobalMemoryBuffers[slot] = resource;
        }

        // One descriptor set per draw from a recycled pool: binding 0 holds the buffers, the images follow.
        private void AllocateDescriptorSet(TranslatedDrawResources resources)
        {
            if (resources.DescriptorSetLayout.Handle == 0)
            {
                return;
            }

            if (_recycledDescriptorPools.TryPop(out var recycledPool))
            {
                Check(_vk.ResetDescriptorPool(_device, recycledPool, 0), "vkResetDescriptorPool");
                resources.DescriptorPool = recycledPool;
            }
            else
            {
                var poolSizes = stackalloc DescriptorPoolSize[3];
                poolSizes[0] = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 256 };
                poolSizes[1] = new DescriptorPoolSize { Type = DescriptorType.StorageImage, DescriptorCount = 64 };
                poolSizes[2] = new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 64 };
                var poolInfo = new DescriptorPoolCreateInfo
                {
                    SType = StructureType.DescriptorPoolCreateInfo,
                    MaxSets = 1,
                    PoolSizeCount = 3,
                    PPoolSizes = poolSizes,
                };
                Check(_vk.CreateDescriptorPool(_device, &poolInfo, null, out var descriptorPool), "vkCreateDescriptorPool");
                resources.DescriptorPool = descriptorPool;
            }

            var setLayout = resources.DescriptorSetLayout;
            var allocateInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = resources.DescriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &setLayout,
            };
            Check(_vk.AllocateDescriptorSets(_device, &allocateInfo, out var descriptorSet), "vkAllocateDescriptorSets");
            resources.DescriptorSet = descriptorSet;

            var globalCount = resources.GlobalMemoryBuffers.Length;
            var textureCount = resources.Textures.Length;
            var bufferInfos = stackalloc DescriptorBufferInfo[Math.Max(globalCount, 1)];
            var imageInfos = stackalloc DescriptorImageInfo[Math.Max(textureCount, 1)];
            var writes = stackalloc WriteDescriptorSet[textureCount + 1];
            var writeCount = 0u;
            if (globalCount != 0)
            {
                for (var index = 0; index < globalCount; index++)
                {
                    var buffer = resources.GlobalMemoryBuffers[index];
                    bufferInfos[index] = new DescriptorBufferInfo { Buffer = buffer.Buffer, Offset = buffer.Offset, Range = buffer.Size };
                }

                writes[writeCount++] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = descriptorSet,
                    DstBinding = 0,
                    DescriptorCount = (uint)globalCount,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = bufferInfos,
                };
            }

            for (var index = 0; index < textureCount; index++)
            {
                var texture = resources.Textures[index];
                if (!texture.IsStorage && texture.Sampler.Handle == 0)
                {
                    texture.Sampler = ResolveSampler(texture.SamplerState);
                }

                imageInfos[index] = new DescriptorImageInfo
                {
                    Sampler = texture.IsStorage ? default : texture.Sampler,
                    ImageView = texture.View,
                    ImageLayout = texture.Layout,
                };
                writes[writeCount++] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = descriptorSet,
                    DstBinding = (uint)(index + 1),
                    DescriptorCount = 1,
                    DescriptorType = texture.IsStorage ? DescriptorType.StorageImage : DescriptorType.CombinedImageSampler,
                    PImageInfo = &imageInfos[index],
                };
            }

            if (writeCount != 0)
            {
                _vk.UpdateDescriptorSets(_device, writeCount, writes, 0, null);
            }
        }

        // The layout each sampled image reads through; a target read by its own draw uses the general layout.
        private void RecordStageTextureTransitions(TextureResource[] bindings)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageTransitions);
            CachedImage? depthImage = null;
            var depthFormat = Format.Undefined;
            if (_hasBoundDepth)
            {
                depthImage = _imageCache.GetImage(_boundDepth.Image);
                var depthView = _boundDepth.Target.Target.Request.View;
                depthFormat = _boundDepth.Target.Target.Format;
                if (_boundDepthLoadState.DepthClearEnabled || _boundDepthLoadState.StencilClearEnabled)
                {
                    foreach (var binding in bindings)
                    {
                        if (binding.IsHostMovie || !ReferenceEquals(binding.CachedImage, depthImage) || !ViewsOverlap(binding.Request.View, depthView))
                        {
                            continue;
                        }

                        // Complete the load clears before the shader reads, then restore the attachment for the draw.
                        RecordSampledDepthClear(depthImage, depthView, depthFormat);
                        _boundDepthLoadState = _boundDepthLoadState with { DepthClearEnabled = false, StencilClearEnabled = false };
                        _depthClearRecordedSeparately = true;
                        var remainingWrites = _boundDepthLoadState.AttachmentWriteAspects(depthFormat);
                        _boundDepthLayout = _boundDepthLoadState.AttachmentLayout(depthFormat);
                        depthImage.Transition(
                            _boundDepthLayout,
                            AccessFlags.DepthStencilAttachmentReadBit | (remainingWrites != 0 ? AccessFlags.DepthStencilAttachmentWriteBit : 0),
                            new SubresourceRange(depthView.BaseLevel, depthView.LevelCount, depthView.BaseLayer, depthView.LayerCount),
                            BeginBatchedGuestCommands());
                        break;
                    }
                }
            }

            var command = BeginBatchedGuestCommands();
            foreach (var binding in bindings)
            {
                if (binding.IsHostMovie || binding.CachedImage is not { } image)
                {
                    continue;
                }

                var view = binding.Request.View;
                var range = new SubresourceRange(view.BaseLevel, view.LevelCount, view.BaseLayer, view.LayerCount);
                var storage = binding.IsStorage;
                if (ImageDescription.IsEmptyRange(image.Description.Data))
                {
                    binding.Layout = ImageLayout.General;
                    image.Transition(binding.Layout, storage || image.Binding.ShaderWrite ? AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit : AccessFlags.ShaderReadBit, range, command);
                }
                else if (depthImage is not null && ReferenceEquals(image, depthImage))
                {
                    var sampledAspects = ViewFormatRules.IsStencilViewFormat(view.Format) ? ImageAspectFlags.StencilBit : ImageAspectFlags.DepthBit;
                    var writes = _boundDepthLoadState.AttachmentWriteAspects(depthFormat);
                    var attachmentView = _boundDepth.Target.Target.Request.View;
                    var overlaps = ViewsOverlap(view, attachmentView);
                    if (overlaps && (sampledAspects & writes) != 0)
                    {
                        throw SubmissionScheduler.Fatal(
                            "A draw cannot sample and write the same depth or stencil aspect without feedback-loop support: " +
                            $"image=0x{image.Description.Data.Address:X16} sampledAspects={sampledAspects} writeAspects={writes} viewFormat={view.Format} viewAspect={view.Aspect} " +
                            $"sampleMip={view.BaseLevel}+{view.LevelCount} sampleLayer={view.BaseLayer}+{view.LayerCount} " +
                            $"targetMip={attachmentView.BaseLevel}+{attachmentView.LevelCount} targetLayer={attachmentView.BaseLayer}+{attachmentView.LayerCount} " +
                            $"clearDepth={_boundDepthLoadState.DepthClearEnabled} clearStencil={_boundDepthLoadState.StencilClearEnabled} depthState={_boundDepthLoadState}.");
                    }

                    // Keep the sampled view and the attachment in the same layout.
                    binding.Layout = overlaps ? _boundDepthLayout : ImageLayout.DepthStencilReadOnlyOptimal;
                    var attachmentAccess = overlaps
                        ? AccessFlags.DepthStencilAttachmentReadBit | (writes != 0 ? AccessFlags.DepthStencilAttachmentWriteBit : 0)
                        : 0;
                    image.Transition(binding.Layout, AccessFlags.ShaderReadBit | attachmentAccess, range, command);
                }
                else if ((image.Binding.ForceGeneral || image.Binding.IsTarget) && !image.Description.IsDepth)
                {
                    var storageAccess = image.Binding.ShaderWrite ? AccessFlags.ShaderWriteBit : 0;
                    binding.Layout = ImageLayout.General;
                    image.Transition(
                        binding.Layout,
                        AccessFlags.ShaderReadBit | storageAccess | AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
                        null,
                        command);
                }
                else if (storage)
                {
                    binding.Layout = ImageLayout.General;
                    image.Transition(binding.Layout, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit, range, command);
                }
                else
                {
                    binding.Layout = image.Description.IsDepth ? ImageLayout.DepthStencilReadOnlyOptimal : ImageLayout.ShaderReadOnlyOptimal;
                    image.Transition(binding.Layout, AccessFlags.ShaderReadBit, range, command);
                }
            }
        }

        private static bool ViewsOverlap(in ImageViewDescription sampled, in ImageViewDescription attachment) =>
            (ulong)sampled.BaseLevel < (ulong)attachment.BaseLevel + attachment.LevelCount &&
            (ulong)attachment.BaseLevel < (ulong)sampled.BaseLevel + sampled.LevelCount &&
            (ulong)sampled.BaseLayer < (ulong)attachment.BaseLayer + attachment.LayerCount &&
            (ulong)attachment.BaseLayer < (ulong)sampled.BaseLayer + sampled.LayerCount;

        // Clears the depth view with a transfer so the draw can sample the cleared image.
        private void RecordSampledDepthClear(CachedImage image, in ImageViewDescription view, Format format)
        {
            var aspects = (_boundDepthLoadState.DepthClearEnabled ? ImageAspectFlags.DepthBit : 0) |
                (_boundDepthLoadState.StencilClearEnabled ? ImageAspectFlags.StencilBit : 0);
            aspects &= ViewFormatRules.DepthAspects(format);
            if (aspects == 0)
            {
                return;
            }

            EndRendering();
            var command = BeginBatchedGuestCommands();
            var range = new SubresourceRange(view.BaseLevel, view.LevelCount, view.BaseLayer, view.LayerCount);
            image.Transition(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, range, command);
            var vkRange = new ImageSubresourceRange(aspects, view.BaseLevel, view.LevelCount, view.BaseLayer, view.LayerCount);
            var value = new ClearDepthStencilValue(_boundDepthLoadState.DepthClearValue, _boundDepthLoadState.StencilClearValue);
            _vk.CmdClearDepthStencilImage(command, image.Backing.Handle, ImageLayout.TransferDstOptimal, &value, 1, &vkRange);
        }

        public void SetDynamicState(in DynamicDrawState state)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawDynamicStateRecording);
            var command = BeginBatchedGuestCommands();
            var viewport = new Viewport(state.ViewportX, state.ViewportY, state.ViewportWidth, state.ViewportHeight, state.ViewportMinDepth, state.ViewportMaxDepth);
            _vk.CmdSetViewport(command, 0, 1, &viewport);
            var scissor = new Rect2D(
                new Offset2D(state.Scissor.Left, state.Scissor.Top),
                new Extent2D((uint)(state.Scissor.Right - state.Scissor.Left), (uint)(state.Scissor.Bottom - state.Scissor.Top)));
            _vk.CmdSetScissor(command, 0, 1, &scissor);
            _vk.CmdSetLineWidth(command, state.LineWidth);
            var blendConstants = stackalloc float[4] { state.BlendRed, state.BlendGreen, state.BlendBlue, state.BlendAlpha };
            _vk.CmdSetBlendConstants(command, blendConstants);
            _extendedDynamicStateApi.CmdSetDepthTestEnable(command, state.DepthTestEnabled);
            _extendedDynamicStateApi.CmdSetDepthWriteEnable(command, state.DepthWriteEnabled);
            _extendedDynamicStateApi.CmdSetDepthCompareOp(command, state.DepthCompare);
            _extendedDynamicState2Api.CmdSetDepthBiasEnable(command, state.DepthBiasEnabled);
            if (state.DepthBiasEnabled)
            {
                _vk.CmdSetDepthBias(command, state.DepthBiasConstantFactor, _supportsDepthBiasClamp ? state.DepthBiasClamp : 0f, state.DepthBiasSlopeFactor);
            }

            if (state.StencilTestEnabled)
            {
                _vk.CmdSetStencilCompareMask(command, StencilFaceFlags.FaceFrontBit, state.FrontStencil.CompareMask);
                _vk.CmdSetStencilCompareMask(command, StencilFaceFlags.FaceBackBit, state.BackStencil.CompareMask);
                _vk.CmdSetStencilWriteMask(command, StencilFaceFlags.FaceFrontBit, state.FrontStencil.WriteMask);
                _vk.CmdSetStencilWriteMask(command, StencilFaceFlags.FaceBackBit, state.BackStencil.WriteMask);
                _vk.CmdSetStencilReference(command, StencilFaceFlags.FaceFrontBit, state.FrontStencil.Reference);
                _vk.CmdSetStencilReference(command, StencilFaceFlags.FaceBackBit, state.BackStencil.Reference);
            }

            if (_colorWriteEnableApi is { } colorWriteEnable && state.ColorWriteCount != 0)
            {
                var enables = stackalloc Bool32[RenderingState.ColorAttachmentCapacity];
                for (var index = 0; index < state.ColorWriteCount; index++)
                {
                    enables[index] = ((state.ColorWriteEnableMask >> index) & 1) != 0;
                }

                colorWriteEnable.CmdSetColorWriteEnable(command, state.ColorWriteCount, enables);
            }
        }


        private static Format ToVkVertexFormat(
            uint dataFormat,
            uint numberFormat,
            uint componentCount)
        {
            var format = (dataFormat, numberFormat) switch
            {
                (1, 0) => Format.R8Unorm,
                (1, 1) => Format.R8SNorm,
                (1, 2) => Format.R8Uscaled,
                (1, 3) => Format.R8Sscaled,
                (1, 4) => Format.R8Uint,
                (1, 5) => Format.R8Sint,
                (1, 9) => Format.R8Srgb,
                (2, 0) => Format.R16Unorm,
                (2, 1) => Format.R16SNorm,
                (2, 2) => Format.R16Uscaled,
                (2, 3) => Format.R16Sscaled,
                (2, 4) => Format.R16Uint,
                (2, 5) => Format.R16Sint,
                (2, 7) => Format.R16Sfloat,
                (3, 0) => Format.R8G8Unorm,
                (3, 1) => Format.R8G8SNorm,
                (3, 2) => Format.R8G8Uscaled,
                (3, 3) => Format.R8G8Sscaled,
                (3, 4) => Format.R8G8Uint,
                (3, 5) => Format.R8G8Sint,
                (3, 9) => Format.R8G8Srgb,
                (4, 4) => Format.R32Uint,
                (4, 5) => Format.R32Sint,
                (4, 7) => Format.R32Sfloat,
                (5, 0) => Format.R16G16Unorm,
                (5, 1) => Format.R16G16SNorm,
                (5, 2) => Format.R16G16Uscaled,
                (5, 3) => Format.R16G16Sscaled,
                (5, 4) => Format.R16G16Uint,
                (5, 5) => Format.R16G16Sint,
                (5, 7) => Format.R16G16Sfloat,
                (6, 7) => Format.B10G11R11UfloatPack32,
                (7, 7) => Format.B10G11R11UfloatPack32,
                (8, 0) => Format.A2B10G10R10UnormPack32,
                (8, 1) => Format.A2B10G10R10SNormPack32,
                (8, 2) => Format.A2B10G10R10UscaledPack32,
                (8, 3) => Format.A2B10G10R10SscaledPack32,
                (8, 4) => Format.A2B10G10R10UintPack32,
                (8, 5) => Format.A2B10G10R10SintPack32,
                // RDNA COLOR_2_10_10_10 stores component 0 (R) in bits
                // 0..9 and A in 30..31. Vulkan names that exact bit layout
                // A2B10G10R10_PACK32 (the packed name is MSB-to-LSB).
                (9, 0) => Format.A2B10G10R10UnormPack32,
                (9, 1) => Format.A2B10G10R10SNormPack32,
                (9, 2) => Format.A2B10G10R10UscaledPack32,
                (9, 3) => Format.A2B10G10R10SscaledPack32,
                (9, 4) => Format.A2B10G10R10UintPack32,
                (9, 5) => Format.A2B10G10R10SintPack32,
                (10, 0) => Format.R8G8B8A8Unorm,
                (10, 1) => Format.R8G8B8A8SNorm,
                (10, 2) => Format.R8G8B8A8Uscaled,
                (10, 3) => Format.R8G8B8A8Sscaled,
                (10, 4) => Format.R8G8B8A8Uint,
                (10, 5) => Format.R8G8B8A8Sint,
                (10, 9) => Format.R8G8B8A8Srgb,
                (11, 4) => Format.R32G32Uint,
                (11, 5) => Format.R32G32Sint,
                (11, 7) => Format.R32G32Sfloat,
                (12, 0) => Format.R16G16B16A16Unorm,
                (12, 1) => Format.R16G16B16A16SNorm,
                (12, 2) => Format.R16G16B16A16Uscaled,
                (12, 3) => Format.R16G16B16A16Sscaled,
                (12, 4) => Format.R16G16B16A16Uint,
                (12, 5) => Format.R16G16B16A16Sint,
                (12, 6) => Format.R16G16B16A16SNorm,
                (12, 7) => Format.R16G16B16A16Sfloat,
                (13, 4) => Format.R32G32B32Uint,
                (13, 5) => Format.R32G32B32Sint,
                (13, 7) => Format.R32G32B32Sfloat,
                (14, 4) => Format.R32G32B32A32Uint,
                (14, 5) => Format.R32G32B32A32Sint,
                (14, 7) => Format.R32G32B32A32Sfloat,
                // Prospero VertexAttribFormat quirks also seen as buffer formats.
                (113, _) => Format.R32G32B32A32Sfloat,
                (121, _) => Format.R16G16Sfloat,
                (16, 0) => Format.B5G6R5UnormPack16,
                (17, 0) => Format.R5G5B5A1UnormPack16,
                (19, 0) => Format.R4G4B4A4UnormPack16,
                (34, 7) => Format.E5B9G9R9UfloatPack32,
                _ => ToVkFloatVertexFormat(componentCount),
            };

            return NarrowVkVertexFormat(format, componentCount);
        }


        /// <summary>
        /// Narrow a sharp's full VkFormat to the component count the VS fetch
        /// actually consumes.
        /// </summary>
        private static Format NarrowVkVertexFormat(Format format, uint usedComponents)
        {
            if (usedComponents == 0)
            {
                return format;
            }

            return (format, usedComponents) switch
            {
                (Format.R32G32B32A32Sfloat, 1) => Format.R32Sfloat,
                (Format.R32G32B32A32Sfloat, 2) => Format.R32G32Sfloat,
                (Format.R32G32B32A32Sfloat, 3) => Format.R32G32B32Sfloat,
                (Format.R32G32B32Sfloat, 1) => Format.R32Sfloat,
                (Format.R32G32B32Sfloat, 2) => Format.R32G32Sfloat,
                (Format.R16G16B16A16Sfloat, 1) => Format.R16Sfloat,
                (Format.R16G16B16A16Sfloat, 2) => Format.R16G16Sfloat,
                (Format.R8G8B8A8Unorm, 1) => Format.R8Unorm,
                (Format.R8G8B8A8Unorm, 2) => Format.R8G8Unorm,
                (Format.R8G8B8A8SNorm, 2) => Format.R8G8SNorm,
                (Format.R8G8B8A8Uint, 1) => Format.R8Uint,
                (Format.R8G8B8A8Uint, 2) => Format.R8G8Uint,
                _ => format,
            };
        }


        private static Format ToVkFloatVertexFormat(uint componentCount) =>
            componentCount switch
            {
                1 => Format.R32Sfloat,
                2 => Format.R32G32Sfloat,
                3 => Format.R32G32B32Sfloat,
                4 => Format.R32G32B32A32Sfloat,
                _ => Format.R32Sfloat,
            };

        private static BlendFactor ToVkBlendFactor(uint factor) =>
            factor switch
            {
                0 => BlendFactor.Zero,
                1 => BlendFactor.One,
                2 => BlendFactor.SrcColor,
                3 => BlendFactor.OneMinusSrcColor,
                4 => BlendFactor.SrcAlpha,
                5 => BlendFactor.OneMinusSrcAlpha,
                6 => BlendFactor.DstAlpha,
                7 => BlendFactor.OneMinusDstAlpha,
                8 => BlendFactor.DstColor,
                9 => BlendFactor.OneMinusDstColor,
                10 => BlendFactor.SrcAlphaSaturate,
                13 => BlendFactor.ConstantColor,
                14 => BlendFactor.OneMinusConstantColor,
                15 => BlendFactor.Src1Color,
                16 => BlendFactor.OneMinusSrc1Color,
                17 => BlendFactor.Src1Alpha,
                18 => BlendFactor.OneMinusSrc1Alpha,
                19 => BlendFactor.ConstantAlpha,
                20 => BlendFactor.OneMinusConstantAlpha,
                _ => BlendFactor.One,
            };


        private static BlendOp ToVkBlendOp(uint function) =>
            function switch
            {
                0 => BlendOp.Add,
                1 => BlendOp.Subtract,
                2 => BlendOp.Min,
                3 => BlendOp.Max,
                4 => BlendOp.ReverseSubtract,
                _ => BlendOp.Add,
            };


        private static ColorComponentFlags ToVkColorWriteMask(uint mask)
        {
            var flags = default(ColorComponentFlags);
            if ((mask & 1u) != 0)
            {
                flags |= ColorComponentFlags.RBit;
            }

            if ((mask & 2u) != 0)
            {
                flags |= ColorComponentFlags.GBit;
            }

            if ((mask & 4u) != 0)
            {
                flags |= ColorComponentFlags.BBit;
            }

            if ((mask & 8u) != 0)
            {
                flags |= ColorComponentFlags.ABit;
            }

            return flags;
        }

        public bool TryResolveColorOutput(
            uint dataFormat,
            uint numberType,
            uint componentSwap,
            out Gen5PixelOutputKind outputKind,
            out Gen5ColorComponentMapping componentMapping)
        {
            if (VulkanVideoPresenter.TryDecodeRenderTargetFormat(
                    dataFormat,
                    numberType,
                    componentSwap,
                    out var format))
            {
                outputKind = format.OutputKind;
                componentMapping = format.ExportMapping;
                return true;
            }

            outputKind = default;
            componentMapping = default;
            return false;
        }
        public void BeginRendering(in RenderingState state)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawRenderingSetup);
            if (_renderingActive && _renderingState == state)
            {
                return;
            }

            EndRendering();
            var command = BeginBatchedGuestCommands();
            _commandBuffer = command;
            var colors = stackalloc RenderingAttachmentInfo[RenderingState.ColorAttachmentCapacity];
            for (var index = 0; index < state.ColorAttachmentCount; index++)
            {
                var attachment = state.ColorAttachments[index];
                colors[index] = new RenderingAttachmentInfo
                {
                    SType = StructureType.RenderingAttachmentInfo,
                    ImageView = attachment.View,
                    ImageLayout = attachment.Layout,
                    LoadOp = attachment.IsClear ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
                    StoreOp = AttachmentStoreOp.Store,
                    ClearValue = new ClearValue
                    {
                        Color = new ClearColorValue
                        {
                            Uint32_0 = attachment.ClearWord0,
                            Uint32_1 = attachment.ClearWord1,
                            Uint32_2 = attachment.ClearWord2,
                            Uint32_3 = attachment.ClearWord3,
                        },
                    },
                };
            }

            var depthStencil = state.DepthStencilAttachment;
            var depthLayout = depthStencil.Layout;
            var depthClear = depthStencil.DepthClear;
            var stencilClear = depthStencil.StencilClear;
            if (_depthClearRecordedSeparately)
            {
                depthLayout = _boundDepthLayout;
                depthClear = false;
                stencilClear = false;
            }

            var depth = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = depthStencil.View,
                ImageLayout = depthLayout,
                LoadOp = depthClear ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue { DepthStencil = new ClearDepthStencilValue(BitConverter.UInt32BitsToSingle(depthStencil.ClearWord0), 0) },
            };
            var stencil = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = depthStencil.View,
                ImageLayout = depthLayout,
                LoadOp = stencilClear ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue { DepthStencil = new ClearDepthStencilValue(0f, depthStencil.ClearWord1) },
            };
            var rendering = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(state.Width, state.Height)),
                LayerCount = state.Layers,
                ColorAttachmentCount = state.ColorAttachmentCount,
                PColorAttachments = colors,
                PDepthAttachment = depthStencil.HasDepth ? &depth : null,
                PStencilAttachment = depthStencil.HasStencil ? &stencil : null,
            };
            _dynamicRenderingApi.CmdBeginRendering(command, &rendering);
            _renderingScopesBegun++;
            _renderingActive = true;
            _renderingState = state;
        }

        public void EndRendering()
        {
            if (!_renderingActive)
            {
                return;
            }

            _renderingActive = false;
            _renderingState = default;
            _dynamicRenderingApi.CmdEndRendering(new CommandBuffer(_scheduler.Current.Handle));
        }

        public void BindPipeline(PipelineBindPoint bindPoint, in PipelineHandle pipeline)
        {
            var entry = RequirePipelineEntry(in pipeline);
            var command = BeginBatchedGuestCommands();
            if (bindPoint == PipelineBindPoint.Graphics)
            {
                _boundGraphicsPipeline = entry;
                if (!entry.RectangleList)
                {
                    _vk.CmdBindPipeline(command, bindPoint, entry.Pipeline);
                }

                return;
            }

            _vk.CmdBindPipeline(command, bindPoint, entry.Pipeline);
            fixed (uint* limits = _computeThreadLimits)
            {
                _vk.CmdPushConstants(command, entry.Layout, ShaderStageFlags.ComputeBit, 0, ComputeThreadLimitBytes, limits);
            }
        }

        private void CountDraw()
        {
            if (_preparation is { } preparation)
                preparation.CommandsRecorded = true;
            Interlocked.Increment(ref _perfDrawCount);
            PerfOverlay.RecordDraw();
            _batchDrawCount++;
        }

        // A single rectangle draws as a strip of four vertices; anything else draws as a triangle list.
        private static bool IsSingleRectangle(uint vertexCount) => vertexCount is 1 or 3 or 4;

        private void BindRectangleListVariant(RenderPipelineEntry entry, bool strip, CommandBuffer command)
        {
            ref var variant = ref strip ? ref entry.StripVariant : ref entry.ListVariant;
            if (variant.Handle == 0)
            {
                var description = entry.Description! with { Topology = strip ? PrimitiveTopology.TriangleStrip : PrimitiveTopology.TriangleList };
                variant = CreateRenderPipeline(description, entry.Layout);
            }

            _vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, variant);
        }

        public void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawRecording);
            var command = BeginBatchedGuestCommands();
            var count = vertexCount;
            if (_boundGraphicsPipeline is { RectangleList: true } entry)
            {
                var strip = IsSingleRectangle(vertexCount);
                BindRectangleListVariant(entry, strip, command);
                if (strip)
                {
                    count = SingleRectangleVertexCount;
                }
            }

            _vk.CmdDraw(command, count, instanceCount, firstVertex, firstInstance);
            CountDraw();
        }

        void IRenderHost.DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset, uint firstInstance)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawRecording);
            var command = BeginBatchedGuestCommands();
            if (_boundGraphicsPipeline is { RectangleList: true } entry)
            {
                BindRectangleListVariant(entry, strip: false, command);
            }

            _vk.CmdDrawIndexed(command, indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);
            CountDraw();
        }

        public void Dispatch(uint groupsX, uint groupsY, uint groupsZ)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawRecording);
            _vk.CmdDispatch(BeginBatchedGuestCommands(), groupsX, groupsY, groupsZ);
            CountDraw();
        }

        private void RecordMemoryBarrier(PipelineStageFlags sourceStages, PipelineStageFlags destinationStages, AccessFlags sourceAccess, AccessFlags destinationAccess)
        {
            EndRendering();
            var command = BeginBatchedGuestCommands();
            var barrier = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = sourceAccess,
                DstAccessMask = destinationAccess,
            };
            _vk.CmdPipelineBarrier(command, sourceStages, destinationStages, 0, 1, &barrier, 0, null, 0, null);
        }

        public void ShaderWriteBarrier(PipelineStageFlags sourceStages) =>
            RecordMemoryBarrier(
                sourceStages,
                PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.VertexInputBit | PipelineStageFlags.VertexShaderBit |
                PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit | PipelineStageFlags.ColorAttachmentOutputBit,
                AccessFlags.ShaderWriteBit,
                AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit | AccessFlags.VertexAttributeReadBit | AccessFlags.IndexReadBit |
                AccessFlags.UniformReadBit | AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit |
                AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit);

        public void ShaderWriteHazardBarrier() =>
            RecordMemoryBarrier(
                PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.ComputeShaderBit,
                AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);

        public void ShaderAccessBarrier() =>
            RecordMemoryBarrier(
                PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.AllCommandsBit,
                AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);

        // Clears the bound targets on the GPU in place of the draw; the store owns the result.
        public void ClearColorTargets(ReadOnlySpan<ColorTargetState> targets, SolidColorClear clear)
        {
            EndRendering();
            var logicalClear = stackalloc float[4] { clear.Red, clear.Green, clear.Blue, clear.Alpha };
            foreach (ref readonly var target in targets)
            {
                var attachment = new ColorAttachment
                {
                    ImageIdentifier = target.Image,
                    Request = target.Resolution.Request,
                    Resolution = target.Resolution,
                };
                AcquireColorAttachment(attachment);
                var command = BeginBatchedGuestCommands();
                var mapping = target.Resolution.ExportMapping;
                var clearValue = new ClearColorValue(
                    logicalClear[mapping.Map(0)], logicalClear[mapping.Map(1)], logicalClear[mapping.Map(2)], logicalClear[mapping.Map(3)]);
                var view = attachment.Request.View;
                var range = new SubresourceRange(view.BaseLevel, view.LevelCount, view.BaseLayer, view.LayerCount);
                attachment.Image.Transition(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, range, command);
                var vkRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, view.BaseLevel, view.LevelCount, view.BaseLayer, view.LayerCount);
                _vk.CmdClearColorImage(command, attachment.Image.Backing.Handle, ImageLayout.TransferDstOptimal, &clearValue, 1, &vkRange);
            }

            CountDraw();
            if (_traceVulkanShaderEnabled)
            {
                TraceVulkanShader($"vk.offscreen_color_clear mrt={targets.Length} rgba=({clear.Red:0.###},{clear.Green:0.###},{clear.Blue:0.###},{clear.Alpha:0.###})");
            }
        }

        public bool TryRetainTargetlessDraw(RegisterBanks banks, GraphicsPrograms programs, in TargetlessDrawArguments arguments)
        {
            _ = programs;
            _translation.RetainTargetlessDraw(banks, in arguments);
            return true;
        }

        public void MarkGpuWritten(ResourceSlotIdentifier image) => _imageCache.MarkGpuWritten(image);

        public void ResolveImage(ResourceSlotIdentifier source, uint sourceMip, uint sourceLayer, ResourceSlotIdentifier destination, uint destinationMip, uint destinationLayer)
        {
            EndRendering();
            var sourceImage = _imageCache.GetImage(source);
            var destinationImage = _imageCache.GetImage(destination);
            destinationImage.ResolveFrom(
                sourceImage,
                new SubresourceRange(sourceMip, 1, sourceLayer, 1),
                new SubresourceRange(destinationMip, 1, destinationLayer, 1));
            _ = BeginBatchedGuestCommands();
        }

        public bool IsMetadata(ulong address) => _imageCache.IsMetadata(address);

        public bool ClearMetadata(ulong address)
        {
            _ = BeginBatchedGuestCommands();
            return _imageCache.ClearMetadata(address);
        }

        public bool TryClearImageFromBuffer(ulong address, ulong size, uint packedClear)
        {
            _ = BeginBatchedGuestCommands();
            return _imageCache.TryClearImageFromBuffer(address, size, packedClear);
        }

        public bool TryAbsorbDccFill(ulong address, ulong size, uint fillValue) => _imageCache.TryAbsorbDccFill(address, size, fillValue);

        private static string ResourceKey(int totalGlobalBuffers, ReadOnlySpan<bool> storageImages)
        {
            var key = new StringBuilder();
            key.Append(totalGlobalBuffers).Append(':');
            foreach (var storage in storageImages)
            {
                key.Append(storage ? 'S' : 'T');
            }

            return key.ToString();
        }

        private static void AppendStorageFlags(List<bool> flags, IReadOnlyList<GuestDrawTexture> textures)
        {
            foreach (var texture in textures)
            {
                flags.Add(texture.IsStorage);
            }
        }

        private PipelineHandle RegisterPipeline(RenderPipelineEntry entry)
        {
            entry.Id = ++_nextPipelineId;
            _pipelineEntries.Add(entry.Id, entry);
            return new PipelineHandle(entry.Id, entry.Layout.Handle, UsesPushDescriptors: false);
        }

        public PipelineHandle CreateGraphicsPipeline(
            ReadOnlySpan<ColorTargetState> colors,
            in DepthAttachmentState depth,
            VertexInputInfo vertexInput,
            PixelInputInfo? pixelInput,
            ContextRegisters context,
            in RenderingState rendering,
            PrimitiveTopology topology,
            bool primitiveRestartEnabled,
            ShaderProgram vertexProgram,
            ShaderProgram pixelProgram)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.PipelineSetup);
            _ = vertexProgram;
            _ = pixelProgram;
            if (colors.Length > _maxColorAttachments)
            {
                throw SubmissionScheduler.Fatal($"The draw binds more color attachments than the device supports: count={colors.Length} max={_maxColorAttachments}.");
            }

            var vertexStage = RequireCompiledProgram(vertexInput.Stage);
            var pixelStage = pixelInput is null ? null : RequireCompiledProgram(pixelInput.Stage);
            var storageImages = new List<bool>();
            if (pixelStage is not null)
            {
                AppendStorageFlags(storageImages, pixelStage.Textures);
            }

            AppendStorageFlags(storageImages, vertexStage.Textures);
            var storageFlags = storageImages.ToArray();
            var resourceKey = ResourceKey(vertexStage.TotalGlobalBuffers, storageFlags);
            var disableBlending = pixelStage?.DisableBlending ?? false;
            var colorFormats = new Format[colors.Length];
            var blends = new PipelineColorBlendAttachmentState[colors.Length];
            for (var index = 0; index < colors.Length; index++)
            {
                ref readonly var color = ref colors[index];
                var format = rendering.ColorAttachments[index].Format;
                colorFormats[index] = format;
                var blend = context.BlendControls[color.Slot];
                var mask = color.Resolution.ExportMapping.ApplyMask(context.RenderTargetMaskForSlot(color.Slot));
                var blendBypass = ((context.ColorTargets[color.Slot].Info >> 16) & 0x1) != 0;
                blends[index] = new PipelineColorBlendAttachmentState
                {
                    ColorWriteMask = ToVkColorWriteMask(mask),
                    BlendEnable = blend.Enable && !blendBypass && !disableBlending && IsBlendableFormat(format),
                    SrcColorBlendFactor = ToVkBlendFactor(blend.ColorSourceFactor),
                    DstColorBlendFactor = ToVkBlendFactor(blend.ColorDestinationFactor),
                    ColorBlendOp = ToVkBlendOp(blend.ColorFunction),
                    SrcAlphaBlendFactor = ToVkBlendFactor(blend.SeparateAlpha ? blend.AlphaSourceFactor : blend.ColorSourceFactor),
                    DstAlphaBlendFactor = ToVkBlendFactor(blend.SeparateAlpha ? blend.AlphaDestinationFactor : blend.ColorDestinationFactor),
                    AlphaBlendOp = ToVkBlendOp(blend.SeparateAlpha ? blend.AlphaFunction : blend.ColorFunction),
                };
            }

            var rectangleList = topology == PrimitiveTopology.PatchList;
            var mode = context.RasterMode;
            var cullMode = CullModeFlags.None;
            if (!rectangleList && mode.CullBack)
            {
                cullMode |= CullModeFlags.BackBit;
            }

            if (!rectangleList && mode.CullFront)
            {
                cullMode |= CullModeFlags.FrontBit;
            }

            var depthState = depth.HasTarget ? depth.Target.State : default;
            var vertexBindings = new VertexInputBindingDescription[vertexInput.Buffers.Length];
            var attributes = vertexStage.VertexAttributes;
            var vertexAttributes = new VertexInputAttributeDescription[attributes.Length];
            for (var index = 0; index < attributes.Length; index++)
            {
                var attribute = attributes[index];
                if (attribute.BufferIndex < 0 || attribute.BufferIndex >= vertexBindings.Length)
                {
                    throw SubmissionScheduler.Fatal($"The vertex attribute names a buffer outside the input: location={attribute.Location} buffer={attribute.BufferIndex} buffers={vertexBindings.Length}.");
                }

                vertexBindings[attribute.BufferIndex] = new VertexInputBindingDescription
                {
                    Binding = (uint)attribute.BufferIndex,
                    Stride = vertexInput.Buffers[attribute.BufferIndex].Stride,
                    InputRate = attribute.PerInstance ? VertexInputRate.Instance : VertexInputRate.Vertex,
                };
                vertexAttributes[index] = new VertexInputAttributeDescription
                {
                    Location = attribute.Location,
                    Binding = (uint)attribute.BufferIndex,
                    Format = ToVkVertexFormat(attribute.DataFormat, attribute.NumberFormat, attribute.ComponentCount),
                    Offset = attribute.OffsetBytes,
                };
            }

            for (var index = 0; index < vertexBindings.Length; index++)
            {
                vertexBindings[index].Binding = (uint)index;
                vertexBindings[index].Stride = vertexInput.Buffers[index].Stride;
            }

            var description = new RenderPipelineDescription(
                vertexStage.Shader.Payload,
                pixelStage?.Shader.Payload,
                colorFormats,
                rendering.DepthFormat,
                rendering.StencilFormat,
                ImageDescription.VulkanSampleCount(rendering.Samples),
                topology,
                primitiveRestartEnabled,
                blends,
                cullMode,
                mode.FrontFaceClockwise ? FrontFace.Clockwise : FrontFace.CounterClockwise,
                !context.Clip.DirectXClipSpace,
                context.Clip.IsZClipEnabled,
                depthState.DepthBoundsTestEnabled,
                depthState.DepthMinBounds,
                depthState.DepthMaxBounds,
                depthState.StencilTestEnabled,
                ToVkStencilOpState(depthState.FrontOperations),
                ToVkStencilOpState(depthState.BackOperations),
                vertexBindings,
                vertexAttributes,
                resourceKey);
            var key = new RenderPipelineKey(
                GetShaderDigest(description.VertexSpirv),
                description.PixelSpirv is null ? string.Empty : GetShaderDigest(description.PixelSpirv),
                DescribePipelineState(description));
            if (_renderPipelines.TryGetValue(key, out var cached))
            {
                return new PipelineHandle(cached.Id, cached.Layout.Handle, UsesPushDescriptors: false);
            }

            var layout = GetOrCreateDescriptorLayout(ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, resourceKey, vertexStage.TotalGlobalBuffers, storageFlags);
            var entry = new RenderPipelineEntry
            {
                Layout = layout.PipelineLayout,
                SetLayout = layout.DescriptorSetLayout,
                Description = description,
            };
            if (!rectangleList)
            {
                entry.Pipeline = CreateRenderPipeline(description, layout.PipelineLayout);
            }

            _renderPipelines.Add(key, entry);
            return RegisterPipeline(entry);
        }

        private static StencilOpState ToVkStencilOpState(in StencilOperations operations) => new()
        {
            FailOp = operations.FailOperation,
            PassOp = operations.PassOperation,
            DepthFailOp = operations.DepthFailOperation,
            CompareOp = operations.Compare,
        };

        // The cache key text of everything but the shaders.
        private static string DescribePipelineState(RenderPipelineDescription description)
        {
            var key = new StringBuilder();
            key.Append((uint)description.Samples).Append('|');
            foreach (var format in description.ColorFormats)
            {
                key.Append((uint)format).Append(',');
            }

            key.Append('|').Append((uint)description.DepthFormat).Append('|').Append((uint)description.StencilFormat)
                .Append('|').Append((uint)description.Topology).Append('|').Append(description.PrimitiveRestart ? 1 : 0).Append('|');
            foreach (var blend in description.Blends)
            {
                key.Append((uint)blend.ColorWriteMask).Append(':').Append(blend.BlendEnable ? 1 : 0).Append(':')
                    .Append((int)blend.SrcColorBlendFactor).Append(':').Append((int)blend.DstColorBlendFactor).Append(':').Append((int)blend.ColorBlendOp).Append(':')
                    .Append((int)blend.SrcAlphaBlendFactor).Append(':').Append((int)blend.DstAlphaBlendFactor).Append(':').Append((int)blend.AlphaBlendOp).Append(';');
            }

            key.Append('|').Append((uint)description.CullMode).Append(':').Append((int)description.FrontFace).Append(':')
                .Append(description.NegativeOneToOne ? 1 : 0).Append(':').Append(description.DepthClipEnable ? 1 : 0).Append(':')
                .Append(description.DepthBoundsTest ? 1 : 0).Append(':').Append(description.DepthMinBounds).Append(':').Append(description.DepthMaxBounds).Append(':')
                .Append(description.StencilTest ? 1 : 0).Append(':');
            AppendStencil(key, description.StencilFront);
            AppendStencil(key, description.StencilBack);
            key.Append('|');
            foreach (var binding in description.VertexBindings)
            {
                key.Append(binding.Binding).Append(',').Append(binding.Stride).Append(',').Append((int)binding.InputRate).Append(';');
            }

            key.Append('|');
            foreach (var attribute in description.VertexAttributes)
            {
                key.Append(attribute.Location).Append(',').Append(attribute.Binding).Append(',').Append((uint)attribute.Format).Append(',').Append(attribute.Offset).Append(';');
            }

            key.Append('|').Append(description.ResourceKey);
            return key.ToString();
        }

        private static void AppendStencil(StringBuilder key, in StencilOpState state) =>
            key.Append((int)state.FailOp).Append(',').Append((int)state.PassOp).Append(',').Append((int)state.DepthFailOp).Append(',').Append((int)state.CompareOp).Append(';');

        // One graphics pipeline for dynamic rendering: the attachment formats travel in the create info.
        private Pipeline CreateRenderPipeline(RenderPipelineDescription description, PipelineLayout layout)
        {
            var vertexModule = CreateShaderModule(description.VertexSpirv);
            var pixelModule = description.PixelSpirv is null ? default : CreateShaderModule(description.PixelSpirv);
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var shaderStages = stackalloc PipelineShaderStageCreateInfo[2];
                var stageCount = 1u;
                shaderStages[0] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vertexModule,
                    PName = entryPoint,
                };
                if (pixelModule.Handle != 0)
                {
                    shaderStages[stageCount++] = new PipelineShaderStageCreateInfo
                    {
                        SType = StructureType.PipelineShaderStageCreateInfo,
                        Stage = ShaderStageFlags.FragmentBit,
                        Module = pixelModule,
                        PName = entryPoint,
                    };
                }

                fixed (VertexInputBindingDescription* vertexBindings = description.VertexBindings)
                fixed (VertexInputAttributeDescription* vertexAttributes = description.VertexAttributes)
                fixed (PipelineColorBlendAttachmentState* blends = description.Blends)
                fixed (Format* colorFormats = description.ColorFormats)
                {
                    var vertexInput = new PipelineVertexInputStateCreateInfo
                    {
                        SType = StructureType.PipelineVertexInputStateCreateInfo,
                        VertexBindingDescriptionCount = (uint)description.VertexBindings.Length,
                        PVertexBindingDescriptions = description.VertexBindings.Length == 0 ? null : vertexBindings,
                        VertexAttributeDescriptionCount = (uint)description.VertexAttributes.Length,
                        PVertexAttributeDescriptions = description.VertexAttributes.Length == 0 ? null : vertexAttributes,
                    };
                    var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                    {
                        SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                        Topology = description.Topology,
                        PrimitiveRestartEnable = description.PrimitiveRestart,
                    };
                    var depthClipControl = new PipelineViewportDepthClipControlCreateInfoEXT
                    {
                        SType = StructureType.PipelineViewportDepthClipControlCreateInfoExt,
                        NegativeOneToOne = description.NegativeOneToOne,
                    };
                    var viewportState = new PipelineViewportStateCreateInfo
                    {
                        SType = StructureType.PipelineViewportStateCreateInfo,
                        PNext = _supportsDepthClipControl ? &depthClipControl : null,
                        ViewportCount = 1,
                        ScissorCount = 1,
                    };
                    var depthClip = new PipelineRasterizationDepthClipStateCreateInfoEXT
                    {
                        SType = StructureType.PipelineRasterizationDepthClipStateCreateInfoExt,
                        DepthClipEnable = description.DepthClipEnable,
                    };
                    var rasterization = new PipelineRasterizationStateCreateInfo
                    {
                        SType = StructureType.PipelineRasterizationStateCreateInfo,
                        PNext = _supportsDepthClipEnable ? &depthClip : null,
                        PolygonMode = PolygonMode.Fill,
                        CullMode = description.CullMode,
                        FrontFace = description.FrontFace,
                        LineWidth = 1,
                    };
                    var multisample = new PipelineMultisampleStateCreateInfo
                    {
                        SType = StructureType.PipelineMultisampleStateCreateInfo,
                        RasterizationSamples = description.Samples,
                        MinSampleShading = 1f,
                    };
                    var colorBlend = new PipelineColorBlendStateCreateInfo
                    {
                        SType = StructureType.PipelineColorBlendStateCreateInfo,
                        LogicOp = LogicOp.Copy,
                        AttachmentCount = (uint)description.Blends.Length,
                        PAttachments = description.Blends.Length == 0 ? null : blends,
                    };
                    var depthStencil = new PipelineDepthStencilStateCreateInfo
                    {
                        SType = StructureType.PipelineDepthStencilStateCreateInfo,
                        DepthBoundsTestEnable = _supportsDepthBounds && description.DepthBoundsTest,
                        StencilTestEnable = description.StencilTest,
                        Front = description.StencilFront,
                        Back = description.StencilBack,
                        MinDepthBounds = description.DepthMinBounds,
                        MaxDepthBounds = description.DepthMaxBounds,
                    };
                    var dynamicStates = stackalloc DynamicState[13];
                    dynamicStates[0] = DynamicState.Viewport;
                    dynamicStates[1] = DynamicState.Scissor;
                    dynamicStates[2] = DynamicState.LineWidth;
                    dynamicStates[3] = DynamicState.DepthTestEnableExt;
                    dynamicStates[4] = DynamicState.DepthWriteEnableExt;
                    dynamicStates[5] = DynamicState.DepthCompareOpExt;
                    dynamicStates[6] = DynamicState.DepthBiasEnableExt;
                    dynamicStates[7] = DynamicState.DepthBias;
                    dynamicStates[8] = DynamicState.StencilCompareMask;
                    dynamicStates[9] = DynamicState.StencilReference;
                    dynamicStates[10] = DynamicState.StencilWriteMask;
                    dynamicStates[11] = DynamicState.BlendConstants;
                    var dynamicStateCount = 12u;
                    // Last so a pipeline without color attachments can leave it out.
                    if (_colorWriteEnableApi is not null && description.ColorFormats.Length != 0)
                    {
                        dynamicStates[dynamicStateCount++] = DynamicState.ColorWriteEnableExt;
                    }

                    var dynamicState = new PipelineDynamicStateCreateInfo
                    {
                        SType = StructureType.PipelineDynamicStateCreateInfo,
                        DynamicStateCount = dynamicStateCount,
                        PDynamicStates = dynamicStates,
                    };
                    var renderingInfo = new PipelineRenderingCreateInfo
                    {
                        SType = StructureType.PipelineRenderingCreateInfo,
                        ColorAttachmentCount = (uint)description.ColorFormats.Length,
                        PColorAttachmentFormats = description.ColorFormats.Length == 0 ? null : colorFormats,
                        DepthAttachmentFormat = description.DepthFormat,
                        StencilAttachmentFormat = description.StencilFormat,
                    };
                    var pipelineInfo = new GraphicsPipelineCreateInfo
                    {
                        SType = StructureType.GraphicsPipelineCreateInfo,
                        PNext = &renderingInfo,
                        StageCount = stageCount,
                        PStages = shaderStages,
                        PVertexInputState = &vertexInput,
                        PInputAssemblyState = &inputAssembly,
                        PViewportState = &viewportState,
                        PRasterizationState = &rasterization,
                        PMultisampleState = &multisample,
                        PDepthStencilState = &depthStencil,
                        PColorBlendState = &colorBlend,
                        PDynamicState = &dynamicState,
                        Layout = layout,
                    };
                    Check(_vk.CreateGraphicsPipelines(_device, _pipelineCache, 1, &pipelineInfo, null, out var pipeline), "vkCreateGraphicsPipelines(rendering)");
                    MarkPipelineCacheDirty();
                    Interlocked.Increment(ref _perfPipelineCreations);
                    SetDebugName(
                        ObjectType.Pipeline,
                        pipeline.Handle,
                        $"SharpEmu graphics vs={description.VertexSpirv.Length}b ps={description.PixelSpirv?.Length ?? 0}b colors={description.ColorFormats.Length}");
                    return pipeline;
                }
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
                if (pixelModule.Handle != 0)
                {
                    _vk.DestroyShaderModule(_device, pixelModule, null);
                }

                _vk.DestroyShaderModule(_device, vertexModule, null);
            }
        }

        public PipelineHandle CreateComputePipeline(ComputeInputInfo input, ShaderProgram program)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.PipelineSetup);
            _ = program;
            var stage = RequireCompiledProgram(input.Stage);
            _computeThreadLimits[0] = input.DispatchThreadDimensions ? input.DispatchThreadsX : uint.MaxValue;
            _computeThreadLimits[1] = input.DispatchThreadDimensions ? input.DispatchThreadsY : uint.MaxValue;
            _computeThreadLimits[2] = input.DispatchThreadDimensions ? input.DispatchThreadsZ : uint.MaxValue;
            var storageImages = new List<bool>();
            AppendStorageFlags(storageImages, stage.Textures);
            var storageFlags = storageImages.ToArray();
            var resourceKey = ResourceKey(stage.TotalGlobalBuffers, storageFlags);
            var spirv = stage.Shader.Payload;
            var key = new ComputePipelineKey(GetShaderDigest(spirv), resourceKey);
            if (_computeEntries.TryGetValue(key, out var cached))
            {
                return new PipelineHandle(cached.Id, cached.Layout.Handle, UsesPushDescriptors: false);
            }

            var layout = GetOrCreateDescriptorLayout(ShaderStageFlags.ComputeBit, resourceKey, stage.TotalGlobalBuffers, storageFlags);
            var computeModule = CreateShaderModule(spirv);
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            Pipeline pipeline;
            try
            {
                var stageInfo = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = computeModule,
                    PName = entryPoint,
                };
                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = stageInfo,
                    Layout = layout.PipelineLayout,
                };
                Check(_vk.CreateComputePipelines(_device, _pipelineCache, 1, &pipelineInfo, null, out pipeline), "vkCreateComputePipelines(rendering)");
                MarkPipelineCacheDirty();
                Interlocked.Increment(ref _perfPipelineCreations);
                SetDebugName(ObjectType.Pipeline, pipeline.Handle, $"SharpEmu compute cs={spirv.Length}b");
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
                _vk.DestroyShaderModule(_device, computeModule, null);
            }

            var entry = new RenderPipelineEntry
            {
                Pipeline = pipeline,
                Layout = layout.PipelineLayout,
                SetLayout = layout.DescriptorSetLayout,
            };
            _computeEntries.Add(key, entry);
            return RegisterPipeline(entry);
        }

        private void DestroyRenderPipelines()
        {
            foreach (var entry in _pipelineEntries.Values)
            {
                foreach (var pipeline in new[] { entry.Pipeline, entry.StripVariant, entry.ListVariant })
                {
                    if (pipeline.Handle != 0)
                    {
                        _vk.DestroyPipeline(_device, pipeline, null);
                    }
                }
            }

            _pipelineEntries.Clear();
            _renderPipelines.Clear();
            _computeEntries.Clear();
        }
    }
}
