// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Collections.Concurrent;
using System.Text;
using System.Diagnostics.CodeAnalysis;

namespace SharpEmu.Libs.Kernel;

public static class KernelPthreadExtendedCompatExports
{
    private const int DefaultThreadPriority = 700;
    private const ulong DefaultThreadAffinityMask = 0x7FUL;
    private const int DefaultDetachState = 0;
    private const ulong DefaultGuardSize = 0x1000UL;
    private const ulong DefaultStackSize = 0x1_00000UL;
    private const int DefaultInheritSched = 4;
    private const int DefaultSchedPolicy = 1;
    private const int DefaultSchedPriority = DefaultThreadPriority;
    private const ulong SyntheticRwlockHandleBase = 0x00006003_0000_0000;
    private const ulong SyntheticPthreadAttrHandleBase = 0x00006004_0000_0000;
    private const ulong SyntheticRwlockAttrHandleBase = 0x00006005_0000_0000;

    private static readonly object _stateGate = new();
    private static readonly Dictionary<ulong, ThreadState> _threadStates = new();
    private static readonly Dictionary<ulong, PthreadAttrState> _attrStates = new();
    private static readonly Dictionary<ulong, PthreadRwlockState> _rwlockStates = new();
    private static readonly ConcurrentDictionary<int, TlsKeyState> _tlsKeys = new();
    private static int _nextTlsKey = 1;
    private static long _nextSyntheticRwlockHandleId = 1;
    private static long _nextSyntheticPthreadAttrHandleId = 1;
    private static long _nextSyntheticRwlockAttrHandleId = 1;

    private static readonly ConcurrentDictionary<ulong, ConcurrentDictionary<int, ulong>> _threadLocalSpecific = new();

    internal static void GetThreadStartScheduling(
        CpuContext ctx,
        ulong attrAddress,
        out int priority,
        out ulong affinityMask)
    {
        if (attrAddress == 0)
        {
            priority = DefaultThreadPriority;
            affinityMask = DefaultThreadAffinityMask;
            return;
        }

        var resolvedAddress = ResolvePthreadAttrHandle(ctx, attrAddress);
        lock (_stateGate)
        {
            var attributes = GetOrCreateAttrStateLocked(resolvedAddress);
            priority = attributes.SchedPriority;
            affinityMask = attributes.AffinityMask;
        }
    }

    internal static void RegisterThreadStart(
        ulong thread,
        string name,
        int priority,
        ulong affinityMask)
    {
        lock (_stateGate)
        {
            var state = GetOrCreateThreadStateLocked(thread);
            state.Name = name;
            state.Priority = priority;
            state.AffinityMask = affinityMask;
            state.Attributes = state.Attributes with
            {
                SchedPriority = priority,
                AffinityMask = affinityMask,
            };
        }
    }

    private sealed class ThreadState
    {
        public string Name { get; set; } = string.Empty;
        public int Priority { get; set; } = DefaultThreadPriority;
        public ulong AffinityMask { get; set; } = DefaultThreadAffinityMask;
        public int DetachState { get; set; } = DefaultDetachState;
        public PthreadAttrState Attributes { get; set; } = PthreadAttrState.Default;
    }

    private sealed class PthreadRwlockState
    {
        public object SyncRoot { get; } = new();
        public Dictionary<ulong, int> ReaderCounts { get; } = new();
        public int ReaderTotalCount { get; set; }
        public ulong WriterThreadId { get; set; }
        public int WaitingWriters { get; set; }

        public int GetReaderCount(ulong threadId)
        {
            return ReaderCounts.TryGetValue(threadId, out var count) ? count : 0;
        }

        public void AddReader(ulong threadId)
        {
            ReaderCounts.TryGetValue(threadId, out var currentCount);
            ReaderCounts[threadId] = currentCount + 1;
            ReaderTotalCount++;
        }

        public bool RemoveReader(ulong threadId)
        {
            if (!ReaderCounts.TryGetValue(threadId, out var currentCount) || currentCount <= 0)
            {
                return false;
            }

            if (currentCount == 1)
            {
                ReaderCounts.Remove(threadId);
            }
            else
            {
                ReaderCounts[threadId] = currentCount - 1;
            }

            ReaderTotalCount = Math.Max(0, ReaderTotalCount - 1);
            return true;
        }
    }

