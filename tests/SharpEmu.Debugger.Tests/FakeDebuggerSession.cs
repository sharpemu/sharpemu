// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Debugging;
using SharpEmu.Debugger;
using SharpEmu.Debugger.Breakpoints;
using SharpEmu.Debugger.Session;
using SharpEmu.HLE;

namespace SharpEmu.Debugger.Tests;

/// <summary>
/// In-memory session for dispatcher tests. Tracks run state and an optional
/// flat guest memory region without parking a real emulation thread.
/// </summary>
internal sealed class FakeDebuggerSession : IDebuggerSession
{
    private readonly Dictionary<ulong, byte> _memory = new();
    private DebugRegisterFile _registers = new(
        new ulong[16],
        rip: 0,
        rflags: 0,
        fsBase: 0,
        gsBase: 0);

    public FakeDebuggerSession(DebuggerRunState state = DebuggerRunState.Paused)
    {
        State = state;
        Breakpoints = new BreakpointStore();
        Hook = NullCpuDebugHook.Instance;
    }

    public BreakpointStore Breakpoints { get; }

    public ICpuDebugHook Hook { get; }

    public DebuggerRunState State { get; set; }

    public DebugStopEvent? LastStop { get; set; }

    public bool PauseRequested { get; private set; }

    public int ContinueCallCount { get; private set; }

    public int StepFrameCallCount { get; private set; }

    public event EventHandler<DebugStopEvent>? Stopped
    {
        add { }
        remove { }
    }

    public event EventHandler? Resumed
    {
        add { }
        remove { }
    }

    public event EventHandler? Terminated
    {
        add { }
        remove { }
    }

    public void SetRegisters(DebugRegisterFile registers) => _registers = registers;

    public void SeedMemory(ulong address, ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            _memory[address + (ulong)i] = bytes[i];
        }
    }

    public bool TryGetRegisters(out DebugRegisterFile registers)
    {
        if (State != DebuggerRunState.Paused)
        {
            registers = default;
            return false;
        }

        registers = _registers;
        return true;
    }

    public bool TrySetRegister(DebugRegisterId id, ulong value)
    {
        if (State != DebuggerRunState.Paused)
        {
            return false;
        }

        var gpr = new ulong[16];
        for (var i = 0; i < 16; i++)
        {
            gpr[i] = _registers[(CpuRegister)i];
        }

        var rip = _registers.Rip;
        var rflags = _registers.Rflags;
        var fsBase = _registers.FsBase;
        var gsBase = _registers.GsBase;

        switch (id)
        {
            case DebugRegisterId.Rip:
                rip = value;
                break;
            case DebugRegisterId.Rflags:
                rflags = value;
                break;
            case DebugRegisterId.FsBase:
                fsBase = value;
                break;
            case DebugRegisterId.GsBase:
                gsBase = value;
                break;
            default:
                if (!id.IsGeneralPurpose())
                {
                    return false;
                }

                gpr[(int)id] = value;
                break;
        }

        _registers = new DebugRegisterFile(gpr, rip, rflags, fsBase, gsBase);
        return true;
    }

    public bool TryReadMemory(ulong address, Span<byte> destination)
    {
        if (State != DebuggerRunState.Paused)
        {
            return false;
        }

        for (var i = 0; i < destination.Length; i++)
        {
            if (!_memory.TryGetValue(address + (ulong)i, out var value))
            {
                return false;
            }

            destination[i] = value;
        }

        return true;
    }

    public bool TryWriteMemory(ulong address, ReadOnlySpan<byte> source)
    {
        if (State != DebuggerRunState.Paused)
        {
            return false;
        }

        SeedMemory(address, source);
        return true;
    }

    public bool TryReadXmm(int registerIndex, out ulong low, out ulong high)
    {
        low = 0;
        high = 0;
        return State == DebuggerRunState.Paused;
    }

    public bool Continue()
    {
        ContinueCallCount++;
        if (State != DebuggerRunState.Paused)
        {
            return false;
        }

        State = DebuggerRunState.Running;
        return true;
    }

    public bool StepFrame()
    {
        StepFrameCallCount++;
        if (State != DebuggerRunState.Paused)
        {
            return false;
        }

        State = DebuggerRunState.Running;
        return true;
    }

    public void RequestPause() => PauseRequested = true;

    public void NotifyTerminated() => State = DebuggerRunState.Terminated;

    private sealed class NullCpuDebugHook : ICpuDebugHook
    {
        public static readonly NullCpuDebugHook Instance = new();

        public void OnFrameEnter(ICpuDebugFrame frame)
        {
        }

        public void OnFrameExit(ICpuDebugFrame frame, OrbisGen2Result result)
        {
        }

        public void OnStall(ICpuDebugFrame frame, CpuStallInfo info)
        {
        }
    }
}
