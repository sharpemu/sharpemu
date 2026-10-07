// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.AvPlayer;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

public sealed class AvPlayerStreamInfoTests
{
    private const string StreamInfoExNid = "ctTAcF5DiKQ";
    private const ulong BaseAddress = 0x1_0000_0000;
    private const int MemorySize = 0x2000;
    private const ulong InfoAddress = BaseAddress + 0x100;
    private const ulong Handle = 0xA0_0000_0001;
    private const ulong DurationMilliseconds = 0x0102_0304_0506_0708;
    private const byte Sentinel = 0xAB;

    [Theory]
    [InlineData(Generation.Gen5, 0u, 32, 1u)]
    [InlineData(Generation.Gen5, 1u, 32, 2u)]
    [InlineData(Generation.Gen4, 0u, 40, 0u)]
    [InlineData(Generation.Gen4, 1u, 40, 1u)]
    public void GetStreamInfoUsesTheGenerationSpecificLayout(
        Generation generation,
        uint streamIndex,
        int structureSize,
        uint expectedStreamType)
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var context = new CpuContext(memory, generation);
        AvPlayerExports.RegisterPlayerForTest(
            Handle,
            1280,
            720,
            DurationMilliseconds,
            hasAudio: true);

        try
        {
            Span<byte> window = stackalloc byte[48];
            window.Fill(Sentinel);
            Assert.True(memory.TryWrite(InfoAddress, window));

            context[CpuRegister.Rdi] = Handle;
            context[CpuRegister.Rsi] = streamIndex;
            context[CpuRegister.Rdx] = InfoAddress;
            Assert.Equal(0, AvPlayerExports.AvPlayerGetStreamInfo(context));

            Span<byte> result = stackalloc byte[48];
            Assert.True(memory.TryRead(InfoAddress, result));
            Assert.Equal(expectedStreamType, BinaryPrimitives.ReadUInt32LittleEndian(result));
            if (streamIndex == 0)
            {
                Assert.Equal(1280u, BinaryPrimitives.ReadUInt32LittleEndian(result[8..]));
                Assert.Equal(720u, BinaryPrimitives.ReadUInt32LittleEndian(result[12..]));
            }
            else
            {
                Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(result[8..]));
                Assert.Equal(48_000u, BinaryPrimitives.ReadUInt32LittleEndian(result[12..]));
            }
            Assert.Equal(DurationMilliseconds, BinaryPrimitives.ReadUInt64LittleEndian(result[24..]));

            for (var index = structureSize; index < result.Length; index++)
            {
                Assert.Equal(Sentinel, result[index]);
            }
        }
        finally
        {
            AvPlayerExports.RemovePlayerForTest(Handle);
        }
    }

    [Fact]
    public void StreamInfoRejectsAudioIndexForVideoOnlyMedia()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var context = new CpuContext(memory, Generation.Gen5);
        AvPlayerExports.RegisterPlayerForTest(Handle, 1280, 720, DurationMilliseconds);

        try
        {
            context[CpuRegister.Rdi] = Handle;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = InfoAddress;
            Assert.NotEqual(0, AvPlayerExports.AvPlayerGetStreamInfo(context));
            Assert.NotEqual(0, AvPlayerExports.AvPlayerGetStreamInfoEx(context));
        }
        finally
        {
            AvPlayerExports.RemovePlayerForTest(Handle);
        }
    }

    [Fact]
    public void GetStreamInfoExWritesThe104ByteGen5Descriptor()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var context = new CpuContext(memory, Generation.Gen5);
        AvPlayerExports.RegisterPlayerForTest(
            Handle,
            378,
            150,
            DurationMilliseconds,
            framesPerSecond: 29.97);

        try
        {
            Span<byte> window = stackalloc byte[120];
            window.Fill(Sentinel);
            Assert.True(memory.TryWrite(InfoAddress, window));

            context[CpuRegister.Rdi] = Handle;
            context[CpuRegister.Rsi] = 0;
            context[CpuRegister.Rdx] = InfoAddress;
            Assert.Equal(0, AvPlayerExports.AvPlayerGetStreamInfoEx(context));

            Span<byte> result = stackalloc byte[120];
            Assert.True(memory.TryRead(InfoAddress, result));
            Assert.Equal(104UL, BinaryPrimitives.ReadUInt64LittleEndian(result));
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(result[8..]));
            Assert.Equal(378u, BinaryPrimitives.ReadUInt32LittleEndian(result[16..]));
            Assert.Equal(150u, BinaryPrimitives.ReadUInt32LittleEndian(result[20..]));
            Assert.Equal(29.97, BinaryPrimitives.ReadDoubleLittleEndian(result[0x40..]));
            Assert.Equal(DurationMilliseconds, BinaryPrimitives.ReadUInt64LittleEndian(result[0x60..]));

            for (var index = 104; index < result.Length; index++)
            {
                Assert.Equal(Sentinel, result[index]);
            }
        }
        finally
        {
            AvPlayerExports.RemovePlayerForTest(Handle);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetStreamInfoFunctionsRejectInvalidArguments(bool useExtendedFunction)
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var context = new CpuContext(memory, Generation.Gen5);
        AvPlayerExports.RegisterPlayerForTest(
            Handle,
            1280,
            720,
            DurationMilliseconds,
            hasAudio: true);

        try
        {
            context[CpuRegister.Rdi] = Handle;
            context[CpuRegister.Rsi] = 2;
            context[CpuRegister.Rdx] = InfoAddress;
            Assert.NotEqual(0, InvokeGetStreamInfo(context, useExtendedFunction));

            context[CpuRegister.Rsi] = 0;
            context[CpuRegister.Rdx] = 0;
            Assert.NotEqual(0, InvokeGetStreamInfo(context, useExtendedFunction));

            context[CpuRegister.Rdi] = Handle + 1;
            context[CpuRegister.Rdx] = InfoAddress;
            Assert.NotEqual(0, InvokeGetStreamInfo(context, useExtendedFunction));
        }
        finally
        {
            AvPlayerExports.RemovePlayerForTest(Handle);
        }
    }

    [Fact]
    public void StreamInfoExExportIsRegisteredForGen5Only()
    {
        var gen4Manager = new ModuleManager();
        gen4Manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4));
        Assert.False(gen4Manager.TryGetExport(StreamInfoExNid, out _));

        var gen5Manager = new ModuleManager();
        gen5Manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        Assert.True(gen5Manager.TryGetExport(StreamInfoExNid, out var export));
        Assert.Equal("sceAvPlayerGetStreamInfoEx", export.Name);
        Assert.Equal("libSceAvPlayer", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
    }

    private static int InvokeGetStreamInfo(CpuContext context, bool useExtendedFunction) =>
        useExtendedFunction
            ? AvPlayerExports.AvPlayerGetStreamInfoEx(context)
            : AvPlayerExports.AvPlayerGetStreamInfo(context);
}
