// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.VideoOut;

internal static unsafe partial class VulkanVideoPresenter
{
    internal static void PrepareCachedBufferAllocations(
        GuestBufferCache cache, IReadOnlyList<GuestMemoryBuffer> buffers,
        IReadOnlyList<GuestVertexBuffer>? vertices = null, GuestIndexBuffer? indices = null)
    {
        // Complete overlapping allocation merges before any descriptor takes a handle.
        foreach (var buffer in buffers)
        {
            if (buffer.BaseAddress != 0 && buffer.WriteBackToGuest && buffer.Length == 0)
            {
                var (address, size) = GlobalBufferRange(buffer);
                _ = cache.FindBuffer(address, size);
            }
        }

        if (vertices is not null)
        {
            foreach (var vertex in vertices)
            {
                if (vertex.BaseAddress == 0 || vertex.Length <= 0) continue;
                var size = (ulong)vertex.Length;
                var needsCache = cache.HasGpuDirtyPages(vertex.BaseAddress, size);
                foreach (var global in buffers)
                {
                    if (!global.Writable || !global.WriteBackToGuest || global.Length != 0 || global.BaseAddress == 0)
                        continue;
                    var (address, bytes) = GlobalBufferRange(global);
                    var pageMask = GuestBufferCache.CachingPageSize - 1;
                    var begin = address & ~pageMask;
                    var end = (address + bytes + pageMask) & ~pageMask;
                    needsCache |= vertex.BaseAddress < end && begin < vertex.BaseAddress + size;
                }
                if (needsCache) _ = cache.FindBuffer(vertex.BaseAddress, size);
            }
        }

        if (indices is { GuestAddress: not 0, Length: > 0 })
            _ = cache.FindBuffer(indices.GuestAddress, (ulong)indices.Length);
    }

    private static (ulong Address, ulong Size) GlobalBufferRange(GuestMemoryBuffer buffer)
    {
        var bias = buffer.BaseAddress & (GuestStorageBufferOffsetAlignment - 1);
        return (buffer.BaseAddress - bias, Math.Max(buffer.Size, sizeof(uint)) + bias);
    }

    // This partial binds guest global memory through the buffer store.

    private sealed partial class Presenter
    {
        private readonly HashSet<(ulong Address, ulong Size)> _tracedGlobalBuffers = new();

        private sealed class GlobalBufferResource
        {
            public ulong BaseAddress;
            public bool Writable;
            public VkBuffer Buffer;
            public DeviceMemory Memory;
            public IDisposable? StreamRetention;
            // Host-owned storage is recycled with the draw; store buffers belong to the cache.
            public bool OwnsBuffer;
            // Offset/Size include the shader-visible byte bias.
            public ulong Offset;
            public ulong Size;
        }

        // A buffer that arrives with bytes is host-owned; guest memory is obtained from the store.
        private GlobalBufferResource CreateGlobalBufferResource(GuestMemoryBuffer guestBuffer, bool formatted = false)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BufferResources);
            if (guestBuffer.BaseAddress == 0 || !guestBuffer.WriteBackToGuest || guestBuffer.Length != 0)
            {
                return CreateTransientGlobalBufferResource(guestBuffer);
            }

            var (start, size) = GlobalBufferRange(guestBuffer);
            var (buffer, offset) = _bufferCache.ObtainBuffer(start, size, guestBuffer.Writable, isTexelBuffer: formatted);
            if (formatted && guestBuffer.Writable)
            {
                _imageCache.InvalidateMemoryForBoundWrite(guestBuffer.BaseAddress, guestBuffer.Size);
            }
            if (offset % _minStorageBufferOffsetAlignment != 0)
            {
                throw SubmissionScheduler.Fatal(
                    $"guest buffer offset 0x{offset:X} is not aligned to Vulkan's " +
                    $"minStorageBufferOffsetAlignment={_minStorageBufferOffsetAlignment}");
            }

            if (ShouldTraceVulkanResources() &&
                _tracedGlobalBuffers.Add((guestBuffer.BaseAddress, guestBuffer.Size)))
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.global_buffer base=0x{guestBuffer.BaseAddress:X16} " +
                    $"bytes={guestBuffer.Size}");
            }

            return new GlobalBufferResource
            {
                BaseAddress = guestBuffer.BaseAddress,
                Writable = guestBuffer.Writable,
                Buffer = buffer.Handle,
                StreamRetention = (buffer as GpuRingBuffer)?.RetainContents(),
                OwnsBuffer = false,
                Offset = offset,
                Size = checked((size + 3) & ~3UL),
            };
        }

        private GlobalBufferResource CreateTransientGlobalBufferResource(GuestMemoryBuffer guestBuffer)
        {
            var bytes = guestBuffer.Length != 0
                ? guestBuffer.Data.AsSpan(0, guestBuffer.Length)
                : new byte[checked((int)Math.Max(guestBuffer.Size, sizeof(uint)))];
            var buffer = CreateHostBuffer(
                bytes,
                BufferUsageFlags.StorageBufferBit,
                out var memory,
                out _);
            if (guestBuffer.Pooled)
            {
                GuestDataPool.Shared.Return(guestBuffer.Data);
            }

            return new GlobalBufferResource
            {
                BaseAddress = guestBuffer.BaseAddress,
                Writable = guestBuffer.Writable && guestBuffer.BaseAddress != 0,
                Buffer = buffer,
                Memory = memory,
                OwnsBuffer = true,
                Offset = 0,
                Size = (ulong)Math.Max(bytes.Length, sizeof(uint)),
            };
        }
    }
}
