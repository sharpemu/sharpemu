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
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
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
    private const uint SingleRectangleVertexCount = 4;

    private static void RequireRenderingFeature(bool supported, string extension, string deviceName)
    {
        if (!supported)
        {
            throw SubmissionScheduler.Fatal($"The device lacks a required rendering feature: device={deviceName} extension={extension}.");
        }
    }

    private sealed partial class Presenter : IRenderHost
    {
        // Keep each new stream allocation intact until the draw is recorded.
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

        private readonly Dictionary<ulong, RenderPipelineEntry> _pipelineEntries = new();
        private ulong _nextPipelineId;
        private RenderPreparation? _preparation;
        private RenderPipelineEntry? _boundGraphicsPipeline;
        private ulong _profileComputePipeline;

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
            => ClampMappedSize(address, size, null, -1);

        private ulong ClampMappedSize(ulong address, ulong size, PreparedStageBindings? prepared, int bufferIndex)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BufferMappedRangeValidation);
            if (address == 0 || size == 0 || size > ulong.MaxValue - address || !_guestMemory.CanRead(address, 1))
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

            if (_guestMemory.CanRead(address, size))
            {
                return size;
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

        public void BindVertexBuffers(ReadOnlySpan<BufferBinding> bindings, VertexInputInfo input)
        {
            if (bindings.IsEmpty) return;
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
            _preparation = new RenderPreparation(this, _bufferCache.GetUtilityBuffer(GpuBufferUsage.Stream).RetainUpcomingAllocations());
            return _preparation;
        }

        private RenderPreparation RequirePreparation() =>
            _preparation ?? throw SubmissionScheduler.Fatal("The resource preparation is not open.");

        private RenderPipelineEntry RequirePipelineEntry(in PipelineHandle pipeline) =>
            _pipelineEntries.TryGetValue(pipeline.Pipeline, out var entry)
                ? entry
                : throw SubmissionScheduler.Fatal($"The pipeline handle is unknown: pipeline={pipeline.Pipeline} layout={pipeline.Layout}.");


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
            _profileComputePipeline = entry.Id;
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
                variant = CreateRenderPipeline(entry.Description!, strip ? PrimitiveTopology.TriangleStrip : PrimitiveTopology.TriangleList, entry.Layout);
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

            _gpuCommandProfile?.WriteMarker(command, VulkanCommandProfile.IntervalKind.Preparation);
            _vk.CmdDraw(command, count, instanceCount, firstVertex, firstInstance);
            _gpuCommandProfile?.WriteMarker(command, VulkanCommandProfile.IntervalKind.Draw,
                _boundGraphicsPipeline?.Id ?? 0, count, instanceCount);
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

            _gpuCommandProfile?.WriteMarker(command, VulkanCommandProfile.IntervalKind.Preparation);
            _vk.CmdDrawIndexed(command, indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);
            _gpuCommandProfile?.WriteMarker(command, VulkanCommandProfile.IntervalKind.DrawIndexed,
                _boundGraphicsPipeline?.Id ?? 0, indexCount, instanceCount);
            CountDraw();
        }

        public void Dispatch(uint groupsX, uint groupsY, uint groupsZ)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawRecording);
            var command = BeginBatchedGuestCommands();
            _gpuCommandProfile?.WriteMarker(command, VulkanCommandProfile.IntervalKind.Preparation);
            _vk.CmdDispatch(command, groupsX, groupsY, groupsZ);
            _gpuCommandProfile?.WriteMarker(command, VulkanCommandProfile.IntervalKind.Dispatch,
                _profileComputePipeline, groupsX, groupsY, groupsZ);
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
    }
}
