// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

// Overlapping guest buffer views must use the same Vulkan allocation. In particular,
// Metal does not track hazards between independent imports of overlapping host pages.
// Fixed backing regions prevent overlap, and import only regions actually used by buffers.
internal sealed unsafe class GuestBufferImportPool(GpuDeviceInfo device)
{
    internal const ulong RegionSize = 4UL * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<(IGuestBackedSpace Backing, ulong Offset), Allocation> _regions = new();

    internal sealed class Allocation(GuestBufferImportPool pool, IGuestBackedSpace backing,
        ulong offset, DeviceMemory memory, uint memoryType)
    {
        internal readonly IGuestBackedSpace Backing = backing;
        internal readonly ulong Offset = offset;
        internal readonly DeviceMemory Memory = memory;
        internal readonly uint MemoryType = memoryType;
        internal int References = 1;
        internal void Release() => pool.Release(this);
    }

    internal bool TryAcquire(IGuestBackedSpace backing, ulong alias, ulong size, ulong alignment,
        uint memoryTypes, out Allocation? allocation, out ulong memoryOffset)
    {
        allocation = null;
        memoryOffset = 0;
        var aliasBase = backing.BackingAliasBase;
        var capacity = backing.BackingAliasSize;
        if (alignment == 0 || device.ImportedHostPointerAlignment == 0 ||
            aliasBase == 0 || alias < aliasBase || alias - aliasBase >= capacity) return false;
        var offset = alias - aliasBase;
        var region = offset / RegionSize * RegionSize;
        var regionSize = Math.Min(RegionSize, capacity - region);
        memoryOffset = offset - region;
        if (size > regionSize - memoryOffset || memoryOffset % alignment != 0 ||
            (aliasBase + region) % device.ImportedHostPointerAlignment != 0 ||
            regionSize % device.ImportedHostPointerAlignment != 0) return false;

        lock (_gate)
        {
            var key = (backing, region);
            if (_regions.TryGetValue(key, out var existing))
            {
                if ((memoryTypes & (1u << (int)existing.MemoryType)) == 0) return false;
                existing.References++;
                allocation = existing;
                return true;
            }

            // Query the pointer actually imported, rather than an interior buffer view.
            var hostProperties = new MemoryHostPointerPropertiesEXT
            { SType = StructureType.MemoryHostPointerPropertiesExt };
            if (device.ExternalMemoryHost == null ||
                device.ExternalMemoryHost.GetMemoryHostPointerProperties(device.Device,
                    ExternalMemoryHandleTypeFlags.HostAllocationBitExt, (void*)(aliasBase + region),
                    &hostProperties) != Result.Success) return false;
            memoryTypes &= hostProperties.MemoryTypeBits;
            const MemoryPropertyFlags required = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
            uint type;
            for (type = 0; type < device.MemoryTypeCount; type++)
                if ((memoryTypes & (1u << (int)type)) != 0 && (device.GetMemoryTypeFlags(type) & required) == required) break;
            if (type == device.MemoryTypeCount || !backing.TryEnterBackingAliasAccess()) return false;

            var flags = new MemoryAllocateFlagsInfo
            {
                SType = StructureType.MemoryAllocateFlagsInfo, Flags = MemoryAllocateFlags.DeviceAddressBit,
            };
            var import = new ImportMemoryHostPointerInfoEXT
            {
                SType = StructureType.ImportMemoryHostPointerInfoExt, PNext = &flags,
                HandleType = ExternalMemoryHandleTypeFlags.HostAllocationBitExt,
                PHostPointer = (void*)(aliasBase + region),
            };
            var info = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo, PNext = &import,
                AllocationSize = regionSize, MemoryTypeIndex = type,
            };
            if (device.AllocateMemory(info, out var memory) != Result.Success)
            {
                backing.ExitBackingAliasAccess();
                return false;
            }
            allocation = new Allocation(this, backing, region, memory, type);
            _regions.Add(key, allocation);
            return true;
        }
    }

    private void Release(Allocation allocation)
    {
        lock (_gate)
        {
            if (--allocation.References != 0) return;
            _regions.Remove((allocation.Backing, allocation.Offset));
            device.FreeMemory(allocation.Memory);
            allocation.Backing.ExitBackingAliasAccess();
        }
    }
}
