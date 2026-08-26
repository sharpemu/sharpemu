// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.VideoOut;

internal static unsafe partial class VulkanVideoPresenter
{
    // This partial manages Vulkan buffers that mirror guest global memory.

    private sealed partial class Presenter
    {

        private readonly HashSet<(ulong Address, int Size)> _tracedGlobalBuffers = new();

        private readonly List<GuestBufferAllocation> _guestBufferAllocations = [];

        private readonly record struct DirtyGuestBufferRange(
            ulong Offset,
            ulong Length,
            string QueueName,
            ulong Timeline);

        private sealed class GuestBufferAllocation
        {
            public ulong BaseAddress;
            public ulong Size;
            public VkBuffer Buffer;
            public DeviceMemory Memory;
            public nint Mapped;
            public byte[] Shadow = [];
            public ulong LastUseTimeline;
            public List<DirtyGuestBufferRange> DirtyRanges { get; } = [];
        }

        private sealed class GlobalBufferResource
        {
            public ulong BaseAddress;
            public bool Writable;
            public bool WriteBackToGuest;
            public VkBuffer Buffer;
            public DeviceMemory Memory;
            public nint Mapped;
            // DescriptorOffset/Size include the shader-visible byte bias.
            public ulong Offset;
            public ulong Size;
            // GuestOffset/Size identify only the original guest resource and
            // are used for dirty writeback; descriptor padding must not be
            // published over unrelated guest bytes.
            public ulong GuestOffset;
            public ulong GuestSize;
            public GuestBufferAllocation? Allocation;
        }

