// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

// The interpreter's side of a deferred dispatch run, with an engine that issues every dispatch and can roll the run back
// once at its barrier.
public sealed class DeferredDispatchRunTests
{
    private const uint ComputeUserData0 = 0x240;
    private const uint PixelProgramLow = 0x008;
    private const uint CacheFlushEvent = 0x07;

    private sealed class FakeRuns(GpuCommandInterpreter interpreter) : IDeferredDispatchRuns
    {
        private bool _open;
        private bool _suppressed;

        public List<(DeferredIssueSite Site, ComputeStageRegisters Compute)> Issued { get; } = new();

        public int RewindTo { get; set; } = -1;

        public int Barriers { get; private set; }

        public int Tables { get; private set; }

        public bool IsOpen => _open;

        public bool Active => _open || _suppressed;

        public bool StopsTicketScan => false;

        public bool WouldDefer(ComputeStageRegisters compute, uint initiator) => false;

        public DeferredIssue TryIssue(in DeferredIssueSite site, uint groupsX, uint groupsY, uint groupsZ, uint initiator)
        {
            if (_suppressed)
            {
                return DeferredIssue.NotEligible;
            }

            Issued.Add((site, interpreter.TypedRegisters.Shader.Compute.Copy()));
            _open = true;
            return DeferredIssue.Issued;
        }

        public DeferredRewind? Barrier()
        {
            Barriers++;
            if (!_open)
            {
                _suppressed = false;
                return null;
            }

            _open = false;
            if (RewindTo < 1)
            {
                return null;
            }

            // A poisoned table of member RewindTo: the state at the issue of the member before it.
            var (site, compute) = Issued[RewindTo - 1];
            RewindTo = -1;
            _suppressed = true;
            return new DeferredRewind
            {
                Depth = site.Depth,
                CursorAddress = site.CursorAddress,
                Offset = site.PacketOffset + 5,
                Compute = compute,
                RawUndoMark = site.RawUndoMark,
                UserDataMarker = site.UserDataMarker,
            };
        }

        public bool NoteTable(ulong address, ulong size)
        {
            Tables++;
            return true;
        }

        public void BreakChain()
        {
        }
    }

    private static uint[] UserData(uint value) => StreamRunner.Packet(PacketOpcode.SetShaderRegister, ComputeUserData0, value);

    private static uint[] Dispatch(uint groups) => StreamRunner.Packet(PacketOpcode.DispatchDirect, groups, 1, 1, 0x41);

    [Fact]
    public void RollbackRunsThePacketsAgainFromThePoisonedMemberWithItsRegisters()
    {
        var runner = new StreamRunner();
        var runs = new FakeRuns(runner.Interpreter) { RewindTo = 1 };
        runner.Host.DispatchRuns = runs;

        runner.Run(
            UserData(0xA), Dispatch(1),
            UserData(0xB), Dispatch(2),
            UserData(0xC), Dispatch(3),
            StreamRunner.Packet(PacketOpcode.SetShaderRegister, PixelProgramLow, 0x1234));

        // The three members went to the engine; the barrier at the pixel register rolled the run back to member 1, whose
        // packets ran again on the stream with the registers member 0 was issued under.
        Assert.Equal(3, runs.Issued.Count);
        Assert.Equal(new[] { 0xAu, 0xBu, 0xCu }, runs.Issued.Select(static entry => entry.Compute.UserScalars.ValueSpan(1)[0]));
        Assert.Equal(new[] { "dispatch 0 2 1 1 41", "dispatch 0 3 1 1 41" }, runner.Host.Calls);
        Assert.Equal(0xCu, runner.Interpreter.Registers.Shader[ComputeUserData0]);
        Assert.Equal(0xCu, runner.Interpreter.TypedRegisters.Shader.Compute.UserScalars.ValueSpan(1)[0]);
        Assert.Equal(0x1234u, runner.Interpreter.Registers.Shader[PixelProgramLow]);
        Assert.False(runs.Active);
    }

    [Fact]
    public void RunCloseAtTheBufferEndKeepsEveryMember()
    {
        var runner = new StreamRunner();
        var runs = new FakeRuns(runner.Interpreter);
        runner.Host.DispatchRuns = runs;

        runner.Run(UserData(0xA), Dispatch(1), UserData(0xB), Dispatch(2));

        Assert.Equal(2, runs.Issued.Count);
        Assert.Empty(runner.Host.Calls);
        Assert.Equal(1, runs.Barriers);
        Assert.False(runs.Active);
        Assert.Equal(0xBu, runner.Interpreter.Registers.Shader[ComputeUserData0]);
    }

    [Fact]
    public void ACacheFlushEventStaysInsideTheRun()
    {
        var runner = new StreamRunner();
        var runs = new FakeRuns(runner.Interpreter);
        runner.Host.DispatchRuns = runs;

        runner.Run(UserData(0xA), Dispatch(1), StreamRunner.Packet(PacketOpcode.EventWrite, CacheFlushEvent), UserData(0xB), Dispatch(2));

        // The event queued its barrier in stream order without closing the run; only the buffer end did.
        Assert.Equal(2, runs.Issued.Count);
        Assert.Equal(new[] { "barrier" }, runner.Host.Calls);
        Assert.Equal(1, runs.Barriers);
    }

    [Fact]
    public void AGraphicsShaderRegisterClosesTheRunBeforeItApplies()
    {
        var runner = new StreamRunner();
        var runs = new FakeRuns(runner.Interpreter);
        runner.Host.DispatchRuns = runs;

        runner.Run(UserData(0xA), Dispatch(1), StreamRunner.Packet(PacketOpcode.SetShaderRegister, PixelProgramLow, 0x1234), UserData(0xB), Dispatch(2));

        // The pixel register is no run packet: the first run closed before it, and the second dispatch opened another run.
        Assert.Equal(2, runs.Issued.Count);
        Assert.Equal(2, runs.Barriers);
        Assert.Equal(0x1234u, runner.Interpreter.Registers.Shader[PixelProgramLow]);
    }

    [Fact]
    public void MirrorRestorePutsBackAnUnwrittenRegister()
    {
        var mirror = new ShaderRegisterMirror();
        mirror[0x241] = 5;
        var before = mirror.Peek(0x242);
        mirror[0x242] = 7;
        mirror.Restore(0x242, before.Value, before.Written);

        Assert.False(mirror.ContainsKey(0x242));
        Assert.Single(mirror);
        Assert.Equal(5u, mirror[0x241]);
    }
}
