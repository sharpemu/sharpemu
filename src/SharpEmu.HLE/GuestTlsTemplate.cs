// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Diagnostics;

namespace SharpEmu.HLE;

/// <summary>
/// Process-wide registry of ELF PT_TLS templates and per-thread dynamic thread
/// vectors. AMD64 uses TLS Variant II: startup/static module blocks precede the
/// thread pointer, while modules introduced after a thread was initialized are
/// represented by separately allocated DTV entries.
/// </summary>
public static class GuestTlsTemplate
{
    // Must match CpuDispatcher/DirectExecutionBackend's mapped prefix. PS5
    // modules can require more than one host page of Variant II static TLS;
    // Dreaming Sarah's startup image, for example, reaches 0x1870 bytes.
    public const ulong StartupStaticTlsReservation = 0x10000UL;
    private static readonly object _gate = new();
    private static readonly SortedDictionary<ulong, ModuleTemplate> _modules = new();
    private static readonly Dictionary<ulong, ThreadDtv> _threadDtvs = new();
    private static ulong _staticTlsSize;
    private static ulong _maximumAlignment = 1;
    private static ulong _generation;

    static GuestTlsTemplate()
    {
        RunTlsLayoutSelfChecks();
    }

    private sealed class ModuleTemplate
    {
        public required ulong ModuleId { get; init; }
        public required byte[] InitImage { get; init; }
        public required ulong MemorySize { get; init; }
        public required ulong Alignment { get; init; }
        public required ulong AlignmentBias { get; init; }
        public required ulong StaticOffset { get; init; }
    }

    private sealed class ThreadDtv
    {
        public ulong Generation { get; set; }
        public Dictionary<ulong, DtvEntry> Entries { get; } = new();
        public nint DtvAllocationBase { get; set; }
        public ulong DtvAddress { get; set; }
    }

    private sealed class DtvEntry
    {
        public required ulong Address { get; init; }
        public nint AllocationBase { get; init; }
    }

    /// <summary>Main executable's initialized PT_TLS bytes.</summary>
    public static byte[] InitImage
    {
        get
        {
            lock (_gate)
            {
                return _modules.TryGetValue(1, out var module)
                    ? (byte[])module.InitImage.Clone()
                    : [];
            }
        }
    }

    /// <summary>Aligned size of the main executable's static TLS block.</summary>
    public static ulong BlockSize
    {
        get
        {
            lock (_gate)
            {
                return _modules.TryGetValue(1, out var module)
                    ? module.StaticOffset
                    : 0;
            }
        }
    }

    /// <summary>Main executable's PT_TLS alignment.</summary>
    public static ulong Alignment
    {
        get
        {
            lock (_gate)
            {
                return _modules.TryGetValue(1, out var module)
                    ? module.Alignment
                    : 1;
            }
        }
    }

    /// <summary>Total Variant II static TLS span required below the TCB.</summary>
    public static ulong StaticTlsSize
    {
        get { lock (_gate) { return _staticTlsSize; } }
    }

    /// <summary>Largest PT_TLS alignment required by the registered modules.</summary>
    public static ulong MaximumAlignment
    {
        get { lock (_gate) { return _maximumAlignment; } }
    }

    /// <summary>Current DTV generation, incremented for every registered module.</summary>
    public static ulong Generation
    {
        get { lock (_gate) { return _generation; } }
    }

    /// <summary>
    /// Backwards-compatible main-module registration entry point.
    /// </summary>
    public static void Set(ReadOnlySpan<byte> initImage, ulong memorySize, ulong alignment)
    {
        Reset();
        RegisterModule(1, initImage, memorySize, alignment, alignmentBias: 0);
    }

    /// <summary>
    /// Registers one module's PT_TLS template and allocates its Variant II
    /// static offset. Modules must be registered in loader module-ID order.
    /// </summary>
    public static ulong RegisterModule(
        ulong moduleId,
        ReadOnlySpan<byte> initImage,
        ulong memorySize,
        ulong alignment,
        ulong alignmentBias = 0)
    {
        if (moduleId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(moduleId));
        }

        var normalizedAlignment = alignment == 0 ? 1 : alignment;
        if ((normalizedAlignment & (normalizedAlignment - 1)) != 0)
        {
            throw new InvalidDataException($"PT_TLS alignment 0x{alignment:X} is not a power of two.");
        }
        if (memorySize > int.MaxValue || (ulong)initImage.Length > memorySize)
        {
            throw new InvalidDataException("PT_TLS template size is invalid or exceeds the supported process limit.");
        }

