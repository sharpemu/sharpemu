// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Vulkan;
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
    private const string ColorWriteEnableExtensionName = "VK_EXT_color_write_enable";
    private const string DepthClipControlExtensionName = "VK_EXT_depth_clip_control";
    private const string DepthClipEnableExtensionName = "VK_EXT_depth_clip_enable";
    private const int DrawsPerBatch = 64;
    // A full batch does not split an open render pass: ending it there flushed and
    // restarted Demon's Souls' 1440p G-buffer pass (five targets + depth) every 64 draws.
    // The pass still ends at this cap so a long pass cannot hold the batch forever.
    private const int DrawsPerBatchInRenderPass = 512;
    private const uint SingleRectangleVertexCount = 4;

    private static void RequireRenderingFeature(bool supported, string feature, string deviceName)
    {
        if (!supported)
        {
            throw SubmissionScheduler.Fatal($"The device lacks a required rendering feature: device={deviceName} feature={feature}.");
        }
    }

    private sealed partial class Presenter
    {
        internal ExtColorWriteEnable? _colorWriteEnableApi;
        internal bool _supportsDepthClipControl;
        internal bool _supportsDepthClipEnable;
        internal bool _supportsDepthBounds;
        internal bool _supportsShaderDrawParameters;
        internal bool _supportsFillRectangle;
        internal bool _supportsRectangleGeometry;
        internal RenderHostLimits _renderHostLimits;
        internal IGuestBackedSpace _guestBacking = null!;

        internal readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, RenderPipelineEntry> _pipelineEntries = new();
        private ulong _nextPipelineId;

        private void LoadRenderingCommands(bool supportsColorWriteEnable, string deviceName)
        {
            if (!_vk.TryGetDeviceExtension(_instance, _device, out _pushDescriptorApi))
            {
                throw SubmissionScheduler.Fatal($"The device extension commands are unavailable: device={deviceName} extension={PushDescriptorExtensionName}.");
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
                $"depth_bounds={(_supportsDepthBounds ? 1 : 0)} fill_rectangle={(_supportsFillRectangle ? 1 : 0)}");
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
    }

    private sealed partial class RecordingHost : IRenderHost
    {
        // Keep each new stream allocation intact until the draw is recorded.
        private sealed class RenderPreparation(RecordingHost owner) : IResourcePreparation
        {
            private IDisposable? streamRetention;

            public RenderPreparation Open(IDisposable? retention)
            {
                streamRetention = retention;
                StencilStorageImages = null;
                StencilStorageWriteBackImages = null;
                FeedbackSnapshots = null;
                Committed = false;
                CommandsRecorded = false;
                return this;
            }

            public List<PreparedStageBindings> Stages { get; } = new();

            // Host buffers that took an upload the ring could not hold; the committed draw owns them.
            public List<(VkBuffer Buffer, DeviceMemory Memory)> OverflowBuffers { get; } = new();

            public Dictionary<CachedImage, CachedImage>? StencilStorageImages { get; set; }

            public HashSet<CachedImage>? StencilStorageWriteBackImages { get; set; }

            public List<CachedImage>? FeedbackSnapshots { get; set; }

            public bool Committed { get; set; }

            public bool CommandsRecorded { get; set; }

            public void Dispose()
            {
                if (!ReferenceEquals(owner._preparation, this))
                {
                    return;
                }

                owner._preparation = null;
                owner._preparedTextures.Clear();
                try
                {
                    streamRetention?.Dispose();
                    if (StencilStorageImages is { } stencilImages)
                    {
                        foreach (var (attachment, storage) in stencilImages)
                        {
                            try
                            {
                                if (CommandsRecorded && StencilStorageWriteBackImages is not null &&
                                    StencilStorageWriteBackImages.Contains(attachment))
                                    attachment.CopyStencilStorage(storage, owner._bufferCache.GetUtilityBuffer(GpuBufferUsage.DeviceLocal), writeBack: true);
                            }
                            finally
                            {
                                owner._scheduler.QueueCompletionAction(storage.Dispose);
                            }
                        }
                    }

                    if (!Committed && FeedbackSnapshots is { } feedbackSnapshots)
                    {
                        foreach (var snapshot in feedbackSnapshots)
                        {
                            snapshot.Dispose();
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
                        owner._presenter.RecycleHostBuffer(buffer, memory);
                    }
                }
                finally
                {
                    foreach (var stage in Stages) owner.ReturnStageScratch(stage);
                    Stages.Clear();
                    OverflowBuffers.Clear();
                    streamRetention = null;
                }
            }
        }

        private RenderPreparation? _preparation;
        private RenderPreparation? _sparePreparation;
        private RenderPipelineEntry? _boundGraphicsPipeline;
        private ulong _profileComputePipeline;

        internal bool _renderingActive;
        private RenderingState _renderingState;
        private VulkanCommandProfile.MeshDrawState _profileMeshDrawState;
        private long _renderingScopesBegun;
        // SHARPEMU_DEFER_GLOBAL_BARRIERS=0 ends the rendering scope at every guest cache flush again.
        internal static readonly bool DeferGlobalBarriers =
            Environment.GetEnvironmentVariable("SHARPEMU_DEFER_GLOBAL_BARRIERS") != "0";
        internal bool _renderingWritesMemory;
        private bool _nextDrawWritesMemory;
        internal bool _globalBarrierAfterRendering;
        // Attachment barriers that order this rendering scope before later work; see TryDeferUntilRenderingEnds.
        private readonly List<ImageMemoryBarrier2> _barriersAfterRendering = new();
        private PipelineStageFlags _afterRenderingSourceStages;
        private PipelineStageFlags _afterRenderingDestinationStages;
        private bool _hasBoundDepth;
        private DepthAttachmentState _boundDepth;
        private DepthStencilState _boundDepthLoadState;
        // A sampled depth target was cleared with a transfer; the rendering scope loads it instead.
        private bool _depthClearRecordedSeparately;

        RenderHostLimits IRenderHost.Limits => _renderHostLimits;

        bool IRenderHost.SupportsNativeRectangles => _presenter._supportsFillRectangle || _presenter._supportsRectangleGeometry;
        bool IRenderHost.SupportsTessellation => _presenter._supportsTessellation;

        bool IRenderHost.TryCopyImage(in ImageRequest source, in ImageRequest destination)
        {
            if (_worker)
                throw Serial("frontend image copy");
            return _imageCache.TryCopyImage(source, destination);
        }

        IImageFormatSupport IRenderHost.FormatSupport => _deviceInfo;

        public bool IsRecording
        {
            get
            {
                if (_presenter._deviceLost)
                {
                    return false;
                }

                if (_worker)
                {
                    return true;
                }

                EnsureBatchedGuestCommands();
                return _scheduler.Active;
            }
        }

        public void RunPendingOperations()
        {
            if (_worker)
            {
                return;
            }

            _presenter.RunPendingCommands();
            if (_presenter._batchOpen && _preparation is null && _bufferCache?.HotWritePending == true && _presenter._batchDrawCount >= Presenter.MinimumFlushDraws)
            {
                EndRendering();
                FlushBatchedGuestCommands();
            }
        }

        public void SetDebugInformation(RecordedOperation operation, ulong submitId, uint argument0, uint argument1, uint argument2, uint argument3, ulong argument4) =>
            CurrentRecording().SetDebugInfo((uint)operation, submitId, argument0, argument1, argument2, argument3, argument4);

        // The part of the range that is mapped from its start; unmapped starts are fatal as the executor cannot bind them.
        public ulong ClampMappedSize(ulong address, ulong size)
            => ClampMappedSize(address, size, null, -1);

        private ulong ClampMappedSize(ulong address, ulong size, PreparedStageBindings? prepared, int bufferIndex)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BufferMappedRangeValidation);
            // A whole readable range is the common case and answers for its start too.
            var valid = address != 0 && size != 0 && size <= ulong.MaxValue - address;
            if (valid && _guestMemory.CanRead(address, size))
            {
                return size;
            }

            if (!valid || !_guestMemory.CanRead(address, 1))
            {
                var context = string.Empty;
                if (prepared is not null)
                {
                    var resource = prepared.Resources.Info.Buffers[bufferIndex];
                    var words = prepared.Stage.Resources.Buffers[bufferIndex];
                    context = $" stage={prepared.Program.Stage} hash=0x{prepared.Program.Hash:X16} shader=0x{prepared.Stage.ShaderBase:X16} " +
                        $"buffer={bufferIndex} source={resource.Source} pc=0x{resource.FirstUsePc:X} " +
                        $"descriptor=[{string.Join(",", words.Select(word => $"{word:X8}"))}] " +
                        $"user_data=[{string.Join(",", prepared.Stage.Resources.UserData.Select(word => $"{word:X8}"))}]";
                }
                throw SubmissionScheduler.Fatal($"The buffer range starts in unmapped memory: address=0x{address:X16} size=0x{size:X16}.{context}");
            }

            // Keep the readable prefix only; a later mapping must not hide a gap.
            var clamped = 1UL;
            var rejectedSize = size;
            while (rejectedSize - clamped > 1)
            {
                var candidateSize = clamped + (rejectedSize - clamped) / 2;
                if (_guestMemory.CanRead(address, candidateSize))
                {
                    clamped = candidateSize;
                }
                else
                {
                    rejectedSize = candidateSize;
                }
            }

            if (RenderTrace.Enabled)
            {
                RenderTrace.Write($"Clamped a buffer range to its mapped part: address=0x{address:X16} size=0x{size:X16} clamped=0x{clamped:X16}");
            }

            return clamped;
        }

        public ResourceSlotIdentifier FindImage(ref ImageRequest request, bool exactFormat)
        {
            _imageCache.TraceNativeColorMetadata(request);
            _imageCache.TraceTextureMetadata(request);
            SynchronizeColorMetadata(request);
            if (_worker)
            {
                return LookUpImage(ref request, exactFormat);
            }

            EnsureBatchedGuestCommands();
            if (request.Role == ImageRole.ColorTarget && !request.Description.Metadata.NativeColorClear &&
                request.Description.DccSliceSize is var sliceSize and not 0)
            {
                _imageCache.SynchronizeGuestDccMetadata(request.Description.Metadata.Range.Address, sliceSize,
                    request.View.BaseLayer, request.View.LayerCount);
            }

            var imageIdentifier = _imageCache.FindImage(ref request, exactFormat);
            ApplyNativeColorClear(imageIdentifier, request);
            return imageIdentifier;
        }

        private ResourceSlotIdentifier LookUpImage(ref ImageRequest request, bool exactFormat = false) =>
            _worker
                ? _imageCache.TryFindImageForSlice(ref request, exactFormat, out var found) ? found : throw Serial("image lookup", resumable: true)
                : _imageCache.FindImage(ref request, exactFormat);

        public void ResetBindings()
        {
            ResetImageBindings();
            _hasBoundDepth = false;
            _depthClearRecordedSeparately = false;
            _boundGraphicsPipeline = null;
        }

        // The acquisition below reads the attachment back into its result and keeps no reference to it,
        // so one render-thread instance serves every draw.
        private ColorAttachment? _colorAttachmentScratch;

        ColorAttachmentAcquisition IRenderHost.AcquireColorAttachment(in ColorTargetState target)
        {
            var attachment = _colorAttachmentScratch ??= new ColorAttachment();
            attachment.ImageIdentifier = target.Image;
            attachment.Request = target.Resolution.Request;
            attachment.Resolution = target.Resolution;
            attachment.Image = null!;
            attachment.View = default;
            attachment.Clear = false;
            attachment.ClearValue = default;
            AcquireColorAttachment(attachment);
            return new ColorAttachmentAcquisition(
                attachment.ImageIdentifier,
                attachment.View,
                attachment.Image.Backing.Samples,
                attachment.Clear,
                attachment.ClearValue);
        }

        DepthAttachmentAcquisition IRenderHost.AcquireDepthAttachment(in DepthAttachmentState depth)
        {
            if (_worker)
            {
                return AcquireDepthAttachmentForSlice(in depth);
            }

            var resolution = depth.Target.Target;
            if (IsStaleImage(depth.Image, out _))
            {
                throw SubmissionScheduler.Fatal($"The depth target changed after render-state discovery: address=0x{resolution.DepthAddress:X16}.");
            }

            var view = _imageCache.AcquireDepthTargetView(depth.Image, resolution.Request);
            EnsureBatchedGuestCommands();
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
        public DrawScopeToken DrawScope =>
            _renderingActive ? new DrawScopeToken(_renderingScopesBegun, _imageCache.LookupGeneration, _imageCache.MetadataGeneration) : default;

        public void KeepDepthAttachment(in DepthAttachmentState depth)
        {
            _hasBoundDepth = true;
            _boundDepth = depth;
            _boundDepthLoadState = depth.LoadState;
        }

        private readonly StagePreparationMemo?[] _keptTextureSets = new StagePreparationMemo?[2];
        private readonly StagePreparationMemo?[] _committedTextureSets = new StagePreparationMemo?[2];
        private DrawScopeToken _keptTextureScope;
        private static long _texturesKept;

        internal static long TakeTexturesKept() => Interlocked.Exchange(ref _texturesKept, 0);

        private static int TextureSetSlot(ShaderProgramInfo program) => program.Stage switch
        {
            ShaderStageKind.Vertex => 0,
            ShaderStageKind.Pixel => 1,
            _ => -1,
        };

        private bool KeepsTextureSet(PreparedStageBindings stage)
        {
            var slot = TextureSetSlot(stage.Program);
            return slot >= 0 && stage.Memoized && stage.Memo is { Valid: true } memo && ReferenceEquals(_keptTextureSets[slot], memo) &&
                _keptTextureScope.IsActive && DrawScope == _keptTextureScope && _presenter._hostMovieFramePixels is null;
        }

        public void TransitionDepthAttachment(in DepthAttachmentState depth, ImageAspectFlags writeAspects)
        {
            var command = BeginBatchedGuestCommands();
            var image = Image(depth.Image);
            var view = depth.Target.Target.Request.View;
            var access = AccessFlags.DepthStencilAttachmentReadBit | (writeAspects != 0 ? AccessFlags.DepthStencilAttachmentWriteBit : 0);
            TransitionImage(image, access, new SubresourceRange(view.BaseLevel, view.LevelCount, view.BaseLayer, view.LayerCount), command);
            _hasBoundDepth = true;
            _boundDepth = depth;
            _boundDepthLoadState = depth.LoadState;
        }

        public BufferBinding NullBuffer => new(NullBufferHandle.Handle, 0);

        public ulong UploadMeshVertexIndices(ReadOnlySpan<uint> indices)
        {
            if (_worker)
            {
                throw Serial("mesh index upload", resumable: true);
            }

            var preparation = RequirePreparation();
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(indices);
            var buffer = _presenter.CreateHostBuffer(bytes,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
                out var memory, out _);
            preparation.OverflowBuffers.Add((buffer, memory));
            var addressInfo = new BufferDeviceAddressInfo
            {
                SType = StructureType.BufferDeviceAddressInfo,
                Buffer = buffer,
            };
            var address = _vk.GetBufferDeviceAddress(_device, &addressInfo);
            if (address == 0) throw new InvalidOperationException("The mesh index buffer has no device address.");
            return address;
        }

        public ulong ObtainBufferDeviceAddress(ulong address, ulong size, bool isWritten)
        {
            if (_worker)
                throw Serial("tessellation device addresses");
            var (buffer, offset) = _bufferCache.ObtainBuffer(address, size, isWritten, requiresDeviceAddress: true);
            return checked(buffer.DeviceAddress + offset);
        }

        public void PrepareBufferAllocations(ReadOnlySpan<SharpEmu.HLE.GpuMemory.GuestSpan> ranges)
        {
            if (_worker)
                throw Serial("tessellation device addresses");
            _bufferCache.PrepareBufferAllocations(ranges);
        }

        public BufferBinding ObtainBuffer(ulong address, ulong size, bool isWritten)
        {
            if (_worker)
            {
                var (sliceBuffer, sliceOffset) = ObtainBufferForSlice(address, size, isWritten, isTexelBuffer: false);
                return new BufferBinding(sliceBuffer.Handle.Handle, sliceOffset);
            }

            var (buffer, offset) = _bufferCache.ObtainBuffer(address, size, isWritten);
            return new BufferBinding(buffer.Handle.Handle, offset);
        }

        // The ring takes the bytes unless it would wrap over a prepared binding; then a host buffer of the draw takes them.
        public BufferBinding UploadTransient(ReadOnlySpan<byte> data, uint alignment)
        {
            if (_worker)
            {
                return _arena!.TryUpload(data, alignment, out var arenaBuffer, out var arenaOffset) ? new BufferBinding(arenaBuffer.Handle, arenaOffset) : throw Serial("arena", resumable: true);
            }

            var preparation = RequirePreparation();
            var stream = _bufferCache.GetUtilityBuffer(GpuBufferUsage.Stream);
            if (stream.TryMap((ulong)data.Length, out var offset, alignment))
            {
                data.CopyTo(stream.Mapped[(int)offset..]);
                stream.Commit();
                return new BufferBinding(stream.Handle.Handle, offset);
            }

            // Transient uploads also back storage-buffer descriptors, so match the ring's usage.
            var buffer = _presenter.CreateHostBuffer(data, GpuBuffer.AllFlags, out var memory, out _);
            preparation.OverflowBuffers.Add((buffer, memory));
            if (RenderTrace.Enabled)
            {
                RenderTrace.Write($"The stream ring could not take an upload inside a preparation; a host buffer holds it: bytes={data.Length} alignment={alignment}");
            }

            return new BufferBinding(buffer.Handle, 0);
        }

        public void BindVertexBuffers(ReadOnlySpan<BufferBinding> bindings, VertexInputInfo input)
        {
            if (bindings.IsEmpty) return;
            var command = BeginBatchedGuestCommands();
            if (VertexBuffersAlreadyBound(command, bindings))
            {
                return;
            }

            var buffers = stackalloc VkBuffer[bindings.Length];
            var offsets = stackalloc ulong[bindings.Length];
            for (var index = 0; index < bindings.Length; index++)
            {
                buffers[index] = new VkBuffer(bindings[index].Handle);
                offsets[index] = bindings[index].Offset;
            }

            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
            {
                _vk.CmdBindVertexBuffers(command, 0, (uint)bindings.Length, buffers, offsets);
            }
        }

        public void BindIndexBuffer(BufferBinding binding, IndexType type)
        {
            var command = BeginBatchedGuestCommands();
            if (!IndexBufferAlreadyBound(command, new VkBuffer(binding.Handle), binding.Offset, type))
            {
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                {
                    _vk.CmdBindIndexBuffer(command, new VkBuffer(binding.Handle), binding.Offset, type);
                }
            }
        }

        public IResourcePreparation BeginPreparation()
        {
            if (_preparation is not null)
            {
                throw SubmissionScheduler.Fatal("A resource preparation is already open.");
            }

            if (_worker)
            {
                _preparation = (_sparePreparation ??= new RenderPreparation(this)).Open(null);
                return _preparation;
            }

            // Acquire the decoded frame before any stage selects its movie texture planes.
            _presenter.PumpHostMovieFrame();

            if (_presenter._batchDrawCount >= (_renderingActive ? DrawsPerBatchInRenderPass : DrawsPerBatch))
            {
                if (GpuSyncProfile.Enabled)
                {
                    GpuSyncProfile.CountBatchFlush();
                }

                EndRendering();
                FlushBatchedGuestCommands();
            }

            EnsureGuestSubmissionCapacity();
            EnsureBatchedGuestCommands();
            _preparation = (_sparePreparation ??= new RenderPreparation(this)).Open(_bufferCache.GetUtilityBuffer(GpuBufferUsage.Stream).RetainUpcomingAllocations());
            return _preparation;
        }

        private RenderPreparation RequirePreparation() =>
            _preparation ?? throw SubmissionScheduler.Fatal("The resource preparation is not open.");

        private RenderPipelineEntry RequirePipelineEntry(in PipelineHandle pipeline) =>
            _presenter._pipelineEntries.TryGetValue(pipeline.Pipeline, out var entry)
                ? entry
                : throw SubmissionScheduler.Fatal($"The pipeline handle is unknown: pipeline={pipeline.Pipeline} layout={pipeline.Layout}.");

        // Vulkan cannot sample a stencil aspect while the same aspect is attached for writes.
        // Use a per-draw storage copy for read-only stencil feedback and keep the attachment authoritative.
        private void PrepareStencilFeedback(TextureResource[] bindings)
        {
            if (!_hasBoundDepth)
            {
                return;
            }

            var depthImage = Image(_boundDepth.Image);
            var attachmentView = _boundDepth.Target.Target.Request.View;
            var writes = _boundDepthLoadState.AttachmentWriteAspects(_boundDepth.Target.Target.Format);
            if ((writes & ImageAspectFlags.StencilBit) == 0)
            {
                return;
            }

            foreach (var binding in bindings)
            {
                if (binding.IsHostMovie || binding.CachedImage is not { } image ||
                    !ReferenceEquals(image, depthImage) || !ViewFormatRules.IsStencilViewFormat(binding.Request.View.Format) ||
                    !ViewsOverlap(binding.Request.View, attachmentView))
                {
                    continue;
                }

                var storage = AcquireStencilStorage(binding, image, writeBack: false);
                binding.Preparation?.Invalidate();
                binding.CachedImage = storage;
                binding.Image = storage.Backing.Handle;
                binding.View = storage.GetOrCreateView(binding.Request.View with { Aspect = ImageAspectFlags.ColorBit });
                binding.MipViews = [];
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
                depthImage = Image(_boundDepth.Image);
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

                        if (_worker)
                        {
                            throw Serial("sampled depth clear");
                        }

                        // Complete the load clears before the shader reads, then restore the attachment for the draw.
                        RecordSampledDepthClear(depthImage, depthView, depthFormat);
                        _boundDepthLoadState = _boundDepthLoadState with { DepthClearEnabled = false, StencilClearEnabled = false };
                        _depthClearRecordedSeparately = true;
                        var remainingWrites = _boundDepthLoadState.AttachmentWriteAspects(depthFormat);
                        TransitionImage(depthImage,
                            AccessFlags.DepthStencilAttachmentReadBit | (remainingWrites != 0 ? AccessFlags.DepthStencilAttachmentWriteBit : 0),
                            new SubresourceRange(depthView.BaseLevel, depthView.LevelCount, depthView.BaseLayer, depthView.LayerCount),
                            BeginBatchedGuestTransferCommands());
                        break;
                    }
                }
            }

            RecordSampledColorMetadataClears(bindings);

            // Create feedback copies after load clears have been materialized;
            // otherwise a shader would sample the pre-clear contents.
            PrepareDepthFeedback(bindings);
            PrepareStencilFeedback(bindings);

            var command = BeginBatchedGuestTransferCommands();
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
                    TransitionImage(image, storage || image.Binding.ShaderWrite ? AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit : AccessFlags.ShaderReadBit, range, command);
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

                    var attachmentAccess = overlaps
                        ? AccessFlags.DepthStencilAttachmentReadBit | (writes != 0 ? AccessFlags.DepthStencilAttachmentWriteBit : 0)
                        : 0;
                    TransitionImage(image, AccessFlags.ShaderReadBit | attachmentAccess, range, command);
                }
                else if ((image.Binding.ForceGeneral || image.Binding.IsTarget) && !image.Description.IsDepth)
                {
                    var storageAccess = image.Binding.ShaderWrite ? AccessFlags.ShaderWriteBit : 0;
                    TransitionImage(image,
                        AccessFlags.ShaderReadBit | storageAccess | AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
                        null,
                        command);
                }
                else if (storage)
                {
                    TransitionImage(image, ImageAccessState.GuestStorageAccess, range, command, guest: true);
                }
                else
                {
                    TransitionImage(image, AccessFlags.ShaderReadBit, range, command);
                }
            }
        }

        private static bool ViewsOverlap(in ImageViewDescription sampled, in ImageViewDescription attachment) =>
            (ulong)sampled.BaseLevel < (ulong)attachment.BaseLevel + attachment.LevelCount &&
            (ulong)attachment.BaseLevel < (ulong)sampled.BaseLevel + sampled.LevelCount &&
            (ulong)sampled.BaseLayer < (ulong)attachment.BaseLayer + attachment.LayerCount &&
            (ulong)attachment.BaseLayer < (ulong)sampled.BaseLayer + sampled.LayerCount;

        // Clears the depth view with a transfer so the draw can sample the cleared image.
        // A DCC fast clear stays pending until the surface binds as a color target. A shader that
        // samples or writes the surface first must see the cleared contents, and the later bind must
        // not clear over its writes: Astro Bot's save-slot cards were drawn by compute into a
        // fast-cleared target, then wiped black by the deferred clear. The register clear color is
        // known only when the surface is a target.
        private void RecordSampledColorMetadataClears(TextureResource[] bindings)
        {
            const byte DccClearToZero = 0x00;
            foreach (var binding in bindings)
            {
                if (binding.IsHostMovie || binding.CachedImage is not { } image || image.Description.Metadata.Kind != MetadataKind.Dcc ||
                    image.Description.Metadata.Range.Size != 0 || image.Description.Metadata.NativeColorClear)
                {
                    continue;
                }

                var metadataAddress = image.Description.Metadata.Range.Address;
                var sliceSize = image.Description.DccSliceSize;
                var fixedClearSupported = ImageRequestBuilders.SupportsDccFixedClear(image.Description.PixelFormat);
                var view = binding.Request.View;
                for (var layer = view.BaseLayer; layer < view.BaseLayer + view.LayerCount; layer++)
                {
                    if (ImageClearTrace.Enabled) _imageCache.TraceVolumeClear("before-sampled-decision", binding.Request, image, $"layer={layer}");
                    var tracked = _imageCache.IsMetadataCleared(metadataAddress, layer, out var metadataValue) && (byte)metadataValue == DccClearToZero;
                    var clearValue = default(ClearColorValue);
                    ulong guestSlice = 0;
                    if (!tracked &&
                        (sliceSize == 0 || !_imageCache.TryReadGuestDccClear(metadataAddress, sliceSize, layer, out guestSlice, out var code) ||
                         !TryDecodeDccClear(code, false, fixedClearSupported, default, out clearValue)))
                    {
                        if (ImageClearTrace.Enabled) _imageCache.TraceVolumeClear("sampled-skip", binding.Request, image, $"layer={layer}");
                        continue;
                    }

                    if (_worker)
                    {
                        throw Serial("sampled dcc clear");
                    }

                    EndRendering();
                    var command = BeginBatchedGuestCommands();
                    var range = new SubresourceRange(0, 1, layer, 1);
                    TransitionImage(image, AccessFlags.TransferWriteBit, range, command);
                    var vkRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, layer, 1);
                    using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                    {
                        _vk.CmdClearColorImage(command, image.Backing.Handle, ImageLayout.General, &clearValue, 1, &vkRange);
                    }

                    if (ImageClearTrace.Enabled) _imageCache.TraceVolumeClear("sampled-clear-recorded", binding.Request, image,
                        $"layer={layer} tracked={tracked} rgbaBits={clearValue.Uint32_0:X8},{clearValue.Uint32_1:X8},{clearValue.Uint32_2:X8},{clearValue.Uint32_3:X8} scope=whole-mip-for-volume");
                    if (!tracked)
                    {
                        _bufferCache.FillDccMetadata(guestSlice, sliceSize, uint.MaxValue);
                        if (ImageClearTrace.Enabled) _imageCache.TraceVolumeClear("sampled-consumed", binding.Request, image, $"address=0x{guestSlice:X} size=0x{sliceSize:X}");
                        if (RenderTrace.Enabled && RenderTrace.MetadataClear())
                        {
                            RenderTrace.Write(
                                $"Materialized a guest DCC clear on a sampled image: metadata=0x{metadataAddress:X16} layer={layer} " +
                                $"slice=0x{sliceSize:X} format={image.Description.PixelFormat}");
                        }

                        continue;
                    }

                    if (!_imageCache.SetMetadataSlice(metadataAddress, layer, false))
                    {
                        throw SubmissionScheduler.Fatal($"The DCC clear state could not be consumed: metadata=0x{metadataAddress:X16} layer={layer}.");
                    }

                    ConsumeGuestDccClears(image.Description, layer, 1);
                    if (ImageClearTrace.Enabled) _imageCache.TraceVolumeClear("sampled-tracked-consumed", binding.Request, image, $"layer={layer}");
                }
            }
        }

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
            TransitionImage(image, AccessFlags.TransferWriteBit, range, command);
            var vkRange = new ImageSubresourceRange(aspects, view.BaseLevel, view.LevelCount, view.BaseLayer, view.LayerCount);
            var value = new ClearDepthStencilValue(_boundDepthLoadState.DepthClearValue, _boundDepthLoadState.StencilClearValue);
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
            {
                _vk.CmdClearDepthStencilImage(command, image.Backing.Handle, ImageLayout.General, &value, 1, &vkRange);
            }
        }

        // The dynamic state the last guest draw set, and where it holds: the command buffer and its
        // tick (a new command buffer starts without state), and no other graphics pipeline since.
        private DynamicDrawState _lastDynamicState;
        private CommandBuffer _lastDynamicStateBuffer;
        private long _lastDynamicStateTick;
        private long _lastDynamicStateEpoch = -1;

        public void SetDynamicState(in DynamicDrawState state)
        {
            SetOcclusionCounting(state.DepthCountControl);
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawDynamicStateRecording);
            var command = BeginBatchedGuestCommands();
            if (_gpuCommandProfile is not null)
            {
                _profileMeshDrawState = new(0, 0, state.ViewportWidth, state.ViewportHeight,
                    (uint)(state.Scissor.Right - state.Scissor.Left), (uint)(state.Scissor.Bottom - state.Scissor.Top),
                    state.DepthTestEnabled, state.DepthWriteEnabled);
            }
            // Every guest pipeline keeps these states dynamic, so they persist across its binds;
            // only what changed since the last draw in this command buffer is set again.
            var known = command.Handle == _lastDynamicStateBuffer.Handle && CurrentRecording().Generation == _lastDynamicStateTick &&
                GraphicsDynamicStateEpoch.Value == _lastDynamicStateEpoch;
            ref readonly var last = ref _lastDynamicState;
            if (!known || state.ViewportX != last.ViewportX || state.ViewportY != last.ViewportY ||
                state.ViewportWidth != last.ViewportWidth || state.ViewportHeight != last.ViewportHeight ||
                state.ViewportMinDepth != last.ViewportMinDepth || state.ViewportMaxDepth != last.ViewportMaxDepth)
            {
                var viewport = new Viewport(state.ViewportX, state.ViewportY, state.ViewportWidth, state.ViewportHeight, state.ViewportMinDepth, state.ViewportMaxDepth);
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                {
                    _vk.CmdSetViewport(command, 0, 1, &viewport);
                }
            }

            if (!known || state.Scissor != last.Scissor)
            {
                var scissor = new Rect2D(
                    new Offset2D(state.Scissor.Left, state.Scissor.Top),
                    new Extent2D((uint)(state.Scissor.Right - state.Scissor.Left), (uint)(state.Scissor.Bottom - state.Scissor.Top)));
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                {
                    _vk.CmdSetScissor(command, 0, 1, &scissor);
                }
            }

            if (!known || state.LineWidth != last.LineWidth)
            {
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                {
                    _vk.CmdSetLineWidth(command, state.LineWidth);
                }
            }

            if (!known || state.BlendRed != last.BlendRed || state.BlendGreen != last.BlendGreen ||
                state.BlendBlue != last.BlendBlue || state.BlendAlpha != last.BlendAlpha)
            {
                var blendConstants = stackalloc float[4] { state.BlendRed, state.BlendGreen, state.BlendBlue, state.BlendAlpha };
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                {
                    _vk.CmdSetBlendConstants(command, blendConstants);
                }
            }

            if (!known || state.DepthTestEnabled != last.DepthTestEnabled)
            {
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                {
                    _vk.CmdSetDepthTestEnable(command, state.DepthTestEnabled);
                }
            }

            if (!known || state.DepthWriteEnabled != last.DepthWriteEnabled)
            {
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                {
                    _vk.CmdSetDepthWriteEnable(command, state.DepthWriteEnabled);
                }
            }

            if (!known || state.DepthCompare != last.DepthCompare)
            {
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                {
                    _vk.CmdSetDepthCompareOp(command, state.DepthCompare);
                }
            }

            if (!known || state.DepthBiasEnabled != last.DepthBiasEnabled)
            {
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                {
                    _vk.CmdSetDepthBiasEnable(command, state.DepthBiasEnabled);
                }
            }

            // The factors only matter while the bias is on; they are set when it turns on or changes.
            if (state.DepthBiasEnabled && (!known || !last.DepthBiasEnabled ||
                state.DepthBiasConstantFactor != last.DepthBiasConstantFactor || state.DepthBiasClamp != last.DepthBiasClamp ||
                state.DepthBiasSlopeFactor != last.DepthBiasSlopeFactor))
            {
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                {
                    _vk.CmdSetDepthBias(command, state.DepthBiasConstantFactor, _presenter._supportsDepthBiasClamp ? state.DepthBiasClamp : 0f, state.DepthBiasSlopeFactor);
                }
            }

            if (state.StencilTestEnabled && (!known || !last.StencilTestEnabled ||
                state.FrontStencil != last.FrontStencil || state.BackStencil != last.BackStencil))
            {
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                {
                    _vk.CmdSetStencilCompareMask(command, StencilFaceFlags.FaceFrontBit, state.FrontStencil.CompareMask);
                }
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                {
                    _vk.CmdSetStencilCompareMask(command, StencilFaceFlags.FaceBackBit, state.BackStencil.CompareMask);
                }
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                {
                    _vk.CmdSetStencilWriteMask(command, StencilFaceFlags.FaceFrontBit, state.FrontStencil.WriteMask);
                }
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                {
                    _vk.CmdSetStencilWriteMask(command, StencilFaceFlags.FaceBackBit, state.BackStencil.WriteMask);
                }
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                {
                    _vk.CmdSetStencilReference(command, StencilFaceFlags.FaceFrontBit, state.FrontStencil.Reference);
                }
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                {
                    _vk.CmdSetStencilReference(command, StencilFaceFlags.FaceBackBit, state.BackStencil.Reference);
                }
            }

            if (state.DepthBoundsTestEnabled && _presenter._supportsDepthBounds && (!known || !last.DepthBoundsTestEnabled ||
                state.DepthMinBounds != last.DepthMinBounds || state.DepthMaxBounds != last.DepthMaxBounds))
            {
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                {
                    _vk.CmdSetDepthBounds(command, state.DepthMinBounds, state.DepthMaxBounds);
                }
            }

            if (_presenter._colorWriteEnableApi is { } colorWriteEnable && state.ColorWriteCount != 0)
            {
                var enables = stackalloc Bool32[RenderingState.ColorAttachmentCapacity];
                for (var index = 0; index < state.ColorWriteCount; index++)
                {
                    enables[index] = ((state.ColorWriteEnableMask >> index) & 1) != 0;
                }

                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                {
                    colorWriteEnable.CmdSetColorWriteEnable(command, state.ColorWriteCount, enables);
                }
            }

            _lastDynamicState = state;
            _lastDynamicStateBuffer = command;
            _lastDynamicStateTick = CurrentRecording().Generation;
            _lastDynamicStateEpoch = GraphicsDynamicStateEpoch.Value;
        }



        internal static BlendFactor ToVkBlendFactor(uint factor) =>
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


        internal static BlendOp ToVkBlendOp(uint function) =>
            function switch
            {
                0 => BlendOp.Add,
                1 => BlendOp.Subtract,
                2 => BlendOp.Min,
                3 => BlendOp.Max,
                4 => BlendOp.ReverseSubtract,
                _ => BlendOp.Add,
            };


        internal static ColorComponentFlags ToVkColorWriteMask(uint mask)
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

        public void BeginRendering(in RenderingState state)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawRenderingSetup);
            if (_renderingActive && _renderingState == state)
            {
                _renderingWritesMemory |= _nextDrawWritesMemory;
                _nextDrawWritesMemory = false;
                KeepCommittedTextureSets();
                return;
            }

            EndRendering();
            if (DeferGlobalBarriers && BatchBarriers)
            {
                // A guest cache flush inside this scope may be deferred to its end; this keeps
                // everything recorded before the scope ordered before its draws either way.
                RequestGuestMemoryBarrier(GlobalMemoryBarrier);
            }

            var command = BeginBatchedGuestCommands();
            _commandBuffer = command;
            if (DeferGlobalBarriers && !BatchBarriers)
            {
                _presenter.RecordGlobalBarrier(command);
            }

            _renderingWritesMemory = _nextDrawWritesMemory;
            _nextDrawWritesMemory = false;
            var colors = stackalloc RenderingAttachmentInfo[RenderingState.ColorAttachmentCapacity];
            for (var index = 0; index < state.ColorAttachmentCount; index++)
            {
                var attachment = state.ColorAttachments[index];
                colors[index] = new RenderingAttachmentInfo
                {
                    SType = StructureType.RenderingAttachmentInfo,
                    ImageView = attachment.View,
                    ImageLayout = ImageLayout.General,
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
            var depthClear = depthStencil.DepthClear;
            var stencilClear = depthStencil.StencilClear;
            if (_depthClearRecordedSeparately)
            {
                depthClear = false;
                stencilClear = false;
            }

            var depth = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = depthStencil.View,
                ImageLayout = ImageLayout.General,
                LoadOp = depthClear ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue { DepthStencil = new ClearDepthStencilValue(BitConverter.UInt32BitsToSingle(depthStencil.ClearWord0), 0) },
            };
            var stencil = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = depthStencil.View,
                ImageLayout = ImageLayout.General,
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
            if (_occlusionCounting && _worker)
                BeginWorkerQuery(command);
            else if (_occlusionCounting)
                _presenter._occlusionQueries?.Begin(command, _presenter._occlusionQueueId, Gpu.GpuCommands.VisibilityResultTrace.Enabled
                    ? _presenter.CommandStream.GetInterpreter(_presenter._occlusionQueueId).TypedRegisters.Context.DepthCountControl : 0);
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
            {
                _vk.CmdBeginRendering(command, &rendering);
            }
            _renderingScopesBegun++;
            _keptTextureSets[0] = null;
            _keptTextureSets[1] = null;
            if (GpuSyncProfile.Enabled)
            {
                GpuSyncProfile.CountRenderingScope();
            }

            _renderingActive = true;
            _renderingState = state;
            KeepCommittedTextureSets();
        }

        private void KeepCommittedTextureSets()
        {
            _keptTextureSets[0] = _committedTextureSets[0];
            _keptTextureSets[1] = _committedTextureSets[1];
            _committedTextureSets[0] = null;
            _committedTextureSets[1] = null;
            _keptTextureScope = DrawScope;
        }

        public void EndRendering()
        {
            if (!_renderingActive)
            {
                return;
            }

            _renderingActive = false;
            _renderingState = default;
            var command = new CommandBuffer(CurrentRecording().Handle);
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
            {
                _vk.CmdEndRendering(command);
            }
            if (_worker)
            {
                EndWorkerQuery(command);
            }
            else
            {
                _presenter._occlusionQueries?.End(command);
            }

            if (_barriersAfterRendering.Count != 0)
            {
                if (GpuSyncProfile.Enabled)
                {
                    BarrierTally.ScopeEnds.Increment();
                    BarrierTally.ScopeEndCalls.Increment();
                    BarrierTally.ScopeEndImages.Add(_barriersAfterRendering.Count);
                }

                fixed (ImageMemoryBarrier2* pointer = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_barriersAfterRendering))
                {
                    VulkanSynchronization.PipelineBarrier(_vk,
                        command, _afterRenderingSourceStages, _afterRenderingDestinationStages, DependencyFlags.ByRegionBit,
                        0, null, 0, null, (uint)_barriersAfterRendering.Count, pointer, BarrierSource.ScopeEnd);
                }

                _barriersAfterRendering.Clear();
                _afterRenderingSourceStages = 0;
                _afterRenderingDestinationStages = 0;
            }

            if (_globalBarrierAfterRendering)
            {
                _globalBarrierAfterRendering = false;
                if (BatchBarriers)
                {
                    RequestGuestMemoryBarrier(GlobalMemoryBarrier);
                }
                else
                {
                    _presenter.RecordGlobalBarrier(command);
                }
            }

            _renderingWritesMemory = false;
        }

        internal void EndRendering(ScopeBreak reason)
        {
            if (_renderingActive)
            {
                BarrierTally.CountScopeBreak(reason);
            }

            EndRendering();
        }

        void IRenderHost.EndRendering(ScopeBreak reason) => EndRendering(reason);

        void IRenderHost.PrepareMemoryWritingDraw()
        {
            // The store must not move ahead of a cache flush deferred inside this scope.
            if (_globalBarrierAfterRendering)
            {
                EndRendering(ScopeBreak.DeferredFlush);
            }

            _nextDrawWritesMemory = true;
            NoteShaderMayWriteMappedMemory();
        }

        private void NoteShaderMayWriteMappedMemory()
        {
            if (_bufferCache.HasMappedBuffers)
            {
                CurrentRecording().NoteMappedMemoryWrite();
            }
        }

        public void BindPipeline(PipelineBindPoint bindPoint, in PipelineHandle pipeline)
        {
            var entry = RequirePipelineEntry(in pipeline);
            var command = BeginBatchedGuestStateCommands();
            if (bindPoint == PipelineBindPoint.Graphics)
            {
                _boundGraphicsPipeline = entry;
                if (!entry.RectangleList)
                {
                    BindPipelineShadowed(command, bindPoint, entry.Pipeline);
                }

                return;
            }

            BindPipelineShadowed(command, bindPoint, entry.Pipeline);
            _profileComputePipeline = entry.Id;
        }

        private void CountDraw()
        {
            if (_preparation is { } preparation)
                preparation.CommandsRecorded = true;
            Interlocked.Increment(ref _perfDrawCount);
            PerfOverlay.RecordDraw();
            if (_worker)
            {
                _sliceDrawCount++;
                return;
            }

            if (_presenter._batchDrawCount++ == 0)
            {
                _presenter._batchOpenedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            }
        }

        // Existing strip compatibility path when native rectangle fill cannot be used.
        private static bool IsSingleRectangle(uint vertexCount) => vertexCount is 1 or 3 or 4;

        private void BindRectangleListVariant(RenderPipelineEntry entry, bool strip, CommandBuffer command)
        {
            Pipeline variant;
            lock (entry)
            {
                ref var slot = ref strip ? ref entry.StripVariant : ref entry.ListVariant;
                if (slot.Handle == 0)
                {
                    slot = _presenter.CreateRenderPipeline(entry.Description!, strip ? PrimitiveTopology.TriangleStrip : PrimitiveTopology.TriangleList, entry.Layout);
                }

                variant = slot;
            }

            BindPipelineShadowed(command, PipelineBindPoint.Graphics, variant);
        }

        private void BindNativeRectangleList(RenderPipelineEntry entry, CommandBuffer command)
        {
            Pipeline variant;
            lock (entry)
            {
                if (entry.RectangleVariant.Handle == 0)
                {
                    entry.RectangleVariant = _presenter.CreateRenderPipeline(entry.Description!, PrimitiveTopology.TriangleList,
                        entry.Layout, _presenter._supportsFillRectangle ? PolygonMode.FillRectangleNV : PolygonMode.Fill,
                        expandRectangles: !_presenter._supportsFillRectangle);
                }

                variant = entry.RectangleVariant;
            }

            BindPipelineShadowed(command, PipelineBindPoint.Graphics, variant);
        }

        // Rectangle assembly consumes groups of three and discards incomplete trailing groups.
        private bool CanDrawNativeRectangles => _presenter._supportsFillRectangle || _presenter._supportsRectangleGeometry;

        public void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawRecording);
            var command = BeginBatchedGuestCommands();
            var count = vertexCount;
            if (_boundGraphicsPipeline is { RectangleList: true } entry)
            {
                if (CanDrawNativeRectangles)
                {
                    BindNativeRectangleList(entry, command);
                }
                else
                {
                    var strip = IsSingleRectangle(vertexCount);
                    BindRectangleListVariant(entry, strip, command);
                    if (strip)
                    {
                        count = SingleRectangleVertexCount;
                    }
                }
            }

            _gpuCommandProfile?.WriteMarker(command, VulkanCommandProfile.IntervalKind.Preparation);
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
            {
                _vk.CmdDraw(command, count, instanceCount, firstVertex, firstInstance);
            }
            TraceOcclusionDraw();
            _gpuCommandProfile?.WriteMarker(command, VulkanCommandProfile.IntervalKind.Draw,
                _boundGraphicsPipeline?.Id ?? 0, count, instanceCount);
            CountDraw();
        }

        void IRenderHost.InsertDrawTraceMarker(string label)
        {
            var command = BeginBatchedGuestCommands();
            _presenter.InsertDebugLabel(command, label);
        }

        void IRenderHost.DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset, uint firstInstance)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawRecording);
            var command = BeginBatchedGuestCommands();
            if (_boundGraphicsPipeline is { RectangleList: true } entry)
            {
                if (CanDrawNativeRectangles)
                {
                    BindNativeRectangleList(entry, command);
                }
                else
                {
                    BindRectangleListVariant(entry, strip: false, command);
                }
            }

            _gpuCommandProfile?.WriteMarker(command, VulkanCommandProfile.IntervalKind.Preparation);
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
            {
                _vk.CmdDrawIndexed(command, indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);
            }
            TraceOcclusionDraw(indexCount);
            _gpuCommandProfile?.WriteMarker(command, VulkanCommandProfile.IntervalKind.DrawIndexed,
                _boundGraphicsPipeline?.Id ?? 0, indexCount, instanceCount);
            CountDraw();
        }

        void IRenderHost.DrawIndexedIndirect(BufferBinding arguments)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawRecording);
            var command = BeginBatchedGuestCommands();
            if (_boundGraphicsPipeline is { RectangleList: true } entry)
            {
                if (CanDrawNativeRectangles)
                {
                    BindNativeRectangleList(entry, command);
                }
                else
                {
                    BindRectangleListVariant(entry, strip: false, command);
                }
            }

            _gpuCommandProfile?.WriteMarker(command, VulkanCommandProfile.IntervalKind.Preparation);
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
            {
                _vk.CmdDrawIndexedIndirect(command, new VkBuffer(arguments.Handle), arguments.Offset, 1, 20);
            }
            TraceOcclusionDraw();
            _gpuCommandProfile?.WriteMarker(command, VulkanCommandProfile.IntervalKind.DrawIndexed,
                _boundGraphicsPipeline?.Id ?? 0, 0, 0);
            CountDraw();
        }

        void IRenderHost.DrawIndirect(BufferBinding arguments)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawRecording);
            var command = BeginBatchedGuestCommands();
            if (_boundGraphicsPipeline is { RectangleList: true } entry)
            {
                if (CanDrawNativeRectangles)
                {
                    BindNativeRectangleList(entry, command);
                }
                else
                {
                    BindRectangleListVariant(entry, strip: false, command);
                }
            }

            _gpuCommandProfile?.WriteMarker(command, VulkanCommandProfile.IntervalKind.Preparation);
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
            {
                _vk.CmdDrawIndirect(command, new VkBuffer(arguments.Handle), arguments.Offset, 1, 16);
            }
            TraceOcclusionDraw();
            _gpuCommandProfile?.WriteMarker(command, VulkanCommandProfile.IntervalKind.Draw,
                _boundGraphicsPipeline?.Id ?? 0, 0, 0);
            CountDraw();
        }

        void IRenderHost.DrawMeshTasks(uint groupCountX, uint groupCountY, uint groupCountZ)
        {
            if (_presenter._cmdDrawMeshTasks is null)
            {
                throw SubmissionScheduler.Fatal("The Vulkan device has no mesh shader draw command.");
            }

            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawRecording);
            var command = BeginBatchedGuestCommands();
            _gpuCommandProfile?.WriteMarker(command, VulkanCommandProfile.IntervalKind.Preparation);
            _presenter._cmdDrawMeshTasks(command, groupCountX, groupCountY, groupCountZ);
            TraceOcclusionDraw();
            if (MeshDrawTrace.Active) MeshDrawTrace.RecordedCommand((ulong)command.Handle);
            _gpuCommandProfile?.WriteMarker(command, VulkanCommandProfile.IntervalKind.MeshDraw,
                _boundGraphicsPipeline?.Id ?? 0, groupCountX, groupCountY, groupCountZ,
                _profileMeshDrawState with { Width = _renderingState.Width, Height = _renderingState.Height });
            CountDraw();
        }

        public void Dispatch(uint groupsX, uint groupsY, uint groupsZ)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawRecording);
            var command = BeginBatchedGuestCommands();
            _gpuCommandProfile?.WriteMarker(command, VulkanCommandProfile.IntervalKind.Preparation);
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
            {
                _vk.CmdDispatch(command, groupsX, groupsY, groupsZ);
            }
            _gpuCommandProfile?.WriteMarker(command, VulkanCommandProfile.IntervalKind.Dispatch,
                _profileComputePipeline, groupsX, groupsY, groupsZ);
            CountDraw();
            NoteShaderMayWriteMappedMemory();
        }

        public bool TryDispatchIndirect(ulong argumentsAddress, ReadOnlySpan<int> physicalAxisOfLogical)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawRecording);
            var permuted = physicalAxisOfLogical is not [0, 1, 2];
            if (permuted && _worker)
            {
                throw Serial("permuted indirect dispatch");
            }

            var (obtainedBuffer, offset) = _worker
                ? ObtainBufferForSlice(argumentsAddress, 3u * sizeof(uint), isWritten: false, isTexelBuffer: false)
                : _bufferCache.ObtainBuffer(argumentsAddress, 3u * sizeof(uint), false);
            var buffer = obtainedBuffer.Handle;
            var permutedArguments = permuted ? UploadTransient(stackalloc byte[3 * sizeof(uint)], 16) : default;
            var command = BeginBatchedGuestCommands();
            var barrier = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcAccessMask = AccessFlags2.ShaderWriteBit | AccessFlags2.TransferWriteBit | AccessFlags2.MemoryWriteBit,
                DstAccessMask = permuted ? AccessFlags2.TransferReadBit : AccessFlags2.IndirectCommandReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = buffer,
                Offset = offset,
                Size = 3u * sizeof(uint),
            };
            VulkanSynchronization.PipelineBarrier(_vk,command, PipelineStageFlags.AllCommandsBit,
                permuted ? PipelineStageFlags.TransferBit : PipelineStageFlags.DrawIndirectBit, 0, 0, null, 1, &barrier, 0, null, BarrierSource.IndirectDispatch);
            if (permuted)
            {
                var permutedBuffer = new VkBuffer(permutedArguments.Handle);
                var regions = stackalloc BufferCopy[3];
                for (var axis = 0; axis < 3; axis++)
                {
                    regions[axis] = new BufferCopy(offset + (ulong)axis * sizeof(uint),
                        permutedArguments.Offset + (ulong)physicalAxisOfLogical[axis] * sizeof(uint), sizeof(uint));
                }

                _vk.CmdCopyBuffer(command, buffer, permutedBuffer, 3, regions);
                barrier.SrcAccessMask = AccessFlags2.TransferWriteBit;
                barrier.DstAccessMask = AccessFlags2.IndirectCommandReadBit;
                barrier.Buffer = permutedBuffer;
                barrier.Offset = permutedArguments.Offset;
                VulkanSynchronization.PipelineBarrier(_vk, command, PipelineStageFlags.TransferBit, PipelineStageFlags.DrawIndirectBit,
                    0, 0, null, 1, &barrier, 0, null, BarrierSource.IndirectDispatch);
                buffer = permutedBuffer;
                offset = permutedArguments.Offset;
            }

            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
            {
                _vk.CmdDispatchIndirect(command, buffer, offset);
            }
            CountDraw();
            NoteShaderMayWriteMappedMemory();
            return true;
        }

        internal static readonly bool BatchBarriers =
            Environment.GetEnvironmentVariable("SHARPEMU_BATCH_BARRIERS") != "0";

        internal static readonly MemoryBarrierRequest GlobalMemoryBarrier = new(
            PipelineStageFlags.AllCommandsBit,
            PipelineStageFlags.AllCommandsBit,
            AccessFlags.MemoryWriteBit,
            AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);

        private void RecordMemoryBarrier(PipelineStageFlags sourceStages, PipelineStageFlags destinationStages, AccessFlags sourceAccess, AccessFlags destinationAccess)
        {
            EndRendering();
            if (BatchBarriers)
            {
                RequestGuestMemoryBarrier(new MemoryBarrierRequest(sourceStages, destinationStages, sourceAccess, destinationAccess));
                return;
            }

            var command = BeginBatchedGuestCommands();
            var barrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcAccessMask = VulkanSynchronization.Access(sourceAccess),
                DstAccessMask = VulkanSynchronization.Access(destinationAccess),
            };
            VulkanSynchronization.PipelineBarrier(_vk,command, sourceStages, destinationStages, 0, 1, &barrier, 0, null, 0, null, BarrierSource.Requested);
        }

        public void ShaderWriteBarrier(PipelineStageFlags sourceStages)
        {
            NoteShaderMayWriteMappedMemory();
            RecordMemoryBarrier(
                sourceStages,
                PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.VertexInputBit | PipelineStageFlags.VertexShaderBit |
                PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit | PipelineStageFlags.ColorAttachmentOutputBit |
                (_presenter._supportsMeshShader ? PipelineStageFlags.MeshShaderBitExt : PipelineStageFlags.None),
                AccessFlags.ShaderWriteBit,
                AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit | AccessFlags.VertexAttributeReadBit | AccessFlags.IndexReadBit |
                AccessFlags.UniformReadBit | AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit |
                AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit);
        }

        private static readonly MemoryBarrierRequest ShaderWriteHazardRequest = new(
            PipelineStageFlags.AllCommandsBit,
            PipelineStageFlags.ComputeShaderBit,
            AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
            AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);

        public void ShaderWriteHazardBarrier()
        {
            if (!BatchBarriers)
            {
                RecordMemoryBarrier(
                    PipelineStageFlags.AllCommandsBit,
                    PipelineStageFlags.ComputeShaderBit,
                    AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                    AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
                return;
            }

            EndRendering();
            RequestGuestMemoryBarrier(ShaderWriteHazardRequest);
        }

        public void ShaderAccessBarrier() =>
            RecordMemoryBarrier(
                PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.AllCommandsBit,
                AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);

        private static readonly MemoryBarrierRequest ShaderAccessRequest = new(
            PipelineStageFlags.ComputeShaderBit,
            PipelineStageFlags.AllCommandsBit,
            AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);

        public bool TryDeferShaderAccessBarrier()
        {
            EndRendering();
            if (!BatchBarriers)
            {
                ShaderAccessBarrier();
                return false;
            }

            var recording = CurrentRecording();
            if (!_worker)
            {
                _presenter.OpenGuestBatch();
            }

            _scheduler.MemoryBarrierRecorder ??= _presenter.RecordRequestedMemoryBarrier;
            recording.DeferMemoryBarrier(ShaderAccessRequest);
            return true;
        }

        public void BeginCheckedDispatch() => CurrentRecording().EnterCheckedWindow();

        public void EndCheckedDispatch() => CurrentRecording().ExitCheckedWindow();

        public long DeferredBarrierPromotions => CurrentRecording().DeferredPromotions;

        // Clears the bound targets on the GPU in place of the draw; the store owns the result.
        public void ClearColorTargets(ReadOnlySpan<ColorTargetState> targets, SolidColorClear clear)
        {
            if (_worker)
            {
                throw Serial("clear color targets");
            }

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
                TransitionImage(attachment.Image, AccessFlags.TransferWriteBit, range, command);
                var vkRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, view.BaseLevel, view.LevelCount, view.BaseLayer, view.LayerCount);
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                {
                    _vk.CmdClearColorImage(command, attachment.Image.Backing.Handle, ImageLayout.General, &clearValue, 1, &vkRange);
                }
            }

            CountDraw();
            if (Presenter._traceVulkanShaderEnabled)
            {
                Presenter.TraceVulkanShader($"vk.offscreen_color_clear mrt={targets.Length} rgba=({clear.Red:0.###},{clear.Green:0.###},{clear.Blue:0.###},{clear.Alpha:0.###})");
            }
        }

        public bool TryRetainTargetlessDraw(RegisterBanks banks, GraphicsPrograms programs, in TargetlessDrawArguments arguments)
        {
            _ = programs;
            return _presenter._translation.TryRetainTargetlessDraw(banks, in arguments);
        }

        public void MarkGpuWritten(ResourceSlotIdentifier image)
        {
            if (_worker)
            {
                throw Serial("mark gpu written");
            }

            _imageCache.MarkGpuWritten(image);
        }

        public void NoteKnownGpuContents(ulong address, ReadOnlySpan<uint> words)
        {
            if (_worker)
            {
                Requests.KnownWrites.Add((address, words.ToArray()));
                return;
            }

            _bufferCache.NoteKnownGpuContents(address, words);
        }

        public void ResolveImage(ResourceSlotIdentifier source, uint sourceMip, uint sourceLayer, ResourceSlotIdentifier destination, uint destinationMip, uint destinationLayer)
        {
            if (_worker)
            {
                throw Serial("resolve image");
            }

            EndRendering();
            var sourceImage = _imageCache.GetImage(source);
            var destinationImage = _imageCache.GetImage(destination);
            destinationImage.ResolveFrom(
                sourceImage,
                new SubresourceRange(sourceMip, 1, sourceLayer, 1),
                new SubresourceRange(destinationMip, 1, destinationLayer, 1));
            EnsureBatchedGuestCommands();
        }

        public void CopyDepthStencilImage(ResourceSlotIdentifier source, ResourceSlotIdentifier destination, in SubresourceRange range, in Extent3D extent, ImageAspectFlags aspects)
        {
            if (_worker)
            {
                throw Serial("depth copy");
            }

            EndRendering();
            _imageCache.RefreshImage(source);
            _imageCache.RefreshImage(destination);
            var sourceImage = _imageCache.GetImage(source);
            var destinationImage = _imageCache.GetImage(destination);
            destinationImage.CopyDepthStencilFrom(sourceImage, in range, in extent, aspects);
            EnsureBatchedGuestCommands();
        }

        public bool IsMetadata(ulong address) => _imageCache.IsMetadata(address);

        public bool ClearMetadata(ulong address)
        {
            if (_worker)
            {
                return _imageCache.PeekSurfaceMetadataKind(address) is null or SurfaceMetadataKind.PendingDcc or SurfaceMetadataKind.Dcc ? false : throw Serial("metadata clear");
            }

            EnsureBatchedGuestCommands();
            return _imageCache.ClearMetadata(address);
        }

        public bool TryClearImageFromBuffer(ulong address, ulong size, uint packedClear)
        {
            if (_worker)
            {
                return _imageCache.WouldClearImageFromBuffer(address, size, packedClear) ? throw Serial("image clear") : false;
            }

            EnsureBatchedGuestCommands();
            return _imageCache.TryClearImageFromBuffer(address, size, packedClear);
        }

        public bool TryAbsorbDccFill(ulong address, ulong size, uint fillValue)
        {
            if (_worker)
            {
                return _imageCache.PeekSurfaceMetadataKind(address) is null or SurfaceMetadataKind.PendingDcc or SurfaceMetadataKind.Dcc ? throw Serial("dcc fill") : false;
            }

            return _imageCache.TryAbsorbDccFill(address, size, fillValue);
        }

        public bool TryFillDccMetadata(ulong address, ulong size, uint fillValue)
        {
            if (!_imageCache.OverlapsDccMetadata(address, size) && !IsMetadataShapedFill(address, size, fillValue))
            {
                return false;
            }

            if (_worker)
            {
                throw Serial("metadata fill");
            }

            _bufferCache.FillDccMetadata(address, size, fillValue);
            return true;
        }

        private bool IsMetadataShapedFill(ulong address, ulong size, uint fillValue)
        {
            const ulong metadataBlockSize = 0x1000;
            const ulong maxMetadataFill = 0x100000;
            return address != 0 && size != 0 && size <= maxMetadataFill &&
                   address % metadataBlockSize == 0 && size % metadataBlockSize == 0 &&
                   fillValue == (fillValue & 0xFF) * 0x01010101u &&
                   !_imageCache.QueryRegion(address, size).ImageBytes;
        }

        public bool TryCopyWordsOnHost(ulong destination, ulong source, ulong sourceWords, ulong words)
        {
            if (_worker)
            {
                return _bufferCache.WouldCopyWordsOnHost(destination, source, sourceWords, words) ? throw Serial("host copy", resumable: true) : false;
            }

            return _bufferCache.TryCopyWordsOnHost(destination, source, sourceWords, words);
        }
    }
}
