// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

// SyncOnAddress waits only if the guest value equals the expected value.
// Wake selects a fixed number of registered waiters at the same address.
public static class KernelSyncOnAddressCompatExports
{
    private sealed class SyncWaiter : IGuestThreadBlockWaiter
    {
        private readonly CpuContext _context;
        private readonly ulong _expected;
        private readonly bool _is64Bit;
        private readonly object _hostGate = new();
        private int _wakeRequested;

        public SyncWaiter(CpuContext context, ulong address, ulong expected, bool is64Bit)
        {
            _context = context;
            Address = address;
            _expected = expected;
            _is64Bit = is64Bit;
            Id = Interlocked.Increment(ref _nextWaiterId);
        }

        public ulong Address { get; }

        public long Id { get; }

        public LinkedListNode<SyncWaiter>? RegistryNode { get; set; }

        public string WakeKey => $"sceKernelSyncOnAddress:{Address:X16}:{Id:X16}";

        public int Resume()
        {
            Unregister(this);
            if (Volatile.Read(ref _wakeRequested) != 0)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            if (!TryReadValue(_context, Address, _is64Bit, out var value))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            return value != _expected
                ? (int)OrbisGen2Result.ORBIS_GEN2_OK
                : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT;
        }

        public bool TryWake() => Volatile.Read(ref _wakeRequested) != 0;

        public bool RequestWake()
        {
            if (Interlocked.Exchange(ref _wakeRequested, 1) != 0)
            {
                return false;
            }

            lock (_hostGate)
            {
                Monitor.Pulse(_hostGate);
            }

            return true;
        }

        public int WaitOnHost(TimeSpan? timeout)
        {
            if (Volatile.Read(ref _wakeRequested) == 0)
            {
                lock (_hostGate)
                {
                    if (timeout is null)
                    {
                        while (Volatile.Read(ref _wakeRequested) == 0)
                        {
                            Monitor.Wait(_hostGate);
                        }
                    }
                    else
                    {
                        var deadline = Stopwatch.GetTimestamp() +
                            (long)Math.Ceiling(timeout.Value.TotalSeconds * Stopwatch.Frequency);
                        while (Volatile.Read(ref _wakeRequested) == 0)
                        {
                            var remainingTicks = deadline - Stopwatch.GetTimestamp();
                            if (remainingTicks <= 0)
                            {
                                break;
                            }

                            var remaining = TimeSpan.FromSeconds(
                                remainingTicks / (double)Stopwatch.Frequency);
                            Monitor.Wait(_hostGate, remaining);
                        }
                    }
                }
            }

            return Resume();
        }
    }

    private static readonly object _registryGate = new();
    private static readonly Dictionary<ulong, LinkedList<SyncWaiter>> _waitersByAddress = new();
    private static long _nextWaiterId;

    [SysAbiExport(
        Nid = "Hc4CaR6JBL0",
        ExportName = "sceKernelSyncOnAddressWait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWait(CpuContext ctx) => Wait(ctx, is64Bit: false);

    [SysAbiExport(
        Nid = "B2n8aDorSH4",
        ExportName = "sceKernelSyncOnAddressWait32",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWait32(CpuContext ctx) => Wait(ctx, is64Bit: false);

    [SysAbiExport(
        Nid = "PZQhiiLXRFs",
        ExportName = "sceKernelSyncOnAddressWait64",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWait64(CpuContext ctx) => Wait(ctx, is64Bit: true);

    [SysAbiExport(
        Nid = "q2y-wDIVWZA",
        ExportName = "sceKernelSyncOnAddressWake",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWake(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var requested = unchecked((int)ctx[CpuRegister.Rsi]);
        if (address == 0 || (address & 3) != 0 || requested < 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (requested == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }

        List<SyncWaiter>? selected = null;
        lock (_registryGate)
        {
            if (_waitersByAddress.TryGetValue(address, out var waiters))
            {
                selected = new List<SyncWaiter>(Math.Min(requested, waiters.Count));
                var node = waiters.First;
                while (node is not null && selected.Count < requested)
                {
                    var next = node.Next;
                    var waiter = node.Value;
                    waiters.Remove(node);
                    waiter.RegistryNode = null;
                    if (waiter.RequestWake())
                    {
                        selected.Add(waiter);
                    }

                    node = next;
                }

                if (waiters.Count == 0)
                {
                    _waitersByAddress.Remove(address);
                }
            }
        }

        if (selected is not null)
        {
            foreach (var waiter in selected)
            {
                _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(waiter.WakeKey, 1);
            }
        }

        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static int Wait(CpuContext ctx, bool is64Bit)
    {
        var address = ctx[CpuRegister.Rdi];
        var alignmentMask = is64Bit ? 7UL : 3UL;
        if (address == 0 || (address & alignmentMask) != 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var expected = is64Bit
            ? ctx[CpuRegister.Rsi]
            : unchecked((uint)ctx[CpuRegister.Rsi]);
        if (!TryReadValue(ctx, address, is64Bit, out var current))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (current != expected)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }

        var timeoutAddress = ctx[CpuRegister.Rdx];
        TimeSpan? timeout = null;
        long deadline = 0;
        if (timeoutAddress != 0)
        {
            if (!ctx.TryReadUInt32(timeoutAddress, out var timeoutMicroseconds))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (timeoutMicroseconds == 0)
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT);
            }

            timeout = TimeSpan.FromTicks((long)timeoutMicroseconds * 10L);
            deadline = GuestThreadExecution.ComputeDeadlineTimestamp(timeout.Value);
        }

        var waiter = new SyncWaiter(ctx, address, expected, is64Bit);
        Register(address, waiter);
        if (!TryReadValue(ctx, address, is64Bit, out current))
        {
            Unregister(waiter);
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (current != expected || waiter.TryWake())
        {
            Unregister(waiter);
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }

        if (GuestThreadExecution.RequestCurrentThreadBlock(
                ctx,
                is64Bit ? "sceKernelSyncOnAddressWait64" : "sceKernelSyncOnAddressWait32",
                waiter.WakeKey,
                waiter,
                deadline))
        {
            // A wake can select this waiter before the scheduler parks it.
            // The scheduler checks TryWake after it registers the continuation.
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }

        return SetReturn(ctx, waiter.WaitOnHost(timeout));
    }

    private static void Register(ulong address, SyncWaiter waiter)
    {
        lock (_registryGate)
        {
            if (!_waitersByAddress.TryGetValue(address, out var waiters))
            {
                waiters = new LinkedList<SyncWaiter>();
                _waitersByAddress.Add(address, waiters);
            }

            waiter.RegistryNode = waiters.AddLast(waiter);
        }
    }

    private static void Unregister(SyncWaiter waiter)
    {
        lock (_registryGate)
        {
            var node = waiter.RegistryNode;
            if (node?.List is not { } waiters)
            {
                return;
            }

            waiters.Remove(node);
            waiter.RegistryNode = null;
            if (waiters.Count == 0)
            {
                _waitersByAddress.Remove(waiter.Address);
            }
        }
    }

    private static bool TryReadValue(
        CpuContext ctx,
        ulong address,
        bool is64Bit,
        out ulong value)
    {
        if (is64Bit)
        {
            return ctx.TryReadUInt64(address, out value);
        }

        if (ctx.TryReadUInt32(address, out var value32))
        {
            value = value32;
            return true;
        }

        value = 0;
        return false;
    }

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result) =>
        SetReturn(ctx, (int)result);

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)(long)result);
        return result;
    }
}
