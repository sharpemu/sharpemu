// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class GpuCommandInterpreterMemoryTests
{
    private const uint Nop = PacketOpcode.Nop;
    private const ulong Label = StreamRunner.LabelAddress;
    private const ulong Data = StreamRunner.DataAddress;

    private static uint[] WriteData(uint control, ulong destination, params uint[] values)
    {
        var payload = new uint[3 + values.Length];
        payload[0] = control;
        payload[1] = StreamRunner.Low(destination);
        payload[2] = StreamRunner.High(destination);
        values.CopyTo(payload, 3);
        return StreamRunner.Packet(PacketOpcode.WriteData, payload);
    }

    private static uint[] DmaData(uint sourceSelect, uint destinationSelect, ulong source, ulong destination, uint bytes, uint engine = 0) =>
        StreamRunner.Packet(
            PacketOpcode.DmaData,
            engine | ((destinationSelect & 3) << 20) | ((sourceSelect & 3) << 29),
            StreamRunner.Low(source), StreamRunner.High(source),
            StreamRunner.Low(destination), StreamRunner.High(destination),
            bytes | ((sourceSelect & 4) << 24) | ((destinationSelect & 4) << 25) | ((sourceSelect & 8) << 25) | ((destinationSelect & 8) << 26));

    private static uint[] CopyData(uint rawSource, uint rawDestination, uint engine, bool is64Bit, ulong source, ulong destination) =>
        StreamRunner.Packet(
            PacketOpcode.CopyData,
            rawSource | (rawDestination << 8) | (is64Bit ? 1u << 16 : 0) | (engine << 30),
            StreamRunner.Low(source), StreamRunner.High(source),
            StreamRunner.Low(destination), StreamRunner.High(destination));

    [Fact]
    public void WriteData_CopiesOrWritesOneAddress()
    {
        var runner = new StreamRunner();

        runner.Run(WriteData(5u << 8, Label, 1, 2, 3));
        Assert.Equal(1u, runner.Host.ReadDword(Label));
        Assert.Equal(3u, runner.Host.ReadDword(Label + 8));

        runner.Run(WriteData((5u << 8) | (1u << 16), Label, 7, 8));
        Assert.Equal(8u, runner.Host.ReadDword(Label));
        Assert.Equal(2u, runner.Host.ReadDword(Label + 4));

        Assert.Contains("write-data destination selector", runner.RunExpectingFatal(WriteData(0, Label, 1)).Message);
        Assert.Empty(runner.Host.Calls);
    }

    [Fact]
    public void WrappedWriteData_UsesTheByteControlAndSkipsOtherDestinations()
    {
        var runner = new StreamRunner();
        runner.Host.WriteDword(Label, 0);

        runner.Run(StreamRunner.CustomPacket(Nop, PacketCustomCode.WriteData, 5u, StreamRunner.Low(Label), StreamRunner.High(Label), 0xAB, 0xCD));
        Assert.Equal(0xABu, runner.Host.ReadDword(Label));
        Assert.Equal(0xCDu, runner.Host.ReadDword(Label + 4));

        runner.Run(StreamRunner.CustomPacket(Nop, PacketCustomCode.WriteData, 5u | (1u << 16), StreamRunner.Low(Label), StreamRunner.High(Label), 1, 2));
        Assert.Equal(2u, runner.Host.ReadDword(Label));

        runner.Run(StreamRunner.CustomPacket(Nop, PacketCustomCode.WriteData, 3u, StreamRunner.Low(Label), StreamRunner.High(Label), 9));
        Assert.Equal(2u, runner.Host.ReadDword(Label));
    }

    [Fact]
    public void DmaData_FillsCopiesAndSkips()
    {
        var runner = new StreamRunner();

        runner.Run(DmaData(2, 0, 0x1234, Data, 16));
        Assert.Equal($"fill {Data:X} 10 00001234 gds=False", Assert.Single(runner.Host.Calls));
        Assert.Equal(0x1234u, runner.Host.ReadDword(Data + 12));

        runner.Host.Calls.Clear();
        runner.Run(DmaData(0, 1, Data, 0x40, 8));
        Assert.Equal($"copy 40 {Data:X} 8 dstGds=True srcGds=False", Assert.Single(runner.Host.Calls));

        runner.Host.Calls.Clear();
        runner.Run(DmaData(3, 2, Data, Label, 8), DmaData(0, 0, Data, 0x3022C, 8), DmaData(2, 0, 0, Data, 0));
        Assert.Empty(runner.Host.Calls);

        Assert.Contains("GDS-to-GDS", runner.RunExpectingFatal(DmaData(1, 1, 0, 0x10, 8)).Message);
        Assert.Contains("nowhere source", runner.RunExpectingFatal(DmaData(0, 2, Data, Label, 8)).Message);
    }

    [Fact]
    public void WrappedDmaData_BothLayouts()
    {
        var runner = new StreamRunner();

        runner.Run(StreamRunner.CustomPacket(Nop, PacketCustomCode.DmaData, 0u | (2u << 16), 0, 8, StreamRunner.Low(Data), StreamRunner.High(Data), 0x77, 0));
        Assert.Equal($"fill {Data:X} 8 00000077 gds=False", Assert.Single(runner.Host.Calls));

        runner.Host.Calls.Clear();
        runner.Run(StreamRunner.CustomPacket(Nop, PacketCustomCode.DmaData, StreamRunner.Low(Label), StreamRunner.High(Label), StreamRunner.Low(Data), StreamRunner.High(Data), 8, 3u | (0u << 8)));
        Assert.Equal($"copy {Label:X} {Data:X} 8 dstGds=False srcGds=False", Assert.Single(runner.Host.Calls));
        Assert.Equal(0x77u, runner.Host.ReadDword(Label));
    }

    [Theory]
    [InlineData(0u, 2u, 0u)]
    [InlineData(1u, 2u, 0x12345678u)]
    [InlineData(0u, 3u, 0x87654321u)]
    [InlineData(1u, 3u, 0u)]
    public void DmaBuilder_UsesGuestArgumentsForFillAndCopy(uint engine, uint sourceSelector, uint value)
    {
        var runner = new StreamRunner();
        var context = new CpuContext(runner.Host.Memory, Generation.Gen5);
        const ulong commandBufferAddress = StreamRunner.TableAddress;
        const ulong stackAddress = StreamRunner.TableAddress + 0x100;
        runner.Host.WriteQword(commandBufferAddress + 0x10, StreamRunner.CommandAddress);
        runner.Host.WriteQword(commandBufferAddress + 0x18, StreamRunner.CommandAddress + 0x100);
        runner.Host.WriteDword(Data, value);
        runner.Host.WriteDword(Label, 0xFFFFFFFF);
        context[CpuRegister.Rdi] = commandBufferAddress;
        context[CpuRegister.Rsi] = engine;
        context[CpuRegister.Rdx] = 3;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = Label;
        context[CpuRegister.R9] = sourceSelector;
        context[CpuRegister.Rsp] = stackAddress;
        runner.Host.WriteQword(stackAddress + 8, 1);
        runner.Host.WriteQword(stackAddress + 16, sourceSelector == 2 ? value : Data);
        runner.Host.WriteQword(stackAddress + 24, 4);
        runner.Host.WriteQword(stackAddress + 32, 1);
        runner.Host.WriteQword(stackAddress + 40, 1);
        runner.Host.WriteQword(stackAddress + 48, 1);

        AgcExports.DcbDmaData(context);

        Assert.Equal(StreamRunner.CommandAddress, context[CpuRegister.Rax]);
        Assert.Equal(StreamRunner.CommandAddress + 32, runner.Host.ReadQword(commandBufferAddress + 0x10));
        Assert.Equal(3u | (sourceSelector << 16) | (1u << 24), runner.Host.ReadDword(StreamRunner.CommandAddress + 4));
        Assert.Equal(engine | 0x01010100u, runner.Host.ReadDword(StreamRunner.CommandAddress + 8));
        runner.Interpreter.Process(new PacketCursorStack(), StreamRunner.CommandAddress, 8);

        Assert.Equal(value, runner.Host.ReadDword(Label));
        Assert.Equal(
            sourceSelector == 2 ? $"fill {Label:X} 4 {value:X8} gds=False" : $"copy {Label:X} {Data:X} 4 dstGds=False srcGds=False",
            Assert.Single(runner.Host.Calls));
    }

    [Fact]
    public void CopyData_MapsSelectorsToTransfers()
    {
        var runner = new StreamRunner();
        runner.Host.WriteQword(Data, 0x1122_3344_5566_7788);

        runner.Run(CopyData(2, 2, 0, false, Data, Label));
        Assert.Equal($"copy {Label:X} {Data:X} 4 dstGds=False srcGds=False", Assert.Single(runner.Host.Calls));

        runner.Host.Calls.Clear();
        runner.Run(CopyData(5, 2, 0, false, 0x5A5A, Label));
        Assert.Equal($"fill {Label:X} 4 00005A5A gds=False", Assert.Single(runner.Host.Calls));

        runner.Host.Calls.Clear();
        runner.Run(CopyData(5, 2, 0, true, 0x0102_0304_0506_0708, Label));
        Assert.Empty(runner.Host.Calls);
        Assert.Equal(0x0102_0304_0506_0708UL, runner.Host.ReadQword(Label));

        runner.Run(CopyData(4, 1, 1, true, 0, Label));
        Assert.NotEqual(0x0102_0304_0506_0708UL, runner.Host.ReadQword(Label));

        Assert.Contains("copy-data source selector", runner.RunExpectingFatal(CopyData(0, 2, 0, false, Data, Label)).Message);
        Assert.Contains("reference-clock copy-data", runner.RunExpectingFatal(CopyData(4, 3, 1, false, 0, Label)).Message);
    }

    [Fact]
    public void CopyData_AtomicReturnAndComputeEncoding()
    {
        var runner = new StreamRunner();
        Assert.Contains("atomic return value is not available", runner.RunExpectingFatal(CopyData(6, 2, 0, false, 0, Label)).Message);

        var atomic = StreamRunner.Packet(PacketOpcode.AtomicMemory, 0x0F, StreamRunner.Low(Data), StreamRunner.High(Data), 5, 0, 0, 0, 0);
        runner.Host.WriteDword(Data, 40);
        runner.Run(atomic, CopyData(6, 2, 0, false, 0, Label));
        Assert.Equal(40u, runner.Host.ReadDword(Label));
        Assert.Equal(45u, runner.Host.ReadDword(Data));

        var compute = new StreamRunner(queueId: 1);
        compute.Host.WriteDword(Data, 0xBEEF);
        compute.Run(CopyData(2, 2, 0, false, Data, Label));
        Assert.Equal($"copy {Label:X} {Data:X} 4 dstGds=False srcGds=False", Assert.Single(compute.Host.Calls));
        Assert.Contains("copy-data source selector", compute.RunExpectingFatal(CopyData(4, 2, 0, false, Data, Label)).Message);
    }

    [Fact]
    public void GetLodStats_ZeroesTheBufferAndMarksItValid()
    {
        var runner = new StreamRunner();
        runner.Host.WriteQword(Label, ulong.MaxValue);
        runner.Host.WriteQword(Label + 8, ulong.MaxValue);

        runner.Run(StreamRunner.Packet(PacketOpcode.GetLodStats, 16, StreamRunner.Low(Label), StreamRunner.High(Label), 0));

        Assert.Equal(1UL, runner.Host.ReadQword(Label));
        Assert.Equal(0UL, runner.Host.ReadQword(Label + 8));
    }

    [Fact]
    public void ReadOfUnmappedMemory_IsFatalWithTheAddress()
    {
        var runner = new StreamRunner();

        var fatal = runner.RunExpectingFatal(StreamRunner.Packet(PacketOpcode.WaitRegisterMemory, 0x13, 0x10, 0, 1, 1, 0));

        Assert.Contains("cannot read guest memory: address=0x0000000000000010", fatal.Message);
    }
}
