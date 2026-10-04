// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Network;
using Xunit;

namespace SharpEmu.Libs.Tests.Network;

public sealed class SslExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;

    [Fact]
    public void GetCaCerts_ClearsResultAndReportsNoHostCertificates()
    {
        SslExports.ResetForTests();
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        ctx[CpuRegister.Rdi] = 0x1000;
        Assert.Equal(0, SslExports.SslInit(ctx));
        var sslId = ctx[CpuRegister.Rax];

        var caCertsAddress = MemoryBase + 0x100;
        Assert.True(memory.TryWrite(caCertsAddress, Enumerable.Repeat((byte)0xA5, 24).ToArray()));
        ctx[CpuRegister.Rdi] = sslId;
        ctx[CpuRegister.Rsi] = caCertsAddress;
        Assert.Equal(unchecked((int)0x8095F004), SslExports.SslGetCaCerts(ctx));

        var result = new byte[24];
        Assert.True(memory.TryRead(caCertsAddress, result));
        Assert.Equal(new byte[24], result);
    }

    [Fact]
    public void GetCaCerts_ValidatesHandleAndOutputPointer()
    {
        SslExports.ResetForTests();
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        ctx[CpuRegister.Rdi] = 0x1000;
        Assert.Equal(0, SslExports.SslInit(ctx));
        var sslId = ctx[CpuRegister.Rax];

        ctx[CpuRegister.Rdi] = sslId;
        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(unchecked((int)0x8095177A), SslExports.SslGetCaCerts(ctx));

        ctx[CpuRegister.Rdi] = 0x7FFF_FFFF;
        ctx[CpuRegister.Rsi] = MemoryBase + 0x100;
        Assert.Equal(unchecked((int)0x8095F006), SslExports.SslGetCaCerts(ctx));

        ctx[CpuRegister.Rdi] = sslId;
        ctx[CpuRegister.Rsi] = MemoryBase + 0x2000;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            SslExports.SslGetCaCerts(ctx));
    }

    [Fact]
    public void GetCaCerts_RegistersPs5Nid()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("TDfQqO-gMbY", out var export));
        Assert.Equal("sceSslGetCaCerts", export.Name);
        Assert.Equal("libSceSsl", export.LibraryName);
    }
}
