// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using SharpEmu.Libs.Bink;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using System.Linq;
using System.Globalization;

namespace SharpEmu.Libs.Kernel;

public static partial class KernelMemoryCompatExports
{
    private const int MaxGuestStringLength = 4096;
    private const int WideCharSize = sizeof(ushort);
    private const int MemsetChunkSize = 16 * 1024;
    private static readonly byte[] _zeroChunk = new byte[MemsetChunkSize];
    private const int O_WRONLY = 0x1;
    private const int O_RDWR = 0x2;
    private const int O_APPEND = 0x8;
    private const int O_CREAT = 0x0200;
    private const int O_TRUNC = 0x0400;
    private const int O_DIRECTORY = 0x00020000;
    private const int OrbisKernelMapFixed = 0x0010;
    private const int OrbisKernelMapOpMapDirect = 0;
    private const int OrbisKernelMapOpUnmap = 1;
    private const int OrbisKernelMapOpProtect = 2;
    private const int OrbisKernelMapOpMapFlexible = 3;
    private const int OrbisKernelMapOpTypeProtect = 4;
    private const int OrbisKernelBatchMapEntrySize = 32;
    private const int OrbisKernelBatchMapEntryStartOffset = 0;
    private const int OrbisKernelBatchMapEntryOffsetOffset = 8;
    private const int OrbisKernelBatchMapEntryLengthOffset = 16;
    private const int OrbisKernelBatchMapEntryProtectionOffset = 24;
    private const int OrbisKernelBatchMapEntryTypeOffset = 25;
    private const int OrbisKernelBatchMapEntryOperationOffset = 28;
    private const ulong OrbisPageSize = 0x4000;
    private const int OrbisProtCpuRead = 0x01;
    private const int OrbisProtCpuWrite = 0x02;
    private const int OrbisProtCpuExec = 0x04;
    private const int OrbisProtGpuRead = 0x10;
    private const int OrbisProtGpuWrite = 0x20;
    private const int OrbisProtCpuReadWrite = OrbisProtCpuRead | OrbisProtCpuWrite;
    private const int SeekSet = 0;
    private const int SeekCur = 1;
    private const int SeekEnd = 2;
    private const ulong DirectMemorySizeBytes = 16384UL * 1024 * 1024;
    private const ulong UnsetMainDirectMemoryPoolBase = ulong.MaxValue;
    private const ulong FlexibleMemorySizeBytes = 448UL * 1024 * 1024;
    private const int OrbisVirtualQueryInfoSize = 72;
    private const int OrbisKernelMaximumNameLength = 32;
    private const uint MemCommit = 0x1000;
    private const uint HostPageNoAccess = 0x01;
    private const uint HostPageReadOnly = 0x02;
    private const uint HostPageReadWrite = 0x04;
    private const uint HostPageWriteCopy = 0x08;
    private const uint HostPageExecute = 0x10;
    private const uint HostPageExecuteRead = 0x20;
    private const uint HostPageExecuteReadWrite = 0x40;
    private const uint HostPageExecuteWriteCopy = 0x80;
    private const uint HostPageGuard = 0x100;
    private const int Enoent = 2;
    private const int Ebadf = 9;
    private const int Enomem = 12;
    private const int Eacces = 13;
    private const int Efault = 14;
    private const int Einval = 22;
    private const int Erange = 34;
    private const int Struncate = 80;
    private const nuint DefaultLibcHeapAlignment = 16;
    private const ushort KernelStatModeDirectory = 0x41FF;
    private const ushort KernelStatModeRegular = 0x81FF;
    private const int KernelStatSize = 120;
    private const int KernelStatStDevOffset = 0;
    private const int KernelStatStInoOffset = 4;
    private const int KernelStatStModeOffset = 8;
    private const int KernelStatStNlinkOffset = 10;
    private const int KernelStatStUidOffset = 12;
    private const int KernelStatStGidOffset = 16;
    private const int KernelStatStRdevOffset = 20;
    private const int KernelStatStAtimOffset = 24;
    private const int KernelStatStMtimOffset = 40;
    private const int KernelStatStCtimOffset = 56;
    private const int KernelStatStSizeOffset = 72;
    private const int KernelStatStBlocksOffset = 80;
    private const int KernelStatStBlksizeOffset = 88;
    private const int KernelStatStFlagsOffset = 92;
    private const int KernelStatStGenOffset = 96;
    private const int KernelStatStLspareOffset = 100;
    private const int KernelStatStBirthtimOffset = 104;

    private static readonly object _fdGate = new();
    private static readonly Dictionary<int, FileStream> _openFiles = new();
    private static readonly Dictionary<int, Bink2MovieBridge.BinkGuestCompletionShim>
        _binkGuestCompletionShims = new();
    private static readonly Dictionary<int, string> _observedBinkGuestFiles = new();
    private static readonly Dictionary<int, OpenDirectory> _openDirectories = new();
    private static readonly object _libcAllocGate = new();
    private static readonly object _memoryGate = new();
    private static readonly object _ioTraceGate = new();
    private static readonly object _statCacheGate = new();
    private static readonly object _guestMountGate = new();
    private static readonly Dictionary<ulong, DirectAllocation> _directAllocations = new();
    private static readonly Dictionary<ulong, LibcHeapAllocation> _libcAllocations = new();
    // Keyed by (and kept sorted on) region base address so VirtualQuery can find a
    // containing/next region with a binary search instead of an O(n) scan. Every
    // write uses the region's own Address as the key (see AddMappedRegionSliceLocked
    // and the mmap sites), so Values enumerate in ascending address order.
    private static readonly SortedList<ulong, MappedRegion> _mappedRegions = new();
    private static readonly Dictionary<ulong, string> _mappedRegionNames = new();
    private static readonly Dictionary<string, string> _guestMounts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _tracedStatResults = new(StringComparer.Ordinal);
    // Both caches memoize host filesystem probe outcomes, so their key
    // equivalence must match the host filesystem's: Windows resolves names
    // case-insensitively, but Linux hosts are case-sensitive, and an
    // ignore-case cache there aliases distinct paths — a cached miss for
    // "/app0/DATA.BIN" keeps answering NOT_FOUND for "/app0/Data.bin" even
    // though that file exists and a fresh probe would find it.
    private static readonly StringComparer HostFsPathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison HostFsPathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly HashSet<string> _negativeStatCache = new(HostFsPathComparer);
    private static readonly ConcurrentDictionary<string, ulong> _aprFileSizeCache = new(HostFsPathComparer);
    private static long _nextFileDescriptor = 2;

    internal static int AllocateGuestFileDescriptor()
    {
        lock (_fdGate)
        {
            return (int)Interlocked.Increment(ref _nextFileDescriptor);
        }
    }

    private static ulong _nextPhysicalAddress;
    private static ulong _nextVirtualAddress;
    // First guest virtual address handed out for direct/flexible mappings
    // when the game does not request one. 4GB is free on Windows, but on
    // POSIX hosts it belongs to the host image / runtime (the Mach-O image
    // base is 0x100000000 on macOS), so search from a guest-owned window
    // well clear of host mappings instead.
    private static readonly ulong DefaultMapSearchBase =
        OperatingSystem.IsWindows() ? 0x1_0000_0000UL : 0x20_0000_0000UL;
    private static ulong _mainDirectMemoryPoolBase = UnsetMainDirectMemoryPoolBase;
    private static ulong _allocatedFlexibleBytes;
    private static ulong _threadAtexitCountCallback;
    private static ulong _threadAtexitReportCallback;
    private static ulong _threadDtorsCallback;
    private static int _nullMemsetRecoveryCount;
    private static int _nonCanonicalMemsetRecoveryCount;
    private static int _inaccessibleMemsetRecoveryCount;
    private static int _hostMemoryWriteFallbackCount;
    private static int _hostMemoryReadFallbackCount;
    private static int _nullWcscpyRecoveryCount;
    private static int _nullStrcasecmpRecoveryCount;
    private static string? _cachedApp0Root;
    private static string? _cachedDownload0Root;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    private static unsafe nuint VirtualQuery(nint lpAddress, out MemoryBasicInformation lpBuffer, nuint dwLength)
    {
        _ = dwLength;
        var result = HostMemory.Query((void*)lpAddress, out var info);
        lpBuffer = default;
        lpBuffer.BaseAddress = (nint)info.BaseAddress;
        lpBuffer.AllocationBase = (nint)info.AllocationBase;
        lpBuffer.AllocationProtect = info.AllocationProtect;
        lpBuffer.RegionSize = (nuint)info.RegionSize;
        lpBuffer.State = info.State;
        lpBuffer.Protect = info.Protect;
        lpBuffer.Type = info.Type;
        return result;
    }

    private static unsafe bool VirtualProtect(nint lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect) =>
        HostMemory.Protect((void*)lpAddress, dwSize, flNewProtect, out lpflOldProtect);

    private sealed class OpenDirectory
    {
        public required string Path { get; init; }
        public required string[] Entries { get; init; }
        public int NextIndex { get; set; }
    }

    private readonly record struct DirectAllocation(ulong Start, ulong Length, int MemoryType);
    private readonly record struct LibcHeapAllocation(nint BaseAddress, nuint Size, nuint Alignment);
    private readonly record struct MappedRegion(ulong Address, ulong Length, int Protection, bool IsFlexible, bool IsDirect, ulong DirectStart);
    private readonly record struct BatchMapEntry(ulong Start, ulong Offset, ulong Length, byte Protection, byte Type, int Operation);

    public static void RegisterGuestPathMount(string guestMountPoint, string hostRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guestMountPoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostRoot);

        var normalizedMountPoint = NormalizeGuestStatCachePath(guestMountPoint);
        if (normalizedMountPoint is null || normalizedMountPoint == "/")
        {
            throw new ArgumentException("Guest mount point must name a directory.", nameof(guestMountPoint));
        }

        var normalizedHostRoot = Path.GetFullPath(hostRoot);
        Directory.CreateDirectory(normalizedHostRoot);
        lock (_guestMountGate)
        {
            _guestMounts[normalizedMountPoint] = normalizedHostRoot;
        }

