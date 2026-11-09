// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Collections.Concurrent;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private RecordingHost? _recording;

        private RecordingHost Recording => _recording ??= new RecordingHost(this);

        IRenderHost? ICommandStreamHost.RenderHost => Recording;
    }

    private sealed partial class RecordingHost
    {
        private readonly Presenter _presenter;
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly GpuDeviceInfo _deviceInfo;
        private readonly SubmissionScheduler _scheduler;
        private readonly GuestBufferCache _bufferCache;
        private readonly GuestImageCache _imageCache;
        private readonly SamplerStore _samplerStore;
        private readonly ICpuMemory _guestMemory;
        private readonly IGuestBackedSpace _guestBacking;
        private readonly DescriptorHeap _descriptorHeap;
        private readonly ContentUploadArena _contentUploads;
        private readonly VulkanCommandProfile? _gpuCommandProfile;
        private readonly RenderHostLimits _renderHostLimits;
        private Silk.NET.Vulkan.Buffer _nullBuffer;
        private CommandBuffer _commandBuffer;

        public RecordingHost(Presenter presenter, bool worker = false)
        {
            _presenter = presenter;
            _worker = worker;
            _vk = presenter._vk;
            _device = presenter._device;
            _deviceInfo = presenter._deviceInfo;
            _scheduler = presenter._scheduler;
            _bufferCache = presenter._bufferCache;
            _imageCache = presenter._imageCache;
            _samplerStore = presenter._samplerStore;
            _guestMemory = presenter._guestMemory;
            _guestBacking = presenter._guestBacking;
            _descriptorHeap = presenter._descriptorHeap;
            _contentUploads = presenter._contentUploads;
            _gpuCommandProfile = worker ? null : presenter._gpuCommandProfile;
            _renderHostLimits = presenter._renderHostLimits;
            if (worker)
            {
                _spareBuffers = new ConcurrentBag<RecordingBuffer>();
                _arena = new SliceArena(presenter._arenaPool!, _deviceInfo.NonCoherentAtomSize);
                _hazards = new SliceHazards();
                _workerRecorder = presenter._translation.CreateWorkerRecorder(this);
            }
        }

        public bool TryReadGuest(ulong address, Span<byte> destination) => ((ICommandStreamHost)_presenter).TryReadGuest(address, destination);

        public Exception Fatal(string message) => ((ICommandStreamHost)_presenter).Fatal(message);

        public void TraceDrawTarget(string disposition, ulong address, ulong pixelShaderAddress, ulong vertexShaderAddress) =>
            _presenter.TraceDrawTarget(disposition, address, pixelShaderAddress, vertexShaderAddress);

        public void TraceVertexBuffer(ulong shaderHash, VertexAttributeResource attribute, VertexInputBuffer input,
            BufferBinding binding, ulong requestedBytes, ulong acquiredBytes) =>
            _presenter.TraceVertexBuffer(shaderHash, attribute, input, binding, requestedBytes, acquiredBytes);

        public bool TryDeferUntilRenderingEnds(PipelineStageFlags sourceStages, PipelineStageFlags destinationStages, List<ImageMemoryBarrier2> barriers)
        {
            if (!_renderingActive)
            {
                return false;
            }

            _afterRenderingSourceStages |= sourceStages;
            _afterRenderingDestinationStages |= destinationStages;
            foreach (var barrier in barriers)
            {
                MergeBarrierAfterRendering(in barrier);
            }

            return true;
        }

        private void MergeBarrierAfterRendering(in ImageMemoryBarrier2 barrier)
        {
            var pending = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_barriersAfterRendering);
            for (var index = 0; index < pending.Length; index++)
            {
                ref var merged = ref pending[index];
                if (merged.Image.Handle == barrier.Image.Handle && SameSubresources(in merged.SubresourceRange, in barrier.SubresourceRange))
                {
                    merged.SrcAccessMask |= barrier.SrcAccessMask;
                    merged.DstAccessMask |= barrier.DstAccessMask;
                    merged.NewLayout = barrier.NewLayout;
                    return;
                }
            }

            _barriersAfterRendering.Add(barrier);
        }

        private static bool SameSubresources(in ImageSubresourceRange left, in ImageSubresourceRange right) =>
            left.AspectMask == right.AspectMask && left.BaseMipLevel == right.BaseMipLevel && left.LevelCount == right.LevelCount &&
            left.BaseArrayLayer == right.BaseArrayLayer && left.LayerCount == right.LayerCount;

        private CommandBuffer BeginBatchedGuestCommands() => _worker ? new CommandBuffer(_workerBuffer!.Handle) : _presenter.BeginBatchedGuestCommands();

        private void EnsureBatchedGuestCommands()
        {
            if (!_worker)
            {
                _presenter.EnsureBatchedGuestCommands();
            }
        }

        private CommandBuffer BeginBatchedGuestStateCommands() => _worker ? new CommandBuffer(_workerBuffer!.StateHandle) : _presenter.BeginBatchedGuestStateCommands();

        private CommandBuffer BeginBatchedGuestTransferCommands() => _worker ? new CommandBuffer(_workerBuffer!.TransferHandle) : _presenter.BeginBatchedGuestTransferCommands();

        private void RequestGuestMemoryBarrier(in MemoryBarrierRequest request)
        {
            if (_worker)
            {
                _workerBuffer!.RequestMemoryBarrier(in request);
            }
            else
            {
                _presenter.RequestGuestMemoryBarrier(in request);
            }
        }

        private ulong FlushBatchedGuestCommands() => _worker ? throw Serial("flush", resumable: true) : _presenter.FlushBatchedGuestCommands();

        private void EnsureGuestSubmissionCapacity(bool collectNow = false)
        {
            if (!_worker)
            {
                _presenter.EnsureGuestSubmissionCapacity(collectNow);
            }
        }

        private RecordingBuffer CurrentRecording() => _worker ? _workerBuffer! : _presenter.CurrentRecording();

        private Silk.NET.Vulkan.Buffer NullBufferHandle
        {
            get
            {
                if (_nullBuffer.Handle == 0)
                {
                    _nullBuffer = _bufferCache.GetBuffer(GuestBufferCache.NullBufferId).Handle;
                }

                return _nullBuffer;
            }
        }

        private CommandBuffer CurrentRecordingBuffer() => _worker ? new CommandBuffer(_workerBuffer!.Handle) : _presenter.CurrentRecordingBuffer();
    }
}
