// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Core.Loader;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Logging;

namespace SharpEmu.Core.Memory;

public sealed unsafe class PhysicalVirtualMemory : IVirtualMemory, IGuestMemoryAllocator, IGuestAddressSpace, IDisposable
{
    private static readonly SharpEmuLogger Log = SharpEmuLog.For("VMEM");

    private readonly ReaderWriterLockSlim _gate = new(LockRecursionPolicy.SupportsRecursion);
    private readonly object _guestAllocationGate = new();
    private readonly object _directMemoryBackingGate = new();
    private readonly object _allocationSearchHintGate = new();
    private readonly List<MemoryRegion> _regions = new();
    private readonly Dictionary<ulong, DirectMapping> _directMappings = new();
    private readonly Dictionary<(ulong DesiredAddress, ulong Alignment, bool Executable), ulong> _allocationSearchHints = new();
    private readonly Dictionary<ulong, ProgramHeaderFlags> _pageProtections = new();
    private bool _disposed;
    private const ulong PageSize = 0x1000;
    private const ulong GuestAllocationArenaAddress = 0x00006000_0000_0000;
    private const ulong GuestAllocationArenaSize = 0x0100_0000;
    private const ulong GuestAllocationArenaStartOffset = PageSize;
    private const ulong LargeDataReserveThreshold = 0x4000_0000UL; // 1 GiB
    private const ulong FullCommitRegionLimit = 4UL << 30;
    private const ulong DefaultLazyReservePrimeBytes = 0x0400_0000UL; // 64 MiB
    private const ulong LazyReservePrimeChunkBytes = 0x0200_0000UL; // 32 MiB

    // Raw Windows PAGE_* values retained for the internal region/protection
    // bookkeeping: regions and saved old-protection values always carry the raw
    // value of the host platform in use, and these classification helpers only
    // ever see values this class itself assigned (see IHostMemory.ProtectRaw).
    private const uint PAGE_EXECUTE_READ = 0x20;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const uint PAGE_EXECUTE = 0x10;
    private const uint PAGE_EXECUTE_WRITECOPY = 0x80;
    private const uint PAGE_READWRITE = 0x04;
    private const uint PAGE_READONLY = 0x02;

    private readonly IHostMemory _hostMemory;
    private IHostSharedMemory? _directMemoryBacking;
    private ulong _directMemoryBackingSize;
    private ulong _guestAllocationArenaBase;
    private readonly SortedDictionary<ulong, ulong> _guestAllocationFreeRanges = new();
    private readonly Dictionary<ulong, (ulong Offset, ulong Size)> _guestAllocations = new();
    private static readonly ulong LazyReservePrimeBytes = ResolveLazyReservePrimeBytes();

    public PhysicalVirtualMemory(IHostMemory? hostMemory = null)
    {
        _hostMemory = hostMemory ?? HostPlatform.Current.Memory;
    }

