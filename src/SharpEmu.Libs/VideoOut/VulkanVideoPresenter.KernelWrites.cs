// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu.Pipelines;

internal static partial class VulkanVideoPresenter
{
    private sealed partial class Presenter : IKernelWriteStream
    {
        public KernelWriteRefusal TryPostKernelWrite(ulong address, ReadOnlySpan<uint> words) => KernelWritePolicy.TryPost(this, address, words);

        bool IKernelWriteStream.OnStreamThread => OnCommandThread && !OnResolveWorker;

        bool IKernelWriteStream.InCrossQueueOrderWindow => InAnyCrossQueueOrderWindow();

        bool IKernelWriteStream.HasPendingPublication(ulong address, ulong size) => HasPendingMemoryPublication(address, size);

        bool IKernelWriteStream.HasPendingRange(ulong address, ulong size) => _commandThread!.HasPendingRange(address, size);

        bool IKernelWriteStream.HasPendingGpuWrite(ulong address, ulong size) => IsPendingOnCommandThread(address, size);

        bool IKernelWriteStream.HasImageBytes(ulong address, ulong size) => _bufferCache.MayHaveImageBytes(address, size);

        bool IKernelWriteStream.HasSurfaceMetadata(ulong address, ulong size) => _imageCache.MayOverlapMetadata(address, size);

        void IKernelWriteStream.PostWrite(ulong address, ReadOnlySpan<byte> data) => _commandThread!.PostWrite(address, data, kernel: true);

        public bool TryPostWordCopy(ulong destination, ulong source, ulong sourceWords, ulong words)
        {
            var refusal = PostWordCopy(destination, source, sourceWords, words);
            ShaderPipelineCache.NoteWordCopy(refusal, words * sizeof(uint));
            return refusal == KernelWriteRefusal.None;
        }

        private KernelWriteRefusal PostWordCopy(ulong destination, ulong source, ulong sourceWords, ulong words)
        {
            if (destination == 0 || source == 0 || sourceWords == 0 || words == 0 || ((destination | source) & 3) != 0 ||
                words > Gpu.Buffers.GuestBufferCache.MaxHostCopyWords || sourceWords > Gpu.Buffers.GuestBufferCache.MaxHostCopyWords)
            {
                return KernelWriteRefusal.Unsupported;
            }

            var size = words * sizeof(uint);
            var sourceSize = Math.Min(sourceWords, words) * sizeof(uint);
            if (size > ulong.MaxValue - destination || sourceSize > ulong.MaxValue - source ||
                (source < destination + size && destination < source + sourceSize))
            {
                return KernelWriteRefusal.Unsupported;
            }

            var refusal = KernelWritePolicy.Check(this, destination, size);
            if (refusal == KernelWriteRefusal.Image && !_imageCache.QueryRegion(destination, size).ImageBytes)
            {
                refusal = _imageCache.MayOverlapMetadata(destination, size) ? KernelWriteRefusal.Metadata : KernelWriteRefusal.None;
            }

            if (refusal != KernelWriteRefusal.None)
            {
                return refusal;
            }

            var data = GC.AllocateUninitializedArray<byte>((int)size);
            if (!TryPeekResidentGuestBytes(source, data.AsSpan(0, (int)sourceSize)))
            {
                if (RenderPhaseProfile.Enabled)
                {
                    ShaderPipelineCache.NoteWordCopyInput(
                        HasPendingMemoryPublication(source, sourceSize) ? 0
                        : !_guestMemory.CanRead(source, sourceSize) ? 1
                        : _commandThread!.HasPendingRange(source, sourceSize) ? 2
                        : IsPendingOnCommandThread(source, sourceSize) ? 3
                        : _bufferCache.HasGpuDirtyBytes(source, sourceSize) ? 4
                        : 5);
                }

                return KernelWriteRefusal.Input;
            }

            for (var filled = (int)sourceSize; filled < data.Length;)
            {
                var count = Math.Min(filled, data.Length - filled);
                data.AsSpan(0, count).CopyTo(data.AsSpan(filled));
                filled += count;
            }

            _commandThread!.PostOwnedWrite(destination, data, kernel: true);
            return KernelWriteRefusal.None;
        }

        // On the render thread at the store's stream position: a store into GPU-dirty pages lands on both copies.
        internal bool TryApplyKernelWriteOverGpuBytes(ulong address, byte[] data)
        {
            var size = (ulong)data.Length;
            if (!_bufferCache.MayHaveGpuDirtyPages(address, size) || !_bufferCache.HasGpuDirtyPages(address, size) ||
                !_bufferCache.TryWriteOverGpuBytes(address, data))
            {
                ShaderPipelineCache.KernelWritesToCpu.Increment();
                return false;
            }

            ShaderPipelineCache.KernelWritesOverGpu.Increment();
            ShaderPipelineCache.KernelWriteBytesOverGpu.Add(data.Length);
            return true;
        }
    }
}