    private readonly record struct TlsKeyState(ulong Destructor);

    private readonly record struct PthreadAttrState(
        ulong AffinityMask,
        int DetachState,
        ulong StackAddress,
        ulong StackSize,
        ulong GuardSize,
        int InheritSched,
        int SchedPolicy,
        int SchedPriority)
    {
        public static PthreadAttrState Default =>
            new(
                DefaultThreadAffinityMask,
                DefaultDetachState,
                0,
                DefaultStackSize,
                DefaultGuardSize,
                DefaultInheritSched,
                DefaultSchedPolicy,
                DefaultSchedPriority);
    }

    [SysAbiExport(
        Nid = "4qGrR6eoP9Y",
        ExportName = "scePthreadDetach",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadDetach(CpuContext ctx)
    {
        var thread = ctx[CpuRegister.Rdi];
        if (thread == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        lock (_stateGate)
        {
            var state = GetOrCreateThreadStateLocked(thread);
            state.DetachState = 1;
            state.Attributes = state.Attributes with { DetachState = 1 };
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "How7B8Oet6k",
        ExportName = "scePthreadGetname",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadGetname(CpuContext ctx)
    {
        var thread = ctx[CpuRegister.Rdi];
        var outNameAddress = ctx[CpuRegister.Rsi];
        if (thread == 0 || outNameAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        string name;
        lock (_stateGate)
        {
            name = GetOrCreateThreadStateLocked(thread).Name;
        }

        if (!TryWriteFixedUtf8CString(ctx, outNameAddress, name, 32))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "bt3CTBKmGyI",
        ExportName = "scePthreadSetaffinity",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadSetaffinity(CpuContext ctx)
    {
        var thread = ctx[CpuRegister.Rdi];
        var mask = ctx[CpuRegister.Rsi];
        if (thread == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        lock (_stateGate)
        {
            var state = GetOrCreateThreadStateLocked(thread);
            state.AffinityMask = mask;
            state.Attributes = state.Attributes with { AffinityMask = mask };
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "rcrVFJsQWRY",
        ExportName = "scePthreadGetaffinity",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadGetaffinity(CpuContext ctx)
    {
        var thread = ctx[CpuRegister.Rdi];
        var outMaskAddress = ctx[CpuRegister.Rsi];
        if (thread == 0 || outMaskAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        ulong affinityMask;
        lock (_stateGate)
        {
            affinityMask = GetOrCreateThreadStateLocked(thread).AffinityMask;
        }

        if (!ctx.TryWriteUInt64(outMaskAddress, affinityMask))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "1tKyG7RlMJo",
        ExportName = "scePthreadGetprio",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadGetprio(CpuContext ctx)
    {
        var thread = ctx[CpuRegister.Rdi];
        var outPriorityAddress = ctx[CpuRegister.Rsi];
        if (thread == 0 || outPriorityAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        int priority;
        lock (_stateGate)
        {
            priority = GetOrCreateThreadStateLocked(thread).Priority;
        }

        if (!ctx.TryWriteInt32(outPriorityAddress, priority))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = unchecked((uint)priority);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "W0Hpm2X0uPE",
        ExportName = "scePthreadSetprio",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadSetprio(CpuContext ctx)
    {
        var thread = ctx[CpuRegister.Rdi];
        var priority = unchecked((int)ctx[CpuRegister.Rsi]);
        if (thread == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        lock (_stateGate)
        {
            GetOrCreateThreadStateLocked(thread).Priority = priority;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Xs9hdiD7sAA",
        ExportName = "pthread_setschedparam",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadSetschedparam(CpuContext ctx)
    {
        var thread = ctx[CpuRegister.Rdi];
        var policy = unchecked((int)ctx[CpuRegister.Rsi]);
        var schedParamAddress = ctx[CpuRegister.Rdx];
        if (thread == 0 || schedParamAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!ctx.TryReadInt32(schedParamAddress, out var schedPriority))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        lock (_stateGate)
        {
            var state = GetOrCreateThreadStateLocked(thread);
            state.Priority = schedPriority;
            state.Attributes = state.Attributes with
            {
                SchedPolicy = policy,
                SchedPriority = schedPriority,
            };
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "nsYoNRywwNg",
        ExportName = "scePthreadAttrInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadAttrInit(CpuContext ctx)
    {
        var attrAddress = ctx[CpuRegister.Rdi];
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var syntheticHandle = AllocateSyntheticHandle(SyntheticPthreadAttrHandleBase, ref _nextSyntheticPthreadAttrHandleId);
        if (!ctx.TryWriteUInt64(attrAddress, syntheticHandle))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        lock (_stateGate)
        {
            _attrStates[attrAddress] = PthreadAttrState.Default;
            _attrStates[syntheticHandle] = PthreadAttrState.Default;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "wtkt-teR1so",
        ExportName = "pthread_attr_init",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadAttrInit(CpuContext ctx)
    {
        return PthreadAttrInit(ctx);
    }

    [SysAbiExport(
        Nid = "62KCwEMmzcM",
        ExportName = "scePthreadAttrDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadAttrDestroy(CpuContext ctx)
    {
        var attrAddress = ctx[CpuRegister.Rdi];
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var resolvedAddress = ResolvePthreadAttrHandle(ctx, attrAddress);
        lock (_stateGate)
        {
            _attrStates.Remove(attrAddress);
            if (resolvedAddress != attrAddress)
            {
                _attrStates.Remove(resolvedAddress);
            }
        }

        _ = ctx.TryWriteUInt64(attrAddress, 0);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "zHchY8ft5pk",
        ExportName = "pthread_attr_destroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadAttrDestroy(CpuContext ctx)
    {
        return PthreadAttrDestroy(ctx);
    }

    [SysAbiExport(
        Nid = "x1X76arYMxU",
        ExportName = "scePthreadAttrGet",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadAttrGet(CpuContext ctx)
    {
        var thread = ctx[CpuRegister.Rdi];
        var outAttrAddress = ctx[CpuRegister.Rsi];
        if (thread == 0 || outAttrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        lock (_stateGate)
        {
            var threadState = GetOrCreateThreadStateLocked(thread);
            _attrStates[outAttrAddress] = threadState.Attributes;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "8+s5BzZjxSg",
        ExportName = "scePthreadAttrGetaffinity",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadAttrGetaffinity(CpuContext ctx)
    {
        var attrAddress = ctx[CpuRegister.Rdi];
        var outMaskAddress = ctx[CpuRegister.Rsi];
        if (attrAddress == 0 || outMaskAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        PthreadAttrState state;
        lock (_stateGate)
        {
            state = GetOrCreateAttrStateLocked(attrAddress);
        }

        if (!ctx.TryWriteUInt64(outMaskAddress, state.AffinityMask))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = state.AffinityMask;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "JaRMy+QcpeU",
        ExportName = "scePthreadAttrGetdetachstate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadAttrGetdetachstate(CpuContext ctx)
    {
        var attrAddress = ctx[CpuRegister.Rdi];
        var outStateAddress = ctx[CpuRegister.Rsi];
        if (attrAddress == 0 || outStateAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        PthreadAttrState state;
        lock (_stateGate)
        {
            state = GetOrCreateAttrStateLocked(attrAddress);
        }

        if (!ctx.TryWriteInt32(outStateAddress, state.DetachState))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = unchecked((uint)state.DetachState);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "txHtngJ+eyc",
        ExportName = "scePthreadAttrGetguardsize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadAttrGetguardsize(CpuContext ctx)
    {
        var attrAddress = ctx[CpuRegister.Rdi];
        var outGuardSizeAddress = ctx[CpuRegister.Rsi];
        if (attrAddress == 0 || outGuardSizeAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        PthreadAttrState state;
        lock (_stateGate)
        {
            state = GetOrCreateAttrStateLocked(attrAddress);
        }

        if (!ctx.TryWriteUInt64(outGuardSizeAddress, state.GuardSize))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = state.GuardSize;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Ru36fiTtJzA",
        ExportName = "scePthreadAttrGetstackaddr",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadAttrGetstackaddr(CpuContext ctx)
    {
        var attrAddress = ctx[CpuRegister.Rdi];
        var outStackAddressPointer = ctx[CpuRegister.Rsi];
        if (attrAddress == 0 || outStackAddressPointer == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        PthreadAttrState state;
        lock (_stateGate)
        {
            state = GetOrCreateAttrStateLocked(attrAddress);
        }

        if (!ctx.TryWriteUInt64(outStackAddressPointer, state.StackAddress))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = state.StackAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "-quPa4SEJUw",
        ExportName = "scePthreadAttrGetstack",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadAttrGetstack(CpuContext ctx)
    {
        var attrAddress = ctx[CpuRegister.Rdi];
        var outStackAddressPointer = ctx[CpuRegister.Rsi];
        var outStackSizeAddress = ctx[CpuRegister.Rdx];
        if (attrAddress == 0 || outStackAddressPointer == 0 || outStackSizeAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        PthreadAttrState state;
        lock (_stateGate)
        {
            state = GetOrCreateAttrStateLocked(attrAddress);
        }

        if (!ctx.TryWriteUInt64(outStackAddressPointer, state.StackAddress) ||
            !ctx.TryWriteUInt64(outStackSizeAddress, state.StackSize))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "-fA+7ZlGDQs",
        ExportName = "scePthreadAttrGetstacksize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadAttrGetstacksize(CpuContext ctx)
    {
        var attrAddress = ctx[CpuRegister.Rdi];
        var outStackSizeAddress = ctx[CpuRegister.Rsi];
        if (attrAddress == 0 || outStackSizeAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        PthreadAttrState state;
        lock (_stateGate)
        {
            state = GetOrCreateAttrStateLocked(attrAddress);
        }

        if (!ctx.TryWriteUInt64(outStackSizeAddress, state.StackSize))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = state.StackSize;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "3qxgM4ezETA",
        ExportName = "scePthreadAttrSetaffinity",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadAttrSetaffinity(CpuContext ctx)
    {
        var attrAddress = ctx[CpuRegister.Rdi];
        var mask = ctx[CpuRegister.Rsi];
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        lock (_stateGate)
        {
            var state = GetOrCreateAttrStateLocked(attrAddress);
            _attrStates[attrAddress] = state with { AffinityMask = mask };
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "-Wreprtu0Qs",
        ExportName = "scePthreadAttrSetdetachstate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadAttrSetdetachstate(CpuContext ctx)
    {
        var attrAddress = ctx[CpuRegister.Rdi];
        var detachState = unchecked((int)ctx[CpuRegister.Rsi]);
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        lock (_stateGate)
        {
            var state = GetOrCreateAttrStateLocked(attrAddress);
            _attrStates[attrAddress] = state with { DetachState = detachState };
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "El+cQ20DynU",
        ExportName = "scePthreadAttrSetguardsize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadAttrSetguardsize(CpuContext ctx)
    {
        var attrAddress = ctx[CpuRegister.Rdi];
        var guardSize = ctx[CpuRegister.Rsi];
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        lock (_stateGate)
        {
            var state = GetOrCreateAttrStateLocked(attrAddress);
            _attrStates[attrAddress] = state with { GuardSize = guardSize };
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "eXbUSpEaTsA",
        ExportName = "scePthreadAttrSetinheritsched",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadAttrSetinheritsched(CpuContext ctx)
    {
        var attrAddress = ctx[CpuRegister.Rdi];
        var inheritSched = unchecked((int)ctx[CpuRegister.Rsi]);
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        lock (_stateGate)
        {
            var state = GetOrCreateAttrStateLocked(attrAddress);
            _attrStates[attrAddress] = state with { InheritSched = inheritSched };
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "FXPWHNk8Of0",
        ExportName = "scePthreadAttrGetschedparam",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadAttrGetschedparam(CpuContext ctx)
    {
        var attrAddress = ctx[CpuRegister.Rdi];
        var schedParamAddress = ctx[CpuRegister.Rsi];
        if (attrAddress == 0 || schedParamAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        PthreadAttrState state;
        lock (_stateGate)
        {
            state = GetOrCreateAttrStateLocked(attrAddress);
        }

        if (!ctx.TryWriteInt32(schedParamAddress, state.SchedPriority))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "DzES9hQF4f4",
        ExportName = "scePthreadAttrSetschedparam",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadAttrSetschedparam(CpuContext ctx)
    {
        var attrAddress = ctx[CpuRegister.Rdi];
        var schedParamAddress = ctx[CpuRegister.Rsi];
        if (attrAddress == 0 || schedParamAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!ctx.TryReadInt32(schedParamAddress, out var schedPriority))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        lock (_stateGate)
        {
            var state = GetOrCreateAttrStateLocked(attrAddress);
            _attrStates[attrAddress] = state with { SchedPriority = schedPriority };
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "4+h9EzwKF4I",
        ExportName = "scePthreadAttrSetschedpolicy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadAttrSetschedpolicy(CpuContext ctx)
    {
        var attrAddress = ctx[CpuRegister.Rdi];
        var policy = unchecked((int)ctx[CpuRegister.Rsi]);
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        lock (_stateGate)
        {
            var state = GetOrCreateAttrStateLocked(attrAddress);
            _attrStates[attrAddress] = state with { SchedPolicy = policy };
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "UTXzJbWhhTE",
        ExportName = "scePthreadAttrSetstacksize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadAttrSetstacksize(CpuContext ctx)
    {
        var attrAddress = ctx[CpuRegister.Rdi];
        var stackSize = ctx[CpuRegister.Rsi];
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var resolvedAddress = ResolvePthreadAttrHandle(ctx, attrAddress);
        lock (_stateGate)
        {
            var state = GetOrCreateAttrStateLocked(resolvedAddress);
            var updated = state with { StackSize = stackSize };
            _attrStates[resolvedAddress] = updated;
            if (resolvedAddress != attrAddress)
            {
                _attrStates[attrAddress] = updated;
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "2Q0z6rnBrTE",
        ExportName = "pthread_attr_setstacksize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadAttrSetstacksize(CpuContext ctx)
    {
        return PthreadAttrSetstacksize(ctx);
    }

    [SysAbiExport(
        Nid = "Bvn74vj6oLo",
        ExportName = "scePthreadAttrSetstack",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadAttrSetstack(CpuContext ctx)
    {
        var attrAddress = ctx[CpuRegister.Rdi];
        var stackAddress = ctx[CpuRegister.Rsi];
        var stackSize = ctx[CpuRegister.Rdx];
        if (attrAddress == 0 || stackAddress == 0 || stackSize == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var resolvedAddress = ResolvePthreadAttrHandle(ctx, attrAddress);
        lock (_stateGate)
        {
            var state = GetOrCreateAttrStateLocked(resolvedAddress);
            var updated = state with { StackAddress = stackAddress, StackSize = stackSize };
            _attrStates[resolvedAddress] = updated;
            if (resolvedAddress != attrAddress)
            {
                _attrStates[attrAddress] = updated;
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "6ULAa0fq4jA",
        ExportName = "scePthreadRwlockInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadRwlockInit(CpuContext ctx)
    {
        var rwlockAddress = ctx[CpuRegister.Rdi];
        if (rwlockAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var syntheticHandle = AllocateSyntheticHandle(SyntheticRwlockHandleBase, ref _nextSyntheticRwlockHandleId);
        lock (_stateGate)
        {
            var resolvedAddress = ResolveRwlockHandle(ctx, rwlockAddress);
            if (_rwlockStates.Remove(resolvedAddress, out var existing))
            {
            }

            var rwlock = new PthreadRwlockState();
            _rwlockStates[rwlockAddress] = rwlock;
            _rwlockStates[syntheticHandle] = rwlock;
        }

        _ = KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, rwlockAddress, syntheticHandle);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "ytQULN-nhL4",
        ExportName = "pthread_rwlock_init",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadRwlockInit(CpuContext ctx) => PthreadRwlockInit(ctx);

    [SysAbiExport(
        Nid = "BB+kb08Tl9A",
        ExportName = "scePthreadRwlockDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadRwlockDestroy(CpuContext ctx)
    {
        var rwlockAddress = ctx[CpuRegister.Rdi];
        if (rwlockAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var resolvedAddress = ResolveRwlockHandle(ctx, rwlockAddress);
        PthreadRwlockState? state;
        lock (_stateGate)
        {
            _rwlockStates.TryGetValue(resolvedAddress, out state);
        }

        if (state is null)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        lock (state.SyncRoot)
        {
            if (state.WriterThreadId != 0 || state.ReaderTotalCount != 0 || state.WaitingWriters != 0)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
            }
        }

        lock (_stateGate)
        {
            _rwlockStates.Remove(resolvedAddress);
            if (resolvedAddress != rwlockAddress)
            {
                _rwlockStates.Remove(rwlockAddress);
            }
        }

        _ = KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, rwlockAddress, 0);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "1471ajPzxh0",
        ExportName = "pthread_rwlock_destroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadRwlockDestroy(CpuContext ctx) => PthreadRwlockDestroy(ctx);

    [SysAbiExport(
        Nid = "Ox9i0c7L5w0",
        ExportName = "scePthreadRwlockRdlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadRwlockRdlock(CpuContext ctx) => PthreadRwlockLockCore(ctx, ctx[CpuRegister.Rdi], write: false);

    [SysAbiExport(
        Nid = "iGjsr1WAtI0",
        ExportName = "pthread_rwlock_rdlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadRwlockRdlock(CpuContext ctx) => PthreadRwlockRdlock(ctx);

    [SysAbiExport(
        Nid = "mqdNorrB+gI",
        ExportName = "scePthreadRwlockWrlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadRwlockWrlock(CpuContext ctx) => PthreadRwlockLockCore(ctx, ctx[CpuRegister.Rdi], write: true);

    [SysAbiExport(
        Nid = "sIlRvQqsN2Y",
        ExportName = "pthread_rwlock_wrlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadRwlockWrlock(CpuContext ctx) => PthreadRwlockWrlock(ctx);

    [SysAbiExport(
        Nid = "+L98PIbGttk",
        ExportName = "scePthreadRwlockUnlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadRwlockUnlock(CpuContext ctx)
    {
        var rwlockAddress = ctx[CpuRegister.Rdi];
        if (rwlockAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryResolveRwlockState(ctx, rwlockAddress, createIfZero: false, out _, out var rwlock))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        var currentThreadId = KernelPthreadState.GetCurrentThreadHandle();

        try
        {
            lock (rwlock.SyncRoot)
            {
                if (rwlock.WriterThreadId == currentThreadId)
                {
                    rwlock.WriterThreadId = 0;
                    Monitor.PulseAll(rwlock.SyncRoot);
                }
                else if (rwlock.RemoveReader(currentThreadId))
                {
                    if (rwlock.ReaderTotalCount == 0 || rwlock.WaitingWriters > 0)
                    {
                        Monitor.PulseAll(rwlock.SyncRoot);
                    }
                }
                else
                {
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
                }
            }
        }
        catch (SynchronizationLockException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "EgmLo6EWgso",
        ExportName = "pthread_rwlock_unlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadRwlockUnlock(CpuContext ctx) => PthreadRwlockUnlock(ctx);

    [SysAbiExport(
        Nid = "yOfGg-I1ZII",
        ExportName = "scePthreadRwlockattrInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadRwlockattrInit(CpuContext ctx)
    {
        var attrAddress = ctx[CpuRegister.Rdi];
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var syntheticHandle = AllocateSyntheticHandle(SyntheticRwlockAttrHandleBase, ref _nextSyntheticRwlockAttrHandleId);
        if (!KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, attrAddress, syntheticHandle))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "i2ifZ3fS2fo",
        ExportName = "scePthreadRwlockattrDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadRwlockattrDestroy(CpuContext ctx)
    {
        var attrAddress = ctx[CpuRegister.Rdi];
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        _ = KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, attrAddress, 0);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "mqULNdimTn0",
        ExportName = "pthread_key_create",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadKeyCreate(CpuContext ctx)
    {
        var outKeyAddress = ctx[CpuRegister.Rdi];
        var destructor = ctx[CpuRegister.Rsi];
        if (outKeyAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        int key;
        while (true)
        {
            key = Interlocked.Increment(ref _nextTlsKey) - 1;
            if (_tlsKeys.TryAdd(key, new TlsKeyState(destructor)))
            {
                break;
            }
        }

        if (!ctx.TryWriteInt32(outKeyAddress, key))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "geDaqgH9lTg",
        ExportName = "scePthreadKeyCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int OrbisPthreadKeyCreate(CpuContext ctx) => PosixPthreadKeyCreate(ctx);

    [SysAbiExport(
        Nid = "6BpEZuDT7YI",
        ExportName = "pthread_key_delete",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadKeyDelete(CpuContext ctx)
    {
        var key = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_tlsKeys.TryRemove(key, out _))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        foreach (var values in _threadLocalSpecific.Values)
        {
            values.TryRemove(key, out _);
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "PrdHuuDekhY",
        ExportName = "scePthreadKeyDelete",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int OrbisPthreadKeyDelete(CpuContext ctx) => PosixPthreadKeyDelete(ctx);

    [SysAbiExport(
        Nid = "WrOLvHU0yQM",
        ExportName = "pthread_setspecific",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadSetspecific(CpuContext ctx)
    {
        var key = unchecked((int)ctx[CpuRegister.Rdi]);
        var value = ctx[CpuRegister.Rsi];
        var currentThreadHandle = KernelPthreadState.GetCurrentThreadHandle();
        if (!_tlsKeys.ContainsKey(key))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        var values = _threadLocalSpecific.GetOrAdd(
            currentThreadHandle,
            static _ => new ConcurrentDictionary<int, ulong>());
        values[key] = value;
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "+BzXYkqYeLE",
        ExportName = "scePthreadSetspecific",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int OrbisPthreadSetspecific(CpuContext ctx) => PosixPthreadSetspecific(ctx);

    [SysAbiExport(
        Nid = "0-KXaS70xy4",
        ExportName = "pthread_getspecific",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadGetspecific(CpuContext ctx)
    {
        var key = unchecked((int)ctx[CpuRegister.Rdi]);
        var currentThreadHandle = KernelPthreadState.GetCurrentThreadHandle();
        ulong value = 0;
        if (!_tlsKeys.ContainsKey(key))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (_threadLocalSpecific.TryGetValue(currentThreadHandle, out var values) &&
            values.TryGetValue(key, out var storedValue))
        {
            value = storedValue;
        }

        ctx[CpuRegister.Rax] = value;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "eoht7mQOCmo",
        ExportName = "scePthreadGetspecific",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int OrbisPthreadGetspecific(CpuContext ctx) => PosixPthreadGetspecific(ctx);

    private static int PthreadRwlockLockCore(CpuContext ctx, ulong rwlockAddress, bool write)
    {
        if (rwlockAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryResolveRwlockState(ctx, rwlockAddress, createIfZero: true, out _, out var rwlock))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        var currentThreadId = KernelPthreadState.GetCurrentThreadHandle();
        lock (rwlock.SyncRoot)
        {
            if (write)
            {
                if (rwlock.WriterThreadId == currentThreadId || rwlock.GetReaderCount(currentThreadId) > 0)
                {
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DEADLOCK;
                }

                rwlock.WaitingWriters++;
                try
                {
                    while (rwlock.WriterThreadId != 0 || rwlock.ReaderTotalCount != 0)
                    {
                        Monitor.Wait(rwlock.SyncRoot);
                    }
                }
                finally
                {
                    rwlock.WaitingWriters--;
                }

                rwlock.WriterThreadId = currentThreadId;
            }
            else
            {
                if (rwlock.WriterThreadId == currentThreadId)
                {
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DEADLOCK;
                }

                while (rwlock.WriterThreadId != 0 ||
                       (rwlock.WaitingWriters > 0 && rwlock.GetReaderCount(currentThreadId) == 0))
                {
                    Monitor.Wait(rwlock.SyncRoot);
                }

                rwlock.AddReader(currentThreadId);
            }
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static ulong ResolveRwlockHandle(CpuContext ctx, ulong rwlockAddress)
    {
        if (rwlockAddress == 0)
        {
            return 0;
        }

        lock (_stateGate)
        {
            if (_rwlockStates.ContainsKey(rwlockAddress))
            {
                return rwlockAddress;
            }
        }

        if (KernelMemoryCompatExports.TryReadUInt64Compat(ctx, rwlockAddress, out var pointedHandle) && pointedHandle != 0)
        {
            lock (_stateGate)
            {
                if (_rwlockStates.ContainsKey(pointedHandle))
                {
                    return pointedHandle;
                }
            }
        }

        return rwlockAddress;
    }

    private static bool TryResolveRwlockState(CpuContext ctx, ulong rwlockAddress, bool createIfZero, out ulong resolvedAddress, [NotNullWhen(true)] out PthreadRwlockState? rwlock)
    {
        resolvedAddress = 0;
        rwlock = null;
        if (rwlockAddress == 0)
        {
            return false;
        }

        lock (_stateGate)
        {
            if (_rwlockStates.TryGetValue(rwlockAddress, out rwlock))
            {
                resolvedAddress = rwlockAddress;
                return true;
            }
        }

        if (!KernelMemoryCompatExports.TryReadUInt64Compat(ctx, rwlockAddress, out var pointedHandle))
        {
            return false;
        }

        if (pointedHandle != 0)
        {
            lock (_stateGate)
            {
                if (_rwlockStates.TryGetValue(pointedHandle, out rwlock))
                {
                    _rwlockStates[rwlockAddress] = rwlock;
                    resolvedAddress = pointedHandle;
                    return true;
                }
            }

            resolvedAddress = pointedHandle;
            return false;
        }

        if (!createIfZero)
        {
            resolvedAddress = rwlockAddress;
            return false;
        }

        var createdRwlock = new PthreadRwlockState();
        var syntheticHandle = AllocateSyntheticHandle(SyntheticRwlockHandleBase, ref _nextSyntheticRwlockHandleId);
        lock (_stateGate)
        {
            _rwlockStates[rwlockAddress] = createdRwlock;
            _rwlockStates[syntheticHandle] = createdRwlock;
        }

        _ = KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, rwlockAddress, syntheticHandle);
        resolvedAddress = syntheticHandle;
        rwlock = createdRwlock;
        return true;
    }

    private static ulong AllocateSyntheticHandle(ulong baseAddress, ref long nextId)
    {
        var id = unchecked((ulong)Interlocked.Increment(ref nextId));
        return baseAddress + (id << 4);
    }

    private static ThreadState GetOrCreateThreadStateLocked(ulong thread)
    {
        if (_threadStates.TryGetValue(thread, out var state))
        {
            return state;
        }

        var name = KernelPthreadState.TryGetThreadIdentity(thread, out var identity)
            ? identity.Name
            : $"Thread-{thread:X}";

        state = new ThreadState
        {
            Name = name,
            Priority = DefaultThreadPriority,
            AffinityMask = DefaultThreadAffinityMask,
            DetachState = DefaultDetachState,
            Attributes = PthreadAttrState.Default,
        };
        _threadStates[thread] = state;
        return state;
    }

    private static PthreadAttrState GetOrCreateAttrStateLocked(ulong attrAddress)
    {
        if (_attrStates.TryGetValue(attrAddress, out var state))
        {
            return state;
        }

        state = PthreadAttrState.Default;
        _attrStates[attrAddress] = state;
        return state;
    }

    private static ulong ResolvePthreadAttrHandle(CpuContext ctx, ulong attrAddress)
    {
        if (attrAddress == 0)
        {
            return 0;
        }

        lock (_stateGate)
        {
            if (_attrStates.ContainsKey(attrAddress))
            {
                return attrAddress;
            }
        }

        if (ctx.TryReadUInt64(attrAddress, out var pointedHandle) && pointedHandle != 0)
        {
            lock (_stateGate)
            {
                if (_attrStates.ContainsKey(pointedHandle))
                {
                    return pointedHandle;
                }
            }
        }

        return attrAddress;
    }

    private static bool TryWriteFixedUtf8CString(CpuContext ctx, ulong address, string value, int maxBytes)
    {
        if (maxBytes <= 0)
        {
            return false;
        }

        var utf8 = Encoding.UTF8.GetBytes(value);
        var payloadLength = Math.Min(utf8.Length, maxBytes - 1);
        var payload = new byte[payloadLength + 1];
        utf8.AsSpan(0, payloadLength).CopyTo(payload);
        payload[^1] = 0;
        return ctx.Memory.TryWrite(address, payload);
    }
}
