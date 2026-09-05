// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

public static class GuestGpuMemoryHook
{
    private static GuestGpuMemory? _current;

    // Keep these counts for the shutdown summary.
    private static long _mapped;
    private static long _unmapped;
    private static long _faultsResolved;
    private static long _faultsDeclined;
    private static int _summaryTaken;

    public static GuestGpuMemory? Current => _current;

    public static void Attach(GuestGpuMemory? memory)
    {
        if (memory != null && _current != null)
        {
            Environment.FailFast("Cannot attach a GPU memory manager while another manager is attached.");
        }

        if (memory != null)
        {
            Interlocked.Exchange(ref _mapped, 0);
            Interlocked.Exchange(ref _unmapped, 0);
            Interlocked.Exchange(ref _faultsResolved, 0);
            Interlocked.Exchange(ref _faultsDeclined, 0);
            Interlocked.Exchange(ref _summaryTaken, 0);
        }

        _current = memory;
    }

    public static bool IsWithinGpuAddressSpace(ulong address, ulong size)
    {
        // Exclude the upper address limit here. GuestSpan.IsValid includes that limit.
        return address != 0 && size != 0 && address < TrackerLayout.SpaceBytes && size < TrackerLayout.SpaceBytes - address;
    }

    public static void NoteMapped(ulong address, ulong size)
    {
        if (_current == null || !IsWithinGpuAddressSpace(address, size))
        {
            return;
        }

        Interlocked.Increment(ref _mapped);
        _current.Register(address, size);
    }

    public static void NoteUnmapped(ulong address, ulong size)
    {
        if (_current == null || !IsWithinGpuAddressSpace(address, size))
        {
            return;
        }

        Interlocked.Increment(ref _unmapped);
        _current.Unregister(address, size);
    }

    public static bool TryResolveFault(FaultKind kind, ulong address)
    {
        if (_current != null && _current.TryResolveFault(kind, address))
        {
            Interlocked.Increment(ref _faultsResolved);
            return true;
        }

        Interlocked.Increment(ref _faultsDeclined);
        return false;
    }

    public static void MarkCpuWrite(ulong address, ulong size)
    {
        if (size == 0)
        {
            return;
        }

        _ = _current!.MarkCpuWrite(address, size);
    }

    // Shutdown can skip disposal. Report the counts once for each attached session.
    public static bool TryTakeShutdownSummary(out string summary)
    {
        summary = string.Empty;
        if (_current == null || Interlocked.Exchange(ref _summaryTaken, 1) != 0)
        {
            return false;
        }

        summary = GetSummary();
        return true;
    }

    public static string GetSummary() =>
        $"gpu_memory: mapped={Interlocked.Read(ref _mapped)} unmapped={Interlocked.Read(ref _unmapped)} faults_resolved={Interlocked.Read(ref _faultsResolved)} faults_declined={Interlocked.Read(ref _faultsDeclined)}";
}
