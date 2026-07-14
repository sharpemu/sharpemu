// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Loader;

namespace SharpEmu.Core.Memory;

public sealed class VirtualMemory : IVirtualMemory
{
    private readonly object _gate = new();
    private readonly List<MappedRegion> _regions = new();

    public void Clear()
    {
        lock (_gate)
        {
            _regions.Clear();
        }
    }

    public void Map(ulong virtualAddress, ulong memorySize, ulong fileOffset, ReadOnlySpan<byte> fileData, ProgramHeaderFlags protection)
    {
        if (memorySize == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(memorySize), "Memory size must be greater than zero.");
        }

        if ((ulong)fileData.Length > memorySize)
        {
            throw new ArgumentOutOfRangeException(nameof(fileData), "File size cannot exceed memory size.");
        }

        if (memorySize > int.MaxValue)
        {
            throw new NotSupportedException("Virtual memory regions larger than 2 GB are not currently supported.");
        }

        var endAddress = checked(virtualAddress + memorySize);
        var backingMemory = new byte[(int)memorySize];
        fileData.CopyTo(backingMemory);

        lock (_gate)
        {
            foreach (var existing in _regions)
            {
                if (virtualAddress < existing.EndAddress && endAddress > existing.Region.VirtualAddress)
                {
                    throw new InvalidOperationException("Attempted to map an overlapping virtual memory region.");
                }
            }

            _regions.Add(new MappedRegion(
                new VirtualMemoryRegion(virtualAddress, memorySize, fileOffset, (ulong)fileData.Length, protection),
                endAddress,
                backingMemory));
        }
    }

    public bool Free(ulong virtualAddress, ulong memorySize)
    {
        _ = memorySize;
        lock (_gate)
        {
            var index = _regions.FindIndex(region => region.Region.VirtualAddress == virtualAddress);
            if (index < 0)
            {
                return false;
            }

            _regions.RemoveAt(index);
            return true;
        }
    }

    public IReadOnlyList<VirtualMemoryRegion> SnapshotRegions()
    {
        lock (_gate)
        {
            var snapshot = new VirtualMemoryRegion[_regions.Count];
            for (var i = 0; i < _regions.Count; i++)
            {
                snapshot[i] = _regions[i].Region;
            }

            return snapshot;
        }
    }

    public bool TryRead(ulong virtualAddress, Span<byte> destination)
    {
        lock (_gate)
        {
            if (!TryResolveRegion(virtualAddress, destination.Length, out var region, out var offset))
            {
                return false;
            }

            region.BackingMemory.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }
    }

    public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
    {
        lock (_gate)
        {
            if (!TryResolveRegion(virtualAddress, source.Length, out var region, out var offset))
            {
                return false;
            }

            source.CopyTo(region.BackingMemory.AsSpan(offset, source.Length));
            return true;
        }
    }

    private bool TryResolveRegion(ulong virtualAddress, int length, out MappedRegion region, out int offset)
    {
        foreach (var candidate in _regions)
        {
            if (virtualAddress < candidate.Region.VirtualAddress || virtualAddress >= candidate.EndAddress)
            {
                continue;
            }

            var candidateOffset = checked((int)(virtualAddress - candidate.Region.VirtualAddress));
            if (candidateOffset + length > candidate.BackingMemory.Length)
            {
                break;
            }

            region = candidate;
            offset = candidateOffset;
            return true;
        }

        region = default;
        offset = 0;
        return false;
    }

    private readonly record struct MappedRegion(VirtualMemoryRegion Region, ulong EndAddress, byte[] BackingMemory);
}
