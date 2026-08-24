// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VideoOutLatencyTests
{
    private const string OpenNid = "Up36PTk687E";
    private const string CloseNid = "uquVH4-Du78";
    private const string SubmitFlipNid = "U46NwOiJpys";
    private const string WaitBeforeInputNid = "eb-gvTYQcoY";
    private const string SetStartPointNid = "MCJ8SkzsQxY";
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ControlAddress = MemoryBase + 0x100;
    private const ulong TimeoutAddress = MemoryBase + 0x180;
    private static readonly ulong InvalidValue = unchecked((ulong)(int)0x80290001);
    private static readonly ulong InvalidHandle = unchecked((ulong)(int)0x8029000B);
    private static readonly ulong TimedOut = unchecked(
        (ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT);

    [Fact]
    public void Gen5LatencyExportsTrackCompletedFlipArguments()
    {
        var manager = CreateManager();
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var handle = Open(manager, context);

        try
        {
            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 42;
            Assert.True(manager.TryDispatch(SetStartPointNid, context, out _));
            Assert.Equal(0UL, context[CpuRegister.Rax]);

            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = ulong.MaxValue;
            context[CpuRegister.Rdx] = 1;
            context[CpuRegister.Rcx] = 42;
            Assert.True(manager.TryDispatch(SubmitFlipNid, context, out _));
            Assert.Equal(0UL, context[CpuRegister.Rax]);

            WriteControl(memory, control: 1, target: 42, extraUsec: 0);
            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = ControlAddress;
            context[CpuRegister.Rdx] = 0;
            Assert.True(manager.TryDispatch(WaitBeforeInputNid, context, out _));
            Assert.Equal(0UL, context[CpuRegister.Rax]);
        }
        finally
        {
            Close(manager, context, handle);
        }
    }

    [Fact]
    public void WaitBeforeInputValidatesControlAndHonorsZeroTimeout()
    {
        var manager = CreateManager();
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var handle = Open(manager, context);

        try
        {
            WriteControl(memory, control: 2, target: 0, extraUsec: 0);
            Assert.Equal(InvalidValue, Wait(manager, context, handle, ControlAddress, 0));

            WriteControl(memory, control: 0, target: 0, extraUsec: 100_001);
            Assert.Equal(InvalidValue, Wait(manager, context, handle, ControlAddress, 0));

            WriteControl(memory, control: 1, target: 99, extraUsec: 0);
            Span<byte> timeout = stackalloc byte[sizeof(uint)];
            timeout.Clear();
            Assert.True(memory.TryWrite(TimeoutAddress, timeout));
            Assert.Equal(TimedOut, Wait(manager, context, handle, ControlAddress, TimeoutAddress));

            Assert.Equal(InvalidHandle, Wait(manager, context, ulong.MaxValue, ControlAddress, 0));
        }
        finally
        {
            Close(manager, context, handle);
        }
    }

    [Fact]
    public async Task ClosingPortReleasesBlockedLatencyWait()
    {
        var manager = CreateManager();
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var openContext = new CpuContext(memory, Generation.Gen5);
        var waitContext = new CpuContext(memory, Generation.Gen5);
        var closeContext = new CpuContext(memory, Generation.Gen5);
        var handle = Open(manager, openContext);

        WriteControl(memory, control: 1, target: 99, extraUsec: 0);
        var waitTask = Task.Run(
            () => Wait(manager, waitContext, handle, ControlAddress, 0));

        await Task.Delay(50);
        Assert.False(waitTask.IsCompleted);
        Close(manager, closeContext, handle);

        Assert.Equal(InvalidHandle, await waitTask.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static ModuleManager CreateManager()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        Assert.True(manager.TryGetExport(WaitBeforeInputNid, out var wait));
        Assert.Equal("sceVideoOutLatencyControlWaitBeforeInput", wait.Name);
        Assert.True(manager.TryGetExport(SetStartPointNid, out var start));
        Assert.Equal("sceVideoOutLatencyMeasureSetStartPoint", start.Name);
        return manager;
    }

    private static ulong Open(ModuleManager manager, CpuContext context)
    {
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = 0;
        Assert.True(manager.TryDispatch(OpenNid, context, out _));
        Assert.NotEqual(0UL, context[CpuRegister.Rax]);
        return context[CpuRegister.Rax];
    }

    private static void Close(ModuleManager manager, CpuContext context, ulong handle)
    {
        context[CpuRegister.Rdi] = handle;
        _ = manager.TryDispatch(CloseNid, context, out _);
    }

    private static ulong Wait(
        ModuleManager manager,
        CpuContext context,
        ulong handle,
        ulong controlAddress,
        ulong timeoutAddress)
    {
        context[CpuRegister.Rdi] = handle;
        context[CpuRegister.Rsi] = controlAddress;
        context[CpuRegister.Rdx] = timeoutAddress;
        Assert.True(manager.TryDispatch(WaitBeforeInputNid, context, out _));
        return context[CpuRegister.Rax];
    }

    private static void WriteControl(
        FakeCpuMemory memory,
        uint control,
        long target,
        uint extraUsec)
    {
        Span<byte> bytes = stackalloc byte[0x20];
        bytes.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[0x00..0x04], control);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[0x08..0x10], target);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[0x10..0x14], extraUsec);
        Assert.True(memory.TryWrite(ControlAddress, bytes));
    }
}