        lock (_statCacheGate)
        {
            _negativeStatCache.RemoveWhere(path =>
                string.Equals(path, normalizedMountPoint, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(normalizedMountPoint + "/", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Removes a guest mount registered by <see cref="RegisterGuestPathMount"/>.</summary>
    public static bool UnregisterGuestPathMount(string guestMountPoint)
    {
        var normalizedMountPoint = NormalizeGuestStatCachePath(guestMountPoint);
        if (normalizedMountPoint is null)
        {
            return false;
        }

        lock (_guestMountGate)
        {
            return _guestMounts.Remove(normalizedMountPoint);
        }
    }

    internal static bool TryAllocateHleData(
        CpuContext ctx,
        ulong length,
        ulong alignment,
        out ulong address)
    {
        address = 0;
        if (length == 0 || length > int.MaxValue)
        {
            return false;
        }

        var mappedLength = AlignUp(length, 0x1000UL);
        var effectiveAlignment = Math.Max(alignment, 0x1000UL);
        lock (_memoryGate)
        {
            var desiredAddress = AlignUp(
                _nextVirtualAddress == 0 ? DefaultMapSearchBase : _nextVirtualAddress,
                effectiveAlignment);
            if (!TryReserveGuestVirtualRange(ctx, desiredAddress, mappedLength, OrbisProtCpuReadWrite, effectiveAlignment, out address) ||
                address == 0)
            {
                return false;
            }

            _nextVirtualAddress = Math.Max(_nextVirtualAddress, address + mappedLength);
            ReplaceMappedRegionRangeLocked(new MappedRegion(
                address,
                mappedLength,
                OrbisProtCpuReadWrite,
                IsFlexible: false,
                IsDirect: false,
                DirectStart: 0));
        }

        for (ulong offset = 0; offset < mappedLength;)
        {
            var chunkLength = (int)Math.Min((ulong)_zeroChunk.Length, mappedLength - offset);
            if (!ctx.Memory.TryWrite(address + offset, _zeroChunk.AsSpan(0, chunkLength)))
            {
                return false;
            }

            offset += (ulong)chunkLength;
        }

        return true;
    }

    internal static void RegisterReservedVirtualRange(ulong address, ulong length)
    {
        if (address == 0 || length == 0)
        {
            return;
        }

        lock (_memoryGate)
        {
            ReplaceMappedRegionRangeLocked(new MappedRegion(
                address,
                length,
                Protection: 0,
                IsFlexible: false,
                IsDirect: false,
                DirectStart: 0));
        }
    }

    [SysAbiExport(
        Nid = "8zTFvBIAIN8",
        ExportName = "memset",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Memset(CpuContext ctx)
    {
        var destination = ctx[CpuRegister.Rdi];
        var value = (byte)(ctx[CpuRegister.Rsi] & 0xFF);
        var length = ctx[CpuRegister.Rdx];
        if (length == 0)
        {
            ctx[CpuRegister.Rax] = destination;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (destination == 0)
        {
            if (length <= 0x20)
            {
                var recoveryIndex = Interlocked.Increment(ref _nullMemsetRecoveryCount);
                if (recoveryIndex <= 8)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARNING] memset null-dst recovery#{recoveryIndex}: rip=0x{ctx.Rip:X16} len=0x{length:X} val=0x{value:X2}");
                }

                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        const ulong CanonicalUserUpper = 0x0000800000000000UL;
        if (destination >= CanonicalUserUpper && length <= 0x40)
        {
            var recoveryIndex = Interlocked.Increment(ref _nonCanonicalMemsetRecoveryCount);
            if (recoveryIndex <= 8)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARNING] memset non-canonical-dst recovery#{recoveryIndex}: rip=0x{ctx.Rip:X16} dst=0x{destination:X16} len=0x{length:X} val=0x{value:X2}");
            }

            ctx[CpuRegister.Rax] = destination;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        // Only obviously-bogus destinations or absurd sizes are rejected. Large
        // but plausible clears (engines zero multi-hundred-MB resource pools in
        // one call) are allowed; the write loop below clamps to the mapped range
        // if the request runs off the end of a region instead of hard-faulting.
        const ulong MaxSane = 2UL * 1024 * 1024 * 1024;
        if (destination < 0x1000 || destination >= CanonicalUserUpper || length > MaxSane)
        {
            Console.WriteLine("!!! CRITICAL: Bad Memset Call !!!");
            Console.WriteLine($"Called from RIP: 0x{ctx.Rip:X}");
            Console.WriteLine($"dst=0x{destination:X} val=0x{value:X2} len=0x{length:X}");
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        // Rent may hand back a larger array than requested; only the first chunkLength
        // bytes are filled, so the loop must cap at chunkLength rather than chunk.Length.
        var chunkLength = (int)Math.Min(length, (ulong)MemsetChunkSize);
        var chunk = value == 0 ? _zeroChunk : ArrayPool<byte>.Shared.Rent(chunkLength);
        if (value != 0)
        {
            chunk.AsSpan(0, chunkLength).Fill(value);
        }

        try
        {
            var remaining = length;
            var cursor = destination;
            while (remaining > 0)
            {
                var take = (int)Math.Min((ulong)chunkLength, remaining);
                if (!TryWriteCompat(ctx, cursor, chunk.AsSpan(0, take)))
                {
                    // Clamp oversized clears to the valid mapped prefix. Small
                    // inaccessible writes are tolerated for compatibility with
                    // titles that probe optional state during startup.
                    if (length <= 0x40)
                    {
                        var recoveryIndex = Interlocked.Increment(ref _inaccessibleMemsetRecoveryCount);
                        if (recoveryIndex <= 8)
                        {
                            Console.Error.WriteLine(
                                $"[LOADER][WARNING] memset inaccessible-dst recovery#{recoveryIndex}: rip=0x{ctx.Rip:X16} dst=0x{destination:X16} len=0x{length:X} val=0x{value:X2}");
                        }

                        ctx[CpuRegister.Rax] = destination;
                        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
                    }

                    var clampIndex = Interlocked.Increment(ref _inaccessibleMemsetRecoveryCount);
                    if (clampIndex <= 8)
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][WARNING] memset clamped to mapped range#{clampIndex}: rip=0x{ctx.Rip:X16} " +
                            $"dst=0x{destination:X16} len=0x{length:X} written=0x{cursor - destination:X} val=0x{value:X2}");
                    }

                    ctx[CpuRegister.Rax] = destination;
                    return (int)OrbisGen2Result.ORBIS_GEN2_OK;
                }

                cursor += (ulong)take;
                remaining -= (ulong)take;
            }
        }
        finally
        {
            if (value != 0)
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }
        }

        ctx[CpuRegister.Rax] = destination;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "j4ViWNHEgww",
        ExportName = "strlen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Strlen(CpuContext ctx)
    {
        if (!TryReadCString(ctx, ctx[CpuRegister.Rdi], 1_048_576, out var bytes))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)bytes.Length);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "5jNubw4vlAA",
        ExportName = "strnlen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Strnlen(CpuContext ctx)
    {
        var maxLength = ctx[CpuRegister.Rsi];
        if (!TryReadCString(ctx, ctx[CpuRegister.Rdi], maxLength, out var bytes))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)bytes.Length);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "LHMrG7e8G78",
        ExportName = "wcsmisc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Wcslen(CpuContext ctx)
    {
        return WcslenCore(ctx, ctx[CpuRegister.Rdi]);
    }

    [SysAbiExport(
        Nid = "WkkeywLJcgU",
        ExportName = "wcslen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int WcslenWkkey(CpuContext ctx)
    {
        return WcslenCore(ctx, ctx[CpuRegister.Rdi]);
    }

    private static int WcslenCore(CpuContext ctx, ulong address)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_WIDE"), "1", StringComparison.Ordinal))
        {
            Span<byte> probe = stackalloc byte[32];
            if (TryReadCompat(ctx, address, probe))
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] wcslen probe @0x{address:X16}: {Convert.ToHexString(probe).ToLowerInvariant()}");
            }
            else
            {
                Console.Error.WriteLine($"[LOADER][TRACE] wcslen probe @0x{address:X16}: <unreadable>");
            }
        }

        if (!TryReadWideCString(ctx, address, 1_048_576, out var units))
        {
            Console.Error.WriteLine($"[LOADER][WARN] wcslen: unreadable string at 0x{address:X16}");
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)units.Length);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Ovb2dSJOAuE",
        ExportName = "strcmp",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Strcmp(CpuContext ctx)
    {
        var left = ctx[CpuRegister.Rdi];
        var right = ctx[CpuRegister.Rsi];
        if (!TryCompareStrings(ctx, left, right, limit: ulong.MaxValue, out var compare))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)compare);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "fV2xHER+bKE",
        ExportName = "wcscoll",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Wcscoll(CpuContext ctx)
    {
        return WcscollCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi]);
    }

    [SysAbiExport(
        Nid = "pNtJdE3x49E",
        ExportName = "wcscmp",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Wcscmp(CpuContext ctx)
    {
        return WcscmpCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi]);
    }

    private static int WcscollCore(CpuContext ctx, ulong left, ulong right)
    {
        return WcscmpCore(ctx, left, right);
    }

    private static int WcscmpCore(CpuContext ctx, ulong left, ulong right)
    {
        if (!TryCompareWideStrings(ctx, left, right, limit: ulong.MaxValue, out var compare))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)compare);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "FM5NPnLqBc8",
        ExportName = "wcscpy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int WcscpyFm5(CpuContext ctx)
    {
        return WcscpyCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi]);
    }

    private static int WcscpyCore(CpuContext ctx, ulong destination, ulong source)
    {
        if (source == 0)
        {
            var recoveryIndex = Interlocked.Increment(ref _nullWcscpyRecoveryCount);
            if (recoveryIndex <= 8)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARNING] wcscpy null-src recovery#{recoveryIndex}: rip=0x{ctx.Rip:X16} dst=0x{destination:X16}");
            }

            if (!TryWriteWideTerminator(ctx, destination))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            ctx[CpuRegister.Rax] = destination;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (!TryReadWideCString(ctx, source, 1_048_576, out var units))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (!TryWriteCompat(ctx, destination, EncodeWideUnitsWithTerminator(units)))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = destination;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "aesyjrHVWy4",
        ExportName = "strncmp",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Strncmp(CpuContext ctx)
    {
        var left = ctx[CpuRegister.Rdi];
        var right = ctx[CpuRegister.Rsi];
        var limit = ctx[CpuRegister.Rdx];
        if (!TryCompareStrings(ctx, left, right, limit, out var compare))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)compare);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "0nV21JjYCH8",
        ExportName = "wcsncpy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Wcsncpy(CpuContext ctx)
    {
        return WcsncpyCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx]);
    }

    [SysAbiExport(
        Nid = "E8wCoUEbfzk",
        ExportName = "wcsncmp",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Wcsncmp(CpuContext ctx)
    {
        return WcsncmpCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx]);
    }

    private static int WcsncmpCore(CpuContext ctx, ulong left, ulong right, ulong limit)
    {
        if (!TryCompareWideStrings(ctx, left, right, limit, out var compare))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)compare);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "eLdDw6l0-bU",
        ExportName = "snprintf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Snprintf(CpuContext ctx)
    {
        return SnprintfCore(ctx);
    }

    [SysAbiExport(
        Nid = "Q2V+iqvjgC0",
        ExportName = "vsnprintf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Vsnprintf(CpuContext ctx)
    {
        return VsnprintfCore(ctx);
    }

    [SysAbiExport(
        Nid = "nJz16JE1txM",
        ExportName = "swprintf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Swprintf(CpuContext ctx)
    {
        return SwprintfCore(ctx);
    }

    [SysAbiExport(
        Nid = "u0XOsuOmOzc",
        ExportName = "vswprintf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Vswprintf(CpuContext ctx)
    {
        return VswprintfCore(ctx);
    }

    [SysAbiExport(
        Nid = "Im55VJ-Bekc",
        ExportName = "swprintf_s",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int SwprintfS(CpuContext ctx)
    {
        return SwprintfCore(ctx);
    }

    [SysAbiExport(
        Nid = "oDoV9tyHTbA",
        ExportName = "vswprintf_s",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int VswprintfS(CpuContext ctx)
    {
        return VswprintfCore(ctx);
    }

    [SysAbiExport(
        Nid = "GMpvxPFW924",
        ExportName = "vprintf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Vprintf(CpuContext ctx)
    {
        var formatAddress = ctx[CpuRegister.Rdi];
        var vaListAddress = ctx[CpuRegister.Rsi];
        if (!TryReadCString(ctx, formatAddress, 1_048_576, out var formatBytes))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var format = Encoding.UTF8.GetString(formatBytes);
        string rendered;
        if (!TryCreateVaListCursor(ctx, vaListAddress, out var vaCursor))
        {
            rendered = format;
        }
        else
        {
            rendered = FormatString(ctx, format, ref vaCursor);
        }

        Console.Write(rendered);
        ctx[CpuRegister.Rax] = unchecked((ulong)Encoding.UTF8.GetByteCount(rendered));
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "kiZSXIWd9vg",
        ExportName = "strcpy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Strcpy(CpuContext ctx)
    {
        var destination = ctx[CpuRegister.Rdi];
        var source = ctx[CpuRegister.Rsi];
        if (!TryReadCString(ctx, source, 1_048_576, out var bytes))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var payload = new byte[bytes.Length + 1];
        bytes.CopyTo(payload.AsSpan());
        if (!TryWriteCompat(ctx, destination, payload))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = destination;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "6f5f-qx4ucA",
        ExportName = "wcscpy_s",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int WcscpyS(CpuContext ctx)
    {
        return WcscpySCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx]);
    }

    [SysAbiExport(
        Nid = "6sJWiWSRuqk",
        ExportName = "strncpy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Strncpy(CpuContext ctx)
    {
        var destination = ctx[CpuRegister.Rdi];
        var source = ctx[CpuRegister.Rsi];
        var count = (int)Math.Min(ctx[CpuRegister.Rdx], int.MaxValue);
        if (count < 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var payload = new byte[count];
        Span<byte> one = stackalloc byte[1];
        var copied = 0;
        while (copied < count)
        {
            if (!TryReadCompat(ctx, source + (ulong)copied, one))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            payload[copied] = one[0];
            copied++;
            if (one[0] == 0)
            {
                break;
            }
        }

        if (!TryWriteCompat(ctx, destination, payload))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = destination;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Slmz4HMpNGs",
        ExportName = "wcsncpy_s",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int WcsncpyS(CpuContext ctx)
    {
        return WcsncpySCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx], ctx[CpuRegister.Rcx]);
    }

    private static int WcsncpyCore(CpuContext ctx, ulong destination, ulong source, ulong countValue)
    {
        var count = (int)Math.Min(countValue, int.MaxValue);
        if (count < 0 || count > (int.MaxValue / WideCharSize))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var payload = new byte[count * WideCharSize];
        if (count == 0)
        {
            ctx[CpuRegister.Rax] = destination;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        // Keep host-pointer reads page-bounded and copy several UTF-16 code
        // units per validation. Large scratch strings otherwise pay the host
        // address-validation and temporary-buffer cost once per character.
        const int maxReadBytes = 4096;
        var readBuffer = GC.AllocateUninitializedArray<byte>(
            Math.Min(maxReadBytes, payload.Length));
        var copied = 0;
        while (copied < count)
        {
            var sourceAddress = source + ((ulong)copied * WideCharSize);
            if (sourceAddress < source)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            var pageBytesRemaining = maxReadBytes -
                (int)(sourceAddress & (maxReadBytes - 1));
            var remainingBytes = (count - copied) * WideCharSize;
            var readBytes = Math.Min(
                readBuffer.Length,
                Math.Min(pageBytesRemaining, remainingBytes));
            readBytes &= ~(WideCharSize - 1);
            if (readBytes == 0 ||
                !TryReadCompat(ctx, sourceAddress, readBuffer.AsSpan(0, readBytes)))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            for (var offset = 0; offset < readBytes; offset += WideCharSize)
            {
                var unit = BinaryPrimitives.ReadUInt16LittleEndian(
                    readBuffer.AsSpan(offset, WideCharSize));
                if (unit == 0)
                {
                    // payload is zero-initialized, supplying wcsncpy padding.
                    copied = count;
                    break;
                }

                BinaryPrimitives.WriteUInt16LittleEndian(
                    payload.AsSpan((copied * WideCharSize) + offset, WideCharSize),
                    unit);
            }

            if (copied != count)
            {
                copied += readBytes / WideCharSize;
            }
        }

        if (!TryWriteCompat(ctx, destination, payload))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = destination;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Ezzq78ZgHPs",
        ExportName = "wcschr",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Wcschr(CpuContext ctx)
    {
        return WcschrCore(ctx, ctx[CpuRegister.Rdi], unchecked((ushort)ctx[CpuRegister.Rsi]));
    }

    private static int WcschrCore(CpuContext ctx, ulong address, ushort needle)
    {
        const int maxReadBytes = 4096;
        var readBuffer = GC.AllocateUninitializedArray<byte>(maxReadBytes);
        const ulong maxUnits = 1_048_576;
        for (ulong index = 0; index < maxUnits;)
        {
            var unitAddress = address + (index * WideCharSize);
            if (unitAddress < address)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            var remainingBytes = (maxUnits - index) * WideCharSize;
            var pageBytesRemaining = maxReadBytes -
                (int)(unitAddress & (maxReadBytes - 1));
            var readBytes = (int)Math.Min(
                (ulong)Math.Min(readBuffer.Length, pageBytesRemaining),
                remainingBytes);
            readBytes &= ~(WideCharSize - 1);
            if (readBytes == 0 ||
                !TryReadCompat(ctx, unitAddress, readBuffer.AsSpan(0, readBytes)))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            for (var offset = 0; offset < readBytes; offset += WideCharSize)
            {
                var unit = BinaryPrimitives.ReadUInt16LittleEndian(
                    readBuffer.AsSpan(offset, WideCharSize));
                if (unit == needle)
                {
                    ctx[CpuRegister.Rax] = unitAddress + (ulong)offset;
                    return (int)OrbisGen2Result.ORBIS_GEN2_OK;
                }

                if (unit == 0)
                {
                    ctx[CpuRegister.Rax] = 0;
                    return (int)OrbisGen2Result.ORBIS_GEN2_OK;
                }
            }

            index += (ulong)(readBytes / WideCharSize);
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int WcscpySCore(CpuContext ctx, ulong destination, ulong destinationCount, ulong source)
    {
        if (destination == 0 || destinationCount == 0)
        {
            ctx[CpuRegister.Rax] = Einval;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (source == 0)
        {
            if (!TryZeroWideDestination(ctx, destination, destinationCount))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            ctx[CpuRegister.Rax] = Einval;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var probeLimit = Math.Min(destinationCount, 1_048_576UL);
        if (!TryReadWideCStringBounded(ctx, source, probeLimit, out var units, out var terminated))
        {
            _ = TryZeroWideDestination(ctx, destination, destinationCount);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (!terminated || (ulong)units.Length + 1 > destinationCount)
        {
            if (!TryZeroWideDestination(ctx, destination, destinationCount))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            ctx[CpuRegister.Rax] = Erange;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (!TryWriteCompat(ctx, destination, EncodeWideUnitsWithTerminator(units)))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int WcsncpySCore(CpuContext ctx, ulong destination, ulong destinationCount, ulong source, ulong count)
    {
        if (destination == 0 || destinationCount == 0)
        {
            ctx[CpuRegister.Rax] = Einval;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (source == 0)
        {
            if (!TryZeroWideDestination(ctx, destination, destinationCount))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            ctx[CpuRegister.Rax] = Einval;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (count == 0)
        {
            if (!TryWriteWideTerminator(ctx, destination))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (count == ulong.MaxValue)
        {
            var copyLimit = Math.Min(destinationCount - 1, 1_048_576UL);
            if (!TryReadWideCStringBounded(ctx, source, copyLimit, out var truncatedUnits, out var terminated))
            {
                _ = TryZeroWideDestination(ctx, destination, destinationCount);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            if (!TryWriteCompat(ctx, destination, EncodeWideUnitsWithTerminator(truncatedUnits)))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            ctx[CpuRegister.Rax] = terminated ? 0UL : Struncate;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var boundedCount = Math.Min(count, 1_048_576UL);
        if (!TryReadWideCStringBounded(ctx, source, boundedCount, out var units, out var sourceTerminated))
        {
            _ = TryZeroWideDestination(ctx, destination, destinationCount);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var requiredUnits = sourceTerminated ? (ulong)units.Length + 1 : boundedCount + 1;
        if (requiredUnits > destinationCount)
        {
            if (!TryZeroWideDestination(ctx, destination, destinationCount))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            ctx[CpuRegister.Rax] = Erange;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (!TryWriteCompat(ctx, destination, EncodeWideUnitsWithTerminator(units)))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Q3VBxCXhUHs",
        ExportName = "memcpy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Memcpy(CpuContext ctx)
    {
        var destination = ctx[CpuRegister.Rdi];
        var source = ctx[CpuRegister.Rsi];
        var count = (int)Math.Min(ctx[CpuRegister.Rdx], int.MaxValue);
        if (count < 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (count > 0 && !ctx.Memory.TryCopy(destination, source, (ulong)count))
        {
            var payload = GC.AllocateUninitializedArray<byte>(count);
            if (!TryReadCompat(ctx, source, payload) || !TryWriteCompat(ctx, destination, payload))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }
        }

        ctx[CpuRegister.Rax] = destination;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "+P6FRGH4LfA",
        ExportName = "memmove",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Memmove(CpuContext ctx)
    {
        return Memcpy(ctx);
    }

    [SysAbiExport(
        Nid = "gQX+4GDQjpM",
        ExportName = "malloc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Malloc(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] =
            TryAllocateLibcHeap(ctx[CpuRegister.Rdi], DefaultLibcHeapAlignment, zeroFill: false, out var address)
                ? address
                : 0;
        TraceLibcAllocation(
            ctx,
            "malloc",
            size: ctx[CpuRegister.Rdi],
            alignment: DefaultLibcHeapAlignment,
            resultAddress: ctx[CpuRegister.Rax]);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "tIhsqj0qsFE",
        ExportName = "free",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Free(CpuContext ctx)
    {
        FreeLibcHeap(ctx[CpuRegister.Rdi]);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "2X5agFjKxMc",
        ExportName = "calloc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Calloc(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] =
            TryMultiplyAllocationSize(ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], out var totalSize) &&
            TryAllocateLibcHeapCore(totalSize, DefaultLibcHeapAlignment, zeroFill: true, out var address)
                ? address
                : 0;
        TraceLibcAllocation(
            ctx,
            "calloc",
            size: ctx[CpuRegister.Rdi],
            count: ctx[CpuRegister.Rsi],
            alignment: DefaultLibcHeapAlignment,
            resultAddress: ctx[CpuRegister.Rax]);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Y7aJ1uydPMo",
        ExportName = "realloc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Realloc(CpuContext ctx)
    {
        var existingAddress = ctx[CpuRegister.Rdi];
        var requestedSize = ctx[CpuRegister.Rsi];

        if (existingAddress == 0)
        {
            ctx[CpuRegister.Rax] =
                TryAllocateLibcHeap(requestedSize, DefaultLibcHeapAlignment, zeroFill: false, out var freshAddress)
                    ? freshAddress
                    : 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (requestedSize == 0)
        {
            FreeLibcHeap(existingAddress);
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        ctx[CpuRegister.Rax] =
            TryReallocateLibcHeap(existingAddress, requestedSize, out var resizedAddress)
                ? resizedAddress
                : 0;
        TraceLibcAllocation(
            ctx,
            "realloc",
            size: requestedSize,
            existingAddress: existingAddress,
            resultAddress: ctx[CpuRegister.Rax]);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Ujf3KzMvRmI",
        ExportName = "memalign",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Memalign(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] =
            TryAllocateAlignedLibcHeap(
                alignmentValue: ctx[CpuRegister.Rdi],
                requestedSize: ctx[CpuRegister.Rsi],
                requireSizeMultiple: false,
                out var address)
                ? address
                : 0;
        TraceLibcAllocation(
            ctx,
            "memalign",
            size: ctx[CpuRegister.Rsi],
            alignment: ctx[CpuRegister.Rdi],
            resultAddress: ctx[CpuRegister.Rax]);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "2Btkg8k24Zg",
        ExportName = "aligned_alloc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int AlignedAlloc(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] =
            TryAllocateAlignedLibcHeap(
                alignmentValue: ctx[CpuRegister.Rdi],
                requestedSize: ctx[CpuRegister.Rsi],
                requireSizeMultiple: true,
                out var address)
                ? address
                : 0;
        TraceLibcAllocation(
            ctx,
            "aligned_alloc",
            size: ctx[CpuRegister.Rsi],
            alignment: ctx[CpuRegister.Rdi],
            resultAddress: ctx[CpuRegister.Rax]);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "cVSk9y8URbc",
        ExportName = "posix_memalign",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int PosixMemalign(CpuContext ctx)
    {
        var outPointerAddress = ctx[CpuRegister.Rdi];
        if (outPointerAddress == 0)
        {
            ctx[CpuRegister.Rax] = Einval;
            TraceLibcAllocation(
                ctx,
                "posix_memalign",
                size: ctx[CpuRegister.Rdx],
                alignment: ctx[CpuRegister.Rsi],
                existingAddress: outPointerAddress,
                resultAddress: 0,
                errorCode: Einval);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (!TryValidateAlignedAllocation(
                ctx[CpuRegister.Rsi],
                ctx[CpuRegister.Rdx],
                requireSizeMultiple: false,
                requirePointerSizedAlignment: true,
                out var alignment,
                out var requestedSize))
        {
            _ = TryWriteUInt64Compat(ctx, outPointerAddress, 0);
            ctx[CpuRegister.Rax] = Einval;
            TraceLibcAllocation(
                ctx,
                "posix_memalign",
                size: ctx[CpuRegister.Rdx],
                alignment: ctx[CpuRegister.Rsi],
                existingAddress: outPointerAddress,
                resultAddress: 0,
                errorCode: Einval);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (!TryAllocateLibcHeapCore(requestedSize, alignment, zeroFill: false, out var address))
        {
            _ = TryWriteUInt64Compat(ctx, outPointerAddress, 0);
            ctx[CpuRegister.Rax] = Enomem;
            TraceLibcAllocation(
                ctx,
                "posix_memalign",
                size: requestedSize,
                alignment: alignment,
                existingAddress: outPointerAddress,
                resultAddress: 0,
                errorCode: Enomem);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (!TryWriteUInt64Compat(ctx, outPointerAddress, address))
        {
            FreeLibcHeap(address);
            ctx[CpuRegister.Rax] = Einval;
            TraceLibcAllocation(
                ctx,
                "posix_memalign",
                size: requestedSize,
                alignment: alignment,
                existingAddress: outPointerAddress,
                resultAddress: 0,
                errorCode: Einval);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        ctx[CpuRegister.Rax] = 0;
        TraceLibcAllocation(
            ctx,
            "posix_memalign",
            size: requestedSize,
            alignment: alignment,
            existingAddress: outPointerAddress,
            resultAddress: address);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "DfivPArhucg",
        ExportName = "memcmp",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Memcmp(CpuContext ctx)
    {
        var left = ctx[CpuRegister.Rdi];
        var right = ctx[CpuRegister.Rsi];
        var count = (int)Math.Min(ctx[CpuRegister.Rdx], int.MaxValue);
        if (count < 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        Span<byte> leftByte = stackalloc byte[1];
        Span<byte> rightByte = stackalloc byte[1];
        for (var i = 0; i < count; i++)
        {
            if (!TryReadCompat(ctx, left + (ulong)i, leftByte) ||
                !TryReadCompat(ctx, right + (ulong)i, rightByte))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            var diff = leftByte[0] - rightByte[0];
            if (diff != 0)
            {
                ctx[CpuRegister.Rax] = unchecked((ulong)diff);
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "QrZZdJ8XsX0",
        ExportName = "fputs",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Fputs(CpuContext ctx)
    {
        var textAddress = ctx[CpuRegister.Rdi];
        var stream = ctx[CpuRegister.Rsi];
        if (textAddress == 0)
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)(-1L));
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryReadNullTerminatedUtf8(ctx, textAddress, MaxGuestStringLength, out var text))
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)(-1L));
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (stream == 0)
        {
            Console.Error.Write(text);
            Console.Error.Flush();
        }
        else
        {
            Console.Out.Write(text);
            Console.Out.Flush();
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)text.Length);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "6c3rCVE-fTU",
        ExportName = "_open",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelOpenUnderscore(CpuContext ctx)
    {
        var pathAddress = ctx[CpuRegister.Rdi];
        var flags = unchecked((int)ctx[CpuRegister.Rsi]);
        // Not migratable to [GuestCString]: the local reader's TryReadCompat host-memory
        // fallback recovers paths in loader-mapped regions that ctx.Memory cannot see.
        if (!TryReadNullTerminatedUtf8(ctx, pathAddress, MaxGuestStringLength, out var guestPath))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var hostPath = ResolveGuestPath(guestPath);
        var access = ResolveOpenAccess(flags);
        var mode = ResolveOpenMode(flags, access);
        // A denied path (empty host path) must not reach FileStream, which would
        // throw an ArgumentException the catch below does not cover.
        if (string.IsNullOrEmpty(hostPath))
        {
            LogOpenTrace($"_open denied path='{guestPath}' flags=0x{flags:X8}");
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }
        try
        {
            if (Bink2MovieBridge.ShouldSkipGuestMovie(hostPath))
            {
                LogOpenTrace(
                    "_open bink-skip path='" + guestPath + "' host='" + hostPath +
                    "' flags=0x" + flags.ToString("X8"));
                Console.Error.WriteLine(
                    "[LOADER][INFO] Skipping Bink movie without a decoder: " +
                    Path.GetFileName(hostPath));
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            Bink2MovieBridge.BinkGuestCompletionShim binkCompletionShim = default;
            var observedBinkMovie = false;
            var useBinkCompletionShim = access == FileAccess.Read &&
                Bink2MovieBridge.TryTakeOverGuestMovie(
                    hostPath,
                    out binkCompletionShim,
                    out observedBinkMovie);

            if (IsMutatingOpen(flags) && IsReadOnlyGuestMutationPath(guestPath))
            {
                LogOpenTrace($"_open readonly path='{guestPath}' host='{hostPath}' flags=0x{flags:X8}");
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
            }

            var wantsDirectory = (flags & O_DIRECTORY) != 0;
            if (wantsDirectory || Directory.Exists(hostPath))
            {
                if (!Directory.Exists(hostPath))
                {
                    LogOpenTrace($"_open miss path='{guestPath}' host='{hostPath}' flags=0x{flags:X8} directory=1");
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
                }

                if (access != FileAccess.Read || (flags & (O_CREAT | O_TRUNC | O_APPEND)) != 0)
                {
                    LogOpenTrace($"_open invalid-dir path='{guestPath}' host='{hostPath}' flags=0x{flags:X8}");
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
                }

                var directoryFd = (int)Interlocked.Increment(ref _nextFileDescriptor);
                lock (_fdGate)
                {
                    _openDirectories[directoryFd] = new OpenDirectory
                    {
                        Path = hostPath,
                        Entries = EnumerateDirectoryEntries(hostPath),
                        NextIndex = 0
                    };
                }

                LogOpenTrace($"_open dir path='{guestPath}' host='{hostPath}' flags=0x{flags:X8} fd={directoryFd}");
                ctx[CpuRegister.Rax] = unchecked((ulong)directoryFd);
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            EnsureOpenParentDirectoryExists(guestPath, hostPath, flags);
            var stream = new FileStream(hostPath, mode, access, FileShare.ReadWrite);
            if ((flags & O_APPEND) != 0)
            {
                stream.Seek(0, SeekOrigin.End);
            }

            var fd = (int)Interlocked.Increment(ref _nextFileDescriptor);
            lock (_fdGate)
            {
                _openFiles[fd] = stream;
                if (useBinkCompletionShim)
                {
                    _binkGuestCompletionShims[fd] = binkCompletionShim;
                }
                if (observedBinkMovie)
                {
                    _observedBinkGuestFiles[fd] = hostPath;
                }
            }

            if (useBinkCompletionShim)
            {
                LogOpenTrace(
                    "_open bink-host-shim path='" + guestPath + "' host='" + hostPath +
                    "' flags=0x" + flags.ToString("X8") + " fd=" + fd);
            }

            if (IsMutatingOpen(flags))
            {
                InvalidateNegativeStatCacheForPathAndAncestors(guestPath);
                InvalidateAprFileSizeCache(hostPath);
            }

            LogOpenTrace($"_open file path='{guestPath}' host='{hostPath}' flags=0x{flags:X8} fd={fd}");
            ctx[CpuRegister.Rax] = unchecked((ulong)fd);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogOpenTrace($"_open fail path='{guestPath}' host='{hostPath}' flags=0x{flags:X8} ex={ex.GetType().Name}: {ex.Message}");
            return ex is UnauthorizedAccessException
                ? (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT
                : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }
    }

    [SysAbiExport(
        Nid = "NNtFaKJbPt0",
        ExportName = "_close",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelCloseUnderscore(CpuContext ctx) => KernelCloseCore(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(
        Nid = "bY-PO6JhzhQ",
        ExportName = "close",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixClose(CpuContext ctx)
    {
        var result = KernelCloseCore(ctx, unchecked((int)ctx[CpuRegister.Rdi]));
        return result == (int)OrbisGen2Result.ORBIS_GEN2_OK
            ? 0
            : PosixFailure(ctx, result, notFoundErrno: Ebadf);
    }

    [SysAbiExport(
        Nid = "UK2Tl2DWUns",
        ExportName = "sceKernelClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelClose(CpuContext ctx) => KernelCloseCore(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(
        Nid = "eV9wAD2riIA",
        ExportName = "sceKernelStat",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelStat(CpuContext ctx)
    {
        var pathAddress = ctx[CpuRegister.Rdi];
        var statAddress = ctx[CpuRegister.Rsi];
        if (pathAddress == 0 || statAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryReadNullTerminatedUtf8(ctx, pathAddress, MaxGuestStringLength, out var guestPath))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var hostPath = ResolveGuestPath(guestPath);
        var statCacheKey = GetNegativeStatCacheKey(guestPath);
        if (statCacheKey is not null && IsNegativeStatCached(statCacheKey))
        {
            LogUniqueStatTrace(guestPath, hostPath, found: false);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        if (!TryWriteHostPathStat(ctx, statAddress, hostPath))
        {
            if (statCacheKey is not null)
            {
                AddNegativeStatCache(statCacheKey);
            }

            LogUniqueStatTrace(guestPath, hostPath, found: false);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        if (statCacheKey is not null)
        {
            RemoveNegativeStatCache(statCacheKey);
        }

        LogUniqueStatTrace(guestPath, hostPath, found: true);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Translates a failed raw Orbis kernel result into the libc/POSIX ABI:
    // return -1 with errno set. The raw sceKernel* implementations report the
    // 0x8002xxxx sentinel through the return value, but the POSIX-named exports
    // (open/fstat/close/read/write/stat) are called by libc code that expects a
    // negative result on failure. Leaking the raw sentinel makes callers store
    // it as a "valid" fd or handle and later dereference it - the null-pointer
    // access violation seen when Unity's IL2CPP file layer probes an absent
    // il2cpp.usym. fd-based calls pass notFoundErrno=Ebadf; path-based calls
    // leave the Enoent default.
    private static int PosixFailure(CpuContext ctx, int orbisResult, int notFoundErrno = Enoent)
    {
        var errno = orbisResult switch
        {
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT => Einval,
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT => Efault,
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED => Eacces,
            _ => notFoundErrno,
        };
        KernelRuntimeCompatExports.TrySetErrno(ctx, errno);
        ctx[CpuRegister.Rax] = ulong.MaxValue;
        return -1;
    }

    [SysAbiExport(
        Nid = "E6ao34wPw+U",
        ExportName = "stat",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int PosixStat(CpuContext ctx)
    {
        var result = KernelStat(ctx);
        return result == (int)OrbisGen2Result.ORBIS_GEN2_OK
            ? 0
            : PosixFailure(ctx, result);
    }

    // POSIX open(2): translates a failed raw open into -1/errno. On success
    // KernelOpenUnderscore already writes the fd into RAX (the import bridge
    // prefers a written RAX over the return value), so returning 0 is correct.
    public static int PosixOpen(CpuContext ctx)
    {
        var result = KernelOpenUnderscore(ctx);
        return result == (int)OrbisGen2Result.ORBIS_GEN2_OK
            ? 0
            : PosixFailure(ctx, result);
    }

    // POSIX fstat(2): a bad fd maps to EBADF rather than the path-oriented ENOENT.
    public static int PosixFstat(CpuContext ctx)
    {
        var result = KernelFstat(ctx);
        return result == (int)OrbisGen2Result.ORBIS_GEN2_OK
            ? 0
            : PosixFailure(ctx, result, notFoundErrno: Ebadf);
    }

    [SysAbiExport(
        Nid = "gEpBkcwxUjw",
        ExportName = "sceKernelAprResolveFilepathsToIdsAndFileSizes",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAprResolveFilepathsToIdsAndFileSizes(CpuContext ctx)
    {
        var pathListAddress = ctx[CpuRegister.Rdi];
        var count = ctx[CpuRegister.Rsi];
        var idsAddress = ctx[CpuRegister.Rdx];
        var sizesAddress = ctx[CpuRegister.Rcx];
        var errorIndexAddress = ctx[CpuRegister.R8];
        if (pathListAddress == 0 || count == 0 || sizesAddress == 0 || count > 1024)
        {
            KernelRuntimeCompatExports.TrySetErrno(ctx, Einval);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        for (ulong i = 0; i < count; i++)
        {
            if (idsAddress != 0 &&
                !TryWriteUInt32Compat(ctx, idsAddress + (i * sizeof(uint)), uint.MaxValue))
            {
                KernelRuntimeCompatExports.TrySetErrno(ctx, Efault);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            if (!TryResolveAprFilepath(ctx, pathListAddress, i, out var guestPath))
            {
                KernelRuntimeCompatExports.TrySetErrno(ctx, Efault);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            var hostPath = ResolveGuestPath(guestPath);
            if (!TryGetAprFileSize(hostPath, out var fileSize))
            {
                // Stop at the first miss and report its index.
                // The caller can then use its normal file-open fallback.
                LogIoTrace("apr_resolve", guestPath, $"host='{hostPath}' index={i} count={count} result=not_found");
                if (sizesAddress != 0 &&
                    !TryWriteUInt64Compat(ctx, sizesAddress + (i * sizeof(ulong)), 0))
                {
                    KernelRuntimeCompatExports.TrySetErrno(ctx, Efault);
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                }

                if (errorIndexAddress != 0 &&
                    !TryWriteUInt32Compat(ctx, errorIndexAddress, (uint)i))
                {
                    KernelRuntimeCompatExports.TrySetErrno(ctx, Efault);
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                }

                KernelRuntimeCompatExports.TrySetErrno(ctx, 2); // ENOENT
                ctx[CpuRegister.Rax] = ulong.MaxValue;
                return -1;
            }

            var fileId = AmprFileRegistry.Register(guestPath, hostPath);
            LogIoTrace("apr_resolve", guestPath, $"host='{hostPath}' index={i} count={count} id=0x{fileId:X8} size={fileSize}");

            if (idsAddress != 0 &&
                !TryWriteUInt32Compat(ctx, idsAddress + (i * sizeof(uint)), fileId))
            {
                KernelRuntimeCompatExports.TrySetErrno(ctx, Efault);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            if (!TryWriteUInt64Compat(ctx, sizesAddress + (i * sizeof(ulong)), fileSize))
            {
                KernelRuntimeCompatExports.TrySetErrno(ctx, Efault);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // The IDs-only sibling of sceKernelAprResolveFilepathsToIdsAndFileSizes.
    // Games that stream via AMPR APR call this to turn asset paths into file
    // IDs, then hand those IDs to sceAmprAprCommandBufferReadFile. Without it
    // the paths never register in AmprFileRegistry, so every subsequent
    // ReadFile fails with NOT_FOUND and the streaming pipeline stalls forever.
    // Signature: (const char* const* paths, size_t count, uint32_t* ids).
    [SysAbiExport(
        Nid = "WT-5NKy42fw",
        ExportName = "sceKernelAprResolveFilepathsToIds",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAprResolveFilepathsToIds(CpuContext ctx)
    {
        var pathListAddress = ctx[CpuRegister.Rdi];
        var count = ctx[CpuRegister.Rsi];
        var idsAddress = ctx[CpuRegister.Rdx];
        if (pathListAddress == 0 || count == 0 || idsAddress == 0 || count > 1024)
        {
            KernelRuntimeCompatExports.TrySetErrno(ctx, Einval);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        for (ulong i = 0; i < count; i++)
        {
            if (!TryWriteUInt32Compat(ctx, idsAddress + (i * sizeof(uint)), uint.MaxValue))
            {
                KernelRuntimeCompatExports.TrySetErrno(ctx, Efault);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            if (!TryResolveAprFilepath(ctx, pathListAddress, i, out var guestPath))
            {
                KernelRuntimeCompatExports.TrySetErrno(ctx, Efault);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            var hostPath = ResolveGuestPath(guestPath);
            if (!TryGetAprFileSize(hostPath, out _))
            {
                LogIoTrace("apr_resolve_ids", guestPath, $"host='{hostPath}' index={i} count={count} result=not_found");
                KernelRuntimeCompatExports.TrySetErrno(ctx, 2);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            var fileId = AmprFileRegistry.Register(guestPath, hostPath);
            LogIoTrace("apr_resolve_ids", guestPath, $"host='{hostPath}' index={i} count={count} id=0x{fileId:X8}");

            if (!TryWriteUInt32Compat(ctx, idsAddress + (i * sizeof(uint)), fileId))
            {
                KernelRuntimeCompatExports.TrySetErrno(ctx, Efault);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Stat an AMPR APR file by the id returned from sceKernelAprResolveFilepathsToIds.
    // Games call this after resolving ids to learn each asset's size before
    // issuing the streaming read. When it is missing the guest gets no size back
    // and dereferences a null result pointer (observed SIGSEGV write to 0x0 in
    // Void Terrarium). Signature: (SceKernelAprFileId id, SceKernelStat* stat).
    [SysAbiExport(
        Nid = "ApkYaHb8Sek",
        ExportName = "sceKernelAprGetFileStat",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAprGetFileStat(CpuContext ctx)
    {
        var fileId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var statAddress = ctx[CpuRegister.Rsi];
        if (statAddress == 0)
        {
            KernelRuntimeCompatExports.TrySetErrno(ctx, Einval);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!AmprFileRegistry.TryGetHostPath(fileId, out var hostPath))
        {
            LogIoTrace("apr_get_file_stat", $"id=0x{fileId:X8}", "result=id_not_registered");
            KernelRuntimeCompatExports.TrySetErrno(ctx, 2);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        if (!TryWriteHostPathStat(ctx, statAddress, hostPath))
        {
            LogIoTrace("apr_get_file_stat", hostPath, $"id=0x{fileId:X8} result=not_found");
            KernelRuntimeCompatExports.TrySetErrno(ctx, 2);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        LogIoTrace("apr_get_file_stat", hostPath, $"id=0x{fileId:X8}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "kBwCPsYX-m4",
        ExportName = "sceKernelFstat",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelFstat(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var statAddress = ctx[CpuRegister.Rsi];
        if (statAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryWriteOpenDescriptorStat(ctx, fd, statAddress))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "AUXVxWeJU-A",
        ExportName = "sceKernelUnlink",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelUnlink(CpuContext ctx)
    {
        var pathAddress = ctx[CpuRegister.Rdi];
        if (pathAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryReadNullTerminatedUtf8(ctx, pathAddress, MaxGuestStringLength, out var guestPath))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var hostPath = ResolveGuestPath(guestPath);
        if (IsReadOnlyGuestMutationPath(guestPath))
        {
            LogOpenTrace($"unlink readonly path='{guestPath}' host='{hostPath}'");
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
        }

        try
        {
            if (Directory.Exists(hostPath))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
            }

            if (!File.Exists(hostPath))
            {
                AddNegativeStatCacheForGuestPath(guestPath);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            File.Delete(hostPath);
            InvalidateNegativeStatCacheForPathAndAncestors(guestPath);
            InvalidateAprFileSizeCache(hostPath);
            AddNegativeStatCacheForGuestPath(guestPath);
            LogOpenTrace($"unlink path='{guestPath}' host='{hostPath}'");
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }
        catch (UnauthorizedAccessException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
        }
        catch (IOException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
        }
    }

    [SysAbiExport(
        Nid = "1-LFLmRFxxM",
        ExportName = "sceKernelMkdir",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelMkdir(CpuContext ctx)
    {
        var pathAddress = ctx[CpuRegister.Rdi];
        if (pathAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryReadNullTerminatedUtf8(ctx, pathAddress, MaxGuestStringLength, out var guestPath))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var hostPath = ResolveGuestPath(guestPath);
        if (IsReadOnlyGuestMutationPath(guestPath))
        {
            LogOpenTrace($"mkdir readonly path='{guestPath}' host='{hostPath}'");
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
        }

        try
        {
            if (File.Exists(hostPath) || Directory.Exists(hostPath))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_ALREADY_EXISTS;
            }

            var parentDirectory = Path.GetDirectoryName(hostPath);
            if (string.IsNullOrWhiteSpace(parentDirectory) || !Directory.Exists(parentDirectory))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            Directory.CreateDirectory(hostPath);
            if (!Directory.Exists(hostPath))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            InvalidateNegativeStatCacheForPathAndAncestors(guestPath);
            LogOpenTrace($"mkdir path='{guestPath}' host='{hostPath}'");
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }
        catch (UnauthorizedAccessException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
        }
        catch (IOException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }
    }

    [SysAbiExport(
        Nid = "naInUjYt3so",
        ExportName = "sceKernelRmdir",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelRmdir(CpuContext ctx)
    {
        var pathAddress = ctx[CpuRegister.Rdi];
        if (pathAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryReadNullTerminatedUtf8(ctx, pathAddress, MaxGuestStringLength, out var guestPath))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var hostPath = ResolveGuestPath(guestPath);
        if (IsReadOnlyGuestMutationPath(guestPath))
        {
            LogOpenTrace($"rmdir readonly path='{guestPath}' host='{hostPath}'");
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
        }

        try
        {
            if (!Directory.Exists(hostPath))
            {
                AddNegativeStatCacheForGuestPath(guestPath);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            Directory.Delete(hostPath, recursive: false);
            InvalidateNegativeStatCacheForPathAndAncestors(guestPath);
            AddNegativeStatCacheForGuestPath(guestPath);
            LogOpenTrace($"rmdir path='{guestPath}' host='{hostPath}'");
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }
        catch (UnauthorizedAccessException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
        }
        catch (IOException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
        }
    }

    private static int KernelCloseCore(CpuContext ctx, int fd)
    {
        if (fd is 0 or 1 or 2)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (KernelSocketCompatExports.TryCloseSocketFd(fd))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        FileStream? stream;
        var notifyBinkClose = false;
        string? observedBinkPath = null;
        lock (_fdGate)
        {
            if (_openFiles.Remove(fd, out stream))
            {
                _binkGuestCompletionShims.Remove(fd);
                if (_observedBinkGuestFiles.Remove(fd, out observedBinkPath))
                {
                    notifyBinkClose = !_observedBinkGuestFiles.Values.Any(path =>
                        string.Equals(
                            path,
                            observedBinkPath,
                            StringComparison.OrdinalIgnoreCase));
                }
            }
            else if (_openDirectories.Remove(fd))
            {
                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }
            else
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }
        }

        if (notifyBinkClose)
        {
            Bink2MovieBridge.NotifyGuestMovieClosed(observedBinkPath!);
        }
        stream.Dispose();
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "DRuBt2pvICk",
        ExportName = "_read",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelReadUnderscore(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var bufferAddress = ctx[CpuRegister.Rsi];
        var requested = (int)Math.Min(ctx[CpuRegister.Rdx], int.MaxValue);
        if (requested < 0 || (requested > 0 && bufferAddress == 0))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (requested == 0 || fd == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        FileStream? stream;
        Bink2MovieBridge.BinkGuestCompletionShim completionShim = default;
        var useBinkCompletionShim = false;
        lock (_fdGate)
        {
            _openFiles.TryGetValue(fd, out stream);
            useBinkCompletionShim = _binkGuestCompletionShims.TryGetValue(fd, out completionShim);
        }

        if (stream is null)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        long positionBefore;
        try
        {
            positionBefore = stream.Position;
        }
        catch (IOException)
        {
            positionBefore = -1;
        }

        var buffer = GC.AllocateUninitializedArray<byte>(requested);
        var read = stream.Read(buffer, 0, requested);
        if (read > 0 && useBinkCompletionShim)
        {
            // The patched NumFrames field is what tells the guest "this
            // movie is fully consumed" - hold that specific read until the
            // host has actually finished showing it, so guest-side game
            // logic can't race ahead of what's still on screen.
            if (completionShim.Patch(positionBefore, buffer.AsSpan(0, read)))
            {
                Bink2MovieBridge.WaitForHostPlaybackToFinish(stream.Name);
            }
        }
        if (read > 0 && !ctx.Memory.TryWrite(bufferAddress, buffer.AsSpan(0, read)))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        long positionAfter;
        try
        {
            positionAfter = stream.Position;
        }
        catch (IOException)
        {
            positionAfter = -1;
        }

        LogIoTrace(
            "read",
            stream.Name,
            $"fd={fd} req={requested} read={read} pos={positionBefore}->{positionAfter} preview='{PreviewIoBytes(buffer, read, 64)}' hex={PreviewIoHex(buffer, read, 32)} guest_tail={PreviewGuestHex(ctx, bufferAddress + (ulong)Math.Max(read, 0), 32)}");

        ctx[CpuRegister.Rax] = unchecked((ulong)read);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "AqBioC2vF3I",
        ExportName = "read",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixRead(CpuContext ctx)
    {
        // On success KernelReadUnderscore writes the byte count into RAX, which
        // the import bridge prefers over this return value.
        var result = KernelReadUnderscore(ctx);
        return result == (int)OrbisGen2Result.ORBIS_GEN2_OK
            ? 0
            : PosixFailure(ctx, result, notFoundErrno: Ebadf);
    }

    [SysAbiExport(
        Nid = "Cg4srZ6TKbU",
        ExportName = "sceKernelRead",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelRead(CpuContext ctx) => KernelReadUnderscore(ctx);

    [SysAbiExport(
        Nid = "Oy6IpwgtYOk",
        ExportName = "lseek",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixLseek(CpuContext ctx)
    {
        var result = KernelLseekCore(
            unchecked((int)ctx[CpuRegister.Rdi]),
            unchecked((long)ctx[CpuRegister.Rsi]),
            unchecked((int)ctx[CpuRegister.Rdx]),
            out var position);

        if (result != OrbisGen2Result.ORBIS_GEN2_OK)
        {
            ctx[CpuRegister.Rax] = ulong.MaxValue;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)position);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "oib76F-12fk",
        ExportName = "sceKernelLseek",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelLseek(CpuContext ctx)
    {
        var result = KernelLseekCore(
            unchecked((int)ctx[CpuRegister.Rdi]),
            unchecked((long)ctx[CpuRegister.Rsi]),
            unchecked((int)ctx[CpuRegister.Rdx]),
            out var position);

        if (result != OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return (int)result;
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)position);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "taRWhTJFTgE",
        ExportName = "sceKernelGetdirentries",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetdirentries(CpuContext ctx)
    {
        return KernelGetdirentriesCore(
            ctx,
            unchecked((int)ctx[CpuRegister.Rdi]),
            ctx[CpuRegister.Rsi],
            unchecked((int)Math.Min(ctx[CpuRegister.Rdx], int.MaxValue)),
            ctx[CpuRegister.Rcx]);
    }

    [SysAbiExport(
        Nid = "j2AIqSqJP0w",
        ExportName = "sceKernelGetdents",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetdents(CpuContext ctx)
    {
        return KernelGetdirentriesCore(
            ctx,
            unchecked((int)ctx[CpuRegister.Rdi]),
            ctx[CpuRegister.Rsi],
            unchecked((int)Math.Min(ctx[CpuRegister.Rdx], int.MaxValue)),
            0);
    }

    private static OrbisGen2Result KernelLseekCore(int fd, long offset, int whence, out long position)
    {
        position = -1;

        FileStream? stream;
        lock (_fdGate)
        {
            _openFiles.TryGetValue(fd, out stream);
        }

        if (stream is null)
        {
            LogIoTrace("lseek", $"fd:{fd}", $"offset={offset} whence={whence} result=badfd");
            return OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        SeekOrigin origin;
        switch (whence)
        {
            case SeekSet:
                origin = SeekOrigin.Begin;
                break;
            case SeekCur:
                origin = SeekOrigin.Current;
                break;
            case SeekEnd:
                origin = SeekOrigin.End;
                break;
            default:
                LogIoTrace("lseek", stream.Name, $"fd={fd} offset={offset} whence={whence} result=invalid_whence");
                return OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        try
        {
            position = stream.Seek(offset, origin);
        }
        catch (IOException ex)
        {
            LogIoTrace("lseek", stream.Name, $"fd={fd} offset={offset} whence={whence} result=io_error ex={ex.Message}");
            return OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }
        catch (ArgumentException ex)
        {
            LogIoTrace("lseek", stream.Name, $"fd={fd} offset={offset} whence={whence} result=invalid ex={ex.Message}");
            return OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        LogIoTrace("lseek", stream.Name, $"fd={fd} offset={offset} whence={whence} pos={position}");
        return OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "FxVZqBAA7ks",
        ExportName = "_write",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelWriteUnderscore(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var bufferAddress = ctx[CpuRegister.Rsi];
        var requested = (int)Math.Min(ctx[CpuRegister.Rdx], int.MaxValue);
        if (requested < 0 || (requested > 0 && bufferAddress == 0))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var payload = requested == 0
            ? Array.Empty<byte>()
            : GC.AllocateUninitializedArray<byte>(requested);
        if (requested > 0 && !ctx.Memory.TryRead(bufferAddress, payload))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (fd == 1 || fd == 2)
        {
            var text = Encoding.UTF8.GetString(payload);
            if (fd == 1)
            {
                Console.Out.Write(text);
                Console.Out.Flush();
            }
            else
            {
                Console.Error.Write(text);
                Console.Error.Flush();
            }

            ctx[CpuRegister.Rax] = unchecked((ulong)requested);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        FileStream? stream;
        lock (_fdGate)
        {
            _openFiles.TryGetValue(fd, out stream);
        }

        if (stream is null)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        stream.Write(payload, 0, requested);
        stream.Flush();
        ctx[CpuRegister.Rax] = unchecked((ulong)requested);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "FN4gaPmuFV8",
        ExportName = "write",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixWrite(CpuContext ctx)
    {
        // On success KernelWriteUnderscore writes the byte count into RAX, which
        // the import bridge prefers over this return value.
        var result = KernelWriteUnderscore(ctx);
        return result == (int)OrbisGen2Result.ORBIS_GEN2_OK
            ? 0
            : PosixFailure(ctx, result, notFoundErrno: Ebadf);
    }

    [SysAbiExport(
        Nid = "4wSze92BhLI",
        ExportName = "sceKernelWrite",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelWrite(CpuContext ctx) => KernelWriteUnderscore(ctx);

    [SysAbiExport(
        Nid = "lLMT9vJAck0",
        ExportName = "clock_gettime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int ClockGettime(CpuContext ctx)
    {
        var timespecAddress = ctx[CpuRegister.Rsi];
        if (timespecAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var now = DateTimeOffset.UtcNow;
        var seconds = now.ToUnixTimeSeconds();
        var nanoseconds = (now.Ticks % TimeSpan.TicksPerSecond) * 100;
        if (!ctx.TryWriteUInt64(timespecAddress, unchecked((ulong)seconds)) ||
            !ctx.TryWriteUInt64(timespecAddress + sizeof(long), unchecked((ulong)nanoseconds)))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "smIj7eqzZE8",
        ExportName = "clock_getres",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int ClockGetres(CpuContext ctx)
    {
        var timespecAddress = ctx[CpuRegister.Rsi];

        // POSIX allows a null resolution pointer: the call then only validates
        // the clock id, which every id a title passes here does.
        if (timespecAddress == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        // clock_gettime above is backed by DateTimeOffset.UtcNow, whose tick is
        // 100 ns, so that is the honest resolution to report rather than the 1 ns
        // a caller might otherwise assume it can rely on.
        const ulong ResolutionNanoseconds = 100;
        if (!ctx.TryWriteUInt64(timespecAddress, 0) ||
            !ctx.TryWriteUInt64(timespecAddress + sizeof(long), ResolutionNanoseconds))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "vNe1w4diLCs",
        ExportName = "__tls_get_addr",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int TlsGetAddr(CpuContext ctx)
    {
        var tlsInfoAddress = ctx[CpuRegister.Rdi];
        if (tlsInfoAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!ctx.TryReadUInt64(tlsInfoAddress, out var moduleId) ||
            !ctx.TryReadUInt64(tlsInfoAddress + sizeof(ulong), out var offset))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = ResolveTlsAddress(ctx, moduleId, offset);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static ulong ResolveTlsAddress(CpuContext ctx, ulong moduleId, ulong offset)
    {
        return SharpEmu.HLE.GuestTlsTemplate.ResolveAddress(ctx, moduleId, offset);
    }

    [SysAbiExport(
        Nid = "pB-yGZ2nQ9o",
        ExportName = "_sceKernelSetThreadAtexitCount",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelSetThreadAtexitCount(CpuContext ctx)
    {
        _threadAtexitCountCallback = ctx[CpuRegister.Rdi];
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "WhCc1w3EhSI",
        ExportName = "_sceKernelSetThreadAtexitReport",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelSetThreadAtexitReport(CpuContext ctx)
    {
        _threadAtexitReportCallback = ctx[CpuRegister.Rdi];
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "rNhWz+lvOMU",
        ExportName = "_sceKernelSetThreadDtors",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelSetThreadDtors(CpuContext ctx)
    {
        _threadDtorsCallback = ctx[CpuRegister.Rdi];
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    /// <summary>
    /// Invokes the registered runtime thread-destructor callback (set via
    /// <c>_sceKernelSetThreadDtors</c>) on the exiting guest thread. C++
    /// runtimes register this to flush per-thread cleanup that would
    /// otherwise leak.
    /// </summary>
    public static void RunThreadDtors(CpuContext ctx)
    {
        var callback = _threadDtorsCallback;
        if (callback == 0)
        {
            return;
        }

        _ = GuestThreadExecution.Scheduler?.TryCallGuestFunction(
            ctx,
            callback,
            0,
            0,
            0,
            0,
            "kernel_thread_dtors",
            out _);
    }

    [SysAbiExport(
        Nid = "Tz4RNUCBbGI",
        ExportName = "_sceKernelRtldThreadAtexitIncrement",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelRtldThreadAtexitIncrement(CpuContext ctx)
    {
        return KernelRtldThreadAtexitAdjust(ctx, delta: +1);
    }

    [SysAbiExport(
        Nid = "8OnWXlgQlvo",
        ExportName = "_sceKernelRtldThreadAtexitDecrement",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelRtldThreadAtexitDecrement(CpuContext ctx)
    {
        return KernelRtldThreadAtexitAdjust(ctx, delta: -1);
    }

    [SysAbiExport(
        Nid = "pO96TwzOm5E",
        ExportName = "sceKernelGetDirectMemorySize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetDirectMemorySize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = DirectMemorySizeBytes;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "C0f7TJcbfac",
        ExportName = "sceKernelAvailableDirectMemorySize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAvailableDirectMemorySize(CpuContext ctx)
    {
        var arg0 = ctx[CpuRegister.Rdi];
        var arg1 = ctx[CpuRegister.Rsi];
        var arg2 = ctx[CpuRegister.Rdx];
        var arg3 = ctx[CpuRegister.Rcx];
        var arg4 = ctx[CpuRegister.R8];

        ulong used = 0;
        lock (_memoryGate)
        {
            foreach (var allocation in _directAllocations.Values)
            {
                used = Math.Min(DirectMemorySizeBytes, used + allocation.Length);
            }
        }

        var totalAvailable = used >= DirectMemorySizeBytes
            ? 0UL
            : DirectMemorySizeBytes - used;

        if (arg1 != 0 || arg2 != 0 || arg3 != 0 || arg4 != 0)
        {
            var searchStartRaw = unchecked((long)arg0);
            var searchEndRaw = unchecked((long)arg1);
            var alignment = arg2 == 0 ? 0x1000UL : arg2;
            var outAddress = arg3;
            var outSize = arg4;
            if (outAddress == 0 || outSize == 0)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
            }

            var searchStart = searchStartRaw < 0 ? 0UL : (ulong)searchStartRaw;
            var searchEnd = searchEndRaw <= 0
                ? DirectMemorySizeBytes
                : Math.Min((ulong)searchEndRaw, DirectMemorySizeBytes);
            if (searchStart >= searchEnd)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
            }

            bool foundSpan;
            ulong candidate;
            ulong rangeAvailable;
            lock (_memoryGate)
            {
                foundSpan = TryFindAvailableDirectMemorySpanLocked(
                    searchStart,
                    searchEnd,
                    alignment,
                    out candidate,
                    out rangeAvailable);
            }

            if (!foundSpan)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            if (!ctx.TryWriteUInt64(outAddress, candidate) || !ctx.TryWriteUInt64(outSize, rangeAvailable))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var outSizeAddress = arg0;
        if (outSizeAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!ctx.TryWriteUInt64(outSizeAddress, totalAvailable))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "aNz11fnnzi4",
        ExportName = "sceKernelAvailableFlexibleMemorySize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAvailableFlexibleMemorySize(CpuContext ctx)
    {
        var outSizeAddress = ctx[CpuRegister.Rdi];
        if (outSizeAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        ulong available;
        lock (_memoryGate)
        {
            available = _allocatedFlexibleBytes >= FlexibleMemorySizeBytes
                ? 0
                : FlexibleMemorySizeBytes - _allocatedFlexibleBytes;
        }

        if (!ctx.TryWriteUInt64(outSizeAddress, available))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "n1-v6FgU7MQ",
        ExportName = "sceKernelConfiguredFlexibleMemorySize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelConfiguredFlexibleMemorySize(CpuContext ctx)
    {
        var outSizeAddress = ctx[CpuRegister.Rdi];
        if (outSizeAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        Span<byte> sizeBytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(sizeBytes, FlexibleMemorySizeBytes);
        return ctx.Memory.TryWrite(outSizeAddress, sizeBytes)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    [SysAbiExport(
        Nid = "rTXw65xmLIA",
        ExportName = "sceKernelAllocateDirectMemory",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAllocateDirectMemory(CpuContext ctx)
    {
        var searchStartRaw = unchecked((long)ctx[CpuRegister.Rdi]);
        var searchEndRaw = unchecked((long)ctx[CpuRegister.Rsi]);
        var length = ctx[CpuRegister.Rdx];
        var alignment = ctx[CpuRegister.Rcx];
        var memoryType = unchecked((int)ctx[CpuRegister.R8]);
        var outAddress = ctx[CpuRegister.R9];

        if (length == 0 || outAddress == 0)
        {
            TraceDirectMemoryCall(
                ctx,
                "allocate_direct",
                length,
                alignment,
                memoryType,
                outAddress,
                result: OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var limit = DirectMemorySizeBytes;
        ulong searchStart;
        ulong searchEnd;

        if (searchEndRaw <= 0)
        {
            searchEnd = limit;
        }
        else
        {
            searchEnd = (ulong)searchEndRaw;
            if (searchEnd > limit)
            {
                searchEnd = limit;
            }
        }

        if (searchStartRaw < 0)
        {
            searchStart = 0;
        }
        else
        {
            searchStart = (ulong)searchStartRaw;
        }

        if (searchStart >= searchEnd)
        {
            searchStart = 0;
        }

        // PS5 direct memory is allocated in 16 KiB pages; when the guest does
        // not care about alignment, default to that granularity rather than the
        // host 4 KiB page so physical offsets stay on true page boundaries.
        var align = alignment == 0 ? OrbisPageSize : alignment;
        ulong selectedAddress;
        lock (_memoryGate)
        {
            if (!TryAllocateDirectMemoryLocked(searchStart, searchEnd, length, align, memoryType, DirectMemorySizeBytes, out selectedAddress))
            {
                TraceDirectMemoryCall(
                    ctx,
                    "allocate_direct",
                    length,
                    align,
                    memoryType,
                    outAddress,
                    result: OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN;
            }
        }

        if (!ctx.TryWriteUInt64(outAddress, selectedAddress))
        {
            TraceDirectMemoryCall(
                ctx,
                "allocate_direct",
                length,
                align,
                memoryType,
                outAddress,
                selectedAddress,
                OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceDirectMemoryCall(
            ctx,
            "allocate_direct",
            length,
            align,
            memoryType,
            outAddress,
            selectedAddress,
            OrbisGen2Result.ORBIS_GEN2_OK);

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "B+vc2AO2Zrc",
        ExportName = "sceKernelAllocateMainDirectMemory",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAllocateMainDirectMemory(CpuContext ctx)
    {
        var length = ctx[CpuRegister.Rdi];
        var alignment = ctx[CpuRegister.Rsi];
        var memoryType = unchecked((int)ctx[CpuRegister.Rdx]);
        var outAddress = ctx[CpuRegister.Rcx];
        if (outAddress == 0 || length == 0)
        {
            TraceDirectMemoryCall(
                ctx,
                "allocate_main_direct",
                length,
                alignment,
                memoryType,
                outAddress,
                result: OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var effectiveAlignment = alignment == 0 ? OrbisPageSize : alignment;
        ulong aligned;
        lock (_memoryGate)
        {
            var allocationLimit = DirectMemorySizeBytes;
            if (_mainDirectMemoryPoolBase != UnsetMainDirectMemoryPoolBase &&
                !TryAddU64(_mainDirectMemoryPoolBase, DirectMemorySizeBytes, out allocationLimit))
            {
                allocationLimit = ulong.MaxValue;
            }

            if (!TryAllocateDirectMemoryLocked(0, allocationLimit, length, effectiveAlignment, memoryType, allocationLimit, out aligned))
            {
                var poolBase = _mainDirectMemoryPoolBase == UnsetMainDirectMemoryPoolBase
                    ? AlignUp(GetDirectMemoryHighWaterMarkLocked(), effectiveAlignment)
                    : _mainDirectMemoryPoolBase;

                if (_mainDirectMemoryPoolBase == UnsetMainDirectMemoryPoolBase &&
                    TryAddU64(poolBase, DirectMemorySizeBytes, out var shiftedLimit) &&
                    TryAllocateDirectMemoryLocked(0, shiftedLimit, length, effectiveAlignment, memoryType, shiftedLimit, out aligned))
                {
                    _mainDirectMemoryPoolBase = poolBase;
                    if (ShouldTraceDirectMemory())
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][TRACE] main_direct_pool: base=0x{poolBase:X16} limit=0x{shiftedLimit:X16}");
                    }
                }
                else
                {
                    TraceDirectMemoryCall(
                        ctx,
                        "allocate_main_direct",
                        length,
                        effectiveAlignment,
                        memoryType,
                        outAddress,
                        result: OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN);
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN;
                }
            }
        }

        if (!ctx.TryWriteUInt64(outAddress, aligned))
        {
            TraceDirectMemoryCall(
                ctx,
                "allocate_main_direct",
                length,
                effectiveAlignment,
                memoryType,
                outAddress,
                aligned,
                OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceDirectMemoryCall(
            ctx,
            "allocate_main_direct",
            length,
            effectiveAlignment,
            memoryType,
            outAddress,
            aligned,
            OrbisGen2Result.ORBIS_GEN2_OK);

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "MBuItvba6z8",
        ExportName = "sceKernelReleaseDirectMemory",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelReleaseDirectMemory(CpuContext ctx)
    {
        var start = ctx[CpuRegister.Rdi];
        var length = ctx[CpuRegister.Rsi];
        if (!IsAligned(start, OrbisPageSize) || !IsAligned(length, OrbisPageSize))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (length == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        lock (_memoryGate)
        {
            // The unchecked API ignores an unallocated range, matching the
            // kernel contract used by guest pool allocators during teardown.
            _ = TryReleaseDirectMemoryRangeLocked(start, length);
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
    Nid = "hwVSPCmp5tM",
    ExportName = "sceKernelCheckedReleaseDirectMemory",
    Target = Generation.Gen4 | Generation.Gen5,
    LibraryName = "libKernel")]
    public static int KernelCheckedReleaseDirectMemory(CpuContext ctx)
    {
        var start = ctx[CpuRegister.Rdi];
        var length = ctx[CpuRegister.Rsi];

        if (!IsAligned(start, OrbisPageSize) || !IsAligned(length, OrbisPageSize))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (length == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        lock (_memoryGate)
        {
            if (!TryReleaseDirectMemoryRangeLocked(start, length))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "L-Q3LEjIbgA",
        ExportName = "sceKernelMapDirectMemory",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelMapDirectMemory(CpuContext ctx)
    {
        return MapDirectMemoryCore(
            ctx,
            inOutAddressPointer: ctx[CpuRegister.Rdi],
            length: ctx[CpuRegister.Rsi],
            protection: unchecked((int)ctx[CpuRegister.Rdx]),
            flags: ctx[CpuRegister.Rcx],
            directMemoryStart: ctx[CpuRegister.R8],
            alignment: ctx[CpuRegister.R9]);
    }

    [SysAbiExport(
        Nid = "BQQniolj9tQ",
        ExportName = "sceKernelMapDirectMemory2",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelMapDirectMemory2(CpuContext ctx)
    {
        // The "2" variant inserts a memoryType argument (rdx) ahead of v1's
        // protection, shifting protection/flags/directMemoryStart down one
        // register each and pushing alignment onto the stack (the 7th argument,
        // at [rsp + 8], above the return address). The memoryType only selects
        // cache/GPU access attributes, which this HLE does not model per
        // mapping, so it is accepted but does not affect placement.
        ulong alignment = 0;
        _ = ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + sizeof(ulong), out alignment);

        return MapDirectMemoryCore(
            ctx,
            inOutAddressPointer: ctx[CpuRegister.Rdi],
            length: ctx[CpuRegister.Rsi],
            protection: unchecked((int)ctx[CpuRegister.Rcx]),
            flags: ctx[CpuRegister.R8],
            directMemoryStart: ctx[CpuRegister.R9],
            alignment: alignment);
    }

    private static int MapDirectMemoryCore(
        CpuContext ctx,
        ulong inOutAddressPointer,
        ulong length,
        int protection,
        ulong flags,
        ulong directMemoryStart,
        ulong alignment)
    {
        if (ShouldTraceDirectMemory())
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] map_direct: inout=0x{inOutAddressPointer:X16} len=0x{length:X16} prot=0x{protection:X8} flags=0x{flags:X16} direct=0x{directMemoryStart:X16} align=0x{alignment:X16}");
        }
        if (inOutAddressPointer == 0 || length == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!ctx.TryReadUInt64(inOutAddressPointer, out var requestedAddress))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ulong mappedAddress;
        lock (_memoryGate)
        {
            var effectiveAlignment = alignment == 0 ? OrbisPageSize : alignment;
            var fixedMapping = (flags & 0x10UL) != 0;
            var desiredAddress = requestedAddress != 0
                ? requestedAddress
                : directMemoryStart != 0
                    ? AlignUp(directMemoryStart, effectiveAlignment)
                    : AlignUp(_nextVirtualAddress == 0 ? DefaultMapSearchBase : _nextVirtualAddress, effectiveAlignment);

            var reserved = false;
            if (fixedMapping && requestedAddress != 0)
            {
                mappedAddress = requestedAddress;
                reserved = IsGuestRangeBacked(ctx, requestedAddress, length);
                if (!reserved)
                {
                    TryReserveExactGuestVirtualRange(ctx, requestedAddress, length, protection);
                    reserved = IsGuestRangeBacked(ctx, requestedAddress, length);
                }

                if (!reserved)
                {
                    // Rosetta places the x86-64 process stack in the low
                    // address window used by some fixed PS5 mappings. Do not
                    // clobber that host memory: relocate the mapping and
                    // return the actual address through the in/out pointer.
                    if (OperatingSystem.IsMacOS())
                    {
                        var fallbackAddress = AlignUp(
                            _nextVirtualAddress == 0 ? DefaultMapSearchBase : _nextVirtualAddress,
                            effectiveAlignment);
                        reserved = TryReserveGuestVirtualRange(
                            ctx,
                            fallbackAddress,
                            length,
                            protection,
                            effectiveAlignment,
                            out mappedAddress);

                        if (reserved && ShouldTraceDirectMemory())
                        {
                            Console.Error.WriteLine(
                                $"[LOADER][WARN] map_direct relocated fixed mapping: requested=0x{requestedAddress:X16} mapped=0x{mappedAddress:X16} len=0x{length:X16}");
                        }
                    }

                    if (!reserved)
                    {
                        mappedAddress = 0;
                    }
                }
            }
            else
            {
                reserved = TryReserveGuestVirtualRange(ctx, desiredAddress, length, protection, effectiveAlignment, out mappedAddress);
            }
            if (ShouldTraceDirectMemory())
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] map_direct reserve: requested=0x{requestedAddress:X16} desired=0x{desiredAddress:X16} reserved={reserved} mapped=0x{mappedAddress:X16}");
            }
            if (!reserved && !fixedMapping)
            {
                if (mappedAddress == 0)
                {
                    mappedAddress = requestedAddress != 0
                        ? requestedAddress
                        : AllocateMappedGuestAddress(ctx, length, effectiveAlignment);
                    if (ShouldTraceDirectMemory())
                    {
                        Console.Error.WriteLine($"[LOADER][TRACE] map_direct fallback mapped=0x{mappedAddress:X16}");
                    }
                }
            }

            if (mappedAddress == 0)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            _nextVirtualAddress = Math.Max(_nextVirtualAddress, mappedAddress + length);
            ReplaceMappedRegionRangeLocked(new MappedRegion(
                mappedAddress,
                length,
                protection,
                IsFlexible: false,
                IsDirect: true,
                DirectStart: directMemoryStart));
        }

        if (!ctx.TryWriteUInt64(inOutAddressPointer, mappedAddress))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        GuestWriteWatch.OnDirectMapping(mappedAddress, length, protection);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "NcaWUxfMNIQ",
        ExportName = "sceKernelMapNamedDirectMemory",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelMapNamedDirectMemory(CpuContext ctx)
    {
        return KernelMapDirectMemory(ctx);
    }

    [SysAbiExport(
        Nid = "mL8NDH86iQI",
        ExportName = "sceKernelMapNamedFlexibleMemory",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelMapNamedFlexibleMemory(CpuContext ctx)
    {
        var inOutAddressPointer = ctx[CpuRegister.Rdi];
        var length = ctx[CpuRegister.Rsi];
        var protection = unchecked((int)ctx[CpuRegister.Rdx]);
        var flags = ctx[CpuRegister.Rcx];
        if (ShouldTraceDirectMemory())
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] map_flexible: inout=0x{inOutAddressPointer:X16} len=0x{length:X16} prot=0x{protection:X8} flags=0x{flags:X16}");
        }
        if (inOutAddressPointer == 0 || length == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!ctx.TryReadUInt64(inOutAddressPointer, out var requestedAddress))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ulong mappedAddress;
        lock (_memoryGate)
        {
            var fixedMapping = (flags & 0x10UL) != 0;
            var desiredAddress = requestedAddress != 0
                ? requestedAddress
                : AlignUp(_nextVirtualAddress == 0 ? DefaultMapSearchBase : _nextVirtualAddress, 0x1000UL);

            if (fixedMapping && requestedAddress != 0)
            {
                mappedAddress = requestedAddress;
                if (!IsGuestRangeBacked(ctx, requestedAddress, length))
                {
                    TryReserveExactGuestVirtualRange(ctx, requestedAddress, length, protection);
                    if (!IsGuestRangeBacked(ctx, requestedAddress, length))
                    {
                        mappedAddress = 0;
                    }
                }
            }
            else if (!TryReserveGuestVirtualRange(ctx, desiredAddress, length, protection, OrbisPageSize, out mappedAddress))
            {
                mappedAddress = AllocateMappedGuestAddress(ctx, length, 0x1000UL);
            }

            if (ShouldTraceDirectMemory())
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] map_flexible reserve: requested=0x{requestedAddress:X16} desired=0x{desiredAddress:X16} mapped=0x{mappedAddress:X16}");
            }

            if (mappedAddress == 0)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            _nextVirtualAddress = Math.Max(_nextVirtualAddress, mappedAddress + length);
            _allocatedFlexibleBytes = Math.Min(FlexibleMemorySizeBytes, _allocatedFlexibleBytes + length);
            ReplaceMappedRegionRangeLocked(new MappedRegion(
                mappedAddress,
                length,
                protection,
                IsFlexible: true,
                IsDirect: false,
                DirectStart: 0));
        }

        if (!ctx.TryWriteUInt64(inOutAddressPointer, mappedAddress))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "IWIBBdTHit4",
        ExportName = "sceKernelMapFlexibleMemory",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelMapFlexibleMemory(CpuContext ctx)
    {
        return KernelMapNamedFlexibleMemory(ctx);
    }

    [SysAbiExport(
        Nid = "4h6F1LLbTiw",
        ExportName = "sceKernelMapNamedFlexibleMemoryInternal",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelMapFlexibleMemoryInternal(CpuContext ctx)
    {
        return KernelMapNamedFlexibleMemory(ctx);
    }

    [SysAbiExport(
        Nid = "2SKEx6bSq-4",
        ExportName = "sceKernelBatchMap",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelBatchMap(CpuContext ctx)
    {
        return KernelBatchMapCore(ctx, OrbisKernelMapFixed);
    }

    [SysAbiExport(
        Nid = "kBJzF8x4SyE",
        ExportName = "sceKernelBatchMap2",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelBatchMap2(CpuContext ctx)
    {
        return KernelBatchMapCore(ctx, unchecked((int)ctx[CpuRegister.Rcx]));
    }

    [SysAbiExport(
        Nid = "yDBwVAolDgg",
        ExportName = "sceKernelIsStack",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelIsStack(CpuContext ctx)
    {
        _ = ctx[CpuRegister.Rdi];
        var startAddress = ctx[CpuRegister.Rsi];
        var endAddress = ctx[CpuRegister.Rdx];

        // The queried ranges used by libc's VM allocator are ordinary heap
        // mappings. The kernel still initializes both outputs when a mapping
        // is not a pthread stack; leaving them untouched makes libc consume
        // stale stack values and issue invalid fixed-range reservations.
        if ((startAddress != 0 && !ctx.TryWriteUInt64(startAddress, 0)) ||
            (endAddress != 0 && !ctx.TryWriteUInt64(endAddress, 0)))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "cQke9UuBQOk",
        ExportName = "sceKernelMunmap",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelMunmap(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var length = ctx[CpuRegister.Rsi];
        if (address == 0 || length == 0 || ulong.MaxValue - address < length)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var rangeEnd = address + length;
        var physicallyBacked = IsGuestRangeBacked(ctx, address, length);
        var removedAny = false;
        lock (_memoryGate)
        {
            var removedRegions = _mappedRegions.Values
                .Where(region =>
                    region.Address >= address &&
                    region.Address < rangeEnd &&
                    region.Length <= rangeEnd - region.Address)
                .ToArray();

            if (removedRegions.Length == 0 && !physicallyBacked)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            foreach (var mappedRegion in removedRegions)
            {
                removedAny |= _mappedRegions.Remove(mappedRegion.Address);
                if (mappedRegion.IsFlexible)
                {
                    _allocatedFlexibleBytes = mappedRegion.Length >= _allocatedFlexibleBytes
                        ? 0
                        : _allocatedFlexibleBytes - mappedRegion.Length;
                }
            }
        }

        if (physicallyBacked || removedAny)
        {
            KernelRuntimeCompatExports.RegisterReleasedVirtualRange(address, length);
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "DGMG3JshrZU",
        ExportName = "sceKernelSetVirtualRangeName",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelSetVirtualRangeName(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var length = ctx[CpuRegister.Rsi];
        var nameAddress = ctx[CpuRegister.Rdx];
        if (nameAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (!TryReadCString(ctx, nameAddress, OrbisKernelMaximumNameLength, out var nameBytes))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var name = Encoding.UTF8.GetString(nameBytes);
        lock (_memoryGate)
        {
            if (!TryFindVirtualQueryRegionLocked(address, findNext: false, out var region) ||
                length > region.Length ||
                address < region.Address ||
                length > region.Address + region.Length - address)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            _mappedRegionNames[region.Address] = name;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "rVjRvHJ0X6c",
        ExportName = "sceKernelVirtualQuery",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelVirtualQuery(CpuContext ctx)
    {
        var queryAddress = ctx[CpuRegister.Rdi];
        var flags = unchecked((int)ctx[CpuRegister.Rsi]);
        var infoAddress = ctx[CpuRegister.Rdx];
        var infoSize = ctx[CpuRegister.Rcx];
        if (infoAddress == 0 || infoSize < OrbisVirtualQueryInfoSize)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        MappedRegion region;
        var memoryType = 0;
        lock (_memoryGate)
        {
            if (!TryFindVirtualQueryRegionLocked(queryAddress, findNext: (flags & 0x1) != 0, out region))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            if (region.IsDirect && TryFindDirectAllocationLocked(region.DirectStart, out var allocation))
            {
                memoryType = allocation.MemoryType;
            }
        }

        Span<byte> payload = stackalloc byte[OrbisVirtualQueryInfoSize];
        payload.Clear();

        var regionEnd = TryAddU64(region.Address, region.Length, out var exclusiveEnd)
            ? exclusiveEnd
            : ulong.MaxValue;
        var stateFlags = 0u;
        if (region.IsFlexible)
        {
            stateFlags |= 0x01u;
        }

        if (region.IsDirect)
        {
            stateFlags |= 0x02u;
        }

        stateFlags |= 0x10u;

        BinaryPrimitives.WriteUInt64LittleEndian(payload[0..8], region.Address);
        BinaryPrimitives.WriteUInt64LittleEndian(payload[8..16], regionEnd);
        BinaryPrimitives.WriteUInt64LittleEndian(payload[16..24], region.DirectStart);
        BinaryPrimitives.WriteInt32LittleEndian(payload[24..28], region.Protection);
        BinaryPrimitives.WriteInt32LittleEndian(payload[28..32], memoryType);
        payload[32] = unchecked((byte)stateFlags);

        string name;
        lock (_memoryGate)
        {
            name = _mappedRegionNames.GetValueOrDefault(region.Address)
                ?? (region.IsDirect
                    ? "direct"
                    : region.IsFlexible
                        ? "flexible"
                        : string.Empty);
        }
        if (!string.IsNullOrEmpty(name))
        {
            var nameBytes = Encoding.ASCII.GetBytes(name);
            nameBytes.AsSpan(0, Math.Min(nameBytes.Length, OrbisKernelMaximumNameLength))
                .CopyTo(payload.Slice(33, OrbisKernelMaximumNameLength));
        }

        if (!TryWriteCompat(ctx, infoAddress, payload))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "WFcfL2lzido",
        ExportName = "sceKernelQueryMemoryProtection",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelQueryMemoryProtection(CpuContext ctx)
    {
        var queryAddress = ctx[CpuRegister.Rdi];
        var startOut = ctx[CpuRegister.Rsi];
        var endOut = ctx[CpuRegister.Rdx];
        var protectionOut = ctx[CpuRegister.Rcx];

        lock (_memoryGate)
        {
            foreach (var region in _mappedRegions.Values)
            {
                if (queryAddress < region.Address || queryAddress >= region.Address + region.Length)
                {
                    continue;
                }

                if (startOut != 0 && !ctx.TryWriteUInt64(startOut, region.Address))
                {
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                }

                if (endOut != 0 && !ctx.TryWriteUInt64(endOut, region.Address + region.Length - 1))
                {
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                }

                if (protectionOut != 0 && !TryWriteInt32(ctx, protectionOut, region.Protection))
                {
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                }

                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
    }

    [SysAbiExport(
        Nid = "BHouLQzh0X0",
        ExportName = "sceKernelDirectMemoryQuery",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelDirectMemoryQuery(CpuContext ctx)
    {
        var offset = ctx[CpuRegister.Rdi];
        var flags = ctx[CpuRegister.Rsi];
        var infoAddress = ctx[CpuRegister.Rdx];
        var infoSize = ctx[CpuRegister.Rcx];
        if (infoAddress == 0 || infoSize < 24)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (offset >= DirectMemorySizeBytes)
        {
            // Real hardware returns EACCES here (0x8002000D), not ENOENT.
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED;
        }

        var findNext = (flags & 1) != 0;
        var found = false;
        var matchStart = 0UL;
        var matchEnd = 0UL;
        var matchMemoryType = 0;

        lock (_memoryGate)
        {
            var candidates = _directAllocations.Values
                .Where(block => findNext
                    ? block.Start + block.Length > offset
                    : offset >= block.Start && offset < block.Start + block.Length)
                .OrderBy(block => block.Start);

            foreach (var block in candidates)
            {
                found = true;
                matchStart = block.Start;
                matchEnd = block.Start + block.Length;
                matchMemoryType = block.MemoryType;
                break;
            }
        }

        if (!found)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED;
        }

        if (!ctx.TryWriteUInt64(infoAddress, matchStart) ||
            !ctx.TryWriteUInt64(infoAddress + sizeof(ulong), matchEnd) ||
            !TryWriteInt32(ctx, infoAddress + (sizeof(ulong) * 2), matchMemoryType))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    /// <summary>
    /// POSIX alias of <see cref="KernelMprotect"/>; identical (addr, len, prot)
    /// argument order. Imported by libcohtml, whose embedded V8 changes page
    /// permissions through this name when moving JIT pages between writable and
    /// executable.
    /// </summary>
    [SysAbiExport(
        Nid = "YQOfxL4QfeU",
        ExportName = "mprotect",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixMprotect(CpuContext ctx) => KernelMprotect(ctx);

    /// <summary>
    /// POSIX alias of <see cref="KernelMunmap"/>; identical (addr, len) argument
    /// order.
    /// </summary>
    [SysAbiExport(
        Nid = "UqDGjXA5yUM",
        ExportName = "munmap",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixMunmap(CpuContext ctx) => KernelMunmap(ctx);

    /// <summary>
    /// Reports the 16 KiB page granularity this backend maps and aligns against
    /// (<see cref="OrbisPageSize"/>), not the host's 4 KiB. An allocator that
    /// rounded to the host value would hand back sub-page offsets that every
    /// mapping call here then rejects for misalignment.
    /// </summary>
    [SysAbiExport(
        Nid = "k+AXqu2-eBc",
        ExportName = "getpagesize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixGetPageSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = OrbisPageSize;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "vSMAm3cxYTY",
        ExportName = "sceKernelMprotect",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelMprotect(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var length = ctx[CpuRegister.Rsi];
        var protection = unchecked((int)ctx[CpuRegister.Rdx]);
        if (address == 0 || length == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryNormalizeProtectRange(address, length, out var alignedAddress, out var alignedLength))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryProtectHostRange(alignedAddress, alignedLength, protection))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        lock (_memoryGate)
        {
            _ = TryApplyMappedRegionProtectionLocked(alignedAddress, alignedLength, protection);
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "9bfdLIyuwCY",
        ExportName = "sceKernelMtypeprotect",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelMtypeprotect(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var length = ctx[CpuRegister.Rsi];
        var memoryType = unchecked((int)ctx[CpuRegister.Rdx]);
        var protection = unchecked((int)ctx[CpuRegister.Rcx]);
        if (address == 0 || length == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryNormalizeProtectRange(address, length, out var alignedAddress, out var alignedLength))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryProtectHostRange(alignedAddress, alignedLength, protection))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        lock (_memoryGate)
        {
            _ = TryApplyMappedRegionProtectionLocked(alignedAddress, alignedLength, protection, memoryType);
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int KernelRtldThreadAtexitAdjust(CpuContext ctx, int delta)
    {
        var counterAddress = ctx[CpuRegister.Rdi];
        if (counterAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!ctx.TryReadUInt64(counterAddress, out var value))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var adjusted = delta >= 0
            ? unchecked(value + (ulong)delta)
            : value >= (ulong)(-delta)
                ? unchecked(value - (ulong)(-delta))
                : 0UL;
        if (!ctx.TryWriteUInt64(counterAddress, adjusted))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = adjusted;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int SnprintfCore(CpuContext ctx)
    {
        var destination = ctx[CpuRegister.Rdi];
        var bufferSize = ctx[CpuRegister.Rsi];
        var formatAddress = ctx[CpuRegister.Rdx];

        if (!TryReadCString(ctx, formatAddress, 1_048_576, out var formatBytes))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var format = Encoding.UTF8.GetString(formatBytes);
        var result = FormatString(ctx, format);

        return WriteSnprintfOutput(ctx, destination, bufferSize, result);
    }

    private static int VsnprintfCore(CpuContext ctx)
    {
        var destination = ctx[CpuRegister.Rdi];
        var bufferSize = ctx[CpuRegister.Rsi];
        var formatAddress = ctx[CpuRegister.Rdx];
        var vaListAddress = ctx[CpuRegister.Rcx];

        if (!TryReadCString(ctx, formatAddress, 1_048_576, out var formatBytes))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var format = Encoding.UTF8.GetString(formatBytes);
        if (!TryCreateVaListCursor(ctx, vaListAddress, out var vaCursor))
        {
            return WriteSnprintfOutput(ctx, destination, bufferSize, formatBytes);
        }

        var rendered = FormatString(ctx, format, ref vaCursor);
        return WriteSnprintfOutput(ctx, destination, bufferSize, rendered);
    }

    private static int SwprintfCore(CpuContext ctx)
    {
        var destination = ctx[CpuRegister.Rdi];
        var bufferSize = ctx[CpuRegister.Rsi];
        var formatAddress = ctx[CpuRegister.Rdx];

        if (!TryReadWideCString(ctx, formatAddress, 1_048_576, out var formatUnits))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var format = DecodeWideUnits(formatUnits);
        var rendered = FormatString(ctx, format);
        TraceWidePrintf(ctx, "swprintf", destination, bufferSize, format, rendered);
        return WriteSwprintfOutput(ctx, destination, bufferSize, rendered);
    }

    private static int VswprintfCore(CpuContext ctx)
    {
        var destination = ctx[CpuRegister.Rdi];
        var bufferSize = ctx[CpuRegister.Rsi];
        var formatAddress = ctx[CpuRegister.Rdx];
        var vaListAddress = ctx[CpuRegister.Rcx];

        if (!TryReadWideCString(ctx, formatAddress, 1_048_576, out var formatUnits))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var format = DecodeWideUnits(formatUnits);
        string rendered;
        if (!TryCreateVaListCursor(ctx, vaListAddress, out var vaCursor))
        {
            rendered = format;
        }
        else
        {
            TraceWidePrintfVaList(ctx, "vswprintf", format, vaListAddress, vaCursor);
            rendered = FormatString(ctx, format, ref vaCursor);
        }

        TraceWidePrintf(ctx, "vswprintf", destination, bufferSize, format, rendered);
        return WriteSwprintfOutput(ctx, destination, bufferSize, rendered);
    }

    private static bool TryCreateVaListCursor(CpuContext ctx, ulong vaListAddress, out SysVAmd64VaListCursor cursor)
    {
        cursor = default;
        if (vaListAddress == 0)
        {
            return false;
        }

        Span<byte> vaList = stackalloc byte[24];
        if (!TryReadCompat(ctx, vaListAddress, vaList))
        {
            return false;
        }

        cursor = new SysVAmd64VaListCursor(
            ctx,
            vaListAddress,
            BinaryPrimitives.ReadUInt32LittleEndian(vaList),
            BinaryPrimitives.ReadUInt32LittleEndian(vaList[4..]),
            BinaryPrimitives.ReadUInt64LittleEndian(vaList[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(vaList[16..]));
        return true;
    }

    internal static bool TryFormatStringFromVaList(
        CpuContext ctx,
        string format,
        ulong vaListAddress,
        out string rendered)
    {
        if (!TryCreateVaListCursor(ctx, vaListAddress, out var vaCursor))
        {
            rendered = format;
            return false;
        }

        rendered = FormatString(ctx, format, ref vaCursor);
        return true;
    }

    private static int WriteSnprintfOutput(
        CpuContext ctx,
        ulong destination,
        ulong bufferSize,
        ReadOnlySpan<byte> outputBytes)
    {
        if (bufferSize != 0 && destination != 0)
        {
            var maxWritable = (int)Math.Min((ulong)int.MaxValue, bufferSize - 1);
            var copyLength = Math.Min(maxWritable, outputBytes.Length);
            if (copyLength > 0 && !ctx.Memory.TryWrite(destination, outputBytes[..copyLength]))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            Span<byte> nullTerminator = stackalloc byte[1];
            if (!ctx.Memory.TryWrite(destination + (ulong)copyLength, nullTerminator))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)outputBytes.Length);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int WriteSnprintfOutput(
        CpuContext ctx,
        ulong destination,
        ulong bufferSize,
        string output)
    {
        if (bufferSize == 0 || destination == 0)
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)Encoding.UTF8.GetByteCount(output));
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        return WriteSnprintfOutput(ctx, destination, bufferSize, Encoding.UTF8.GetBytes(output));
    }

    private static int WriteSwprintfOutput(
        CpuContext ctx,
        ulong destination,
        ulong bufferSize,
        string rendered)
    {
        if (bufferSize != 0 && destination != 0)
        {
            var maxWritableChars = (int)Math.Min((ulong)int.MaxValue, bufferSize - 1);
            var copyLength = Math.Min(maxWritableChars, rendered.Length);
            if (copyLength > 0)
            {
                var outputBytes = Encoding.Unicode.GetBytes(rendered[..copyLength]);
                if (!TryWriteCompat(ctx, destination, outputBytes))
                {
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                }
            }

            Span<byte> nullTerminator = stackalloc byte[WideCharSize];
            if (!TryWriteCompat(ctx, destination + ((ulong)copyLength * WideCharSize), nullTerminator))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)rendered.Length);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static void TraceWidePrintf(CpuContext ctx, string exportName, ulong destination, ulong bufferSize, string format, string rendered)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_WIDE_PRINTF"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var formatPreview = format.Length > 160 ? format[..160] + "..." : format;
        var renderedPreview = rendered.Length > 160 ? rendered[..160] + "..." : rendered;
        Console.Error.WriteLine(
            $"[LOADER][TRACE] {exportName}: dst=0x{destination:X16} count=0x{bufferSize:X} len={rendered.Length} fmt='{formatPreview}' rendered='{renderedPreview}'");
    }

    private static void TraceWidePrintfVaList(
        CpuContext ctx,
        string exportName,
        string format,
        ulong vaListAddress,
        SysVAmd64VaListCursor cursor)
    {
        if (!ShouldTraceWidePrintfArgs(format))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] {exportName}: va_list=0x{vaListAddress:X16} gp_offset={cursor.GpOffset} fp_offset={cursor.FpOffset} overflow=0x{cursor.OverflowArgArea:X16} reg_save=0x{cursor.RegSaveArea:X16}");

        if (cursor.RegSaveArea != 0)
        {
            for (var i = 0; i < 6; i++)
            {
                var slotAddress = cursor.RegSaveArea + ((ulong)i * 8);
                var valueText = TryReadUInt64Compat(ctx, slotAddress, out var value)
                    ? $"0x{value:X16}"
                    : "<unreadable>";
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] {exportName}: reg_save.gp[{i}] @0x{slotAddress:X16} = {valueText}");
            }
        }

        if (cursor.OverflowArgArea != 0)
        {
            for (var i = 0; i < 4; i++)
            {
                var slotAddress = cursor.OverflowArgArea + ((ulong)i * 8);
                var valueText = TryReadUInt64Compat(ctx, slotAddress, out var value)
                    ? $"0x{value:X16}"
                    : "<unreadable>";
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] {exportName}: overflow[{i}] @0x{slotAddress:X16} = {valueText}");
            }
        }
    }

    private static bool ShouldTraceWidePrintfArgs(string format)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_WIDE_PRINTF_ARGS"), "1", StringComparison.Ordinal))
        {
            return false;
        }

        var filter = Environment.GetEnvironmentVariable("SHARPEMU_LOG_WIDE_PRINTF_FILTER");
        return string.IsNullOrEmpty(filter) || format.Contains(filter, StringComparison.Ordinal);
    }

    private static void TracePrintfStringArgument(CpuContext ctx, string lengthMod, ulong address)
    {
        if (!ShouldTraceWidePrintfArgs(lengthMod == "l" ? "%ls" : "%s"))
        {
            return;
        }

        var widePreview = "<not-wide>";
        if (address != 0 && TryReadWideCString(ctx, address, 64, out var wideUnits))
        {
            widePreview = SanitizeTracePreview(DecodeWideUnits(wideUnits), 64);
        }
        else if (address == 0)
        {
            widePreview = "<null>";
        }
        else
        {
            widePreview = "<unreadable>";
        }

        var utf8Preview = "<null>";
        if (address != 0 && TryReadCString(ctx, address, 128, out var utf8Bytes))
        {
            utf8Preview = SanitizeTracePreview(Encoding.UTF8.GetString(utf8Bytes), 64);
        }
        else if (address != 0)
        {
            utf8Preview = "<unreadable>";
        }

        var rawPreview = "<null>";
        if (address != 0)
        {
            Span<byte> raw = stackalloc byte[32];
            rawPreview = TryReadCompat(ctx, address, raw)
                ? Convert.ToHexString(raw).ToLowerInvariant()
                : "<unreadable>";
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] printf-arg %{lengthMod}s addr=0x{address:X16} wide='{widePreview}' utf8='{utf8Preview}' raw={rawPreview}");
    }

    private static string SanitizeTracePreview(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var sanitized = value
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
        return sanitized.Length > maxLength
            ? sanitized[..maxLength] + "..."
            : sanitized;
    }

    private readonly record struct PosixTm(
        int Second,
        int Minute,
        int Hour,
        int Day,
        int Month,
        int Year,
        int WeekDay,
        int YearDay,
        int IsDst)
    {
        public DateTime ToDateTime()
        {
            var year = Math.Clamp(1900 + Year, 1, 9999);
            var month = Math.Clamp(Month + 1, 1, 12);
            var day = Math.Clamp(Day, 1, DateTime.DaysInMonth(year, month));
            var hour = Math.Clamp(Hour, 0, 23);
            var minute = Math.Clamp(Minute, 0, 59);
            var second = Math.Clamp(Second, 0, 60);
            if (second == 60)
            {
                second = 59;
            }

            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        }
    }

    private static bool TryReadPosixTm(CpuContext ctx, ulong address, out PosixTm tm)
    {
        tm = default;
        if (address == 0)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[9 * sizeof(int)];
        if (!TryReadCompat(ctx, address, bytes))
        {
            return false;
        }

        tm = new PosixTm(
            BinaryPrimitives.ReadInt32LittleEndian(bytes[0..4]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[4..8]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[8..12]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[12..16]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[16..20]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[20..24]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[24..28]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[28..32]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[32..36]));
        return true;
    }

    private static string FormatWcsftime(string format, PosixTm tm)
    {
        var timestamp = tm.ToDateTime();
        var culture = CultureInfo.InvariantCulture;
        var builder = new StringBuilder(format.Length + 16);
        for (var i = 0; i < format.Length; i++)
        {
            var ch = format[i];
            if (ch != '%' || i + 1 >= format.Length)
            {
                builder.Append(ch);
                continue;
            }

            i++;
            builder.Append(format[i] switch
            {
                '%' => "%",
                'a' => timestamp.ToString("ddd", culture),
                'A' => timestamp.ToString("dddd", culture),
                'b' or 'h' => timestamp.ToString("MMM", culture),
                'B' => timestamp.ToString("MMMM", culture),
                'c' => timestamp.ToString("ddd MMM dd HH:mm:ss yyyy", culture),
                'd' => timestamp.ToString("dd", culture),
                'e' => timestamp.Day.ToString().PadLeft(2, ' '),
                'F' => timestamp.ToString("yyyy-MM-dd", culture),
                'H' => timestamp.ToString("HH", culture),
                'I' => timestamp.ToString("hh", culture),
                'j' => timestamp.DayOfYear.ToString("D3", culture),
                'm' => timestamp.ToString("MM", culture),
                'M' => timestamp.ToString("mm", culture),
                'n' => "\n",
                'p' => timestamp.ToString("tt", culture),
                'R' => timestamp.ToString("HH:mm", culture),
                'S' => timestamp.ToString("ss", culture),
                't' => "\t",
                'T' => timestamp.ToString("HH:mm:ss", culture),
                'u' => (((int)timestamp.DayOfWeek + 6) % 7 + 1).ToString(culture),
                'w' => ((int)timestamp.DayOfWeek).ToString(culture),
                'x' => timestamp.ToString("MM/dd/yy", culture),
                'X' => timestamp.ToString("HH:mm:ss", culture),
                'y' => timestamp.ToString("yy", culture),
                'Y' => timestamp.ToString("yyyy", culture),
                'Z' => tm.IsDst == 0 ? "UTC" : "DST",
                'z' => "+0000",
                _ => "%" + format[i]
            });
        }

        return builder.ToString();
    }

    private static void TraceLibcAllocation(
        CpuContext ctx,
        string operation,
        ulong size,
        ulong alignment = 0,
        ulong count = 0,
        ulong existingAddress = 0,
        ulong resultAddress = 0,
        int? errorCode = null)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_LIBC_ALLOC"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var returnRip = 0UL;
        var stackPointer = ctx[CpuRegister.Rsp];
        if (stackPointer != 0)
        {
            _ = ctx.TryReadUInt64(stackPointer, out returnRip);
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] {operation}: ret=0x{returnRip:X16} size=0x{size:X16} count=0x{count:X16} align=0x{alignment:X16} in=0x{existingAddress:X16} result=0x{resultAddress:X16} errno={(errorCode.HasValue ? errorCode.Value : 0)}");
    }

    internal static string FormatStringFromVarArgs(CpuContext ctx, string format, int firstGpArgIndex)
    {
        var argumentSource = new RegisterPrintfArgumentSource(ctx, Math.Max(0, firstGpArgIndex));
        return FormatString(ctx, format, ref argumentSource);
    }

    private static string FormatString(CpuContext ctx, string format)
    {
        return FormatStringFromVarArgs(ctx, format, firstGpArgIndex: 3);
    }

    [ThreadStatic]
    private static StringBuilder? _formatBuilder;

    // printf length modifier collapsed to the widths our conversions care about.
    // Kept as an enum rather than a per-argument substring so the hot format
    // loop avoids a string allocation and string comparisons on every spec.
    private enum PrintfLength
    {
        None,
        Char,   // hh
        Short,  // h
        Long,   // l / ll / j / z / t / L
    }

    private static string FormatString<TArgumentSource>(
        CpuContext ctx,
        string format,
        ref TArgumentSource argumentSource)
        where TArgumentSource : struct, IPrintfArgumentSource
    {
        // printf formatting is one of the hottest HLE paths during loading, so
        // reuse a per-thread builder and append literal runs as spans instead of
        // allocating a builder and appending char-by-char on every call.
        var sb = _formatBuilder ??= new StringBuilder(256);
        sb.Clear();
        if (sb.Capacity < format.Length + 32)
        {
            sb.EnsureCapacity(format.Length + 32);
        }

        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '%')
            {
                var literalStart = i;
                while (i < format.Length && format[i] != '%')
                {
                    i++;
                }

                sb.Append(format.AsSpan(literalStart, i - literalStart));
                i--;
                continue;
            }

            i++;
            if (i >= format.Length)
            {
                sb.Append('%');
                break;
            }

            var leftAlign = false;
            var showSign = false;
            var spaceForSign = false;
            var padWithZero = false;
            var alternateForm = false;

            while (i < format.Length)
            {
                switch (format[i])
                {
                    case '-': leftAlign = true; i++; continue;
                    case '+': showSign = true; i++; continue;
                    case ' ': spaceForSign = true; i++; continue;
                    case '0': padWithZero = true; i++; continue;
                    case '#': alternateForm = true; i++; continue;
                }
                break;
            }

            var width = 0;
            if (i < format.Length && format[i] == '*')
            {
                width = unchecked((int)argumentSource.NextGpArg());
                i++;
                if (width < 0)
                {
                    leftAlign = true;
                    width = -width;
                }
            }
            else if (i < format.Length && char.IsDigit(format[i]))
            {
                while (i < format.Length && char.IsDigit(format[i]))
                {
                    width = width * 10 + (format[i] - '0');
                    i++;
                }
            }

            var precision = -1;
            if (i < format.Length && format[i] == '.')
            {
                i++;
                if (i < format.Length && format[i] == '*')
                {
                    precision = unchecked((int)argumentSource.NextGpArg());
                    i++;
                }
                else if (i < format.Length && char.IsDigit(format[i]))
                {
                    precision = 0;
                    while (i < format.Length && char.IsDigit(format[i]))
                    {
                        precision = precision * 10 + (format[i] - '0');
                        i++;
                    }
                }
                else
                {
                    precision = 0;
                }
            }

            var lengthMod = PrintfLength.None;
            if (i < format.Length)
            {
                if (i + 1 < format.Length && format[i] == 'h' && format[i + 1] == 'h')
                {
                    lengthMod = PrintfLength.Char;
                    i += 2;
                }
                else if (i + 1 < format.Length && format[i] == 'l' && format[i + 1] == 'l')
                {
                    lengthMod = PrintfLength.Long;
                    i += 2;
                }
                else if (format[i] == 'h')
                {
                    lengthMod = PrintfLength.Short;
                    i++;
                }
                else if (format[i] is 'l' or 'j' or 'z' or 't' or 'L')
                {
                    lengthMod = PrintfLength.Long;
                    i++;
                }
            }

            if (i >= format.Length)
            {
                sb.Append('%');
                break;
            }

            var specifier = format[i];

            switch (specifier)
            {
                case '%':
                    sb.Append('%');
                    break;

                case 'd':
                case 'i':
                    {
                        long value = lengthMod switch
                        {
                            PrintfLength.Char => unchecked((sbyte)argumentSource.NextGpArg()),
                            PrintfLength.Short => unchecked((short)argumentSource.NextGpArg()),
                            PrintfLength.Long => unchecked((long)argumentSource.NextGpArg()),
                            _ => unchecked((int)argumentSource.NextGpArg())
                        };

                        var formatted = value.ToString(CultureInfo.InvariantCulture);
                        if (showSign && value >= 0)
                            formatted = "+" + formatted;
                        else if (spaceForSign && value >= 0)
                            formatted = " " + formatted;

                        sb.Append(PadString(formatted, width, leftAlign, padWithZero && !leftAlign));
                    }
                    break;

                case 'u':
                    {
                        ulong value = lengthMod switch
                        {
                            PrintfLength.Char => (byte)argumentSource.NextGpArg(),
                            PrintfLength.Short => (ushort)argumentSource.NextGpArg(),
                            PrintfLength.Long => argumentSource.NextGpArg(),
                            _ => (uint)argumentSource.NextGpArg()
                        };

                        var formatted = value.ToString(CultureInfo.InvariantCulture);
                        sb.Append(PadString(formatted, width, leftAlign, padWithZero && !leftAlign));
                    }
                    break;

                case 'x':
                case 'X':
                    {
                        ulong value = lengthMod switch
                        {
                            PrintfLength.Char => (byte)argumentSource.NextGpArg(),
                            PrintfLength.Short => (ushort)argumentSource.NextGpArg(),
                            PrintfLength.Long => argumentSource.NextGpArg(),
                            _ => (uint)argumentSource.NextGpArg()
                        };

                        var formatted = specifier == 'x'
                            ? value.ToString("x", CultureInfo.InvariantCulture)
                            : value.ToString("X", CultureInfo.InvariantCulture);

                        if (alternateForm && value != 0)
                            formatted = specifier == 'x' ? "0x" + formatted : "0X" + formatted;

                        sb.Append(PadString(formatted, width, leftAlign, padWithZero && !leftAlign));
                    }
                    break;

                case 'o':
                    {
                        ulong value = lengthMod switch
                        {
                            PrintfLength.Char => (byte)argumentSource.NextGpArg(),
                            PrintfLength.Short => (ushort)argumentSource.NextGpArg(),
                            PrintfLength.Long => argumentSource.NextGpArg(),
                            _ => (uint)argumentSource.NextGpArg()
                        };

                        var formatted = Convert.ToString((long)value, 8);
                        if (alternateForm && value != 0)
                            formatted = "0" + formatted;

                        sb.Append(PadString(formatted, width, leftAlign, padWithZero && !leftAlign));
                    }
                    break;

                case 'p':
                    {
                        var value = argumentSource.NextGpArg();
                        var formatted = value == 0
                            ? "(nil)"
                            : $"0x{value:X}";
                        sb.Append(formatted);
                    }
                    break;

                case 's':
                    {
                        var strAddr = argumentSource.NextGpArg();
                        TracePrintfStringArgument(ctx, lengthMod == PrintfLength.Long ? "l" : "", strAddr);
                        if (strAddr == 0)
                        {
                            sb.Append("(null)");
                        }
                        else if (lengthMod == PrintfLength.Long)
                        {
                            if (TryReadWideCString(ctx, strAddr, 1_048_576, out var wideUnits))
                            {
                                var str = DecodeWideUnits(wideUnits);
                                if (precision >= 0 && str.Length > precision)
                                    str = str.Substring(0, precision);
                                sb.Append(PadString(str, width, leftAlign, false));
                            }
                            else
                            {
                                sb.Append("(null)");
                            }
                        }
                        else if (TryReadCString(ctx, strAddr, 1_048_576, out var strBytes))
                        {
                            var str = Encoding.UTF8.GetString(strBytes);
                            if (precision >= 0 && str.Length > precision)
                                str = str.Substring(0, precision);
                            sb.Append(PadString(str, width, leftAlign, false));
                        }
                        else
                        {
                            sb.Append("(null)");
                        }
                    }
                    break;

                case 'c':
                    {
                        string renderedChar;
                        if (lengthMod == PrintfLength.Long)
                        {
                            var scalar = unchecked((ushort)argumentSource.NextGpArg());
                            renderedChar = TryConvertWideScalarToString(scalar, out var wideCharText)
                                ? wideCharText
                                : "?";
                        }
                        else
                        {
                            renderedChar = ((char)(byte)argumentSource.NextGpArg()).ToString();
                        }

                        sb.Append(PadString(renderedChar, width, leftAlign, false));
                    }
                    break;

                case 'f':
                case 'F':
                case 'e':
                case 'E':
                case 'g':
                case 'G':
                    {
                        var value = argumentSource.NextFloatArg();

                        var formatStr = precision >= 0
                            ? $"{{0:{specifier}{precision}}}"
                            : $"{{0:{specifier}}}";
                        var formatted = string.Format(
                            CultureInfo.InvariantCulture,
                            formatStr,
                            value);

                        if (showSign && value >= 0)
                            formatted = "+" + formatted;
                        else if (spaceForSign && value >= 0)
                            formatted = " " + formatted;

                        sb.Append(PadString(formatted, width, leftAlign, padWithZero && !leftAlign));
                    }
                    break;

                case 'n':
                    {
                        var addr = argumentSource.NextGpArg();
                        if (addr != 0)
                        {
                            _ = TryWriteInt32(ctx, addr, sb.Length);
                        }
                    }
                    break;

                default:
                    sb.Append('%');
                    sb.Append(specifier);
                    break;
            }
        }

        return sb.ToString();
    }

    private static ulong ReadStackArg(CpuContext ctx, ulong offset)
    {
        var rsp = ctx[CpuRegister.Rsp];
        if (!ctx.TryReadUInt64(rsp + offset + 8, out var value)) // +8 to skip return address
        {
            return 0;
        }
        return value;
    }

    private static string PadString(string str, int width, bool leftAlign, bool padWithZero)
    {
        if (width <= str.Length)
            return str;

        var padChar = padWithZero ? '0' : ' ';
        var padLength = width - str.Length;
        var padding = new string(padChar, padLength);

        return leftAlign ? str + padding : padding + str;
    }

    private interface IPrintfArgumentSource
    {
        ulong NextGpArg();

        double NextFloatArg();
    }

    private struct RegisterPrintfArgumentSource : IPrintfArgumentSource
    {
        private readonly CpuContext _ctx;
        private int _gpIndex;
        private int _fpIndex;
        private int _stackIndex;

        public RegisterPrintfArgumentSource(CpuContext ctx, int gpIndex)
        {
            _ctx = ctx;
            _gpIndex = gpIndex;
            _fpIndex = 0;
            _stackIndex = 0;
        }

        public ulong NextGpArg()
        {
            var index = _gpIndex;
            if (index < 6)
            {
                _gpIndex++;
                return index switch
                {
                    0 => _ctx[CpuRegister.Rdi],
                    1 => _ctx[CpuRegister.Rsi],
                    2 => _ctx[CpuRegister.Rdx],
                    3 => _ctx[CpuRegister.Rcx],
                    4 => _ctx[CpuRegister.R8],
                    _ => _ctx[CpuRegister.R9],
                };
            }

            return ReadStackArg(_ctx, (ulong)(_stackIndex++) * 8);
        }

        public double NextFloatArg()
        {
            ulong bits;
            if (_fpIndex < 8)
            {
                _ctx.GetXmmRegister(_fpIndex++, out bits, out _);
            }
            else
            {
                bits = ReadStackArg(_ctx, (ulong)(_stackIndex++) * 8);
            }

            return BitConverter.Int64BitsToDouble(unchecked((long)bits));
        }
    }

    private struct SysVAmd64VaListCursor : IPrintfArgumentSource
    {
        private const uint GpSaveAreaLimit = 48;
        private const uint FpSaveAreaLimit = 176;

        private readonly CpuContext _ctx;
        private readonly ulong _vaListAddress;
        private uint _gpOffset;
        private uint _fpOffset;
        private ulong _overflowArgArea;
        private readonly ulong _regSaveArea;

        public SysVAmd64VaListCursor(
            CpuContext ctx,
            ulong vaListAddress,
            uint gpOffset,
            uint fpOffset,
            ulong overflowArgArea,
            ulong regSaveArea)
        {
            _ctx = ctx;
            _vaListAddress = vaListAddress;
            _gpOffset = gpOffset;
            _fpOffset = fpOffset;
            _overflowArgArea = overflowArgArea;
            _regSaveArea = regSaveArea;
        }

        public ulong NextGpArg()
        {
            ulong readAddress;
            if (_regSaveArea != 0 && _gpOffset <= GpSaveAreaLimit - 8)
            {
                readAddress = _regSaveArea + _gpOffset;
                _gpOffset += 8;
            }
            else
            {
                readAddress = _overflowArgArea;
                _overflowArgArea += 8;
            }

            return TryReadUInt64Compat(_ctx, readAddress, out var value) ? value : 0;
        }

        public double NextFloatArg()
        {
            ulong readAddress;
            if (_regSaveArea != 0 && _fpOffset <= FpSaveAreaLimit - 16)
            {
                readAddress = _regSaveArea + _fpOffset;
                _fpOffset += 16;
            }
            else
            {
                readAddress = _overflowArgArea;
                _overflowArgArea += 8;
            }

            return TryReadUInt64Compat(_ctx, readAddress, out var rawBits)
                ? BitConverter.Int64BitsToDouble(unchecked((long)rawBits))
                : 0.0;
        }

        public uint GpOffset => _gpOffset;

        public uint FpOffset => _fpOffset;

        public ulong OverflowArgArea => _overflowArgArea;

        public ulong RegSaveArea => _regSaveArea;
    }

    private static ulong AllocateMappedGuestAddress(CpuContext ctx, ulong length, ulong alignment)
    {
        if (length == 0)
        {
            return 0;
        }

        var effectiveAlignment = alignment == 0 ? OrbisPageSize : alignment;
        if (_nextVirtualAddress == 0)
        {
            _nextVirtualAddress = 0x0100_0000UL;
        }

        var probeCandidates = new[]
        {
            8UL * 1024 * 1024,
            2UL * 1024 * 1024,
            512UL * 1024,
            128UL * 1024,
            0x1000UL,
        };

        foreach (var probeCandidate in probeCandidates)
        {
            var cursor = AlignUp(_nextVirtualAddress, effectiveAlignment);
            for (var i = 0; i < 0x4000; i++)
            {
                if (IsMappedGuestRangeAvailable(ctx, cursor, length, probeCandidate))
                {
                    _nextVirtualAddress = cursor + length;
                    return cursor;
                }

                cursor = AlignUp(cursor + 0x1000UL, effectiveAlignment);
            }
        }

        return 0;
    }

    private static bool TryReserveGuestVirtualRange(
        CpuContext ctx,
        ulong desiredAddress,
        ulong length,
        int protection,
        ulong alignment,
        out ulong mappedAddress)
    {
        var executable = (protection & OrbisProtCpuExec) != 0;
        return KernelVirtualRangeAllocator.TryReserve(
            ctx,
            desiredAddress,
            length,
            executable,
            alignment,
            allowSearch: true,
            allowAllocateAtAlternative: false,
            "reserve range",
            out mappedAddress);
    }

    private static bool TryReserveExactGuestVirtualRange(
        CpuContext ctx,
        ulong desiredAddress,
        ulong length,
        int protection)
    {
        var executable = (protection & OrbisProtCpuExec) != 0;
        return KernelVirtualRangeAllocator.TryReserve(
            ctx,
            desiredAddress,
            length,
            executable,
            alignment: 0,
            allowSearch: false,
            allowAllocateAtAlternative: false,
            "reserve fixed range",
            out _,
            backPartialOverlap: true);
    }

    internal static bool IsGuestRangeBacked(CpuContext ctx, ulong address, ulong length)
    {
        if (address == 0 || length == 0 || ulong.MaxValue - address < length - 1)
        {
            return false;
        }

        Span<byte> probe = stackalloc byte[1];
        return ctx.Memory.TryRead(address, probe) &&
               ctx.Memory.TryRead(address + length - 1, probe);
    }

    private static bool IsMappedGuestRangeAvailable(
        CpuContext ctx,
        ulong address,
        ulong length,
        ulong minimumReadableSpan)
    {
        if (length == 0)
        {
            return false;
        }

        if (ulong.MaxValue - address < length - 1)
        {
            return false;
        }

        var end = address + length - 1;
        foreach (var region in _mappedRegions.Values)
        {
            var regionEnd = region.Address + region.Length - 1;
            if (address <= regionEnd && end >= region.Address)
            {
                return false;
            }
        }

        var probeLength = Math.Min(length, Math.Max(0x1000UL, minimumReadableSpan));
        var probeEnd = address + probeLength - 1;
        Span<byte> probe = stackalloc byte[1];
        return ctx.Memory.TryRead(address, probe) &&
               ctx.Memory.TryRead(probeEnd, probe);
    }

    private static FileAccess ResolveOpenAccess(int flags)
    {
        if ((flags & O_RDWR) == O_RDWR)
        {
            return FileAccess.ReadWrite;
        }

        if ((flags & O_WRONLY) == O_WRONLY)
        {
            return FileAccess.Write;
        }

        return FileAccess.Read;
    }

    private static FileMode ResolveOpenMode(int flags, FileAccess access)
    {
        var create = (flags & O_CREAT) != 0;
        var truncate = (flags & O_TRUNC) != 0;
        if (create && truncate)
        {
            return FileMode.Create;
        }

        if (create)
        {
            return FileMode.OpenOrCreate;
        }

        if (truncate)
        {
            return access == FileAccess.Read ? FileMode.Open : FileMode.Truncate;
        }

        return FileMode.Open;
    }

    public static string ResolveGuestPath(string guestPath)
    {
        if (string.IsNullOrWhiteSpace(guestPath))
        {
            return guestPath;
        }

        if (TryResolveRegisteredGuestMount(guestPath, out var mountedPath, out var mountPrefixMatched))
        {
            return mountedPath;
        }

        // A registered mount claimed this path by prefix but denied it (failed
        // containment or a reparse point inside the mount). That denial is
        // authoritative: do NOT fall through to the built-in branches below,
        // which could re-resolve an overlapping prefix (e.g. a registered
        // "/app0" vs the built-in SHARPEMU_APP0_DIR branch) against a different
        // root and turn the denial back into a resolution.
        if (mountPrefixMatched)
        {
            return string.Empty;
        }

        if (guestPath.StartsWith("/devlog/app/", StringComparison.OrdinalIgnoreCase))
        {
            var relative = NormalizeMountRelativePath(guestPath["/devlog/app/".Length..]);
            return CombineWithinMount(ResolveDevlogAppRoot(), relative);
        }

        if (guestPath.StartsWith("devlog/app/", StringComparison.OrdinalIgnoreCase))
        {
            var relative = NormalizeMountRelativePath(guestPath["devlog/app/".Length..]);
            return CombineWithinMount(ResolveDevlogAppRoot(), relative);
        }

        if (string.Equals(guestPath, "/devlog/app", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(guestPath, "devlog/app", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveDevlogAppRoot();
        }

        if (guestPath.StartsWith("/temp0/", StringComparison.OrdinalIgnoreCase))
        {
            var relative = NormalizeMountRelativePath(guestPath["/temp0/".Length..]);
            return CombineWithinMount(ResolveTemp0Root(), relative);
        }

        if (string.Equals(guestPath, "/temp0", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveTemp0Root();
        }

        if (guestPath.StartsWith("/download0/", StringComparison.OrdinalIgnoreCase))
        {
            var relative = NormalizeMountRelativePath(guestPath["/download0/".Length..]);
            return CombineWithinMount(ResolveDownload0Root(), relative);
        }

        if (guestPath.StartsWith("download0/", StringComparison.OrdinalIgnoreCase))
        {
            var relative = NormalizeMountRelativePath(guestPath["download0/".Length..]);
            return CombineWithinMount(ResolveDownload0Root(), relative);
        }

        if (string.Equals(guestPath, "/download0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(guestPath, "download0", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveDownload0Root();
        }

        if (guestPath.StartsWith("/hostapp/", StringComparison.OrdinalIgnoreCase))
        {
            var relative = NormalizeMountRelativePath(guestPath["/hostapp/".Length..]);
            return CombineWithinMount(ResolveHostappRoot(), relative);
        }

        if (guestPath.StartsWith("hostapp/", StringComparison.OrdinalIgnoreCase))
        {
            var relative = NormalizeMountRelativePath(guestPath["hostapp/".Length..]);
            return CombineWithinMount(ResolveHostappRoot(), relative);
        }

        if (string.Equals(guestPath, "/hostapp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(guestPath, "hostapp", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveHostappRoot();
        }

        var app0Root = ResolveApp0Root();
        if (!string.IsNullOrWhiteSpace(app0Root))
        {
            if (string.Equals(guestPath, "$", StringComparison.Ordinal) ||
                string.Equals(guestPath, "$/", StringComparison.Ordinal) ||
                string.Equals(guestPath, "$\\", StringComparison.Ordinal))
            {
                return app0Root;
            }

            if (guestPath.StartsWith("$/", StringComparison.Ordinal) ||
                guestPath.StartsWith("$\\", StringComparison.Ordinal))
            {
                var relative = NormalizeMountRelativePath(guestPath[2..]);
                return CombineWithinMount(app0Root, relative);
            }

            if (string.Equals(guestPath, "/app0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(guestPath, "app0", StringComparison.OrdinalIgnoreCase))
            {
                return app0Root;
            }

            if (guestPath.StartsWith("/app0/", StringComparison.OrdinalIgnoreCase))
            {
                var relative = NormalizeMountRelativePath(guestPath["/app0/".Length..]);
                return CombineWithinMount(app0Root, relative);
            }

            if (guestPath.StartsWith("app0/", StringComparison.OrdinalIgnoreCase))
            {
                var relative = NormalizeMountRelativePath(guestPath["app0/".Length..]);
                return CombineWithinMount(app0Root, relative);
            }

            if (!Path.IsPathFullyQualified(guestPath) &&
                !guestPath.StartsWith("/", StringComparison.Ordinal) &&
                !guestPath.StartsWith("\\", StringComparison.Ordinal))
            {
                var relative = NormalizeMountRelativePath(guestPath);
                return CombineWithinMount(app0Root, relative);
            }
        }

        // Default-deny: a guest path that matched no mount prefix must NOT be
        // handed back verbatim as a host path. Returning it raw let any absolute
        // guest path address the host filesystem directly ("/etc/passwd",
        // "C:\\Windows\\...") because it is already fully qualified and skips the
        // relative-path app0 fallback above. Callers treat an empty host path as
        // "resolves to nothing" and fail the syscall with NOT_FOUND.
        return string.Empty;
    }

    private static bool TryResolveRegisteredGuestMount(
        string guestPath,
        out string hostPath,
        out bool mountPrefixMatched)
    {
        hostPath = string.Empty;
        mountPrefixMatched = false;
        var normalizedGuestPath = NormalizeGuestStatCachePath(guestPath);
        if (normalizedGuestPath is null)
        {
            return false;
        }

        string? matchedMountPoint = null;
        string? matchedHostRoot = null;
        lock (_guestMountGate)
        {
            foreach (var (mountPoint, hostRoot) in _guestMounts)
            {
                if ((string.Equals(normalizedGuestPath, mountPoint, StringComparison.OrdinalIgnoreCase) ||
                     normalizedGuestPath.StartsWith(mountPoint + "/", StringComparison.OrdinalIgnoreCase)) &&
                    (matchedMountPoint is null || mountPoint.Length > matchedMountPoint.Length))
                {
                    matchedMountPoint = mountPoint;
                    matchedHostRoot = hostRoot;
                }
            }
        }

        if (matchedMountPoint is null || matchedHostRoot is null)
        {
            return false;
        }

        // A registered mount owns this prefix. Whatever the outcome below
        // (containment or reparse denial), the caller must NOT fall through to a
        // built-in branch for the same prefix, or a denied path could be
        // re-resolved against a different root.
        mountPrefixMatched = true;

        var relativePath = normalizedGuestPath[matchedMountPoint.Length..].TrimStart('/');
        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(
                matchedHostRoot,
                NormalizeMountRelativePath(relativePath)));
        }
        catch (Exception ex) when (
            ex is IOException or ArgumentException or NotSupportedException)
        {
            // The relative part comes from an untrusted guest path; a crafted
            // over-long or invalid path can make GetFullPath throw. Fail closed
            // rather than propagate out of ResolveGuestPath (which callers invoke
            // outside their try blocks).
            return false;
        }

        var rootWithSeparator = Path.TrimEndingDirectorySeparator(matchedHostRoot) + Path.DirectorySeparatorChar;
        // Host-semantics comparison: an ignore-case check on a case-sensitive
        // host would let a relative path escape into a sibling directory that
        // differs from the mount root only by case (root ".../Save" vs
        // sibling ".../save").
        if (!string.Equals(candidate, matchedHostRoot, HostFsPathComparison) &&
            !candidate.StartsWith(rootWithSeparator, HostFsPathComparison))
        {
            return false;
        }

        // Textual containment does not follow symlinks/junctions; refuse a
        // reparse point planted inside the mount that would redirect onto the
        // host filesystem. See EscapesMountViaReparsePoint.
        if (EscapesMountViaReparsePoint(Path.GetFullPath(matchedHostRoot), candidate))
        {
            return false;
        }

        hostPath = candidate;
        return true;
    }

    private static string? ResolveApp0Root()
    {
        var cached = Volatile.Read(ref _cachedApp0Root);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        var configured = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        Interlocked.CompareExchange(ref _cachedApp0Root, configured, null);
        return _cachedApp0Root;
    }

    // Resolves "." and ".." inside a mount-relative guest path and clamps the
    // result at the mount root, so a guest path can never escape into the host
    // filesystem. Unreal Engine titles depend on this: their base directory is
    // <app>/binaries/<platform>, so they address content with "../../../"
    // prefixes that land back inside /app0 on real hardware. Combining those
    // raw against the app0 root walked out of the game folder entirely, so the
    // title enumerated an unrelated host directory and never found its .pak files.
    private static string NormalizeMountRelativePath(string relativePath)
    {
        var segments = relativePath.Split(
            new[] { '/', '\\' },
            StringSplitOptions.RemoveEmptyEntries);
        var resolved = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (resolved.Count > 0)
                {
                    resolved.RemoveAt(resolved.Count - 1);
                }

                continue;
            }

            resolved.Add(segment);
        }

        return string.Join(Path.DirectorySeparatorChar, resolved);
    }

    // Combines a mount-relative guest path onto a built-in mount root and
    // re-verifies the result stays under that root. NormalizeMountRelativePath
    // strips "." / ".." but splits only on separators, so a drive-qualified
    // token like "C:" survives as a segment; Path.Combine then DISCARDS the
    // mount root because its second argument is drive-rooted, yielding a raw
    // host path such as "C:\Windows\...". Re-resolving with Path.GetFullPath and
    // checking containment (the same guard TryResolveRegisteredGuestMount uses)
    // rejects that escape. Returns string.Empty on denial, which callers treat
    // as an unresolved path.
    private static string CombineWithinMount(string mountRoot, string relative)
    {
        string fullRoot;
        string candidate;
        try
        {
            fullRoot = Path.GetFullPath(mountRoot);
            candidate = Path.GetFullPath(Path.Combine(fullRoot, relative));
        }
        catch (Exception ex) when (
            ex is IOException or ArgumentException or NotSupportedException)
        {
            // The relative part comes from an untrusted guest path; a crafted
            // over-long or invalid path can make GetFullPath throw. Fail closed
            // rather than propagate out of ResolveGuestPath (which callers invoke
            // outside their try blocks).
            return string.Empty;
        }

        var rootWithSeparator =
            Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar;
        if (!string.Equals(candidate, fullRoot, HostFsPathComparison) &&
            !candidate.StartsWith(rootWithSeparator, HostFsPathComparison))
        {
            return string.Empty;
        }

        if (EscapesMountViaReparsePoint(fullRoot, candidate))
        {
            return string.Empty;
        }

        return candidate;
    }

    // Lexical containment (Path.GetFullPath + StartsWith) proves the TEXTUAL
    // path stays under the mount root, but it does not follow symlinks or
    // Windows junctions. A malicious game dump can plant a reparse point inside
    // an otherwise-contained mount (e.g. "/app0/link" -> "/") so that
    // "/app0/link/etc/passwd" passes the textual check yet resolves onto the
    // host filesystem. Walk each already-existing component from the mount root
    // down to the candidate and refuse if any is a reparse point. Components
    // that do not yet exist (e.g. an O_CREAT target and its parents) carry no
    // link to follow and are simply skipped. Mirrors the reparse rejection in
    // AvPlayerExports.TryResolveSandboxedFile.
    private static bool EscapesMountViaReparsePoint(string mountRoot, string candidate)
    {
        var rootTrimmed = Path.TrimEndingDirectorySeparator(mountRoot);
        if (string.Equals(candidate, rootTrimmed, HostFsPathComparison))
        {
            return false;
        }

        var relative = Path.GetRelativePath(rootTrimmed, candidate);
        // A leading ".." segment means the candidate is not under the root. Match
        // the segment precisely: bare ".." or a "../" prefix, NOT a legitimate
        // file merely named "..foo". This branch is a defensive fallback (lexical
        // containment already passed before it runs), so it fails closed.
        if (relative == "." || relative == ".." || Path.IsPathRooted(relative) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            // Not actually under the root (should have been caught lexically);
            // treat as an escape rather than walk outside it.
            return true;
        }

        var current = rootTrimmed;
        foreach (var segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Component does not exist yet (create path); nothing to follow.
                break;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // The path comes from an untrusted dump, so a crafted over-long,
                // invalid-char, or unreadable intermediate component can make
                // GetAttributes throw. Fail closed: if containment cannot be
                // verified, treat it as an escape rather than let the exception
                // crash the syscall (ResolveGuestPath runs outside the callers'
                // try blocks).
                return true;
            }
        }

        return false;
    }

    private static string ResolveDevlogAppRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("SHARPEMU_DEVLOG_APP_DIR");
        string root;
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            root = Path.GetFullPath(configuredRoot);
        }
        else
        {
            root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "logs", "devlog", "app"));
        }

        Directory.CreateDirectory(root);
        return root;
    }

    private static string ResolveTemp0Root()
    {
        const string temp0VariableName = "SHARPEMU_TEMP0_DIR";
        var configuredRoot = Environment.GetEnvironmentVariable(temp0VariableName);
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot);
        }

        var app0Root = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        var appName = string.IsNullOrWhiteSpace(app0Root)
            ? "default"
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(app0Root));
        if (string.IsNullOrWhiteSpace(appName))
        {
            appName = "default";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        appName = new string(appName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
        var root = Path.Combine(Path.GetTempPath(), "SharpEmu", appName, "temp0");
        Environment.SetEnvironmentVariable(temp0VariableName, root);
        return root;
    }

    private static string ResolveDownload0Root()
    {
        var cached = Volatile.Read(ref _cachedDownload0Root);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        const string download0VariableName = "SHARPEMU_DOWNLOAD0_DIR";
        var configuredRoot = Environment.GetEnvironmentVariable(download0VariableName);
        string root;
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            root = Path.GetFullPath(configuredRoot);
        }
        else
        {
            root = Path.Combine(GetPerAppWritableRoot(), "download0");
            Environment.SetEnvironmentVariable(download0VariableName, root);
        }

        Directory.CreateDirectory(root);
        Interlocked.CompareExchange(ref _cachedDownload0Root, root, null);
        return _cachedDownload0Root;
    }

    private static string ResolveHostappRoot()
    {
        const string hostappVariableName = "SHARPEMU_HOSTAPP_DIR";
        var configuredRoot = Environment.GetEnvironmentVariable(hostappVariableName);
        string root;
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            root = Path.GetFullPath(configuredRoot);
        }
        else
        {
            root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "logs", "hostapp"));
            Environment.SetEnvironmentVariable(hostappVariableName, root);
        }

        Directory.CreateDirectory(root);
        return root;
    }

    private static string GetPerAppWritableRoot()
    {
        var app0Root = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        var appName = string.IsNullOrWhiteSpace(app0Root)
            ? "default"
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(app0Root));
        if (string.IsNullOrWhiteSpace(appName))
        {
            appName = "default";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        appName = new string(appName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
        return Path.Combine(Path.GetTempPath(), "SharpEmu", appName);
    }

    private static void EnsureOpenParentDirectoryExists(string guestPath, string hostPath, int flags)
    {
        if (string.IsNullOrWhiteSpace(hostPath))
        {
            return;
        }

        var shouldCreateParent =
            (flags & O_CREAT) != 0 ||
            guestPath.StartsWith("/devlog/app/", StringComparison.OrdinalIgnoreCase) ||
            guestPath.StartsWith("devlog/app/", StringComparison.OrdinalIgnoreCase);
        if (!shouldCreateParent)
        {
            return;
        }

        var parentDirectory = Path.GetDirectoryName(hostPath);
        if (!string.IsNullOrWhiteSpace(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }
    }

    private static bool IsMutatingOpen(int flags) =>
        (flags & (O_WRONLY | O_RDWR | O_CREAT | O_TRUNC | O_APPEND)) != 0;

    // Dev-build dumps (unpackaged UE titles, etc.) may write their Saved/ tree under
    // /app0, which is read-only on retail hardware. Opt in via SHARPEMU_WRITABLE_APP0=1
    // to allow those writes so such dumps can boot; defaults off to keep retail semantics.
    private static readonly bool _writableApp0 =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_WRITABLE_APP0"), "1", StringComparison.Ordinal);

    public static bool IsReadOnlyGuestMutationPath(string guestPath)
    {
        if (_writableApp0)
        {
            return false;
        }

        var normalized = NormalizeGuestStatCachePath(guestPath);
        return normalized is not null &&
               (string.Equals(normalized, "/app0", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("/app0/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryReadCString(CpuContext ctx, ulong address, ulong maxLength, out byte[] bytes)
    {
        return TryReadBytesUntilNull(ctx, address, maxLength, 1_048_576, out bytes);
    }

    private static bool TryReadBytesUntilNull(
        CpuContext ctx,
        ulong address,
        ulong maxLength,
        int hardLimit,
        out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (address == 0)
        {
            return false;
        }

        var limit = (int)Math.Min(maxLength, (ulong)Math.Max(0, hardLimit));
        if (limit == 0)
        {
            return true;
        }

        const int maxChunkSize = 4096;
        const int inlineChunkSize = 256;
        Span<byte> inlineChunk = stackalloc byte[inlineChunkSize];
        var firstPageRemaining = maxChunkSize - (int)(address & (maxChunkSize - 1));
        var firstReadLength = Math.Min(limit, Math.Min(inlineChunkSize, firstPageRemaining));
        var firstSpan = inlineChunk[..firstReadLength];
        ulong offset = 0;
        if (TryReadCompat(ctx, address, firstSpan))
        {
            var nulIndex = firstSpan.IndexOf((byte)0);
            if (nulIndex >= 0)
            {
                bytes = firstSpan[..nulIndex].ToArray();
                return true;
            }

            offset = unchecked((ulong)firstReadLength);
        }

        var chunk = GC.AllocateUninitializedArray<byte>(Math.Min(maxChunkSize, limit));
        var writer = new ArrayBufferWriter<byte>(Math.Min(limit, 256));
        if (offset != 0)
        {
            firstSpan.CopyTo(writer.GetSpan(firstReadLength));
            writer.Advance(firstReadLength);
        }

        Span<byte> one = stackalloc byte[1];
        while (offset < (ulong)limit)
        {
            var current = address + offset;
            if (current < address)
            {
                return false;
            }

            var pageRemaining = maxChunkSize - (int)(current & (maxChunkSize - 1));
            var remaining = (int)Math.Min((ulong)limit - offset, (ulong)Math.Min(chunk.Length, pageRemaining));
            var span = chunk.AsSpan(0, remaining);
            if (TryReadCompat(ctx, current, span))
            {
                var nulIndex = span.IndexOf((byte)0);
                var copyLength = nulIndex >= 0 ? nulIndex : remaining;
                if (copyLength > 0)
                {
                    span[..copyLength].CopyTo(writer.GetSpan(copyLength));
                    writer.Advance(copyLength);
                }

                if (nulIndex >= 0)
                {
                    bytes = writer.WrittenSpan.ToArray();
                    return true;
                }

                offset += (ulong)remaining;
                continue;
            }

            if (!TryReadCompat(ctx, current, one))
            {
                return false;
            }

            if (one[0] == 0)
            {
                bytes = writer.WrittenSpan.ToArray();
                return true;
            }

            one.CopyTo(writer.GetSpan(1));
            writer.Advance(1);
            offset++;
        }

        bytes = writer.WrittenSpan.ToArray();
        return true;
    }

    private static bool TryCompareStrings(CpuContext ctx, ulong left, ulong right, ulong limit, out int compare)
    {
        compare = 0;
        if (left == 0 || right == 0)
        {
            return false;
        }

        var max = limit == ulong.MaxValue ? 1_048_576UL : Math.Min(limit, 1_048_576UL);
        Span<byte> leftByte = stackalloc byte[1];
        Span<byte> rightByte = stackalloc byte[1];
        for (ulong i = 0; i < max; i++)
        {
            if (!TryReadCompat(ctx, left + i, leftByte) ||
                !TryReadCompat(ctx, right + i, rightByte))
            {
                return false;
            }

            compare = leftByte[0] - rightByte[0];
            if (compare != 0 || leftByte[0] == 0 || rightByte[0] == 0)
            {
                return true;
            }
        }

        compare = 0;
        return true;
    }

    private static bool TryReadWideCString(CpuContext ctx, ulong address, ulong maxLength, out ushort[] units)
    {
        return TryReadWideCStringCore(ctx, address, maxLength, out units, out _);
    }

    private static bool TryReadWideCStringBounded(CpuContext ctx, ulong address, ulong maxLength, out ushort[] units, out bool terminated)
    {
        return TryReadWideCStringCore(ctx, address, maxLength, out units, out terminated);
    }

    private static bool TryReadWideCStringCore(
        CpuContext ctx,
        ulong address,
        ulong maxLength,
        out ushort[] units,
        out bool terminated)
    {
        units = Array.Empty<ushort>();
        terminated = false;
        if (address == 0)
        {
            return false;
        }

        var limit = (int)Math.Min(maxLength, 1_048_576UL);
        var buffer = new List<ushort>(Math.Min(limit, 256));
        const int maxReadBytes = 4096;
        var readBuffer = GC.AllocateUninitializedArray<byte>(
            Math.Min(maxReadBytes, Math.Max(WideCharSize, limit * WideCharSize)));
        var index = 0;
        while (index < limit)
        {
            var unitAddress = address + ((ulong)index * WideCharSize);
            if (unitAddress < address)
            {
                return false;
            }

            var remainingBytes = (limit - index) * WideCharSize;
            var pageBytesRemaining = maxReadBytes -
                (int)(unitAddress & (maxReadBytes - 1));
            var readBytes = Math.Min(
                readBuffer.Length,
                Math.Min(pageBytesRemaining, remainingBytes));
            readBytes &= ~(WideCharSize - 1);
            if (readBytes == 0 ||
                !TryReadCompat(ctx, unitAddress, readBuffer.AsSpan(0, readBytes)))
            {
                return false;
            }

            for (var offset = 0; offset < readBytes; offset += WideCharSize)
            {
                var unit = BinaryPrimitives.ReadUInt16LittleEndian(
                    readBuffer.AsSpan(offset, WideCharSize));
                if (unit == 0)
                {
                    terminated = true;
                    units = buffer.ToArray();
                    return true;
                }

                buffer.Add(unit);
                index++;
            }
        }

        units = buffer.ToArray();
        return true;
    }

    private static bool TryCompareWideStrings(CpuContext ctx, ulong left, ulong right, ulong limit, out int compare)
    {
        compare = 0;
        if (left == 0 || right == 0)
        {
            return false;
        }

        var max = limit == ulong.MaxValue ? 1_048_576UL : Math.Min(limit, 1_048_576UL);
        for (ulong i = 0; i < max; i++)
        {
            if (!TryReadUInt16Compat(ctx, left + (i * WideCharSize), out var leftUnit) ||
                !TryReadUInt16Compat(ctx, right + (i * WideCharSize), out var rightUnit))
            {
                return false;
            }

            compare = leftUnit == rightUnit ? 0 : leftUnit < rightUnit ? -1 : 1;
            if (compare != 0 || leftUnit == 0 || rightUnit == 0)
            {
                return true;
            }
        }

        compare = 0;
        return true;
    }

    private static byte[] EncodeWideUnits(ReadOnlySpan<ushort> units)
    {
        var bytes = new byte[units.Length * WideCharSize];
        for (var i = 0; i < units.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(i * WideCharSize, WideCharSize),
                units[i]);
        }

        return bytes;
    }

    private static string DecodeWideUnits(ReadOnlySpan<ushort> units)
    {
        if (units.IsEmpty)
        {
            return string.Empty;
        }

        return new string(MemoryMarshal.Cast<ushort, char>(units));
    }

    private static bool TryConvertWideScalarToString(ushort scalar, out string text)
    {
        text = ((char)scalar).ToString();
        return true;
    }

    private static byte[] EncodeWideUnitsWithTerminator(ReadOnlySpan<ushort> units)
    {
        var bytes = new byte[(units.Length + 1) * WideCharSize];
        EncodeWideUnits(units).CopyTo(bytes, 0);
        return bytes;
    }

    private static bool TryWriteWideTerminator(CpuContext ctx, ulong address)
    {
        Span<byte> terminator = stackalloc byte[WideCharSize];
        terminator.Clear();
        return TryWriteCompat(ctx, address, terminator);
    }

    private static bool TryZeroWideDestination(CpuContext ctx, ulong destination, ulong destinationCount)
    {
        return destination == 0 || destinationCount == 0 || TryWriteWideTerminator(ctx, destination);
    }

    public static bool TryReadNullTerminatedUtf8(CpuContext ctx, ulong address, int maxLength, out string value)
    {
        value = string.Empty;
        if (address == 0 || maxLength <= 0)
        {
            return false;
        }

        if (!TryReadBytesUntilNull(ctx, address, (ulong)maxLength, maxLength, out var bytes))
        {
            return false;
        }

        value = Encoding.UTF8.GetString(bytes);
        return true;
    }

    private static bool TryResolveAprFilepath(CpuContext ctx, ulong pathListAddress, ulong index, out string guestPath)
    {
        guestPath = string.Empty;
        if (TryReadAprPathPointer(ctx, pathListAddress + (index * sizeof(ulong)), out guestPath))
        {
            return true;
        }

        if (index != 0)
        {
            return false;
        }

        if (TryReadAprPathText(ctx, pathListAddress, out guestPath))
        {
            return true;
        }

        const ulong scanLimit = 0x40;
        for (ulong offset = 0; offset < scanLimit; offset += sizeof(ulong))
        {
            if (TryReadAprPathPointer(ctx, pathListAddress + offset, out guestPath))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadAprPathPointer(CpuContext ctx, ulong pointerAddress, out string guestPath)
    {
        guestPath = string.Empty;
        if (!ctx.TryReadUInt64(pointerAddress, out var candidatePath) || candidatePath == 0)
        {
            return false;
        }

        return TryReadAprPathText(ctx, candidatePath, out guestPath);
    }

    private static bool TryReadAprPathText(CpuContext ctx, ulong address, out string guestPath)
    {
        guestPath = string.Empty;
        if (TryReadNullTerminatedUtf8(ctx, address, MaxGuestStringLength, out guestPath) &&
            !string.IsNullOrWhiteSpace(guestPath))
        {
            return true;
        }

        if (!TryReadWideCString(ctx, address, MaxGuestStringLength, out var wideUnits))
        {
            return false;
        }

        guestPath = DecodeWideUnits(wideUnits);
        return !string.IsNullOrWhiteSpace(guestPath);
    }

    private static bool TryReadCompat(CpuContext ctx, ulong address, Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            return true;
        }

        if (ctx.Memory.TryRead(address, destination))
        {
            return true;
        }

        if (!TryReadHostMemory(address, destination))
        {
            return false;
        }

        var recoveryIndex = Interlocked.Increment(ref _hostMemoryReadFallbackCount);
        if (recoveryIndex <= 8)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARNING] host-read fallback#{recoveryIndex}: addr=0x{address:X16} len=0x{destination.Length:X}");
        }

        return true;
    }

    private static bool TryReadUInt32Compat(CpuContext ctx, ulong address, out uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        if (!TryReadCompat(ctx, address, bytes))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return true;
    }

    private static bool TryReadUInt16Compat(CpuContext ctx, ulong address, out ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        if (!TryReadCompat(ctx, address, bytes))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        return true;
    }

    internal static bool TryReadUInt64Compat(CpuContext ctx, ulong address, out ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        if (!TryReadCompat(ctx, address, bytes))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        return true;
    }

    private static bool TryWriteCompat(CpuContext ctx, ulong address, ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty)
        {
            return true;
        }

        if (ctx.Memory.TryWrite(address, source))
        {
            return true;
        }

        if (!TryWriteHostMemory(address, source))
        {
            return false;
        }

        var recoveryIndex = Interlocked.Increment(ref _hostMemoryWriteFallbackCount);
        if (recoveryIndex <= 8)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARNING] host-write fallback#{recoveryIndex}: addr=0x{address:X16} len=0x{source.Length:X}");
        }

        return true;
    }

    private static bool TryWriteUInt32Compat(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return TryWriteCompat(ctx, address, bytes);
    }

    internal static bool TryWriteUInt64Compat(CpuContext ctx, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return TryWriteCompat(ctx, address, bytes);
    }

    private static int KernelBatchMapCore(CpuContext ctx, int flags)
    {
        var entriesAddress = ctx[CpuRegister.Rdi];
        var entryCount = unchecked((int)ctx[CpuRegister.Rsi]);
        var processedOutAddress = ctx[CpuRegister.Rdx];
        var processedCount = 0;
        var result = (int)OrbisGen2Result.ORBIS_GEN2_OK;

        for (var index = 0; index < entryCount; index++)
        {
            var entryAddress = entriesAddress + (ulong)(index * OrbisKernelBatchMapEntrySize);
            if (!TryReadBatchMapEntry(ctx, entryAddress, out var entry) ||
                entry.Length == 0 ||
                entry.Operation < OrbisKernelMapOpMapDirect ||
                entry.Operation > OrbisKernelMapOpTypeProtect)
            {
                result = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
                break;
            }

            result = entry.Operation switch
            {
                OrbisKernelMapOpMapDirect => InvokeKernelMemoryOperation(
                    ctx,
                    KernelMapDirectMemory,
                    entryAddress + OrbisKernelBatchMapEntryStartOffset,
                    entry.Length,
                    entry.Protection,
                    unchecked((ulong)(uint)flags),
                    entry.Offset,
                    0),
                OrbisKernelMapOpUnmap => InvokeKernelMemoryOperation(
                    ctx,
                    KernelMunmap,
                    entry.Start,
                    entry.Length),
                OrbisKernelMapOpProtect => InvokeKernelMemoryOperation(
                    ctx,
                    KernelMprotect,
                    entry.Start,
                    entry.Length,
                    entry.Protection),
                OrbisKernelMapOpMapFlexible => InvokeKernelMemoryOperation(
                    ctx,
                    KernelMapNamedFlexibleMemory,
                    entryAddress + OrbisKernelBatchMapEntryStartOffset,
                    entry.Length,
                    entry.Protection,
                    unchecked((ulong)(uint)flags)),
                OrbisKernelMapOpTypeProtect => InvokeKernelMemoryOperation(
                    ctx,
                    KernelMtypeprotect,
                    entry.Start,
                    entry.Length,
                    entry.Type,
                    entry.Protection),
                _ => (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            };

            if (result != (int)OrbisGen2Result.ORBIS_GEN2_OK)
            {
                break;
            }

            processedCount++;
        }

        if (processedOutAddress != 0 && !TryWriteInt32(ctx, processedOutAddress, processedCount))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return result;
    }

    private static int InvokeKernelMemoryOperation(
        CpuContext ctx,
        Func<CpuContext, int> operation,
        ulong rdi = 0,
        ulong rsi = 0,
        ulong rdx = 0,
        ulong rcx = 0,
        ulong r8 = 0,
        ulong r9 = 0)
    {
        var savedRdi = ctx[CpuRegister.Rdi];
        var savedRsi = ctx[CpuRegister.Rsi];
        var savedRdx = ctx[CpuRegister.Rdx];
        var savedRcx = ctx[CpuRegister.Rcx];
        var savedR8 = ctx[CpuRegister.R8];
        var savedR9 = ctx[CpuRegister.R9];

        ctx[CpuRegister.Rdi] = rdi;
        ctx[CpuRegister.Rsi] = rsi;
        ctx[CpuRegister.Rdx] = rdx;
        ctx[CpuRegister.Rcx] = rcx;
        ctx[CpuRegister.R8] = r8;
        ctx[CpuRegister.R9] = r9;

        try
        {
            return operation(ctx);
        }
        finally
        {
            ctx[CpuRegister.Rdi] = savedRdi;
            ctx[CpuRegister.Rsi] = savedRsi;
            ctx[CpuRegister.Rdx] = savedRdx;
            ctx[CpuRegister.Rcx] = savedRcx;
            ctx[CpuRegister.R8] = savedR8;
            ctx[CpuRegister.R9] = savedR9;
        }
    }

    private static bool TryReadBatchMapEntry(CpuContext ctx, ulong entryAddress, out BatchMapEntry entry)
    {
        entry = default;
        if (!ctx.TryReadUInt64(entryAddress + OrbisKernelBatchMapEntryStartOffset, out var start) ||
            !ctx.TryReadUInt64(entryAddress + OrbisKernelBatchMapEntryOffsetOffset, out var offset) ||
            !ctx.TryReadUInt64(entryAddress + OrbisKernelBatchMapEntryLengthOffset, out var length))
        {
            return false;
        }

        Span<byte> protection = stackalloc byte[1];
        Span<byte> memoryType = stackalloc byte[1];
        if (!TryReadCompat(ctx, entryAddress + OrbisKernelBatchMapEntryProtectionOffset, protection) ||
            !TryReadCompat(ctx, entryAddress + OrbisKernelBatchMapEntryTypeOffset, memoryType) ||
            !TryReadUInt32Compat(ctx, entryAddress + OrbisKernelBatchMapEntryOperationOffset, out var operation))
        {
            return false;
        }

        entry = new BatchMapEntry(start, offset, length, protection[0], memoryType[0], unchecked((int)operation));
        return true;
    }

    private static bool TryApplyMappedRegionProtectionLocked(
        ulong address,
        ulong length,
        int protection,
        int? memoryType = null)
    {
        if (length == 0 || !TryAddU64(address, length, out var endAddress))
        {
            return false;
        }

        var affected = new List<MappedRegion>();
        var cursor = address;
        // _mappedRegions is a SortedList keyed by address, so Values already
        // enumerate in ascending address order.
        foreach (var region in _mappedRegions.Values)
        {
            if (!TryAddU64(region.Address, region.Length, out var regionEnd) || regionEnd <= cursor)
            {
                continue;
            }

            if (region.Address > cursor)
            {
                return false;
            }

            affected.Add(region);
            cursor = regionEnd >= endAddress ? endAddress : regionEnd;
            if (cursor == endAddress)
            {
                break;
            }
        }

        if (cursor != endAddress)
        {
            return false;
        }

        foreach (var region in affected)
        {
            _mappedRegions.Remove(region.Address);
        }

        foreach (var region in affected)
        {
            var regionEnd = region.Address + region.Length;
            var protectStart = Math.Max(region.Address, address);
            var protectEnd = Math.Min(regionEnd, endAddress);

            if (region.Address < protectStart)
            {
                AddMappedRegionSliceLocked(region, region.Address, protectStart, region.Protection);
            }

            AddMappedRegionSliceLocked(region, protectStart, protectEnd, protection);

            if (protectEnd < regionEnd)
            {
                AddMappedRegionSliceLocked(region, protectEnd, regionEnd, region.Protection);
            }

            if (memoryType.HasValue && region.IsDirect && TryFindDirectAllocationLocked(region.DirectStart, out var allocation))
            {
                _directAllocations[allocation.Start] = allocation with { MemoryType = memoryType.Value };
            }
        }

        return true;
    }

    /// <summary>
    /// Inserts <paramref name="replacement"/> into the tracked-region table,
    /// carving it out of any regions it overlaps and preserving the parts of
    /// those regions that fall outside it.
    /// </summary>
    /// <remarks>
    /// The table is a SortedList keyed by start address, so assigning directly
    /// destroys any existing entry that happens to share a start address. That
    /// silently discarded enclosing reservations: a title reserves a large range
    /// and then commits a small mapping at the same base, and the record of
    /// everything past the small mapping disappears — leaving sceKernelVirtualQuery
    /// unable to find memory the guest legitimately owns.
    ///
    /// Carving also keeps the table non-overlapping. Previously a new region
    /// starting inside an existing one produced two overlapping entries, which
    /// the ordered scan in TryFindVirtualQueryRegionLocked is not written to
    /// expect.
    /// </remarks>
    private static void ReplaceMappedRegionRangeLocked(MappedRegion replacement)
    {
        if (replacement.Length == 0 ||
            !TryAddU64(replacement.Address, replacement.Length, out var replacementEnd))
        {
            _mappedRegions[replacement.Address] = replacement;
            return;
        }

        var start = replacement.Address;
        List<MappedRegion>? overlapping = null;
        foreach (var region in _mappedRegions.Values)
        {
            if (region.Length == 0 ||
                !TryAddU64(region.Address, region.Length, out var regionEnd))
            {
                continue;
            }

            if (region.Address < replacementEnd && regionEnd > start)
            {
                (overlapping ??= []).Add(region);
            }
        }

        if (overlapping is not null)
        {
            foreach (var region in overlapping)
            {
                _mappedRegions.Remove(region.Address);
            }

            foreach (var region in overlapping)
            {
                var regionEnd = region.Address + region.Length;
                if (region.Address < start)
                {
                    AddMappedRegionSliceLocked(region, region.Address, start, region.Protection);
                }

                if (regionEnd > replacementEnd)
                {
                    AddMappedRegionSliceLocked(region, replacementEnd, regionEnd, region.Protection);
                }
            }
        }

        _mappedRegions[start] = replacement;
    }

    private static void AddMappedRegionSliceLocked(
        MappedRegion source,
        ulong start,
        ulong end,
        int protection)
    {
        if (end <= start)
        {
            return;
        }

        var directStart = source.IsDirect
            ? unchecked(source.DirectStart + (start - source.Address))
            : 0UL;

        _mappedRegions[start] = source with
        {
            Address = start,
            Length = end - start,
            Protection = protection,
            DirectStart = directStart,
        };
    }

    private static bool TryFindDirectAllocationLocked(ulong directStart, out DirectAllocation allocation)
    {
        foreach (var candidate in _directAllocations.Values)
        {
            if (!TryAddU64(candidate.Start, candidate.Length, out var candidateEnd))
            {
                continue;
            }

            if (directStart >= candidate.Start && directStart < candidateEnd)
            {
                allocation = candidate;
                return true;
            }
        }

        allocation = default;
        return false;
    }

    private static bool TryNormalizeProtectRange(
        ulong address,
        ulong length,
        out ulong alignedAddress,
        out ulong alignedLength)
    {
        alignedAddress = 0;
        alignedLength = 0;
        if (length == 0 || !TryAddU64(address, length, out var endAddress))
        {
            return false;
        }

        alignedAddress = AlignDown(address, OrbisPageSize);
        var alignedEnd = AlignUp(endAddress, OrbisPageSize);
        alignedLength = alignedEnd - alignedAddress;
        return alignedLength != 0;
    }

    private static bool TryProtectHostRange(ulong address, ulong length, int orbisProtection)
    {
        if (length == 0 || length > nuint.MaxValue)
        {
            return false;
        }

        var hostProtection = ResolveHostProtection(orbisProtection);
        if (!VirtualProtect((nint)address, (nuint)length, hostProtection, out _))
        {
            return false;
        }

        return true;
    }

    private static uint ResolveHostProtection(int orbisProtection)
    {
        var read = (orbisProtection & (OrbisProtCpuRead | OrbisProtGpuRead)) != 0;
        var write = (orbisProtection & (OrbisProtCpuWrite | OrbisProtGpuWrite)) != 0;
        var execute = (orbisProtection & OrbisProtCpuExec) != 0;

        if (execute)
        {
            return write
                ? HostPageExecuteReadWrite
                : read
                    ? HostPageExecuteRead
                    : HostPageExecute;
        }

        return write
            ? HostPageReadWrite
            : read
                ? HostPageReadOnly
                : HostPageNoAccess;
    }

    private static bool TryFindVirtualQueryRegionLocked(ulong queryAddress, bool findNext, out MappedRegion region)
    {
        region = default;
        var keys = _mappedRegions.Keys;
        var values = _mappedRegions.Values;
        var count = keys.Count;

        // First index whose region address is >= queryAddress.
        var lo = 0;
        var hi = count;
        while (lo < hi)
        {
            var mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (keys[mid] < queryAddress)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        // Regions do not overlap, so only the one with the greatest base address
        // <= queryAddress can contain it — index lo when it starts exactly at
        // queryAddress, otherwise lo - 1.
        var floorIndex = (lo < count && keys[lo] == queryAddress) ? lo : lo - 1;
        if (floorIndex >= 0)
        {
            var candidate = values[floorIndex];
            if (TryAddU64(candidate.Address, candidate.Length, out var candidateEnd) &&
                queryAddress >= candidate.Address &&
                queryAddress < candidateEnd)
            {
                region = candidate;
                return true;
            }
        }

        // findNext: the region with the smallest base address >= queryAddress.
        if (findNext && lo < count)
        {
            region = values[lo];
            return true;
        }

        return false;
    }

    private static void TraceDirectMemoryCall(
        CpuContext ctx,
        string operation,
        ulong length,
        ulong alignment,
        int memoryType,
        ulong outAddress,
        ulong selectedAddress = 0,
        OrbisGen2Result? result = null)
    {
        if (!ShouldTraceDirectMemory())
        {
            return;
        }

        var returnRip = 0UL;
        var stackPointer = ctx[CpuRegister.Rsp];
        if (stackPointer != 0)
        {
            _ = ctx.TryReadUInt64(stackPointer, out returnRip);
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] {operation}: ret=0x{returnRip:X16} len=0x{length:X16} align=0x{alignment:X16} type=0x{memoryType:X8} out=0x{outAddress:X16} selected=0x{selectedAddress:X16} result={result?.ToString() ?? "<pending>"}");
    }

    // Cached once so the ~8 direct-memory call sites don't each do a
    // GetEnvironmentVariable P/Invoke per operation.
    private static readonly bool _traceDirectMemory = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_DIRECT_MEMORY"), "1", StringComparison.Ordinal);

    private static bool ShouldTraceDirectMemory() => _traceDirectMemory;

    private static bool TryReleaseDirectMemoryRangeLocked(ulong start, ulong length)
    {
        if (!TryAddU64(start, length, out var releaseEnd))
        {
            return false;
        }

        DirectAllocation? owner = null;
        ulong ownerEnd = 0;
        foreach (var allocation in _directAllocations.Values)
        {
            if (TryAddU64(allocation.Start, allocation.Length, out var allocationEnd) &&
                start >= allocation.Start &&
                releaseEnd <= allocationEnd)
            {
                owner = allocation;
                ownerEnd = allocationEnd;
                break;
            }
        }

        if (owner is not { } releasedFrom)
        {
            return false;
        }

        _directAllocations.Remove(releasedFrom.Start);
        if (start > releasedFrom.Start)
        {
            _directAllocations[releasedFrom.Start] = releasedFrom with
            {
                Length = start - releasedFrom.Start,
            };
        }

        if (releaseEnd < ownerEnd)
        {
            _directAllocations[releaseEnd] = new DirectAllocation(
                releaseEnd,
                ownerEnd - releaseEnd,
                releasedFrom.MemoryType);
        }

        _nextPhysicalAddress = GetDirectMemoryHighWaterMarkLocked();
        return true;
    }

    private static bool IsAligned(ulong value, ulong alignment) =>
        alignment != 0 && value % alignment == 0;

    private static bool TryAllocateDirectMemoryLocked(
        ulong searchStart,
        ulong searchEnd,
        ulong length,
        ulong alignment,
        int memoryType,
        ulong allocationLimit,
        out ulong selectedAddress)
    {
        selectedAddress = 0;
        if (length == 0 || searchStart >= searchEnd)
        {
            return false;
        }

        var effectiveAlignment = alignment == 0 ? OrbisPageSize : alignment;
        if (!TryFindAllocatableDirectMemoryRangeLocked(searchStart, searchEnd, length, effectiveAlignment, allocationLimit, out var freePosition) ||
            !TryAddU64(freePosition, length, out var endAddress))
        {
            return false;
        }

        _directAllocations[freePosition] = new DirectAllocation(freePosition, length, memoryType);
        _nextPhysicalAddress = endAddress;
        selectedAddress = freePosition;
        return true;
    }

    private static bool TryFindAllocatableDirectMemoryRangeLocked(
        ulong searchStart,
        ulong searchEnd,
        ulong length,
        ulong alignment,
        ulong allocationLimit,
        out ulong selectedAddress)
    {
        selectedAddress = 0;
        if (length == 0 || searchStart >= searchEnd)
        {
            return false;
        }

        var effectiveEnd = Math.Min(searchEnd, allocationLimit);
        var candidate = AlignUp(searchStart, alignment);
        if (candidate >= effectiveEnd)
        {
            return false;
        }

        var allocations = new List<DirectAllocation>(_directAllocations.Values);
        allocations.Sort(static (left, right) => left.Start.CompareTo(right.Start));

        foreach (var allocation in allocations)
        {
            if (!TryAddU64(allocation.Start, allocation.Length, out var allocationEnd))
            {
                return false;
            }

            if (allocationEnd <= candidate)
            {
                continue;
            }

            var gapEnd = Math.Min(allocation.Start, effectiveEnd);
            if (candidate < gapEnd &&
                TryAddU64(candidate, length, out var candidateEnd) &&
                candidateEnd <= gapEnd)
            {
                selectedAddress = candidate;
                return true;
            }

            if (allocation.Start >= effectiveEnd)
            {
                break;
            }

            candidate = AlignUp(Math.Max(candidate, allocationEnd), alignment);
            if (candidate >= effectiveEnd)
            {
                return false;
            }
        }

        if (!TryAddU64(candidate, length, out var endAddress) || endAddress > effectiveEnd)
        {
            return false;
        }

        selectedAddress = candidate;
        return true;
    }

    private static bool TryFindAvailableDirectMemorySpanLocked(
        ulong searchStart,
        ulong searchEnd,
        ulong alignment,
        out ulong spanStart,
        out ulong spanLength)
    {
        spanStart = 0;
        spanLength = 0;
        if (searchStart >= searchEnd)
        {
            return false;
        }

        var effectiveEnd = Math.Min(searchEnd, DirectMemorySizeBytes);
        var candidate = AlignUp(searchStart, alignment);
        if (candidate >= effectiveEnd)
        {
            return false;
        }

        var allocations = new List<DirectAllocation>(_directAllocations.Values);
        allocations.Sort(static (left, right) => left.Start.CompareTo(right.Start));

        foreach (var allocation in allocations)
        {
            if (!TryAddU64(allocation.Start, allocation.Length, out var allocationEnd))
            {
                return false;
            }

            if (allocationEnd <= candidate)
            {
                continue;
            }

            var gapEnd = Math.Min(allocation.Start, effectiveEnd);
            if (candidate < gapEnd)
            {
                var candidateLength = gapEnd - candidate;
                if (candidateLength > spanLength)
                {
                    spanStart = candidate;
                    spanLength = candidateLength;
                }
            }

            if (allocation.Start >= effectiveEnd)
            {
                break;
            }

            candidate = AlignUp(Math.Max(candidate, allocationEnd), alignment);
            if (candidate >= effectiveEnd)
            {
                break;
            }
        }

        if (candidate < effectiveEnd)
        {
            var candidateLength = effectiveEnd - candidate;
            if (candidateLength > spanLength)
            {
                spanStart = candidate;
                spanLength = candidateLength;
            }
        }

        return spanLength != 0;
    }

    private static ulong GetDirectMemoryHighWaterMarkLocked()
    {
        ulong highWaterMark = 0;
        foreach (var allocation in _directAllocations.Values)
        {
            if (!TryAddU64(allocation.Start, allocation.Length, out var endAddress))
            {
                return ulong.MaxValue;
            }

            if (endAddress > highWaterMark)
            {
                highWaterMark = endAddress;
            }
        }

        return highWaterMark;
    }

    private static unsafe bool TryReadHostMemory(ulong address, Span<byte> destination)
    {
        if (destination.IsEmpty || !IsHostRangeAccessible(address, (ulong)destination.Length, writeAccess: false))
        {
            return false;
        }

        try
        {
            new ReadOnlySpan<byte>((void*)address, destination.Length).CopyTo(destination);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryReadTrackedLibcHeap(
        ulong address,
        Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            return true;
        }

        var length = (ulong)destination.Length;
        lock (_libcAllocGate)
        {
            foreach (var (allocationAddress, allocation) in _libcAllocations)
            {
                var allocationSize = (ulong)allocation.Size;
                var offset = address >= allocationAddress
                    ? address - allocationAddress
                    : ulong.MaxValue;
                if (offset > allocationSize ||
                    length > allocationSize - offset)
                {
                    continue;
                }

                return TryReadHostMemory(address, destination);
            }
        }

        return false;
    }

    internal static bool TryReadShaderGuestMemory(
        ulong address,
        Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            return true;
        }

        if (TryReadTrackedLibcHeap(address, destination))
        {
            return true;
        }

        var length = (ulong)destination.Length;
        lock (_memoryGate)
        {
            if (TryFindVirtualQueryRegionLocked(
                    address,
                    findNext: false,
                    out var region) &&
                length <= region.Length &&
                address >= region.Address &&
                length <= region.Address + region.Length - address)
            {
                return TryReadHostMemory(address, destination);
            }
        }

        // Direct execution uses guest virtual addresses as host virtual addresses.
        // Some native mmap paths predate _mappedRegions tracking, so retain the same
        // committed/readable-page fallback used by the libc compatibility layer.
        return TryReadHostMemory(address, destination);
    }

    internal static bool TryReadTrackedLibcHeapGpuAlias(
        ulong packedAddress,
        Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            return true;
        }

        // Gen5 texture descriptors retain 46 bits of the byte address.  Host
        // libc allocations can live at 0x7F... on Linux, so recover the full
        // tracked allocation address when the descriptor contains its packed
        // low-bit alias.
        const ulong textureAddressMask = (1UL << 46) - 1;
        var length = (ulong)destination.Length;
        ulong resolvedAddress = 0;
        lock (_libcAllocGate)
        {
            foreach (var (allocationAddress, allocation) in _libcAllocations)
            {
                var packedBase = allocationAddress & textureAddressMask;
                if (packedAddress < packedBase)
                {
                    continue;
                }

                var offset = packedAddress - packedBase;
                var allocationSize = (ulong)allocation.Size;
                if (offset > allocationSize || length > allocationSize - offset)
                {
                    continue;
                }

                var candidate = allocationAddress + offset;
                if (resolvedAddress != 0 && resolvedAddress != candidate)
                {
                    // Do not guess if two live host allocations collide after
                    // descriptor address packing.
                    return false;
                }

                resolvedAddress = candidate;
            }

            return resolvedAddress != 0 &&
                   TryReadHostMemory(resolvedAddress, destination);
        }
    }

    private static bool TryAllocateLibcHeap(ulong requestedSize, nuint alignment, bool zeroFill, out ulong address)
    {
        address = 0;
        return TryConvertAllocationSize(requestedSize, out var size) &&
               TryAllocateLibcHeapCore(size, alignment, zeroFill, out address);
    }

    private static unsafe bool TryAllocateLibcHeapCore(nuint requestedSize, nuint alignment, bool zeroFill, out ulong address)
    {
        address = 0;
        alignment = NormalizeLibcAlignment(alignment);
        var actualSize = requestedSize == 0 ? 1u : requestedSize;

        nuint totalSize;
        try
        {
            checked
            {
                totalSize = actualSize + alignment - 1 + (nuint)IntPtr.Size;
            }
        }
        catch (OverflowException)
        {
            return false;
        }

        nint baseAddress;
        try
        {
            baseAddress = Marshal.AllocHGlobal(checked((nint)totalSize));
        }
        catch (OutOfMemoryException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }

        if (baseAddress == 0)
        {
            return false;
        }

        var alignedAddress = AlignUp(unchecked((ulong)baseAddress) + (ulong)IntPtr.Size, (ulong)alignment);
        lock (_libcAllocGate)
        {
            _libcAllocations[alignedAddress] = new LibcHeapAllocation(baseAddress, actualSize, alignment);
        }

        try
        {
            if (zeroFill)
            {
                NativeMemory.Clear((void*)alignedAddress, actualSize);
            }
        }
        catch
        {
            FreeLibcHeap(alignedAddress);
            return false;
        }

        address = alignedAddress;
        return true;
    }

    private static unsafe bool TryReallocateLibcHeap(ulong existingAddress, ulong requestedSize, out ulong resizedAddress)
    {
        resizedAddress = 0;
        if (existingAddress == 0)
        {
            return TryAllocateLibcHeap(requestedSize, DefaultLibcHeapAlignment, zeroFill: false, out resizedAddress);
        }

        if (requestedSize == 0)
        {
            FreeLibcHeap(existingAddress);
            return true;
        }

        LibcHeapAllocation allocation;
        lock (_libcAllocGate)
        {
            if (!_libcAllocations.TryGetValue(existingAddress, out allocation))
            {
                return false;
            }
        }

        if (!TryAllocateLibcHeap(requestedSize, allocation.Alignment, zeroFill: false, out resizedAddress))
        {
            return false;
        }

        var bytesToCopy = Math.Min(allocation.Size, (nuint)requestedSize);
        Buffer.MemoryCopy(
            source: (void*)existingAddress,
            destination: (void*)resizedAddress,
            destinationSizeInBytes: checked((long)Math.Max(bytesToCopy, 1u)),
            sourceBytesToCopy: checked((long)bytesToCopy));
        FreeLibcHeap(existingAddress);
        return true;
    }

    private static bool TryAllocateAlignedLibcHeap(ulong alignmentValue, ulong requestedSize, bool requireSizeMultiple, out ulong address)
    {
        address = 0;
        return TryValidateAlignedAllocation(
                   alignmentValue,
                   requestedSize,
                   requireSizeMultiple,
                   requirePointerSizedAlignment: false,
                   out var alignment,
                   out var size) &&
               TryAllocateLibcHeapCore(size, alignment, zeroFill: false, out address);
    }

    private static bool TryValidateAlignedAllocation(
        ulong alignmentValue,
        ulong requestedSize,
        bool requireSizeMultiple,
        bool requirePointerSizedAlignment,
        out nuint alignment,
        out nuint size)
    {
        alignment = 0;
        size = 0;
        if (!TryConvertAllocationSize(requestedSize, out size) ||
            alignmentValue == 0 ||
            alignmentValue > (ulong)nint.MaxValue)
        {
            return false;
        }

        alignment = (nuint)alignmentValue;
        if (!IsPowerOfTwo(alignment))
        {
            return false;
        }

        if (requirePointerSizedAlignment && alignment % (nuint)IntPtr.Size != 0)
        {
            return false;
        }

        if (alignment < (nuint)IntPtr.Size)
        {
            alignment = (nuint)IntPtr.Size;
        }

        if (requireSizeMultiple && size % alignment != 0)
        {
            return false;
        }

        return true;
    }

    private static void FreeLibcHeap(ulong address)
    {
        if (address == 0)
        {
            return;
        }

        LibcHeapAllocation allocation;
        lock (_libcAllocGate)
        {
            if (!_libcAllocations.Remove(address, out allocation))
            {
                return;
            }
        }

        Marshal.FreeHGlobal(allocation.BaseAddress);
    }

    private static bool TryMultiplyAllocationSize(ulong left, ulong right, out nuint size)
    {
        size = 0;
        if (!TryConvertAllocationSize(left, out var leftSize) ||
            !TryConvertAllocationSize(right, out var rightSize))
        {
            return false;
        }

        try
        {
            checked
            {
                size = leftSize * rightSize;
            }
        }
        catch (OverflowException)
        {
            return false;
        }

        return true;
    }

    private static bool TryConvertAllocationSize(ulong requestedSize, out nuint size)
    {
        size = 0;
        if (requestedSize > (ulong)nint.MaxValue)
        {
            return false;
        }

        size = (nuint)requestedSize;
        return true;
    }

    private static nuint NormalizeLibcAlignment(nuint alignment)
    {
        if (alignment < DefaultLibcHeapAlignment)
        {
            return DefaultLibcHeapAlignment;
        }

        return alignment;
    }

    private static bool IsPowerOfTwo(nuint value)
    {
        return value != 0 && (value & (value - 1)) == 0;
    }

    private static unsafe bool TryWriteHostMemory(ulong address, ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty || !IsHostRangeAccessible(address, (ulong)source.Length, writeAccess: true))
        {
            return false;
        }

        try
        {
            source.CopyTo(new Span<byte>((void*)address, source.Length));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsHostRangeAccessible(ulong address, ulong length, bool writeAccess)
    {
        if (address == 0 || length == 0)
        {
            return false;
        }

        const ulong canonicalUpper = 0x0000800000000000UL;
        if (address >= canonicalUpper)
        {
            return false;
        }

        if (ulong.MaxValue - address < length - 1)
        {
            return false;
        }

        var endAddress = address + length - 1;
        var currentAddress = address;
        while (currentAddress <= endAddress)
        {
            if (!TryQueryHostPage(currentAddress, out var info) ||
                !HasRequiredProtection(info.Protect, writeAccess))
            {
                return false;
            }

            var regionBase = unchecked((ulong)info.BaseAddress);
            var regionSize = (ulong)info.RegionSize;
            if (regionSize == 0 ||
                regionBase > currentAddress ||
                ulong.MaxValue - regionBase < regionSize)
            {
                return false;
            }

            var regionEnd = regionBase + regionSize;
            if (regionEnd <= currentAddress)
            {
                return false;
            }

            if (regionEnd > endAddress)
            {
                return true;
            }

            currentAddress = regionEnd;
        }

        return true;
    }

    private static bool TryQueryHostPage(ulong address, out MemoryBasicInformation info)
    {
        info = default;
        var size = (nuint)Marshal.SizeOf<MemoryBasicInformation>();
        if (VirtualQuery((nint)address, out info, size) == 0)
        {
            return false;
        }

        return info.State == MemCommit;
    }

    private static bool HasRequiredProtection(uint protect, bool writeAccess)
    {
        if ((protect & (HostPageNoAccess | HostPageGuard)) != 0)
        {
            return false;
        }

        const uint readableMask = HostPageReadOnly | HostPageReadWrite | HostPageWriteCopy | HostPageExecuteRead | HostPageExecuteReadWrite | HostPageExecuteWriteCopy;
        const uint writableMask = HostPageReadWrite | HostPageWriteCopy | HostPageExecuteReadWrite | HostPageExecuteWriteCopy;
        var expected = writeAccess ? writableMask : readableMask;
        return (protect & expected) != 0;
    }

    private static bool TryWriteInt32(CpuContext ctx, ulong address, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BitConverter.TryWriteBytes(bytes, value);
        return ctx.Memory.TryWrite(address, bytes);
    }

    private static bool TryWriteOpenDescriptorStat(CpuContext ctx, int fd, ulong statAddress)
    {
        if (fd is 0 or 1 or 2)
        {
            var now = DateTime.UtcNow;
            LogIoTrace("fstat", $"stdio:{fd}", $"fd={fd} size=0 dir=0");
            return TryWriteKernelStat(ctx, statAddress, isDirectory: false, size: 0, now, now, now, $"stdio:{fd}");
        }

        string? hostPath = null;
        bool isDirectory = false;
        lock (_fdGate)
        {
            if (_openDirectories.TryGetValue(fd, out var directory))
            {
                hostPath = directory.Path;
                isDirectory = true;
            }
            else if (_openFiles.TryGetValue(fd, out var stream))
            {
                hostPath = stream.Name;
            }
        }

        if (!string.IsNullOrWhiteSpace(hostPath))
        {
            long size = 0;
            if (!isDirectory)
            {
                try
                {
                    size = new FileInfo(hostPath!).Length;
                }
                catch (IOException)
                {
                    size = -1;
                }
            }

            LogIoTrace("fstat", hostPath!, $"fd={fd} size={size} dir={(isDirectory ? 1 : 0)}");
        }

        return !string.IsNullOrWhiteSpace(hostPath) && TryWriteHostPathStat(ctx, statAddress, hostPath!, isDirectory);
    }

    private static bool TryWriteHostPathStat(CpuContext ctx, ulong statAddress, string hostPath)
    {
        var isDirectory = Directory.Exists(hostPath);
        if (!isDirectory && !File.Exists(hostPath))
        {
            return false;
        }

        return TryWriteHostPathStat(ctx, statAddress, hostPath, isDirectory);
    }

    private static bool TryGetAprFileSize(string hostPath, out ulong size)
    {
        size = 0;

        string cachePath;
        try
        {
            cachePath = Path.GetFullPath(hostPath);
        }
        catch
        {
            cachePath = hostPath;
        }

        if (_aprFileSizeCache.TryGetValue(cachePath, out size))
        {
            return true;
        }

        try
        {
            var fileInfo = new FileInfo(cachePath);
            if (fileInfo.Exists)
            {
                var length = fileInfo.Length;
                size = length < 0 ? 0UL : unchecked((ulong)length);
                _aprFileSizeCache.TryAdd(cachePath, size);
                return true;
            }

            if (!new DirectoryInfo(cachePath).Exists)
            {
                return false;
            }

            size = 65536;
            _aprFileSizeCache.TryAdd(cachePath, size);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryWriteHostPathStat(CpuContext ctx, ulong statAddress, string hostPath, bool isDirectory)
    {
        if (isDirectory)
        {
            if (!Directory.Exists(hostPath))
            {
                return false;
            }
        }
        else if (!File.Exists(hostPath))
        {
            return false;
        }

        try
        {
            var lastAccessUtc = File.GetLastAccessTimeUtc(hostPath);
            var lastWriteUtc = File.GetLastWriteTimeUtc(hostPath);
            var creationUtc = File.GetCreationTimeUtc(hostPath);
            var size = isDirectory ? 65536L : new FileInfo(hostPath).Length;
            return TryWriteKernelStat(ctx, statAddress, isDirectory, size, lastAccessUtc, lastWriteUtc, creationUtc, hostPath);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryWriteKernelStat(
        CpuContext ctx,
        ulong statAddress,
        bool isDirectory,
        long size,
        DateTime lastAccessUtc,
        DateTime lastWriteUtc,
        DateTime creationUtc,
        string inodeSeed)
    {
        Span<byte> payload = stackalloc byte[KernelStatSize];
        payload.Clear();

        var seedBytes = Encoding.UTF8.GetBytes(inodeSeed);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[KernelStatStDevOffset..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[KernelStatStInoOffset..], ComputeDirectoryEntryHash(seedBytes));
        BinaryPrimitives.WriteUInt16LittleEndian(payload[KernelStatStModeOffset..], isDirectory ? KernelStatModeDirectory : KernelStatModeRegular);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[KernelStatStNlinkOffset..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[KernelStatStUidOffset..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[KernelStatStGidOffset..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[KernelStatStRdevOffset..], 0);
        WriteKernelTimespec(payload[KernelStatStAtimOffset..], lastAccessUtc);
        WriteKernelTimespec(payload[KernelStatStMtimOffset..], lastWriteUtc);
        WriteKernelTimespec(payload[KernelStatStCtimOffset..], lastWriteUtc);
        BinaryPrimitives.WriteInt64LittleEndian(payload[KernelStatStSizeOffset..], size);
        BinaryPrimitives.WriteInt64LittleEndian(payload[KernelStatStBlocksOffset..], isDirectory ? 128 : (size + 511) / 512);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[KernelStatStBlksizeOffset..], isDirectory ? 65536U : 512U);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[KernelStatStFlagsOffset..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[KernelStatStGenOffset..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(payload[KernelStatStLspareOffset..], 0);
        WriteKernelTimespec(payload[KernelStatStBirthtimOffset..], creationUtc);
        return TryWriteCompat(ctx, statAddress, payload);
    }

    private static void WriteKernelTimespec(Span<byte> destination, DateTime utcTime)
    {
        var timestamp = utcTime.Kind == DateTimeKind.Utc ? utcTime : utcTime.ToUniversalTime();
        var dto = new DateTimeOffset(timestamp);
        BinaryPrimitives.WriteInt64LittleEndian(destination, dto.ToUnixTimeSeconds());
        var ticksWithinSecond = timestamp.Ticks % TimeSpan.TicksPerSecond;
        BinaryPrimitives.WriteInt64LittleEndian(destination[sizeof(long)..], ticksWithinSecond * 100);
    }

    private static int KernelGetdirentriesCore(CpuContext ctx, int fd, ulong bufferAddress, int requested, ulong basePointerAddress)
    {
        if (fd < 0 || bufferAddress == 0 || requested < 512)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        OpenDirectory? directory;
        lock (_fdGate)
        {
            _openDirectories.TryGetValue(fd, out directory);
        }

        if (directory is null)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        var currentIndex = directory.NextIndex;
        if (basePointerAddress != 0 && !TryWriteUInt64Compat(ctx, basePointerAddress, (ulong)currentIndex))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (currentIndex >= directory.Entries.Length)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var entryName = directory.Entries[currentIndex];
        directory.NextIndex = currentIndex + 1;

        var entryBytes = Encoding.UTF8.GetBytes(entryName);
        var nameLength = Math.Min(entryBytes.Length, 255);
        var entryPath = Path.Combine(directory.Path, entryName);
        var entryType = Directory.Exists(entryPath) ? (byte)4 : (byte)8;

        var payload = new byte[512];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, sizeof(uint)), ComputeDirectoryEntryHash(entryBytes.AsSpan(0, nameLength)));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, sizeof(ushort)), 512);
        payload[6] = entryType;
        payload[7] = unchecked((byte)nameLength);
        entryBytes.AsSpan(0, nameLength).CopyTo(payload.AsSpan(8));

        if (!TryWriteCompat(ctx, bufferAddress, payload))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 512;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static string[] EnumerateDirectoryEntries(string hostPath)
    {
        return Directory.EnumerateFileSystemEntries(hostPath)
            .Select(Path.GetFileName)
            .Where(static name => !string.IsNullOrEmpty(name))
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray()!;
    }

    private static uint ComputeDirectoryEntryHash(ReadOnlySpan<byte> utf8Name)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        for (var i = 0; i < utf8Name.Length; i++)
        {
            hash ^= utf8Name[i];
            hash *= prime;
        }

        return hash;
    }

    private static void LogOpenTrace(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_OPEN"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] {message}");
    }

    private static void LogIoTrace(string operation, string path, string detail)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_IO"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var filter = Environment.GetEnvironmentVariable("SHARPEMU_LOG_IO_FILTER");
        if (!string.IsNullOrWhiteSpace(filter) &&
            path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] {operation} path='{path}' {detail}");
    }

    private static void LogUniqueStatTrace(string guestPath, string hostPath, bool found)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_IO"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var result = found ? "found" : "not_found";
        lock (_ioTraceGate)
        {
            if (!_tracedStatResults.Add($"{result}\0{guestPath}"))
            {
                return;
            }
        }

        LogIoTrace("stat", guestPath, $"host='{hostPath}' result={result}");
    }

    private static string? GetNegativeStatCacheKey(string guestPath)
    {
        var normalized = NormalizeGuestStatCachePath(guestPath);
        return IsReadOnlyGuestStatPath(normalized) ? normalized : null;
    }

    private static string? NormalizeGuestStatCachePath(string guestPath)
    {
        var normalized = guestPath.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0)
        {
            return null;
        }

        if (normalized[0] != '/')
        {
            normalized = "/" + normalized;
        }

        return normalized;
    }

    private static bool IsReadOnlyGuestStatPath(string? normalizedGuestPath) =>
        normalizedGuestPath is not null &&
        (string.Equals(normalizedGuestPath, "/app0", StringComparison.OrdinalIgnoreCase) ||
         normalizedGuestPath.StartsWith("/app0/", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(normalizedGuestPath, "/hostapp", StringComparison.OrdinalIgnoreCase) ||
         normalizedGuestPath.StartsWith("/hostapp/", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(normalizedGuestPath, "/devlog/app", StringComparison.OrdinalIgnoreCase) ||
         normalizedGuestPath.StartsWith("/devlog/app/", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(normalizedGuestPath, "/temp0", StringComparison.OrdinalIgnoreCase) ||
         normalizedGuestPath.StartsWith("/temp0/", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(normalizedGuestPath, "/download0", StringComparison.OrdinalIgnoreCase) ||
         normalizedGuestPath.StartsWith("/download0/", StringComparison.OrdinalIgnoreCase));

    private static bool IsNegativeStatCached(string cacheKey)
    {
        lock (_statCacheGate)
        {
            return _negativeStatCache.Contains(cacheKey);
        }
    }

    private static void AddNegativeStatCache(string cacheKey)
    {
        lock (_statCacheGate)
        {
            _negativeStatCache.Add(cacheKey);
        }
    }

    private static void RemoveNegativeStatCache(string cacheKey)
    {
        lock (_statCacheGate)
        {
            _negativeStatCache.Remove(cacheKey);
        }
    }

    private static void AddNegativeStatCacheForGuestPath(string guestPath)
    {
        var cacheKey = GetNegativeStatCacheKey(guestPath);
        if (cacheKey is not null)
        {
            AddNegativeStatCache(cacheKey);
        }
    }

    private static void InvalidateNegativeStatCacheForPathAndAncestors(string guestPath)
    {
        var normalized = NormalizeGuestStatCachePath(guestPath);
        if (normalized is null)
        {
            return;
        }

        lock (_statCacheGate)
        {
            var current = normalized;
            while (true)
            {
                _negativeStatCache.Remove(current);
                var slash = current.LastIndexOf('/');
                if (slash <= 0)
                {
                    break;
                }

                current = current[..slash];
            }

            _negativeStatCache.Remove("/");
        }
    }

    private static void InvalidateAprFileSizeCache(string hostPath)
    {
        try
        {
            hostPath = Path.GetFullPath(hostPath);
        }
        catch
        {
            // The cache key remains the original path when normalization fails.
        }

        _aprFileSizeCache.TryRemove(hostPath, out _);
    }

    private static string PreviewIoBytes(byte[] buffer, int count, int maxBytes)
    {
        if (count <= 0)
        {
            return string.Empty;
        }

        var previewLength = Math.Min(count, maxBytes);
        var text = Encoding.UTF8.GetString(buffer, 0, previewLength);
        return SanitizeTracePreview(text, maxBytes);
    }

    private static string PreviewIoHex(byte[] buffer, int count, int maxBytes)
    {
        if (count <= 0)
        {
            return string.Empty;
        }

        var previewLength = Math.Min(count, maxBytes);
        return Convert.ToHexString(buffer, 0, previewLength);
    }

    private static string PreviewGuestHex(CpuContext ctx, ulong address, int maxBytes)
    {
        if (address == 0 || maxBytes <= 0)
        {
            return string.Empty;
        }

        var bytes = GC.AllocateUninitializedArray<byte>(maxBytes);
        if (!ctx.Memory.TryRead(address, bytes))
        {
            return "<unreadable>";
        }

        return Convert.ToHexString(bytes);
    }

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        if (alignment <= 1)
        {
            return value;
        }

        var mask = alignment - 1;
        return (value + mask) & ~mask;
    }

    private static ulong AlignDown(ulong value, ulong alignment)
    {
        if (alignment <= 1)
        {
            return value;
        }

        var mask = alignment - 1;
        return value & ~mask;
    }

    private static bool TryAddU64(ulong left, ulong right, out ulong sum)
    {
        sum = left + right;
        return sum >= left;
    }

    [SysAbiExport(
        Nid = "AV6ipCNa4Rw",
        ExportName = "strcasecmp",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Strcasecmp(CpuContext ctx)
    {
        var left = ctx[CpuRegister.Rdi];
        var right = ctx[CpuRegister.Rsi];
        if (left == 0 || right == 0)
        {
            var recoveryIndex = Interlocked.Increment(ref _nullStrcasecmpRecoveryCount);
            if (recoveryIndex <= 16)
            {
                var otherAddress = left == 0 ? right : left;
                var otherText = otherAddress != 0 && TryReadNullTerminatedUtf8(ctx, otherAddress, 256, out var text)
                    ? text
                    : "<unreadable>";
                _ = ctx.TryReadUInt64(ctx[CpuRegister.Rsp], out var returnRip);
                Console.Error.WriteLine(
                    $"[LOADER][WARNING] strcasecmp null-arg recovery#{recoveryIndex}: ret=0x{returnRip:X16} left=0x{left:X16} right=0x{right:X16} other=\"{otherText}\"");
            }

            // Real strcasecmp(NULL, x) is undefined behaviour and previously crashed inside the
            // LLE-routed implementation. Treat it as "not equal" instead so callers doing
            // `if (strcasecmp(a, b) == 0)` degrade gracefully rather than taking down the guest.
            ctx[CpuRegister.Rax] = left == right ? 0uL : 1uL;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (!TryCompareStringsCaseInsensitive(ctx, left, right, limit: ulong.MaxValue, out var compare))
        {
            ctx[CpuRegister.Rax] = 1;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)compare);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // sprintf/vsprintf are served from the same HLE formatting engine as snprintf/vsnprintf
    // instead of falling through to the game's bundled libc.
    [SysAbiExport(
        Nid = "tcVi5SivF7Q",
        ExportName = "sprintf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Sprintf(CpuContext ctx)
    {
        var destination = ctx[CpuRegister.Rdi];
        var formatAddress = ctx[CpuRegister.Rsi];

        if (!TryReadCString(ctx, formatAddress, 1_048_576, out var formatBytes))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var format = Encoding.UTF8.GetString(formatBytes);
        var rendered = FormatStringFromVarArgs(ctx, format, firstGpArgIndex: 2);
        return WriteSnprintfOutput(ctx, destination, ulong.MaxValue, rendered);
    }

    [SysAbiExport(
        Nid = "jbz9I9vkqkk",
        ExportName = "vsprintf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Vsprintf(CpuContext ctx)
    {
        var destination = ctx[CpuRegister.Rdi];
        var formatAddress = ctx[CpuRegister.Rsi];
        var vaListAddress = ctx[CpuRegister.Rdx];

        if (!TryReadCString(ctx, formatAddress, 1_048_576, out var formatBytes))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var format = Encoding.UTF8.GetString(formatBytes);
        if (!TryCreateVaListCursor(ctx, vaListAddress, out var vaCursor))
        {
            return WriteSnprintfOutput(ctx, destination, ulong.MaxValue, formatBytes);
        }

        var rendered = FormatString(ctx, format, ref vaCursor);
        return WriteSnprintfOutput(ctx, destination, ulong.MaxValue, rendered);
    }

    // Was unresolved (returning the 0x80020002 sentinel, then crashing when the guest
    // dereferenced it) - the game's own heap instrumentation calls this hook when it
    // detects a corrupted/invalid block, not the emulator's allocator, so this is purely
    // a diagnostic sink: log what was reported and return success so the caller continues.
    [SysAbiExport(
        Nid = "al3JzFI9MQ0",
        ExportName = "sceLibcInternalHeapErrorReportForGame",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceLibcInternal")]
    public static int LibcInternalHeapErrorReportForGame(CpuContext ctx)
    {
        Console.Error.WriteLine(
            $"[LOADER][WARN] sceLibcInternalHeapErrorReportForGame: rdi=0x{ctx[CpuRegister.Rdi]:X16} " +
            $"rsi=0x{ctx[CpuRegister.Rsi]:X16} rdx=0x{ctx[CpuRegister.Rdx]:X16} rcx=0x{ctx[CpuRegister.Rcx]:X16}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "ob5xAW4ln-0",
        ExportName = "strchr",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Strchr(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var needle = unchecked((byte)ctx[CpuRegister.Rsi]);
        if (address == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        // The terminator counts as part of the scanned range, so strchr(s, '\0')
        // returns a pointer to the string's null byte just like a native libc.
        Span<byte> current = stackalloc byte[1];
        for (ulong index = 0; index < 1_048_576; index++)
        {
            if (!TryReadCompat(ctx, address + index, current))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            if (current[0] == needle)
            {
                ctx[CpuRegister.Rax] = address + index;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            if (current[0] == 0)
            {
                break;
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "9yDWMxEFdJU",
        ExportName = "strrchr",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Strrchr(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var needle = unchecked((byte)ctx[CpuRegister.Rsi]);
        if (address == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ulong match = 0;
        var found = false;
        Span<byte> current = stackalloc byte[1];
        for (ulong index = 0; index < 1_048_576; index++)
        {
            if (!TryReadCompat(ctx, address + index, current))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            if (current[0] == needle)
            {
                match = address + index;
                found = true;
            }

            if (current[0] == 0)
            {
                break;
            }
        }

        ctx[CpuRegister.Rax] = found ? match : 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "8u8lPzUEq+U",
        ExportName = "memchr",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Memchr(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var needle = unchecked((byte)ctx[CpuRegister.Rsi]);
        var count = ctx[CpuRegister.Rdx];

        Span<byte> current = stackalloc byte[1];
        for (ulong index = 0; index < count; index++)
        {
            if (!TryReadCompat(ctx, address + index, current))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            if (current[0] == needle)
            {
                ctx[CpuRegister.Rax] = address + index;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Ls4tzzhimqQ",
        ExportName = "strcat",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Strcat(CpuContext ctx)
    {
        var destination = ctx[CpuRegister.Rdi];
        var source = ctx[CpuRegister.Rsi];
        if (!TryReadCString(ctx, source, 1_048_576, out var sourceBytes))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (!TryReadCString(ctx, destination, 1_048_576, out var destinationBytes))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        // Overwrite the destination terminator and re-terminate after the copied bytes.
        var appendAddress = destination + (ulong)destinationBytes.Length;
        var payload = new byte[sourceBytes.Length + 1];
        sourceBytes.CopyTo(payload.AsSpan());
        if (!TryWriteCompat(ctx, appendAddress, payload))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = destination;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "kHg45qPC6f0",
        ExportName = "strncat",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Strncat(CpuContext ctx)
    {
        var destination = ctx[CpuRegister.Rdi];
        var source = ctx[CpuRegister.Rsi];
        var limit = ctx[CpuRegister.Rdx];

        // Bounding the source read by the count yields strncat's "at most n bytes"
        // semantics while still stopping early at the source terminator.
        if (!TryReadCString(ctx, source, limit, out var sourceBytes))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (!TryReadCString(ctx, destination, 1_048_576, out var destinationBytes))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var appendAddress = destination + (ulong)destinationBytes.Length;
        var payload = new byte[sourceBytes.Length + 1];
        sourceBytes.CopyTo(payload.AsSpan());
        if (!TryWriteCompat(ctx, appendAddress, payload))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = destination;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "viiwFMaNamA",
        ExportName = "strstr",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Strstr(CpuContext ctx)
    {
        var haystack = ctx[CpuRegister.Rdi];
        var needle = ctx[CpuRegister.Rsi];
        if (!TryReadCString(ctx, haystack, 1_048_576, out var haystackBytes))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (!TryReadCString(ctx, needle, 1_048_576, out var needleBytes))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        // An empty needle matches at the start of the haystack.
        if (needleBytes.Length == 0)
        {
            ctx[CpuRegister.Rax] = haystack;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var matchIndex = haystackBytes.AsSpan().IndexOf(needleBytes.AsSpan());
        ctx[CpuRegister.Rax] = matchIndex >= 0 ? haystack + (ulong)matchIndex : 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "fgIsQ10xYVA",
        ExportName = "sceKernelChmod",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelChmod(CpuContext ctx)
    {
        var pathAddress = ctx[CpuRegister.Rdi];
        var mode = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (pathAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryReadNullTerminatedUtf8(ctx, pathAddress, MaxGuestStringLength, out var guestPath))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var hostPath = ResolveGuestPath(guestPath);
        if (IsReadOnlyGuestMutationPath(guestPath))
        {
            LogOpenTrace($"chmod readonly path='{guestPath}' host='{hostPath}' mode=0x{mode:X}");
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
        }

        if (!File.Exists(hostPath) && !Directory.Exists(hostPath))
        {
            AddNegativeStatCacheForGuestPath(guestPath);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        // POSIX permission bits have no host equivalent on Windows; accept the call
        // so guests that chmod their freshly created files/directories can proceed.
        LogOpenTrace($"chmod path='{guestPath}' host='{hostPath}' mode=0x{mode:X}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
    private static bool TryCompareStringsCaseInsensitive(
        CpuContext ctx,
        ulong left,
        ulong right,
        ulong limit,
        out int compare)
    {
        compare = 0;
        Span<byte> leftBuffer = stackalloc byte[1];
        Span<byte> rightBuffer = stackalloc byte[1];
        for (ulong index = 0; index < limit; index++)
        {
            if (!TryReadCompat(ctx, left + index, leftBuffer) ||
                !TryReadCompat(ctx, right + index, rightBuffer))
            {
                return false;
            }

            var leftValue = leftBuffer[0];
            var rightValue = rightBuffer[0];
            var leftLower = ToAsciiLower(leftValue);
            var rightLower = ToAsciiLower(rightValue);
            if (leftLower != rightLower)
            {
                compare = leftLower - rightLower;
                return true;
            }

            if (leftValue == 0)
            {
                return true;
            }
        }

        return true;
    }

    private static byte ToAsciiLower(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;
}
