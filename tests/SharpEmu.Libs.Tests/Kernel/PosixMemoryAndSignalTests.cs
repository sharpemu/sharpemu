// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class PosixMemoryAndSignalTests : IDisposable
{
    private readonly FlexibleBackingPool _previous =
        KernelMemoryCompatExports.SetFlexibleBackingForTests(new FlexibleBackingPool(0, 0x100000));

    public void Dispose() => KernelMemoryCompatExports.SetFlexibleBackingForTests(_previous);

    [Theory]
    [InlineData(Generation.Gen4)]
    [InlineData(Generation.Gen5)]
    public void AnonymousMmapUsesSharedFlexibleBackingAndRoundsToGuestPages(Generation generation)
    {
        using var test = new BackedKernelMemory();
        var context = new CpuContext(test.Memory, generation);
        SetMmapArguments(context, 1);
        Assert.Equal(0, KernelMemoryCompatExports.PosixMmap(context));
        var address = context[CpuRegister.Rax];
        Assert.NotEqual(0UL, address);
        Assert.Equal(0UL, address & 0x3FFF);
        Assert.True(test.Memory.IsBackedRange(address, 0x4000));
        var contents = new byte[0x4000];
        Assert.True(test.Memory.TryRead(address, contents));
        Assert.All(contents, value => Assert.Equal(0, value));
        Assert.True(test.Memory.TryWrite(address, new byte[] { 123 }));
        Assert.Equal(0, test.Unmap(address, 0x4000));
    }

    [Fact]
    public void FixedMmapReusesTheRequestedReservedRange()
    {
        using var test = new BackedKernelMemory();
        var address = test.Reserve(0x4000);
        SetMmapArguments(test.Context, 0x4000);
        test.Context[CpuRegister.Rdi] = address;
        test.Context[CpuRegister.Rcx] = 0x1012;
        Assert.Equal(0, KernelMemoryCompatExports.PosixMmap(test.Context));
        Assert.Equal(address, test.Context[CpuRegister.Rax]);
        Assert.True(test.Memory.IsBackedRange(address, 0x4000));
    }

    [Theory]
    [InlineData(0UL, 0x1002u, -1)]
    [InlineData(ulong.MaxValue, 0x1002u, -1)]
    [InlineData(0x4000UL, 0x1002u, 3)]
    [InlineData(0x4000UL, 0x2u, -1)]
    [InlineData(0x4000UL, 0x0D001002u, -1)]
    public void InvalidMmapReturnsMapFailedNotAKernelErrorPointer(ulong length, uint flags, int fd)
    {
        using var test = new BackedKernelMemory();
        SetMmapArguments(test.Context, length);
        test.Context[CpuRegister.Rcx] = flags;
        test.Context[CpuRegister.R8] = unchecked((ulong)(long)fd);
        Assert.Equal(-1, KernelMemoryCompatExports.PosixMmap(test.Context));
        Assert.Equal(ulong.MaxValue, test.Context[CpuRegister.Rax]);
    }

    [Fact]
    public void ReadOnlySharedFileMappingReturnsFileBytesAndZeroTail()
    {
        using var test = new BackedKernelMemory();
        var path = Path.GetTempFileName();
        var data = Enumerable.Range(1, 100).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(path, data);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Position = 5;
        const int fd = 7001;
        KernelMemoryCompatExports.RegisterOpenFileForTests(fd, stream);
        try
        {
            SetFileMmapArguments(test.Context, 0x8000, 1, 1, fd, 0);
            Assert.Equal(0, KernelMemoryCompatExports.PosixMmap(test.Context));
            var address = test.Context[CpuRegister.Rax];
            var contents = new byte[0x4000];
            Assert.True(test.Memory.TryRead(address, contents));
            Assert.Equal(data, contents[..data.Length]);
            Assert.All(contents[data.Length..], value => Assert.Equal(0, value));
            Assert.Equal(5, stream.Position);
        }
        finally
        {
            KernelMemoryCompatExports.UnregisterOpenFileForTests(fd);
            stream.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void FileMappingHonoursPageAlignedOffset()
    {
        using var test = new BackedKernelMemory();
        var path = Path.GetTempFileName();
        var data = new byte[0x4000 + 8];
        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)(i < 0x4000 ? 1 : 9);
        File.WriteAllBytes(path, data);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        const int fd = 7002;
        KernelMemoryCompatExports.RegisterOpenFileForTests(fd, stream);
        try
        {
            SetFileMmapArguments(test.Context, 0x4000, 3, 2, fd, 0x4000);
            Assert.Equal(0, KernelMemoryCompatExports.PosixMmap(test.Context));
            var contents = new byte[16];
            Assert.True(test.Memory.TryRead(test.Context[CpuRegister.Rax], contents));
            Assert.Equal(new byte[] { 9, 9, 9, 9, 9, 9, 9, 9, 0, 0, 0, 0, 0, 0, 0, 0 }, contents);
            SetFileMmapArguments(test.Context, 0x4000, 1, 2, fd, 0x100);
            Assert.Equal(-1, KernelMemoryCompatExports.PosixMmap(test.Context));
        }
        finally
        {
            KernelMemoryCompatExports.UnregisterOpenFileForTests(fd);
            stream.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void FileMappingWithUnknownDescriptorFailsWithEbadf()
    {
        using var test = new BackedKernelMemory();
        SetFileMmapArguments(test.Context, 0x4000, 1, 1, 7999, 0);
        Assert.Equal(-1, KernelMemoryCompatExports.PosixMmap(test.Context));
        Assert.Equal(ulong.MaxValue, test.Context[CpuRegister.Rax]);
    }

    private static void SetFileMmapArguments(CpuContext context, ulong length, int protection, uint flags, int fd, ulong offset)
    {
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = length;
        context[CpuRegister.Rdx] = (ulong)protection;
        context[CpuRegister.Rcx] = flags;
        context[CpuRegister.R8] = unchecked((ulong)(long)fd);
        context[CpuRegister.R9] = offset;
    }

    [Fact]
    public void SignalMaskOperationsPreserveThePreviousMaskAndAllowAnAliasedOutput()
    {
        const ulong address = 0x1000;
        var memory = new FakeCpuMemory(address, 0x1000);
        var context = new CpuContext(memory, Generation.Gen4);
        context[CpuRegister.Rdi] = 3;
        context[CpuRegister.Rsi] = address;
        context[CpuRegister.Rdx] = 0;
        Assert.Equal(0, KernelRuntimeCompatExports.PosixSigprocmask(context));
        context[CpuRegister.Rdi] = address;
        Assert.Equal(0, KernelRuntimeCompatExports.PosixSigfillset(context));
        context[CpuRegister.Rdi] = 1;
        context[CpuRegister.Rsi] = address;
        context[CpuRegister.Rdx] = address;
        Assert.Equal(0, KernelRuntimeCompatExports.PosixSigprocmask(context));
        var previous = new byte[16];
        Assert.True(memory.TryRead(address, previous));
        Assert.All(previous, value => Assert.Equal(0, value));
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(0, KernelRuntimeCompatExports.PosixSigprocmask(context));
        Assert.True(memory.TryRead(address, previous));
        Assert.All(previous, value => Assert.Equal(255, value));
        Assert.True(memory.TryWrite(address, new byte[16]));
        context[CpuRegister.Rdi] = 3;
        context[CpuRegister.Rsi] = address;
        context[CpuRegister.Rdx] = 0;
        Assert.Equal(0, KernelRuntimeCompatExports.PosixSigprocmask(context));
    }

    [Fact]
    public void PosixExportsAreAvailableToBothGenerations()
    {
        foreach (var generation in new[] { Generation.Gen4, Generation.Gen5 })
            foreach (var nid in new[] { "BPE9s9vQQXo", "VkTAsrZDcJ0", "aPcyptbOiZs" })
                Assert.Single(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation), export => export.Nid == nid);
    }

    private static void SetMmapArguments(CpuContext context, ulong length)
    {
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = length;
        context[CpuRegister.Rdx] = 3;
        context[CpuRegister.Rcx] = 0x1002;
        context[CpuRegister.R8] = ulong.MaxValue;
        context[CpuRegister.R9] = 0;
    }
}
