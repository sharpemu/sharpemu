// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.CommonDialog;
using Xunit;

namespace SharpEmu.Libs.Tests.CommonDialog;

[CollectionDefinition("WebBrowserDialog", DisableParallelization = true)]
public sealed class WebBrowserDialogCollection;

[Collection("WebBrowserDialog")]
public sealed class WebBrowserDialogExportsTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x1000);
    private readonly CpuContext _context;

    public WebBrowserDialogExportsTests()
    {
        WebBrowserDialogExports.ResetForTests();
        _context = new CpuContext(_memory, Generation.Gen5);
    }

    public void Dispose() => WebBrowserDialogExports.ResetForTests();

    [Fact]
    public void HeadlessLifecycle_CompletesOnFirstStatusPoll()
    {
        Assert.Equal(WebBrowserDialogExports.StatusNone,
            WebBrowserDialogExports.WebBrowserDialogGetStatus(_context));
        Assert.Equal(0, WebBrowserDialogExports.WebBrowserDialogInitialize(_context));
        Assert.Equal(WebBrowserDialogExports.StatusInitialized,
            WebBrowserDialogExports.WebBrowserDialogGetStatus(_context));

        _context[CpuRegister.Rdi] = MemoryBase + 0x100;
        Assert.Equal(0, WebBrowserDialogExports.WebBrowserDialogOpen(_context));
        Assert.Equal(WebBrowserDialogExports.StatusRunning, WebBrowserDialogExports.StatusForTests);
        Assert.Equal(WebBrowserDialogExports.StatusFinished,
            WebBrowserDialogExports.WebBrowserDialogGetStatus(_context));

        var resultAddress = MemoryBase + 0x200;
        _context[CpuRegister.Rdi] = resultAddress;
        Assert.Equal(0, WebBrowserDialogExports.WebBrowserDialogGetResult(_context));
        Span<byte> result = stackalloc byte[sizeof(int)];
        Assert.True(_memory.TryRead(resultAddress, result));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(result));

        Assert.Equal(0, WebBrowserDialogExports.WebBrowserDialogTerminate(_context));
        Assert.Equal(WebBrowserDialogExports.StatusNone,
            WebBrowserDialogExports.WebBrowserDialogGetStatus(_context));
    }

    [Fact]
    public void Close_FinishesRunningDialogWithoutPolling()
    {
        Assert.Equal(0, WebBrowserDialogExports.WebBrowserDialogInitialize(_context));
        _context[CpuRegister.Rdi] = MemoryBase + 0x100;
        Assert.Equal(0, WebBrowserDialogExports.WebBrowserDialogOpenForPredeterminedContent(_context));
        Assert.Equal(0, WebBrowserDialogExports.WebBrowserDialogClose(_context));
        Assert.Equal(WebBrowserDialogExports.StatusFinished, WebBrowserDialogExports.StatusForTests);
    }
}
