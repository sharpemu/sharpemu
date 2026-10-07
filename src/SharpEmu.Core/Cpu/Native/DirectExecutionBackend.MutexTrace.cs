// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Diagnostics;
using System.Collections.Generic;
using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Native;

public sealed partial class DirectExecutionBackend
{
    private readonly object _mutexTraceGate = new();
    private ulong _mutexTraceAddress;
    private GuestOwnershipTrace? _ownershipTrace;
    private MutexTraceEntry[]? _mutexTraceEntries;
    private int _mutexTraceCount;
    private long _mutexTraceDropped;
    private readonly HashSet<CpuContext> _mutexTraceContexts = new();
    private MutexLocationEntry[]? _mutexLocationEntries;
    private int _mutexLocationCount;
    private long _mutexLocationDropped;
    private ulong _mutexSnapshotStart;
    private ulong _mutexSnapshotEnd;
    private readonly Queue<MutexRegisterEntry> _mutexRegisterEntries = new();
    private long _mutexRegisterTotal;
    private readonly HashSet<ulong> _mutexMemoryCallers = new();
    private string[] _mutexMemoryPaths = Array.Empty<string>();
    private readonly Queue<MutexMemoryEntry> _mutexMemoryEntries = new();
    private long _mutexMemoryTotal;
    private readonly record struct MutexMemoryRegion(string Path, ulong Address, byte[]? Bytes);
    private readonly record struct MutexMemoryEntry(long Timestamp, ulong Thread, uint HostThread,
        string Name, bool Returned, ulong Caller,
        MutexMemoryRegion[] Regions);
    private readonly record struct MutexRegisterEntry(long Timestamp, string Name,
        HostThreadContextSnapshot Registers, byte[]? Stack);
    private readonly record struct MutexLocationEntry(long Timestamp, string Name, ulong Instruction,
        int ImportIndex, string? BlockReason);

    private readonly record struct MutexTraceEntry(long Timestamp, ulong Thread, uint HostThread, string Name,
        bool Returned, ulong Result, ulong Caller, ulong Argument0, ulong Argument1,
        ulong Register13, ulong Register14, ulong Register15);

