// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VideoOutOutputSupportTests
{
    private const string OpenNid = "Up36PTk687E";
    private const string CloseNid = "uquVH4-Du78";
    private const string OutputSupportNid = "Nv8c-Kb+DUM";
    private const string OutputStatusNid = "utPrVdxio-8";
    private const string ConfigureOutputNid = "w0hLuNarQxY";
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong OptionsAddress = MemoryBase + 0x100;
    private static readonly ulong InvalidValue = unchecked((ulong)(int)0x80290001);
    private static readonly ulong InvalidHandle = unchecked((ulong)(int)0x8029000B);
    private static readonly ulong UnsupportedOutputMode = unchecked((ulong)(int)0x80290016);
    private static readonly ulong InvalidOption = unchecked((ulong)(int)0x8029001A);
    private static readonly ulong MemoryFault =
        unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);

    [Theory]
    [InlineData(Generation.Gen4)]
    [InlineData(Generation.Gen5)]
    public void OutputStatusUsesRefreshRateCodeAndPreservesBufferBoundaries(Generation generation)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation));
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, generation);
        Assert.True(manager.TryDispatch(OpenNid, context, out _));
        var handle = context[CpuRegister.Rax];
        Assert.InRange(handle, 1UL, (ulong)int.MaxValue);
        try
        {
            var buffer = Enumerable.Repeat((byte)0xA5, 0x32).ToArray();
            Assert.True(memory.TryWrite(OptionsAddress, buffer));
            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = OptionsAddress + 1;
            Assert.True(manager.TryDispatch(OutputStatusNid, context, out _));
            Assert.Equal(0UL, context[CpuRegister.Rax]);
            Assert.True(memory.TryRead(OptionsAddress, buffer));

            var expected = new byte[0x32];
            expected[0] = 0xA5;
            expected[^1] = 0xA5;
            BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(1), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(5), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(expected.AsSpan(9), 3);
            Assert.Equal(expected, buffer);

            context[CpuRegister.Rsi] = 0;
            Assert.True(manager.TryDispatch(OutputStatusNid, context, out _));
            Assert.Equal(unchecked((ulong)(int)0x80290002), context[CpuRegister.Rax]);
            context[CpuRegister.Rsi] = MemoryBase + 0xFF0;
            Assert.True(manager.TryDispatch(OutputStatusNid, context, out _));
            Assert.Equal(MemoryFault, context[CpuRegister.Rax]);
            context[CpuRegister.Rsi] = OptionsAddress + 1;
            context[CpuRegister.Rdi] = ulong.MaxValue;
            Assert.True(manager.TryDispatch(OutputStatusNid, context, out _));
            Assert.Equal(InvalidHandle, context[CpuRegister.Rax]);
        }
        finally
        {
            context[CpuRegister.Rdi] = handle;
            Assert.True(manager.TryDispatch(CloseNid, context, out _));
        }
    }

    [Fact]
    public void Gen5QueryReportsCapabilitiesAndValidatesArguments()
    {
        var gen4Manager = new ModuleManager();
        gen4Manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4));
        Assert.False(gen4Manager.TryGetExport(OutputSupportNid, out _));

        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        Assert.True(manager.TryGetExport(OutputSupportNid, out var export));
        Assert.Equal("sceVideoOutIsOutputSupported", export.Name);
        Assert.Equal("libSceVideoOut", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);

        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = 0;
        Assert.True(manager.TryDispatch(OpenNid, context, out _));
        var handle = context[CpuRegister.Rax];
        Assert.NotEqual(0UL, handle);

        try
        {
            Assert.Equal(1UL, DispatchOutputSupport(manager, context, handle, 1));
            Assert.Equal(0UL, DispatchOutputSupport(manager, context, handle, 15));
            Assert.Equal(
                InvalidHandle,
                DispatchOutputSupport(manager, context, ulong.MaxValue, 1));
            Assert.Equal(
                InvalidValue,
                DispatchOutputSupport(manager, context, handle, 1, reservedPointer: 1));
            Assert.Equal(
                InvalidValue,
                DispatchOutputSupport(manager, context, handle, 1, reserved: 1));
            Assert.Equal(
                1UL,
                DispatchOutputSupport(manager, context, handle, 1, OptionsAddress));
            Assert.Equal(
                MemoryFault,
                DispatchOutputSupport(manager, context, handle, 1, MemoryBase + 0x1000));

            Assert.True(memory.TryWrite(OptionsAddress, new byte[] { 1 }));
            Assert.Equal(
                InvalidOption,
                DispatchOutputSupport(manager, context, handle, 1, OptionsAddress));
            Assert.Equal(
                UnsupportedOutputMode,
                DispatchOutputSupport(manager, context, handle, 2));

            // Configuration must use the same validation as the capability query.
            Assert.True(manager.TryDispatch(ConfigureOutputNid, context, out _));
            Assert.Equal(UnsupportedOutputMode, context[CpuRegister.Rax]);
            DispatchOutputSupport(manager, context, handle, 1, OptionsAddress);
            Assert.True(manager.TryDispatch(ConfigureOutputNid, context, out _));
            Assert.Equal(InvalidOption, context[CpuRegister.Rax]);
            Assert.True(memory.TryWrite(OptionsAddress, new byte[0x40]));
            DispatchOutputSupport(manager, context, handle, 1, OptionsAddress);
            Assert.True(manager.TryDispatch(ConfigureOutputNid, context, out _));
            Assert.Equal(0UL, context[CpuRegister.Rax]);
            DispatchOutputSupport(manager, context, handle, 15);
            Assert.True(manager.TryDispatch(ConfigureOutputNid, context, out _));
            Assert.Equal(unchecked((ulong)(int)0x80290019), context[CpuRegister.Rax]);
            DispatchOutputSupport(manager, context, handle, 1, reserved: 1);
            Assert.True(manager.TryDispatch(ConfigureOutputNid, context, out _));
            Assert.Equal(InvalidValue, context[CpuRegister.Rax]);
            DispatchOutputSupport(manager, context, handle, 1, reservedPointer: 1);
            Assert.True(manager.TryDispatch(ConfigureOutputNid, context, out _));
            Assert.Equal(InvalidValue, context[CpuRegister.Rax]);
            DispatchOutputSupport(manager, context, handle, 1, MemoryBase + 0x1000);
            Assert.True(manager.TryDispatch(ConfigureOutputNid, context, out _));
            Assert.Equal(MemoryFault, context[CpuRegister.Rax]);
            DispatchOutputSupport(manager, context, ulong.MaxValue, 1);
            Assert.True(manager.TryDispatch(ConfigureOutputNid, context, out _));
            Assert.Equal(InvalidHandle, context[CpuRegister.Rax]);
        }
        finally
        {
            context[CpuRegister.Rdi] = handle;
            _ = manager.TryDispatch(CloseNid, context, out _);
        }
    }

    private static ulong DispatchOutputSupport(
        ModuleManager manager,
        CpuContext context,
        ulong handle,
        ulong mode,
        ulong optionsAddress = 0,
        ulong reservedPointer = 0,
        ulong reserved = 0)
    {
        context[CpuRegister.Rdi] = handle;
        context[CpuRegister.Rsi] = mode;
        context[CpuRegister.Rdx] = optionsAddress;
        context[CpuRegister.Rcx] = reservedPointer;
        context[CpuRegister.R8] = reserved;
        context[CpuRegister.R9] = 0x1FC;

        Assert.True(manager.TryDispatch(OutputSupportNid, context, out _));
        return context[CpuRegister.Rax];
    }
}
