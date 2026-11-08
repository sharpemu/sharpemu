// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GnmMode;
using SharpEmu.GnmMode.Libraries.Np;
using SharpEmu.HLE;
using SharpEmu.Libs.Tests.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class GnmMatching2ExportsTests : IDisposable
{
    private const ulong Address = 0x1_0000_0000;

    public GnmMatching2ExportsTests() => Terminate();

    public void Dispose() => Terminate();

    [Fact]
    public void Matching2LifecycleExportsAreRegisteredOnlyInGnmMode()
    {
        var exports = GnmExportRegistry.CreateExports();
        foreach (var nid in new[] { "10t3e5+JPnU", "Mqp3lJ+sjy4" })
        {
            var export = Assert.Single(exports, export => export.Nid == nid);
            Assert.Equal("libSceNpMatching2", export.LibraryName);
            Assert.Equal(Generation.Gen4, export.Target);
            Assert.DoesNotContain(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
                export => export.Nid == nid);
        }
    }

    [Theory]
    [InlineData(0x28)]
    [InlineData(0x30)]
    public void AcceptsBothParameterVersionsAndTracksInitialization(int size)
    {
        var memory = new FakeCpuMemory(Address, size);
        var context = new CpuContext(memory, Generation.Gen4);
        Assert.True(context.TryWriteUInt64(Address + 0x20, (ulong)size));
        var before = new byte[size];
        Assert.True(memory.TryRead(Address, before));
        context[CpuRegister.Rdi] = Address;
        Assert.Equal(0, GnmMatching2Exports.Initialize(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(Address, context[CpuRegister.Rdi]);
        var after = new byte[size];
        Assert.True(memory.TryRead(Address, after));
        Assert.Equal(before, after);

        context[CpuRegister.Rdi] = 0;
        Assert.Equal(unchecked((int)0x80550C02), GnmMatching2Exports.Initialize(context));
        Assert.Equal(0, GnmMatching2Exports.Terminate(context));
        Assert.Equal(unchecked((int)0x80550C03), GnmMatching2Exports.Terminate(context));
        context[CpuRegister.Rdi] = Address;
        Assert.Equal(0, GnmMatching2Exports.Initialize(context));
    }

    [Theory]
    [InlineData(0x28, 0)]
    [InlineData(0x28, 0x20)]
    [InlineData(0x28, 0x30)]
    [InlineData(0x27, 0x28)]
    public void InvalidOrTruncatedParametersDoNotInitializeTheLibrary(int length, int size)
    {
        var memory = new FakeCpuMemory(Address, length);
        var context = new CpuContext(memory, Generation.Gen4);
        if (length >= 0x28)
            Assert.True(context.TryWriteUInt64(Address + 0x20, (ulong)size));
        context[CpuRegister.Rdi] = Address;
        Assert.Equal(unchecked((int)0x80550C0A), GnmMatching2Exports.Initialize(context));
        Assert.Equal(unchecked((int)0x80550C03), GnmMatching2Exports.Terminate(context));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(Address + 0x1000)]
    public void NullOrUnmappedParametersAreRejected(ulong address)
    {
        var context = new CpuContext(new FakeCpuMemory(Address, 0x28), Generation.Gen4);
        context[CpuRegister.Rdi] = address;
        Assert.Equal(unchecked((int)0x80550C0A), GnmMatching2Exports.Initialize(context));
        Assert.Equal(unchecked((int)0x80550C03), GnmMatching2Exports.Terminate(context));
    }

    private static void Terminate() =>
        GnmMatching2Exports.Terminate(new CpuContext(new FakeCpuMemory(Address, 0x28), Generation.Gen4));
}
