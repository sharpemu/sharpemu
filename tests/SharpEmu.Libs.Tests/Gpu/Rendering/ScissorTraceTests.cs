// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

public sealed class ScissorTraceTests
{
    [Fact]
    public void HistoryKeepsNewestEventsUntilTriggerThenStopsAfterBoundedFollowup()
    {
        using var output = new StringWriter();
        var trace = new ScissorTrace(output, historyLimit: 3, followupLimit: 2);
        trace.Record("old");
        trace.Record("write");
        trace.Record("context");
        Assert.Equal("", output.ToString());

        trace.Record("draw", trigger: true);
        trace.Record("after-one");
        Assert.True(trace.Active);
        trace.Record("after-two");
        Assert.False(trace.Active);
        var captured = output.ToString();
        trace.Record("ignored");
        trace.Finish("ignored");

        Assert.Equal(captured, output.ToString());
        Assert.DoesNotContain(" old", captured);
        Assert.Contains("history_entries=3", captured);
        Assert.Equal(new[] { "write", "context", "draw", "after-one", "after-two" },
            Entries(captured).Select(line => line[(line.LastIndexOf(' ') + 1)..]));
        Assert.Contains("end reason=followup-limit last_seq=6", captured);
    }

    [Fact]
    public void FinishFlushesBoundedHistoryEvenIfNoDrawTriggered()
    {
        using var output = new StringWriter();
        var trace = new ScissorTrace(output, historyLimit: 2);
        trace.Record("first");
        trace.Record("second");
        trace.Record("third");
        trace.Finish("presenter-exit");

        Assert.False(trace.Active);
        Assert.DoesNotContain(" first", output.ToString());
        Assert.Contains("begin reason=presenter-exit history_entries=2", output.ToString());
        Assert.Equal(2, Entries(output.ToString()).Length);
    }

    [Fact]
    public void RegistersContextRestoreAndDrawRetainAddressesAndOriginalState()
    {
        using var output = new StringWriter();
        var trace = new ScissorTrace(output, followupLimit: 0);
        var banks = new RegisterBanks(static message => new InvalidOperationException(message));
        var packet = new PacketContext(PacketHeader.Make(3, PacketOpcode.SetContextRegister), 0x123400, 0, 3, 3);
        const uint windowBottom = 0x03100310;
        RegisterWriteTable.WriteContextPacket(banks, in packet, ContextRegisterOffset.PaScWindowScissorBr, [windowBottom]);
        trace.RegisterWrite(banks, 9, 2, in packet, ContextRegisterOffset.PaScWindowScissorBr, windowBottom);
        trace.RegisterWrite(banks, 9, 2, in packet, ContextRegisterOffset.PaScViewportScissor0Br, 0x08700F00, 0x2000, 7, 12);
        trace.RegisterWrite(banks, 9, 2, in packet, ContextRegisterOffset.CbTargetMask, 15);
        var before = banks.Context;
        banks.ApplyContextState(ContextStateOperation.PushClear);
        trace.ContextOperation(9, 2, packet.PacketAddress, "PushClear", banks, before);
        before = banks.Context;
        banks.ApplyContextState(ContextStateOperation.Pop);
        trace.ContextOperation(9, 2, packet.PacketAddress, "Pop", banks, before);
        trace.DrawIssued(banks, 9, 2, 0x123480);
        var state = FullViewport();
        trace.Draw(banks, 9, 0x123480, 3840, 2160, 0x3000, 0x4000, in state);
        banks.Context.ScreenViewport.WindowScissorRight = 999;

        var lines = Entries(output.ToString());
        Assert.Equal(6, lines.Length);
        Assert.Contains("submit=9 queue=2", lines[0]);
        Assert.Contains("packet=0x0000000000123400", lines[0]);
        Assert.Contains("source=direct", lines[0]);
        Assert.Contains("register=0x0082 value=0x03100310", lines[0]);
        Assert.Contains("source=table table=0x0000000000002000 entry=7 count=12", lines[1]);
        Assert.Contains("operation=PushClear", lines[2]);
        Assert.Contains("before=[screen=0,0,0,0 window=0,0,784,784", lines[2]);
        Assert.Contains("after=[screen=0,0,0,0 window=0,0,0,0", lines[2]);
        Assert.Contains("operation=Pop", lines[3]);
        Assert.Contains("after=[screen=0,0,0,0 window=0,0,784,784", lines[3]);
        Assert.Contains("event=draw-issued", lines[4]);
        Assert.Contains("event=draw-execute", lines[5]);
        Assert.Contains("packet=0x0000000000123480", lines[5]);
        Assert.Contains("target=3840x2160 color=0x0000000000003000 depth=0x0000000000004000", lines[5]);
        Assert.Contains("final=0,0,784,784", lines[5]);
        Assert.DoesNotContain("window=0,0,999,784", output.ToString());
    }

    [Fact]
    public void TriggerRequiresSmallWindowAndFullTargetViewportNotAnOffscreenCharacterDraw()
    {
        var context = new ContextRegisters();
        var state = FullViewport();
        Assert.False(ScissorTrace.ShouldTrigger(context, 3840, 2160, in state));
        context.ScreenViewport.WindowScissorRight = 784;
        context.ScreenViewport.WindowScissorBottom = 784;
        Assert.True(ScissorTrace.ShouldTrigger(context, 3840, 2160, in state));
        var recovered = state with { Scissor = new ScissorRectangle(0, 0, 3840, 2160) };
        Assert.True(ScissorTrace.ShouldTrigger(context, 3840, 2160, in recovered));
        var offscreen = state with { ViewportWidth = 784, ViewportHeight = -784 };
        Assert.False(ScissorTrace.ShouldTrigger(context, 784, 784, in offscreen));
        Assert.False(ScissorTrace.ShouldTrigger(context, 3840, 2160, in offscreen));
        context.ScreenViewport.WindowScissorRight = 3840;
        context.ScreenViewport.WindowScissorBottom = 2160;
        Assert.False(ScissorTrace.ShouldTrigger(context, 3840, 2160, in state));
    }

    [Fact]
    public async Task ConcurrentWritersHaveUniqueOrderedSequenceNumbers()
    {
        using var output = new StringWriter();
        var trace = new ScissorTrace(output, historyLimit: 200);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++) trace.Record($"worker={worker} i={i}");
        })));
        trace.Finish("test");
        var sequences = Entries(output.ToString()).Select(line => long.Parse(line.Split(' ')[1][4..]));
        Assert.Equal(Enumerable.Range(1, 100).Select(i => (long)i), sequences);
    }

    private static string[] Entries(string output) => output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
        .Where(line => line.Contains("] seq=", StringComparison.Ordinal)).ToArray();

    private static DynamicDrawState FullViewport() => default(DynamicDrawState) with
    {
        ViewportWidth = 3840,
        ViewportHeight = -2160,
        Scissor = new ScissorRectangle(0, 0, 784, 784),
    };
}