    private sealed class CrossPlatformHostMemory : IHostMemory
    {
        public static readonly CrossPlatformHostMemory Instance = new();

        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection) =>
            unchecked((ulong)HostMemory.Alloc(
                (void*)desiredAddress,
                (nuint)size,
                HostMemory.MEM_RESERVE | HostMemory.MEM_COMMIT,
                ToRawProtection(protection)));

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) =>
            unchecked((ulong)HostMemory.Alloc(
                (void*)desiredAddress,
                (nuint)size,
                HostMemory.MEM_RESERVE,
                ToRawProtection(protection)));

        public bool Commit(ulong address, ulong size, HostPageProtection protection) =>
            HostMemory.Alloc(
                (void*)address,
                (nuint)size,
                HostMemory.MEM_COMMIT,
                ToRawProtection(protection)) != null;

        public bool Free(ulong address) =>
            HostMemory.Free((void*)address, 0, HostMemory.MEM_RELEASE);

        public bool Protect(
            ulong address,
            ulong size,
            HostPageProtection protection,
            out uint rawOldProtection) =>
            HostMemory.Protect(
                (void*)address,
                (nuint)size,
                ToRawProtection(protection),
                out rawOldProtection);

        public bool ProtectRaw(
            ulong address,
            ulong size,
            uint rawProtection,
            out uint rawOldProtection) =>
            HostMemory.Protect((void*)address, (nuint)size, rawProtection, out rawOldProtection);

        public bool Query(ulong address, out HostRegionInfo info)
        {
            if (HostMemory.Query((void*)address, out var raw) == 0)
            {
                info = default;
                return false;
            }

            var state = raw.State switch
            {
                HostMemory.MEM_FREE_STATE => HostRegionState.Free,
                HostMemory.MEM_RESERVE => HostRegionState.Reserved,
                _ => HostRegionState.Committed,
            };

            info = new HostRegionInfo(
                raw.BaseAddress,
                raw.AllocationBase,
                raw.RegionSize,
                state,
                raw.State,
                FromRawProtection(raw.Protect),
                raw.Protect,
                raw.AllocationProtect);
            return true;
        }

        public void FlushInstructionCache(ulong address, ulong size) =>
            HostMemory.FlushInstructionCache((void*)address, (nuint)size);

        private static uint ToRawProtection(HostPageProtection protection) => protection switch
        {
            HostPageProtection.NoAccess => HostMemory.PAGE_NOACCESS,
            HostPageProtection.ReadOnly => HostMemory.PAGE_READONLY,
            HostPageProtection.ReadWrite => HostMemory.PAGE_READWRITE,
            HostPageProtection.Execute => HostMemory.PAGE_EXECUTE,
            HostPageProtection.ReadExecute => HostMemory.PAGE_EXECUTE_READ,
            HostPageProtection.ReadWriteExecute => HostMemory.PAGE_EXECUTE_READWRITE,
            HostPageProtection.ExecuteWriteCopy => 0x80,
            _ => HostMemory.PAGE_NOACCESS,
        };

        private static HostPageProtection FromRawProtection(uint protection) => protection switch
        {
            HostMemory.PAGE_READONLY => HostPageProtection.ReadOnly,
            HostMemory.PAGE_READWRITE => HostPageProtection.ReadWrite,
            HostMemory.PAGE_EXECUTE => HostPageProtection.Execute,
            HostMemory.PAGE_EXECUTE_READ => HostPageProtection.ReadExecute,
            HostMemory.PAGE_EXECUTE_READWRITE => HostPageProtection.ReadWriteExecute,
            0x80 => HostPageProtection.ExecuteWriteCopy,
            _ => HostPageProtection.NoAccess,
        };
    }

    public bool TryAllocateAtExact(ulong desiredAddress, ulong size, bool executable, out ulong actualAddress)
    {
        actualAddress = 0;
        if (size == 0)
        {
            return false;
        }

        var alignedSize = (size + 0xFFF) & ~0xFFFUL;
        var protection = executable ? PAGE_EXECUTE_READWRITE : PAGE_READWRITE;
        var hostProtection = executable ? HostPageProtection.ReadWriteExecute : HostPageProtection.ReadWrite;
        var result = _hostMemory.Allocate(desiredAddress, alignedSize, hostProtection);
        if (result == 0)
        {
            return false;
        }

        actualAddress = result;
        if (actualAddress != desiredAddress)
        {
            _hostMemory.Free(result);
            actualAddress = 0;
            return false;
        }

        _gate.EnterWriteLock();
        try
        {
            InsertRegionSorted(new MemoryRegion
            {
                VirtualAddress = actualAddress,
                Size = alignedSize,
                IsExecutable = executable,
                IsReservedOnly = false,
                Protection = protection
            });
        }
        finally
        {
            _gate.ExitWriteLock();
        }

        var allocationKind = executable ? "executable memory" : "data memory";
        TraceVmem($"Allocated exact {allocationKind}: 0x{actualAddress:X16} - 0x{actualAddress + alignedSize:X16} ({alignedSize} bytes)");
        return true;
    }

    public string DescribeAddressForDiagnostics(ulong address)
    {
        if (!_hostMemory.Query(address, out var info))
        {
            return "unable to query host memory at this address";
        }

        return info.State switch
        {
            HostRegionState.Free => "address reports free, but the exact-address reservation still failed",
            HostRegionState.Reserved =>
                $"already reserved by another host allocation (base=0x{info.AllocationBase:X16}, size=0x{info.RegionSize:X})",
            HostRegionState.Committed =>
                $"already committed by another host allocation (base=0x{info.AllocationBase:X16}, size=0x{info.RegionSize:X}, protect=0x{info.RawProtection:X})",
            _ => $"in an unexpected host state (raw=0x{info.RawState:X})",
        };
    }

    public ulong AllocateAt(ulong desiredAddress, ulong size, bool executable = true, bool allowAlternative = true)
    {
        if (size == 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Size must be greater than zero");

        var alignedSize = (size + 0xFFF) & ~0xFFFUL;

        var protection = executable ? PAGE_EXECUTE_READWRITE : PAGE_READWRITE;
        var hostProtection = executable ? HostPageProtection.ReadWriteExecute : HostPageProtection.ReadWrite;
        var reservedOnly = false;
        var preferReserveOnly = !executable &&
            alignedSize >= LargeDataReserveThreshold &&
            alignedSize > FullCommitRegionLimit;

        ulong result = 0;
        if (preferReserveOnly)
        {
            result = _hostMemory.Reserve(desiredAddress, alignedSize, HostPageProtection.ReadWrite);
            if (result == 0 && allowAlternative)
            {
                result = _hostMemory.Reserve(0, alignedSize, HostPageProtection.ReadWrite);
            }

            if (result != 0)
            {
                reservedOnly = true;
            }
        }

        if (result == 0)
        {
            result = _hostMemory.Allocate(desiredAddress, alignedSize, hostProtection);
        }

        if (result == 0)
        {
            if (!allowAlternative)
            {
                throw new InvalidOperationException($"Failed to allocate exact mapping at 0x{desiredAddress:X16} ({alignedSize} bytes)");
            }

            TraceVmem($"Could not allocate at 0x{desiredAddress:X16}, trying any address...");
            result = _hostMemory.Allocate(0, alignedSize, hostProtection);

            if (result == 0)
            {
                if (!executable)
                {
                    result = _hostMemory.Reserve(desiredAddress, alignedSize, HostPageProtection.ReadWrite);
                    if (result == 0 && allowAlternative)
                    {
                        result = _hostMemory.Reserve(0, alignedSize, HostPageProtection.ReadWrite);
                    }

                    if (result != 0)
                    {
                        reservedOnly = true;
                    }
                }

                if (result == 0)
                {
                    throw new OutOfMemoryException($"Failed to allocate {alignedSize} bytes of virtual memory");
                }
            }
        }

        var actualAddress = result;

        var lazyPrimeState = "n/a";
        if (reservedOnly && !_hostMemory.UsesPlaceholderReservations)
        {
            var primeBytes = Math.Min(alignedSize, LazyReservePrimeBytes);
            if (primeBytes != 0)
            {
                ulong committedBytes = 0;
                while (committedBytes < primeBytes)
                {
                    var remaining = primeBytes - committedBytes;
                    var chunkBytes = Math.Min(remaining, LazyReservePrimeChunkBytes);
                    var commitAddress = actualAddress + committedBytes;
                    if (!_hostMemory.Commit(commitAddress, chunkBytes, HostPageProtection.ReadWrite))
                    {
                        break;
                    }

                    committedBytes += chunkBytes;
                }

                if (committedBytes != 0)
                {
                    lazyPrimeState = committedBytes == primeBytes
                        ? $"ok:{committedBytes:X}"
                        : $"partial:{committedBytes:X}/{primeBytes:X}";
                    TraceVmem($"Primed lazy region: 0x{actualAddress:X16} - 0x{actualAddress + committedBytes:X16} ({committedBytes} bytes)");
                }
                else
                {
                    lazyPrimeState = $"fail:{primeBytes:X}";
                    TraceVmem($"Failed to prime lazy region at 0x{actualAddress:X16} ({primeBytes} bytes), continuing with on-demand commit");
                }
            }
            else
            {
                lazyPrimeState = "skip:0";
            }
        }

        _gate.EnterWriteLock();
        try
        {
            InsertRegionSorted(new MemoryRegion
            {
                VirtualAddress = actualAddress,
                Size = alignedSize,
                IsExecutable = executable,
                IsReservedOnly = reservedOnly,
                Protection = protection
            });
        }
        finally
        {
            _gate.ExitWriteLock();
        }

        var allocationKind = reservedOnly
            ? "reserved data memory (lazy commit)"
            : (executable ? "executable memory" : "data memory");
        TraceVmem($"Allocated {allocationKind}: 0x{actualAddress:X16} - 0x{actualAddress + alignedSize:X16} ({alignedSize} bytes) lazy_prime={lazyPrimeState}");

        return actualAddress;
    }

    public ulong ReserveAt(ulong desiredAddress, ulong size, bool executable = true, bool allowAlternative = true)
    {
        if (size == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Size must be greater than zero");
        }

        var alignedSize = AlignUp(size, PageSize);
        var result = _hostMemory.Reserve(desiredAddress, alignedSize, HostPageProtection.NoAccess);
        if (result == 0 && allowAlternative)
        {
            result = _hostMemory.Reserve(0, alignedSize, HostPageProtection.NoAccess);
        }

        if (result == 0 || (desiredAddress != 0 && !allowAlternative && result != desiredAddress))
        {
            if (result != 0)
            {
                _hostMemory.Free(result);
            }

            throw new InvalidOperationException(
                $"Failed to reserve guest range at 0x{desiredAddress:X16} ({alignedSize} bytes)");
        }

        _gate.EnterWriteLock();
        try
        {
            InsertRegionSorted(new MemoryRegion
            {
                VirtualAddress = result,
                Size = alignedSize,
                IsExecutable = executable,
                IsReservedOnly = true,
                IsGuestReservation = true,
                Protection = executable ? PAGE_EXECUTE_READWRITE : PAGE_READWRITE
            });
        }
        finally
        {
            _gate.ExitWriteLock();
        }

        TraceVmem(
            $"Reserved guest range: 0x{result:X16} - 0x{result + alignedSize:X16} ({alignedSize} bytes)");
        return result;
    }

    public bool TryAllocateAtOrAbove(
        ulong desiredAddress,
        ulong size,
        bool executable,
        ulong alignment,
        out ulong actualAddress)
    {
        actualAddress = 0;
        if (size == 0)
        {
            return false;
        }

        var alignedSize = AlignUp(size, PageSize);
        var effectiveAlignment = Math.Max(PageSize, alignment == 0 ? PageSize : alignment);
        var requestedCursor = AlignUp(desiredAddress, effectiveAlignment);
        var cursor = GetAllocationSearchCursor(desiredAddress, requestedCursor, effectiveAlignment, executable);

        // macOS needs alignment over-allocation; Linux uses exact-address search.
        if (OperatingSystem.IsMacOS())
        {
            var reserveSize = effectiveAlignment > PageSize
                ? alignedSize + effectiveAlignment
                : alignedSize;
            try
            {
                var posixAddress = AllocateAt(cursor, reserveSize, executable, allowAlternative: true);
                if (posixAddress != 0)
                {
                    var alignedBase = AlignUp(posixAddress, effectiveAlignment);
                    if (alignedBase + alignedSize <= posixAddress + reserveSize)
                    {
                        actualAddress = alignedBase;
                        UpdateAllocationSearchCursor(desiredAddress, effectiveAlignment, executable, alignedBase + alignedSize);
                        return true;
                    }

                    ReleaseUntrackedAllocation(posixAddress);
                }
            }
            catch
            {
            }

            return false;
        }

        for (var attempt = 0; attempt < 0x10000; attempt++)
        {
            if (cursor == 0 || ulong.MaxValue - cursor < alignedSize)
            {
                return false;
            }

            if (TryGetOverlappingRegionEnd(cursor, alignedSize, out var overlapEnd))
            {
                cursor = AlignUp(overlapEnd, effectiveAlignment);
                continue;
            }

            if (TryAllocateAtExact(cursor, alignedSize, executable, out actualAddress))
            {
                UpdateAllocationSearchCursor(desiredAddress, effectiveAlignment, executable, actualAddress + alignedSize);
                return true;
            }

            cursor = AlignUp(cursor + effectiveAlignment, effectiveAlignment);
        }

        return false;
    }

    public bool TryReserveAtOrAbove(
        ulong desiredAddress,
        ulong size,
        bool executable,
        ulong alignment,
        out ulong actualAddress)
    {
        actualAddress = 0;
        if (size == 0)
        {
            return false;
        }

        var alignedSize = AlignUp(size, PageSize);
        var effectiveAlignment = Math.Max(PageSize, alignment == 0 ? PageSize : alignment);
        var requestedCursor = AlignUp(desiredAddress, effectiveAlignment);
        var cursor = GetAllocationSearchCursor(desiredAddress, requestedCursor, effectiveAlignment, executable);

        for (var attempt = 0; attempt < 0x10000; attempt++)
        {
            if (cursor == 0 || ulong.MaxValue - cursor < alignedSize)
            {
                return false;
            }

            if (TryGetOverlappingRegionEnd(cursor, alignedSize, out var overlapEnd))
            {
                cursor = AlignUp(overlapEnd, effectiveAlignment);
                continue;
            }

            try
            {
                actualAddress = ReserveAt(
                    cursor,
                    alignedSize,
                    executable,
                    allowAlternative: OperatingSystem.IsMacOS());
                if (actualAddress != 0 &&
                    (actualAddress & (effectiveAlignment - 1)) == 0)
                {
                    UpdateAllocationSearchCursor(
                        desiredAddress,
                        effectiveAlignment,
                        executable,
                        actualAddress + alignedSize);
                    return true;
                }

                if (actualAddress != 0)
                {
                    ReleaseUntrackedAllocation(actualAddress);
                }

                actualAddress = 0;
            }
            catch
            {
            }

            cursor = AlignUp(cursor + effectiveAlignment, effectiveAlignment);
        }

        return false;
    }

    public bool TryMapDirectMemory(
        ulong desiredAddress,
        ulong size,
        ulong directMemoryOffset,
        ulong directMemorySize,
        GuestPageProtection protection,
        ulong alignment,
        bool allowSearch,
        out ulong actualAddress)
    {
        actualAddress = 0;
        if (size == 0 ||
            directMemorySize == 0 ||
            size > ulong.MaxValue - (PageSize - 1) ||
            directMemoryOffset > directMemorySize ||
            size > directMemorySize - directMemoryOffset)
        {
            return false;
        }

        var alignedSize = AlignUp(size, PageSize);
        if (alignedSize > directMemorySize - directMemoryOffset)
        {
            return false;
        }

        var effectiveAlignment = Math.Max(PageSize, alignment == 0 ? PageSize : alignment);
        if (effectiveAlignment % PageSize != 0)
        {
            return false;
        }

        IHostSharedMemory backing;
        lock (_directMemoryBackingGate)
        {
            if (_directMemoryBacking is null)
            {
                _directMemoryBacking = _hostMemory.CreateSharedMemory(directMemorySize);
                _directMemoryBackingSize = _directMemoryBacking?.Size ?? 0;
            }

            if (_directMemoryBacking is null || _directMemoryBackingSize != directMemorySize)
            {
                TraceVmem(
                    $"Failed to create direct-memory backing: requested=0x{directMemorySize:X} actual=0x{_directMemoryBackingSize:X}");
                return false;
            }

            backing = _directMemoryBacking;
        }

        var hostProtection = ResolveProtection(protection);
        var isExecutable = (protection & GuestPageProtection.Execute) != 0;
        var rawProtection = isExecutable
            ? (protection & GuestPageProtection.Write) != 0 ? PAGE_EXECUTE_READWRITE : PAGE_EXECUTE_READ
            : (protection & GuestPageProtection.Write) != 0 ? PAGE_READWRITE : PAGE_READONLY;
        ulong cursor;
        try
        {
            cursor = desiredAddress == 0
                ? 0
                : allowSearch
                    ? AlignUpToMultiple(desiredAddress, effectiveAlignment)
                    : desiredAddress;
        }
        catch (OverflowException)
        {
            return false;
        }

        if (!allowSearch &&
            (cursor == 0 || (cursor & (PageSize - 1)) != 0))
        {
            return false;
        }

        var maximumAttempts = allowSearch ? 0x10000 : 1;

        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            MemoryRegion? containingReservation = null;
            if (cursor != 0)
            {
                _gate.EnterReadLock();
                try
                {
                    var containingRegion = FindRegion(cursor, alignedSize);
                    if (containingRegion?.IsReservedOnly == true)
                    {
                        containingReservation = containingRegion;
                    }
                }
                finally
                {
                    _gate.ExitReadLock();
                }
            }

            if (cursor != 0 && containingReservation is null &&
                (ulong.MaxValue - cursor < alignedSize || TryGetOverlappingRegionEnd(cursor, alignedSize, out _)))
            {
                TraceVmem(
                    $"Direct-memory candidate overlaps committed mapping: va=0x{cursor:X16} size=0x{alignedSize:X}");
                if (!allowSearch || ulong.MaxValue - cursor < effectiveAlignment)
                {
                    return false;
                }

                cursor = AlignUpToMultiple(
                    cursor + effectiveAlignment,
                    effectiveAlignment);
                continue;
            }

            var mappedAddress = _hostMemory.MapSharedMemory(
                backing,
                cursor,
                alignedSize,
                directMemoryOffset,
                hostProtection);
            if (mappedAddress != 0 &&
                (allowSearch
                    ? mappedAddress % effectiveAlignment == 0
                    : mappedAddress == cursor))
            {
                _gate.EnterWriteLock();
                try
                {
                    if (containingReservation is null)
                    {
                        InsertRegionSorted(new MemoryRegion
                        {
                            VirtualAddress = mappedAddress,
                            Size = alignedSize,
                            IsExecutable = isExecutable,
                            IsReservedOnly = false,
                            Protection = rawProtection
                        });
                    }

                    _directMappings.Add(
                        mappedAddress,
                        new DirectMapping(
                            mappedAddress,
                            alignedSize,
                            directMemoryOffset,
                            hostProtection));
                }
                finally
                {
                    _gate.ExitWriteLock();
                }

                actualAddress = mappedAddress;
                TraceVmem(
                    $"Mapped direct memory: phys=0x{directMemoryOffset:X16} va=0x{mappedAddress:X16} size=0x{alignedSize:X}");
                return true;
            }

            if (mappedAddress != 0)
            {
                _hostMemory.Free(mappedAddress);
            }

            TraceVmem(
                $"Host rejected direct-memory view: phys=0x{directMemoryOffset:X16} va=0x{cursor:X16} size=0x{alignedSize:X}");

            if (!allowSearch || cursor == 0 || ulong.MaxValue - cursor < effectiveAlignment)
            {
                return false;
            }

            cursor = AlignUpToMultiple(
                cursor + effectiveAlignment,
                effectiveAlignment);
        }

        return false;
    }

    public bool TryReplaceDirectMemory(
        ulong address,
        ulong size,
        ulong directMemoryOffset,
        ulong directMemorySize,
        GuestPageProtection protection,
        out ulong actualAddress)
    {
        actualAddress = 0;
        if (address == 0 ||
            size == 0 ||
            directMemorySize == 0 ||
            size > ulong.MaxValue - (PageSize - 1))
        {
            return false;
        }

        var alignedSize = AlignUp(size, PageSize);
        if ((address & (PageSize - 1)) != 0 ||
            ulong.MaxValue - address < alignedSize ||
            directMemoryOffset > directMemorySize ||
            alignedSize > directMemorySize - directMemoryOffset)
        {
            return false;
        }

        IHostSharedMemory backing;
        lock (_directMemoryBackingGate)
        {
            if (_directMemoryBacking is null)
            {
                _directMemoryBacking = _hostMemory.CreateSharedMemory(directMemorySize);
                _directMemoryBackingSize = _directMemoryBacking?.Size ?? 0;
            }

            if (_directMemoryBacking is null || _directMemoryBackingSize != directMemorySize)
            {
                TraceVmem(
                    $"Failed to create direct-memory backing: requested=0x{directMemorySize:X} actual=0x{_directMemoryBackingSize:X}");
                return false;
            }

            backing = _directMemoryBacking;
        }

        var end = address + alignedSize;
        var hostProtection = ResolveProtection(protection);
        var isExecutable = (protection & GuestPageProtection.Execute) != 0;
        var rawProtection = isExecutable
            ? (protection & GuestPageProtection.Write) != 0 ? PAGE_EXECUTE_READWRITE : PAGE_EXECUTE_READ
            : (protection & GuestPageProtection.Write) != 0 ? PAGE_READWRITE : PAGE_READONLY;

        _gate.EnterWriteLock();
        try
        {
            var overlaps = _directMappings.Values
                .Where(mapping =>
                    mapping.VirtualAddress < end &&
                    address < mapping.VirtualAddress + mapping.Size)
                .OrderBy(static mapping => mapping.VirtualAddress)
                .ToArray();
            if (overlaps.Length == 0)
            {
                return false;
            }

            var standaloneRegions = new Dictionary<ulong, MemoryRegion>();
            var survivors = new List<DirectMapping>(overlaps.Length * 2);
            var survivorRegions = new List<MemoryRegion>(overlaps.Length * 2);
            foreach (var mapping in overlaps)
            {
                var standaloneRegion = _regions.FirstOrDefault(region =>
                    region.VirtualAddress == mapping.VirtualAddress &&
                    region.Size == mapping.Size &&
                    !region.IsReservedOnly);
                if (standaloneRegion is not null)
                {
                    standaloneRegions.Add(mapping.VirtualAddress, standaloneRegion);
                }

                var mappingEnd = mapping.VirtualAddress + mapping.Size;
                var overlapStart = Math.Max(address, mapping.VirtualAddress);
                var overlapEnd = Math.Min(end, mappingEnd);
                if (mapping.VirtualAddress < overlapStart)
                {
                    var prefix = mapping with { Size = overlapStart - mapping.VirtualAddress };
                    survivors.Add(prefix);
                    if (standaloneRegion is not null)
                    {
                        survivorRegions.Add(CloneRegionRange(
                            standaloneRegion,
                            prefix.VirtualAddress,
                            prefix.Size));
                    }
                }

                if (overlapEnd < mappingEnd)
                {
                    var suffix = new DirectMapping(
                        overlapEnd,
                        mappingEnd - overlapEnd,
                        mapping.PhysicalOffset + (overlapEnd - mapping.VirtualAddress),
                        mapping.Protection);
                    survivors.Add(suffix);
                    if (standaloneRegion is not null)
                    {
                        survivorRegions.Add(CloneRegionRange(
                            standaloneRegion,
                            suffix.VirtualAddress,
                            suffix.Size));
                    }
                }
            }

            var replacement = new DirectMapping(
                address,
                alignedSize,
                directMemoryOffset,
                hostProtection);
            var containingReservation = FindRegion(address, alignedSize);
            var replacementRegion = containingReservation?.IsReservedOnly == true
                ? null
                : new MemoryRegion
                {
                    VirtualAddress = address,
                    Size = alignedSize,
                    IsExecutable = isExecutable,
                    IsReservedOnly = false,
                    Protection = rawProtection
                };

            var removedOriginals = new List<DirectMapping>(overlaps.Length);
            foreach (var mapping in overlaps)
            {
                if (!_hostMemory.UnmapSharedMemory(mapping.VirtualAddress, mapping.Size))
                {
                    RollbackDirectReplacementLocked(backing, [], removedOriginals);
                    return false;
                }

                removedOriginals.Add(mapping);
            }

            var installedMappings = new List<DirectMapping>(survivors.Count + 1);
            foreach (var survivor in survivors)
            {
                if (!TryMapDirectViewLocked(backing, survivor))
                {
                    RollbackDirectReplacementLocked(backing, installedMappings, removedOriginals);
                    return false;
                }

                installedMappings.Add(survivor);
            }

            if (!TryMapDirectViewLocked(backing, replacement))
            {
                RollbackDirectReplacementLocked(backing, installedMappings, removedOriginals);
                return false;
            }

            installedMappings.Add(replacement);

            foreach (var mapping in overlaps)
            {
                _directMappings.Remove(mapping.VirtualAddress);
                if (standaloneRegions.TryGetValue(mapping.VirtualAddress, out var standaloneRegion))
                {
                    _regions.Remove(standaloneRegion);
                }
            }

            foreach (var survivor in survivors)
            {
                _directMappings.Add(survivor.VirtualAddress, survivor);
            }

            foreach (var survivorRegion in survivorRegions)
            {
                InsertRegionSorted(survivorRegion);
            }

            _directMappings.Add(replacement.VirtualAddress, replacement);
            if (replacementRegion is not null)
            {
                InsertRegionSorted(replacementRegion);
            }

            actualAddress = address;
            TraceVmem(
                $"Replaced direct memory: phys=0x{directMemoryOffset:X16} va=0x{address:X16} size=0x{alignedSize:X}");
            return true;
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    private bool TryMapDirectViewLocked(IHostSharedMemory backing, DirectMapping mapping)
    {
        var mappedAddress = _hostMemory.MapSharedMemory(
            backing,
            mapping.VirtualAddress,
            mapping.Size,
            mapping.PhysicalOffset,
            mapping.Protection);
        if (mappedAddress == mapping.VirtualAddress)
        {
            return true;
        }

        if (mappedAddress != 0 &&
            !_hostMemory.UnmapSharedMemory(mappedAddress, mapping.Size))
        {
            throw new InvalidOperationException(
                $"Failed to remove misplaced direct-memory view at 0x{mappedAddress:X16}");
        }

        return false;
    }

    private void RollbackDirectReplacementLocked(
        IHostSharedMemory backing,
        IReadOnlyList<DirectMapping> installedMappings,
        IReadOnlyList<DirectMapping> removedOriginals)
    {
        var restored = true;
        for (var index = installedMappings.Count - 1; index >= 0; index--)
        {
            var mapping = installedMappings[index];
            restored &= _hostMemory.UnmapSharedMemory(mapping.VirtualAddress, mapping.Size);
        }

        foreach (var mapping in removedOriginals)
        {
            restored &= TryMapDirectViewLocked(backing, mapping);
        }

        if (!restored)
        {
            throw new InvalidOperationException(
                "Failed to restore direct-memory mappings after an atomic replacement failure");
        }
    }

    public bool TryUnmapDirectMemory(ulong address, ulong size)
    {
        if (size == 0)
        {
            return false;
        }

        var alignedSize = AlignUp(size, PageSize);
        _gate.EnterWriteLock();
        try
        {
            if (!_directMappings.TryGetValue(address, out var mapping) ||
                mapping.Size != alignedSize ||
                !_hostMemory.UnmapSharedMemory(address, alignedSize))
            {
                return false;
            }

            _directMappings.Remove(address);
            for (var index = 0; index < _regions.Count; index++)
            {
                var region = _regions[index];
                if (region.VirtualAddress == address &&
                    region.Size == alignedSize &&
                    !region.IsReservedOnly)
                {
                    _regions.RemoveAt(index);
                    break;
                }
            }

            return true;
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    public bool TryUnmapDirectMemoryRange(ulong address, ulong size)
    {
        if (size == 0 || ulong.MaxValue - address < size)
        {
            return false;
        }

        var alignedSize = AlignUp(size, PageSize);
        if (ulong.MaxValue - address < alignedSize)
        {
            return false;
        }

        var end = address + alignedSize;
        _gate.EnterWriteLock();
        try
        {
            var overlaps = _directMappings.Values
                .Where(mapping =>
                    mapping.VirtualAddress < end &&
                    address < mapping.VirtualAddress + mapping.Size)
                .OrderBy(static mapping => mapping.VirtualAddress)
                .ToArray();
            if (overlaps.Length == 0)
            {
                return true;
            }

            IHostSharedMemory? backing;
            lock (_directMemoryBackingGate)
            {
                backing = _directMemoryBacking;
            }

            if (backing is null)
            {
                return false;
            }

            var standaloneRegions = new Dictionary<ulong, MemoryRegion>();
            foreach (var mapping in overlaps)
            {
                var standaloneRegion = _regions.FirstOrDefault(region =>
                    region.VirtualAddress == mapping.VirtualAddress &&
                    region.Size == mapping.Size &&
                    !region.IsReservedOnly);
                if (standaloneRegion is not null)
                {
                    standaloneRegions.Add(mapping.VirtualAddress, standaloneRegion);
                }

                if (!_hostMemory.UnmapSharedMemory(mapping.VirtualAddress, mapping.Size))
                {
                    return false;
                }

                _directMappings.Remove(mapping.VirtualAddress);
                if (standaloneRegion is not null)
                {
                    _regions.Remove(standaloneRegion);
                }
            }

            foreach (var mapping in overlaps)
            {
                var mappingEnd = mapping.VirtualAddress + mapping.Size;
                var overlapStart = Math.Max(address, mapping.VirtualAddress);
                var overlapEnd = Math.Min(end, mappingEnd);
                if (mapping.VirtualAddress < overlapStart &&
                    !TryRemapDirectFragmentLocked(
                        backing,
                        mapping,
                        mapping.VirtualAddress,
                        overlapStart - mapping.VirtualAddress,
                        standaloneRegions.GetValueOrDefault(mapping.VirtualAddress)))
                {
                    return false;
                }

                if (overlapEnd < mappingEnd &&
                    !TryRemapDirectFragmentLocked(
                        backing,
                        mapping,
                        overlapEnd,
                        mappingEnd - overlapEnd,
                        standaloneRegions.GetValueOrDefault(mapping.VirtualAddress)))
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    private bool TryRemapDirectFragmentLocked(
        IHostSharedMemory backing,
        DirectMapping source,
        ulong address,
        ulong size,
        MemoryRegion? standaloneRegion)
    {
        var physicalOffset = source.PhysicalOffset + (address - source.VirtualAddress);
        var mappedAddress = _hostMemory.MapSharedMemory(
            backing,
            address,
            size,
            physicalOffset,
            source.Protection);
        if (mappedAddress != address)
        {
            if (mappedAddress != 0)
            {
                _hostMemory.UnmapSharedMemory(mappedAddress, size);
            }

            return false;
        }

        _directMappings.Add(
            address,
            new DirectMapping(address, size, physicalOffset, source.Protection));
        if (standaloneRegion is not null)
        {
            InsertRegionSorted(CloneRegionRange(standaloneRegion, address, size));
        }

        return true;
    }

    public bool TryUnmapReservedMemory(ulong address, ulong size)
    {
        if (size == 0)
        {
            return false;
        }

        var alignedSize = AlignUp(size, PageSize);
        _gate.EnterWriteLock();
        try
        {
            var region = FindRegion(address, alignedSize);
            if (region?.IsGuestReservation != true ||
                _directMappings.Values.Any(mapping =>
                    mapping.VirtualAddress < address + alignedSize &&
                    address < mapping.VirtualAddress + mapping.Size) ||
                !_hostMemory.UnmapReservedMemory(address, alignedSize))
            {
                return false;
            }

            _regions.Remove(region);
            if (region.VirtualAddress < address)
            {
                InsertRegionSorted(CloneRegionRange(
                    region,
                    region.VirtualAddress,
                    address - region.VirtualAddress));
            }

            var end = address + alignedSize;
            var regionEnd = region.VirtualAddress + region.Size;
            if (end < regionEnd)
            {
                InsertRegionSorted(CloneRegionRange(region, end, regionEnd - end));
            }

            return true;
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    private void ReleaseUntrackedAllocation(ulong address)
    {
        _gate.EnterWriteLock();
        try
        {
            for (var i = 0; i < _regions.Count; i++)
            {
                if (_regions[i].VirtualAddress == address)
                {
                    _regions.RemoveAt(i);
                    break;
                }
            }
        }
        finally
        {
            _gate.ExitWriteLock();
        }

        _hostMemory.Free(address);
    }

    public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
    {
        address = 0;
        if (size == 0 || alignment == 0 || (alignment & (alignment - 1)) != 0)
        {
            return false;
        }

        lock (_guestAllocationGate)
        {
            if (_guestAllocationArenaBase == 0)
            {
                try
                {
                    _guestAllocationArenaBase = AllocateAt(
                        GuestAllocationArenaAddress,
                        GuestAllocationArenaSize,
                        executable: false,
                        allowAlternative: true);
                    _guestAllocationFreeRanges.Add(
                        GuestAllocationArenaStartOffset,
                        GuestAllocationArenaSize - GuestAllocationArenaStartOffset);
                }
                catch (Exception)
                {
                    return false;
                }
            }

            ulong rangeOffset = 0;
            ulong rangeSize = 0;
            ulong alignedOffset = 0;
            var found = false;
            foreach (var range in _guestAllocationFreeRanges)
            {
                alignedOffset = AlignUp(range.Key, alignment);
                if (alignedOffset >= range.Key &&
                    alignedOffset - range.Key <= range.Value &&
                    size <= range.Value - (alignedOffset - range.Key))
                {
                    rangeOffset = range.Key;
                    rangeSize = range.Value;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }

            _guestAllocationFreeRanges.Remove(rangeOffset);
            if (alignedOffset > rangeOffset)
            {
                _guestAllocationFreeRanges.Add(rangeOffset, alignedOffset - rangeOffset);
            }

            var allocationEnd = alignedOffset + size;
            var rangeEnd = rangeOffset + rangeSize;
            if (allocationEnd < rangeEnd)
            {
                _guestAllocationFreeRanges.Add(allocationEnd, rangeEnd - allocationEnd);
            }

            address = _guestAllocationArenaBase + alignedOffset;
            _guestAllocations.Add(address, (alignedOffset, size));
            return true;
        }
    }

    public bool TryFreeGuestMemory(ulong address)
    {
        lock (_guestAllocationGate)
        {
            if (!_guestAllocations.Remove(address, out var allocation))
            {
                return false;
            }

            var freeOffset = allocation.Offset;
            var freeSize = allocation.Size;
            ulong? previousOffset = null;
            ulong? nextOffset = null;

            foreach (var range in _guestAllocationFreeRanges)
            {
                if (range.Key < freeOffset)
                {
                    previousOffset = range.Key;
                    continue;
                }

                nextOffset = range.Key;
                break;
            }

            if (previousOffset is { } previous &&
                previous + _guestAllocationFreeRanges[previous] == freeOffset)
            {
                freeOffset = previous;
                freeSize += _guestAllocationFreeRanges[previous];
                _guestAllocationFreeRanges.Remove(previous);
            }

            if (nextOffset is { } next && freeOffset + freeSize == next)
            {
                freeSize += _guestAllocationFreeRanges[next];
                _guestAllocationFreeRanges.Remove(next);
            }

            _guestAllocationFreeRanges.Add(freeOffset, freeSize);
            return true;
        }
    }

    public bool TryProtect(ulong address, ulong size, GuestPageProtection protection)
    {
        if (size == 0)
        {
            return false;
        }

        return _hostMemory.Protect(address, size, ResolveProtection(protection), out _);
    }

    // Reproduces the decomposition KernelMemoryCompatExports.ResolveHostProtection
    // performed before this seam existed; the Windows backend maps each case back
    // to the identical PAGE_* value.
    private static HostPageProtection ResolveProtection(GuestPageProtection protection)
    {
        var read = (protection & GuestPageProtection.Read) != 0;
        var write = (protection & GuestPageProtection.Write) != 0;
        var execute = (protection & GuestPageProtection.Execute) != 0;

        if (execute)
        {
            return write
                ? HostPageProtection.ReadWriteExecute
                : read
                    ? HostPageProtection.ReadExecute
                    : HostPageProtection.Execute;
        }

        return write
            ? HostPageProtection.ReadWrite
            : read
                ? HostPageProtection.ReadOnly
                : HostPageProtection.NoAccess;
    }

    public void Clear()
    {
        lock (_guestAllocationGate)
        {
            _gate.EnterWriteLock();
            try
            {
                foreach (var mapping in _directMappings.Values)
                {
                    _hostMemory.UnmapSharedMemory(mapping.VirtualAddress, mapping.Size);
                }

                foreach (var region in _regions)
                {
                    _hostMemory.Free(region.VirtualAddress);
                }
                _regions.Clear();
                _directMappings.Clear();
                _pageProtections.Clear();
                lock (_allocationSearchHintGate)
                {
                    _allocationSearchHints.Clear();
                }
            }
            finally
            {
                _gate.ExitWriteLock();
            }

            _guestAllocationArenaBase = 0;
            _guestAllocationFreeRanges.Clear();
            _guestAllocations.Clear();
        }

        lock (_directMemoryBackingGate)
        {
            _directMemoryBacking?.Dispose();
            _directMemoryBacking = null;
            _directMemoryBackingSize = 0;
        }
    }

    public void Map(ulong virtualAddress, ulong memorySize, ulong fileOffset, ReadOnlySpan<byte> fileData, ProgramHeaderFlags protection)
    {
        if (memorySize == 0)
            throw new ArgumentOutOfRangeException(nameof(memorySize));

        if ((ulong)fileData.Length > memorySize)
            throw new ArgumentOutOfRangeException(nameof(fileData), "File size cannot exceed memory size");

        var mapStart = AlignDown(virtualAddress, PageSize);
        var segmentEnd = checked(virtualAddress + memorySize);
        var mapEnd = AlignUp(segmentEnd, PageSize);
        var mapSize = checked(mapEnd - mapStart);

        _gate.EnterWriteLock();
        try
        {
            var existingRegion = FindRegion(mapStart, mapSize);
            if (existingRegion == null)
            {
                var isExecutable = (protection & ProgramHeaderFlags.Execute) != 0;
                AllocateAt(mapStart, mapSize, isExecutable, allowAlternative: false);
            }

            var stageProtection = (protection & ProgramHeaderFlags.Execute) != 0
                ? ProgramHeaderFlags.Read | ProgramHeaderFlags.Write | ProgramHeaderFlags.Execute
                : ProgramHeaderFlags.Read | ProgramHeaderFlags.Write;
            SetProtection(mapStart, mapSize, stageProtection);

            if (!fileData.IsEmpty)
            {
                var destPtr = (void*)virtualAddress;
                fixed (byte* srcPtr = fileData)
                {
                    Buffer.MemoryCopy(srcPtr, destPtr, (nuint)memorySize, (nuint)fileData.Length);
                }
            }

            var zeroFillSize = memorySize - (ulong)fileData.Length;
            if (zeroFillSize != 0)
            {
                NativeMemory.Clear((void*)(virtualAddress + (ulong)fileData.Length), (nuint)zeroFillSize);
            }

            ApplySegmentProtection(mapStart, mapEnd, protection);

            TraceVmem($"Mapped segment: 0x{virtualAddress:X16} - 0x{virtualAddress + memorySize:X16} (file: {fileData.Length} bytes, prot: {protection})");
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    private void ApplySegmentProtection(ulong mapStart, ulong mapEnd, ProgramHeaderFlags flags)
    {
        var runStart = mapStart;
        var runFlags = ProgramHeaderFlags.None;
        var hasRun = false;

        for (var pageAddress = mapStart; pageAddress < mapEnd; pageAddress += PageSize)
        {
            _pageProtections.TryGetValue(pageAddress, out var existingFlags);
            var mergedFlags = existingFlags | flags;
            _pageProtections[pageAddress] = mergedFlags;

            if (!hasRun)
            {
                runStart = pageAddress;
                runFlags = mergedFlags;
                hasRun = true;
            }
            else if (mergedFlags != runFlags)
            {
                SetProtection(runStart, pageAddress - runStart, runFlags);
                runStart = pageAddress;
                runFlags = mergedFlags;
            }
        }

        if (hasRun)
        {
            SetProtection(runStart, mapEnd - runStart, runFlags);
        }
    }

    private void SetProtection(ulong address, ulong size, ProgramHeaderFlags flags)
    {
        HostPageProtection protection;

        if (flags == ProgramHeaderFlags.None)
        {
            protection = HostPageProtection.NoAccess;
        }
        else if ((flags & ProgramHeaderFlags.Execute) != 0)
        {
            protection = (flags & ProgramHeaderFlags.Write) != 0
                ? HostPageProtection.ReadWriteExecute
                : HostPageProtection.ReadExecute;
        }
        else if ((flags & ProgramHeaderFlags.Write) != 0)
        {
            protection = HostPageProtection.ReadWrite;
        }
        else
        {
            protection = HostPageProtection.ReadOnly;
        }

        if (!_hostMemory.Protect(address, size, protection, out _))
        {
            throw new InvalidOperationException($"Failed to set memory protection at 0x{address:X16}");
        }

        if ((flags & ProgramHeaderFlags.Execute) != 0)
        {
            _hostMemory.FlushInstructionCache(address, size);
        }
    }

    public IReadOnlyList<VirtualMemoryRegion> SnapshotRegions()
    {
        _gate.EnterReadLock();
        try
        {
            var snapshot = new List<VirtualMemoryRegion>(_regions.Count + _directMappings.Count);
            foreach (var region in _regions)
            {
                if (region.IsGuestReservation)
                {
                    continue;
                }

                snapshot.Add(new VirtualMemoryRegion(
                    region.VirtualAddress,
                    region.Size,
                    0,
                    region.Size,
                    region.IsExecutable
                        ? ProgramHeaderFlags.Execute | ProgramHeaderFlags.Read
                        : ProgramHeaderFlags.Read));
            }

            foreach (var mapping in _directMappings.Values)
            {
                var containingRegion = FindRegion(mapping.VirtualAddress, mapping.Size);
                if (containingRegion?.IsGuestReservation != true)
                {
                    continue;
                }

                snapshot.Add(new VirtualMemoryRegion(
                    mapping.VirtualAddress,
                    mapping.Size,
                    0,
                    mapping.Size,
                    ProgramHeaderFlags.Read));
            }

            return snapshot
                .OrderBy(static region => region.VirtualAddress)
                .ToArray();
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    public bool TryRead(ulong virtualAddress, Span<byte> destination)
    {
        var requiresExclusiveAccess = false;
        _gate.EnterReadLock();
        try
        {
            var length = (ulong)destination.Length;
            if (TryResolveRegionRun(virtualAddress, length, out var firstIndex, out var regionCount))
            {
                if (destination.IsEmpty)
                {
                    return true;
                }

                if (!TryCommitRegionRun(virtualAddress, length, firstIndex, regionCount))
                {
                    return false;
                }

                if (!CanAccessRegionRunWithoutProtectionChange(
                        virtualAddress,
                        length,
                        firstIndex,
                        regionCount,
                        write: false))
                {
                    requiresExclusiveAccess = true;
                }
                else
                {
                    fixed (byte* destPtr = destination)
                    {
                        Buffer.MemoryCopy((void*)virtualAddress, destPtr, (nuint)destination.Length, (nuint)destination.Length);
                    }

                    return true;
                }
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }

        if (!requiresExclusiveAccess)
        {
            return false;
        }

        _gate.EnterWriteLock();
        try
        {
            return TryReadExclusive(virtualAddress, destination);
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    public bool TryCompare(ulong virtualAddress, ReadOnlySpan<byte> expected)
    {
        _gate.EnterReadLock();
        try
        {
            var length = (ulong)expected.Length;
            if (!TryResolveRegionRun(virtualAddress, length, out var firstIndex, out var regionCount))
            {
                return false;
            }

            if (expected.IsEmpty)
            {
                return true;
            }

            if (!TryCommitRegionRun(virtualAddress, length, firstIndex, regionCount))
            {
                return false;
            }

            if (!CanAccessRegionRunWithoutProtectionChange(
                    virtualAddress,
                    length,
                    firstIndex,
                    regionCount,
                    write: false))
            {
                return false;
            }

            return new ReadOnlySpan<byte>((void*)virtualAddress, expected.Length).SequenceEqual(expected);
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
    {
        var requiresExclusiveAccess = false;
        _gate.EnterReadLock();
        try
        {
            var length = (ulong)source.Length;
            if (TryResolveRegionRun(virtualAddress, length, out var firstIndex, out var regionCount))
            {
                if (source.IsEmpty)
                {
                    return true;
                }

                if (!TryCommitRegionRun(virtualAddress, length, firstIndex, regionCount))
                {
                    return false;
                }

                if (!CanAccessRegionRunWithoutProtectionChange(
                        virtualAddress,
                        length,
                        firstIndex,
                        regionCount,
                        write: true))
                {
                    requiresExclusiveAccess = true;
                }
                else
                {
                    fixed (byte* srcPtr = source)
                    {
                        Buffer.MemoryCopy(srcPtr, (void*)virtualAddress, (nuint)source.Length, (nuint)source.Length);
                    }

                    return true;
                }
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }

        if (!requiresExclusiveAccess)
        {
            return false;
        }

        _gate.EnterWriteLock();
        try
        {
            return TryWriteExclusive(virtualAddress, source);
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    private bool TryReadExclusive(ulong virtualAddress, Span<byte> destination)
    {
        var length = (ulong)destination.Length;
        if (!TryResolveRegionRun(virtualAddress, length, out var firstIndex, out var regionCount))
        {
            return false;
        }

        var cursor = virtualAddress;
        var end = virtualAddress + length;
        var touchedPages = new List<(ulong Address, uint Protection)>();
        try
        {
            for (var index = firstIndex; index < firstIndex + regionCount; index++)
            {
                var region = _regions[index];
                var chunkSize = RegionChunkSize(region, cursor, end);
                if (!EnsureRangeCommitted(cursor, chunkSize, region))
                {
                    return false;
                }

                if (!CanReadWithoutProtectionChange(cursor, chunkSize, region))
                {
                    if (!TryTemporarilyProtectForRead(cursor, chunkSize, region, out var chunkPages))
                    {
                        return false;
                    }

                    touchedPages.AddRange(chunkPages);
                }

                cursor += chunkSize;
            }

            fixed (byte* destPtr = destination)
            {
                Buffer.MemoryCopy((void*)virtualAddress, destPtr, (nuint)destination.Length, (nuint)destination.Length);
            }

            return true;
        }
        finally
        {
            RestorePageProtections(touchedPages);
        }
    }

    private bool TryWriteExclusive(ulong virtualAddress, ReadOnlySpan<byte> source)
    {
        var length = (ulong)source.Length;
        if (!TryResolveRegionRun(virtualAddress, length, out var firstIndex, out var regionCount))
        {
            return false;
        }

        var cursor = virtualAddress;
        var end = virtualAddress + length;
        var protectedChunks = new List<(ulong Address, ulong Size, uint Protection)>();
        try
        {
            for (var index = firstIndex; index < firstIndex + regionCount; index++)
            {
                var region = _regions[index];
                var chunkSize = RegionChunkSize(region, cursor, end);
                if (!EnsureRangeCommitted(cursor, chunkSize, region))
                {
                    return false;
                }

                if (!CanWriteWithoutProtectionChange(cursor, chunkSize, region))
                {
                    if (!_hostMemory.Protect(cursor, chunkSize, HostPageProtection.ReadWriteExecute, out var oldProtect))
                    {
                        return false;
                    }

                    protectedChunks.Add((cursor, chunkSize, oldProtect));
                }

                cursor += chunkSize;
            }

            fixed (byte* srcPtr = source)
            {
                Buffer.MemoryCopy(srcPtr, (void*)virtualAddress, (nuint)source.Length, (nuint)source.Length);
            }

            return true;
        }
        finally
        {
            foreach (var (chunkAddress, chunkSize, protection) in protectedChunks)
            {
                _hostMemory.ProtectRaw(chunkAddress, chunkSize, protection, out _);
                if (IsExecutableProtection(protection))
                {
                    _hostMemory.FlushInstructionCache(chunkAddress, chunkSize);
                }
            }
        }
    }

    public bool TryWriteUInt64(ulong virtualAddress, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BitConverter.TryWriteBytes(buffer, value);
        return TryWrite(virtualAddress, buffer);
    }

    public void* GetPointer(ulong virtualAddress)
    {
        _gate.EnterReadLock();
        try
        {
            var region = FindRegion(virtualAddress, 1);
            if (region is null ||
                (region.IsReservedOnly && !EnsureRangeCommitted(virtualAddress, 1, region)))
            {
                return null;
            }

            return (void*)virtualAddress;
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    public bool IsAccessible(ulong virtualAddress, ulong size)
    {
        _gate.EnterReadLock();
        try
        {
            return TryResolveRegionRun(virtualAddress, size, out _, out _);
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    private MemoryRegion? FindRegion(ulong address, ulong size)
    {
        var low = 0;
        var high = _regions.Count - 1;
        MemoryRegion? candidate = null;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            var region = _regions[middle];
            if (region.VirtualAddress <= address)
            {
                candidate = region;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return candidate is not null &&
            TryResolveRegionOffset(address, size, candidate, out _)
                ? candidate
                : null;
    }

    /// <summary>
    /// Resolves the run of adjacent regions that covers
    /// <c>[address, address + size)</c> without gaps. Guest-contiguous memory
    /// is frequently assembled from many separate mappings, so accessors must
    /// not require a single containing region.
    /// </summary>
    private bool TryResolveRegionRun(ulong address, ulong size, out int firstIndex, out int regionCount)
    {
        firstIndex = 0;
        regionCount = 0;
        if (ulong.MaxValue - address < size)
        {
            return false;
        }

        var low = 0;
        var high = _regions.Count - 1;
        var candidate = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            if (_regions[middle].VirtualAddress <= address)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (candidate < 0)
        {
            return false;
        }

        var cursor = address;
        var end = address + size;
        for (var index = candidate; index < _regions.Count; index++)
        {
            var region = _regions[index];
            if (region.VirtualAddress > cursor)
            {
                return false;
            }

            var offset = cursor - region.VirtualAddress;
            if (offset > region.Size)
            {
                return false;
            }

            if (region.Size - offset >= end - cursor)
            {
                firstIndex = candidate;
                regionCount = index - candidate + 1;
                return true;
            }

            cursor += region.Size - offset;
        }

        return false;
    }

    private static ulong RegionChunkSize(MemoryRegion region, ulong cursor, ulong end)
    {
        var available = region.Size - (cursor - region.VirtualAddress);
        var remaining = end - cursor;
        return available < remaining ? available : remaining;
    }

    private bool TryCommitRegionRun(ulong address, ulong size, int firstIndex, int regionCount)
    {
        var cursor = address;
        var end = address + size;
        for (var index = firstIndex; index < firstIndex + regionCount; index++)
        {
            var region = _regions[index];
            var chunkSize = RegionChunkSize(region, cursor, end);
            if (region.IsReservedOnly && !EnsureRangeCommitted(cursor, chunkSize, region))
            {
                return false;
            }

            cursor += chunkSize;
        }

        return true;
    }

    private bool CanAccessRegionRunWithoutProtectionChange(
        ulong address,
        ulong size,
        int firstIndex,
        int regionCount,
        bool write)
    {
        var cursor = address;
        var end = address + size;
        for (var index = firstIndex; index < firstIndex + regionCount; index++)
        {
            var region = _regions[index];
            var chunkSize = RegionChunkSize(region, cursor, end);
            if (!CanAccessWithoutProtectionChange(cursor, chunkSize, region, write))
            {
                return false;
            }

            cursor += chunkSize;
        }

        return true;
    }

    private void InsertRegionSorted(MemoryRegion region)
    {
        var low = 0;
        var high = _regions.Count;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (_regions[middle].VirtualAddress < region.VirtualAddress)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        _regions.Insert(low, region);
    }

    private bool TryGetOverlappingRegionEnd(ulong address, ulong size, out ulong overlapEnd)
    {
        overlapEnd = 0;
        if (size == 0 || ulong.MaxValue - address < size - 1)
        {
            return false;
        }

        var end = address + size;
        _gate.EnterReadLock();
        try
        {
            foreach (var region in _regions)
            {
                var regionEnd = region.VirtualAddress + region.Size;
                if (region.VirtualAddress >= end)
                {
                    break;
                }

                if (regionEnd <= address)
                {
                    continue;
                }

                if (address < regionEnd && region.VirtualAddress < end)
                {
                    overlapEnd = Math.Max(overlapEnd, regionEnd);
                }
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }

        return overlapEnd != 0;
    }

    private ulong GetAllocationSearchCursor(
        ulong desiredAddress,
        ulong requestedCursor,
        ulong alignment,
        bool executable)
    {
        lock (_allocationSearchHintGate)
        {
            var key = (desiredAddress, alignment, executable);
            if (_allocationSearchHints.TryGetValue(key, out var hintedCursor) &&
                hintedCursor > requestedCursor)
            {
                return AlignUp(hintedCursor, alignment);
            }
        }

        return requestedCursor;
    }

    private void UpdateAllocationSearchCursor(
        ulong desiredAddress,
        ulong alignment,
        bool executable,
        ulong nextCursor)
    {
        lock (_allocationSearchHintGate)
        {
            _allocationSearchHints[(desiredAddress, alignment, executable)] = AlignUp(nextCursor, alignment);
        }
    }

    private static bool TryResolveRegionOffset(ulong address, ulong size, MemoryRegion region, out ulong offset)
    {
        offset = 0;
        if (address < region.VirtualAddress)
        {
            return false;
        }

        offset = address - region.VirtualAddress;
        if (offset > region.Size)
        {
            return false;
        }

        if (size > region.Size - offset)
        {
            return false;
        }

        return true;
    }

    private static bool IsExecutableProtection(uint protection)
    {
        return protection is PAGE_EXECUTE or PAGE_EXECUTE_READ or PAGE_EXECUTE_READWRITE or PAGE_EXECUTE_WRITECOPY;
    }

    private bool CanReadWithoutProtectionChange(ulong address, ulong size, MemoryRegion region) =>
        CanAccessWithoutProtectionChange(address, size, region, write: false);

    private bool CanWriteWithoutProtectionChange(ulong address, ulong size, MemoryRegion region) =>
        CanAccessWithoutProtectionChange(address, size, region, write: true);

    private bool CanAccessWithoutProtectionChange(ulong address, ulong size, MemoryRegion region, bool write)
    {
        var startPage = AlignDown(address, PageSize);
        var endPage = AlignUp(address + size, PageSize);
        for (var pageAddress = startPage; pageAddress < endPage; pageAddress += PageSize)
        {
            if (_pageProtections.TryGetValue(pageAddress, out var flags))
            {
                if (write ? (flags & ProgramHeaderFlags.Write) == 0 : (flags & ProgramHeaderFlags.Read) == 0)
                {
                    return false;
                }
            }
            else if (write ? !IsWritableProtection(region.Protection) : !IsReadableProtection(region.Protection))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsReadableProtection(uint protection)
    {
        return protection is PAGE_READONLY or PAGE_READWRITE or PAGE_EXECUTE_READ or PAGE_EXECUTE_READWRITE;
    }

    private static bool IsWritableProtection(uint protection)
    {
        return protection is PAGE_READWRITE or PAGE_EXECUTE_READWRITE;
    }

    private static HostPageProtection GetCommitProtection(MemoryRegion region)
    {
        return region.IsExecutable ? HostPageProtection.ReadWriteExecute : HostPageProtection.ReadWrite;
    }

    private bool EnsureRangeCommitted(ulong address, ulong size, MemoryRegion region)
    {
        if (size == 0)
        {
            return true;
        }

        if (region.IsGuestReservation)
        {
            return IsDirectMappedRange(address, size);
        }

        if (!region.IsReservedOnly)
        {
            return true;
        }

        var startPage = AlignDown(address, PageSize);
        var endPage = AlignUp(address + size, PageSize);
        var commitProtection = GetCommitProtection(region);

        var pageAddress = startPage;
        while (pageAddress < endPage)
        {
            if (!_hostMemory.Query(pageAddress, out var info))
            {
                return false;
            }

            var queriedEnd = info.RegionSize > ulong.MaxValue - info.BaseAddress
                ? ulong.MaxValue
                : info.BaseAddress + info.RegionSize;
            var rangeEnd = Math.Min(endPage, queriedEnd);
            if (rangeEnd <= pageAddress)
            {
                return false;
            }

            if (info.State == HostRegionState.Committed)
            {
                pageAddress = rangeEnd;
                continue;
            }

            if (info.State != HostRegionState.Reserved)
            {
                return false;
            }

            var commitSize = rangeEnd - pageAddress;
            if (!_hostMemory.Commit(pageAddress, commitSize, commitProtection))
            {
                return false;
            }

            pageAddress = rangeEnd;
        }

        return true;
    }

    private bool IsDirectMappedRange(ulong address, ulong size)
    {
        if (size == 0 || ulong.MaxValue - address < size)
        {
            return false;
        }

        // Reservations are populated by individually mapped views, so the
        // range may be covered by several adjacent mappings rather than one.
        var end = address + size;
        var cursor = address;
        while (cursor < end)
        {
            var next = cursor;
            foreach (var mapping in _directMappings.Values)
            {
                if (mapping.VirtualAddress > cursor ||
                    cursor - mapping.VirtualAddress >= mapping.Size)
                {
                    continue;
                }

                var reach = mapping.VirtualAddress + mapping.Size;
                if (reach > next)
                {
                    next = reach < end ? reach : end;
                }
            }

            if (next == cursor)
            {
                return false;
            }

            cursor = next;
        }

        return true;
    }

    private static MemoryRegion CloneRegionRange(MemoryRegion source, ulong address, ulong size) =>
        new()
        {
            VirtualAddress = address,
            Size = size,
            IsExecutable = source.IsExecutable,
            IsReservedOnly = source.IsReservedOnly,
            IsGuestReservation = source.IsGuestReservation,
            Protection = source.Protection,
        };

    private bool TryTemporarilyProtectForRead(
        ulong address,
        ulong size,
        MemoryRegion region,
        out List<(ulong Address, uint Protection)> touchedPages)
    {
        touchedPages = new List<(ulong Address, uint Protection)>();

        var startPage = AlignDown(address, PageSize);
        var endPage = AlignUp(address + size, PageSize);
        var temporaryProtection = region.IsExecutable ? HostPageProtection.ReadWriteExecute : HostPageProtection.ReadWrite;

        for (var pageAddress = startPage; pageAddress < endPage; pageAddress += PageSize)
        {
            if (!_hostMemory.Protect(pageAddress, PageSize, temporaryProtection, out var oldProtection))
            {
                RestorePageProtections(touchedPages);
                touchedPages.Clear();
                return false;
            }

            touchedPages.Add((pageAddress, oldProtection));
        }

        return true;
    }

    private void RestorePageProtections(List<(ulong Address, uint Protection)> touchedPages)
    {
        foreach (var (pageAddress, protection) in touchedPages)
        {
            _hostMemory.ProtectRaw(pageAddress, PageSize, protection, out _);
        }
    }

    private static ulong AlignDown(ulong value, ulong alignment)
    {
        var mask = alignment - 1;
        return value & ~mask;
    }

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        var mask = alignment - 1;
        return checked((value + mask) & ~mask);
    }

    private static ulong AlignUpToMultiple(ulong value, ulong alignment)
    {
        var remainder = value % alignment;
        return remainder == 0
            ? value
            : checked(value + alignment - remainder);
    }

    private static ulong ResolveLazyReservePrimeBytes()
    {
        var configured = Environment.GetEnvironmentVariable("SHARPEMU_LAZY_RESERVE_PRIME_MB");
        if (ulong.TryParse(configured, out var megabytes))
        {
            return megabytes == 0
                ? 0
                : checked(Math.Min(megabytes, 4096UL) * 1024UL * 1024UL);
        }

        return DefaultLazyReservePrimeBytes;
    }

    private static void TraceVmem(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_VMEM"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Log.Debug(message);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Clear();
            _disposed = true;
        }
    }

    private class MemoryRegion
    {
        public ulong VirtualAddress { get; set; }
        public ulong Size { get; set; }
        public bool IsExecutable { get; set; }
        public bool IsReservedOnly { get; set; }
        public bool IsGuestReservation { get; set; }
        public uint Protection { get; set; }
    }

    private readonly record struct DirectMapping(
        ulong VirtualAddress,
        ulong Size,
        ulong PhysicalOffset,
        HostPageProtection Protection);

}