        private GlobalBufferResource CreateGlobalBufferResource(
            GuestMemoryBuffer guestBuffer)
        {
            if (guestBuffer.BaseAddress == 0)
            {
                return CreateTransientGlobalBufferResource(guestBuffer);
            }

            var size = (ulong)Math.Max(guestBuffer.Length, sizeof(uint));
            if (guestBuffer.BaseAddress > ulong.MaxValue - size)
            {
                return CreateTransientGlobalBufferResource(guestBuffer);
            }

            var endAddress = guestBuffer.BaseAddress + size;
            var allocation = FindGuestBufferAllocation(
                guestBuffer.BaseAddress,
                endAddress);

            if (allocation is null)
            {
                throw new InvalidOperationException(
                    $"no Vulkan guest buffer allocation covers " +
                    $"0x{guestBuffer.BaseAddress:X16}-0x{endAddress:X16}");
            }

            var guestOffset = guestBuffer.BaseAddress - allocation.BaseAddress;
            var descriptorOffset = guestOffset &
                ~(GuestStorageBufferOffsetAlignment - 1);
            var byteBias = guestOffset - descriptorOffset;
            if (descriptorOffset % _minStorageBufferOffsetAlignment != 0)
            {
                throw new InvalidOperationException(
                    $"guest buffer alias offset 0x{descriptorOffset:X} is not aligned to Vulkan's " +
                    $"minStorageBufferOffsetAlignment={_minStorageBufferOffsetAlignment}");
            }

            var expectedBias = guestBuffer.BaseAddress &
                (GuestStorageBufferOffsetAlignment - 1);
            if (byteBias != expectedBias)
            {
                throw new InvalidOperationException(
                    $"guest buffer allocation base 0x{allocation.BaseAddress:X16} " +
                    $"does not satisfy alias alignment " +
                    $"{GuestStorageBufferOffsetAlignment}");
            }

            var source = guestBuffer.Data.AsSpan(0, guestBuffer.Length);
            var shadow = allocation.Shadow.AsSpan(checked((int)guestOffset), guestBuffer.Length);
            var capturedMatchesShadow = source.SequenceEqual(shadow);
            var liveMatchesShadow = capturedMatchesShadow;
            if (guestBuffer.Writable && _guestMemory is not null)
            {
                if (_guestMemory.TryCompare(
                        guestBuffer.BaseAddress,
                        shadow,
                        out var comparedLiveMatchesShadow))
                {
                    liveMatchesShadow = comparedLiveMatchesShadow;
                }
                else
                {
                    var live = GuestDataPool.Shared.Rent(guestBuffer.Length);
                    try
                    {
                        var liveSpan = live.AsSpan(0, guestBuffer.Length);
                        if (_guestMemory.TryRead(guestBuffer.BaseAddress, liveSpan))
                        {
                            liveMatchesShadow = liveSpan.SequenceEqual(shadow);
                        }
                    }
                    finally
                    {
                        GuestDataPool.Shared.Return(live);
                    }
                }
            }

            var needsRefresh = ShouldRefreshGuestGlobalBuffer(
                guestBuffer.Writable,
                capturedMatchesShadow,
                liveMatchesShadow);
            var allocationInFlight = allocation.LastUseTimeline > _completedTimeline;
            var allocationInOpenBatch =
                IsGuestBufferAllocationReferencedByOpenBatch(allocation);
            if (ShouldVersionReadOnlyGuestGlobalBuffer(
                    guestBuffer.Writable,
                    needsRefresh,
                    allocationInFlight,
                    allocationInOpenBatch))
            {
                return CreateVersionedReadOnlyGlobalBufferResource(
                    guestBuffer,
                    expectedBias,
                    size);
            }

            if (needsRefresh)
            {
                // HOST_COHERENT does not permit a mapped CPU write while a
                // shader uses the allocation. Retire prior users first.
                WaitForGuestBufferAllocationForCpuVisibility(allocation);
                WriteBackAllDirtyGuestBuffers();
                // Populate the cached shadow copy first and write it out to the
                // mapped allocation in one pass. The mapped memory is
                // HOST_VISIBLE|HOST_COHERENT (write-combined on most drivers),
                // so CPU reads from it are uncached and orders of magnitude
                // slower than heap reads — never use it as a copy source.
                if (!guestBuffer.Writable)
                {
                    source.CopyTo(shadow);
                }
                else if (_guestMemory?.TryRead(guestBuffer.BaseAddress, shadow) != true)
                {
                    source.CopyTo(shadow);
                }

                shadow.CopyTo(new Span<byte>(
                    (void*)(allocation.Mapped + checked((nint)guestOffset)),
                    guestBuffer.Length));
            }

            if (ShouldTraceVulkanResources() &&
                _tracedGlobalBuffers.Add((guestBuffer.BaseAddress, guestBuffer.Length)))
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.global_buffer base=0x{guestBuffer.BaseAddress:X16} " +
                    $"bytes={guestBuffer.Length}");
            }
            if (guestBuffer.Pooled)
            {
                GuestDataPool.Shared.Return(guestBuffer.Data);
            }

