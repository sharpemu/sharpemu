// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

using SharpEmu.HLE;

namespace SharpEmu.Libs.Ult;

public static partial class UltExports
{
    internal const int ErrorNull = unchecked((int)0x80810001);
    internal const int ErrorInvalid = unchecked((int)0x80810004);
    internal const int ErrorPermission = unchecked((int)0x80810005);
    internal const int ErrorBusy = unchecked((int)0x80810007);
    internal const int ErrorAgain = unchecked((int)0x80810008);

    // The guest only allocates this block and hands it back to sceUltUlthreadRuntimeCreate;
    // the HLE runtime keeps its state on the host side and never touches it, so the size only has to be plausible.
    private const ulong WorkAreaHeaderSize = 0x100;
    private const ulong WorkAreaPerUlthread = 0x100;
    private const ulong WorkAreaPerWorker = 0x400;

    private const int UlthreadPriority = 700;
    private const ulong UlthreadAffinityMask = 0x7FUL;
    private const int OptParamSize = 128;

    private sealed class RuntimeState(ulong address, string name, uint maxUlthreads)
    {
        public ulong Address { get; } = address;

        public string Name { get; } = name;

        public uint MaxUlthreads { get; } = maxUlthreads;

        public int LiveUlthreads;
        
        public List<ulong> Workers { get; } = new();

        public Stack<ulong> IdleWorkers { get; } = new();

        public Queue<UlthreadState> PendingUlthreads { get; } = new();
    }

    private sealed class UlthreadState(ulong address, ulong runtimeAddress, string name, ulong entry, ulong argument)
    {
        public ulong Address { get; } = address;

        public ulong RuntimeAddress { get; } = runtimeAddress;

        public string Name { get; } = name;

        public ulong Entry { get; } = entry;

        public ulong Argument { get; } = argument;
        
        public ulong WorkerHandle;

        public bool Done;

        public ulong ExitValue;
    }

    private static readonly ConcurrentDictionary<ulong, RuntimeState> Runtimes = new();
    private static readonly ConcurrentDictionary<ulong, UlthreadState> Ulthreads = new();

