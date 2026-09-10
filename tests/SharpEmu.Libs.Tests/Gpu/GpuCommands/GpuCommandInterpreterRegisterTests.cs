// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class GpuCommandInterpreterRegisterTests
{
    private const uint Nop = PacketOpcode.Nop;
    private const ulong Table = StreamRunner.TableAddress;

    private static uint[] Table5(uint opcode, ulong table, uint count) =>
        StreamRunner.Packet(opcode, StreamRunner.Low(table), StreamRunner.High(table), 0, count);

    private static uint[] Pairs(params (uint Offset, uint Value)[] pairs)
    {
        var words = new uint[pairs.Length * 2];
        for (var index = 0; index < pairs.Length; index++)
        {
            words[index * 2] = pairs[index].Offset;
            words[(index * 2) + 1] = pairs[index].Value;
        }

        return words;
    }

    [Fact]
    public void DirectPackets_WriteTheRightBank()
    {
        var runner = new StreamRunner();

        runner.Run(
            StreamRunner.Packet(PacketOpcode.SetContextRegister, 0x4000_008E, 0xF, 0x3),
            StreamRunner.Packet(PacketOpcode.SetShaderRegister, 0x10, 0xAA),
            StreamRunner.Packet(PacketOpcode.SetUserConfigRegister, 0x1000_0243, 0x1),
            StreamRunner.Packet(PacketOpcode.SetUserConfigRegisterIndex, 0x1000_0244, 0x2));

        Assert.Equal(0xFu, runner.Interpreter.Registers.Context[0x8E]);
        Assert.Equal(0x3u, runner.Interpreter.Registers.Context[0x8F]);
        Assert.Equal(0xAAu, runner.Interpreter.Registers.Shader[0x10]);
        Assert.Equal(0x1u, runner.Interpreter.Registers.UserConfig[0x243]);
        Assert.Equal(0x2u, runner.Interpreter.Registers.UserConfig[0x244]);
        Assert.Equal(1u, runner.Interpreter.IndexTypeAndSize);
    }

    [Fact]
    public void NopRegistersAndInternalDataPackets_AreSkipped()
    {
        var runner = new StreamRunner();

        runner.Run(
            StreamRunner.Packet(PacketOpcode.SetContextRegister, RegisterBankLayout.ContextNop, 1),
            StreamRunner.Packet(PacketOpcode.SetShaderRegister, RegisterBankLayout.ShaderRegisterNop, 1),
            StreamRunner.Packet(PacketOpcode.SetUserConfigRegister, RegisterBankLayout.UserConfigNop, 1),
            StreamRunner.CustomPacket(PacketOpcode.SetUserConfigRegister, 1, 0x342, 0xC800_0000));

        Assert.Empty(runner.Interpreter.Registers.Context);
        Assert.Empty(runner.Interpreter.Registers.Shader);
        Assert.Empty(runner.Interpreter.Registers.UserConfig);
    }

    [Fact]
    public void RegistersOutsideTheBank_AreFatal()
    {
        var runner = new StreamRunner();

        Assert.Contains("context register is outside", runner.RunExpectingFatal(StreamRunner.Packet(PacketOpcode.SetContextRegister, 0x400, 1)).Message);
        Assert.Contains("shader register is outside", runner.RunExpectingFatal(StreamRunner.Packet(PacketOpcode.SetShaderRegister, 0x300, 1)).Message);
        Assert.Contains("user-config register is outside", runner.RunExpectingFatal(StreamRunner.Packet(PacketOpcode.SetUserConfigRegister, 0x4000, 1)).Message);
        Assert.Contains("range leaves the bank", runner.RunExpectingFatal(StreamRunner.Packet(PacketOpcode.SetContextRegister, 0x3FF, 1, 2)).Message);
    }

    [Fact]
    public void DirectDepthWrites_ClearTheCompositeExtent()
    {
        var runner = new StreamRunner();
        runner.Interpreter.Registers.CompositeDepthSizeXy = 0x1234;

        runner.Run(StreamRunner.Packet(PacketOpcode.SetContextRegister, RegisterBankLayout.DepthZInfo, 5));

        Assert.Null(runner.Interpreter.Registers.CompositeDepthSizeXy);
    }

    [Theory]
    [InlineData(ContextStateOperation.Push)]
    [InlineData(ContextStateOperation.PushClear)]
    public void ContextState_RestoresTheSavedCompositeExtent(ContextStateOperation operation)
    {
        var registers = new CommandRegisterBanks { CompositeDepthSizeXy = 0x1234 };
        registers.ApplyContextState(operation, message => new InvalidOperationException(message));

        Assert.Equal(operation == ContextStateOperation.Push ? 0x1234u : (uint?)null, registers.CompositeDepthSizeXy);
        registers.CompositeDepthSizeXy = 0x5678;
        registers.ApplyContextState(ContextStateOperation.Clear, message => new InvalidOperationException(message));
        Assert.Null(registers.CompositeDepthSizeXy);
        registers.ApplyContextState(ContextStateOperation.Pop, message => new InvalidOperationException(message));
        Assert.Equal(0x1234u, registers.CompositeDepthSizeXy);

        registers.CompositeDepthSizeXy = null;
        registers.ApplyContextState(operation, message => new InvalidOperationException(message));
        registers.CompositeDepthSizeXy = 0x5678;
        registers.ApplyContextState(ContextStateOperation.Pop, message => new InvalidOperationException(message));
        Assert.Null(registers.CompositeDepthSizeXy);
    }

    [Fact]
    public void Reset_DiscardsActiveAndSavedContextState()
    {
        var registers = new CommandRegisterBanks { CompositeDepthSizeXy = 0x1234 };
        registers.ApplyContextState(ContextStateOperation.Push, message => new InvalidOperationException(message));

        registers.Reset();

        Assert.Null(registers.CompositeDepthSizeXy);
        Assert.False(registers.ContextPushed);
        Assert.Throws<InvalidOperationException>(() =>
            registers.ApplyContextState(ContextStateOperation.Pop, message => new InvalidOperationException(message)));
        registers.ApplyContextState(ContextStateOperation.Push, message => new InvalidOperationException(message));
        registers.CompositeDepthSizeXy = 0x5678;
        registers.ApplyContextState(ContextStateOperation.Pop, message => new InvalidOperationException(message));
        Assert.Null(registers.CompositeDepthSizeXy);
    }

    [Fact]
    public void NativeTables_ApplyPairsWithSkipsAndBounds()
    {
        var runner = new StreamRunner();
        runner.Host.WriteWords(Table, Pairs((0x8E, 0xF), (0xFFFF_FFFF, 9), (RegisterBankLayout.ContextNop, 9), (0x400, 9), (0x7000_0202, 0x5)));

        runner.Run(Table5(PacketOpcode.SetContextRegisterIndirect, Table, 5));
        Assert.Equal(0xFu, runner.Interpreter.Registers.Context[0x8E]);
        Assert.Equal(0x5u, runner.Interpreter.Registers.Context[0x202]);
        Assert.Equal(2, runner.Interpreter.Registers.Context.Count);

        runner.Host.WriteWords(Table, Pairs((0x10, 1), (RegisterBankLayout.ShaderRegisterNop, 2), (0xFFFF_FFFF, 3)));
        runner.Run(Table5(PacketOpcode.SetShaderRegisterIndirect, Table, 3));
        Assert.Equal(1u, runner.Interpreter.Registers.Shader[0x10]);
        Assert.Single(runner.Interpreter.Registers.Shader);

        runner.Host.WriteWords(Table, Pairs((0x300, 1)));
        Assert.Contains("shader table register is outside", runner.RunExpectingFatal(Table5(PacketOpcode.SetShaderRegisterIndirect, Table, 1)).Message);

        runner.Host.WriteWords(Table, Pairs((RegisterBankLayout.UserConfigNop, 1), (RegisterBankLayout.DepthSizeXy, 0x77), (0x243, 2)));
        runner.Run(Table5(PacketOpcode.SetUserConfigRegisterIndirect, Table, 3));
        Assert.Equal(0x77u, runner.Interpreter.Registers.Context[RegisterBankLayout.DepthSizeXy]);
        Assert.Equal(2u, runner.Interpreter.IndexTypeAndSize);
        Assert.False(runner.Interpreter.Registers.UserConfig.ContainsKey(RegisterBankLayout.DepthSizeXy));

        runner.Run(Table5(PacketOpcode.SetContextRegisterIndirect, 0, 0));
        Assert.Contains("table address is zero", runner.RunExpectingFatal(Table5(PacketOpcode.SetContextRegisterIndirect, 0, 1)).Message);
    }

    [Fact]
    public void WrappedTables_CarryTheCountFirst()
    {
        var runner = new StreamRunner();
        runner.Host.WriteWords(Table, Pairs((0x8E, 0xA), (0x8F, 0xB)));

        runner.Run(StreamRunner.CustomPacket(Nop, PacketCustomCode.ContextRegisterTable, 2, StreamRunner.Low(Table), StreamRunner.High(Table)));

        Assert.Equal(0xAu, runner.Interpreter.Registers.Context[0x8E]);
        Assert.Equal(0xBu, runner.Interpreter.Registers.Context[0x8F]);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(128, false)]
    [InlineData(129, false)]
    [InlineData(257, false)]
    [InlineData(129, true)]
    public void Tables_ReadBoundedBlocksAndPreserveRepeatedWriteOrder(int entryCount, bool wrapped)
    {
        var runner = new StreamRunner();
        var entries = Enumerable.Range(0, entryCount).Select(index => (0x10u, (uint)index)).ToArray();
        runner.Host.WriteWords(Table, Pairs(entries));
        var packet = wrapped
            ? StreamRunner.CustomPacket(Nop, PacketCustomCode.ShaderRegisterTable, (uint)entryCount, StreamRunner.Low(Table), StreamRunner.High(Table))
            : Table5(PacketOpcode.SetShaderRegisterIndirect, Table, (uint)entryCount);

        runner.Run(packet);

        Assert.Equal((uint)entryCount - 1, runner.Interpreter.Registers.Shader[0x10]);
        var tableReads = runner.Host.GuestReads.Where(address => address >= Table && address < Table + (ulong)entryCount * 8).ToArray();
        var expectedAddresses = Enumerable.Range(0, (entryCount + 127) / 128).Select(index => Table + (ulong)index * 1024);
        Assert.Equal(expectedAddresses, tableReads);
    }

    [Fact]
    public void TableBlock_UsesSynchronizedExecutionTimeData()
    {
        var runner = new StreamRunner();
        runner.Host.WriteWords(Table, Pairs((0x10, 1)));
        runner.Host.PendingGpuValues[Table] = (2UL << 32) | 0x10;
        var packet = Table5(PacketOpcode.SetShaderRegisterIndirect, Table, 1);
        runner.Run(packet);
        Assert.Equal(2u, runner.Interpreter.Registers.Shader[0x10]);

        runner.Host.WriteWords(Table, Pairs((0x10, 3)));
        runner.Run(packet);
        Assert.Equal(3u, runner.Interpreter.Registers.Shader[0x10]);
    }

    [Fact]
    public void TableBlock_DoesNotReadPastTheRequestedRange()
    {
        var runner = new StreamRunner();
        var lastEntry = RecordingCommandStreamHost.MemoryBase + RecordingCommandStreamHost.MemorySize - 8UL;
        runner.Host.WriteWords(lastEntry, Pairs((0x10, 7)));
        runner.Run(Table5(PacketOpcode.SetShaderRegisterIndirect, lastEntry, 1));
        Assert.Equal(7u, runner.Interpreter.Registers.Shader[0x10]);
        Assert.Contains("register table cannot be read", runner.RunExpectingFatal(Table5(PacketOpcode.SetShaderRegisterIndirect, lastEntry, 2)).Message);
        Assert.Contains("exceeds the address space", runner.RunExpectingFatal(Table5(PacketOpcode.SetShaderRegisterIndirect, ulong.MaxValue - 3, 2)).Message);
    }
}