            return new GlobalBufferResource
            {
                BaseAddress = guestBuffer.BaseAddress,
                Writable = guestBuffer.Writable,
                WriteBackToGuest = guestBuffer.WriteBackToGuest,
                Buffer = allocation.Buffer,
                Memory = allocation.Memory,
                Mapped = allocation.Mapped + checked((nint)guestOffset),
                Offset = descriptorOffset,
                Size = checked((size + byteBias + 3) & ~3UL),
                GuestOffset = guestOffset,
                GuestSize = size,
                Allocation = allocation,
            };
        }

        private GuestBufferAllocation? FindGuestBufferAllocation(
            ulong baseAddress,
            ulong endAddress)
        {
            var lower = 0;
            var upper = _guestBufferAllocations.Count - 1;
            var candidateIndex = -1;
            while (lower <= upper)
            {
                var middle = lower + ((upper - lower) / 2);
                var candidate = _guestBufferAllocations[middle];
                if (candidate.BaseAddress <= baseAddress)
                {
                    candidateIndex = middle;
                    lower = middle + 1;
                }
                else
                {
                    upper = middle - 1;
                }
            }

            if (candidateIndex < 0)
            {
                return null;
            }

            var allocation = _guestBufferAllocations[candidateIndex];
            return endAddress - allocation.BaseAddress <= allocation.Size
                ? allocation
                : null;
        }

        private bool IsGuestBufferAllocationReferencedByOpenBatch(
            GuestBufferAllocation allocation)
        {
            if (!_batchOpen)
            {
                return false;
            }

            foreach (var resources in _batchResources)
            {
                foreach (var globalBuffer in resources.GlobalMemoryBuffers)
                {
                    if (ReferenceEquals(globalBuffer?.Allocation, allocation))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private GlobalBufferResource CreateVersionedReadOnlyGlobalBufferResource(
            GuestMemoryBuffer guestBuffer,
            ulong byteBias,
            ulong guestSize)
        {
            var descriptorSize = checked((guestSize + byteBias + 3) & ~3UL);
            var descriptorLength = checked((int)descriptorSize);
            var snapshot = GuestDataPool.Shared.Rent(descriptorLength);
            try
            {
                var snapshotData = snapshot.AsSpan(0, descriptorLength);
                snapshotData.Clear();
                guestBuffer.Data.AsSpan(0, guestBuffer.Length).CopyTo(
                    snapshotData[checked((int)byteBias)..]);

                var buffer = CreateHostBuffer(
                    snapshotData,
                    BufferUsageFlags.StorageBufferBit,
                    out var memory,
                    out var mapped);
                return new GlobalBufferResource
                {
                    BaseAddress = guestBuffer.BaseAddress,
                    Writable = false,
                    WriteBackToGuest = false,
                    Buffer = buffer,
                    Memory = memory,
                    Mapped = mapped + checked((nint)byteBias),
                    Offset = 0,
                    Size = descriptorSize,
                    GuestOffset = byteBias,
                    GuestSize = guestSize,
                };
            }
            finally
            {
                GuestDataPool.Shared.Return(snapshot);
                if (guestBuffer.Pooled)
                {
                    GuestDataPool.Shared.Return(guestBuffer.Data);
                }
            }
        }

        private GlobalBufferResource CreateTransientGlobalBufferResource(
            GuestMemoryBuffer guestBuffer)
        {
            var buffer = CreateHostBuffer(
                guestBuffer.Data.AsSpan(0, guestBuffer.Length),
                BufferUsageFlags.StorageBufferBit,
                out var memory,
                out var mapped);
            if (guestBuffer.Pooled)
            {
                GuestDataPool.Shared.Return(guestBuffer.Data);
            }

            return new GlobalBufferResource
            {
                BaseAddress = 0,
                Writable = false,
                WriteBackToGuest = false,
                Buffer = buffer,
                Memory = memory,
                Mapped = mapped,
                Offset = 0,
                Size = (ulong)Math.Max(guestBuffer.Length, sizeof(uint)),
                GuestOffset = 0,
                GuestSize = (ulong)Math.Max(guestBuffer.Length, sizeof(uint)),
            };
        }

        private void PrepareGuestBufferAllocations(
            IReadOnlyList<GuestMemoryBuffer> buffers)
        {
            if (buffers.Count == 0)
            {
                return;
            }

            var ranges = new List<(ulong Start, ulong End)>(buffers.Count);
            foreach (var buffer in buffers)
            {
                if (buffer.BaseAddress == 0)
                {
                    continue;
                }

                var size = (ulong)Math.Max(buffer.Length, sizeof(uint));
                if (buffer.BaseAddress > ulong.MaxValue - size - 3)
                {
                    continue;
                }

                var alignedStart = buffer.BaseAddress &
                    ~(GuestStorageBufferOffsetAlignment - 1);
                var paddedEnd = (buffer.BaseAddress + size + 3) & ~3UL;
                ranges.Add((
                    alignedStart,
                    paddedEnd));
            }

            if (ranges.Count == 0)
            {
                return;
            }

            ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
            var merged = new List<(ulong Start, ulong End)>(ranges.Count);
            foreach (var range in ranges)
            {
                if (merged.Count == 0 || range.Start > merged[^1].End)
                {
                    merged.Add(range);
                    continue;
                }

                var previous = merged[^1];
                merged[^1] = (
                    previous.Start,
                    Math.Max(previous.End, range.End));
            }

            foreach (var range in merged)
            {
                EnsureGuestBufferAllocation(range.Start, range.End);
            }
        }

        private void EnsureGuestBufferAllocation(
            ulong requestedStart,
            ulong requestedEnd)
        {
            if (FindGuestBufferAllocation(requestedStart, requestedEnd) is not null)
            {
                return;
            }

            var start = requestedStart;
            var end = requestedEnd;
            List<GuestBufferAllocation> overlaps;
            do
            {
                overlaps = _guestBufferAllocations
                    .Where(allocation =>
                        allocation.BaseAddress < end &&
                        start < allocation.BaseAddress + allocation.Size)
                    .ToList();
                var expandedStart = overlaps.Aggregate(
                    start,
                    static (value, allocation) => Math.Min(value, allocation.BaseAddress));
                var expandedEnd = overlaps.Aggregate(
                    end,
                    static (value, allocation) =>
                        Math.Max(value, allocation.BaseAddress + allocation.Size));
                if (expandedStart == start && expandedEnd == end)
                {
                    break;
                }

                start = expandedStart;
                end = expandedEnd;
            }
            while (true);

            if (overlaps.Count == 1 &&
                overlaps[0].BaseAddress <= requestedStart &&
                overlaps[0].BaseAddress + overlaps[0].Size >= requestedEnd)
            {
                return;
            }

            if (overlaps.Count > 0)
            {
                // Growing/merging an aliased allocation is rare. Synchronize
                // only this structural transition so no in-flight descriptor
                // can observe storage being replaced underneath it.
                WaitForAllGuestSubmissionsForCpuVisibility();
                WriteBackAllDirtyGuestBuffers();
            }

            var replacement = CreateGuestBufferAllocation(start, end);
            foreach (var overlap in overlaps)
            {
                _guestBufferAllocations.Remove(overlap);
                DestroyGuestBufferAllocation(overlap);
            }

            _guestBufferAllocations.Add(replacement);
            _guestBufferAllocations.Sort(static (left, right) =>
                left.BaseAddress.CompareTo(right.BaseAddress));
            UpdateGuestBufferCacheMetric();
            TraceVulkanShader(
                $"vk.guest_buffer_allocation base=0x{start:X16} bytes={replacement.Size} " +
                $"merged={overlaps.Count}");
        }

        private void UpdateGuestBufferCacheMetric()
        {
            var bytes = 0UL;
            foreach (var allocation in _guestBufferAllocations)
            {
                bytes = checked(bytes + allocation.Size);
            }

            PerfOverlay.SetGuestBufferCacheBytes(bytes);
        }

        private GuestBufferAllocation CreateGuestBufferAllocation(
            ulong start,
            ulong end)
        {
            var size = checked(end - start);
            if (size == 0 || size > int.MaxValue)
            {
                throw new InvalidOperationException(
                    $"guest buffer allocation is outside the supported host span: " +
                    $"base=0x{start:X16} bytes={size}");
            }

            var buffer = CreateBuffer(
                size,
                BufferUsageFlags.StorageBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out var memory,
                preferredMemoryFlags: MemoryPropertyFlags.HostCachedBit);
            void* mapped;
            Check(_vk.MapMemory(_device, memory, 0, size, 0, &mapped), "vkMapMemory(guest buffer)");
            var shadow = new byte[checked((int)size)];
            _ = _guestMemory?.TryRead(start, shadow);
            shadow.CopyTo(new Span<byte>(mapped, shadow.Length));
            SetDebugName(
                ObjectType.Buffer,
                buffer.Handle,
                $"SharpEmu guest VA 0x{start:X16}-0x{end:X16}");
            return new GuestBufferAllocation
            {
                BaseAddress = start,
                Size = size,
                Buffer = buffer,
                Memory = memory,
                Mapped = (nint)mapped,
                Shadow = shadow,
            };
        }

        private void DestroyGuestBufferAllocation(GuestBufferAllocation allocation)
        {
            ForgetDirtyGuestBuffer(allocation);
            if (allocation.Mapped != 0)
            {
                _vk.UnmapMemory(_device, allocation.Memory);
            }

            _vk.DestroyBuffer(_device, allocation.Buffer, null);
            _vk.FreeMemory(_device, allocation.Memory, null);
        }
    }
}
