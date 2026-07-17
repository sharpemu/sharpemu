// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.AvPlayer;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

public sealed class AvPlayerStreamInfoTests
{
    private const ulong InfoAddress = 0x2_0000;

    [Fact]
    public void Gen5BasicVideoInfoUsesThirtyTwoByteLayoutAndVideoType()
    {
        var memory = new FakeCpuMemory(InfoAddress, 32);
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.True(
            AvPlayerExports.TryWriteStreamInfo(
                context,
                InfoAddress,
                streamIndex: 0,
                width: 1920,
                height: 1080,
                framesPerSecond: 30,
                durationMilliseconds: 16_333,
                extended: false,
                out var size));
        Assert.Equal(32, size);

        var info = Read(memory, size);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(info));
        Assert.Equal(1920u, BinaryPrimitives.ReadUInt32LittleEndian(info[8..]));
        Assert.Equal(1080u, BinaryPrimitives.ReadUInt32LittleEndian(info[12..]));
        Assert.Equal(16_333ul, BinaryPrimitives.ReadUInt64LittleEndian(info[24..]));
    }

    [Fact]
    public void Gen5BasicAudioInfoUsesAudioType()
    {
        var memory = new FakeCpuMemory(InfoAddress, 32);
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.True(
            AvPlayerExports.TryWriteStreamInfo(
                context,
                InfoAddress,
                streamIndex: 1,
                width: 1920,
                height: 1080,
                framesPerSecond: 30,
                durationMilliseconds: 16_333,
                extended: false,
                out var size));

        var info = Read(memory, size);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(info));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(info[8..]));
        Assert.Equal(48_000u, BinaryPrimitives.ReadUInt32LittleEndian(info[12..]));
    }

    [Fact]
    public void Gen5BasicInfoDoesNotOverwriteTrailingCallerMemory()
    {
        var memory = new FakeCpuMemory(InfoAddress, 40);
        Assert.True(memory.TryWrite(InfoAddress + 32, Enumerable.Repeat((byte)0xA5, 8).ToArray()));
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.True(
            AvPlayerExports.TryWriteStreamInfo(
                context,
                InfoAddress,
                streamIndex: 0,
                width: 1920,
                height: 1080,
                framesPerSecond: 30,
                durationMilliseconds: 16_333,
                extended: false,
                out var size));
        Assert.Equal(32, size);

        var bytes = Read(memory, 40);
        Assert.All(bytes[32..], value => Assert.Equal(0xA5, value));
    }

    [Fact]
    public void Gen4BasicInfoRetainsFortyByteLayoutAndTypeValues()
    {
        var memory = new FakeCpuMemory(InfoAddress, 40);
        var context = new CpuContext(memory, Generation.Gen4);

        Assert.True(
            AvPlayerExports.TryWriteStreamInfo(
                context,
                InfoAddress,
                streamIndex: 0,
                width: 1920,
                height: 1080,
                framesPerSecond: 60,
                durationMilliseconds: 5_000,
                extended: false,
                out var size,
                useVideoDecoderSoftware2: false));
        Assert.Equal(40, size);

        var info = Read(memory, size);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(info));
        Assert.Equal(1920u, BinaryPrimitives.ReadUInt32LittleEndian(info[8..]));
        Assert.Equal(1080u, BinaryPrimitives.ReadUInt32LittleEndian(info[12..]));
        Assert.Equal(5_000ul, BinaryPrimitives.ReadUInt64LittleEndian(info[24..]));
        Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(info[32..]));
    }

    [Fact]
    public void Gen5ExtendedInfoWritesSizeTypeDetailsAndDuration()
    {
        var memory = new FakeCpuMemory(InfoAddress, 104);
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.True(
            AvPlayerExports.TryWriteStreamInfo(
                context,
                InfoAddress,
                streamIndex: 0,
                width: 1920,
                height: 1080,
                framesPerSecond: 59.94,
                durationMilliseconds: 20_000,
                extended: true,
                out var size));
        Assert.Equal(104, size);

        var info = Read(memory, size);
        Assert.Equal(104ul, BinaryPrimitives.ReadUInt64LittleEndian(info));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(info[8..]));
        Assert.Equal(1920u, BinaryPrimitives.ReadUInt32LittleEndian(info[16..]));
        Assert.Equal(1080u, BinaryPrimitives.ReadUInt32LittleEndian(info[20..]));
        Assert.Equal(2048u, BinaryPrimitives.ReadUInt32LittleEndian(info[52..]));
        Assert.Equal(8, info[56]);
        Assert.Equal(8, info[57]);
        Assert.Equal(59.94, BinaryPrimitives.ReadDoubleLittleEndian(info[64..]), 3);
        Assert.Equal(20_000ul, BinaryPrimitives.ReadUInt64LittleEndian(info[96..]));
    }

    [Fact]
    public void Gen5ExtendedAudioInfoUsesAudioTypeAndDetails()
    {
        var memory = new FakeCpuMemory(InfoAddress, 104);
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.True(
            AvPlayerExports.TryWriteStreamInfo(
                context,
                InfoAddress,
                streamIndex: 1,
                width: 1920,
                height: 1080,
                framesPerSecond: 30,
                durationMilliseconds: 16_333,
                extended: true,
                out var size));

        var info = Read(memory, size);
        Assert.Equal(104ul, BinaryPrimitives.ReadUInt64LittleEndian(info));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(info[8..]));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(info[16..]));
        Assert.Equal(48_000u, BinaryPrimitives.ReadUInt32LittleEndian(info[20..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(info[24..]));
        Assert.Equal(16_333ul, BinaryPrimitives.ReadUInt64LittleEndian(info[96..]));
    }

    [Fact]
    public void ExtendedStreamInfoExportRegistersForGen5Only()
    {
        var gen5Manager = new ModuleManager();
        gen5Manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        Assert.True(gen5Manager.TryGetExport("ctTAcF5DiKQ", out var export));
        Assert.Equal("sceAvPlayerGetStreamInfoEx", export.Name);
        Assert.Equal("libSceAvPlayer", export.LibraryName);

        var gen4Manager = new ModuleManager();
        gen4Manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4));
        Assert.False(gen4Manager.TryGetExport("ctTAcF5DiKQ", out _));
    }

    [Fact]
    public void StreamInfoWritePreservesMemoryFaultAndGenerationValidation()
    {
        var shortMemory = new FakeCpuMemory(InfoAddress, 31);
        var gen5 = new CpuContext(shortMemory, Generation.Gen5);
        Assert.False(
            AvPlayerExports.TryWriteStreamInfo(
                gen5,
                InfoAddress,
                streamIndex: 0,
                width: 1920,
                height: 1080,
                framesPerSecond: 30,
                durationMilliseconds: 1,
                extended: false,
                out var size));
        Assert.Equal(32, size);

        var gen4Memory = new FakeCpuMemory(InfoAddress, 104);
        var gen4 = new CpuContext(gen4Memory, Generation.Gen4);
        Assert.False(
            AvPlayerExports.TryWriteStreamInfo(
                gen4,
                InfoAddress,
                streamIndex: 0,
                width: 1920,
                height: 1080,
                framesPerSecond: 30,
                durationMilliseconds: 1,
                extended: true,
                out size));
        Assert.Equal(0, size);
    }

    private static byte[] Read(FakeCpuMemory memory, int size)
    {
        var bytes = new byte[size];
        Assert.True(memory.TryRead(InfoAddress, bytes));
        return bytes;
    }
}