    private void InitializeMutexTrace()
    {
        _mutexTraceAddress = ParseOptionalHexAddress(
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_MUTEX_ADDRESS"));
        var ownershipPlan = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_OWNERSHIP_PLAN");
        if (!string.IsNullOrWhiteSpace(ownershipPlan) && _ownershipTrace == null)
        {
            _ownershipTrace = GuestOwnershipTrace.Load(ownershipPlan);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => _ownershipTrace.Write(Console.Error);
        }
        if (_mutexTraceAddress == 0 || _mutexTraceEntries != null) return;
        _mutexTraceEntries = new MutexTraceEntry[65536];
        _mutexLocationEntries = new MutexLocationEntry[65536];
        _mutexSnapshotStart = ParseOptionalHexAddress(Environment.GetEnvironmentVariable("SHARPEMU_TRACE_MUTEX_RIP_START"));
        _mutexSnapshotEnd = ParseOptionalHexAddress(Environment.GetEnvironmentVariable("SHARPEMU_TRACE_MUTEX_RIP_END"));
        foreach (var caller in (Environment.GetEnvironmentVariable("SHARPEMU_TRACE_MUTEX_MEMORY_CALLERS") ?? "")
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var address = ParseOptionalHexAddress(caller);
            if (address != 0) _mutexMemoryCallers.Add(address);
        }
        _mutexMemoryPaths = (Environment.GetEnvironmentVariable("SHARPEMU_TRACE_MUTEX_MEMORY_PATHS") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (_mutexMemoryPaths.Length > 16) Array.Resize(ref _mutexMemoryPaths, 16);
        AppDomain.CurrentDomain.ProcessExit += WriteMutexTrace;
    }

    internal static bool IsMutexTraceSynchronizationImport(string name) => name is
        "scePthreadMutexLock" or "scePthreadMutexTrylock" or "scePthreadMutexUnlock" or
        "scePthreadCondWait" or "scePthreadCondTimedwait" or "scePthreadCondSignal" or
        "scePthreadCondBroadcast" or "pthread_mutex_lock" or "pthread_mutex_trylock" or
        "pthread_mutex_unlock" or "pthread_cond_wait" or "pthread_cond_timedwait" or
        "pthread_cond_signal" or "pthread_cond_broadcast";

    private unsafe ulong ResolveMutexTraceCaller(nint arguments)
    {
        var caller = *(ulong*)(arguments + 96);
        if (_mutexMemoryCallers.Contains(caller) || _ownershipTrace?.IncludesCaller(caller) == true) return caller;
        var frame = *(ulong*)(arguments + 56);
        if (frame != 0 && frame <= ulong.MaxValue - sizeof(ulong) &&
            TryReadStackU64(frame + sizeof(ulong), out var savedCaller))
            caller = savedCaller;
        return caller;
    }

    private unsafe void RecordOwnershipTrace(string name, nint arguments, ulong caller, bool returned, ulong result)
    {
        if (ActiveCpuContext is not { } context || !_ownershipTrace!.ShouldCapture(name, caller, returned)) return;
        var registers = new Dictionary<string, ulong>
        {
            ["arg0"] = *(ulong*)arguments,
            ["arg1"] = *(ulong*)(arguments + 8),
            ["arg2"] = *(ulong*)(arguments + 16),
            ["rbx"] = *(ulong*)(arguments + 48),
            ["frame"] = *(ulong*)(arguments + 56),
            ["r12"] = *(ulong*)(arguments + 64),
            ["r13"] = *(ulong*)(arguments + 72),
            ["r14"] = *(ulong*)(arguments + 80),
            ["r15"] = *(ulong*)(arguments + 88),
            ["stack"] = (ulong)arguments + 96,
        };
        _ownershipTrace!.Record(context, name, caller, returned, result, GetCurrentThreadId(), registers);
    }

    private unsafe void RecordMutexTrace(string name, nint arguments, ulong caller, bool returned, ulong result)
    {
        var frame = *(ulong*)(arguments + 56);
        var entry = new MutexTraceEntry(Stopwatch.GetTimestamp(),
            GuestThreadExecution.CurrentGuestThreadHandle, GetCurrentThreadId(), name, returned, result, caller,
            *(ulong*)arguments, *(ulong*)(arguments + 8),
            *(ulong*)(arguments + 72), *(ulong*)(arguments + 80), *(ulong*)(arguments + 88));
        if (_mutexMemoryCallers.Contains(caller) &&
            ActiveCpuContext is { } memoryContext)
            RecordMutexMemory(memoryContext, entry, frame, (ulong)arguments + 96);
        lock (_mutexTraceGate)
        {
            if (ActiveCpuContext is { } context) _mutexTraceContexts.Add(context);
            if (_mutexTraceCount == _mutexTraceEntries!.Length)
            {
                _mutexTraceDropped++;
                return;
            }
            _mutexTraceEntries[_mutexTraceCount++] = entry;
        }
    }

    private void RecordMutexLocation(CpuContext context, string name, ulong instruction, string? blockReason)
    {
        if (_mutexTraceAddress == 0) return;
        lock (_mutexTraceGate)
        {
            if (!_mutexTraceContexts.Contains(context)) return;
            if (_mutexLocationCount == _mutexLocationEntries!.Length)
            {
                _mutexLocationDropped++;
                return;
            }
            _mutexLocationEntries[_mutexLocationCount++] = new MutexLocationEntry(
                Stopwatch.GetTimestamp(), name, instruction, context.ActiveImportIndex, blockReason);
        }
    }

    private void WriteMutexTrace(object? sender, EventArgs arguments)
    {
        MutexTraceEntry[] entries;
        long dropped;
        MutexLocationEntry[] locations;
        long locationsDropped;
        MutexRegisterEntry[] registers;
        long registerTotal;
        MutexMemoryEntry[] memory;
        long memoryTotal;
        lock (_mutexTraceGate)
        {
            entries = _mutexTraceEntries![.._mutexTraceCount];
            dropped = _mutexTraceDropped;
            locations = _mutexLocationEntries![.._mutexLocationCount];
            locationsDropped = _mutexLocationDropped;
            registers = _mutexRegisterEntries.ToArray();
            registerTotal = _mutexRegisterTotal;
            memory = _mutexMemoryEntries.ToArray();
            memoryTotal = _mutexMemoryTotal;
        }
        Console.Error.WriteLine($"[LOADER][TRACE] mutex-events address=0x{_mutexTraceAddress:X16} " +
            $"count={entries.Length} dropped={dropped} frequency={Stopwatch.Frequency}");
        foreach (var entry in entries)
            Console.Error.WriteLine($"[LOADER][TRACE] mutex-event ticks={entry.Timestamp} " +
                $"thread=0x{entry.Thread:X16} host_thread={entry.HostThread} name={entry.Name} returned={entry.Returned} " +
                $"result=0x{entry.Result:X16} caller=0x{entry.Caller:X16} " +
                $"argument0=0x{entry.Argument0:X16} argument1=0x{entry.Argument1:X16} " +
                $"r13=0x{entry.Register13:X16} r14=0x{entry.Register14:X16} r15=0x{entry.Register15:X16}");
        Console.Error.WriteLine($"[LOADER][TRACE] mutex-locations count={locations.Length} dropped={locationsDropped}");
        foreach (var location in locations)
        {
            var import = (uint)location.ImportIndex < (uint)_importEntries.Length
                ? _importEntries[location.ImportIndex].Export?.Name ?? _importEntries[location.ImportIndex].Nid
                : "none";
            Console.Error.WriteLine($"[LOADER][TRACE] mutex-location ticks={location.Timestamp} " +
                $"name={location.Name} rip=0x{location.Instruction:X16} import={import} blocked={location.BlockReason ?? "none"}");
        }
        Console.Error.WriteLine($"[LOADER][TRACE] mutex-memory retained={memory.Length} total={memoryTotal} atomic=False");
        foreach (var entry in memory)
            foreach (var region in entry.Regions)
                Console.Error.WriteLine($"[LOADER][TRACE] mutex-memory-region ticks={entry.Timestamp} " +
                    $"thread=0x{entry.Thread:X16} host_thread={entry.HostThread} name={entry.Name} returned={entry.Returned} " +
                    $"caller=0x{entry.Caller:X16} path={region.Path} " +
                    $"address=0x{region.Address:X16} readable={region.Bytes != null} " +
                    $"bytes={(region.Bytes == null ? string.Empty : Convert.ToHexString(region.Bytes))}");
        Console.Error.WriteLine($"[LOADER][TRACE] mutex-registers retained={registers.Length} total={registerTotal}");
        foreach (var entry in registers)
        {
            var value = entry.Registers;
            Console.Error.WriteLine($"[LOADER][TRACE] mutex-register ticks={entry.Timestamp} name={entry.Name} " +
                $"rip=0x{value.Rip:X16} rsp=0x{value.Rsp:X16} rbp=0x{value.Rbp:X16} " +
                $"rax=0x{value.Rax:X16} rbx=0x{value.Rbx:X16} rcx=0x{value.Rcx:X16} " +
                $"rdx=0x{value.Rdx:X16} rsi=0x{value.Rsi:X16} rdi=0x{value.Rdi:X16} " +
                $"r8=0x{value.R8:X16} r9=0x{value.R9:X16} r10=0x{value.R10:X16} " +
                $"r11=0x{value.R11:X16} r12=0x{value.R12:X16} r13=0x{value.R13:X16} " +
                $"r14=0x{value.R14:X16} r15=0x{value.R15:X16} " +
                $"stack_readable={entry.Stack != null} stack={(entry.Stack == null ? string.Empty : Convert.ToHexString(entry.Stack))}");
        }
    }

    private void RecordMutexMemory(CpuContext context, MutexTraceEntry entry, ulong frame, ulong stack)
    {
        var regions = new MutexMemoryRegion[_mutexMemoryPaths.Length];
        for (var index = 0; index < regions.Length; index++)
        {
            var path = _mutexMemoryPaths[index];
            var readable = TryResolveMutexMemoryPath(context, path, frame, entry.Register15, out var address, stack);
            byte[]? bytes = new byte[512];
            if (!readable || address == 0 || address > ulong.MaxValue - (ulong)bytes.Length ||
                !context.Memory.TryRead(address, bytes)) bytes = null;
            regions[index] = new MutexMemoryRegion(path, address, bytes);
        }
        // Other threads can change these regions. The snapshots are not an atomic memory image.
        lock (_mutexTraceGate)
        {
            if (_mutexMemoryEntries.Count == 2048) _mutexMemoryEntries.Dequeue();
            _mutexMemoryEntries.Enqueue(new MutexMemoryEntry(entry.Timestamp, entry.Thread, entry.HostThread,
                entry.Name, entry.Returned, entry.Caller, regions));
            _mutexMemoryTotal++;
        }
    }

    internal static bool TryResolveMutexMemoryPath(CpuContext context, string path, ulong frame,
        ulong register15, out ulong address, ulong stack = 0)
    {
        var parts = path.Split('/');
        address = parts[0] switch
        {
            "frame" => frame,
            "stack" => stack,
            "r15" => register15,
            _ => ParseOptionalHexAddress(parts[0]),
        };
        if (address == 0 || parts.Length > 9) return false;
        for (var index = 1; index < parts.Length; index++)
        {
            if (!ulong.TryParse(parts[index], System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture, out var offset) ||
                offset > ulong.MaxValue - address || address + offset > ulong.MaxValue - sizeof(ulong) ||
                !context.TryReadUInt64(address + offset, out address) || address == 0)
                return false;
        }
        return true;
    }

    private void RecordMutexRegisters(CpuContext context, string name, HostThreadContextSnapshot registers)
    {
        if (_mutexTraceAddress == 0 || _mutexSnapshotStart == 0 ||
            registers.Rip < _mutexSnapshotStart || registers.Rip >= _mutexSnapshotEnd) return;
        lock (_mutexTraceGate)
        {
            if (!_mutexTraceContexts.Contains(context)) return;
        }
        var timestamp = Stopwatch.GetTimestamp();
        byte[]? stack = new byte[256];
        // The thread has resumed. Stack bytes are supporting evidence, not an atomic unwind.
        if (registers.Rsp == 0 || registers.Rsp > ulong.MaxValue - (ulong)stack.Length ||
            !context.Memory.TryRead(registers.Rsp, stack)) stack = null;
        lock (_mutexTraceGate)
        {
            if (_mutexRegisterEntries.Count == 2048) _mutexRegisterEntries.Dequeue();
            _mutexRegisterEntries.Enqueue(new MutexRegisterEntry(timestamp, name, registers, stack));
            _mutexRegisterTotal++;
        }
    }
}
