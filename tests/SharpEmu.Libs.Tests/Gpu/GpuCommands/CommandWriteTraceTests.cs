// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class CommandWriteTraceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RejectedWrite_DoesNotEnterWriterHistory(int form)
    {
        var runner = new StreamRunner();
        var address = RecordingCommandStreamHost.MemoryBase + RecordingCommandStreamHost.MemorySize;
        Assert.Throws<CommandStreamFatalException>(() => Write(runner.Interpreter, address, form));
        Assert.Empty(runner.Host.TracedWrites);
        Assert.Equal(0u, runner.Host.ReadDword(StreamRunner.DataAddress));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SuccessfulWrite_IsCompleteBeforeWriterHistory(int form)
    {
        var runner = new StreamRunner();
        Write(runner.Interpreter, StreamRunner.DataAddress, form);
        var expected = form == 1 ? new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 } : new byte[] { 8, 7, 6, 5 };
        Span<byte> actual = stackalloc byte[expected.Length];
        Assert.True(runner.Host.GuestMemory.TryRead(StreamRunner.DataAddress, actual));
        Assert.Equal(expected, actual.ToArray());
        if (!ImageClearTrace.Enabled)
        {
            Assert.Empty(runner.Host.TracedWrites);
            return;
        }
        var trace = Assert.Single(runner.Host.TracedWrites);
        Assert.Equal("command-write-Write", trace.Operation);
        Assert.Equal(StreamRunner.DataAddress, trace.Address);
        Assert.Equal((ulong)expected.Length, trace.Size);
        Assert.Equal(expected, trace.Bytes);
    }

    [Fact]
    public void PacketWrite_RetainsTheHandlerNameAndRange()
    {
        var runner = new StreamRunner();
        var address = StreamRunner.DataAddress;
        runner.Run(StreamRunner.Packet(PacketOpcode.WriteData, 5u << 8,
            StreamRunner.Low(address), StreamRunner.High(address), 0x11223344u, 0x55667788u));
        Assert.Equal(0x11223344u, runner.Host.ReadDword(address));
        Assert.Equal(0x55667788u, runner.Host.ReadDword(address + 4));
        if (!ImageClearTrace.Enabled) return;
        var trace = Assert.Single(runner.Host.TracedWrites);
        Assert.Equal("command-write-WriteDataValues", trace.Operation);
        Assert.Equal(address, trace.Address);
        Assert.Equal(8UL, trace.Size);
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32LittleEndian(trace.Bytes));
        Assert.Equal(0x55667788u, BinaryPrimitives.ReadUInt32LittleEndian(trace.Bytes.AsSpan(4)));
    }

    private static void Write(GpuCommandInterpreter interpreter, ulong address, int form)
    {
        if (form == 0) interpreter.WriteDword(address, 0x05060708u);
        else if (form == 1) interpreter.WriteQword(address, 0x0102030405060708UL);
        else interpreter.WriteBytes(address, new byte[] { 8, 7, 6, 5 });
    }
}
