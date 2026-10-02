// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class GpuCommandInterpreterOcclusionTests
{
    private const ulong Result = RecordingCommandStreamHost.MemoryBase + 0x8000;
    private const ulong Ready = 1UL << 63;

    private static uint[] Dump(ulong address) => StreamRunner.Packet(PacketOpcode.EventWrite,
        0x139, StreamRunner.Low(address), StreamRunner.High(address));

    [Theory]
    [InlineData(0UL)]
    [InlineData(27UL)]
    public void Dump_UsesCompletedSamplesWithoutDuplicatingTheHostSum(ulong count)
    {
        var runner = new StreamRunner();
        runner.Host.ReadOcclusionSamples = _ => count;
        runner.Run(Dump(Result), Dump(Result + 8));

        Assert.Equal(Ready | count, runner.Host.ReadQword(Result));
        Assert.Equal(Ready | count, runner.Host.ReadQword(Result + 8));
        for (var block = 1UL; block < 16; block++)
        {
            Assert.Equal(Ready, runner.Host.ReadQword(Result + block * 16));
            Assert.Equal(Ready, runner.Host.ReadQword(Result + block * 16 + 8));
        }
    }

    [Fact]
    public void Dump_PreservesTheSampleDeltaAndResetPoint()
    {
        var runner = new StreamRunner();
        var samples = 100UL;
        runner.Host.ReadOcclusionSamples = _ => samples;
        runner.Run(Dump(Result));
        samples += 37;
        runner.Run(Dump(Result + 8));
        Assert.Equal(37UL, runner.Host.ReadQword(Result + 8) - runner.Host.ReadQword(Result));

        runner.Run(StreamRunner.Packet(PacketOpcode.EventWrite, 0x3A));
        samples += 9;
        runner.Run(Dump(Result));
        Assert.Equal(Ready | 9, runner.Host.ReadQword(Result));
    }

    [Fact]
    public void Dump_DoesNotPublishWhenTheHostReadFails()
    {
        var runner = new StreamRunner();
        runner.Host.ReadOcclusionSamples = _ => throw new CommandStreamFatalException("Query read failed.");
        Assert.Contains("Query read failed", runner.RunExpectingFatal(Dump(Result)).Message);
        Assert.Equal(0UL, runner.Host.ReadQword(Result));
        Assert.Equal(0UL, runner.Host.ReadQword(Result + 15 * 16));
    }

    [Fact]
    public void Dump_UsesTheQueueAndPreservesDisabledBlocks()
    {
        var runner = new StreamRunner(queueId: 2);
        runner.Host.ReadOcclusionSamples = queueId => { Assert.Equal(2, queueId); return 11; };
        runner.Host.WriteQword(Result, 0xAA);
        const ulong select = (2UL << 9) | (0x800002UL << 11);
        runner.Run(StreamRunner.Packet(PacketOpcode.EventWrite, 0x138, StreamRunner.Low(select), StreamRunner.High(select)), Dump(Result));
        Assert.Equal(0xAAUL, runner.Host.ReadQword(Result));
        Assert.Equal(Ready, runner.Host.ReadQword(Result + 16));
        Assert.Equal(Ready, runner.Host.ReadQword(Result + 23 * 16));
    }

    [Fact]
    public void OtherCounters_KeepTheLegacyFallback()
    {
        var runner = new StreamRunner();
        runner.Host.ReadOcclusionSamples = _ => throw new InvalidOperationException("Counter zero must not be read.");
        const ulong select = (3UL << 3) | (2UL << 9) | (1UL << 11);
        runner.Run(StreamRunner.Packet(PacketOpcode.EventWrite, 0x138, StreamRunner.Low(select), StreamRunner.High(select)), Dump(Result), Dump(Result + 8));
        Assert.Equal(Ready, runner.Host.ReadQword(Result));
        Assert.Equal(Ready | 1, runner.Host.ReadQword(Result + 8));
    }
}