        lock (_gate)
        {
            if (_modules.TryGetValue(moduleId, out var existing))
            {
                if (existing.MemorySize != memorySize ||
                    existing.Alignment != normalizedAlignment ||
                    existing.AlignmentBias != alignmentBias ||
                    !existing.InitImage.AsSpan().SequenceEqual(initImage))
                {
                    throw new InvalidOperationException($"TLS module {moduleId} was registered with a different template.");
                }

                return existing.StaticOffset;
            }

            // FreeBSD/AMD64 Variant II: choose the smallest offset satisfying
            // offset - previous >= size and (-offset) % align == p_vaddr % align.
            var staticOffset = CalculateStaticOffset(
                _staticTlsSize,
                memorySize,
                normalizedAlignment,
                alignmentBias);
            if (staticOffset > StartupStaticTlsReservation)
            {
                throw new InvalidOperationException(
                    $"Static TLS requires 0x{staticOffset:X} bytes, but startup maps only " +
                    $"0x{StartupStaticTlsReservation:X} bytes below the thread pointer.");
            }
            _modules.Add(moduleId, new ModuleTemplate
            {
                ModuleId = moduleId,
                InitImage = initImage.ToArray(),
                MemorySize = memorySize,
                Alignment = normalizedAlignment,
                AlignmentBias = alignmentBias,
                StaticOffset = staticOffset,
            });
            _staticTlsSize = staticOffset;
            _maximumAlignment = Math.Max(_maximumAlignment, normalizedAlignment);
            _generation++;
            return staticOffset;
        }
    }

    /// <summary>Returns the static TP-relative block offset for a module.</summary>
    public static bool TryGetStaticOffset(ulong moduleId, out ulong staticOffset)
    {
        lock (_gate)
        {
            if (_modules.TryGetValue(moduleId, out var module))
            {
                staticOffset = module.StaticOffset;
                return true;
            }
        }

        staticOffset = 0;
        return false;
    }

    /// <summary>Clears all module templates and frees dynamic DTV blocks.</summary>
    public static void Reset()
    {
        lock (_gate)
        {
            foreach (var dtv in _threadDtvs.Values)
            {
                FreeDynamicEntries(dtv);
            }

            _threadDtvs.Clear();
            _modules.Clear();
            _staticTlsSize = 0;
            _maximumAlignment = 1;
            _generation++;
        }
    }

    /// <summary>
    /// Initializes every currently registered startup TLS block for a thread
    /// and records their addresses in that thread's DTV.
    /// </summary>
    public static void SeedThreadBlock(CpuContext context, ulong threadPointer)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (threadPointer == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_threadDtvs.Remove(threadPointer, out var oldDtv))
            {
                FreeDynamicEntries(oldDtv);
            }

            var dtv = new ThreadDtv();
            _threadDtvs.Add(threadPointer, dtv);
            foreach (var module in _modules.Values)
            {
                dtv.Entries[module.ModuleId] = CreateStaticOrDynamicEntry(context, threadPointer, module);
            }
            RebuildGuestDtv(context, threadPointer, dtv);
        }
    }

    /// <summary>
    /// Implements the DTV lookup used by <c>__tls_get_addr</c>. Unknown module
    /// IDs and offsets outside the module's TLS image are rejected with zero.
    /// </summary>
    public static ulong ResolveAddress(CpuContext context, ulong moduleId, ulong offset)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.FsBase == 0 || moduleId == 0)
        {
            return 0;
        }

        lock (_gate)
        {
            if (!_modules.TryGetValue(moduleId, out var module) || offset >= module.MemorySize)
            {
                return 0;
            }

            if (!_threadDtvs.TryGetValue(context.FsBase, out var dtv))
            {
                dtv = new ThreadDtv();
                _threadDtvs.Add(context.FsBase, dtv);
                foreach (var startupModule in _modules.Values)
                {
                    dtv.Entries[startupModule.ModuleId] = CreateStaticOrDynamicEntry(context, context.FsBase, startupModule);
                }
                RebuildGuestDtv(context, context.FsBase, dtv);
            }

            var entryAdded = false;
            if (!dtv.Entries.TryGetValue(moduleId, out var entry))
            {
                // This module appeared after the thread's startup TLS layout was
                // seeded. Model rtld's lazy DTV allocation instead of assuming
                // unused space exists below the already-live thread pointer.
                entry = CreateDynamicEntry(module);
                dtv.Entries.Add(moduleId, entry);
                entryAdded = true;
            }

            if (entryAdded || dtv.Generation != _generation)
            {
                RebuildGuestDtv(context, context.FsBase, dtv);
            }

            return checked(entry.Address + offset);
        }
    }

    private static DtvEntry CreateStaticOrDynamicEntry(
        CpuContext context,
        ulong threadPointer,
        ModuleTemplate module)
    {
        if (threadPointer >= module.StaticOffset)
        {
            var address = threadPointer - module.StaticOffset;
            var zeroImage = module.MemorySize == 0 ? [] : new byte[(int)module.MemorySize];
            if ((zeroImage.Length == 0 || context.Memory.TryWrite(address, zeroImage)) &&
                (module.InitImage.Length == 0 || context.Memory.TryWrite(address, module.InitImage)))
            {
                return new DtvEntry { Address = address };
            }
        }

        return CreateDynamicEntry(module);
    }

    private static DtvEntry CreateDynamicEntry(ModuleTemplate module)
    {
        var size = Math.Max(1UL, module.MemorySize);
        var allocationSize = checked(size + module.Alignment - 1);
        if (allocationSize > int.MaxValue)
        {
            throw new OutOfMemoryException("TLS module allocation exceeds the supported host allocation size.");
        }

        var allocationBase = Marshal.AllocHGlobal((int)allocationSize);
        var alignedAddress = AlignUp(unchecked((ulong)allocationBase), module.Alignment);
        Marshal.Copy(new byte[(int)size], 0, unchecked((nint)alignedAddress), (int)size);
        if (module.InitImage.Length != 0)
        {
            Marshal.Copy(module.InitImage, 0, unchecked((nint)alignedAddress), module.InitImage.Length);
        }

        return new DtvEntry
        {
            Address = alignedAddress,
            AllocationBase = allocationBase,
        };
    }

    private static void FreeDynamicEntries(ThreadDtv dtv)
    {
        foreach (var entry in dtv.Entries.Values)
        {
            if (entry.AllocationBase != 0)
            {
                Marshal.FreeHGlobal(entry.AllocationBase);
            }
        }
        if (dtv.DtvAllocationBase != 0)
        {
            Marshal.FreeHGlobal(dtv.DtvAllocationBase);
            dtv.DtvAllocationBase = 0;
            dtv.DtvAddress = 0;
        }
    }

    private static void RebuildGuestDtv(CpuContext context, ulong threadPointer, ThreadDtv dtv)
    {
        var maximumModuleId = _modules.Count == 0 ? 0UL : _modules.Keys.Max();
        var byteSize = checked(2UL * sizeof(ulong) + maximumModuleId * sizeof(ulong));
        if (byteSize > int.MaxValue)
        {
            throw new OutOfMemoryException("Guest DTV exceeds the supported host allocation size.");
        }

        var newAllocation = Marshal.AllocHGlobal((int)Math.Max((ulong)sizeof(ulong) * 2, byteSize));
        var newAddress = unchecked((ulong)newAllocation);
        Marshal.Copy(new byte[(int)Math.Max((ulong)sizeof(ulong) * 2, byteSize)], 0, newAllocation, (int)Math.Max((ulong)sizeof(ulong) * 2, byteSize));
        Marshal.WriteInt64(newAllocation, 0, unchecked((long)_generation));
        Marshal.WriteInt64(newAllocation, sizeof(ulong), unchecked((long)maximumModuleId));
        foreach (var (moduleId, entry) in dtv.Entries)
        {
            var slotOffset = checked((int)(2UL * sizeof(ulong) + (moduleId - 1) * sizeof(ulong)));
            Marshal.WriteInt64(newAllocation, slotOffset, unchecked((long)entry.Address));
        }

        if (!context.TryWriteUInt64(threadPointer + sizeof(ulong), newAddress))
        {
            Marshal.FreeHGlobal(newAllocation);
            throw new InvalidOperationException("Failed to install the guest DTV pointer in the thread control block.");
        }

        if (dtv.DtvAllocationBase != 0)
        {
            Marshal.FreeHGlobal(dtv.DtvAllocationBase);
        }
        dtv.DtvAllocationBase = newAllocation;
        dtv.DtvAddress = newAddress;
        dtv.Generation = _generation;
    }

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        var mask = alignment - 1;
        return checked((value + mask) & ~mask);
    }

    private static ulong CalculateStaticOffset(
        ulong previousOffset,
        ulong size,
        ulong alignment,
        ulong alignmentBias)
    {
        var result = checked(previousOffset + size + alignment - 1);
        return result - ((result + alignmentBias) & (alignment - 1));
    }

    [Conditional("DEBUG")]
    private static void RunTlsLayoutSelfChecks()
    {
        var firstOffset = RegisterModule(1, [0x11, 0x22], 0x20, 0x10, 0);
        var secondOffset = RegisterModule(2, [0x7A], 0x18, 0x20, 8);
        Debug.Assert(firstOffset == 0x20, "First Variant II TLS offset is incorrect.");
        Debug.Assert(secondOffset >= firstOffset + 0x18, "TLS modules overlap in the static layout.");
        Debug.Assert((unchecked(0UL - secondOffset) & 0x1F) == 8, "PT_TLS virtual-address congruence was not preserved.");

        var lateEntry = CreateDynamicEntry(_modules[2]);
        try
        {
            Debug.Assert(lateEntry.AllocationBase != 0, "A late TLS module did not receive a dynamic DTV block.");
            Debug.Assert(Marshal.ReadByte(unchecked((nint)lateEntry.Address)) == 0x7A, "Late-module tdata was not initialized.");
            Debug.Assert(Marshal.ReadByte(unchecked((nint)lateEntry.Address + 1)) == 0, "Late-module tbss was not zero initialized.");
        }
        finally
        {
            Marshal.FreeHGlobal(lateEntry.AllocationBase);
            Reset();
        }
    }
}
