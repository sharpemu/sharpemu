// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Remoteplay;
using Xunit;

namespace SharpEmu.Libs.Tests.Remoteplay;

public sealed class RemoteplayExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong StatusAddress = MemoryBase + 0x100;
    private const ulong FaultingAddress = 0xDEAD_0000_0000;

    [Fact]
    public void InitializeAndConnectionStatusExportsAreRegisteredForGen5()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("k1SwgkMSOM8", out var initialize));
        Assert.Equal("sceRemoteplayInitialize", initialize.Name);
        Assert.True(manager.TryGetExport("g3PNjYKWqnQ", out var getStatus));
        Assert.Equal("sceRemoteplayGetConnectionStatus", getStatus.Name);
    }

    [Fact]
    public void GetConnectionStatusReportsDisconnected()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rsi] = StatusAddress;

        Assert.Equal(0, RemoteplayExports.RemoteplayGetConnectionStatus(context));

        Span<byte> status = stackalloc byte[sizeof(int)];
        Assert.True(memory.TryRead(StatusAddress, status));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(status));
    }

    [Theory]
    [InlineData(0UL, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT)]
    [InlineData(FaultingAddress, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT)]
    public void GetConnectionStatusRejectsInvalidOutput(
        ulong statusAddress,
        OrbisGen2Result expected)
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rsi] = statusAddress;

        Assert.Equal((int)expected, RemoteplayExports.RemoteplayGetConnectionStatus(context));
    }
}