    private static readonly bool TraceEnabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_ULT"),
        "1",
        StringComparison.Ordinal);

    internal static void ResetRuntimeState()
    {
        Runtimes.Clear();
        Ulthreads.Clear();
        ResetSyncState();
    }
    
    [SysAbiExport(
        Nid = "hZIg1EWGsHM",
        ExportName = "sceUltInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int UltInitialize(CpuContext ctx)
    {
        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "grs2pbc2awM",
        ExportName = "sceUltUlthreadRuntimeGetWorkAreaSize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int UlthreadRuntimeGetWorkAreaSize(CpuContext ctx)
    {
        var maxUlthreads = (uint)ctx[CpuRegister.Rdi];
        var workerThreads = (uint)ctx[CpuRegister.Rsi];
        ctx[CpuRegister.Rax] = ComputeRuntimeWorkAreaSize(maxUlthreads, workerThreads);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    internal static ulong ComputeRuntimeWorkAreaSize(uint maxUlthreads, uint workerThreads)
    {
        return WorkAreaHeaderSize +
            (maxUlthreads * WorkAreaPerUlthread) +
            (workerThreads * WorkAreaPerWorker);
    }
    
    [SysAbiExport(
        Nid = "7+pdaXR9jWw",
        ExportName = "sceUltUlthreadRuntimeOptParamInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    [SysAbiExport(
        Nid = "V2u3WLrwh64",
        ExportName = "_sceUltUlthreadRuntimeOptParamInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int UlthreadRuntimeOptParamInitialize(CpuContext ctx)
    {
        return OptParamInitialize(ctx);
    }

    [SysAbiExport(
        Nid = "V89UhsfEhJk",
        ExportName = "sceUltUlthreadOptParamInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    [SysAbiExport(
        Nid = "vIuen2qlRmo",
        ExportName = "_sceUltUlthreadOptParamInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int UlthreadOptParamInitialize(CpuContext ctx)
    {
        return OptParamInitialize(ctx);
    }

    private static int OptParamInitialize(CpuContext ctx)
    {
        var param = ctx[CpuRegister.Rdi];
        if (param == 0)
        {
            return ctx.SetReturn(ErrorNull, typeof(long));
        }

        // Opt params are reserved blocks; all-zero means "use defaults".
        Span<byte> zero = stackalloc byte[OptParamSize];
        zero.Clear();
        ctx.Memory.TryWrite(param, zero);
        return ctx.SetReturn(0, typeof(long));
    }
    
    [SysAbiExport(
        Nid = "81ag9-mItVY",
        ExportName = "sceUltUlthreadRuntimeCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    [SysAbiExport(
        Nid = "jw9FkZBXo-g",
        ExportName = "_sceUltUlthreadRuntimeCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int UlthreadRuntimeCreate(CpuContext ctx)
    {
        var runtime = ctx[CpuRegister.Rdi];
        var nameAddress = ctx[CpuRegister.Rsi];
        var maxUlthreads = (uint)ctx[CpuRegister.Rdx];
        var workerThreads = (uint)ctx[CpuRegister.Rcx];
        var workArea = ctx[CpuRegister.R8];

        if (runtime == 0 || workArea == 0)
        {
            return ctx.SetReturn(ErrorNull, typeof(long));
        }

        if (maxUlthreads == 0 || workerThreads == 0)
        {
            return ctx.SetReturn(ErrorInvalid, typeof(long));
        }

        var name = ReadName(ctx, nameAddress);
        if (!Runtimes.TryAdd(runtime, new RuntimeState(runtime, name, maxUlthreads)))
        {
            return ctx.SetReturn(ErrorBusy, typeof(long));
        }

        TraceUlt(
            $"runtime_create: runtime=0x{runtime:X16} name='{name}' " +
            $"max_ulthreads={maxUlthreads} workers={workerThreads}");
        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "-gxcs521SvA",
        ExportName = "sceUltUlthreadRuntimeDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int UlthreadRuntimeDestroy(CpuContext ctx)
    {
        var runtime = ctx[CpuRegister.Rdi];
        if (runtime == 0)
        {
            return ctx.SetReturn(ErrorNull, typeof(long));
        }

        if (!Runtimes.TryGetValue(runtime, out var state))
        {
            return ctx.SetReturn(ErrorInvalid, typeof(long));
        }

        if (Volatile.Read(ref state.LiveUlthreads) != 0)
        {
            return ctx.SetReturn(ErrorBusy, typeof(long));
        }

        Runtimes.TryRemove(runtime, out _);
        return ctx.SetReturn(0, typeof(long));
    }
    
    [SysAbiExport(
        Nid = "RlN-xQPtYnM",
        ExportName = "sceUltUlthreadCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    [SysAbiExport(
        Nid = "znI3q8S7KQ4",
        ExportName = "_sceUltUlthreadCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int UlthreadCreate(CpuContext ctx)
    {
        var ulthread = ctx[CpuRegister.Rdi];
        var nameAddress = ctx[CpuRegister.Rsi];
        var entry = ctx[CpuRegister.Rdx];
        var argument = ctx[CpuRegister.Rcx];

        if (!ctx.TryGetImportStackArgument(0, out var runtime) &&
            !ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + sizeof(ulong), out runtime))
        {
            return ctx.SetReturn(ErrorNull, typeof(long));
        }

        if (ulthread == 0 || entry == 0 || runtime == 0)
        {
            return ctx.SetReturn(ErrorNull, typeof(long));
        }

        if (!Runtimes.TryGetValue(runtime, out var runtimeState))
        {
            return ctx.SetReturn(ErrorInvalid, typeof(long));
        }

        if (Interlocked.Increment(ref runtimeState.LiveUlthreads) > runtimeState.MaxUlthreads)
        {
            Interlocked.Decrement(ref runtimeState.LiveUlthreads);
            return ctx.SetReturn(ErrorAgain, typeof(long));
        }

        var name = ReadName(ctx, nameAddress);
        var state = new UlthreadState(ulthread, runtime, name, entry, argument);
        Ulthreads[ulthread] = state;

        TraceUlt(
            $"ulthread_create: ulthread=0x{ulthread:X16} name='{name}' entry=0x{entry:X16} " +
            $"arg=0x{argument:X16} runtime=0x{runtime:X16}");

        if (GuestThreadExecution.Scheduler is not { } scheduler)
        {
            // Without a scheduler nothing can run the entry; treat it as done.
            lock (SyncGate)
            {
                state.Done = true;
            }

            return ctx.SetReturn(0, typeof(long));
        }

        if (!DispatchUlthread(ctx, scheduler, runtimeState, state, out var error))
        {
            Console.Error.WriteLine(
                $"[LOADER][ERROR] sceUltUlthreadCreate: failed to schedule ulthread '{name}' " +
                $"entry=0x{entry:X16}: {error}");
            ForgetUlthread(state);
            return ctx.SetReturn(ErrorAgain, typeof(long));
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "gCeAI57LGgI",
        ExportName = "sceUltUlthreadJoin",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int UlthreadJoin(CpuContext ctx)
    {
        return JoinCore(ctx, tryOnly: false);
    }

    [SysAbiExport(
        Nid = "DsW+3FTXL0Q",
        ExportName = "sceUltUlthreadTryJoin",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int UlthreadTryJoin(CpuContext ctx)
    {
        return JoinCore(ctx, tryOnly: true);
    }

    private static int JoinCore(CpuContext ctx, bool tryOnly)
    {
        var ulthread = ctx[CpuRegister.Rdi];
        var statusAddress = ctx[CpuRegister.Rsi];
        if (ulthread == 0)
        {
            return ctx.SetReturn(ErrorNull, typeof(long));
        }

        if (!Ulthreads.TryGetValue(ulthread, out var state))
        {
            return ctx.SetReturn(ErrorInvalid, typeof(long));
        }

        lock (SyncGate)
        {
            if (!state.Done && state.WorkerHandle != 0 &&
                state.WorkerHandle == GuestThreadExecution.CurrentGuestThreadHandle)
            {
                return ctx.SetReturn(ErrorPermission, typeof(long));
            }

            if (tryOnly && !state.Done)
            {
                return ctx.SetReturn(ErrorBusy, typeof(long));
            }
        }

        var memory = ctx.Memory;
        return BlockUntil(ctx, "sceUltUlthreadJoin", () => TryCompleteJoin(memory, state, statusAddress));
    }

    private static bool TryCompleteJoin(ICpuMemory memory, UlthreadState state, ulong statusAddress)
    {
        lock (SyncGate)
        {
            if (!state.Done)
            {
                return false;
            }
        }

        if (statusAddress != 0)
        {
            Span<byte> status = stackalloc byte[sizeof(int)];
            BitConverter.TryWriteBytes(status, unchecked((int)state.ExitValue));
            memory.TryWrite(statusAddress, status);
        }

        ForgetUlthread(state);
        return true;
    }

    [SysAbiExport(
        Nid = "kF--s3xZjco",
        ExportName = "sceUltUlthreadExit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int UlthreadExit(CpuContext ctx)
    {
        var status = unchecked((int)ctx[CpuRegister.Rdi]);
        if (CurrentUlthread() is null)
        {
            return ctx.SetReturn(ErrorPermission, typeof(long));
        }

        // Unwinds the ulthread's entry;
        // the worker reports the status as the entry's return value and goes back to the pool.
        GuestThreadExecution.RequestCurrentEntryExit("sceUltUlthreadExit", status);
        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "HFd-lpjGxJA",
        ExportName = "sceUltUlthreadYield",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int UlthreadYield(CpuContext ctx)
    {
        if (!Thread.Yield())
        {
            Thread.Sleep(0);
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "xOQ9oACobA4",
        ExportName = "sceUltUlthreadGetSelf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int UlthreadGetSelf(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdi];
        if (outAddress == 0)
        {
            return ctx.SetReturn(ErrorNull, typeof(long));
        }

        if (CurrentUlthread() is not { } state)
        {
            return ctx.SetReturn(ErrorPermission, typeof(long));
        }

        ctx.TryWriteUInt64(outAddress, state.Address);
        return ctx.SetReturn(0, typeof(long));
    }

    private static void ForgetUlthread(UlthreadState state)
    {
        if (Ulthreads.TryRemove(new KeyValuePair<ulong, UlthreadState>(state.Address, state)) &&
            Runtimes.TryGetValue(state.RuntimeAddress, out var runtimeState))
        {
            Interlocked.Decrement(ref runtimeState.LiveUlthreads);
        }
    }

    private static string ReadName(CpuContext ctx, ulong address)
    {
        return ctx.TryReadNullTerminatedUtf8(address, 32, out var name) ? name : string.Empty;
    }

    private static void TraceUlt(string message)
    {
        if (TraceEnabled)
        {
            Console.Error.WriteLine("[LOADER][TRACE] ult " + message);
        }
    }
}
