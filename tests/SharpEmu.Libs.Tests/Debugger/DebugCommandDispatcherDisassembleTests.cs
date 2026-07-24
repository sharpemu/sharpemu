// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Debugging;
using SharpEmu.Debugger;
using SharpEmu.Debugger.Breakpoints;
using SharpEmu.Debugger.Protocol;
using SharpEmu.Debugger.Session;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Debugger;

public sealed class DebugCommandDispatcherDisassembleTests
{
    // nop; ret; mov eax, 1 — a small, unambiguous mix of a 1-byte, another
    // 1-byte, and a 5-byte instruction so length and cursor advancement are
    // both exercised.
    private static readonly byte[] SampleCode = [0x90, 0xC3, 0xB8, 0x01, 0x00, 0x00, 0x00];

    [Fact]
    public void Disassemble_DecodesRequestedInstructionCount()
    {
        var dispatcher = new DebugCommandDispatcher(new FakeSession(0x1000, SampleCode));
        var request = Parse("""{"command":"disassemble","address":"0x1000","count":3}""");

        var response = dispatcher.Dispatch(request);

        Assert.True(response.Ok);
        var instructions = Assert.IsAssignableFrom<IReadOnlyList<Dictionary<string, object?>>>(
            response.Data!["instructions"]);
        Assert.Equal(3, instructions.Count);
        Assert.Equal("0x0000000000001000", instructions[0]["address"]);
        Assert.Equal(1, instructions[0]["length"]);
        Assert.Equal("0x0000000000001001", instructions[1]["address"]);
        Assert.Equal(1, instructions[1]["length"]);
        Assert.Equal("0x0000000000001002", instructions[2]["address"]);
        Assert.Equal(5, instructions[2]["length"]);
        Assert.Equal("B801000000", instructions[2]["bytes"]);
    }

    [Fact]
    public void Disassemble_StopsEarlyWhenMemoryRunsOut()
    {
        // Only SampleCode.Length bytes are readable; the default count (10) asks
        // for more than that, so the run should stop cleanly instead of failing.
        var dispatcher = new DebugCommandDispatcher(new FakeSession(0x2000, SampleCode));
        var request = Parse("""{"command":"disassemble","address":"0x2000"}""");

        var response = dispatcher.Dispatch(request);

        Assert.True(response.Ok);
        var instructions = Assert.IsAssignableFrom<IReadOnlyList<Dictionary<string, object?>>>(
            response.Data!["instructions"]);
        Assert.Equal(3, instructions.Count);
    }

    [Fact]
    public void Disassemble_FailsWhenTargetIsNotPaused()
    {
        var dispatcher = new DebugCommandDispatcher(new FakeSession(0x1000, code: null));
        var request = Parse("""{"command":"disassemble","address":"0x1000"}""");

        var response = dispatcher.Dispatch(request);

        Assert.False(response.Ok);
        Assert.Equal("Memory is unreadable or target is not paused.", response.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(513)]
    public void Disassemble_RejectsOutOfRangeCount(int count)
    {
        var dispatcher = new DebugCommandDispatcher(new FakeSession(0x1000, SampleCode));
        var request = Parse($$"""{"command":"disassemble","address":"0x1000","count":{{count}}}""");

        var response = dispatcher.Dispatch(request);

        Assert.False(response.Ok);
    }

    private static DebugRequest Parse(string json)
    {
        Assert.True(DebugRequest.TryParse(json, out var request, out var error), error);
        return request;
    }

    /// <summary>Minimal <see cref="IDebuggerSession"/> serving fixed bytes from a base address.</summary>
    private sealed class FakeSession(ulong baseAddress, byte[]? code) : IDebuggerSession, ICpuDebugHook
    {
        public DebuggerRunState State => DebuggerRunState.Paused;

        public DebugStopEvent? LastStop => null;

        public BreakpointStore Breakpoints { get; } = new();

        public ICpuDebugHook Hook => this;

        public event EventHandler<DebugStopEvent>? Stopped { add { } remove { } }

        public event EventHandler? Resumed { add { } remove { } }

        public event EventHandler? Terminated { add { } remove { } }

        public bool TryGetRegisters(out DebugRegisterFile registers)
        {
            registers = default;
            return false;
        }

        public bool TrySetRegister(DebugRegisterId id, ulong value) => false;

        public bool TryReadMemory(ulong address, Span<byte> destination)
        {
            if (code is null || address < baseAddress)
            {
                return false;
            }

            var offset = address - baseAddress;
            if (offset + (ulong)destination.Length > (ulong)code.Length)
            {
                return false;
            }

            code.AsSpan((int)offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWriteMemory(ulong address, ReadOnlySpan<byte> source) => false;

        public bool TryReadXmm(int registerIndex, out ulong low, out ulong high)
        {
            low = 0;
            high = 0;
            return false;
        }

        public bool Continue() => false;

        public bool StepFrame() => false;

        public void RequestPause()
        {
        }

        public void NotifyTerminated()
        {
        }

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
