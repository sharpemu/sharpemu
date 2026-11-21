// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.GnmMode;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

public sealed class GnmEndOfPipeTests
{
    private const ulong Label = StreamRunner.LabelAddress;

    private static uint[] EventWriteEop(uint eventType, uint eventIndex, uint cacheAction, uint source, uint interruptSelector, ulong destination, ulong value) =>
        StreamRunner.Packet(
            PacketOpcode.EventWriteEndOfPipe,
            eventType | (eventIndex << 8) | (cacheAction << 12),
            StreamRunner.Low(destination),
            (StreamRunner.High(destination) & 0xFFFF) | (interruptSelector << 24) | (source << 29),
            StreamRunner.Low(value),
            StreamRunner.High(value));

    // A PS4 Unity title (Signalis) emits a 32-bit cache-flush timestamp with a full cache write-back.
    [Theory]
    [InlineData(0x04u, 0x00u, EndOfPipeWriteKind.Write32)]
    [InlineData(0x04u, 0x38u, EndOfPipeWriteKind.WriteBack32)]
    [InlineData(0x28u, 0x38u, EndOfPipeWriteKind.WriteBack32)]
    public void GnmEndOfPipeLowDword_TimestampWritesThirtyTwoBits(uint eventType, uint cacheAction, EndOfPipeWriteKind expected)
    {
        var runner = new StreamRunner();
        var packets = typeof(GnmRuntime).Assembly.GetType("SharpEmu.GnmMode.Gpu.GnmCommandPackets")!;
        var handlers = (PacketHandler?[])packets.GetField("Handlers", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        runner.Interpreter.SetPacketHandlers(handlers);
        runner.Host.WriteQword(Label, 0xFFFF_FFFF_FFFF_FFFF);

        runner.Run(EventWriteEop(eventType, 5, cacheAction, 1, 0, Label, 0x1122_3344_5566_7788));

        Assert.Equal(expected, Assert.Single(runner.Host.EndOfPipeWrites).Kind);
        Assert.Equal(0xFFFF_FFFF_5566_7788UL, runner.Host.ReadQword(Label));
    }

    [Fact]
    public void AgcEndOfPipeLowDword_WriteBackTimestampStaysRejected()
    {
        var runner = new StreamRunner();

        var fatal = runner.RunExpectingFatal(EventWriteEop(0x04, 5, 0x38, 1, 0, Label, 0x1122_3344_5566_7788));

        Assert.Contains("type=0x04", fatal.Message);
    }
}
