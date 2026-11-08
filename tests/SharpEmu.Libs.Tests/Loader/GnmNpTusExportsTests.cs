// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GnmMode;
using SharpEmu.GnmMode.Libraries.Np;
using SharpEmu.HLE;
using SharpEmu.Libs.Tests.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class GnmNpTusExportsTests : IDisposable
{
    private const ulong Address = 0x1_0000_0000;
    private readonly FakeCpuMemory _memory = new(Address, 0x100);
    private readonly List<int> _contexts = [];

    public void Dispose()
    {
        var context = new CpuContext(_memory, Generation.Gen4);
        foreach (var id in _contexts)
        {
            context[CpuRegister.Rdi] = (uint)id;
            GnmNpTusExports.DeleteNpTitleCtx(context);
        }
    }

    [Fact]
    public void TheImportedTusLifecycleIsAvailableOnlyToGen4()
    {
        var gnm = GnmExportRegistry.CreateExports();
        var shared = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5);
        foreach (var nid in new[] { "BIkMmUfNKWM", "H3uq7x0sZOI", "3bh2aBvvmvM", "CcIH40dYS88",
                     "t7b6dmpQNiI", "LUwvy0MOSqw", "uHtKS5V1T5k", "G68xdfQuiyU" })
        {
            Assert.Equal(Generation.Gen4, Assert.Single(gnm, export => export.Nid == nid).Target);
            Assert.DoesNotContain(shared, export => export.Nid == nid);
        }
    }

    [Theory]
    [InlineData("LUwvy0MOSqw")]
    [InlineData("uHtKS5V1T5k")]
    [InlineData("G68xdfQuiyU")]
    public void OfflineRequestsCompleteWithNoLoginInsteadOfWaitingOrFabricatingSaveData(string nid)
    {
        var context = CreateContext();
        var title = CreateTitle(context);
        context[CpuRegister.Rdi] = (uint)title;
        var request = GnmNpTusExports.CreateRequest(context);
        Assert.True(request > 0);
        Assert.NotEqual(title, request);
        var guard = Enumerable.Repeat((byte)0xAB, 16).ToArray();
        Assert.True(_memory.TryWrite(Address + 0x40, guard));
        context[CpuRegister.Rdi] = (uint)request;
        context[CpuRegister.Rsi] = Address + 0x44;
        Assert.Equal(1, GnmNpTusExports.PollAsync(context));
        var actual = new byte[16];
        Assert.True(_memory.TryRead(Address + 0x40, actual));
        Assert.Equal(guard, actual);
        var begin = Assert.Single(GnmExportRegistry.CreateExports(), export => export.Nid == nid);
        Assert.Equal(0, begin.Function(context));
        Assert.Equal(0, GnmNpTusExports.PollAsync(context));
        Assert.True(context.TryReadUInt32(Address + 0x44, out var result));
        Assert.Equal(0x80550705u, result);
        Assert.True(_memory.TryRead(Address + 0x40, actual));
        Assert.Equal(guard[..4], actual[..4]);
        Assert.Equal(guard[8..], actual[8..]);
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(0, GnmNpTusExports.PollAsync(context));
        context[CpuRegister.Rsi] = Address + 0xFE;
        Assert.Equal(unchecked((int)0x80550704), GnmNpTusExports.PollAsync(context));
    }

    [Fact]
    public void DeletingAContextInvalidatesItsRequestsWithoutAffectingOtherContexts()
    {
        var context = CreateContext();
        var first = CreateTitle(context);
        context[CpuRegister.Rdi] = (uint)first;
        var firstRequest = GnmNpTusExports.CreateRequest(context);
        var second = CreateTitle(context);
        context[CpuRegister.Rdi] = (uint)second;
        var secondRequest = GnmNpTusExports.CreateRequest(context);
        context[CpuRegister.Rdi] = (uint)first;
        Assert.Equal(0, GnmNpTusExports.DeleteNpTitleCtx(context));
        Assert.Equal(unchecked((int)0x8055070E), GnmNpTusExports.CreateRequest(context));
        context[CpuRegister.Rdi] = (uint)firstRequest;
        Assert.Equal(unchecked((int)0x8055070E), GnmNpTusExports.BeginOfflineRequest(context));
        Assert.Equal(unchecked((int)0x8055070E), GnmNpTusExports.PollAsync(context));
        context[CpuRegister.Rdi] = (uint)secondRequest;
        Assert.Equal(0, GnmNpTusExports.BeginOfflineRequest(context));
        Assert.Equal(0, GnmNpTusExports.DeleteRequest(context));
        Assert.Equal(unchecked((int)0x8055070E), GnmNpTusExports.DeleteRequest(context));
        context[CpuRegister.Rdi] = (uint)second;
        Assert.True(GnmNpTusExports.CreateRequest(context) > secondRequest);
    }

    [Fact]
    public void InvalidNpIdsAndServiceLabelsAreRejected()
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = uint.MaxValue;
        context[CpuRegister.Rsi] = Address;
        Assert.Equal(unchecked((int)0x80550704), GnmNpTusExports.CreateNpTitleCtx(context));
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(unchecked((int)0x8055070C), GnmNpTusExports.CreateNpTitleCtx(context));
        context[CpuRegister.Rsi] = Address + 0xE0;
        Assert.Equal(unchecked((int)0x8055070C), GnmNpTusExports.CreateNpTitleCtx(context));
        context[CpuRegister.Rsi] = Address;
        Assert.True(_memory.TryWrite(Address, new byte[36]));
        Assert.Equal(unchecked((int)0x8055070F), GnmNpTusExports.CreateNpTitleCtx(context));
    }

    private CpuContext CreateContext()
    {
        Assert.True(_memory.TryWrite(Address, "Player"u8));
        return new CpuContext(_memory, Generation.Gen4);
    }

    private int CreateTitle(CpuContext context)
    {
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = Address;
        var id = GnmNpTusExports.CreateNpTitleCtx(context);
        Assert.True(id > 0);
        _contexts.Add(id);
        return id;
    }
}
