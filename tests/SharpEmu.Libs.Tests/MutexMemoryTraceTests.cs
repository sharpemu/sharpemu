// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class MutexMemoryTraceTests
{
    [Theory]
    [InlineData("frame", 0x1100UL)]
    [InlineData("r15", 0x1200UL)]
    [InlineData("stack", 0x1600UL)]
    [InlineData("0x1300", 0x1300UL)]
    [InlineData("r15/58/8", 0x1500UL)]
    public void ResolvesConfiguredRootsAndPointerChains(string path, ulong expected)
    {
        var context = new CpuContext(new FakeCpuMemory(0x1000, 0x1000), Generation.Gen5);
        Assert.True(context.TryWriteUInt64(0x1258, 0x1400));
        Assert.True(context.TryWriteUInt64(0x1408, 0x1500));
        Assert.True(DirectExecutionBackend.TryResolveMutexMemoryPath(context, path, 0x1100, 0x1200, out var address, 0x1600));
        Assert.Equal(expected, address);
    }

    [Theory]
    [InlineData("r15/FFFFFFFFFFFFFFFF")]
    [InlineData("FFFFFFFFFFFFFFFE/0")]
    [InlineData("r15/0")]
    [InlineData("r15/not-an-offset")]
    [InlineData("0")]
    [InlineData("bad-root")]
    [InlineData("stack")]
    [InlineData("3000/0")]
    [InlineData("r15/0/0/0/0/0/0/0/0/0")]
    public void RejectsInvalidUnreadableOrExcessivePaths(string path)
    {
        var context = new CpuContext(new FakeCpuMemory(0x1000, 0x1000), Generation.Gen5);
        Assert.False(DirectExecutionBackend.TryResolveMutexMemoryPath(context, path, 0x1100, 0x1200, out _));
    }

    [Theory]
    [InlineData("scePthreadCondWait", true)]
    [InlineData("scePthreadCondBroadcast", true)]
    [InlineData("scePthreadMutexUnlock", true)]
    [InlineData("pthread_cond_signal", true)]
    [InlineData("memcpy", false)]
    [InlineData("scePthreadCreate", false)]
    public void CallerSelectionIncludesOnlySynchronizationImports(string name, bool expected)
    {
        Assert.Equal(expected, DirectExecutionBackend.IsMutexTraceSynchronizationImport(name));
    }
}
